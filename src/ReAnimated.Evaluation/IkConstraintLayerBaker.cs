using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Evaluation;

/// <summary>
/// Deterministically samples one keyed two-bone IK layer into an ordinary
/// authored override layer. The result enters the same edit-layer path as
/// hand-authored FK keys and no longer depends on an IK solver at export time.
/// </summary>
public static class IkConstraintLayerBaker
{
    public const long MaximumBakeFrameCount = 65_535;

    public static BoneEditLayer BakeToOverrideLayer(
        IAnimationEvaluator evaluator,
        EvaluationRequest template,
        Guid ikLayerId,
        Guid outputLayerId,
        string outputLayerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(template);
        if (ikLayerId == Guid.Empty)
        {
            throw new ArgumentException(
                "An IK layer identifier is required.",
                nameof(ikLayerId));
        }

        if (outputLayerId == Guid.Empty)
        {
            throw new ArgumentException(
                "A baked edit-layer identifier is required.",
                nameof(outputLayerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputLayerName);
        IkConstraintLayer selected = template.IkLayers.SingleOrDefault(
                layer => layer.Id == ikLayerId)
            ?? throw new InvalidOperationException(
                "The selected IK layer is not present in the evaluation request.");
        if (!selected.Enabled ||
            selected.Weight <= 0 ||
            selected.Scope != IkConstraintScope.AuthoredExportable)
        {
            throw new InvalidOperationException(
                "Only an enabled authored IK layer with non-zero weight can be baked.");
        }

        if (template.Clip.FrameCount > MaximumBakeFrameCount)
        {
            throw new InvalidOperationException(
                $"IK baking is limited to {MaximumBakeFrameCount:N0} frames.");
        }

        int[] chainBones =
        [
            selected.RootBoneIndex,
            selected.JointBoneIndex,
            selected.EndBoneIndex,
        ];
        HashSet<int> chainBoneSet = chainBones.ToHashSet();
        if (template.IkConstraints.Any(constraint =>
                constraint.Weight > 0 &&
                (chainBoneSet.Contains(constraint.RootBoneIndex) ||
                 chainBoneSet.Contains(constraint.JointBoneIndex) ||
                 chainBoneSet.Contains(constraint.EndBoneIndex))) ||
            template.IkLayers.Any(layer =>
                layer.Id != selected.Id &&
                layer.Enabled &&
                layer.Weight > 0 &&
                (chainBoneSet.Contains(layer.RootBoneIndex) ||
                 chainBoneSet.Contains(layer.JointBoneIndex) ||
                 chainBoneSet.Contains(layer.EndBoneIndex))))
        {
            throw new InvalidOperationException(
                "The selected IK chain overlaps another enabled IK constraint. Bake or disable the overlapping layer first.");
        }

        var keysByBone =
            chainBones.ToDictionary(
                static boneIndex => boneIndex,
                static _ => ImmutableArray.CreateBuilder<
                    TransformKeyframe>());
        for (long frameIndex = 0;
             frameIndex < template.Clip.FrameCount;
             frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds =
                template.Clip.FrameRate.SecondsForFrame(frameIndex);
            EvaluationRequest request = CreateBakeRequest(
                template,
                selected,
                seconds);
            EvaluationFrame frame = evaluator.Evaluate(request);
            foreach (int boneIndex in chainBones)
            {
                keysByBone[boneIndex].Add(
                    new TransformKeyframe(
                        frameIndex,
                        frame.AuthoredPose.LocalTransforms[boneIndex]));
            }
        }

        BoneEditTrack[] tracks = chainBones
            .Select(boneIndex =>
                new BoneEditTrack(
                    boneIndex,
                    keysByBone[boneIndex].ToImmutable()))
            .ToArray();
        return new BoneEditLayer(
            outputLayerId,
            outputLayerName,
            BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            tracks);
    }

    private static EvaluationRequest CreateBakeRequest(
        EvaluationRequest template,
        IkConstraintLayer selected,
        double seconds) =>
        new(
            template.SourceRig,
            template.TargetRig,
            template.Clip,
            seconds,
            PreviewProfile.RawAuthoring,
            template.RetargetMap,
            template.EditLayers,
            playbackMode: PlaybackMode.Clamp,
            purpose: EvaluationPurpose.Export,
            // Root/helper policy remains downstream of a baked FK layer and
            // therefore must not be captured into the layer itself.
            dl1AuthoringPolicy: null,
            ikLayers: [selected]);
}
