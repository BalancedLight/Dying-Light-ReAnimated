using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Geometry;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Adapts preserved FBX source identities into generic topology analysis without changing render partitions.</summary>
public static class FbxSourceGeometryAnalysis
{
    public static SourceGeometryAnalysis Build(FbxModelAuthoringImportResult model, bool anatomyOnly = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Package.Document.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (model.Package.Document.RiggingSession is { } session &&
            !string.Equals(session.SourceSha256, model.Package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Geometry analysis cannot use component decisions from another source revision. Reconcile the source first.");
        var selections = model.Package.Document.RiggingSession?.Components.ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var declared = model.Package.Document.Meshes.ToDictionary(m => string.Create(CultureInfo.InvariantCulture,
            $"fbx:{m.ModelObjectId}:{m.GeometryObjectId}"), StringComparer.Ordinal);
        var processed = new HashSet<string>(StringComparer.Ordinal);
        if (model.Surfaces.Any(static surface => surface.SourceGeometry is null))
            throw new InvalidDataException("Source geometry provenance is unavailable. Reimport the source before geometry analysis.");
        var result = ImmutableArray.CreateBuilder<SourceGeometryComponentAnalysis>();
        foreach (var group in model.Surfaces.GroupBy(static s => s.SourceGeometry!.Id).OrderBy(static g => g.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selections is not null)
            {
                if (!selections.TryGetValue(group.Key, out var selection))
                    throw new InvalidDataException("Source geometry component selection must be reconciled before analysis.");
                if (!selection.Included || anatomyOnly && !selection.UseForAnatomy) continue;
            }
            GeometrySourceComponent geometry = group.First().SourceGeometry!;
            if (!declared.TryGetValue(group.Key, out var sourceMesh)) throw new InvalidDataException("Source geometry has no declared component identity.");
            if (string.IsNullOrWhiteSpace(geometry.Id) || geometry.ControlPoints.IsDefault || geometry.ControlPoints.Any(static p => !p.IsFinite))
                throw new InvalidDataException("Source geometry has an invalid identity or control-point inventory.");
            if (geometry.ControlPoints.Length != sourceMesh.ControlPointCount) throw new InvalidDataException("Source control-point inventory is incomplete.");
            if (geometry.Coordinates is not { } coordinates || !double.IsFinite(coordinates.MetersPerSourceUnit) ||
                coordinates.MetersPerSourceUnit <= 0 || !coordinates.SourceToAuthoring.IsFinite)
                throw new InvalidDataException("Source geometry coordinate provenance is missing or invalid.");
            if (geometry.Skinning is null) throw new InvalidDataException("Source skin provenance is unavailable. Reimport the source before geometry analysis.");
            geometry.Skinning.Validate(geometry.ControlPoints.Length, cancellationToken);
            var triangles = new Dictionary<GeometrySourceTriangle, SourceGeometryAnalysisTriangle>();
            foreach (FbxModelSurface surface in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(geometry, surface.SourceGeometry) && !geometry.ControlPoints.SequenceEqual(surface.SourceGeometry!.ControlPoints))
                    throw new InvalidDataException("Render partitions disagree about their source control points.");
                if (geometry.Coordinates != surface.SourceGeometry!.Coordinates)
                    throw new InvalidDataException("Render partitions disagree about their source coordinate conversion.");
                if (!SameSkinning(geometry.Skinning, surface.SourceGeometry!.Skinning, cancellationToken))
                    throw new InvalidDataException("Render partitions disagree about their original source weights.");
                if (surface.SourceCorners.IsDefault || surface.SourceTriangles.IsDefault || surface.SourceCorners.Length != surface.Vertices.Length ||
                    (long)surface.SourceTriangles.Length * 3 != surface.Indices.Length)
                    throw new InvalidDataException("Render partition provenance does not cover its vertices and triangles.");
                for (int i = 0; i < surface.SourceTriangles.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    GeometrySourceTriangle identity = surface.SourceTriangles[i];
                    if (identity.PolygonIndex < 0 || identity.TriangleInPolygon < 0)
                        throw new InvalidDataException("Source triangle identity is invalid.");
                    int a = Point(i * 3), b = Point(i * 3 + 1), c = Point(i * 3 + 2);
                    if (!triangles.TryAdd(identity, new(identity, a, b, c)))
                        throw new InvalidDataException("A source triangle appears more than once across render partitions.");
                }
                int Point(int index)
                {
                    uint vertex = surface.Indices[index];
                    if (vertex >= surface.SourceCorners.Length) throw new InvalidDataException("A render triangle has an invalid vertex index.");
                    GeometrySourceCorner corner = surface.SourceCorners[(int)vertex];
                    if ((uint)corner.ControlPointIndex >= (uint)geometry.ControlPoints.Length || corner.PolygonVertexIndex < 0)
                        throw new InvalidDataException("A render corner has an invalid source identity.");
                    return corner.ControlPointIndex;
                }
            }
            ImmutableArray<SourceGeometryAnalysisTriangle> ordered = triangles.Values.OrderBy(static t => t.Source.PolygonIndex)
                .ThenBy(static t => t.Source.TriangleInPolygon).ToImmutableArray();
            if (ordered.Length != sourceMesh.TriangleCount) throw new InvalidDataException("Source triangles are missing from the render partitions.");
            SourceMeshTopologyResult topology = SourceMeshTopology.Build(geometry.ControlPoints.Length,
                ordered.Select(static t => (t.A, t.B, t.C)).ToArray(), cancellationToken);
            result.Add(new(geometry, ordered, topology));
            processed.Add(group.Key);
        }
        foreach (var pair in declared.Where(static p => p.Value.TriangleCount > 0))
        {
            if (selections is not null && selections.TryGetValue(pair.Key, out var selection) &&
                (!selection.Included || anatomyOnly && !selection.UseForAnatomy)) continue;
            if (!processed.Contains(pair.Key)) throw new InvalidDataException("A selected source component has no geometry to analyze.");
        }
        return new(model.Package.Document.Source.ContentSha256, result.ToImmutable());
    }

    private static bool SameSkinning(GeometrySourceSkinning first, GeometrySourceSkinning? second, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(first, second)) return true;
        if (second is null || second.ControlPoints.IsDefault || first.HasSkinDeformer != second.HasSkinDeformer ||
            first.ControlPoints.Length != second.ControlPoints.Length) return false;
        for (int i = 0; i < first.ControlPoints.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = first.ControlPoints[i];
            var right = second.ControlPoints[i];
            if (right is null || right.Influences.IsDefault || left.RetainedWeight != right.RetainedWeight ||
                left.DiscardedWeight != right.DiscardedWeight || !left.Influences.SequenceEqual(right.Influences)) return false;
        }
        return true;
    }
}
