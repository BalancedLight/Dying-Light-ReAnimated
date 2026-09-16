using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public enum AnatomicalDetectionStatus { Proposed, NeedsAssistance }
public enum AnatomicalPlacementMethod { SectionCenter, RegionCenter, BranchBoundary, PathBend, ArcInterpolation, UserGuide, StoredGuide }
public enum AnatomicalRegion : byte { Unassigned, Torso, Head, LeftArm, RightArm, LeftLeg, RightLeg }

public sealed record AnatomicalGuide(string Role, Vector3D Position, bool Locked = true);
public sealed record AnatomicalJointProposal(string Role, Vector3D Position, AnatomicalPlacementMethod Method,
    double EvidenceStrength, bool Locked, VolumeGridCoordinate? SampleCell);
public sealed record AnatomicalDetectionDiagnostic(string Code, string Message, string? Role = null);
public sealed record AnatomicalConnection(string ParentRole, string ChildRole);

/// <summary>The supplied authoring frame, not an inferred native bone-axis contract.</summary>
public sealed record AnatomicalDetectionFrame
{
    public Vector3D Up { get; init; } = Vector3D.UnitY;
    public Vector3D Left { get; init; } = Vector3D.UnitX;
}

public sealed record AnatomicalDetectionOptions
{
    public AnatomicalDetectionFrame Frame { get; init; } = new();
    public ImmutableArray<AnatomicalGuide> Guides { get; init; } = [];
    public double PathClearanceWeight { get; init; } = 4;
    public int MaximumInteriorCells { get; init; } = 500_000;
}

/// <summary>
/// Reviewable anatomical proposals and voxel-region evidence. This result does not emit a skeleton,
/// rebind vertices, approve a native profile, or represent calibrated prediction confidence.
/// </summary>
public sealed record AnatomicalDetectionResult(string SourceSha256, string GridFingerprint, string InputFingerprint,
    string ConfigurationFingerprint, AnatomicalDetectionStatus Status,
    ImmutableArray<AnatomicalJointProposal> Joints, ImmutableArray<AnatomicalConnection> Connections,
    ImmutableArray<AnatomicalRegion> Regions, ImmutableArray<AnatomicalDetectionDiagnostic> Diagnostics,
    double ResolutionMeters)
{
    public bool RequiresReview { get; } = true;
}
