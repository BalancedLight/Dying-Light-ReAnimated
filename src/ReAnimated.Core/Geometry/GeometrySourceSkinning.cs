using System.Collections.Immutable;

namespace ReAnimated.Core.Geometry;

/// <summary>
/// One original source weight entry. Object identifiers are scoped to the source artifact.
/// Retained refers to the aggregate joint surviving import-time reduction; Weight is never normalized.
/// ImportedBoneIndex refers to the original imported rig, not a subsequently edited rig.
/// </summary>
public readonly record struct GeometrySourceInfluence(string SkinId, string DeformerId, string JointId,
    int SourceEntryIndex, int ImportedBoneIndex, double Weight, bool Retained);

/// <summary>All original entries for a control point, including duplicate joint and sub-threshold entries.</summary>
public sealed record GeometrySourceControlPointWeights(ImmutableArray<GeometrySourceInfluence> Influences,
    double RetainedWeight, double DiscardedWeight);

/// <summary>Import-time source binding evidence, shared by all render partitions of a component.</summary>
public sealed record GeometrySourceSkinning(bool HasSkinDeformer,
    ImmutableArray<GeometrySourceControlPointWeights> ControlPoints)
{
    /// <summary>Checks source evidence internally; does not validate a current edited rig or skin solution.</summary>
    public void Validate(int controlPointCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ControlPoints.IsDefault || ControlPoints.Length != controlPointCount)
            throw new InvalidDataException("Source skin weights do not cover the source control-point inventory.");
        var jointBones = new Dictionary<string, int>(StringComparer.Ordinal);
        var boneJoints = new Dictionary<int, string>();
        var deformers = new Dictionary<(string Skin, string Deformer), string>();
        var sourceEntries = new HashSet<(string Skin, string Deformer, int Entry)>();
        foreach (GeometrySourceControlPointWeights point in ControlPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point is null || point.Influences.IsDefault ||
                !double.IsFinite(point.RetainedWeight) || point.RetainedWeight < 0 ||
                !double.IsFinite(point.DiscardedWeight) || point.DiscardedWeight < 0)
                throw new InvalidDataException("Source control-point weights contain invalid totals or entries.");
            double retained = 0, discarded = 0;
            var jointDecisions = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (GeometrySourceInfluence influence in point.Influences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(influence.SkinId) || string.IsNullOrWhiteSpace(influence.DeformerId) ||
                    string.IsNullOrWhiteSpace(influence.JointId) || influence.SourceEntryIndex < 0 || influence.ImportedBoneIndex < 0 ||
                    !double.IsFinite(influence.Weight) || influence.Weight <= 0)
                    throw new InvalidDataException("Source skin influence has an invalid identity or weight.");
                if (!sourceEntries.Add((influence.SkinId, influence.DeformerId, influence.SourceEntryIndex)))
                    throw new InvalidDataException("A source skin entry is repeated across control points.");
                if (jointBones.TryGetValue(influence.JointId, out int bone) && bone != influence.ImportedBoneIndex ||
                    boneJoints.TryGetValue(influence.ImportedBoneIndex, out string? joint) && joint != influence.JointId ||
                    deformers.TryGetValue((influence.SkinId, influence.DeformerId), out string? deformerJoint) && deformerJoint != influence.JointId ||
                    jointDecisions.TryGetValue(influence.JointId, out bool decision) && decision != influence.Retained)
                    throw new InvalidDataException("Source skin influences disagree about joint identity or retention.");
                jointBones[influence.JointId] = influence.ImportedBoneIndex;
                boneJoints[influence.ImportedBoneIndex] = influence.JointId;
                deformers[(influence.SkinId, influence.DeformerId)] = influence.JointId;
                jointDecisions[influence.JointId] = influence.Retained;
                if (influence.Retained) retained += influence.Weight;
                else discarded += influence.Weight;
            }
            if (!double.IsFinite(retained + discarded) || !Matches(retained, point.RetainedWeight) || !Matches(discarded, point.DiscardedWeight))
                throw new InvalidDataException("Source skin totals do not match the preserved original weights.");
            if (HasSkinDeformer ? retained <= 0 : point.Influences.Length != 0)
                throw new InvalidDataException("Source skin activation disagrees with its control-point weights.");
        }
    }

    private static bool Matches(double left, double right) =>
        left == right || Math.Abs(left - right) <= 1e-12 * Math.Max(Math.Abs(left), Math.Abs(right));
}
