using System.Collections.Immutable;
using System.IO;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

/// <summary>A ray hit on current neutral render geometry, with render-vertex indexes.</summary>
public sealed record MorphSurfacePickHit(int TriangleIndex, int VertexIndex, Vector3D Position, double Distance);

/// <summary>
/// Reusable two-sided face picker. Triangle and vertex IDs are the exact
/// ordered render-surface IDs used by morph transfer, not original control
/// point IDs from SourceCorners.
/// </summary>
public sealed class MorphSurfacePicking
{
    private const double MinimumNormalSquared = 1e-24;
    private readonly ImmutableArray<Vector3D> _positions;
    private readonly ImmutableArray<(int A, int B, int C)> _triangles;
    private readonly TriangleSpatialIndex _index;

    private MorphSurfacePicking(string surfaceId, ImmutableArray<Vector3D> positions,
        ImmutableArray<(int A, int B, int C)> triangles, TriangleSpatialIndex index)
    {
        SurfaceId = surfaceId;
        _positions = positions;
        _triangles = triangles;
        _index = index;
    }

    public string SurfaceId { get; }
    public int TriangleCount => _triangles.Length;
    public int VertexCount => _positions.Length;

    public static MorphSurfacePicking Build(FbxModelSurface surface, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (string.IsNullOrWhiteSpace(surface.Id) || surface.Vertices.IsDefaultOrEmpty ||
            surface.Vertices.Length > TriangleSpatialIndex.MaximumControlPointCount ||
            surface.Indices.IsDefaultOrEmpty || surface.Indices.Length % 3 != 0 ||
            surface.Indices.Length / 3 > TriangleSpatialIndex.MaximumTriangleCount)
            throw new InvalidDataException("Face picking requires bounded, nonempty current triangle topology.");
        var positions = ImmutableArray.CreateBuilder<Vector3D>(surface.Vertices.Length);
        foreach (FbxModelVertex vertex in surface.Vertices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vector3D position = vertex.Position;
            if (!position.IsFinite || Math.Max(Math.Abs(position.X),
                    Math.Max(Math.Abs(position.Y), Math.Abs(position.Z))) > TriangleSpatialIndex.MaximumAbsoluteCoordinate)
                throw new InvalidDataException("Face picking positions must be finite and within the spatial-index bound.");
            positions.Add(position);
        }
        var triangles = ImmutableArray.CreateBuilder<(int A, int B, int C)>(surface.Indices.Length / 3);
        for (int offset = 0; offset < surface.Indices.Length; offset += 3)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint a = surface.Indices[offset], b = surface.Indices[offset + 1], c = surface.Indices[offset + 2];
            if (a >= (uint)positions.Count || b >= (uint)positions.Count || c >= (uint)positions.Count ||
                a == b || a == c || b == c)
                throw new InvalidDataException("A face-picking triangle has invalid or repeated render-vertex indexes.");
            Vector3D normal = Vector3D.Cross(positions[(int)b] - positions[(int)a],
                positions[(int)c] - positions[(int)a]);
            if (!normal.IsFinite || normal.LengthSquared <= MinimumNormalSquared)
                throw new InvalidDataException("A face-picking triangle is degenerate or outside numeric bounds.");
            triangles.Add(((int)a, (int)b, (int)c));
        }
        ImmutableArray<Vector3D> frozenPositions = positions.ToImmutable();
        ImmutableArray<(int A, int B, int C)> frozenTriangles = triangles.ToImmutable();
        TriangleSpatialIndex index = TriangleSpatialIndex.Build(frozenPositions, frozenTriangles, cancellationToken);
        return new(surface.Id, frozenPositions, frozenTriangles, index);
    }

    public MorphSurfacePickHit? Pick(Vector3D origin, Vector3D direction,
        CancellationToken cancellationToken = default)
    {
        TriangleRayHit? hit = _index.FindNearestRayHit(origin, direction, double.PositiveInfinity, cancellationToken);
        if (hit is not { } nearest) return null;
        (int a, int b, int c) = _triangles[nearest.TriangleOrdinal];
        int vertex = a;
        double distanceSquared = (_positions[a] - nearest.Point).LengthSquared;
        Consider(b);
        Consider(c);
        return new(nearest.TriangleOrdinal, vertex, nearest.Point, nearest.Distance);

        void Consider(int candidate)
        {
            double candidateDistance = (_positions[candidate] - nearest.Point).LengthSquared;
            if (candidateDistance < distanceSquared || candidateDistance == distanceSquared && candidate < vertex)
            {
                vertex = candidate;
                distanceSquared = candidateDistance;
            }
        }
    }
}
