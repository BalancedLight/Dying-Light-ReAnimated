using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Evaluation;

/// <summary>
/// The authoritative pose evaluator shared by animation export and preview.
/// </summary>
public sealed class AnimationEvaluator : IAnimationEvaluator
{
    private readonly ImmutableArray<IPreviewProceduralStage> _previewProceduralStages;

    public AnimationEvaluator(
        IEnumerable<IPreviewProceduralStage>? previewProceduralStages = null)
    {
        _previewProceduralStages =
            previewProceduralStages?.ToImmutableArray() ?? [];
        if (_previewProceduralStages
            .Select(static stage => stage.Id)
            .Distinct(StringComparer.Ordinal)
            .Count() != _previewProceduralStages.Length)
        {
            throw new ArgumentException(
                "Preview procedural stage identifiers must be unique.",
                nameof(previewProceduralStages));
        }

        string[] reservedOverrides = _previewProceduralStages
            .Select(static stage => stage.Id)
            .Where(Dl1PreviewStageIds.IsBuiltIn)
            .ToArray();
        if (reservedOverrides.Length > 0)
        {
            throw new ArgumentException(
                "Built-in DL1 preview stage identifiers cannot be overridden: " +
                string.Join(", ", reservedOverrides),
                nameof(previewProceduralStages));
        }
    }

