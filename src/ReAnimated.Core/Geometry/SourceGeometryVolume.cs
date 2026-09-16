using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

[Flags]
public enum SourceVolumeTopologyIssue { None = 0, OpenBoundary = 1, NonManifoldEdge = 2, InconsistentWinding = 4, DegenerateTriangle = 8, ZeroVolume = 16 }
public enum SourceVolumeLocation { Empty, Exterior, Interior, Surface, Unknown }
public enum SourceVolumeFieldKind { Unavailable, SignedDistanceToInputShell, UnionOfSignedShellFields }

public sealed record SourceVolumeShellInfo(string ComponentId, int IslandIndex, int TriangleCount,
    Vector3D Min, Vector3D Max, SourceVolumeTopologyIssue Issues)
{
    /// <summary>Edge closure, orientation and nonzero volume checks; does not certify absence of self-intersections.</summary>
    public bool IsClosedCandidate => Issues == SourceVolumeTopologyIssue.None;
}

public sealed record SourceVolumeBuildOptions
{
    public int MaximumTriangles { get; init; } = 1_000_000;
    public int MaximumControlPoints { get; init; } = 2_000_000;
    public int MaximumShells { get; init; } = 4096;
}

public readonly record struct SourceVolumeSample(SourceVolumeLocation Location, double? SignedField,
    SourceVolumeFieldKind FieldKind, SourceGeometrySurfaceHit? NearestInputSurface, bool HasUnreliableTopology);

/// <summary>
/// Read-only volume queries in normalized authoring metres. Closed source-index islands are filled
/// individually and UNIONED, including nested/overlapping islands. A multi-shell field is the minimum
/// of signed shell fields, not an exact Euclidean distance to a CSG union boundary. Open or unreliable
/// islands never silently become solids. No welding, repair, geometry editing or rig generation occurs.
/// </summary>
public sealed class SourceGeometryVolume
{
    private static readonly Vector3D[] RayDirections = [new(1, .371, .527), new(-.419, 1, .233), new(.317, -.613, 1),
        new(-1, -.271, .719), new(.677, -1, -.349), new(-.239, .811, -1), new(1, .193, -.887)];
    private readonly ImmutableArray<Shell> _shells;
    private readonly ImmutableHashSet<string> _componentIds;
    public string SourceSha256 { get; }
    public string GeometryFingerprint { get; }
    public ImmutableArray<SourceVolumeShellInfo> Shells { get; }
    public bool HasGeometry => !_shells.IsEmpty;
    public bool HasUnreliableTopology => Shells.Any(static s => !s.IsClosedCandidate);
    public Vector3D Min { get; }
    public Vector3D Max { get; }

    private SourceGeometryVolume(string hash, string geometryFingerprint, ImmutableArray<Shell> shells, ImmutableHashSet<string> componentIds)
    {
        SourceSha256 = hash; GeometryFingerprint = geometryFingerprint; _shells = shells; _componentIds = componentIds;
        Shells = shells.Select(static s => s.Info).ToImmutableArray();
        Min = shells.IsEmpty ? Vector3D.Zero : new(shells.Min(static s => s.Info.Min.X), shells.Min(static s => s.Info.Min.Y), shells.Min(static s => s.Info.Min.Z));
        Max = shells.IsEmpty ? Vector3D.Zero : new(shells.Max(static s => s.Info.Max.X), shells.Max(static s => s.Info.Max.Y), shells.Max(static s => s.Info.Max.Z));
    }

