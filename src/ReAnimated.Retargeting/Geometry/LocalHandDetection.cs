using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public sealed record LocalHandDetectionOptions
{
    public string Side { get; init; } = "left";
    public Vector3D Wrist { get; init; }
    public Vector3D Forward { get; init; } = Vector3D.UnitZ;
    public Vector3D PalmNormalHint { get; init; } = Vector3D.UnitY;
    public int ExpectedDigits { get; init; } = 5;
    public double MinimumBranchFraction { get; init; } = .1;
    public double PathClearanceWeight { get; init; } = 4;
    public int MaximumInteriorCells { get; init; } = 300_000;
    public ImmutableArray<AnatomicalGuide> Guides { get; init; } = [];
}

/// <summary>A branch is not assigned a human digit name unless its ordering is supported.</summary>
public sealed record HandFingerProposal(string BranchId, string? Digit,
    ImmutableArray<AnatomicalJointProposal> Joints, Vector3D CurlPlaneNormal,
    bool RollAmbiguous, double BranchLength, double BranchPersistence);

/// <summary>Local geometry evidence and proposals; never certifies anatomical accuracy or a native rig.</summary>
public sealed record LocalHandDetectionResult(string SourceSha256, string GridFingerprint, string InputFingerprint,
    string Side, AnatomicalDetectionStatus Status, TransformMatrix? PalmFrame,
    ImmutableArray<HandFingerProposal> Fingers, ImmutableArray<AnatomicalJointProposal> PreservedGuides,
    ImmutableArray<AnatomicalDetectionDiagnostic> Diagnostics, double ResolutionMeters, int InteriorCells)
{
    public bool RequiresReview { get; } = true;
}
