using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum RigHandSide { Left, Right }
public enum RigFingerPresence { Unresolved, Present, Absent, Fused }

/// <summary>A persisted semantic digit declaration. It records review state and source evidence only.</summary>
public sealed record RigFingerDeclaration
{
    public string Id { get; init; } = string.Empty;
    public RigFingerPresence Presence { get; init; }
    public ImmutableArray<Guid> JointGuideIds { get; init; } = [];
    public Vector3D? CurlPlaneNormal { get; init; }
    public double RollDegrees { get; init; }
    public bool UserApproved { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public void Validate() => Validate(RigHandSide.Left, null);

    internal void Validate(RigHandSide side, IReadOnlyDictionary<Guid, RigLandmark>? guides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        if (Id != Id.Trim() || Id.Contains('.') || Id.Any(char.IsWhiteSpace))
            throw new ArgumentException("Finger identifiers must be semantic dot-free tokens.");
        if (!Enum.IsDefined(Presence)) throw new ArgumentOutOfRangeException(null, "Finger presence must be a defined value.");
        RigContractRules.Array(JointGuideIds, nameof(JointGuideIds));
        if (JointGuideIds.Length > 5 || JointGuideIds.Distinct().Count() != JointGuideIds.Length || JointGuideIds.Any(static id => id == Guid.Empty))
            throw new ArgumentException("Finger guide identities must be unique, non-empty and contain at most five joints.");
        if (CurlPlaneNormal is { } normal && (!normal.IsFinite || !double.IsFinite(normal.LengthSquared) || normal.LengthSquared == 0))
            throw new ArgumentException("A finger curl-plane normal must be finite and nonzero.");
        if (!double.IsFinite(RollDegrees)) throw new ArgumentOutOfRangeException(null, "Finger roll must be finite.");
        RigRecipeRules.Evidence(Evidence);
        if (Presence is RigFingerPresence.Absent or RigFingerPresence.Fused && !JointGuideIds.IsEmpty)
            throw new ArgumentException("Absent or fused digits cannot retain fictitious joint guides.");
        if (Presence == RigFingerPresence.Present && JointGuideIds.Length is < 2 or > 5)
            throw new ArgumentException("A present digit requires between two and five joint guides.");
        if (guides is null || JointGuideIds.IsEmpty) return;
        for (int index = 0; index < JointGuideIds.Length; index++)
        {
            if (!guides.TryGetValue(JointGuideIds[index], out RigLandmark? guide))
                throw new ArgumentException("A finger declaration references a missing guide.");
            string prefix = $"finger.{side.ToString().ToLowerInvariant()}.{Id}.";
            if (!guide.RoleId.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(guide.RoleId[prefix.Length..], out int segment) || segment != index + 1 || segment is < 1 or > 5)
                throw new ArgumentException("Finger guides must use contiguous finger.<side>.<digit>.<segment> roles.");
        }
    }
}

/// <summary>Persisted hand setup for later hand/finger rig extension.</summary>
public sealed record RigHandSetup
{
    public RigHandSide Side { get; init; }
    public Guid WristGuideId { get; init; }
    public TransformMatrix PalmFrame { get; init; } = TransformMatrix.Identity;
    public bool UserApproved { get; init; }
    public ImmutableArray<RigFingerDeclaration> Fingers { get; init; } = [];
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public void Validate() => Validate(null);

    internal void Validate(IReadOnlyDictionary<Guid, RigLandmark>? guides)
    {
        if (!Enum.IsDefined(Side)) throw new ArgumentOutOfRangeException(null, "Hand side must be a defined value.");
        if (WristGuideId == Guid.Empty) throw new ArgumentException("A hand setup requires a wrist guide.");
        RigRecipeRules.Affine(PalmFrame, nameof(PalmFrame));
        if (PalmFrame.LinearDeterminant <= 0) throw new ArgumentException("A palm frame must have positive orientation and nonsingular scale.");
        RigContractRules.Array(Fingers, nameof(Fingers));
        RigRecipeRules.Evidence(Evidence);
        var digitIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (RigFingerDeclaration finger in Fingers)
        {
            finger.Validate(Side, guides);
            if (!digitIds.Add(finger.Id)) throw new ArgumentException("A hand cannot declare a digit more than once.");
        }
        if (guides is null) return;
        if (!guides.TryGetValue(WristGuideId, out RigLandmark? wrist) ||
            wrist.RoleId != $"hand.{Side.ToString().ToLowerInvariant()}")
            throw new ArgumentException("The wrist guide must be an existing hand guide on the matching side.");
    }
}