    public EvaluationFrame Evaluate(EvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        double sampleFrame = request.Clip.ResolveFrame(
            request.TimeSeconds,
            request.PlaybackMode);
        SkeletonPose authoredPose = EvaluateBeforeDl1Policy(
            request,
            request.TimeSeconds,
            request.PlaybackMode,
            out CompatibilityReport? compatibility,
            out SkeletonPose sourcePose);
        if (request.Dl1AuthoringPolicy is not null)
        {
            SkeletonPose firstAuthoredPose = sampleFrame == 0.0
                ? authoredPose
                : EvaluateBeforeDl1Policy(
                    request,
                    0.0,
                    PlaybackMode.Clamp,
                    out _,
                    out _);
            authoredPose = ApplyDl1AuthoringPolicy(
                request,
                authoredPose,
                firstAuthoredPose);
        }

        authoredPose = ApplyRetargetHelperOverrides(
            request,
            sourcePose,
            authoredPose);

        ImmutableDictionary<string, double> sampledMorphWeights =
            request.Clip.SampleScalars(
                request.TimeSeconds,
                request.PlaybackMode);
        var diagnostics = ImmutableArray.CreateBuilder<EvaluationDiagnostic>();
        AuxiliaryTransformTrack? motionTrack =
            request.Clip.AuxiliaryTransformTracks.FirstOrDefault(
                static track => track.Descriptor ==
                    Dl1RootMotionPolicy.MotionAccumulatorDescriptor);
        TransformTRS? auxiliaryMotion = motionTrack?.Sample(sampleFrame);
        TransformMatrix actorWorldTransform = TransformMatrix.Identity;
        if (request.Purpose == EvaluationPurpose.Preview &&
            request.PreviewMotionAccumulationEnabled)
        {
            if (motionTrack is null)
            {
                diagnostics.Add(new EvaluationDiagnostic(
                    "dl1_motion_accumulator_unavailable",
                    EvaluationDiagnosticSeverity.Warning,
                    "Preview accumulation is enabled, but this ANM2 has no auxiliary 0xCCC3CDDF track. Skeletal playback remains recorded and unchanged."));
            }
            else
            {
                actorWorldTransform = ActorMotionEvaluator.Evaluate(
                    motionTrack,
                    sampleFrame);
            }
        }

        MorphEvaluationResult evaluatedMorphs = MorphEvaluator.Evaluate(
            sampledMorphWeights,
            request.TargetRig,
            sampleFrame,
            request.PreviewProfile,
            request.Purpose,
            request.MorphBindings,
            request.MorphEditLayers);
        diagnostics.AddRange(evaluatedMorphs.Diagnostics);

        SkeletonPose displayPose = authoredPose;
        Dl1PreviewEvaluationResult? dl1Preview = null;
        if (request.Purpose == EvaluationPurpose.Preview)
        {
            displayPose = BoneEditLayerEvaluator.ApplyLayers(
                displayPose,
                sampleFrame,
                request.EditLayers,
                BoneEditLayerScope.PreviewOnly);
            ImmutableArray<TwoBoneIkConstraint> previewConstraints =
                ResolveIkConstraints(request, sampleFrame);
            displayPose = ApplyIkConstraints(
                displayPose,
                previewConstraints,
                IkConstraintScope.PreviewOnly);
            displayPose = ApplyRetargetHelperOverrides(
                request,
                sourcePose,
                displayPose);
            displayPose = ApplyPreviewProceduralStages(
                authoredPose,
                displayPose,
                sampleFrame,
                request.PreviewProfile,
                evaluatedMorphs.DisplayWeights,
                diagnostics);
            dl1Preview = Dl1PreviewContextEvaluator.Evaluate(
                displayPose,
                request.PreviewProfile,
                request.Dl1PreviewInputs);
            displayPose = dl1Preview.DisplayPose;
            diagnostics.AddRange(dl1Preview.Diagnostics);
        }

        ImmutableArray<EvaluatedAttachment> authoredAttachments =
            EvaluateAttachments(
                authoredPose,
                request.Attachments.Where(
                    static attachment =>
                        attachment.Scope == AttachmentScope.AuthoredExportable),
                diagnostics);
        ImmutableArray<EvaluatedAttachment> displayAttachments =
            request.Purpose == EvaluationPurpose.Preview
                ? EvaluateAttachments(
                    displayPose,
                    request.Attachments,
                    diagnostics)
                : authoredAttachments;
        EvaluatedCamera? evaluatedCamera = dl1Preview?.Camera is
            { } relativeCamera
                ? relativeCamera with
                {
                    WorldTransform = actorWorldTransform *
                        relativeCamera.WorldTransform,
                }
                : null;
        ImmutableArray<EvaluatedCameraHelper> cameraHelpers =
            dl1Preview?.CameraHelpers
                .Select(helper => helper with
                {
                    WorldTransform = actorWorldTransform *
                        helper.WorldTransform,
                })
                .ToImmutableArray() ?? [];

        return new EvaluationFrame(
            sampleFrame,
            authoredPose,
            displayPose,
            evaluatedMorphs.AuthoredWeights,
            evaluatedMorphs.DisplayWeights,
            request.PreviewProfile,
            evaluatedCamera,
            authoredAttachments,
            displayAttachments,
            compatibility,
            diagnostics.ToImmutable(),
            dl1Preview?.Stages,
            cameraHelpers,
            sourcePose,
            auxiliaryMotion,
            actorWorldTransform,
            sampledMorphWeights);
    }

    /// <summary>
    /// Evaluates only the authored/exportable pose for a bounded set of sample
    /// times. The first authored pose required by DL1 root-motion policy is
    /// evaluated once and shared by every sample, while the pose pipeline stays
    /// identical to <see cref="Evaluate"/>.
    /// </summary>
    public static void EvaluateAuthoredPoseBatch(
        EvaluationRequest request,
        IReadOnlyList<double> sampleTimesSeconds,
        Action<int, SkeletonPose> acceptPose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sampleTimesSeconds);
        ArgumentNullException.ThrowIfNull(acceptPose);

        cancellationToken.ThrowIfCancellationRequested();
        SkeletonPose? firstAuthoredPose = null;
        if (request.Dl1AuthoringPolicy is not null)
        {
            firstAuthoredPose = EvaluateBeforeDl1Policy(
                request,
                0.0,
                PlaybackMode.Clamp,
                out _,
                out _);
        }

