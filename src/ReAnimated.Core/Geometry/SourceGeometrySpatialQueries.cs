using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

/// <summary>A surface sample mapped back to immutable source component and polygon/triangle identities.</summary>
public sealed record SourceGeometrySurfaceHit(string ComponentId, SourceGeometryAnalysisTriangle Triangle,
    Vector3D Point, double Distance, Vector3D BarycentricWeights);

/// <summary>
/// Disposable queries over selected source components in normalized authoring metres.
/// Uses original source geometry; does not replace render topology, create a rig or change weights.
/// </summary>
public sealed class SourceGeometrySpatialQueries
{
    private static readonly TriangleSpatialIndex EmptyIndex = TriangleSpatialIndex.Build([], []);
    private readonly ImmutableArray<ComponentIndex> _components;
    private readonly Dictionary<string, ComponentIndex> _byId;

    private SourceGeometrySpatialQueries(string sourceSha256, ImmutableArray<ComponentIndex> components)
    {
        SourceSha256 = sourceSha256;
        _components = components;
        _byId = components.ToDictionary(static c => c.Source.Geometry.Id, StringComparer.Ordinal);
    }

    public string SourceSha256 { get; }
    public int ComponentCount => _components.Length;

    public static SourceGeometrySpatialQueries Build(SourceGeometryAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        if (analysis.Components.IsDefault || analysis.SourceSha256 is not { Length: 64 } ||
            analysis.SourceSha256.Any(static c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Spatial analysis requires a source content hash and component inventory.", nameof(analysis));
        var result = ImmutableArray.CreateBuilder<ComponentIndex>(analysis.Components.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var component in analysis.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component is null || component.Geometry is null || component.Triangles.IsDefault || component.Geometry.ControlPoints.IsDefault ||
                component.Triangles.Length > TriangleSpatialIndex.MaximumTriangleCount ||
                component.Geometry.ControlPoints.Length > TriangleSpatialIndex.MaximumControlPointCount)
                throw new ArgumentException("Spatial analysis contains missing or oversized source geometry.", nameof(analysis));
        }
        foreach (var component in analysis.Components.OrderBy(static c => c.Geometry.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(component.Geometry.Id) || !identities.Add(component.Geometry.Id) ||
                component.Triangles.IsDefault || component.Geometry.ControlPoints.IsDefault)
                throw new ArgumentException("Spatial analysis contains invalid or repeated source components.", nameof(analysis));
            var triangles = new (int A, int B, int C)[component.Triangles.Length];
            var sourceTriangles = new HashSet<GeometrySourceTriangle>();
            for (int i = 0; i < triangles.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var triangle = component.Triangles[i];
                if (triangle.Source.PolygonIndex < 0 || triangle.Source.TriangleInPolygon < 0 || !sourceTriangles.Add(triangle.Source))
                    throw new ArgumentException("Spatial analysis contains invalid or repeated source triangles.", nameof(analysis));
                triangles[i] = (triangle.A, triangle.B, triangle.C);
            }
            result.Add(new(component, TriangleSpatialIndex.Build(component.Geometry.ControlPoints, triangles, cancellationToken)));
        }
        return new(analysis.SourceSha256, result.ToImmutable());
    }

    /// <summary>Optional component restriction isolates a region; an unknown component is an error, not a whole-model fallback.</summary>
    public SourceGeometrySurfaceHit? FindClosestPoint(Vector3D point, double maximumDistance,
        string? componentId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceGeometrySurfaceHit? best = null;
        if (_components.IsEmpty && componentId is null)
            _ = EmptyIndex.FindClosestPoint(point, maximumDistance, cancellationToken);
        foreach (var component in Selected(componentId))
        {
            var hit = component.Index.FindClosestPoint(point, maximumDistance, cancellationToken);
            if (hit is { } value && (best is null || value.Distance < best.Distance))
                best = new(component.Source.Geometry.Id, component.Source.Triangles[value.TriangleOrdinal], value.Point,
                    value.Distance, new(value.BarycentricA, value.BarycentricB, value.BarycentricC));
        }
        return best;
    }

    /// <summary>Two-sided nearest ray hit; distance is in authoring metres, independent of direction magnitude.</summary>
    public SourceGeometrySurfaceHit? FindNearestRayHit(Vector3D origin, Vector3D direction, double maximumDistance,
        string? componentId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceGeometrySurfaceHit? best = null;
        if (_components.IsEmpty && componentId is null)
            _ = EmptyIndex.FindNearestRayHit(origin, direction, maximumDistance, cancellationToken);
        foreach (var component in Selected(componentId))
        {
            var hit = component.Index.FindNearestRayHit(origin, direction, maximumDistance, cancellationToken);
            if (hit is { } value && (best is null || value.Distance < best.Distance))
                best = new(component.Source.Geometry.Id, component.Source.Triangles[value.TriangleOrdinal], value.Point,
                    value.Distance, new(value.BarycentricA, value.BarycentricB, value.BarycentricC));
        }
        return best;
    }

    private ImmutableArray<ComponentIndex> Selected(string? componentId) => componentId is null ? _components :
        _byId.TryGetValue(componentId, out var component) ? [component] :
        throw new ArgumentException("The requested source component is not part of this analysis.", nameof(componentId));

    private sealed record ComponentIndex(SourceGeometryComponentAnalysis Source, TriangleSpatialIndex Index);
}