    public static SourceGeometryVolume Build(SourceGeometryAnalysis analysis, SourceVolumeBuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        if (options.MaximumTriangles is <= 0 or > TriangleSpatialIndex.MaximumTriangleCount ||
            options.MaximumControlPoints is <= 0 or > TriangleSpatialIndex.MaximumControlPointCount || options.MaximumShells is <= 0 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(options), "Source volume budgets are outside supported limits.");
        if (analysis.SourceSha256 is not { Length: 64 } || analysis.SourceSha256.Any(static c => !char.IsAsciiHexDigit(c)) || analysis.Components.IsDefault)
            throw new ArgumentException("Source volume analysis needs a source hash and component inventory.", nameof(analysis));
        int pointCount = 0, triangleCount = 0;
        if (analysis.Components.Any(static c => c?.Geometry is null)) throw new ArgumentException("Source volume contains a missing component.", nameof(analysis));
        var ids = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var shells = ImmutableArray.CreateBuilder<Shell>();
        foreach (var component in analysis.Components.OrderBy(static c => c.Geometry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component?.Geometry is not { } geometry || string.IsNullOrWhiteSpace(geometry.Id) || !ids.Add(geometry.Id) ||
                geometry.ControlPoints.IsDefault || component.Triangles.IsDefault)
                throw new ArgumentException("Source volume contains missing or repeated components.", nameof(analysis));
            if (geometry.ControlPoints.Length > options.MaximumControlPoints - pointCount || component.Triangles.Length > options.MaximumTriangles - triangleCount)
                throw new ArgumentException("Source volume exceeds its geometry budget.", nameof(analysis));
            pointCount += geometry.ControlPoints.Length; triangleCount += component.Triangles.Length;
            foreach (var point in geometry.ControlPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!point.IsFinite || Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) > TriangleSpatialIndex.MaximumAbsoluteCoordinate)
                    throw new ArgumentException("Source volume contains coordinates outside its supported finite range.", nameof(analysis));
            }
            var triangles = new (int A, int B, int C)[component.Triangles.Length];
            var triangleIds = new HashSet<GeometrySourceTriangle>();
            for (int i = 0; i < triangles.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var triangle = component.Triangles[i];
                if (triangle.Source.PolygonIndex < 0 || triangle.Source.TriangleInPolygon < 0 || !triangleIds.Add(triangle.Source))
                    throw new ArgumentException("Source volume contains invalid or repeated triangle identities.", nameof(analysis));
                triangles[i] = (triangle.A, triangle.B, triangle.C);
            }
            // Recompute rather than trusting topology cached against a different triangle inventory.
            var topology = SourceMeshTopology.Build(geometry.ControlPoints.Length, triangles, cancellationToken);
            if (topology.Islands.Length > options.MaximumShells - shells.Count) throw new ArgumentException("Source volume exceeds its shell budget.", nameof(analysis));
            var triangleIslands = new int[triangles.Length];
            var issues = new SourceVolumeTopologyIssue[topology.Islands.Length];
            for (int i = 0; i < topology.Islands.Length; i++)
                foreach (int triangle in topology.Islands[i].TriangleIndices) triangleIslands[triangle] = i;
            foreach (var edge in topology.Edges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int island = triangleIslands[edge.Incidents[0].TriangleIndex];
                if (edge.Incidents.Length == 1) issues[island] |= SourceVolumeTopologyIssue.OpenBoundary;
                if (edge.Incidents.Length > 2) issues[island] |= SourceVolumeTopologyIssue.NonManifoldEdge;
                if (edge.HasWindingConflict) issues[island] |= SourceVolumeTopologyIssue.InconsistentWinding;
            }
            for (int i = 0; i < topology.Islands.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var island = topology.Islands[i];
                var positions = island.SourceControlPointIds.Select(p => geometry.ControlPoints[p]).ToArray();
                var remap = island.SourceControlPointIds.Select((p, index) => (p, index)).ToDictionary(static p => p.p, static p => p.index);
                var localTriangles = island.TriangleIndices.Select(t => (remap[triangles[t].A], remap[triangles[t].B], remap[triangles[t].C])).ToArray();
                var index = TriangleSpatialIndex.Build(positions, localTriangles, cancellationToken);
                Vector3D min = new(positions.Min(static p => p.X), positions.Min(static p => p.Y), positions.Min(static p => p.Z));
                Vector3D max = new(positions.Max(static p => p.X), positions.Max(static p => p.Y), positions.Max(static p => p.Z));
                double extent = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
                double volume = 0, compensation = 0;
                Vector3D center = (min + max) / 2;
                foreach (var (a, b, c) in localTriangles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (extent <= 0) { issues[i] |= SourceVolumeTopologyIssue.DegenerateTriangle; continue; }
                    var pa = (positions[a] - center) / extent; var pb = (positions[b] - center) / extent; var pc = (positions[c] - center) / extent;
                    if (Vector3D.Cross(pb - pa, pc - pa).LengthSquared <= 1e-24) issues[i] |= SourceVolumeTopologyIssue.DegenerateTriangle;
                    double contribution = Vector3D.Dot(pa, Vector3D.Cross(pb, pc)) / 6 - compensation;
                    double sum = volume + contribution;
                    compensation = (sum - volume) - contribution; volume = sum;
                }
                if (Math.Abs(volume) <= 1e-12) issues[i] |= SourceVolumeTopologyIssue.ZeroVolume;
                shells.Add(new(new(geometry.Id, i, localTriangles.Length, min, max, issues[i]), index,
                    island.TriangleIndices.Select(t => component.Triangles[t]).ToImmutableArray()));
            }
        }
        return new(analysis.SourceSha256, SourceGeometryFingerprint.Compute(analysis, "source-volume-union-v1", cancellationToken), shells.ToImmutable(), ids.ToImmutable());
    }

    public SourceVolumeSample Sample(Vector3D point, string? componentId = null, double surfaceTolerance = 1e-6,
        int maximumRayHits = TriangleSpatialIndex.MaximumRayHits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!point.IsFinite || Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) > TriangleSpatialIndex.MaximumAbsoluteCoordinate)
            throw new ArgumentException("Volume query point is outside the supported finite range.", nameof(point));
        if (!double.IsFinite(surfaceTolerance) || surfaceTolerance < 0) throw new ArgumentOutOfRangeException(nameof(surfaceTolerance));
        if (maximumRayHits is <= 0 or > TriangleSpatialIndex.MaximumRayHits) throw new ArgumentOutOfRangeException(nameof(maximumRayHits));
        if (componentId is not null && !_componentIds.Contains(componentId)) throw new ArgumentException("The requested component is not part of this volume.", nameof(componentId));
        SourceGeometrySurfaceHit? nearest = null;
        bool inside = false, onSurface = false, unknown = false, unreliable = false;
        double minimumField = double.PositiveInfinity;
        int count = 0;
        foreach (var shell in _shells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (componentId is not null && shell.Info.ComponentId != componentId) continue;
            count++;
            var closest = shell.Index.FindClosestPoint(point, double.PositiveInfinity, cancellationToken)!.Value;
            if (nearest is null || closest.Distance < nearest.Distance)
                nearest = new(shell.Info.ComponentId, shell.Triangles[closest.TriangleOrdinal], closest.Point, closest.Distance,
                    new(closest.BarycentricA, closest.BarycentricB, closest.BarycentricC));
            bool surface = closest.Distance <= surfaceTolerance;
            onSurface |= surface;
            if (!shell.Info.IsClosedCandidate) { unreliable = true; unknown = true; continue; }
            bool? shellInside = surface ? false : InsideBounds(point, shell.Info) ? ClassifyParity(shell.Index, point, maximumRayHits, cancellationToken) : false;
            if (shellInside is null) { unknown = true; continue; }
            inside |= shellInside.Value;
            minimumField = Math.Min(minimumField, surface ? 0 : shellInside.Value ? -closest.Distance : closest.Distance);
        }
        var location = count == 0 ? SourceVolumeLocation.Empty : inside ? SourceVolumeLocation.Interior :
            onSurface ? SourceVolumeLocation.Surface : unknown ? SourceVolumeLocation.Unknown : SourceVolumeLocation.Exterior;
        bool hasField = count > 0 && !unknown && double.IsFinite(minimumField);
        return new(location, hasField ? minimumField : null,
            !hasField ? SourceVolumeFieldKind.Unavailable : count == 1 ? SourceVolumeFieldKind.SignedDistanceToInputShell : SourceVolumeFieldKind.UnionOfSignedShellFields,
            nearest, unreliable);
    }

    private static bool InsideBounds(Vector3D point, SourceVolumeShellInfo shell) => point.X >= shell.Min.X && point.X <= shell.Max.X &&
        point.Y >= shell.Min.Y && point.Y <= shell.Max.Y && point.Z >= shell.Min.Z && point.Z <= shell.Max.Z;

    private static bool? ClassifyParity(TriangleSpatialIndex index, Vector3D point, int maximumHits, CancellationToken cancellationToken)
    {
        bool? inside = null;
        int agreed = 0;
        foreach (var direction in RayDirections)
        {
            var hits = index.FindRayHits(point, direction, double.PositiveInfinity, maximumHits, cancellationToken);
            // Shared edges/vertices, grazing intersections and coincident sheets are not counted by guess.
            if (hits.Any(static h => Math.Min(h.BarycentricA, Math.Min(h.BarycentricB, h.BarycentricC)) <= 1e-9) ||
                hits.Zip(hits.Skip(1)).Any(static pair => Math.Abs(pair.First.Distance - pair.Second.Distance) <= 1e-10 * Math.Max(1e-6, Math.Abs(pair.First.Distance))))
                continue;
            bool candidate = hits.Length % 2 == 1;
            if (inside is { } previous && previous != candidate) return null;
            inside = candidate;
            if (++agreed == 3) return inside;
        }
        return null;
    }

    private sealed record Shell(SourceVolumeShellInfo Info, TriangleSpatialIndex Index, ImmutableArray<SourceGeometryAnalysisTriangle> Triangles);
}
