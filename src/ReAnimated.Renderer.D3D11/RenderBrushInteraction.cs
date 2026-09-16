using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// A pointer ray expressed in the same world space as the current render
/// frame. The ray origin is on the camera's near clip plane and the direction is
/// normalized.
/// </summary>
public readonly record struct RenderBrushPointerRay(
    Vector3 Origin,
    Vector3 Direction);

/// <summary>
/// Optional scene-source target for a transient brush stroke. The target is
/// captured for the complete pointer gesture and receives a cancellation when
/// the source, target, focus, or capture changes.
/// </summary>
public interface IRenderBrushTarget
{
    bool IsBrushEnabled { get; }

    bool TryBeginBrush(RenderBrushPointerRay ray);

    bool UpdateBrush(RenderBrushPointerRay ray);

    void CompleteBrush(bool commit);
}

/// <summary>
/// Supplies the current brush target independently of the scene contents.
/// </summary>
public interface IRenderBrushSource
{
    IRenderBrushTarget? BrushTarget { get; }
}

/// <summary>
/// Isolated brush gesture state. It captures the target object and its source
/// object, so replacing a forwarding property cannot commit an old stroke.
/// </summary>
public sealed class RenderBrushDragSession
{
    private readonly IRenderSceneSource _source;
    private readonly IRenderBrushTarget _target;
    private bool _completed;

    public RenderBrushDragSession(
        IRenderSceneSource source,
        IRenderBrushTarget target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        _source = source;
        _target = target;
    }

    public IRenderSceneSource Source => _source;

    public IRenderBrushTarget Target => _target;

    public bool IsCompleted => _completed;

    public bool TryUpdate(
        IRenderSceneSource? currentSource,
        RenderBrushPointerRay ray)
    {
        if (_completed || !IsCurrent(currentSource))
        {
            Cancel();
            return false;
        }

        bool updated;
        try
        {
            updated = _target.UpdateBrush(ray);
        }
        catch
        {
            Cancel();
            return false;
        }

        if (!updated)
        {
            Cancel();
        }

        return updated;
    }

    public void Complete(
        IRenderSceneSource? currentSource,
        bool commit)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        bool accepted = commit && IsCurrent(currentSource);
        try
        {
            _target.CompleteBrush(accepted);
        }
        catch
        {
            // The gesture is already closed. A target exception must not leave
            // the host believing that capture or a brush transaction remains
            // active.
        }
    }

    public void Cancel() => Complete(_source, commit: false);

    private bool IsCurrent(IRenderSceneSource? currentSource) =>
        ReferenceEquals(currentSource, _source) &&
        currentSource is IRenderBrushSource brushSource &&
        ReferenceEquals(brushSource.BrushTarget, _target) &&
        _target.IsBrushEnabled;
}

/// <summary>
/// Converts a physical client pixel into a world-space ray using the exact
/// camera projection used by the line and mesh passes.
/// </summary>
public static class RenderBrushRay
{
    public static bool TryCreate(
        RenderCamera camera,
        int x,
        int y,
        int width,
        int height,
        out RenderBrushPointerRay ray)
    {
        ray = default;
        if (!IsValidCamera(camera) ||
            width <= 0 ||
            height <= 0 ||
            x < 0 ||
            y < 0 ||
            x >= width ||
            y >= height)
        {
            return false;
        }

        RenderViewportRectangle viewport =
            RenderCameraMath.CreateSceneViewport(camera, width, height);
        if (!IsFinite(viewport) ||
            viewport.Width <= 0.0f ||
            viewport.Height <= 0.0f ||
            x < viewport.X ||
            y < viewport.Y ||
            x >= viewport.X + viewport.Width ||
            y >= viewport.Y + viewport.Height)
        {
            return false;
        }

        float normalizedX =
            ((x - viewport.X) / viewport.Width) * 2.0f - 1.0f;
        float normalizedY =
            1.0f - ((y - viewport.Y) / viewport.Height) * 2.0f;
        if (!float.IsFinite(normalizedX) ||
            !float.IsFinite(normalizedY))
        {
            return false;
        }

        Matrix4x4 viewProjection =
            RenderCameraMath.CreateViewProjection(camera, width, height);
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse))
        {
            return false;
        }

        Vector4 farClip = Vector4.Transform(
            new Vector4(normalizedX, normalizedY, 1.0f, 1.0f),
            inverse);
        Vector4 nearClip = Vector4.Transform(new Vector4(normalizedX, normalizedY, 0.0f, 1.0f), inverse);
        if (!IsFinite(farClip) || MathF.Abs(farClip.W) <= 1.0e-8f || !IsFinite(nearClip) || MathF.Abs(nearClip.W) <= 1.0e-8f)
        {
            return false;
        }

        Vector3 farWorld = new(
            farClip.X / farClip.W,
            farClip.Y / farClip.W,
            farClip.Z / farClip.W);
        Vector3 nearWorld = new(nearClip.X / nearClip.W, nearClip.Y / nearClip.W, nearClip.Z / nearClip.W);
        Vector3 direction = farWorld - nearWorld;
        float lengthSquared = direction.LengthSquared();
        if (!IsFinite(farWorld) || !IsFinite(nearWorld) ||
            !IsFinite(direction) ||
            !float.IsFinite(lengthSquared) ||
            lengthSquared <= 1.0e-12f)
        {
            return false;
        }

        direction /= MathF.Sqrt(lengthSquared);
        if (!IsFinite(direction))
        {
            return false;
        }

        ray = new RenderBrushPointerRay(nearWorld, direction);
        return true;
    }

    private static bool IsValidCamera(RenderCamera camera)
    {
        if (!IsFinite(camera.Eye) ||
            !IsFinite(camera.Target) ||
            !IsFinite(camera.Up) ||
            !float.IsFinite(camera.VerticalFieldOfViewDegrees) ||
            !float.IsFinite(camera.NearPlane) ||
            !float.IsFinite(camera.FarPlane) ||
            camera.VerticalFieldOfViewDegrees <= 1.0f ||
            camera.VerticalFieldOfViewDegrees >= 179.0f ||
            camera.NearPlane <= 0.0f ||
            camera.FarPlane <= camera.NearPlane)
        {
            return false;
        }

        if (camera.ProjectionAspectRatio is float aspect &&
            (!float.IsFinite(aspect) || aspect <= 0.0f))
        {
            return false;
        }

        Vector3 forward = camera.Target - camera.Eye;
        return IsFinite(forward) &&
            forward.LengthSquared() > 1.0e-8f &&
            camera.Up.LengthSquared() > 1.0e-8f &&
            Vector3.Cross(forward, camera.Up).LengthSquared() > 1.0e-8f;
    }

    private static bool IsFinite(RenderViewportRectangle rectangle) =>
        float.IsFinite(rectangle.X) &&
        float.IsFinite(rectangle.Y) &&
        float.IsFinite(rectangle.Width) &&
        float.IsFinite(rectangle.Height);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
