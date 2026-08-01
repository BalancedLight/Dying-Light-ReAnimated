using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

public enum RenderCameraNavigationKind
{
    Orbit,
    Pan,
    Zoom,
}

public enum RenderCameraPointerButton
{
    Left,
    Middle,
}

public enum RenderCameraNavigationResult
{
    Applied,
    NoChange,
    PreviewCameraLocked,
    InvalidCamera,
}

public readonly record struct RenderCameraNavigationInput(
    RenderCameraNavigationKind Kind,
    float HorizontalDeltaPixels,
    float VerticalDeltaPixels,
    int WheelDelta,
    int ViewportWidth,
    int ViewportHeight)
{
    public static RenderCameraNavigationInput Orbit(
        float horizontalDeltaPixels,
        float verticalDeltaPixels,
        int viewportWidth,
        int viewportHeight) =>
        new(
            RenderCameraNavigationKind.Orbit,
            horizontalDeltaPixels,
            verticalDeltaPixels,
            0,
            viewportWidth,
            viewportHeight);

    public static RenderCameraNavigationInput Pan(
        float horizontalDeltaPixels,
        float verticalDeltaPixels,
        int viewportWidth,
        int viewportHeight) =>
        new(
            RenderCameraNavigationKind.Pan,
            horizontalDeltaPixels,
            verticalDeltaPixels,
            0,
            viewportWidth,
            viewportHeight);

    public static RenderCameraNavigationInput Zoom(
        int wheelDelta,
        int viewportWidth,
        int viewportHeight) =>
        new(
            RenderCameraNavigationKind.Zoom,
            0.0f,
            0.0f,
            wheelDelta,
            viewportWidth,
            viewportHeight);
}

/// <summary>
/// Optional scene-source contract used only from the hosted D3D child-window
/// message boundary. Implementations decide whether an editor orbit camera may
/// be changed, so evaluated game-camera overrides remain authoritative.
/// </summary>
public interface IRenderCameraNavigationTarget
{
    RenderCameraNavigationResult NavigateCamera(
        RenderCameraNavigationInput input);
}

/// <summary>
/// Deterministic pointer gesture state shared by the Win32 host and tests.
/// It deliberately has no WPF or Win32 dependency.
/// </summary>
public sealed class RenderCameraInputState
{
    private RenderCameraPointerButton? _activeButton;
    private int _lastX;
    private int _lastY;

    public bool IsDragging => _activeButton is not null;

    public bool BeginDrag(
        RenderCameraPointerButton button,
        int x,
        int y)
    {
        if (_activeButton is not null)
        {
            return false;
        }

        _activeButton = button;
        _lastX = x;
        _lastY = y;
        return true;
    }

    public bool TryMove(
        int x,
        int y,
        int viewportWidth,
        int viewportHeight,
        out RenderCameraNavigationInput input)
    {
        if (_activeButton is not { } button)
        {
            input = default;
            return false;
        }

        int horizontalDelta = x - _lastX;
        int verticalDelta = y - _lastY;
        _lastX = x;
        _lastY = y;
        if (horizontalDelta == 0 && verticalDelta == 0)
        {
            input = default;
            return false;
        }

        input = button == RenderCameraPointerButton.Left
            ? RenderCameraNavigationInput.Orbit(
                horizontalDelta,
                verticalDelta,
                viewportWidth,
                viewportHeight)
            : RenderCameraNavigationInput.Pan(
                horizontalDelta,
                verticalDelta,
                viewportWidth,
                viewportHeight);
        return true;
    }

    public bool EndDrag(RenderCameraPointerButton button)
    {
        if (_activeButton != button)
        {
            return false;
        }

        _activeButton = null;
        return true;
    }

    public void CancelDrag()
    {
        _activeButton = null;
    }
}

public static class RenderCameraNavigation
{
    private const float MinimumCameraDistance = 0.01f;
    private const float MaximumOrbitElevationRadians =
        85.0f * (MathF.PI / 180.0f);
    private const float ZoomExponentPerWheelNotch = 0.2f;
    private const float WheelDeltaPerNotch = 120.0f;

    public static bool TryApply(
        RenderCamera camera,
        RenderCameraNavigationInput input,
        out RenderCamera result)
    {
        result = camera;
        if (!TryCreateBasis(
                camera,
                out float distance,
                out Vector3 offset,
                out Vector3 orbitUp,
                out Vector3 right,
                out Vector3 viewUp) ||
            input.ViewportWidth <= 0 ||
            input.ViewportHeight <= 0 ||
            !float.IsFinite(input.HorizontalDeltaPixels) ||
            !float.IsFinite(input.VerticalDeltaPixels))
        {
            return false;
        }

        result = input.Kind switch
        {
            RenderCameraNavigationKind.Orbit => ApplyOrbit(
                camera,
                input,
                distance,
                offset,
                orbitUp),
            RenderCameraNavigationKind.Pan => ApplyPan(
                camera,
                input,
                distance,
                right,
                viewUp),
            RenderCameraNavigationKind.Zoom => ApplyZoom(
                camera,
                input,
                distance,
                offset),
            _ => camera,
        };
        if (!Enum.IsDefined(input.Kind) ||
            !IsFinite(result.Eye) ||
            !IsFinite(result.Target) ||
            !IsFinite(result.Up))
        {
            result = camera;
            return false;
        }

        return true;
    }

