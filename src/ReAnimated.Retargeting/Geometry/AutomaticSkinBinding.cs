using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public enum SkinBindingOrigin { VolumeGeodesic, ExplicitFixed }

/// <summary>A deform influence in authoring coordinates. AllowedRegions restricts automatic propagation; explicit fixed fractions are retained independently.</summary>
public sealed record SkinBindingHandle(Guid Id, Vector3D Start, Vector3D End)
{
    public ImmutableArray<int> AllowedRegions { get; init; } = [];
}

/// <summary>Exact fixed fraction. A zero fraction explicitly excludes that influence from automatic assignment.</summary>
public readonly record struct FixedSkinInfluence(Guid HandleId, double Weight);
public readonly record struct GeneratedSkinInfluence(Guid HandleId, double Weight);

/// <summary>Caller-supplied source point identity. Integration must validate IDs/positions against the immutable source inventory.</summary>
public sealed record SkinBindingPoint(string ComponentId, int ControlPointIndex, Vector3D Position)
{
    public ImmutableArray<FixedSkinInfluence> FixedInfluences { get; init; } = [];
    public ImmutableArray<Guid> AllowedHandles { get; init; } = [];
}

public sealed record AutomaticSkinBindingOptions
{
    public int MaximumInfluences { get; init; } = 4;
    public double DistancePower { get; init; } = 2;
    public double SurfaceSearchRadiusCells { get; init; } = 2;
    public int MaximumGridCells { get; init; } = 1_000_000;
    public int MaximumPoints { get; init; } = 250_000;
    public int MaximumHandles { get; init; } = 128;
    public long MaximumExpandedCells { get; init; } = 16_000_000;
    public long MaximumPointHandlePairs { get; init; } = 32_000_000;
    public long MaximumGridHandlePairs { get; init; } = 64_000_000;
    /// <summary>Optional reviewed region IDs, in the supplied grid's X-fastest cell order. Never inferred from bone names.</summary>
    public ImmutableArray<int> CellRegions { get; init; } = [];
}

public sealed record SkinBindingPointResult(string ComponentId, int ControlPointIndex,
    ImmutableArray<GeneratedSkinInfluence> Influences, VolumeGridCoordinate? SampleCell,
    double? SurfaceSampleDistance, double UnboundFraction, double RemovedWeightBeforeRenormalization);

public sealed record SkinBindingHandleResult(Guid Id, int InteriorSeedCells, int SegmentSamples, int InteriorSegmentSamples, int ReachedCells);
public sealed record SkinBindingDiagnostic(string Code, string Message, string? ComponentId = null, int? ControlPointIndex = null, Guid? HandleId = null);

/// <summary>Candidate bind weights. Assignment coverage is distinct from deformation review and native readiness.</summary>
public sealed record AutomaticSkinBindingResult(string SourceSha256, string? GridFingerprint, string InputFingerprint,
    ImmutableArray<SkinBindingPointResult> Points, ImmutableArray<SkinBindingHandleResult> Handles,
    ImmutableArray<SkinBindingDiagnostic> Diagnostics, long ExpandedCells)
{
    public SkinBindingOrigin Origin { get; init; } = SkinBindingOrigin.VolumeGeodesic;
    public const string BackendId = "cpu-volume-geodesic";
    public const string BackendVersion = "1";
    public bool AllPointsAssigned => !Points.IsDefaultOrEmpty && Points.All(static p =>
        p.UnboundFraction is >= 0 and <= 1e-12 && !p.Influences.IsDefaultOrEmpty &&
        p.Influences.Length <= 8 && p.Influences.Select(static w => w.HandleId).Distinct().Count() == p.Influences.Length &&
        p.Influences.All(static w => w.HandleId != Guid.Empty && double.IsFinite(w.Weight) && w.Weight > 0 && w.Weight <= 1) &&
        Math.Abs(p.Influences.Sum(static w => w.Weight) - 1) <= 1e-12);
    public bool RequiresDeformationReview { get; } = true;
}
