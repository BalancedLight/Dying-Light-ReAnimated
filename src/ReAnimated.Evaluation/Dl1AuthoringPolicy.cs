using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Evaluation;

public enum Dl1TargetTrackSource
{
    Evaluated,
    TargetBind,
}

/// <summary>
/// Immutable ownership of one target-rig track at the DL1 authoring boundary.
/// Target-only rows retain their own bind-local transform and therefore still
/// inherit motion from an evaluated parent.
/// </summary>
public sealed record Dl1TargetTrackPolicy
{
    public Dl1TargetTrackPolicy(
        int targetBoneIndex,
        int? sourceBoneIndex,
        Dl1TargetTrackSource source,
        bool requiredForExport,
        bool isHelper)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetBoneIndex);
        if (sourceBoneIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceBoneIndex));
        }

        if (source == Dl1TargetTrackSource.Evaluated &&
            !sourceBoneIndex.HasValue)
        {
            throw new ArgumentException(
                "An evaluated DL1 target track requires a source bone.",
                nameof(sourceBoneIndex));
        }

        if (source == Dl1TargetTrackSource.TargetBind &&
            sourceBoneIndex.HasValue)
        {
            throw new ArgumentException(
                "A target-bind DL1 track cannot also name a source bone.",
                nameof(sourceBoneIndex));
        }

        TargetBoneIndex = targetBoneIndex;
        SourceBoneIndex = sourceBoneIndex;
        Source = source;
        RequiredForExport = requiredForExport;
        IsHelper = isHelper;
    }

    public int TargetBoneIndex { get; }

    public int? SourceBoneIndex { get; }

    public Dl1TargetTrackSource Source { get; }

    public bool RequiredForExport { get; }

    public bool IsHelper { get; }
}

/// <summary>
/// Rig-resolved form of DL1's legacy inplace, Bip01, and motion-accumulator
/// policies. The target root is selected independently from the policy name.
/// </summary>
public sealed record Dl1RootMotionPolicy
{
    public const uint MotionAccumulatorDescriptor = 0xCCC3CDDF;

    public Dl1RootMotionPolicy(
        AnimationRootMode mode,
        int targetRootBoneIndex,
        int? motionAccumulatorBoneIndex,
        Vector3D worldUpAxis)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetRootBoneIndex);
        if (motionAccumulatorBoneIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(motionAccumulatorBoneIndex));
        }

        if (!worldUpAxis.IsFinite ||
            !worldUpAxis.TryNormalize(out Vector3D normalized))
        {
            throw new ArgumentException(
                "DL1 world-up must be a finite non-zero vector.",
                nameof(worldUpAxis));
        }

        if (motionAccumulatorBoneIndex == targetRootBoneIndex)
        {
            throw new ArgumentException(
                "The DL1 skeletal root and motion accumulator must be distinct tracks.",
                nameof(motionAccumulatorBoneIndex));
        }

        Mode = mode;
        TargetRootBoneIndex = targetRootBoneIndex;
        MotionAccumulatorBoneIndex = motionAccumulatorBoneIndex;
        WorldUpAxis = normalized;
    }

    public AnimationRootMode Mode { get; }

    public int TargetRootBoneIndex { get; }

    public int? MotionAccumulatorBoneIndex { get; }

    public Vector3D WorldUpAxis { get; }
}

/// <summary>
/// Complete immutable DL1 policy bound to a source rig, target rig, and
/// retarget map. It is created once with an animation document and reused for
/// every authoritative preview/export evaluation.
/// </summary>
public sealed class Dl1AuthoringPolicy
{
    public Dl1AuthoringPolicy(
        string sourceRigId,
        string targetRigId,
        Dl1RootMotionPolicy rootMotion,
        IEnumerable<Dl1TargetTrackPolicy> targetTracks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRigId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRigId);
        ArgumentNullException.ThrowIfNull(rootMotion);
        ArgumentNullException.ThrowIfNull(targetTracks);

        ImmutableArray<Dl1TargetTrackPolicy> tracks =
            targetTracks.ToImmutableArray();
        if (tracks.IsEmpty)
        {
            throw new ArgumentException(
                "A DL1 authoring policy requires target-track ownership.",
                nameof(targetTracks));
        }

