using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public enum ContactOriginMode { ParentPivot, FootprintCenter }
public enum ContactAxisMode { FootprintMajorAxis, ExplicitDirections }

public sealed record ContactFootprintOptions
{
    public Vector3D Up { get; init; } = Vector3D.UnitY;
    public Vector3D Forward { get; init; } = Vector3D.UnitZ;
    public double BottomBandFraction { get; init; } = 0.2;
    public ContactOriginMode Origin { get; init; } = ContactOriginMode.ParentPivot;
    public ContactAxisMode Axes { get; init; } = ContactAxisMode.FootprintMajorAxis;
}

/// <summary>A geometric draft, not a native contact recipe or runtime certification.</summary>
public sealed record ContactFootprintFit(
    TransformMatrix GlobalFrame, TransformMatrix LocalFrame,
    Vector3D BoundsCenter, Vector3D BoundsHalfExtents,
    ImmutableArray<Vector3D> Footprint, double ContactPlaneLocalY,
    int SourcePointCount, int SamplePointCount, double Directionality,
    bool OrientationAmbiguous)
{
    public bool HasVolume => BoundsHalfExtents.X > 0 && BoundsHalfExtents.Y > 0 && BoundsHalfExtents.Z > 0;
}

/// <summary>
/// Fits a proper model-space frame and shoe bounds from selected bind-deformed
/// positions. Its low-vertex convex hull is a sampling footprint; it does not
/// infer a native trace schema. The exact parent inverse retains affine scale.
/// </summary>
public static class ContactFootprintSolver
{
    public static ContactFootprintFit Fit(IReadOnlyList<Vector3D> positions,
        TransformMatrix parentGlobal, ContactFootprintOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(options);
        if (positions.Count is < 3 or > 250_000)
            throw new ArgumentException("Select between three and 250,000 shoe points.", nameof(positions));
        if (!parentGlobal.IsFinite || parentGlobal.LinearDeterminant <= 0)
            throw new ArgumentException("Contact fitting requires a finite, invertible parent without reflection.", nameof(parentGlobal));
        var inverseParent = parentGlobal.InvertedAffine();
        if (!Enum.IsDefined(options.Origin) || !Enum.IsDefined(options.Axes) ||
            !double.IsFinite(options.BottomBandFraction) || options.BottomBandFraction is < 0 or > 1)
            throw new ArgumentException("Choose valid contact modes and a bottom band from zero to one.", nameof(options));
        var up = options.Up.Normalized();
        var forward = (options.Forward - up * Vector3D.Dot(options.Forward, up)).Normalized();
        var right = Vector3D.Cross(up, forward).Normalized();
        // Work near the source to avoid cancellation when the asset is translated far from zero.
        Vector3D anchor = positions[0];
        double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
        foreach (var point in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!point.IsFinite) throw new ArgumentException("Shoe points must be finite.", nameof(positions));
            double height = Vector3D.Dot(point - anchor, up);
            bottom = Math.Min(bottom, height); top = Math.Max(top, height);
        }
        double cutoff = bottom + (top - bottom) * options.BottomBandFraction;
        var samples = new List<Point>();
        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delta = position - anchor;
            if (Vector3D.Dot(delta, up) <= cutoff)
                samples.Add(new(Vector3D.Dot(delta, right), Vector3D.Dot(delta, forward)));
        }
        var hull = Hull(samples, cancellationToken);
        if (hull.Count < 3) throw new ArgumentException("The selected bottom band has no two-dimensional footprint. Increase the band or change the selection.", nameof(positions));
        // Area moments, rather than vertex counts, avoid bias from uneven tessellation.
        double area2 = 0, cx = 0, cz = 0, xx = 0, zz = 0, xz = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Count];
            double cross = a.X * b.Z - b.X * a.Z;
            area2 += cross; cx += (a.X + b.X) * cross; cz += (a.Z + b.Z) * cross;
            xx += (a.X * a.X + a.X * b.X + b.X * b.X) * cross;
            zz += (a.Z * a.Z + a.Z * b.Z + b.Z * b.Z) * cross;
            xz += (2 * a.X * a.Z + a.X * b.Z + b.X * a.Z + 2 * b.X * b.Z) * cross;
        }
        cx /= 3 * area2; cz /= 3 * area2;
        xx = xx / (6 * area2) - cx * cx;
        zz = zz / (6 * area2) - cz * cz;
        xz = xz / (12 * area2) - cx * cz;
        double separation = Math.Sqrt((xx - zz) * (xx - zz) + 4 * xz * xz);
        double major = (xx + zz + separation) * 0.5;
        double directionality = major > 0 ? Math.Clamp(separation / major, 0, 1) : 0;
        if (!double.IsFinite(area2) || area2 <= 0 || !double.IsFinite(directionality) ||
            !double.IsFinite(cx) || !double.IsFinite(cz))
            throw new ArgumentException("Shoe geometry does not produce finite footprint moments.", nameof(positions));
        bool ambiguous = directionality < 0.1;
        var footprint = hull.Select(p => anchor + right * p.X + forward * p.Z + up * bottom).ToImmutableArray();
        var footprintCenter = anchor + right * cx + forward * cz + up * bottom;
        if (options.Axes == ContactAxisMode.FootprintMajorAxis && !ambiguous)
        {
            double angle = 0.5 * Math.Atan2(2 * xz, xx - zz);
            var direction = right * Math.Cos(angle) + forward * Math.Sin(angle);
            // The supplied forward direction resolves the principal axis's sign.
            double alignment = Vector3D.Dot(direction, forward);
            if (Math.Abs(alignment) < 1e-8) ambiguous = true;
            else
            {
                forward = alignment < 0 ? -direction : direction;
                right = Vector3D.Cross(up, forward).Normalized();
            }
        }
        Vector3D origin = options.Origin == ContactOriginMode.ParentPivot ? parentGlobal.Translation : footprintCenter;
        var global = new TransformMatrix(
            right.X, up.X, forward.X, origin.X,
            right.Y, up.Y, forward.Y, origin.Y,
            right.Z, up.Z, forward.Z, origin.Z,
            0, 0, 0, 1);
        var inverse = global.InvertedAffine();
        var minimum = new Vector3D(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var maximum = -minimum;
        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = inverse.TransformPoint(position);
            minimum = new(Math.Min(minimum.X, p.X), Math.Min(minimum.Y, p.Y), Math.Min(minimum.Z, p.Z));
            maximum = new(Math.Max(maximum.X, p.X), Math.Max(maximum.Y, p.Y), Math.Max(maximum.Z, p.Z));
        }
        var center = (minimum + maximum) * 0.5;
        var extents = (maximum - minimum) * 0.5;
        var local = inverseParent * global;
        if (!center.IsFinite || !extents.IsFinite || !local.IsFinite)
            throw new ArgumentException("Shoe bounds or the parent-local frame are not finite.", nameof(positions));
        return new(global, local, center, extents, footprint, inverse.TransformPoint(footprint[0]).Y,
            positions.Count, samples.Count, directionality, options.Axes == ContactAxisMode.FootprintMajorAxis && ambiguous);
    }

    private readonly record struct Point(double X, double Z);
    private static List<Point> Hull(List<Point> points, CancellationToken cancellationToken)
    {
        var sorted = points.Distinct().OrderBy(static p => p.X).ThenBy(static p => p.Z).ToArray();
        var hull = new List<Point>();
        foreach (var point in sorted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        int lower = hull.Count;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (hull.Count > lower && Cross(hull[^2], hull[^1], sorted[i]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(sorted[i]);
        }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        return hull;
    }
    private static double Cross(Point a, Point b, Point c) => (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
}
