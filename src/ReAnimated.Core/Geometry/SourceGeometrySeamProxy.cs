using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

public sealed record SourceGeometrySeamReport(string ComponentId, int PairedBoundaryEdges, int AmbiguousBoundaryGroups,
    int UnpairedBoundaryEdges, int DegenerateTrianglesRetained, ImmutableArray<int> SourceToRepresentative);

/// <summary>
/// Disposable analysis adjacency over exact coincident boundary-edge pairs within each selected component.
/// Only unique, oppositely directed edge pairs are joined. Source vertices, render topology, weights,
/// seams and morphs remain in Source. Analysis keeps the same positions and source triangle identities
/// but references representative source point IDs; it intentionally carries no guessed merged skin weights.
/// This is not an authored-mesh repair and does not close arbitrary holes or ambiguous overlaps.
/// </summary>
public sealed class SourceGeometrySeamProxy
{
    public SourceGeometryAnalysis Source { get; }
    public SourceGeometryAnalysis Analysis { get; }
    public ImmutableArray<SourceGeometrySeamReport> Reports { get; }
    private readonly ImmutableDictionary<string, ImmutableDictionary<GeometrySourceTriangle, SourceGeometryAnalysisTriangle>> _originalTriangles;

    private SourceGeometrySeamProxy(SourceGeometryAnalysis source, SourceGeometryAnalysis analysis, ImmutableArray<SourceGeometrySeamReport> reports)
    {
        Source = source; Analysis = analysis; Reports = reports;
        _originalTriangles = source.Components.ToImmutableDictionary(static c => c.Geometry.Id,
            static c => c.Triangles.ToImmutableDictionary(static t => t.Source), StringComparer.Ordinal);
    }

    public static SourceGeometrySeamProxy Build(SourceGeometryAnalysis source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Components.IsDefault || source.SourceSha256 is not { Length: 64 } || source.SourceSha256.Any(static c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A seam proxy needs source identity and geometry.", nameof(source));
        int totalPoints = 0, totalTriangles = 0;
        var components = ImmutableArray.CreateBuilder<SourceGeometryComponentAnalysis>();
        var reports = ImmutableArray.CreateBuilder<SourceGeometrySeamReport>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in source.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component?.Geometry is not { } geometry || string.IsNullOrWhiteSpace(geometry.Id) || !ids.Add(geometry.Id) ||
                geometry.ControlPoints.IsDefault || component.Triangles.IsDefault)
                throw new ArgumentException("Seam proxy source components are missing or repeated.", nameof(source));
            if (geometry.ControlPoints.Length > 2_000_000 - totalPoints || component.Triangles.Length > 1_000_000 - totalTriangles)
                throw new ArgumentException("Seam proxy geometry exceeds its bounded inventory.", nameof(source));
            totalPoints += geometry.ControlPoints.Length; totalTriangles += component.Triangles.Length;
            foreach (var point in geometry.ControlPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!point.IsFinite) throw new ArgumentException("Seam proxy source positions must be finite.", nameof(source));
            }
            var triangleIds = new HashSet<GeometrySourceTriangle>();
            foreach (var triangle in component.Triangles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (triangle.Source.PolygonIndex < 0 || triangle.Source.TriangleInPolygon < 0 || !triangleIds.Add(triangle.Source))
                    throw new ArgumentException("Seam proxy triangle provenance is invalid or repeated.", nameof(source));
            }
            var original = component.Triangles.Select(static t => (t.A, t.B, t.C)).ToArray();
            var topology = SourceMeshTopology.Build(geometry.ControlPoints.Length, original, cancellationToken);
            if (topology.BoundaryEdges.Length > 2_000_000) throw new ArgumentException("Seam proxy boundary inventory exceeds its budget.", nameof(source));
            var groups = new Dictionary<EdgePosition, List<Boundary>>();
            foreach (var edge in topology.BoundaryEdges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int from = edge.Incidents[0].IsForward ? edge.A : edge.B;
                int to = edge.Incidents[0].IsForward ? edge.B : edge.A;
                Vector3D a = geometry.ControlPoints[from], b = geometry.ControlPoints[to];
                if (a == b) continue;
                bool forward = Compare(a, b) < 0;
                var key = forward ? new EdgePosition(a, b) : new EdgePosition(b, a);
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
                group.Add(new(from, to, forward));
            }
            int[] representatives = Enumerable.Range(0, geometry.ControlPoints.Length).ToArray();
            int paired = 0, ambiguous = 0;
            foreach (var group in groups.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Count == 2 && group[0].Forward != group[1].Forward)
                {
                    Join(group[0].From, group[1].To);
                    Join(group[0].To, group[1].From);
                    paired += 2;
                }
                else if (group.Count > 1) ambiguous++;
            }
            for (int i = 0; i < representatives.Length; i++) { cancellationToken.ThrowIfCancellationRequested(); representatives[i] = Find(i); }
            int degenerates = 0;
            var triangles = component.Triangles.Select(t => {
                cancellationToken.ThrowIfCancellationRequested();
                int a = representatives[t.A], b = representatives[t.B], c = representatives[t.C];
                if (a == b || b == c || c == a) { degenerates++; return t; }
                return t with { A = a, B = b, C = c };
            }).ToImmutableArray();
            var proxyTopology = SourceMeshTopology.Build(geometry.ControlPoints.Length, triangles.Select(static t => (t.A, t.B, t.C)).ToArray(), cancellationToken);
            components.Add(new(geometry with { Skinning = null }, triangles, proxyTopology));
            reports.Add(new(geometry.Id, paired, ambiguous, topology.BoundaryEdges.Length - paired, degenerates, representatives.ToImmutableArray()));

            int Find(int value)
            {
                int root = value;
                while (representatives[root] != root) root = representatives[root];
                while (representatives[value] != value) { int next = representatives[value]; representatives[value] = root; value = next; }
                return root;
            }
            void Join(int a, int b)
            {
                int left = Find(a), right = Find(b);
                if (left != right) representatives[Math.Max(left, right)] = Math.Min(left, right);
            }
        }
        return new(source, new(source.SourceSha256, components.ToImmutable()), reports.ToImmutable());
    }

    /// <summary>Restores original corner identity after a volume/proxy surface query; barycentric positions are unchanged.</summary>
    public SourceGeometrySurfaceHit RemapHit(SourceGeometrySurfaceHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (!_originalTriangles.TryGetValue(hit.ComponentId, out var triangles) || !triangles.TryGetValue(hit.Triangle.Source, out var original))
            throw new ArgumentException("Surface hit does not belong to this proxy's source.", nameof(hit));
        return hit with { Triangle = original };
    }

    private static int Compare(Vector3D a, Vector3D b)
    {
        int x = a.X.CompareTo(b.X);
        if (x != 0) return x;
        int y = a.Y.CompareTo(b.Y);
        return y != 0 ? y : a.Z.CompareTo(b.Z);
    }
    private readonly record struct EdgePosition(Vector3D A, Vector3D B);
    private readonly record struct Boundary(int From, int To, bool Forward);
}
