using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

public enum RenderTransformGizmoMode
{
    Translate,
    Rotate,
    Scale,
}

public enum RenderTransformGizmoAxis
{
    X,
    Y,
    Z,
}

public readonly record struct RenderTransformGizmoBinding(
    int BoneIndex,
    RenderTransformGizmoMode Mode,
    RenderTransformGizmoAxis Axis,
    RenderGizmoSpace Space);

public readonly record struct RenderTransformGizmoDragStart(
    RenderTransformGizmoBinding Binding,
    Vector3 AxisDirectionWorld);

public readonly record struct RenderTransformGizmoDragUpdate(
    RenderTransformGizmoBinding Binding,
    Vector3 WorldDelta,
    float AxisDistance,
    float RotationRadians,
    float ScaleFactor);

/// <summary>
/// Scene-source contract for transient transform-gizmo preview followed by one
/// explicit commit or cancellation.
/// </summary>
public interface IRenderTransformGizmoTarget
{
    bool TryBeginTransformGizmoDrag(
        RenderTransformGizmoDragStart start);

    bool UpdateTransformGizmoDrag(
        RenderTransformGizmoDragUpdate update);

    void CompleteTransformGizmoDrag(bool commit);
}

public sealed class RenderTransformGizmoDragSession
{
    private const float MinimumTranslationDistance = 1.0e-6f;
    private const float MinimumRotationRadians = 1.0e-5f;
    private const float MinimumScaleDelta = 1.0e-5f;
    private const float RotationRadiansPerPixel = 0.01f;
    private const float ScaleExponentPerPixel = 0.01f;
    private const float MinimumScaleFactor = 0.01f;
    private const float MaximumScaleFactor = 100.0f;
    private readonly Vector2 _pointerStart;
    private readonly Vector2 _screenDirection;
    private readonly Vector3 _worldAxis;
    private readonly float _worldUnitsPerPixel;

    internal RenderTransformGizmoDragSession(
        RenderTransformGizmoBinding binding,
        Vector3 worldAxis,
        Vector2 screenDirection,
        float worldUnitsPerPixel,
        Vector2 pointerStart)
    {
        Binding = binding;
        _worldAxis = worldAxis;
        _screenDirection = screenDirection;
        _worldUnitsPerPixel = worldUnitsPerPixel;
        _pointerStart = pointerStart;
    }

    public RenderTransformGizmoBinding Binding { get; }

    public Vector3 AxisDirectionWorld => _worldAxis;

    public bool HasMeaningfulMovement { get; private set; }