        if (tracks
            .Select(static track => track.TargetBoneIndex)
            .Distinct()
            .Count() != tracks.Length)
        {
            throw new ArgumentException(
                "DL1 target-track policy contains duplicate target bones.",
                nameof(targetTracks));
        }

        SourceRigId = sourceRigId;
        TargetRigId = targetRigId;
        RootMotion = rootMotion;
        TargetTracks = tracks;
    }

    public string SourceRigId { get; }

    public string TargetRigId { get; }

    public Dl1RootMotionPolicy RootMotion { get; }

    public ImmutableArray<Dl1TargetTrackPolicy> TargetTracks { get; }

    public ImmutableArray<int> TargetBindBoneIndices =>
        TargetTracks
            .Where(static track =>
                track.Source == Dl1TargetTrackSource.TargetBind)
            .Select(static track => track.TargetBoneIndex)
            .ToImmutableArray();

    public static Dl1AuthoringPolicy Create(
        RigDefinition sourceRig,
        RigDefinition targetRig,
        RetargetMap? retargetMap,
        AnimationRootMode rootMode,
        string? targetRootBoneName = null,
        Vector3D? worldUpAxis = null)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(targetRig);
        if (retargetMap is not null &&
            (!string.Equals(
                 retargetMap.SourceRigId,
                 sourceRig.Id,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 retargetMap.TargetRigId,
                 targetRig.Id,
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The DL1 policy retarget map is not bound to the selected rigs.",
                nameof(retargetMap));
        }

        int targetRootBoneIndex = ResolveTargetRoot(
            targetRig,
            targetRootBoneName);
        int? accumulatorBoneIndex = ResolveMotionAccumulator(targetRig);
        ValidateMotionAccumulator(
            targetRig,
            targetRootBoneIndex,
            accumulatorBoneIndex,
            rootMode);

        ImmutableDictionary<int, BoneMapEntry> entriesByTarget =
            retargetMap?.Entries.ToImmutableDictionary(
                static entry => entry.TargetBoneIndex) ??
            ImmutableDictionary<int, BoneMapEntry>.Empty;
        bool directRigEvaluation = retargetMap is null;
        var tracks = ImmutableArray.CreateBuilder<Dl1TargetTrackPolicy>(
            targetRig.BoneCount);
        foreach (BoneDefinition targetBone in targetRig.Bones)
        {
            bool isHelper = targetBone.Kind is
                BoneKind.Helper or BoneKind.Camera or BoneKind.Prop;
            if (isHelper &&
                targetBone.RequiredForExport &&
                targetBone.DescriptorHash is null)
            {
                throw new InvalidOperationException(
                    $"Required DL1 helper '{targetBone.Name}' has no authoritative descriptor.");
            }

            if (directRigEvaluation)
            {
                if (targetBone.Index >= sourceRig.BoneCount)
                {
                    throw new InvalidOperationException(
                        "Direct DL1 evaluation requires matching source and target rig contracts.");
                }

                tracks.Add(
                    new(
                        targetBone.Index,
                        targetBone.Index,
                        Dl1TargetTrackSource.Evaluated,
                        targetBone.RequiredForExport,
                        isHelper));
            }
            else if (entriesByTarget.TryGetValue(
                         targetBone.Index,
                         out BoneMapEntry? entry))
            {
                tracks.Add(
                    new(
                        targetBone.Index,
                        entry.SourceBoneIndex,
                        Dl1TargetTrackSource.Evaluated,
                        targetBone.RequiredForExport,
                        isHelper));
            }
            else
            {
                tracks.Add(
                    new(
                        targetBone.Index,
                        null,
                        Dl1TargetTrackSource.TargetBind,
                        targetBone.RequiredForExport,
                        isHelper));
            }
        }

        return new(
            sourceRig.Id,
            targetRig.Id,
            new Dl1RootMotionPolicy(
                rootMode,
                targetRootBoneIndex,
                accumulatorBoneIndex,
                worldUpAxis ?? Vector3D.UnitY),
            tracks.MoveToImmutable());
    }

    public void ValidateFor(
        RigDefinition sourceRig,
        RigDefinition targetRig)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(targetRig);
        if (!string.Equals(
                SourceRigId,
                sourceRig.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                TargetRigId,
                targetRig.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The DL1 authoring policy is not bound to the evaluated rigs.");
        }

        if (TargetTracks.Length != targetRig.BoneCount ||
            TargetTracks
                .OrderBy(static track => track.TargetBoneIndex)
                .Select(static track => track.TargetBoneIndex)
                .Where((targetBoneIndex, index) => targetBoneIndex != index)
                .Any())
        {
            throw new InvalidOperationException(
                "The DL1 authoring policy does not cover the complete target rig.");
        }

        if (RootMotion.TargetRootBoneIndex >= targetRig.BoneCount ||
            RootMotion.MotionAccumulatorBoneIndex >= targetRig.BoneCount)
        {
            throw new InvalidOperationException(
                "The DL1 root-motion policy refers outside the target rig.");
        }

        foreach (Dl1TargetTrackPolicy track in TargetTracks)
        {
            if (track.SourceBoneIndex >= sourceRig.BoneCount)
            {
                throw new InvalidOperationException(
                    "A DL1 target-track policy refers outside the source rig.");
            }

            BoneDefinition targetBone =
                targetRig.Bones[track.TargetBoneIndex];
            bool isHelper = targetBone.Kind is
                BoneKind.Helper or BoneKind.Camera or BoneKind.Prop;
            if (track.RequiredForExport != targetBone.RequiredForExport ||
                track.IsHelper != isHelper)
            {
                throw new InvalidOperationException(
                    $"DL1 target-track ownership for '{targetBone.Name}' does not match its rig contract.");
            }

            if (isHelper &&
                targetBone.RequiredForExport &&
                targetBone.DescriptorHash is null)
            {
                throw new InvalidOperationException(
                    $"Required DL1 helper '{targetBone.Name}' has no authoritative descriptor.");
            }
        }

        ValidateMotionAccumulator(
            targetRig,
            RootMotion.TargetRootBoneIndex,
            RootMotion.MotionAccumulatorBoneIndex,
            RootMotion.Mode);
    }

    private static int ResolveTargetRoot(
        RigDefinition rig,
        string? targetRootBoneName)
    {
        if (!string.IsNullOrWhiteSpace(targetRootBoneName))
        {
            int explicitIndex = rig.GetBoneIndex(targetRootBoneName);
            return explicitIndex >= 0
                ? explicitIndex
                : throw new InvalidOperationException(
                    $"DL1 target root '{targetRootBoneName}' is absent from rig '{rig.Id}'.");
        }

        BoneDefinition? semantic = rig.Bones.FirstOrDefault(
            static bone =>
                string.Equals(
                    bone.SemanticRole,
                    "root.skeletal",
                    StringComparison.OrdinalIgnoreCase));
        if (semantic is not null)
        {
            return semantic.Index;
        }

        int bip01 = rig.GetBoneIndex("Bip01");
        if (bip01 >= 0)
        {
            return bip01;
        }

        BoneDefinition[] roots = rig.Bones
            .Where(static bone =>
                bone.ParentIndex < 0 &&
                bone.DescriptorHash !=
                    Dl1RootMotionPolicy.MotionAccumulatorDescriptor)
            .ToArray();
        return roots.Length switch
        {
            1 => roots[0].Index,
            0 => throw new InvalidOperationException(
                $"Rig '{rig.Id}' has no DL1 skeletal-root candidate."),
            _ => throw new InvalidOperationException(
                $"Rig '{rig.Id}' has multiple root tracks; select the DL1 skeletal root explicitly."),
        };
    }

    private static int? ResolveMotionAccumulator(RigDefinition rig)
    {
        BoneDefinition[] matches = rig.Bones
            .Where(static bone =>
                bone.DescriptorHash ==
                    Dl1RootMotionPolicy.MotionAccumulatorDescriptor)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0].Index,
            _ => throw new InvalidOperationException(
                "Rig contains duplicate DL1 motion-accumulator descriptors."),
        };
    }

    private static void ValidateMotionAccumulator(
        RigDefinition rig,
        int rootBoneIndex,
        int? accumulatorBoneIndex,
        AnimationRootMode rootMode)
    {
        if (!accumulatorBoneIndex.HasValue)
        {
            // DL1 samples 0xCCC3CDDF through the animation accumulator stage;
            // compact retail mesh skeletons are not required to own that row.
            return;
        }

        BoneDefinition accumulator = rig.Bones[accumulatorBoneIndex.Value];
        if (accumulator.Index == rootBoneIndex ||
            accumulator.ParentIndex >= 0 ||
            accumulator.Kind != BoneKind.Helper ||
            !accumulator.RequiredForExport)
        {
            throw new InvalidOperationException(
                "DL1 descriptor 0xCCC3CDDF must be a distinct, root-level, required helper track.");
        }
    }
}

