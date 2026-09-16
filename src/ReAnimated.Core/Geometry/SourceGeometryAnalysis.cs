using System.Collections.Immutable;

namespace ReAnimated.Core.Geometry;

public readonly record struct SourceGeometryAnalysisTriangle(GeometrySourceTriangle Source, int A, int B, int C);

/// <summary>Disposable topology analysis in original normalized source coordinates, not an authored rig or binding result.</summary>
public sealed record SourceGeometryComponentAnalysis(GeometrySourceComponent Geometry,
    ImmutableArray<SourceGeometryAnalysisTriangle> Triangles, SourceMeshTopologyResult Topology);

public sealed record SourceGeometryAnalysis(string SourceSha256, ImmutableArray<SourceGeometryComponentAnalysis> Components);
