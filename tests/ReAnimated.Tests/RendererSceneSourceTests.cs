using System.Numerics;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererSceneSourceTests
{
    [Fact]
    public void LinkedViewportCameraPropagatesInBothDirections()
    {
        LinkedViewportCoordinator coordinator = new();
        RenderCamera authored = RenderCamera.Default with
        {
            Eye = new Vector3(7.0f, 3.0f, 2.0f),
            VerticalFieldOfViewDegrees = 74.0f,
        };

        coordinator.UpdateCamera(ViewportSide.Source, authored);

        Assert.Equal(authored, coordinator.GetCamera(ViewportSide.Source));
        Assert.Equal(authored, coordinator.GetCamera(ViewportSide.Target));

        coordinator.IsLinked = false;
        RenderCamera targetOnly = authored with { NearPlane = 0.08f };
        coordinator.UpdateCamera(ViewportSide.Target, targetOnly);

        Assert.Equal(authored, coordinator.GetCamera(ViewportSide.Source));
        Assert.Equal(targetOnly, coordinator.GetCamera(ViewportSide.Target));
    }

    [Fact]
    public void EvaluatedTargetCameraOverridePreservesBothOrbitCameras()
    {
        LinkedViewportCoordinator coordinator = new();
        RenderCamera orbit = RenderCamera.Default with
        {
            Eye = new Vector3(8.0f, 4.0f, 2.0f),
        };
        RenderCamera evaluated = RenderCamera.Default with
        {
            Eye = new Vector3(1.0f, 2.0f, 3.0f),
        };
        coordinator.UpdateCamera(ViewportSide.Source, orbit);

        coordinator.SetTargetPreviewCameraOverride(evaluated);

        Assert.Equal(orbit, coordinator.GetCamera(ViewportSide.Source));
        Assert.Equal(evaluated, coordinator.GetCamera(ViewportSide.Target));

        coordinator.SetTargetPreviewCameraOverride(null);

        Assert.Equal(orbit, coordinator.GetCamera(ViewportSide.Source));
        Assert.Equal(orbit, coordinator.GetCamera(ViewportSide.Target));
    }

    [Fact]
    public void EvaluatedTargetCameraCanBeSuspendedAcrossWorkspaceLayoutChanges()
    {
        LinkedViewportCoordinator coordinator = new();
        RenderCamera orbit = RenderCamera.Default with
        {
            Eye = new Vector3(9.0f, 5.0f, 3.0f),
        };
        RenderCamera evaluated = RenderCamera.Default with
        {
            Eye = new Vector3(0.25f, 1.7f, 0.1f),
            VerticalFieldOfViewDegrees = 72.0f,
        };
        coordinator.UpdateCamera(ViewportSide.Source, orbit);
        coordinator.SetTargetPreviewCameraOverride(evaluated);

        Assert.False(
            coordinator.SetTargetPreviewCameraOverrideActive(false));
        Assert.False(coordinator.HasTargetPreviewCameraOverride);
        Assert.Equal(orbit, coordinator.GetCamera(ViewportSide.Source));
        Assert.Equal(orbit, coordinator.GetCamera(ViewportSide.Target));
        Assert.Equal(
            RenderCameraNavigationResult.Applied,
            coordinator.NavigateCamera(
                ViewportSide.Target,
                RenderCameraNavigationInput.Pan(
                    8.0f,
                    -4.0f,
                    800,
                    600)));

        Assert.True(
            coordinator.SetTargetPreviewCameraOverrideActive(true));
        Assert.True(coordinator.HasTargetPreviewCameraOverride);
        Assert.Equal(evaluated, coordinator.GetCamera(ViewportSide.Target));
        Assert.Equal(
            RenderCameraNavigationResult.PreviewCameraLocked,
            coordinator.NavigateCamera(
                ViewportSide.Target,
                RenderCameraNavigationInput.Orbit(
                    8.0f,
                    -4.0f,
                    800,
                    600)));
    }

    [Fact]
    public void Dl1CameraAdapterUsesRetailMtx34DirectionAndUpAxes()
    {
        TransformMatrix world =
            TransformMatrix.CreateTranslation(
                new Vector3D(2.0, 3.0, 4.0));
        var camera = new EvaluatedCamera(
            world,
            new CameraLens(72.0, 16.0 / 9.0, 0.03, 900.0),
            true,
            EvaluatedCameraSource.Dl1FppEyeCamera);

        RenderCamera rendered =
            Dl1PreviewCameraAdapter.ToRenderCamera(camera);

        Assert.Equal(new Vector3(2.0f, 3.0f, 4.0f), rendered.Eye);
        Assert.Equal(new Vector3(2.0f, 3.0f, 5.0f), rendered.Target);
        Assert.Equal(-Vector3.UnitY, rendered.Up);
        Assert.Equal(72.0f, rendered.VerticalFieldOfViewDegrees);
        Assert.Equal(0.03f, rendered.NearPlane);
        Assert.Equal(900.0f, rendered.FarPlane);
    }

    [Fact]
    public void SceneSourceTakesStableCopiesOfMutableLists()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Source,
            new Vector4(0.1f, 0.2f, 0.3f, 1.0f));
        List<GizmoRenderData> gizmos =
        [
            new(
                GizmoKind.Axis,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.0f),
        ];

        source.SetGizmos(gizmos);
        gizmos.Clear();

        RenderFrameSnapshot frame = source.CaptureFrame();
        Assert.Single(frame.Gizmos);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 1.0f), frame.ClearColor);
    }

    [Fact]
    public void SceneSourcePrecomputesAndReusesImmutableDeformedBounds()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        var mesh = new MeshRenderData(
            "bounds",
            new MeshVertex[]
            {
                new(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            false);
        var overlays = new RenderAuthoringOverlayState(
            new RenderAuthoringOverlayOptions(
                ShowDeformedBounds: true),
            null);
        source.SetMeshes([mesh]);

        source.SetAuthoringOverlays(overlays);
        RenderFrameSnapshot first = source.CaptureFrame();
        source.SetAuthoringOverlays(overlays);
        RenderFrameSnapshot second = source.CaptureFrame();

        Assert.Single(first.AuthoringOverlays.DeformedMeshBounds);
        Assert.True(
            first.AuthoringOverlays.DeformedMeshBounds.Equals(
                second.AuthoringOverlays.DeformedMeshBounds));

        source.SetMeshes(
        [
            mesh with
            {
                LocalToWorld =
                    Matrix4x4.CreateTranslation(5.0f, 0.0f, 0.0f),
            },
        ]);
        source.SetAuthoringOverlays(overlays);
        RenderFrameSnapshot moved = source.CaptureFrame();

        Assert.False(
            first.AuthoringOverlays.DeformedMeshBounds.Equals(
                moved.AuthoringOverlays.DeformedMeshBounds));
        Assert.Equal(
            new Vector3(5.0f, 0.0f, 0.0f),
            Assert.Single(
                moved.AuthoringOverlays.DeformedMeshBounds).Minimum);
    }

    [Fact]
    public void SceneSourcePublishesBoneSelectionWithoutMutatingInput()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        BoneRenderData[] bones =
        [
            new(
                "root",
                -1,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                false),
            new(
                "child",
                0,
                Matrix4x4.Identity,
                Matrix4x4.CreateTranslation(Vector3.UnitY),
                false),
        ];
        source.SetSkeleton(new SkeletonRenderData(
            bones,
            Matrix4x4.Identity));

        source.SelectBone(1);
        RenderFrameSnapshot frame = source.CaptureFrame();

        Assert.False(frame.Skeleton!.Bones[0].IsSelected);
        Assert.True(frame.Skeleton.Bones[1].IsSelected);
        Assert.False(bones[1].IsSelected);
    }

    [Fact]
    public void SceneSourceKeepsSkeletonRoleVisibilityAcrossSceneReplacement()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        source.SetSkeletonVisibility(
            showDeformBones: true,
            showHelpers: true,
            showCameraHelpers: false,
            showProps: true);
        var helper = new BoneRenderData(
            "offset_helper",
            -1,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            false)
        {
            Role = BoneRenderRole.Helper,
        };

        source.SetScene(
            [],
            new SkeletonRenderData(
                [helper],
                Matrix4x4.Identity),
            []);
        SkeletonRenderData first =
            Assert.IsType<SkeletonRenderData>(
                source.CaptureFrame().Skeleton);
        Assert.True(first.ShowHelpers);
        Assert.False(first.ShowCameraHelpers);
        Assert.True(first.ShowProps);
        Assert.Equal(
            BoneRenderRole.Helper,
            Assert.Single(first.Bones).Role);

        source.SetSkeleton(new SkeletonRenderData(
            [helper],
            Matrix4x4.Identity));
        SkeletonRenderData replacement =
            Assert.IsType<SkeletonRenderData>(
                source.CaptureFrame().Skeleton);
        Assert.True(replacement.ShowHelpers);
        Assert.False(replacement.ShowCameraHelpers);
        Assert.True(replacement.ShowProps);
    }

    [Fact]
    public void EvaluatedTargetCameraBlocksTranslationGizmoTarget()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        var target = new RecordingTranslationGizmoTarget();
        source.SetTranslationGizmoTarget(target);
        var binding = new TranslationGizmoBinding(
            0,
            TranslationGizmoAxis.X,
            RenderGizmoSpace.Global);
        var start = new RenderTranslationGizmoDragStart(
            binding,
            Vector3.UnitX);

        Assert.True(source.TryBeginTranslationGizmoDrag(start));
        Assert.Equal(1, target.BeginCount);

        coordinator.SetTargetPreviewCameraOverride(
            RenderCamera.Default);

        Assert.False(source.TryBeginTranslationGizmoDrag(start));
        Assert.False(source.UpdateTranslationGizmoDrag(
            new RenderTranslationGizmoDragUpdate(
                binding,
                Vector3.UnitX,
                1.0f)));
        Assert.Equal(1, target.BeginCount);
        Assert.Equal(0, target.UpdateCount);
        source.CompleteTranslationGizmoDrag(commit: true);
        Assert.False(Assert.Single(target.Completions));
    }

    [Fact]
    public void EvaluatedTargetCameraBlocksTransformGizmoTarget()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        var target = new RecordingTransformGizmoTarget();
        source.SetTransformGizmoTarget(target);
        var binding = new RenderTransformGizmoBinding(
            0,
            RenderTransformGizmoMode.Rotate,
            RenderTransformGizmoAxis.Y,
            RenderGizmoSpace.Global);
        var start = new RenderTransformGizmoDragStart(
            binding,
            Vector3.UnitY);

        Assert.True(source.TryBeginTransformGizmoDrag(start));
        Assert.Equal(1, target.BeginCount);

        coordinator.SetTargetPreviewCameraOverride(
            RenderCamera.Default);

        Assert.False(source.TryBeginTransformGizmoDrag(start));
        Assert.False(source.UpdateTransformGizmoDrag(
            new RenderTransformGizmoDragUpdate(
                binding,
                Vector3.Zero,
                0.0f,
                0.25f,
                1.0f)));
        Assert.Equal(1, target.BeginCount);
        Assert.Equal(0, target.UpdateCount);
        source.CompleteTransformGizmoDrag(commit: true);
        Assert.False(Assert.Single(target.Completions));
    }

    private sealed class RecordingTranslationGizmoTarget :
        IRenderTranslationGizmoTarget
    {
        public int BeginCount { get; private set; }

        public int UpdateCount { get; private set; }

        public List<bool> Completions { get; } = [];

        public bool TryBeginTranslationGizmoDrag(
            RenderTranslationGizmoDragStart start)
        {
            BeginCount++;
            return true;
        }

        public bool UpdateTranslationGizmoDrag(
            RenderTranslationGizmoDragUpdate update)
        {
            UpdateCount++;
            return true;
        }

        public void CompleteTranslationGizmoDrag(bool commit)
        {
            Completions.Add(commit);
        }
    }

    private sealed class RecordingTransformGizmoTarget :
        IRenderTransformGizmoTarget
    {
        public int BeginCount { get; private set; }

        public int UpdateCount { get; private set; }

        public List<bool> Completions { get; } = [];

        public bool TryBeginTransformGizmoDrag(
            RenderTransformGizmoDragStart start)
        {
            BeginCount++;
            return true;
        }

        public bool UpdateTransformGizmoDrag(
            RenderTransformGizmoDragUpdate update)
        {
            UpdateCount++;
            return true;
        }

        public void CompleteTransformGizmoDrag(bool commit)
        {
            Completions.Add(commit);
        }
    }
}
