using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

internal static class DirectRigPoseBinder
{
    public static SkeletonPose Apply(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        AnimationClip clip,
        DirectRigBinding binding)
    {
        ArgumentNullException.ThrowIfNull(sourcePose);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(binding);
        binding.ValidateFor(sourcePose.Rig, targetRig);

        Dictionary<int, DirectBoneBinding> rowsBySource = binding.Rows
            .ToDictionary(static row => row.SourceBoneIndex);
        foreach (TransformTrack track in clip.TransformTracks)
        {
            if (!rowsBySource.ContainsKey(track.BoneIndex))
            {
                throw new InvalidOperationException(
                    $"Direct binding does not cover animated source node {track.BoneIndex}.");
            }
        }

        ImmutableArray<TransformTRS>.Builder targetLocals = targetRig.Bones
            .Select(static bone => bone.LocalBindPose)
            .ToImmutableArray()
            .ToBuilder();
        foreach (DirectBoneBinding row in binding.Rows)
        {
            TransformMatrix sourceBind = sourcePose.Rig
                .Bones[row.SourceBoneIndex]
                .LocalBindPose
                .ToMatrix();
            TransformMatrix sourceCurrent = sourcePose
                .LocalTransforms[row.SourceBoneIndex]
                .ToMatrix();
            TransformMatrix targetBind = targetRig
                .Bones[row.TargetBoneIndex]
                .LocalBindPose
                .ToMatrix();
            TransformMatrix localDelta =
                sourceBind.InvertedAffine() * sourceCurrent;
            targetLocals[row.TargetBoneIndex] =
                (targetBind * localDelta).Decompose();
        }

        return new SkeletonPose(targetRig, targetLocals.MoveToImmutable());
    }
}