public sealed record Dl1AuthoringPolicyResult(
    SkeletonPose ExportablePose,
    ImmutableArray<int> PreservedTargetBindBoneIndices);

/// <summary>
/// Applies DL1-only track ownership and root-motion semantics after authored IK.
/// Preview-only edits and procedural stages must never be passed to this type.
/// </summary>
public static class Dl1AuthoringPolicyEvaluator
{
    public static Dl1AuthoringPolicyResult Apply(
        RigDefinition sourceRig,
        SkeletonPose authoredPose,
        SkeletonPose firstAuthoredPose,
        Dl1AuthoringPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(authoredPose);
        ArgumentNullException.ThrowIfNull(firstAuthoredPose);
        ArgumentNullException.ThrowIfNull(policy);
        policy.ValidateFor(sourceRig, authoredPose.Rig);
        EnsureSameRig(authoredPose.Rig, firstAuthoredPose.Rig);

        ImmutableArray<int> targetBindIndices =
            policy.TargetBindBoneIndices;
        SkeletonPose current = PreserveTargetBindLocals(
            authoredPose,
            targetBindIndices);
        SkeletonPose first = PreserveTargetBindLocals(
            firstAuthoredPose,
            targetBindIndices);
        Dl1RootMotionPolicy rootMotion = policy.RootMotion;
        current = rootMotion.Mode switch
        {
            AnimationRootMode.Recorded => current,
            AnimationRootMode.Bip01 =>
                HoldAccumulatorAtBind(current, rootMotion),
            AnimationRootMode.InPlace =>
                ApplyInPlace(current, first, rootMotion),
            AnimationRootMode.MotionAccumulator =>
                ApplyMotionAccumulator(current, first, rootMotion),
            _ => throw new InvalidOperationException(
                $"Unsupported DL1 root-motion mode '{rootMotion.Mode}'."),
        };
        return new(current, targetBindIndices);
    }

