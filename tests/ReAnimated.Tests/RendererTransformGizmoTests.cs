using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererTransformGizmoTests
{
    [Theory]
    [InlineData(
        RenderTransformGizmoMode.Translate,
        GizmoKind.TranslationHandle)]
    [InlineData(
        RenderTransformGizmoMode.Rotate,
        GizmoKind.RotationHandle)]
    [InlineData(
        RenderTransformGizmoMode.Scale,
        GizmoKind.ScaleHandle)]
    public void PointerHitProducesFiniteModeSpecificDrag(
        RenderTransformGizmoMode mode,
        GizmoKind kind)
    {
        var binding = new RenderTransformGizmoBinding(
            4,
            mode,
            RenderTransformGizmoAxis.Z,
            mode == RenderTransformGizmoMode.Scale
                ? RenderGizmoSpace.Local
                : RenderGizmoSpace.Global);
        RenderFrameSnapshot frame = CreateFrame(
            new GizmoRenderData(
                kind,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.5f,
                TransformBinding: binding,
                InteractionAxisWorld: Vector3.UnitZ));

        Assert.True(RenderTransformGizmoHitTest.TryBeginDrag(
            frame,
            450,
            300,
            800,
            600,
            out RenderTransformGizmoDragSession? session));
        Assert.NotNull(session);
        Assert.Equal(binding, session.Binding);
        Assert.Equal(Vector3.UnitZ, session.AxisDirectionWorld);
        Assert.True(session.TryUpdate(
            475,
            300,
            out RenderTransformGizmoDragUpdate update));

        Assert.Equal(binding, update.Binding);
        Assert.True(float.IsFinite(update.AxisDistance));
        Assert.True(float.IsFinite(update.RotationRadians));
        Assert.True(float.IsFinite(update.ScaleFactor));
        Assert.True(update.ScaleFactor > 0.0f);
        Assert.True(session.HasMeaningfulMovement);
        switch (mode)
        {
            case RenderTransformGizmoMode.Translate:
                Assert.True(update.WorldDelta.Z > 0.0f);
                break;
            case RenderTransformGizmoMode.Rotate:
                Assert.True(update.RotationRadians > 0.0f);
                break;
            case RenderTransformGizmoMode.Scale:
                Assert.True(update.ScaleFactor > 1.0f);
                break;
        }
    }

    [Fact]
    public void ScaleDragRemainsPositiveAndBoundedAtPointerExtremes()
    {
        var binding = new RenderTransformGizmoBinding(
            0,
            RenderTransformGizmoMode.Scale,
            RenderTransformGizmoAxis.X,
            RenderGizmoSpace.Local);
        RenderFrameSnapshot frame = CreateFrame(
            new GizmoRenderData(
                GizmoKind.ScaleHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.0f,
                TransformBinding: binding));
        Assert.True(RenderTransformGizmoHitTest.TryBeginDrag(
            frame,
            450,
            300,
            800,
            600,
            out RenderTransformGizmoDragSession? session));
        Assert.NotNull(session);

        Assert.True(session.TryUpdate(
            int.MaxValue,
            300,
            out RenderTransformGizmoDragUpdate maximum));
        Assert.Equal(100.0f, maximum.ScaleFactor);
        Assert.True(session.TryUpdate(
            int.MinValue,
            300,
            out RenderTransformGizmoDragUpdate minimum));
        Assert.Equal(0.01f, minimum.ScaleFactor);
    }

    [Fact]
    public void BindingKindMismatchAndInvalidInteractionAxisFailClosed()
    {
        var rotate = new RenderTransformGizmoBinding(
            0,
            RenderTransformGizmoMode.Rotate,
            RenderTransformGizmoAxis.X,
            RenderGizmoSpace.Local);
        RenderFrameSnapshot wrongKind = CreateFrame(
            new GizmoRenderData(
                GizmoKind.ScaleHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.0f,
                TransformBinding: rotate));
        Assert.False(RenderTransformGizmoHitTest.TryBeginDrag(
            wrongKind,
            450,
            300,
            800,
            600,
            out _));

        RenderFrameSnapshot invalidAxis = CreateFrame(
            new GizmoRenderData(
                GizmoKind.RotationHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.0f,
                TransformBinding: rotate,
                InteractionAxisWorld:
                    new Vector3(float.NaN, 0.0f, 0.0f)));
        Assert.False(RenderTransformGizmoHitTest.TryBeginDrag(
            invalidAxis,
            450,
            300,
            800,
            600,
            out _));

        var globalScale = new RenderTransformGizmoBinding(
            0,
            RenderTransformGizmoMode.Scale,
            RenderTransformGizmoAxis.X,
            RenderGizmoSpace.Global);
        RenderFrameSnapshot invalidScaleSpace = CreateFrame(
            new GizmoRenderData(
                GizmoKind.ScaleHandle,
                Vector3.Zero,
                Vector3.UnitX,
                Vector4.One,
                1.0f,
                TransformBinding: globalScale));
        Assert.False(RenderTransformGizmoHitTest.TryBeginDrag(
            invalidScaleSpace,
            450,
            300,
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
