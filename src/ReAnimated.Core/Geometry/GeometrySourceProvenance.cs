using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

/// <summary>
/// Source-artifact-local component identity and normalized authoring-space control points.
/// Identity must be scoped by the owning source artifact hash when comparing assets.
/// </summary>
public sealed record GeometrySourceComponent(string Id, ImmutableArray<Vector3D> ControlPoints)
{
    /// <summary>Original source weights and import-time reduction decisions, not the current edited binding.</summary>
    public GeometrySourceSkinning? Skinning { get; init; }

    /// <summary>Conversion already applied to ControlPoints. Do not apply it a second time for analysis.</summary>
    public GeometrySourceCoordinates? Coordinates { get; init; }
}

/// <summary>Original component-local source units/axes and mesh placement to normalized authoring metres.</summary>
public sealed record GeometrySourceCoordinates(double MetersPerSourceUnit, TransformMatrix SourceToAuthoring);

/// <summary>A render corner's original source control point and polygon-vertex identity.</summary>
public readonly record struct GeometrySourceCorner(int ControlPointIndex, int PolygonVertexIndex);

/// <summary>Triangle identity before material and palette partitioning.</summary>
public readonly record struct GeometrySourceTriangle(int PolygonIndex, int TriangleInPolygon);
