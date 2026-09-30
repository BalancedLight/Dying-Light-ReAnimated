using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

/// <summary>Finite camera frame directions and frustum corners for editor review overlays.</summary>
public sealed record CameraReviewFrame
{
    internal CameraReviewFrame(
        TransformMatrix frame,
        CameraLens lens,
        bool invertUp,
        Vector3D position,
        Vector3D forward,
        Vector3D up,
        Vector3D right,
        double? rollDegrees)
    {
        Frame = frame;
        Lens = lens;
        InvertUp = invertUp;
        Position = position;
        Forward = forward;
        Up = up;
        Right = right;
        RollDegrees = rollDegrees;
        NearCorners = CameraReviewGeometry.CornersAtDepth(position, forward, up, right, lens.NearClipMeters, lens);
        FarCorners = CameraReviewGeometry.CornersAtDepth(position, forward, up, right, lens.FarClipMeters, lens);
    }

    public TransformMatrix Frame { get; }
    public CameraLens Lens { get; }
    public bool InvertUp { get; }
    public Vector3D Position { get; }
    public Vector3D Forward { get; }
    public Vector3D Up { get; }
    public Vector3D Right { get; }
    public double? RollDegrees { get; }
    public ImmutableArray<Vector3D> NearCorners { get; }
    public ImmutableArray<Vector3D> FarCorners { get; }

    public ImmutableArray<Vector3D> GetCornersAtDepth(double depth) =>
        CameraReviewGeometry.CornersAtDepth(Position, Forward, Up, Right, depth, Lens);
}

public static class CameraReviewGeometry
{
    public static CameraReviewFrame Create(
        TransformMatrix frame,
        CameraLens lens,
        bool invertUp = true)
    {
        ValidateFrame(frame);
        ValidateLens(lens);

        Vector3D forward = frame.TransformDirection(Vector3D.UnitZ).Normalized();
        Vector3D rawUp = frame.TransformDirection(Vector3D.UnitY);
        if (invertUp) rawUp = -rawUp;
        Vector3D up = Orthogonalize(rawUp, forward);
        if (!up.TryNormalize(out up))
            throw new InvalidDataException("The camera frame has no stable up direction under the selected convention.");

        Vector3D right = Vector3D.Cross(forward, up);
        if (!right.TryNormalize(out right))
            throw new InvalidDataException("The camera frame has no usable right direction.");
        up = Vector3D.Cross(right, forward).Normalized();

        Vector3D projectedWorldUp = Orthogonalize(Vector3D.UnitY, forward);
        double? roll = projectedWorldUp.TryNormalize(out Vector3D worldUp)
            ? Math.Atan2(Vector3D.Dot(Vector3D.Cross(worldUp, up), forward), Vector3D.Dot(worldUp, up)) * 180.0 / Math.PI
            : null;
        if (roll is { } angle && !double.IsFinite(angle))
            throw new InvalidDataException("The camera roll is non-finite.");

        return new CameraReviewFrame(frame, lens, invertUp, frame.Translation, forward, up, right, roll);
    }

    internal static ImmutableArray<Vector3D> CornersAtDepth(
        Vector3D position,
        Vector3D forward,
        Vector3D up,
        Vector3D right,
        double depth,
        CameraLens lens)
    {
        ValidateLens(lens);
        if (!double.IsFinite(depth) || depth <= 0)
            throw new ArgumentOutOfRangeException(nameof(depth), "Frustum depth must be finite and positive.");
        double halfHeight = depth * Math.Tan(lens.VerticalFieldOfViewDegrees * Math.PI / 360.0);
        double halfWidth = halfHeight * lens.AspectRatio;
        if (!double.IsFinite(halfHeight) || !double.IsFinite(halfWidth))
            throw new InvalidDataException("The camera frustum extent is non-finite.");
        Vector3D center = position + (forward * depth);
        var corners = ImmutableArray.Create(
            center + (up * halfHeight) - (right * halfWidth),
            center + (up * halfHeight) + (right * halfWidth),
            center - (up * halfHeight) + (right * halfWidth),
            center - (up * halfHeight) - (right * halfWidth));
        if (corners.Any(static corner => !corner.IsFinite))
            throw new InvalidDataException("The camera frustum corner is non-finite.");
        return corners;
    }

    private static Vector3D Orthogonalize(Vector3D value, Vector3D normal) =>
        value - (normal * Vector3D.Dot(value, normal));

    private static void ValidateFrame(TransformMatrix frame)
    {
        if (!frame.IsFinite || Math.Abs(frame.M41) > 1e-12 || Math.Abs(frame.M42) > 1e-12 ||
            Math.Abs(frame.M43) > 1e-12 || Math.Abs(frame.M44 - 1.0) > 1e-12 ||
            !double.IsFinite(frame.LinearDeterminant) || frame.LinearDeterminant == 0)
            throw new ArgumentException("Camera frames must be finite, affine and nonsingular.", nameof(frame));
    }

    private static void ValidateLens(CameraLens lens)
    {
        if (!double.IsFinite(lens.VerticalFieldOfViewDegrees) || lens.VerticalFieldOfViewDegrees <= 1 || lens.VerticalFieldOfViewDegrees >= 179 ||
            !double.IsFinite(lens.AspectRatio) || lens.AspectRatio <= 0 ||
            !double.IsFinite(lens.NearClipMeters) || lens.NearClipMeters <= 0 ||
            !double.IsFinite(lens.FarClipMeters) || lens.FarClipMeters <= lens.NearClipMeters)
            throw new ArgumentException("Camera lens values are invalid.", nameof(lens));
    }
}