    private static SkeletonPose PreserveTargetBindLocals(
        SkeletonPose pose,
        ImmutableArray<int> targetBindIndices)
    {
        ImmutableArray<TransformTRS> locals = pose.LocalTransforms;
        foreach (int boneIndex in targetBindIndices)
        {
            locals = locals.SetItem(
                boneIndex,
                pose.Rig.Bones[boneIndex].LocalBindPose);
        }

        return targetBindIndices.IsEmpty
            ? pose
            : new SkeletonPose(pose.Rig, locals);
    }

    private static SkeletonPose HoldAccumulatorAtBind(
        SkeletonPose pose,
        Dl1RootMotionPolicy policy) =>
        policy.MotionAccumulatorBoneIndex is int accumulatorBoneIndex
            ? pose.WithLocalTransform(
                accumulatorBoneIndex,
                pose.Rig.Bones[accumulatorBoneIndex].LocalBindPose)
            : pose;

    private static SkeletonPose ApplyInPlace(
        SkeletonPose pose,
        SkeletonPose firstPose,
        Dl1RootMotionPolicy policy)
    {
        SkeletonPose corrected = ApplyRootCorrection(
            pose,
            firstPose,
            policy,
            useBindGlobalTranslation: true,
            out _,
            out _);
        return policy.MotionAccumulatorBoneIndex is int accumulatorBoneIndex
            ? corrected.WithLocalTransform(
                accumulatorBoneIndex,
                TransformTRS.Identity)
            : corrected;
    }

    private static SkeletonPose ApplyMotionAccumulator(
        SkeletonPose pose,
        SkeletonPose firstPose,
        Dl1RootMotionPolicy policy)
    {
        SkeletonPose corrected = ApplyRootCorrection(
            pose,
            firstPose,
            policy,
            useBindGlobalTranslation: false,
            out Vector3D planarDisplacement,
            out QuaternionD heading);
        return policy.MotionAccumulatorBoneIndex is int accumulatorBoneIndex
            ? corrected.WithLocalTransform(
                accumulatorBoneIndex,
                new TransformTRS(
                    planarDisplacement,
                    heading,
                    Vector3D.One))
            : corrected;
    }

