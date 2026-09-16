using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RenderBrushInteractionTests
{
    [Fact]
    public void CenterPixelRayUsesTheCameraForwardDirection()
    {
        RenderCamera camera = RenderCamera.Default;

        Assert.True(
            RenderBrushRay.TryCreate(
                camera,
                50,
                50,
                100,
                100,
                out RenderBrushPointerRay ray));

        Vector3 expected = Vector3.Normalize(camera.Target - camera.Eye);
        Assert.InRange(Vector3.Distance(camera.Eye + expected * camera.NearPlane, ray.Origin), 0, 1e-4f);
        Assert.InRange(Vector3.Dot(expected, ray.Direction), 0.99999f, 1.00001f);
        Assert.InRange(ray.Direction.Length(), 0.99999f, 1.00001f);
    }

    [Fact]
    public void LetterboxBarsAreRejectedAndSceneCenterRemainsStable()
    {
        RenderCamera camera = RenderCamera.Default with
        {
            ProjectionAspectRatio = 1.0f,
        };

        Assert.False(
            RenderBrushRay.TryCreate(
                camera,
                10,
                50,
                200,
                100,
                out _));
        Assert.False(
            RenderBrushRay.TryCreate(
                camera,
                150,
                50,
                200,
                100,
                out _));
        Assert.True(
            RenderBrushRay.TryCreate(
                camera,
                100,
                50,
                200,
                100,
                out RenderBrushPointerRay wideRay));
        Assert.True(
            RenderBrushRay.TryCreate(
                camera with { ProjectionAspectRatio = null },
                50,
                50,
                100,
                100,
                out RenderBrushPointerRay squareRay));

        Assert.InRange(
            Vector3.Dot(wideRay.Direction, squareRay.Direction),
            0.99999f,
            1.00001f);
    }

    [Theory]
    [InlineData(-1, 0, 100, 100)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(0, -1, 100, 100)]
    [InlineData(0, 100, 100, 100)]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, 100, 0)]
    public void InvalidPixelOrViewportIsRejected(
        int x,
        int y,
        int width,
        int height)
    {
        Assert.False(
            RenderBrushRay.TryCreate(
                RenderCamera.Default,
                x,
                y,
                width,
                height,
                out _));
    }

    [Fact]
    public void InvalidCameraIsRejectedInsteadOfBeingSilentlyRepaired()
    {
        RenderCamera invalid = RenderCamera.Default with
        {
            FarPlane = RenderCamera.Default.NearPlane,
        };

        Assert.False(
            RenderBrushRay.TryCreate(
                invalid,
                50,
                50,
                100,
                100,
                out _));
    }

    [Fact]
    public void SessionCapturesTargetAndCommitsOnce()
    {
        var source = new TestSource();
        var target = new TestBrushTarget();
        source.BrushTarget = target;
        var session = new RenderBrushDragSession(source, target);
        RenderBrushPointerRay ray = ValidRay();

        Assert.True(target.TryBeginBrush(ray));
        Assert.True(session.TryUpdate(source, ray));
        session.Complete(source, commit: true);
        session.Complete(source, commit: true);

        Assert.Equal(1, target.UpdateCount);
        Assert.Equal([true], target.Completions);
    }

    [Fact]
    public void SessionCancelsWhenSourceTargetOrEnablementChanges()
    {
        var source = new TestSource();
        var original = new TestBrushTarget();
        var replacement = new TestBrushTarget();
        source.BrushTarget = original;
        var session = new RenderBrushDragSession(source, original);

        source.BrushTarget = replacement;
        Assert.False(session.TryUpdate(source, ValidRay()));
        session.Complete(source, commit: true);
        Assert.Equal([false], original.Completions);

        var disabledSource = new TestSource();
        var disabled = new TestBrushTarget { IsBrushEnabled = false };
        disabledSource.BrushTarget = disabled;
        var disabledSession = new RenderBrushDragSession(disabledSource, disabled);
        Assert.False(disabledSession.TryUpdate(disabledSource, ValidRay()));
        Assert.Equal([false], disabled.Completions);
    }

    [Fact]
    public void SessionCancelsWhenTheSceneSourceChanges()
    {
        var source = new TestSource();
        var target = new TestBrushTarget();
        source.BrushTarget = target;
        var otherSource = new TestSource { BrushTarget = new TestBrushTarget() };
        var session = new RenderBrushDragSession(source, target);

        Assert.False(session.TryUpdate(otherSource, ValidRay()));
        Assert.Equal([false], target.Completions);
    }

    private static RenderBrushPointerRay ValidRay() =>
        new(Vector3.Zero, -Vector3.UnitZ);

    private sealed class TestSource : IRenderSceneSource, IRenderBrushSource
    {
        public IRenderBrushTarget? BrushTarget { get; set; }

        public RenderFrameSnapshot CaptureFrame() =>
            RenderFrameSnapshot.Empty();
    }

    private sealed class TestBrushTarget : IRenderBrushTarget
    {
        public bool IsBrushEnabled { get; set; } = true;

        public int UpdateCount { get; private set; }

        public List<bool> Completions { get; } = [];

        public bool TryBeginBrush(RenderBrushPointerRay ray) => IsBrushEnabled;

        public bool UpdateBrush(RenderBrushPointerRay ray)
        {
            UpdateCount++;
            return IsBrushEnabled;
        }

        public void CompleteBrush(bool commit) => Completions.Add(commit);
    }
}
