using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Retargeting;

public interface IPoseRetargeter
{
    SkeletonPose Retarget(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        RetargetMap map,
        IEnumerable<int>? reviewedTargetBindBones = null);
}

public sealed class PoseRetargetingService : IPoseRetargeter
{
    public SkeletonPose Retarget(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        RetargetMap map,
        IEnumerable<int>? reviewedTargetBindBones = null) =>
        PoseRetargeter.Retarget(
            sourcePose,
            targetRig,
            map,
            reviewedTargetBindBones);
}

/// <summary>
/// Applies the primary body solve first, followed by target-specific helper
/// overrides. The two phases are also exposed separately so the authoritative
/// evaluator can place authored edits, IK, and root policy before the helper
/// phase. Every policy produces a target-local TRS through the explicit
/// authoring transform boundary; unowned components retain the target bind.
/// </summary>
public static class PoseRetargeter
{
    public static SkeletonPose Retarget(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        RetargetMap map,
        IEnumerable<int>? reviewedTargetBindBones = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePose);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(map);

        ValidateCompatibility(
            sourcePose,
            targetRig,
            map,
            reviewedTargetBindBones);
        SkeletonPose bodyPose = RetargetBodyCore(
            sourcePose,
            targetRig,
            map);
        return ApplyHelperOverridesCore(
            sourcePose,
            bodyPose,
            map);
    }

    /// <summary>
    /// Evaluates only ordinary body mappings. Helper, camera, and prop targets
    /// remain at target bind until <see cref="ApplyHelperOverrides"/> is called.
    /// </summary>
    public static SkeletonPose RetargetBody(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        RetargetMap map,
        IEnumerable<int>? reviewedTargetBindBones = null)
    {
        ArgumentNullException.ThrowIfNull(sourcePose);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(map);

        ValidateCompatibility(
            sourcePose,
            targetRig,
            map,
            reviewedTargetBindBones);
        return RetargetBodyCore(sourcePose, targetRig, map);
    }

    /// <summary>
    /// Re-evaluates helper overrides against an already-authored target pose.
    /// Global policies therefore resolve against the final edited/IK/root
    /// parent transforms instead of the pre-edit retarget pose.
    /// </summary>
    public static SkeletonPose ApplyHelperOverrides(
        SkeletonPose sourcePose,
        SkeletonPose targetPose,
        RetargetMap map)
    {
        ArgumentNullException.ThrowIfNull(sourcePose);
        ArgumentNullException.ThrowIfNull(targetPose);
        ArgumentNullException.ThrowIfNull(map);
        ValidateMapContract(sourcePose.Rig, targetPose.Rig, map);
        return ApplyHelperOverridesCore(sourcePose, targetPose, map);
    }

    private static void ValidateCompatibility(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        RetargetMap map,
        IEnumerable<int>? reviewedTargetBindBones)
    {
        CompatibilityReport report = RigCompatibilityAnalyzer.Analyze(
            sourcePose.Rig,
            targetRig,
            map,
            reviewedTargetBindBones);
        if (!report.CanEvaluate)
        {
            string reasons = string.Join(
                "; ",
                report.Diagnostics
                    .Where(
                        static diagnostic =>
                            diagnostic.Severity == CompatibilityDiagnosticSeverity.Error)
                    .Select(static diagnostic => diagnostic.Message));
            throw new InvalidOperationException($"The retarget map is incompatible: {reasons}");
        }
    }

    private static SkeletonPose RetargetBodyCore(
        SkeletonPose sourcePose,
        RigDefinition targetRig,
        RetargetMap map)
    {
        ValidateMapContract(sourcePose.Rig, targetRig, map);

        SkeletonPose sourceBind = sourcePose.Rig.CreateBindPose();
        SkeletonPose targetBind = targetRig.CreateBindPose();
        Dictionary<int, BoneMapEntry> bodyByTarget = map.Entries
            .Where(static entry =>
                entry.MappingKind == RetargetMappingKind.Bone)
            .ToDictionary(
                static entry => entry.TargetBoneIndex);
        var targetGlobals = new TransformMatrix[targetRig.BoneCount];
        var targetLocals = new TransformTRS[targetRig.BoneCount];
        QuaternionD[] sourcePoseGlobalRotations =
            ComputeGlobalRotations(sourcePose);
        QuaternionD[] sourceBindGlobalRotations =
            ComputeGlobalRotations(sourceBind);
        QuaternionD[] targetBindGlobalRotations =
            ComputeGlobalRotations(targetBind);
        var targetGlobalRotations =
            new QuaternionD[targetRig.BoneCount];
        IReadOnlyDictionary<int, QuaternionD>
            anatomicalGlobalRotations =
                HumanoidAnatomicalRetargeter
                    .EvaluateDesiredGlobalRotations(
                        sourcePose,
                        targetBind,
                        map);

        for (int targetIndex = 0; targetIndex < targetRig.BoneCount; targetIndex++)
        {
            int targetParent = targetRig.Bones[targetIndex].ParentIndex;
            TransformMatrix? targetParentGlobal = targetParent < 0
                ? null
                : targetGlobals[targetParent];
            QuaternionD? targetParentGlobalRotation =
                targetParent < 0
                    ? null
                    : targetGlobalRotations[targetParent];
            TransformTRS targetLocal = bodyByTarget.TryGetValue(
                    targetIndex,
                    out BoneMapEntry? entry)
                ? EvaluateAndMergeLocal(
                    sourcePose,
                    sourceBind,
                    targetBind,
                    targetIndex,
                    entry,
                    targetParentGlobal,
                    targetParentGlobalRotation,
                    sourcePoseGlobalRotations,
                    sourceBindGlobalRotations,
                    targetBindGlobalRotations,
                    anatomicalGlobalRotations)
                : targetBind.LocalTransforms[targetIndex];
            targetLocals[targetIndex] = targetLocal;
            targetGlobals[targetIndex] = targetParentGlobal.HasValue
                ? targetParentGlobal.Value * targetLocal.ToMatrix()
                : targetLocal.ToMatrix();
            targetGlobalRotations[targetIndex] =
                targetParentGlobalRotation.HasValue
                    ? (
                        targetParentGlobalRotation.Value *
                        targetLocal.Rotation
                    ).Normalized()
                    : targetLocal.Rotation.Normalized();
        }

        return new SkeletonPose(targetRig, targetLocals);
    }

    private static SkeletonPose ApplyHelperOverridesCore(
        SkeletonPose sourcePose,
        SkeletonPose targetPose,
        RetargetMap map)
    {
        Dictionary<int, BoneMapEntry> helpersByTarget = map.Entries
            .Where(static entry =>
                entry.MappingKind == RetargetMappingKind.HelperOverride)
            .ToDictionary(
                static entry => entry.TargetBoneIndex);
        if (helpersByTarget.Count == 0)
        {
            return targetPose;
        }

        SkeletonPose sourceBind = sourcePose.Rig.CreateBindPose();
        SkeletonPose targetBind = targetPose.Rig.CreateBindPose();
        TransformTRS[] targetLocals = targetPose.LocalTransforms.ToArray();
        var targetGlobals =
            new TransformMatrix[targetPose.Rig.BoneCount];
        QuaternionD[] sourcePoseGlobalRotations =
            ComputeGlobalRotations(sourcePose);
        QuaternionD[] sourceBindGlobalRotations =
            ComputeGlobalRotations(sourceBind);
        QuaternionD[] targetBindGlobalRotations =
            ComputeGlobalRotations(targetBind);
        var targetGlobalRotations =
            new QuaternionD[targetPose.Rig.BoneCount];
        IReadOnlyDictionary<int, QuaternionD>
            noAnatomicalOverrides =
                new Dictionary<int, QuaternionD>();

        // Rebuild globals in hierarchy order so a helper parent affects its
        // descendants without re-running or changing their authored locals.
        for (int targetIndex = 0;
             targetIndex < targetPose.Rig.BoneCount;
             targetIndex++)
        {
            int targetParent =
                targetPose.Rig.Bones[targetIndex].ParentIndex;
            TransformMatrix? targetParentGlobal = targetParent < 0
                ? null
                : targetGlobals[targetParent];
            QuaternionD? targetParentGlobalRotation =
                targetParent < 0
                    ? null
                    : targetGlobalRotations[targetParent];
            if (helpersByTarget.TryGetValue(
                    targetIndex,
                    out BoneMapEntry? helper))
            {
                targetLocals[targetIndex] = EvaluateAndMergeLocal(
                    sourcePose,
                    sourceBind,
                    targetBind,
                    targetIndex,
                    helper,
                    targetParentGlobal,
                    targetParentGlobalRotation,
                    sourcePoseGlobalRotations,
                    sourceBindGlobalRotations,
                    targetBindGlobalRotations,
                    noAnatomicalOverrides);
            }

            targetGlobals[targetIndex] = targetParentGlobal.HasValue
                ? targetParentGlobal.Value *
                  targetLocals[targetIndex].ToMatrix()
                : targetLocals[targetIndex].ToMatrix();
            targetGlobalRotations[targetIndex] =
                targetParentGlobalRotation.HasValue
                    ? (
                        targetParentGlobalRotation.Value *
                        targetLocals[targetIndex].Rotation
                    ).Normalized()
                    : targetLocals[targetIndex].Rotation.Normalized();
        }

        return new SkeletonPose(targetPose.Rig, targetLocals);
    }

    private static void ValidateMapContract(
        RigDefinition sourceRig,
        RigDefinition targetRig,
        RetargetMap map)
    {
        if (!string.Equals(
                map.SourceRigId,
                sourceRig.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                map.TargetRigId,
                targetRig.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The retarget map rig identities do not match the supplied source and target rigs.");
        }

        if (map.Entries.Any(entry =>
                entry.SourceBoneIndex >= sourceRig.BoneCount ||
                entry.TargetBoneIndex >= targetRig.BoneCount))
        {
            throw new InvalidOperationException(
                "The retarget map contains a bone index outside the supplied source or target rig.");
        }
    }

    private static TransformTRS EvaluateAndMergeLocal(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        int targetIndex,
        BoneMapEntry entry,
        TransformMatrix? targetParentGlobal,
        QuaternionD? targetParentGlobalRotation,
        IReadOnlyList<QuaternionD> sourcePoseGlobalRotations,
        IReadOnlyList<QuaternionD> sourceBindGlobalRotations,
        IReadOnlyList<QuaternionD> targetBindGlobalRotations,
        IReadOnlyDictionary<int, QuaternionD>
            anatomicalGlobalRotations)
    {
        int sourceIndex = entry.SourceBoneIndex;
        TransformTRS candidate = entry.TransferPolicy switch
        {
            RetargetTransferPolicy.GlobalBindBasis =>
                EvaluateGlobalBindBasis(
                    sourcePose,
                    sourceBind,
                    targetBind,
                    targetIndex,
                    sourceIndex,
                    targetParentGlobal,
                    targetParentGlobalRotation,
                    sourcePoseGlobalRotations,
                    sourceBindGlobalRotations,
                    targetBindGlobalRotations),
            RetargetTransferPolicy.RestRelative =>
                EvaluateRestRelative(
                    sourcePose,
                    sourceBind,
                    targetBind,
                    targetIndex,
                    sourceIndex),
            RetargetTransferPolicy.RotationDelta =>
                EvaluateRotationDelta(
                    sourcePose,
                    sourceBind,
                    targetBind,
                    targetIndex,
                    sourceIndex),
            RetargetTransferPolicy.GlobalRotationDelta =>
                EvaluateGlobalRotationDelta(
                    targetBind,
                    targetIndex,
                    sourceIndex,
                    targetParentGlobalRotation,
                    sourcePoseGlobalRotations,
                    sourceBindGlobalRotations,
                    targetBindGlobalRotations),
            RetargetTransferPolicy.AnatomicalDirection =>
                EvaluateAnatomicalDirection(
                    sourcePose,
                    sourceBind,
                    targetBind,
                    targetIndex,
                    sourceIndex,
                    targetParentGlobalRotation,
                    anatomicalGlobalRotations),
            RetargetTransferPolicy.CopyLocal =>
                sourcePose.LocalTransforms[sourceIndex],
            RetargetTransferPolicy.Bind =>
                targetBind.LocalTransforms[targetIndex],
            _ => throw new InvalidOperationException(
                $"Unsupported retarget transfer policy '{entry.TransferPolicy}'."),
        };

        TransformTRS basis = targetBind.LocalTransforms[targetIndex];
        TransformTRS merged = entry.ComponentPolicy switch
        {
            RetargetComponentPolicy.FullTransform => candidate,
            RetargetComponentPolicy.Rotation => basis with
            {
                Rotation = candidate.Rotation,
            },
            RetargetComponentPolicy.Translation => basis with
            {
                Translation = candidate.Translation,
            },
            RetargetComponentPolicy.RotationTranslation => basis with
            {
                Translation = candidate.Translation,
                Rotation = candidate.Rotation,
            },
            RetargetComponentPolicy.Scale => basis with
            {
                Scale = candidate.Scale,
            },
            _ => throw new InvalidOperationException(
                $"Unsupported retarget component policy '{entry.ComponentPolicy}'."),
        };
        return RequireValidLocal(
            merged,
            targetIndex,
            entry.TransferPolicy,
            entry.ComponentPolicy);
    }

    private static TransformTRS EvaluateGlobalBindBasis(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        int targetIndex,
        int sourceIndex,
        TransformMatrix? targetParentGlobal,
        QuaternionD? targetParentGlobalRotation,
        IReadOnlyList<QuaternionD> sourcePoseGlobalRotations,
        IReadOnlyList<QuaternionD> sourceBindGlobalRotations,
        IReadOnlyList<QuaternionD> targetBindGlobalRotations)
    {
        TransformMatrix desiredGlobal =
            sourcePose.GlobalMatrices[sourceIndex] *
            sourceBind.GlobalMatrices[sourceIndex].InvertedAffine() *
            targetBind.GlobalMatrices[targetIndex];
        TransformMatrix local = targetParentGlobal.HasValue
            ? targetParentGlobal.Value.InvertedAffine() * desiredGlobal
            : desiredGlobal;
        try
        {
            return DecomposeFinite(
                local,
                targetIndex,
                RetargetTransferPolicy.GlobalBindBasis);
        }
        catch (InvalidOperationException exception) when (
            IsShearDecompositionFailure(exception))
        {
            // Older schema-1 C# projects may contain automatic full-transform
            // global-bind rows for anatomically matched but differently
            // proportioned rigs.  A non-uniform source parent can turn that
            // correction into shear, which ANM2 cannot encode and which would
            // catastrophically deform a skinned target if approximated as a
            // full matrix.  Preserve target translation/scale and transfer
            // only the source's model-space bind-relative rotation, matching
            // the safe cross-rig policy used by new automatic maps.
            return EvaluateGlobalRotationDelta(
                targetBind,
                targetIndex,
                sourceIndex,
                targetParentGlobalRotation,
                sourcePoseGlobalRotations,
                sourceBindGlobalRotations,
                targetBindGlobalRotations);
        }
    }

    private static TransformTRS EvaluateRestRelative(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        int targetIndex,
        int sourceIndex)
    {
        TransformTRS target = targetBind.LocalTransforms[targetIndex];
        TransformTRS sourceRest = sourceBind.LocalTransforms[sourceIndex];
        TransformTRS sourceAnimated =
            sourcePose.LocalTransforms[sourceIndex];
        try
        {
            return DecomposeFinite(
                target.ToMatrix() *
                sourceRest.ToMatrix().InvertedAffine() *
                sourceAnimated.ToMatrix(),
                targetIndex,
                RetargetTransferPolicy.RestRelative);
        }
        catch (InvalidOperationException exception) when (
            IsShearDecompositionFailure(exception))
        {
            // Rest-relative helper rows can encounter the same unencodable
            // shear when a source hierarchy uses non-uniform scale.  Preserve
            // a deterministic TRS delta instead of dropping the complete
            // preview.  Component ownership is applied by the caller, so
            // rotation-only camera/helper rows still retain their target bind
            // pivot and scale exactly.
            Vector3D relativeScale = new(
                sourceAnimated.Scale.X / sourceRest.Scale.X,
                sourceAnimated.Scale.Y / sourceRest.Scale.Y,
                sourceAnimated.Scale.Z / sourceRest.Scale.Z);
            return new TransformTRS(
                target.Translation +
                (sourceAnimated.Translation -
                 sourceRest.Translation),
                (
                    target.Rotation *
                    sourceRest.Rotation.Inverse() *
                    sourceAnimated.Rotation
                ).Normalized(),
                Vector3D.ComponentMultiply(
                    target.Scale,
                    relativeScale));
        }
    }

    private static TransformTRS EvaluateRotationDelta(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        int targetIndex,
        int sourceIndex)
    {
        TransformTRS target = targetBind.LocalTransforms[targetIndex];
        QuaternionD rotation =
            target.Rotation *
            sourceBind.LocalTransforms[sourceIndex].Rotation.Inverse() *
            sourcePose.LocalTransforms[sourceIndex].Rotation;
        return target with { Rotation = rotation.Normalized() };
    }

    private static TransformTRS EvaluateGlobalRotationDelta(
        SkeletonPose targetBind,
        int targetIndex,
        int sourceIndex,
        QuaternionD? targetParentGlobalRotation,
        IReadOnlyList<QuaternionD> sourcePoseGlobalRotations,
        IReadOnlyList<QuaternionD> sourceBindGlobalRotations,
        IReadOnlyList<QuaternionD> targetBindGlobalRotations)
    {
        TransformTRS target = targetBind.LocalTransforms[targetIndex];
        QuaternionD sourceWorldDelta =
            (
                sourcePoseGlobalRotations[sourceIndex] *
                sourceBindGlobalRotations[sourceIndex].Inverse()
            ).Normalized();
        QuaternionD desiredTargetGlobal =
            (
                sourceWorldDelta *
                targetBindGlobalRotations[targetIndex]
            ).Normalized();
        QuaternionD targetLocalRotation =
            targetParentGlobalRotation.HasValue
                ? (
                    targetParentGlobalRotation.Value.Inverse() *
                    desiredTargetGlobal
                ).Normalized()
                : desiredTargetGlobal;
        return target with
        {
            Rotation = targetLocalRotation,
        };
    }

    private static TransformTRS EvaluateAnatomicalDirection(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        int targetIndex,
        int sourceIndex,
        QuaternionD? targetParentGlobalRotation,
        IReadOnlyDictionary<int, QuaternionD>
            anatomicalGlobalRotations)
    {
        if (!anatomicalGlobalRotations.TryGetValue(
                targetIndex,
                out QuaternionD desiredGlobal))
        {
            BoneDefinition targetBone =
                targetBind.Rig.Bones[targetIndex];
            if (HumanoidAnatomicalRetargeter
                .KeepsBindWhenUnsolved(
                    targetBone.SemanticRole ??
                    targetBone.Name))
            {
                return targetBind.LocalTransforms[targetIndex];
            }

            return EvaluateRotationDelta(
                sourcePose,
                sourceBind,
                targetBind,
                targetIndex,
                sourceIndex);
        }

        QuaternionD local = targetParentGlobalRotation.HasValue
            ? (
                targetParentGlobalRotation.Value.Inverse() *
                desiredGlobal
            ).Normalized()
            : desiredGlobal.Normalized();
        return targetBind.LocalTransforms[targetIndex] with
        {
            Rotation = local,
        };
    }

    private static QuaternionD[] ComputeGlobalRotations(
        SkeletonPose pose)
    {
        var result = new QuaternionD[pose.Rig.BoneCount];
        for (int boneIndex = 0;
             boneIndex < pose.Rig.BoneCount;
             boneIndex++)
        {
            int parentIndex =
                pose.Rig.Bones[boneIndex].ParentIndex;
            QuaternionD local =
                pose.LocalTransforms[boneIndex].Rotation.Normalized();
            result[boneIndex] = parentIndex < 0
                ? local
                : (result[parentIndex] * local).Normalized();
        }

        return result;
    }

    private static TransformTRS DecomposeFinite(
        TransformMatrix matrix,
        int targetIndex,
        RetargetTransferPolicy transferPolicy)
    {
        if (!matrix.IsFinite)
        {
            throw new InvalidOperationException(
                $"Retarget policy '{transferPolicy}' produced a non-finite matrix for target bone {targetIndex}.");
        }

        try
        {
            return RequireValidLocal(
                matrix.Decompose(),
                targetIndex,
                transferPolicy,
                RetargetComponentPolicy.FullTransform);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Retarget policy '{transferPolicy}' produced an invalid local transform for target bone {targetIndex}.",
                exception);
        }
    }

    private static bool IsShearDecompositionFailure(
        InvalidOperationException exception) =>
        exception.InnerException is InvalidOperationException
        {
            Message: "A sheared matrix cannot be represented as translation, rotation, and scale.",
        };

    private static TransformTRS RequireValidLocal(
        TransformTRS transform,
        int targetIndex,
        RetargetTransferPolicy transferPolicy,
        RetargetComponentPolicy componentPolicy)
    {
        if (!transform.IsFinite ||
            Math.Abs(transform.Scale.X) <= 1e-12 ||
            Math.Abs(transform.Scale.Y) <= 1e-12 ||
            Math.Abs(transform.Scale.Z) <= 1e-12)
        {
            throw new InvalidOperationException(
                $"Retarget policies '{transferPolicy}'/'{componentPolicy}' produced a non-finite or singular local transform for target bone {targetIndex}.");
        }

        return transform.Normalized();
    }
}
