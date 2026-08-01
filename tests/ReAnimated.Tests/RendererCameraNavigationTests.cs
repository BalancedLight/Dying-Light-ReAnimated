using System.Numerics;
using ReAnimated.App.ViewModels;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererCameraNavigationTests
{
    [Fact]
    public void OrbitPreservesTargetDistanceAndLens()
    {
        RenderCamera original = CreateCamera();
        float originalDistance = Vector3.Distance(
            original.Eye,
            original.Target);

        Assert.True(RenderCameraNavigation.TryApply(
            original,
            RenderCameraNavigationInput.Orbit(
                120.0f,
                -60.0f,
                800,
                600),
            out RenderCamera orbit));

        AssertVectorNear(original.Target, orbit.Target);
        Assert.Equal(
            originalDistance,
            Vector3.Distance(orbit.Eye, orbit.Target),
            4);
        Assert.NotEqual(original.Eye, orbit.Eye);
        Assert.Equal(original.Up, orbit.Up);
        Assert.Equal(
            original.VerticalFieldOfViewDegrees,
            orbit.VerticalFieldOfViewDegrees);
        Assert.Equal(original.NearPlane, orbit.NearPlane);
        Assert.Equal(original.FarPlane, orbit.FarPlane);
    }

    [Fact]
    public void OrbitClampsExtremePitchAwayFromPoles()
    {
        RenderCamera camera = CreateCamera();

        Assert.True(RenderCameraNavigation.TryApply(
            camera,
            RenderCameraNavigationInput.Orbit(
                0.0f,
                float.MaxValue,
                1,
                1),
            out RenderCamera orbit));

        Vector3 forward = Vector3.Normalize(
            orbit.Target - orbit.Eye);
        Assert.True(IsFinite(orbit.Eye));
        Assert.InRange(
            MathF.Abs(Vector3.Dot(forward, orbit.Up)),
            0.0f,
            MathF.Sin(85.0f * MathF.PI / 180.0f) + 1.0e-5f);
    }

    [Fact]
    public void PanMovesEyeAndTargetTogetherAtPerspectiveScale()
    {
        RenderCamera camera = new(
            new Vector3(0.0f, 0.0f, 10.0f),
            Vector3.Zero,
            Vector3.UnitY,
            90.0f,
            0.1f,
            100.0f);

        Assert.True(RenderCameraNavigation.TryApply(
            camera,
            RenderCameraNavigationInput.Pan(
                10.0f,
                -5.0f,
                100,
                100),
            out RenderCamera panned));

        AssertVectorNear(
            new Vector3(-2.0f, -1.0f, 10.0f),
            panned.Eye);
        AssertVectorNear(
            new Vector3(-2.0f, -1.0f, 0.0f),
            panned.Target);
        AssertVectorNear(
            camera.Target - camera.Eye,
            panned.Target - panned.Eye);
    }

    [Fact]
    public void WheelZoomIsExponentialAndCannotCrossTarget()
    {
        RenderCamera camera = new(
            new Vector3(0.0f, 0.0f, 10.0f),
            Vector3.Zero,
            Vector3.UnitY,
            60.0f,
            0.1f,
            100.0f);

        Assert.True(RenderCameraNavigation.TryApply(
            camera,
            RenderCameraNavigationInput.Zoom(120, 800, 600),
            out RenderCamera zoomed));
        Assert.Equal(
            10.0f * MathF.Exp(-0.2f),
            Vector3.Distance(zoomed.Eye, zoomed.Target),
            4);
        AssertVectorNear(camera.Target, zoomed.Target);

        Assert.True(RenderCameraNavigation.TryApply(
            zoomed,
            RenderCameraNavigationInput.Zoom(
                int.MaxValue,
                800,
                600),
            out RenderCamera closest));
        Assert.Equal(
            camera.NearPlane * 2.0f,
            Vector3.Distance(closest.Eye, closest.Target),
            4);
    }

    [Fact]
    public void InvalidCameraFailsClosedWithoutChangingValue()
    {
        RenderCamera invalid = CreateCamera() with
        {
            Eye = new Vector3(float.NaN, 0.0f, 1.0f),
        };

        Assert.False(RenderCameraNavigation.TryApply(
            invalid,
            RenderCameraNavigationInput.Pan(
                1.0f,
                1.0f,
                800,
                600),
            out RenderCamera result));
        Assert.Equal(invalid, result);
    }

    [Fact]
    public void PointerStateMapsLeftAndMiddleDragsDeterministically()
    {
        RenderCameraInputState state = new();
        Assert.True(state.BeginDrag(
            RenderCameraPointerButton.Left,
            100,
            50));
        Assert.False(state.BeginDrag(
            RenderCameraPointerButton.Middle,
            100,
            50));

        Assert.True(state.TryMove(
            112,
            43,
            800,
            600,
            out RenderCameraNavigationInput orbit));
        Assert.Equal(RenderCameraNavigationKind.Orbit, orbit.Kind);
        Assert.Equal(12.0f, orbit.HorizontalDeltaPixels);
        Assert.Equal(-7.0f, orbit.VerticalDeltaPixels);
        Assert.Equal(800, orbit.ViewportWidth);
        Assert.Equal(600, orbit.ViewportHeight);
        Assert.False(state.EndDrag(RenderCameraPointerButton.Middle));
        Assert.True(state.IsDragging);
        Assert.True(state.EndDrag(RenderCameraPointerButton.Left));
        Assert.False(state.IsDragging);

        Assert.True(state.BeginDrag(
            RenderCameraPointerButton.Middle,
            -4,
            8));
        Assert.True(state.TryMove(
            1,
            10,
            320,
            200,
            out RenderCameraNavigationInput pan));
        Assert.Equal(RenderCameraNavigationKind.Pan, pan.Kind);
        Assert.Equal(5.0f, pan.HorizontalDeltaPixels);
        Assert.Equal(2.0f, pan.VerticalDeltaPixels);
        state.CancelDrag();
        Assert.False(state.IsDragging);
    }

    [Fact]
    public void LinkedNavigationPropagatesInBothDirections()
    {
        LinkedViewportCoordinator coordinator = new();
        RenderCamera initial = coordinator.GetCamera(
            ViewportSide.Source);
        RenderCameraNavigationInput input =
            RenderCameraNavigationInput.Zoom(120, 800, 600);

        Assert.Equal(
            RenderCameraNavigationResult.Applied,
            coordinator.NavigateCamera(
                ViewportSide.Target,
                input));

        RenderCamera source = coordinator.GetCamera(
            ViewportSide.Source);
        RenderCamera target = coordinator.GetCamera(
            ViewportSide.Target);
        Assert.NotEqual(initial, source);
        Assert.Equal(source, target);
    }

    [Fact]
    public void TargetPreviewOverrideBlocksTargetNavigation()
    {
        LinkedViewportCoordinator coordinator = new()
        {
            IsLinked = false,
        };
        RenderCamera targetOrbit = CreateCamera() with
        {
            Eye = new Vector3(8.0f, 4.0f, 2.0f),
        };
        RenderCamera preview = CreateCamera() with
        {
            Eye = new Vector3(1.0f, 2.0f, 3.0f),
        };
        coordinator.UpdateCamera(
            ViewportSide.Target,
            targetOrbit);
        coordinator.SetTargetPreviewCameraOverride(preview);

        Assert.Equal(
            RenderCameraNavigationResult.PreviewCameraLocked,
            coordinator.NavigateCamera(
                ViewportSide.Target,
                RenderCameraNavigationInput.Orbit(
                    50.0f,
                    25.0f,
                    800,
                    600)));
        Assert.Equal(
            preview,
            coordinator.GetCamera(ViewportSide.Target));

        coordinator.SetTargetPreviewCameraOverride(null);
        Assert.Equal(
            targetOrbit,
            coordinator.GetCamera(ViewportSide.Target));
    }

    [Fact]
    public void SourceNavigationUpdatesLinkedOrbitBehindTargetOverride()
    {
        LinkedViewportCoordinator coordinator = new();
        RenderCamera preview = CreateCamera() with
        {
            Eye = new Vector3(1.0f, 2.0f, 3.0f),
        };
        coordinator.SetTargetPreviewCameraOverride(preview);

        Assert.Equal(
            RenderCameraNavigationResult.Applied,
            coordinator.NavigateCamera(
                ViewportSide.Source,
                RenderCameraNavigationInput.Pan(
                    20.0f,
                    -10.0f,
                    800,
                    600)));
        RenderCamera updatedOrbit = coordinator.GetCamera(
            ViewportSide.Source);
        Assert.Equal(
            preview,
            coordinator.GetCamera(ViewportSide.Target));

        coordinator.SetTargetPreviewCameraOverride(null);
        Assert.Equal(
            updatedOrbit,
            coordinator.GetCamera(ViewportSide.Target));
    }

    private static RenderCamera CreateCamera() =>
        new(
            new Vector3(2.5f, 1.8f, 4.5f),
            new Vector3(0.0f, 1.0f, 0.0f),
            Vector3.UnitY,
            60.0f,
            0.02f,
            2_000.0f);

    private static void AssertVectorNear(
        Vector3 expected,
        Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
