using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

/// <summary>Explicit authoring driver. Attachment scope controls whether it participates in export.</summary>
public sealed record AttachmentSecondaryIk
{
    public int RootBoneIndex { get; init; }
    public string RootBoneName { get; init; } = string.Empty;
    public int JointBoneIndex { get; init; }
    public string JointBoneName { get; init; } = string.Empty;
    public int EndBoneIndex { get; init; }
    public string EndBoneName { get; init; } = string.Empty;
    public ImmutableArray<double> Pole { get; init; } = [0, 0, 1];
    public bool PoleInPrimaryGripSpace { get; init; } = true;
    public bool MatchOrientation { get; init; } = true;
    public double Weight { get; init; } = 1;
    [JsonIgnore] public Vector3D PolePoint => new(Pole[0], Pole[1], Pole[2]);
    public void Validate()
    {
        if (RootBoneIndex < 0 || JointBoneIndex < 0 || EndBoneIndex < 0 || RootBoneIndex == JointBoneIndex || RootBoneIndex == EndBoneIndex || JointBoneIndex == EndBoneIndex)
            throw new ArgumentException("Secondary IK requires three distinct nonnegative chain indices.");
        foreach (string name in new[] { RootBoneName, JointBoneName, EndBoneName })
            if (string.IsNullOrWhiteSpace(name) || name.Length > AttachmentBinding.MaximumNameLength) throw new ArgumentException("Secondary IK requires bounded stable chain names.");
        if (Pole.IsDefault || Pole.Length != 3 || Pole.Any(static n => !double.IsFinite(n)) || !double.IsFinite(Weight) || Weight is < 0 or > 1)
            throw new ArgumentException("Secondary IK requires a finite pole and weight within 0..1.");
    }
}