    private static SkeletonPose ApplyRootCorrection(
        SkeletonPose pose,
        SkeletonPose firstPose,
        Dl1RootMotionPolicy policy,
        bool useBindGlobalTranslation,
        out Vector3D planarDisplacement,
        out QuaternionD heading)
    {
        int rootBoneIndex = policy.TargetRootBoneIndex;
        Vector3D currentPosition =
            pose.GlobalMatrices[rootBoneIndex].Translation;
        Vector3D firstPosition =
            firstPose.GlobalMatrices[rootBoneIndex].Translation;
        Vector3D displacement = currentPosition - firstPosition;
        Vector3D up = policy.WorldUpAxis;
        planarDisplacement =
            displacement -
            (up * Vector3D.Dot(displacement, up));

        QuaternionD currentRotation =
            ComputeGlobalRotation(pose, rootBoneIndex);
        QuaternionD firstRotation =
            ComputeGlobalRotation(firstPose, rootBoneIndex);
        QuaternionD worldDelta =
            (currentRotation * firstRotation.Inverse()).Normalized();
        heading = ExtractHeadingTwist(worldDelta, up);
        QuaternionD correctedGlobalRotation =
            (heading.Inverse() * currentRotation).Normalized();

        Vector3D correctedGlobalPosition = useBindGlobalTranslation
            ? pose.Rig.CreateBindPose()
                .GlobalMatrices[rootBoneIndex]
                .Translation
            : currentPosition - planarDisplacement;
        int parentBoneIndex =
            pose.Rig.Bones[rootBoneIndex].ParentIndex;
        Vector3D correctedLocalPosition;
        QuaternionD correctedLocalRotation;
        if (parentBoneIndex < 0)
        {
            correctedLocalPosition = correctedGlobalPosition;
            correctedLocalRotation = correctedGlobalRotation;
        }
        else
        {
            correctedLocalPosition =
                pose.GlobalMatrices[parentBoneIndex]
                    .InvertedAffine()
                    .TransformPoint(correctedGlobalPosition);
            QuaternionD parentGlobalRotation =
                ComputeGlobalRotation(pose, parentBoneIndex);
            correctedLocalRotation =
                (parentGlobalRotation.Inverse() *
                 correctedGlobalRotation).Normalized();
        }

        TransformTRS currentLocal =
            pose.LocalTransforms[rootBoneIndex];
        return pose.WithLocalTransform(
            rootBoneIndex,
            currentLocal with
            {
                Translation = correctedLocalPosition,
                Rotation = correctedLocalRotation,
            });
    }

    private static QuaternionD ComputeGlobalRotation(
        SkeletonPose pose,
        int boneIndex)
    {
        var chain = new Stack<int>();
        int current = boneIndex;
        while (current >= 0)
        {
            chain.Push(current);
            current = pose.Rig.Bones[current].ParentIndex;
        }

        QuaternionD rotation = QuaternionD.Identity;
        while (chain.TryPop(out int chainBoneIndex))
        {
            rotation =
                (rotation *
                 pose.LocalTransforms[chainBoneIndex].Rotation).Normalized();
        }

        return rotation;
    }

    private static QuaternionD ExtractHeadingTwist(
        QuaternionD worldDelta,
        Vector3D up)
    {
        QuaternionD unit = worldDelta.Normalized();
        Vector3D vector = new(unit.X, unit.Y, unit.Z);
        Vector3D projected = up * Vector3D.Dot(vector, up);
        var twist = new QuaternionD(
            projected.X,
            projected.Y,
            projected.Z,
            unit.W);
        return twist.LengthSquared <= 1e-20
            ? QuaternionD.Identity
            : twist.Normalized();
    }

    private static void EnsureSameRig(
        RigDefinition current,
        RigDefinition first)
    {
        if (!ReferenceEquals(current, first) &&
            (!string.Equals(current.Id, first.Id, StringComparison.Ordinal) ||
             current.BoneCount != first.BoneCount))
        {
            throw new InvalidOperationException(
                "DL1 policy reference and current poses use different rigs.");
        }
    }
}
