using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererTranslationGizmoTests
{
    private static readonly TranslationGizmoBinding XBinding =
        new(
            3,
            TranslationGizmoAxis.X,
            RenderGizmoSpace.Local);

    [Fact]
    public void HitTestStartsBoundHandleAndProducesCumulativeWorldDelta()
    {
        RenderFrameSnapshot frame = CreateFrame(
            new GizmoRenderData(
                GizmoKind.TranslationHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.UnitX,
                1.5f,
                XBinding));

        Assert.True(RenderTranslationGizmoHitTest.TryBeginDrag(
            frame,
            450,
            300,
            800,
            600,
            out RenderTranslationGizmoDragSession? session));
        Assert.NotNull(session);
        Assert.Equal(XBinding, session.Binding);
        Assert.Equal(Vector3.UnitX, session.AxisDirectionWorld);

        Assert.True(session.TryUpdate(
            470,
            300,
            out RenderTranslationGizmoDragUpdate first));
        Assert.True(session.TryUpdate(
            490,
            300,
            out RenderTranslationGizmoDragUpdate second));

        Assert.Equal(XBinding, first.Binding);
        Assert.True(first.AxisDistance > 0.0f);
        Assert.True(second.AxisDistance > first.AxisDistance);
        Assert.Equal(first.AxisDistance, first.WorldDelta.X, 5);
        Assert.Equal(second.AxisDistance, second.WorldDelta.X, 5);
        Assert.Equal(0.0f, second.WorldDelta.Y);
        Assert.Equal(0.0f, second.WorldDelta.Z);
        Assert.True(session.HasMeaningfulMovement);
    }

    [Fact]
    public void UnboundAndDegenerateHandlesFailClosed()
    {
        RenderFrameSnapshot unbound = CreateFrame(
            new GizmoRenderData(
                GizmoKind.TranslationHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.5f));
        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            unbound,
            450,
            300,
            800,
            600,
            out _));

        RenderFrameSnapshot zeroLength = CreateFrame(
            new GizmoRenderData(
                GizmoKind.TranslationHandle,
                Vector3.Zero,
                Vector3.Zero,
                Vector4.One,
                1.5f,
                XBinding));
        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            zeroLength,
            400,
            300,
            800,
            600,
            out _));

        RenderFrameSnapshot cameraParallel = CreateFrame(
            new GizmoRenderData(
                GizmoKind.TranslationHandle,
                Vector3.Zero,
                Vector3.UnitZ,
                Vector4.One,
                1.5f,
                XBinding));
        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            cameraParallel,
            400,
            300,
            800,
            600,
            out _));
    }

    [Theory]
    [InlineData(99, (int)RenderGizmoSpace.Local)]
    [InlineData((int)TranslationGizmoAxis.X, 99)]
    public void UndefinedLegacyBindingEnumsFailClosed(
        int axis,
        int space)
    {
        var malformed = new TranslationGizmoBinding(
            0,
            (TranslationGizmoAxis)axis,
            (RenderGizmoSpace)space);
        RenderFrameSnapshot frame = CreateFrame(
            new GizmoRenderData(
                GizmoKind.TranslationHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.5f,
                malformed));

        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            frame,
            450,
            300,
            800,
            600,
            out _));
    }

    [Fact]
    public void InvalidCameraViewportPointerAndMissFailClosed()
    {
        RenderFrameSnapshot frame = CreateFrame(
            new GizmoRenderData(
                GizmoKind.TranslationHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.5f,
                XBinding));
        RenderFrameSnapshot invalidCamera = frame with
        {
            Camera = frame.Camera with
            {
                Eye = new Vector3(float.NaN, 0.0f, 5.0f),
            },
        };

        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            invalidCamera,
            450,
            300,
            800,
            600,
            out _));
        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            frame,
            450,
            300,
            0,
            600,
            out _));
        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            frame,
            -1,
            300,
            800,
            600,
            out _));
        Assert.False(RenderTranslationGizmoHitTest.TryBeginDrag(
            frame,
            450,
            100,
            800,
            600,
            out _));
    }

    private static RenderFrameSnapshot CreateFrame(
        params GizmoRenderData[] gizmos) =>
        RenderFrameSnapshot.Empty() with
        {
            Camera = new RenderCamera(
                new Vector3(0.0f, 0.0f, 5.0f),
                Vector3.Zero,
                Vector3.UnitY,
                60.0f,
                0.1f,
                100.0f),
            Gizmos = gizmos,
        };
}