        for (int index = 0; index < sampleTimesSeconds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double timeSeconds = sampleTimesSeconds[index];
            double sampleFrame = request.Clip.ResolveFrame(
                timeSeconds,
                request.PlaybackMode);
            SkeletonPose authoredPose;
            SkeletonPose sourcePose;
            if (firstAuthoredPose is not null &&
                sampleFrame == 0.0)
            {
                authoredPose = firstAuthoredPose;
                sourcePose = request.Clip.SamplePose(
                    request.SourceRig,
                    timeSeconds,
                    request.PlaybackMode);
            }
            else
            {
                authoredPose = EvaluateBeforeDl1Policy(
                        request,
                        timeSeconds,
                        request.PlaybackMode,
                        out _,
                        out sourcePose);
            }

            if (request.Dl1AuthoringPolicy is not null)
            {
                authoredPose = ApplyDl1AuthoringPolicy(
                    request,
                    authoredPose,
                    firstAuthoredPose!);
            }

            authoredPose = ApplyRetargetHelperOverrides(
                request,
                sourcePose,
                authoredPose);
            acceptPose(index, authoredPose);
        }
    }

    private static SkeletonPose ApplyDl1AuthoringPolicy(
        EvaluationRequest request,
        SkeletonPose authoredPose,
        SkeletonPose firstAuthoredPose) =>
        Dl1AuthoringPolicyEvaluator.Apply(
            request.SourceRig,
            authoredPose,
            firstAuthoredPose,
            request.Dl1AuthoringPolicy!).ExportablePose;

    private static SkeletonPose EvaluateBeforeDl1Policy(
        EvaluationRequest request,
        double timeSeconds,
        PlaybackMode playbackMode,
        out CompatibilityReport? compatibility,
        out SkeletonPose sourcePose)
    {
        double sampleFrame = request.Clip.ResolveFrame(
            timeSeconds,
            playbackMode);
        sourcePose = request.Clip.SamplePose(
            request.SourceRig,
            timeSeconds,
            playbackMode);

        SkeletonPose basePose;
        if (request.RetargetMap is not null)
        {
            compatibility = RigCompatibilityAnalyzer.Analyze(
                request.SourceRig,
                request.TargetRig,
                request.RetargetMap,
                request.Dl1AuthoringPolicy?.TargetBindBoneIndices);
            basePose = PoseRetargeter.RetargetBody(
                sourcePose,
                request.TargetRig,
                request.RetargetMap,
                request.Dl1AuthoringPolicy?.TargetBindBoneIndices);
        }
        else
        {
            compatibility = null;
            EnsureSameRigContract(request.SourceRig, request.TargetRig);
            basePose = new SkeletonPose(
                request.TargetRig,
                sourcePose.LocalTransforms);
        }

        SkeletonPose authoredPose = BoneEditLayerEvaluator.ApplyLayers(
            basePose,
            sampleFrame,
            request.EditLayers,
            BoneEditLayerScope.AuthoredExportable);
        return ApplyIkConstraints(
            authoredPose,
            ResolveIkConstraints(request, sampleFrame),
            IkConstraintScope.AuthoredExportable);
    }

    private static SkeletonPose ApplyRetargetHelperOverrides(
        EvaluationRequest request,
        SkeletonPose sourcePose,
        SkeletonPose targetPose) =>
        request.RetargetMap is null
            ? targetPose
            : PoseRetargeter.ApplyHelperOverrides(
                sourcePose,
                targetPose,
                request.RetargetMap);

    private static ImmutableArray<TwoBoneIkConstraint> ResolveIkConstraints(
        EvaluationRequest request,
        double sampleFrame) =>
        request.IkConstraints.AddRange(
            request.IkLayers.Select(layer => layer.Sample(sampleFrame)));

    private SkeletonPose ApplyPreviewProceduralStages(
        SkeletonPose authoredPose,
        SkeletonPose displayPose,
        double sampleFrame,
        PreviewProfile previewProfile,
        ImmutableDictionary<string, double> morphWeights,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        SkeletonPose result = displayPose;
        foreach (IPreviewProceduralStage stage in _previewProceduralStages)
        {
            if (!stage.IsEnabled(previewProfile))
            {
                continue;
            }

            PreviewProceduralResult stageResult = stage.Apply(
                new PreviewProceduralContext(
                    authoredPose,
                    result,
                    sampleFrame,
                    previewProfile,
                    morphWeights));
            EnsureSameRigContract(result.Rig, stageResult.DisplayPose.Rig);
            result = stageResult.DisplayPose;
            diagnostics.AddRange(stageResult.Diagnostics);
        }

        return result;
    }

    private static ImmutableArray<EvaluatedAttachment> EvaluateAttachments(
        SkeletonPose pose,
        IEnumerable<AttachmentBinding> bindings,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        var evaluated = ImmutableArray.CreateBuilder<EvaluatedAttachment>();
        foreach (AttachmentBinding binding in bindings)
        {
            if (binding.ParentBoneIndex >= pose.Rig.BoneCount)
            {
                diagnostics.Add(
                    new(
                        "attachment_parent_bone_missing",
                        EvaluationDiagnosticSeverity.Error,
                        $"Attachment '{binding.Name}' refers to missing bone index {binding.ParentBoneIndex}."));
                continue;
            }

            string actualBoneName =
                pose.Rig.Bones[binding.ParentBoneIndex].Name;
            if (!string.IsNullOrWhiteSpace(binding.ParentBoneName) &&
                !string.Equals(
                    binding.ParentBoneName,
                    actualBoneName,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(
                    new(
                        "attachment_parent_bone_mismatch",
                        EvaluationDiagnosticSeverity.Error,
                        $"Attachment '{binding.Name}' expects parent bone '{binding.ParentBoneName}' at index {binding.ParentBoneIndex}, but the active rig contains '{actualBoneName}' there."));
                continue;
            }

            evaluated.Add(
                new(
                    binding.Id,
                    binding.AssetId,
                    binding.Name,
                    pose.GlobalMatrices[binding.ParentBoneIndex] *
                        binding.LocalOffset.ToMatrix(),
                    binding.Scope));
        }

        return evaluated.ToImmutable();
    }

    private static SkeletonPose ApplyIkConstraints(
        SkeletonPose pose,
        ImmutableArray<TwoBoneIkConstraint> constraints,
        IkConstraintScope scope)
    {
        SkeletonPose result = pose;
        foreach (TwoBoneIkConstraint constraint in constraints.Where(
                     constraint => constraint.Scope == scope))
        {
            result = ApplyIkConstraint(result, constraint);
        }

        return result;
    }

    private static SkeletonPose ApplyIkConstraint(
        SkeletonPose pose,
        TwoBoneIkConstraint constraint)
    {
        ValidateIkChain(pose.Rig, constraint);
        if (constraint.Weight <= 0.0)
        {
            return pose;
        }

        Vector3D rootPosition =
            pose.GlobalMatrices[constraint.RootBoneIndex].Translation;
        Vector3D jointPosition =
            pose.GlobalMatrices[constraint.JointBoneIndex].Translation;
        Vector3D endPosition =
            pose.GlobalMatrices[constraint.EndBoneIndex].Translation;
        TwoBoneIkSolution solution = TwoBoneIkSolver.Solve(
            rootPosition,
            jointPosition,
            endPosition,
            constraint.Target,
            constraint.Pole);

        TransformTRS rootGlobal =
            pose.GlobalMatrices[constraint.RootBoneIndex].Decompose();
        QuaternionD rootDelta = QuaternionD.FromToRotation(
            jointPosition - rootPosition,
            solution.JointPosition - solution.RootPosition);
        QuaternionD desiredRootGlobal =
            (rootDelta * rootGlobal.Rotation).Normalized();
        QuaternionD desiredRootLocal = GlobalToLocalRotation(
            pose,
            constraint.RootBoneIndex,
            desiredRootGlobal);

        TransformTRS rootLocal = pose.LocalTransforms[constraint.RootBoneIndex];
        TransformTRS blendedRoot = rootLocal with
        {
            Rotation = QuaternionD.Slerp(
                rootLocal.Rotation,
                desiredRootLocal,
                constraint.Weight),
        };
        SkeletonPose rootAdjusted = pose.WithLocalTransform(
            constraint.RootBoneIndex,
            blendedRoot);

        Vector3D adjustedJoint =
            rootAdjusted.GlobalMatrices[constraint.JointBoneIndex].Translation;
        Vector3D adjustedEnd =
            rootAdjusted.GlobalMatrices[constraint.EndBoneIndex].Translation;
        Vector3D desiredLowerDirection =
            solution.EndPosition - solution.JointPosition;
        QuaternionD jointDelta = QuaternionD.FromToRotation(
            adjustedEnd - adjustedJoint,
            desiredLowerDirection);
        TransformTRS jointGlobal =
            rootAdjusted.GlobalMatrices[constraint.JointBoneIndex].Decompose();
        QuaternionD desiredJointGlobal =
            (jointDelta * jointGlobal.Rotation).Normalized();
        QuaternionD desiredJointLocal = GlobalToLocalRotation(
            rootAdjusted,
            constraint.JointBoneIndex,
            desiredJointGlobal);

        TransformTRS jointLocal =
            rootAdjusted.LocalTransforms[constraint.JointBoneIndex];
        TransformTRS blendedJoint = jointLocal with
        {
            Rotation = QuaternionD.Slerp(
                jointLocal.Rotation,
                desiredJointLocal,
                constraint.Weight),
        };
        SkeletonPose result = rootAdjusted.WithLocalTransform(
            constraint.JointBoneIndex,
            blendedJoint);
        if (constraint.EndOrientation is not QuaternionD endOrientation)
        {
            return result;
        }

        QuaternionD desiredEndLocal = GlobalToLocalRotation(
            result,
            constraint.EndBoneIndex,
            endOrientation);
        TransformTRS endLocal =
            result.LocalTransforms[constraint.EndBoneIndex];
        return result.WithLocalTransform(
            constraint.EndBoneIndex,
            endLocal with
            {
                Rotation = QuaternionD.Slerp(
                    endLocal.Rotation,
                    desiredEndLocal,
                    constraint.Weight),
            });
    }

    private static QuaternionD GlobalToLocalRotation(
        SkeletonPose pose,
        int boneIndex,
        QuaternionD globalRotation)
    {
        int parentIndex = pose.Rig.Bones[boneIndex].ParentIndex;
        if (parentIndex < 0)
        {
            return globalRotation;
        }

        QuaternionD parentGlobal =
            pose.GlobalMatrices[parentIndex].Decompose().Rotation;
        return (parentGlobal.Inverse() * globalRotation).Normalized();
    }

    private static void ValidateIkChain(
        RigDefinition rig,
        TwoBoneIkConstraint constraint)
    {
        if ((uint)constraint.RootBoneIndex >= (uint)rig.BoneCount ||
            (uint)constraint.JointBoneIndex >= (uint)rig.BoneCount ||
            (uint)constraint.EndBoneIndex >= (uint)rig.BoneCount)
        {
            throw new InvalidOperationException("An IK constraint refers to a missing bone.");
        }

        if (rig.Bones[constraint.JointBoneIndex].ParentIndex !=
                constraint.RootBoneIndex ||
            rig.Bones[constraint.EndBoneIndex].ParentIndex !=
                constraint.JointBoneIndex)
        {
            throw new InvalidOperationException(
                "A two-bone IK constraint must describe a direct root/joint/end hierarchy.");
        }
    }

    private static void EnsureSameRigContract(
        RigDefinition source,
        RigDefinition target)
    {
        bool sameContract =
            source.BoneCount == target.BoneCount &&
            source.Bones.Zip(
                    target.Bones,
                    static (sourceBone, targetBone) =>
                        sourceBone.ParentIndex == targetBone.ParentIndex &&
                        string.Equals(
                            sourceBone.Name,
                            targetBone.Name,
                            StringComparison.OrdinalIgnoreCase))
                .All(static matches => matches);
        if (!sameContract)
        {
            throw new InvalidOperationException(
                "A retarget map is required when source and target rig contracts differ.");
        }
    }
}