    public bool TryUpdate(
        int pointerX,
        int pointerY,
        out RenderTransformGizmoDragUpdate update)
    {
        Vector2 pointerDelta =
            new Vector2(pointerX, pointerY) - _pointerStart;
        float pixelDistance = Vector2.Dot(
            pointerDelta,
            _screenDirection);
        float axisDistance =
            pixelDistance * _worldUnitsPerPixel;
        Vector3 worldDelta = _worldAxis * axisDistance;
        float rotationRadians =
            pixelDistance * RotationRadiansPerPixel;
        float scaleFactor = Math.Clamp(
            MathF.Exp(pixelDistance * ScaleExponentPerPixel),
            MinimumScaleFactor,
            MaximumScaleFactor);
        if (!float.IsFinite(pixelDistance) ||
            !float.IsFinite(axisDistance) ||
            !IsFinite(worldDelta) ||
            !float.IsFinite(rotationRadians) ||
            !float.IsFinite(scaleFactor) ||
            scaleFactor <= 0.0f)
        {
            update = default;
            return false;
        }

        HasMeaningfulMovement = Binding.Mode switch
        {
            RenderTransformGizmoMode.Translate =>
                MathF.Abs(axisDistance) >
                MinimumTranslationDistance,
            RenderTransformGizmoMode.Rotate =>
                MathF.Abs(rotationRadians) >
                MinimumRotationRadians,
            RenderTransformGizmoMode.Scale =>
                MathF.Abs(scaleFactor - 1.0f) >
                MinimumScaleDelta,
            _ => false,
        };
        update = new RenderTransformGizmoDragUpdate(
            Binding,
            worldDelta,
            axisDistance,
            rotationRadians,
            scaleFactor);
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public static class RenderTransformGizmoHitTest
{
    private const float MinimumWorldSegmentLength = 1.0e-6f;
    private const float MinimumScreenSegmentLength = 4.0f;
    private const float BaseHitRadiusPixels = 7.0f;

    public static bool TryBeginDrag(
        RenderFrameSnapshot frame,
        int pointerX,
        int pointerY,
        int viewportWidth,
        int viewportHeight,
        out RenderTransformGizmoDragSession? session)
    {
        ArgumentNullException.ThrowIfNull(frame);
        session = null;
        if (!HasValidCamera(frame.Camera) ||
            viewportWidth <= 0 ||
            viewportHeight <= 0 ||
            pointerX < 0 ||
            pointerX >= viewportWidth ||
            pointerY < 0 ||
            pointerY >= viewportHeight)
        {
            return false;
        }

        Matrix4x4 viewProjection =
            RenderCameraMath.CreateViewProjection(
                frame.Camera,
                viewportWidth,
                viewportHeight);
        var pointer = new Vector2(pointerX, pointerY);
        float bestDistance = float.PositiveInfinity;
        foreach (GizmoRenderData gizmo in frame.Gizmos)
        {
            if (gizmo.TransformBinding is not { } binding ||
                !KindMatchesMode(gizmo.Kind, binding.Mode) ||
                !Enum.IsDefined(binding.Axis) ||
                !Enum.IsDefined(binding.Space) ||
                (binding.Mode == RenderTransformGizmoMode.Scale &&
                 binding.Space != RenderGizmoSpace.Local) ||
                binding.BoneIndex < 0 ||
                !float.IsFinite(gizmo.Thickness))
            {
                continue;
            }

            Vector3 worldSegment = gizmo.End - gizmo.Start;
            float worldLength = worldSegment.Length();
            Vector3 worldAxis =
                gizmo.InteractionAxisWorld ?? worldSegment;
            if (!TryNormalize(ref worldAxis) ||
                !float.IsFinite(worldLength) ||
                worldLength < MinimumWorldSegmentLength ||
                !TryProject(
                    gizmo.Start,
                    viewProjection,
                    viewportWidth,
                    viewportHeight,
                    out Vector2 screenStart) ||
                !TryProject(
                    gizmo.End,
                    viewProjection,
                    viewportWidth,
                    viewportHeight,
                    out Vector2 screenEnd))
            {
                continue;
            }

            Vector2 screenSegment = screenEnd - screenStart;
            float screenLength = screenSegment.Length();
            if (!float.IsFinite(screenLength) ||
                screenLength < MinimumScreenSegmentLength)
            {
                continue;
            }

            float distance = DistanceToSegment(
                pointer,
                screenStart,
                screenEnd);
            float hitRadius = BaseHitRadiusPixels +
                Math.Clamp(gizmo.Thickness, 0.0f, 4.0f);
            if (distance > hitRadius ||
                distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            session = new RenderTransformGizmoDragSession(
                binding,
                worldAxis,
                screenSegment / screenLength,
                worldLength / screenLength,
                pointer);
        }

        return session is not null;
    }

    private static bool KindMatchesMode(
        GizmoKind kind,
        RenderTransformGizmoMode mode) =>
        (kind, mode) switch
        {
            (
                GizmoKind.TranslationHandle,
                RenderTransformGizmoMode.Translate) => true,
            (
                GizmoKind.RotationHandle,
                RenderTransformGizmoMode.Rotate) => true,
            (
                GizmoKind.ScaleHandle,
                RenderTransformGizmoMode.Scale) => true,
            _ => false,
        };

    private static bool HasValidCamera(RenderCamera camera)
    {
        if (!IsFinite(camera.Eye) ||
            !IsFinite(camera.Target) ||
            !IsFinite(camera.Up) ||
            !float.IsFinite(camera.VerticalFieldOfViewDegrees) ||
            !float.IsFinite(camera.NearPlane) ||
            !float.IsFinite(camera.FarPlane) ||
            camera.VerticalFieldOfViewDegrees <= 0.0f ||
            camera.VerticalFieldOfViewDegrees >= 180.0f ||
            camera.NearPlane <= 0.0f ||
            camera.FarPlane <= camera.NearPlane)
        {
            return false;
        }

        Vector3 forward = camera.Target - camera.Eye;
        return forward.LengthSquared() > 1.0e-8f &&
               camera.Up.LengthSquared() > 1.0e-8f &&
               Vector3.Cross(forward, camera.Up).LengthSquared() >
                   1.0e-8f;
    }

    private static bool TryProject(
        Vector3 point,
        Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        out Vector2 screen)
    {
        screen = default;
        if (!IsFinite(point))
        {
            return false;
        }

        Vector4 clip = Vector4.Transform(
            new Vector4(point, 1.0f),
            viewProjection);
        if (!IsFinite(clip) ||
            clip.W <= 1.0e-6f)
        {
            return false;
        }

        float inverseW = 1.0f / clip.W;
        float normalizedX = clip.X * inverseW;
        float normalizedY = clip.Y * inverseW;
        if (!float.IsFinite(normalizedX) ||
            !float.IsFinite(normalizedY))
        {
            return false;
        }

        screen = new Vector2(
            (normalizedX * 0.5f + 0.5f) * viewportWidth,
            (-normalizedY * 0.5f + 0.5f) * viewportHeight);
        return IsFinite(screen);
    }

    private static float DistanceToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 1.0e-8f)
        {
            return Vector2.Distance(point, start);
        }

        float position = Math.Clamp(
            Vector2.Dot(point - start, segment) /
                lengthSquared,
            0.0f,
            1.0f);
        return Vector2.Distance(
            point,
            start + segment * position);
    }

    private static bool TryNormalize(ref Vector3 value)
    {
        if (!IsFinite(value) ||
            value.LengthSquared() < 1.0e-8f)
        {
            return false;
        }

        value = Vector3.Normalize(value);
        return IsFinite(value);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y);

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