    private static RenderCamera ApplyOrbit(
        RenderCamera camera,
        RenderCameraNavigationInput input,
        float distance,
        Vector3 offset,
        Vector3 orbitUp)
    {
        double rawYawRadians =
            -(double)input.HorizontalDeltaPixels /
            input.ViewportWidth *
            Math.PI;
        float yawRadians = (float)Math.IEEERemainder(
            rawYawRadians,
            Math.Tau);
        Quaternion yaw = Quaternion.CreateFromAxisAngle(
            orbitUp,
            yawRadians);
        Vector3 yawedOffset = Vector3.Transform(offset, yaw);
        Vector3 yawedForward = -Vector3.Normalize(yawedOffset);
        Vector3 right = Vector3.Normalize(
            Vector3.Cross(yawedForward, orbitUp));

        float currentElevation = MathF.Asin(
            Math.Clamp(
                Vector3.Dot(yawedForward, orbitUp),
                -1.0f,
                1.0f));
        float requestedPitch =
            input.VerticalDeltaPixels /
            input.ViewportHeight *
            MathF.PI;
        float targetElevation = Math.Clamp(
            currentElevation + requestedPitch,
            -MaximumOrbitElevationRadians,
            MaximumOrbitElevationRadians);
        Quaternion pitch = Quaternion.CreateFromAxisAngle(
            right,
            targetElevation - currentElevation);
        Vector3 nextOffset = Vector3.Normalize(
            Vector3.Transform(yawedOffset, pitch)) * distance;

        return camera with
        {
            Eye = camera.Target + nextOffset,
            Up = orbitUp,
        };
    }

    private static RenderCamera ApplyPan(
        RenderCamera camera,
        RenderCameraNavigationInput input,
        float distance,
        Vector3 right,
        Vector3 viewUp)
    {
        float fieldOfViewRadians = Math.Clamp(
                camera.VerticalFieldOfViewDegrees,
                1.0f,
                179.0f) *
            (MathF.PI / 180.0f);
        float worldUnitsPerPixel =
            2.0f *
            distance *
            MathF.Tan(fieldOfViewRadians * 0.5f) /
            input.ViewportHeight;
        Vector3 translation =
            (-input.HorizontalDeltaPixels * right +
             input.VerticalDeltaPixels * viewUp) *
            worldUnitsPerPixel;

        return camera with
        {
            Eye = camera.Eye + translation,
            Target = camera.Target + translation,
        };
    }

    private static RenderCamera ApplyZoom(
        RenderCamera camera,
        RenderCameraNavigationInput input,
        float distance,
        Vector3 offset)
    {
        float wheelNotches =
            input.WheelDelta / WheelDeltaPerNotch;
        float exponent = Math.Clamp(
            -wheelNotches * ZoomExponentPerWheelNotch,
            -20.0f,
            20.0f);
        float requestedDistance =
            distance * MathF.Exp(exponent);
        float minimumDistance = MathF.Max(
            MinimumCameraDistance,
            camera.NearPlane * 2.0f);
        float maximumDistance = MathF.Max(
            minimumDistance * 2.0f,
            camera.FarPlane * 0.95f);
        float nextDistance = Math.Clamp(
            requestedDistance,
            minimumDistance,
            maximumDistance);

        return camera with
        {
            Eye = camera.Target +
                Vector3.Normalize(offset) * nextDistance,
        };
    }

    private static bool TryCreateBasis(
        RenderCamera camera,
        out float distance,
        out Vector3 offset,
        out Vector3 orbitUp,
        out Vector3 right,
        out Vector3 viewUp)
    {
        distance = 0.0f;
        offset = default;
        orbitUp = default;
        right = default;
        viewUp = default;
        if (!IsFinite(camera.Eye) ||
            !IsFinite(camera.Target) ||
            !IsFinite(camera.Up) ||
            !float.IsFinite(camera.VerticalFieldOfViewDegrees) ||
            !float.IsFinite(camera.NearPlane) ||
            !float.IsFinite(camera.FarPlane) ||
            camera.VerticalFieldOfViewDegrees <= 0.0f ||
            camera.NearPlane <= 0.0f ||
            camera.FarPlane <= camera.NearPlane)
        {
            return false;
        }

        offset = camera.Eye - camera.Target;
        distance = offset.Length();
        if (!float.IsFinite(distance) ||
            distance < MinimumCameraDistance)
        {
            return false;
        }

        Vector3 forward = -offset / distance;
        orbitUp = camera.Up;
        float upLength = orbitUp.Length();
        if (!float.IsFinite(upLength) ||
            upLength < 1.0e-6f)
        {
            return false;
        }

        orbitUp /= upLength;
        Vector3 rightCandidate = Vector3.Cross(
            forward,
            orbitUp);
        if (rightCandidate.LengthSquared() < 1.0e-8f)
        {
            orbitUp = MathF.Abs(forward.Y) < 0.9f
                ? Vector3.UnitY
                : Vector3.UnitZ;
            rightCandidate = Vector3.Cross(
                forward,
                orbitUp);
        }

        right = Vector3.Normalize(rightCandidate);
        viewUp = Vector3.Normalize(
            Vector3.Cross(right, forward));
        return IsFinite(right) && IsFinite(viewUp);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
