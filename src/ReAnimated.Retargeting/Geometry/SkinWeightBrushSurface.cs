using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public sealed record SkinWeightBrushHit(Vector3D Position, string ComponentId, ImmutableArray<SkinWeightCorrectionSelection> Selection);

/// <summary>A rest-surface brush that follows mesh edges from the nearest visible triangle, never a screen-space depth guess.</summary>
public sealed class SkinWeightBrushSurface
{
    private readonly ImmutableArray<SkinWeightCorrectionPoint> _points;
    private readonly ImmutableArray<Vector3D> _positions;
    private readonly ImmutableArray<(int A, int B, int C)> _triangles;
    private readonly TriangleSpatialIndex _spatial;

    private SkinWeightBrushSurface(ImmutableArray<SkinWeightCorrectionPoint> points, ImmutableArray<Vector3D> positions,
        ImmutableArray<(int A, int B, int C)> triangles, TriangleSpatialIndex spatial)
    { _points = points; _positions = positions; _triangles = triangles; _spatial = spatial; }

    public static SkinWeightBrushSurface Build(IReadOnlyList<SkinWeightCorrectionPoint> points, IReadOnlyList<Vector3D> positions,
        IReadOnlyList<(int A, int B, int C)> triangles, CancellationToken cancellationToken = default)
    {
        if (points.Count != positions.Count || points.Count > 250_000 || triangles.Count > 2_000_000)
            throw new ArgumentException("Brush geometry exceeds its source correspondence budget.");
        var spatial = TriangleSpatialIndex.Build(positions, triangles, cancellationToken);
        foreach (var (a, b, c) in triangles)
            if (points[a].ComponentId != points[b].ComponentId || points[a].ComponentId != points[c].ComponentId)
                throw new ArgumentException("A brush triangle must belong to one source component.", nameof(triangles));
        for (int i = 0; i < points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (points[i].Neighbors.IsDefault || points[i].Neighbors.Any(n => (uint)n >= (uint)points.Count || points[n].ComponentId != points[i].ComponentId))
                throw new ArgumentException("Brush adjacency must remain within observed source components.", nameof(points));
        }
        return new(points.ToImmutableArray(), positions.ToImmutableArray(), triangles.ToImmutableArray(), spatial);
    }

    public SkinWeightBrushHit? Sample(Vector3D rayOrigin, Vector3D rayDirection, string componentId,
        double radius, double strength, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(strength) || strength is < 0 or > 1)
            throw new ArgumentException("Brush radius must be positive and strength must be between zero and one.");
        var hit = _spatial.FindNearestRayHit(rayOrigin, rayDirection, double.PositiveInfinity, cancellationToken);
        if (hit is not { } surfaceHit) return null;
        var triangle = _triangles[surfaceHit.TriangleOrdinal];
        if (_points[triangle.A].ComponentId != componentId) return null;
        var distances = new Dictionary<int, double>();
        var queue = new PriorityQueue<int, (double Distance, int Index)>();
        Enqueue(triangle.A, (_positions[triangle.A] - surfaceHit.Point).Length);
        Enqueue(triangle.B, (_positions[triangle.B] - surfaceHit.Point).Length);
        Enqueue(triangle.C, (_positions[triangle.C] - surfaceHit.Point).Length);
        int edges = 0;
        while (queue.TryDequeue(out int current, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (priority.Distance != distances[current]) continue;
            foreach (int neighbor in _points[current].Neighbors)
            {
                if (++edges > 200_000) throw new InvalidOperationException("Brush sampling exceeded its edge budget. Use a smaller radius.");
                Enqueue(neighbor, priority.Distance + (_positions[neighbor] - _positions[current]).Length);
            }
        }
        return new(surfaceHit.Point, componentId, distances.OrderBy(static p => p.Key).Select(p =>
            new SkinWeightCorrectionSelection(p.Key, strength * Math.Pow(1 - p.Value / radius, 2))).Where(static p => p.Strength > 0).ToImmutableArray());

        void Enqueue(int point, double distance)
        {
            if (distance >= radius || distances.TryGetValue(point, out double prior) && prior <= distance) return;
            if (distances.Count >= 20_000 && !distances.ContainsKey(point)) throw new InvalidOperationException("Brush sampling exceeded its point budget. Use a smaller radius.");
            distances[point] = distance; queue.Enqueue(point, (distance, point));
        }
    }
}
