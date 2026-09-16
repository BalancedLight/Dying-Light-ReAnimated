using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

/// <summary>
/// Aggregates original source skin influences over source geometry. It uses
/// source control-point identity and the caller's current-rig joint map; it
/// never mutates weights or infers anatomy.
/// </summary>
public static class SourceSkinInfluenceRegions
{
    /// <summary>Maximum source control-point count accepted before allocation.</summary>
    public const int MaximumControlPointCount = 4_000_000;

    /// <summary>Maximum source triangle count accepted before analysis.</summary>
    public const int MaximumTriangleCount = 8_000_000;

    /// <summary>Coordinate bound used to keep area and moment arithmetic finite.</summary>
    public const double MaximumAbsoluteCoordinate = 1e75;

    private const double AreaTolerance = 1e-24;

    /// <summary>
    /// Builds immutable original-skin influence regions over all components and
    /// triangles present in <paramref name="analysis"/>. Every source joint,
    /// including discarded entries, must be present in the supplied map.
    /// </summary>
    public static SourceSkinInfluenceRegionsResult Build(
        SourceGeometryAnalysis analysis,
        IReadOnlyDictionary<string, int> boneIndicesBySourceJointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(boneIndicesBySourceJointId);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(analysis.SourceSha256))
        {
            throw new ArgumentException("Source analysis must have a source hash.", nameof(analysis));
        }

        if (analysis.Components.IsDefault || analysis.Components.Length > MaximumControlPointCount)
        {
            throw new ArgumentException("Source analysis has an invalid component inventory.", nameof(analysis));
        }

