using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

public enum TranslationGizmoAxis
{
    X,
    Y,
    Z,
}

public enum RenderGizmoSpace
{
    Local,
    Global,
}

public readonly record struct TranslationGizmoBinding(
    int BoneIndex,
    TranslationGizmoAxis Axis,
    RenderGizmoSpace Space);

public readonly record struct RenderTranslationGizmoDragStart(
    TranslationGizmoBinding Binding,
    Vector3 AxisDirectionWorld);

public readonly record struct RenderTranslationGizmoDragUpdate(
    TranslationGizmoBinding Binding,
    Vector3 WorldDelta,
    float AxisDistance);

/// <summary>
/// Optional scene-source contract for staged translation-gizmo authoring. A
/// target must keep preview updates transient and commit only from
/// <see cref="CompleteTranslationGizmoDrag"/>.
/// </summary>
public interface IRenderTranslationGizmoTarget
{
    bool TryBeginTranslationGizmoDrag(
        RenderTranslationGizmoDragStart start);

    bool UpdateTranslationGizmoDrag(
        RenderTranslationGizmoDragUpdate update);

    void CompleteTranslationGizmoDrag(bool commit);
}

public sealed class RenderTranslationGizmoDragSession
{
    private const float MinimumCommittedDistance = 1.0e-6f;
    private readonly Vector2 _pointerStart;
    private readonly Vector2 _screenAxis;
    private readonly Vector3 _worldAxis;
    private readonly float _worldUnitsPerPixel;

    internal RenderTranslationGizmoDragSession(
        TranslationGizmoBinding binding,
        Vector3 worldAxis,
        Vector2 screenAxis,
        float worldUnitsPerPixel,
        Vector2 pointerStart)
    {
        Binding = binding;
        _worldAxis = worldAxis;
        _screenAxis = screenAxis;
        _worldUnitsPerPixel = worldUnitsPerPixel;
        _pointerStart = pointerStart;
    }

    public TranslationGizmoBinding Binding { get; }

    public Vector3 AxisDirectionWorld => _worldAxis;

    public bool HasMeaningfulMovement { get; private set; }

    public bool TryUpdate(
        int pointerX,
        int pointerY,
        out RenderTranslationGizmoDragUpdate update)
    {
        var pointerDelta = new Vector2(
            pointerX,
            pointerY) - _pointerStart;
        float pixelDistance = Vector2.Dot(
            pointerDelta,
            _screenAxis);
        float axisDistance =
            pixelDistance * _worldUnitsPerPixel;
        Vector3 worldDelta = _worldAxis * axisDistance;
        if (!float.IsFinite(axisDistance) ||
            !IsFinite(worldDelta))
        {
            update = default;
            return false;
        }

        HasMeaningfulMovement =
            MathF.Abs(axisDistance) >
            MinimumCommittedDistance;
        update = new RenderTranslationGizmoDragUpdate(
            Binding,
            worldDelta,
            axisDistance);
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public static class RenderTranslationGizmoHitTest
{
    private const float MinimumWorldAxisLength = 1.0e-6f;
    private const float MinimumScreenAxisLength = 4.0f;
    private const float BaseHitRadiusPixels = 7.0f;

    public static bool TryBeginDrag(
        RenderFrameSnapshot frame,
        int pointerX,
        int pointerY,
        int viewportWidth,
        int viewportHeight,
        out RenderTranslationGizmoDragSession? session)
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
            if (gizmo.Kind != GizmoKind.TranslationHandle ||
                gizmo.TranslationBinding is not { } binding ||
                !Enum.IsDefined(binding.Axis) ||
                !Enum.IsDefined(binding.Space) ||
                binding.BoneIndex < 0 ||
                !float.IsFinite(gizmo.Thickness))
            {
                continue;
            }

            Vector3 worldSegment = gizmo.End - gizmo.Start;
            float worldLength = worldSegment.Length();
            if (!float.IsFinite(worldLength) ||
                worldLength < MinimumWorldAxisLength ||
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
                screenLength < MinimumScreenAxisLength)
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
            session = new RenderTranslationGizmoDragSession(
                binding,
                worldSegment / worldLength,
                screenSegment / screenLength,
                worldLength / screenLength,
                pointer);
        }

        return session is not null;
    }

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
