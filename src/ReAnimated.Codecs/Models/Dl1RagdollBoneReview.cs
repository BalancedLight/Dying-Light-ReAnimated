using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

/// <summary>Source-level PHX relationships for one declared physical bone; not a native collision shape.</summary>
public sealed record Dl1RagdollBoneReview(
    string BoneName,
    int CallIndex,
    string ShapeToken,
    double RelativeMass,
    double? RadiusMultiplier,
    ImmutableArray<int> RelatedCallIndexes,
    int JointCount,
    int CollisionCount,
    int SynchronizationCount)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double? ScaleMultiplier=>RadiusMultiplier;
}

public static class Dl1RagdollBoneReviewProjection
{
    public static ImmutableArray<Dl1RagdollBoneReview> Build(Dl1RagdollDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Bones.Select(bone => bone.BoneName).Distinct(StringComparer.Ordinal).Count() != document.Bones.Length)
            throw new InvalidDataException("A physical bone is declared more than once in this ragdoll source.");

        var related = document.Bones.ToDictionary(bone => bone.BoneName,
            _ => new HashSet<int>(), StringComparer.Ordinal);
        foreach (Dl1RagdollBoneUse bone in document.Bones)
            related[bone.BoneName].Add(bone.CallIndex);

        foreach (NativeCharacterReference reference in document.References)
        {
            if (reference.Kind == NativeCharacterReferenceKind.Bone)
            {
                if (related.TryGetValue(reference.Name, out HashSet<int>? calls)) calls.Add(reference.CallIndex);
            }
            else if (reference.Kind == NativeCharacterReferenceKind.JointPair)
            {
                string[] names = reference.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (names.Length != 2)
                    throw new InvalidDataException("A classified ragdoll joint pair must name exactly two bones.");
                foreach (string name in names.Distinct(StringComparer.Ordinal))
                    if (related.TryGetValue(name, out HashSet<int>? calls)) calls.Add(reference.CallIndex);
            }
        }

        return document.Bones.Select(bone =>
        {
            HashSet<int> calls = related[bone.BoneName];
            return new Dl1RagdollBoneReview(
                bone.BoneName,
                bone.CallIndex,
                bone.ShapeToken,
                bone.RelativeMass,
                bone.RadiusMultiplier,
                calls.Order().ToImmutableArray(),
                document.Joints.Count(joint => joint.FirstBone == bone.BoneName || joint.SecondBone == bone.BoneName),
                document.CollisionSettings.Count(setting => calls.Contains(setting.CallIndex)),
                document.SynchronizationSettings.Count(setting => calls.Contains(setting.CallIndex)));
        }).ToImmutableArray();
    }
}