        foreach (KeyValuePair<string, int> mapping in boneIndicesBySourceJointId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(mapping.Key) || mapping.Value < 0)
            {
                throw new ArgumentException(
                    "Current-rig joint mappings must have non-empty IDs and non-negative bone indexes.",
                    nameof(boneIndicesBySourceJointId));
            }
        }

        var regions = new Dictionary<int, RegionAccumulator>();
        var discarded = new List<SourceSkinDiscardedInfluence>();
        Vector3D usedMinimum = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3D usedMaximum = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        bool hasUsedGeometry = false;
        int totalControlPointCount = 0;
        int totalTriangleCount = 0;
        var componentIds = new HashSet<string>(StringComparer.Ordinal);

        for (int componentOrdinal = 0; componentOrdinal < analysis.Components.Length; componentOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceGeometryComponentAnalysis component = analysis.Components[componentOrdinal]
                ?? throw new InvalidDataException("Source analysis contains a null component.");
            GeometrySourceComponent geometry = component.Geometry
                ?? throw new InvalidDataException("Source analysis contains a component without geometry.");
            ValidateGeometry(geometry, componentOrdinal, cancellationToken);
            if (!componentIds.Add(geometry.Id)) throw new InvalidDataException("Source influence analysis repeats a component identity.");
            if (geometry.ControlPoints.Length > MaximumControlPointCount - totalControlPointCount)
            {
                throw new InvalidDataException("Source analysis exceeds the bounded control-point inventory.");
            }

            totalControlPointCount += geometry.ControlPoints.Length;
            if (component.Triangles.IsDefault ||
                component.Triangles.Length > MaximumTriangleCount - totalTriangleCount)
            {
                throw new InvalidDataException("Source analysis exceeds the bounded triangle inventory.");
            }

            totalTriangleCount += component.Triangles.Length;
            var usedControlPoints = new HashSet<int>();
            var triangleIds = new HashSet<GeometrySourceTriangle>();
            for (int triangleOffset = 0; triangleOffset < component.Triangles.Length; triangleOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceGeometryAnalysisTriangle triangle = component.Triangles[triangleOffset];
                ValidateTriangle(triangle, geometry.ControlPoints.Length, componentOrdinal, triangleOffset);
                if (triangle.Source.PolygonIndex < 0 || triangle.Source.TriangleInPolygon < 0 || !triangleIds.Add(triangle.Source))
                    throw new InvalidDataException("Source influence analysis contains invalid or repeated triangle identities.");
                usedControlPoints.Add(triangle.A);
                usedControlPoints.Add(triangle.B);
                usedControlPoints.Add(triangle.C);
            }

            int[] sortedUsedControlPoints = usedControlPoints.Order().ToArray();
            foreach (int controlPoint in sortedUsedControlPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateBounds(
                    ref usedMinimum,
                    ref usedMaximum,
                    geometry.ControlPoints[controlPoint]);
                hasUsedGeometry = true;
            }

            Dictionary<int, ImmutableArray<NormalizedInfluence>> normalized =
                BuildNormalizedInfluences(
                    geometry,
                    componentOrdinal,
                    boneIndicesBySourceJointId,
                    discarded,
                    cancellationToken);

            foreach (int controlPoint in sortedUsedControlPoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!normalized.TryGetValue(controlPoint, out ImmutableArray<NormalizedInfluence> influences))
                {
                    continue;
                }

                SourcePointKey sourcePoint = new(componentOrdinal, controlPoint);
                Vector3D position = geometry.ControlPoints[controlPoint];
                foreach (NormalizedInfluence influence in influences)
                {
                    RegionAccumulator region = GetRegion(regions, influence.BoneIndex);
                    region.AddControlPoint(sourcePoint, position, influence.NormalizedWeight);
                }
            }

            foreach (SourceGeometryAnalysisTriangle triangle in component.Triangles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double area = TriangleArea(
                    geometry.ControlPoints[triangle.A],
                    geometry.ControlPoints[triangle.B],
                    geometry.ControlPoints[triangle.C]);
                if (area == 0.0)
                {
                    continue;
                }

                double perCornerArea = area / 3.0;
                AddSurfaceContributions(
                    normalized,
                    regions,
                    componentOrdinal,
                    triangle.A,
                    geometry.ControlPoints[triangle.A],
                    perCornerArea,
                    cancellationToken);
                AddSurfaceContributions(
                    normalized,
                    regions,
                    componentOrdinal,
                    triangle.B,
                    geometry.ControlPoints[triangle.B],
                    perCornerArea,
                    cancellationToken);
                AddSurfaceContributions(
                    normalized,
                    regions,
                    componentOrdinal,
                    triangle.C,
                    geometry.ControlPoints[triangle.C],
                    perCornerArea,
                    cancellationToken);
            }
        }

        var immutableRegions = ImmutableSortedDictionary.CreateBuilder<int, SourceSkinInfluenceRegion>();
        foreach (KeyValuePair<int, RegionAccumulator> pair in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            immutableRegions.Add(pair.Key, pair.Value.ToImmutable());
        }

        return new SourceSkinInfluenceRegionsResult(
            analysis.SourceSha256,
            hasUsedGeometry,
            hasUsedGeometry ? usedMinimum : Vector3D.Zero,
            hasUsedGeometry ? usedMaximum : Vector3D.Zero,
            immutableRegions.ToImmutable(),
            ImmutableArray.CreateRange(discarded));
    }

    private static void ValidateGeometry(
        GeometrySourceComponent geometry,
        int componentOrdinal,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(geometry.Id) ||
            geometry.ControlPoints.IsDefault ||
            geometry.ControlPoints.Length > MaximumControlPointCount)
        {
            throw new InvalidDataException($"Source component {componentOrdinal} has an invalid control-point inventory.");
        }

        for (int index = 0; index < geometry.ControlPoints.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vector3D point = geometry.ControlPoints[index];
            if (!IsSupportedCoordinate(point))
            {
                throw new InvalidDataException(
                    $"Source component {componentOrdinal} control point {index} is outside the finite supported coordinate range.");
            }
        }
    }

    private static void ValidateTriangle(
        SourceGeometryAnalysisTriangle triangle,
        int controlPointCount,
        int componentOrdinal,
        int triangleOffset)
    {
        if ((uint)triangle.A >= (uint)controlPointCount ||
            (uint)triangle.B >= (uint)controlPointCount ||
            (uint)triangle.C >= (uint)controlPointCount)
        {
            throw new InvalidDataException(
                $"Source component {componentOrdinal} triangle {triangleOffset} has an out-of-range control-point index.");
        }
    }

    private static Dictionary<int, ImmutableArray<NormalizedInfluence>> BuildNormalizedInfluences(
        GeometrySourceComponent geometry,
        int componentOrdinal,
        IReadOnlyDictionary<string, int> boneIndicesBySourceJointId,
        List<SourceSkinDiscardedInfluence> discarded,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ImmutableArray<NormalizedInfluence>>();
        GeometrySourceSkinning? skinning = geometry.Skinning;
        if (skinning is null)
        {
            return result;
        }
        skinning.Validate(geometry.ControlPoints.Length, cancellationToken);

        if (skinning.ControlPoints.IsDefault || skinning.ControlPoints.Length != geometry.ControlPoints.Length)
        {
            throw new InvalidDataException(
                $"Source component {componentOrdinal} skinning does not cover its control points.");
        }

        for (int controlPoint = 0; controlPoint < skinning.ControlPoints.Length; controlPoint++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GeometrySourceControlPointWeights? row = skinning.ControlPoints[controlPoint];
            if (row is null || row.Influences.IsDefault)
            {
                throw new InvalidDataException(
                    $"Source component {componentOrdinal} control point {controlPoint} has invalid skinning evidence.");
            }

            double total = 0.0;
            double retainedTotal = 0.0;
            double discardedTotal = 0.0;
            foreach (GeometrySourceInfluence influence in row.Influences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateInfluence(influence, componentOrdinal, controlPoint);
                total += influence.Weight;
                if (influence.Retained)
                {
                    retainedTotal += influence.Weight;
                }
                else
                {
                    discardedTotal += influence.Weight;
                }
            }

            if (!double.IsFinite(total) || total < 0.0 ||
                !double.IsFinite(row.RetainedWeight) || row.RetainedWeight < 0.0 ||
                !double.IsFinite(row.DiscardedWeight) || row.DiscardedWeight < 0.0 ||
                !Matches(retainedTotal, row.RetainedWeight) ||
                !Matches(discardedTotal, row.DiscardedWeight))
            {
                throw new InvalidDataException(
                    $"Source component {componentOrdinal} control point {controlPoint} has inconsistent original skin totals.");
            }

            if (skinning.HasSkinDeformer && total <= 0.0)
            {
                throw new InvalidDataException(
                    $"Source component {componentOrdinal} control point {controlPoint} has no positive original skin weight.");
            }

            if (row.Influences.IsEmpty)
            {
                if (skinning.HasSkinDeformer)
                {
                    throw new InvalidDataException(
                        $"Source component {componentOrdinal} has an empty skinned control-point row.");
                }

                continue;
            }

            if (!skinning.HasSkinDeformer)
            {
                throw new InvalidDataException(
                    $"Source component {componentOrdinal} has skin influences without a skin deformer.");
            }

            var normalized = new List<NormalizedInfluence>(row.Influences.Length);
            foreach (GeometrySourceInfluence influence in row.Influences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!boneIndicesBySourceJointId.TryGetValue(influence.JointId, out int boneIndex))
                {
                    throw new KeyNotFoundException(
                        $"Source joint '{influence.JointId}' has no current-rig bone mapping.");
                }

                if (boneIndex < 0)
                {
                    throw new ArgumentException(
                        $"Current-rig bone mapping for source joint '{influence.JointId}' is negative.",
                        nameof(boneIndicesBySourceJointId));
                }

                double normalizedWeight = influence.Weight / total;
                normalized.Add(new NormalizedInfluence(boneIndex, normalizedWeight));
                if (!influence.Retained)
                {
                    discarded.Add(new SourceSkinDiscardedInfluence(
                        geometry.Id,
                        controlPoint,
                        influence.JointId,
                        boneIndex,
                        influence.SourceEntryIndex,
                        influence.Weight,
                        normalizedWeight));
                }
            }

            result.Add(controlPoint, ImmutableArray.CreateRange(normalized));
        }

        return result;
    }

    private static void ValidateInfluence(
        GeometrySourceInfluence influence,
        int componentOrdinal,
        int controlPoint)
    {
        if (string.IsNullOrWhiteSpace(influence.JointId) ||
            influence.SourceEntryIndex < 0 ||
            influence.ImportedBoneIndex < 0 ||
            !double.IsFinite(influence.Weight) ||
            influence.Weight <= 0.0)
        {
            throw new InvalidDataException(
                $"Source component {componentOrdinal} control point {controlPoint} has invalid skin influence evidence.");
        }
    }

    private static void AddSurfaceContributions(
        Dictionary<int, ImmutableArray<NormalizedInfluence>> normalized,
        Dictionary<int, RegionAccumulator> regions,
        int componentOrdinal,
        int controlPoint,
        Vector3D position,
        double area,
        CancellationToken cancellationToken)
    {
        if (!normalized.TryGetValue(controlPoint, out ImmutableArray<NormalizedInfluence> influences))
        {
            return;
        }

        foreach (NormalizedInfluence influence in influences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetRegion(regions, influence.BoneIndex).AddSurface(
                area * influence.NormalizedWeight,
                position);
        }
    }

    private static RegionAccumulator GetRegion(
        Dictionary<int, RegionAccumulator> regions,
        int boneIndex)
    {
        if (!regions.TryGetValue(boneIndex, out RegionAccumulator? region))
        {
            region = new RegionAccumulator(boneIndex);
            regions.Add(boneIndex, region);
        }

        return region;
    }

    private static double TriangleArea(Vector3D a, Vector3D b, Vector3D c)
    {
        Vector3D ab = b - a;
        Vector3D ac = c - a;
        double scale = MaximumAbsComponent(ab, ac);
        if (scale == 0.0)
        {
            return 0.0;
        }

        Vector3D scaledAb = ab / scale;
        Vector3D scaledAc = ac / scale;
        double normalizedCrossLength = Vector3D.Cross(scaledAb, scaledAc).Length;
        if (!double.IsFinite(normalizedCrossLength) || normalizedCrossLength <= AreaTolerance)
        {
            return 0.0;
        }

        double area = 0.5 * normalizedCrossLength * scale * scale;
        if (!double.IsFinite(area))
        {
            throw new InvalidDataException("Source triangle area is outside the finite supported range.");
        }

        return area;
    }

    private static void UpdateBounds(ref Vector3D minimum, ref Vector3D maximum, Vector3D point)
    {
        minimum = new Vector3D(
            Math.Min(minimum.X, point.X),
            Math.Min(minimum.Y, point.Y),
            Math.Min(minimum.Z, point.Z));
        maximum = new Vector3D(
            Math.Max(maximum.X, point.X),
            Math.Max(maximum.Y, point.Y),
            Math.Max(maximum.Z, point.Z));
    }

    private static double MaximumAbsComponent(Vector3D first, Vector3D second)
    {
        return Math.Max(
            Math.Max(Math.Abs(first.X), Math.Max(Math.Abs(first.Y), Math.Abs(first.Z))),
            Math.Max(Math.Abs(second.X), Math.Max(Math.Abs(second.Y), Math.Abs(second.Z))));
    }

    private static bool IsSupportedCoordinate(Vector3D point) =>
        point.IsFinite &&
        Math.Abs(point.X) <= MaximumAbsoluteCoordinate &&
        Math.Abs(point.Y) <= MaximumAbsoluteCoordinate &&
        Math.Abs(point.Z) <= MaximumAbsoluteCoordinate;

    private static bool Matches(double left, double right) =>
        left == right || Math.Abs(left - right) <= 1e-12 * Math.Max(Math.Abs(left), Math.Abs(right));

    private readonly record struct SourcePointKey(int ComponentOrdinal, int ControlPointIndex);

    private readonly record struct NormalizedInfluence(int BoneIndex, double NormalizedWeight);

    private sealed class RegionAccumulator
    {
        private readonly HashSet<SourcePointKey> _sourcePoints = [];
        private Vector3D _minimum = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        private Vector3D _maximum = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        private double _originalMass;
        private double _surfaceMass;
        private Vector3D _surfaceMoment;

        public RegionAccumulator(int boneIndex) => BoneIndex = boneIndex;

        public int BoneIndex { get; }

        public void AddControlPoint(SourcePointKey sourcePoint, Vector3D position, double normalizedWeight)
        {
            _originalMass += normalizedWeight;
            if (!_sourcePoints.Add(sourcePoint))
            {
                return;
            }

            _minimum = new Vector3D(
                Math.Min(_minimum.X, position.X),
                Math.Min(_minimum.Y, position.Y),
                Math.Min(_minimum.Z, position.Z));
            _maximum = new Vector3D(
                Math.Max(_maximum.X, position.X),
                Math.Max(_maximum.Y, position.Y),
                Math.Max(_maximum.Z, position.Z));
        }

        public void AddSurface(double contribution, Vector3D position)
        {
            if (!double.IsFinite(contribution) || contribution <= 0.0)
            {
                return;
            }

            _surfaceMass += contribution;
            _surfaceMoment += position * contribution;
        }

        public SourceSkinInfluenceRegion ToImmutable()
        {
            bool hasBounds = _sourcePoints.Count > 0;
            bool hasSurfaceSupport = _surfaceMass > 0.0 && double.IsFinite(_surfaceMass);
            Vector3D centroid = hasSurfaceSupport
                ? _surfaceMoment / _surfaceMass
                : Vector3D.Zero;
            return new SourceSkinInfluenceRegion(
                BoneIndex,
                _sourcePoints.Count,
                _originalMass,
                _surfaceMass,
                hasSurfaceSupport,
                centroid,
                hasBounds ? _minimum : Vector3D.Zero,
                hasBounds ? _maximum : Vector3D.Zero);
        }
    }
}

/// <summary>Immutable aggregate of original source skin influence regions.</summary>
public sealed class SourceSkinInfluenceRegionsResult
{
    internal SourceSkinInfluenceRegionsResult(
        string sourceSha256,
        bool hasUsedGeometry,
        Vector3D usedGeometryMin,
        Vector3D usedGeometryMax,
        ImmutableSortedDictionary<int, SourceSkinInfluenceRegion> regions,
        ImmutableArray<SourceSkinDiscardedInfluence> discardedInfluences)
    {
        SourceSha256 = sourceSha256;
        HasUsedGeometry = hasUsedGeometry;
        UsedGeometryMin = usedGeometryMin;
        UsedGeometryMax = usedGeometryMax;
        Regions = regions;
        DiscardedInfluences = discardedInfluences;
    }

    public string SourceSha256 { get; }

    public bool HasUsedGeometry { get; }

    public Vector3D UsedGeometryMin { get; }

    public Vector3D UsedGeometryMax { get; }

    public ImmutableSortedDictionary<int, SourceSkinInfluenceRegion> Regions { get; }

    public ImmutableArray<SourceSkinDiscardedInfluence> DiscardedInfluences { get; }
}

/// <summary>Immutable aggregate for one current-rig bone's source region.</summary>
public sealed class SourceSkinInfluenceRegion
{
    internal SourceSkinInfluenceRegion(
        int boneIndex,
        int sourceControlPointCount,
        double originalNormalizedInfluenceMass,
        double surfaceAreaWeightedInfluenceMass,
        bool hasSurfaceSupport,
        Vector3D centroid,
        Vector3D minimum,
        Vector3D maximum)
    {
        BoneIndex = boneIndex;
        SourceControlPointCount = sourceControlPointCount;
        OriginalNormalizedInfluenceMass = originalNormalizedInfluenceMass;
        SurfaceAreaWeightedInfluenceMass = surfaceAreaWeightedInfluenceMass;
        HasSurfaceSupport = hasSurfaceSupport;
        Centroid = centroid;
        Min = minimum;
        Max = maximum;
    }

    public int BoneIndex { get; }

    public int SourceControlPointCount { get; }

    public double OriginalNormalizedInfluenceMass { get; }

    public double SurfaceAreaWeightedInfluenceMass { get; }

    /// <summary>Alias that states the normalized source-weight basis explicitly.</summary>
    public double SurfaceAreaWeightedNormalizedInfluenceMass => SurfaceAreaWeightedInfluenceMass;

    /// <summary>Alias for <see cref="OriginalNormalizedInfluenceMass"/>.</summary>
    public double OriginalNormalizedWeightMass => OriginalNormalizedInfluenceMass;

    public bool HasSurfaceSupport { get; }

    public Vector3D Centroid { get; }

    public Vector3D Min { get; }

    public Vector3D Max { get; }
}

/// <summary>Evidence for one discarded original source influence.</summary>
public readonly record struct SourceSkinDiscardedInfluence(
    string ComponentId,
    int SourceControlPointIndex,
    string JointId,
    int BoneIndex,
    int SourceEntryIndex,
    double OriginalWeight,
    double NormalizedWeight);
