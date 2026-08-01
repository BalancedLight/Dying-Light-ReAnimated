using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Retargeting;

/// <summary>
/// Reconstructs mapped humanoid limb and torso rotations from anatomical joint
/// directions. This is the C# form of the editor-validated DL1 absolute
/// full-body retarget used by the Python oracle: source directions are
/// expressed in the animated source body frame, moved into the corresponding
/// target body frame, then resolved back through the actual target hierarchy.
/// </summary>
internal static class HumanoidAnatomicalRetargeter
{
    private const double DirectionEpsilon = 1.0e-10;

    private static readonly (string Bone, string Child)[] AxialChains =
    [
        ("body.spine.0", "body.spine.1"),
        ("body.spine.1", "body.spine.2"),
        ("body.spine.2", "body.neck.0"),
        ("body.spine.3", "body.neck.0"),
        ("body.neck.0", "body.head"),
        ("body.neck.1", "body.head"),
    ];

    private static readonly (string Root, string Mid, string End)[] LimbChains =
    [
        ("arm.left.upper", "arm.left.lower", "hand.left"),
        ("arm.right.upper", "arm.right.lower", "hand.right"),
        ("leg.left.upper", "leg.left.lower", "foot.left"),
        ("leg.right.upper", "leg.right.lower", "foot.right"),
    ];

    private static readonly
        (string Root, string Mid, string End, string Terminal)[]
        TerminalChains =
        [
            (
                "arm.left.upper",
                "arm.left.lower",
                "hand.left",
                "finger.left.middle.1"),
            (
                "arm.right.upper",
                "arm.right.lower",
                "hand.right",
                "finger.right.middle.1"),
            (
                "leg.left.upper",
                "leg.left.lower",
                "foot.left",
                "toe.left"),
            (
                "leg.right.upper",
                "leg.right.lower",
                "foot.right",
                "toe.right"),
        ];

    private static readonly string[] FingerDigits =
    [
        "thumb",
        "index",
        "middle",
        "ring",
        "little",
    ];

    internal static IReadOnlyDictionary<int, QuaternionD>
        EvaluateDesiredGlobalRotations(
            SkeletonPose sourcePose,
            SkeletonPose targetBind,
            RetargetMap map)
    {
        ArgumentNullException.ThrowIfNull(sourcePose);
        ArgumentNullException.ThrowIfNull(targetBind);
        ArgumentNullException.ThrowIfNull(map);

        Dictionary<string, BoneMapEntry> rowsByRole =
            BuildMappedRowsByTargetRole(
                sourcePose.Rig,
                targetBind.Rig,
                map);
        if (!TryCreateBodyFrames(
                sourcePose,
                sourcePose.Rig.CreateBindPose(),
                targetBind,
                rowsByRole,
                out QuaternionD sourceBody,
                out QuaternionD sourceBindBody,
                out QuaternionD targetBindBody,
                out QuaternionD targetBody,
                out Vector3D sourceBodyRight,
                out Vector3D targetBindRight))
        {
            return new Dictionary<int, QuaternionD>();
        }

        QuaternionD[] targetBindGlobals =
            ComputeGlobalRotations(targetBind);
        var desired = new Dictionary<int, QuaternionD>();

        AddPelvis(
            targetBind,
            rowsByRole,
            targetBindGlobals,
            targetBindBody,
            targetBody,
            desired);
        AddAxialChains(
            sourcePose,
            targetBind,
            rowsByRole,
            targetBindGlobals,
            sourceBody,
            targetBody,
            sourceBodyRight,
            targetBindRight,
            desired);
        AddHead(
            sourcePose,
            sourcePose.Rig.CreateBindPose(),
            targetBind,
            rowsByRole,
            targetBindGlobals,
            sourceBody,
            targetBody,
            sourceBodyRight,
            targetBindRight,
            desired);
        AddClavicles(
            sourcePose,
            targetBind,
            rowsByRole,
            targetBindGlobals,
            sourceBody,
            targetBindBody,
            targetBody,
            desired);
        AddLimbs(
            sourcePose,
            targetBind,
            rowsByRole,
            targetBindGlobals,
            sourceBody,
            targetBody,
            desired);
        AddTerminals(
            sourcePose,
            targetBind,
            rowsByRole,
            targetBindGlobals,
            sourceBody,
            targetBody,
            desired);
        AddFingers(
            sourcePose,
            targetBind,
            rowsByRole,
            targetBindGlobals,
            desired);
        return desired;
    }

    internal static bool SupportsTargetRole(string? value)
    {
        string? role =
            HumanoidBoneSemanticClassifier.Classify(value)?.Role;
        if (role is null)
        {
            return false;
        }

        return role == "body.pelvis" ||
               role.StartsWith("body.spine.", StringComparison.Ordinal) ||
               role.StartsWith("body.neck.", StringComparison.Ordinal) ||
               role == "body.head" ||
               role.StartsWith("arm.", StringComparison.Ordinal) &&
               (role.EndsWith(".clavicle", StringComparison.Ordinal) ||
                role.EndsWith(".upper", StringComparison.Ordinal) ||
                role.EndsWith(".lower", StringComparison.Ordinal)) ||
               role is "hand.left" or "hand.right" ||
               role.StartsWith("leg.", StringComparison.Ordinal) &&
               (role.EndsWith(".upper", StringComparison.Ordinal) ||
                role.EndsWith(".lower", StringComparison.Ordinal)) ||
               role is "foot.left" or "foot.right" ||
               role.StartsWith("finger.", StringComparison.Ordinal);
    }

    internal static bool KeepsBindWhenUnsolved(string? value)
    {
        string? role =
            HumanoidBoneSemanticClassifier.Classify(value)?.Role;
        return role == "body.head" ||
               role is "hand.left" or "hand.right" ||
               role is "foot.left" or "foot.right" ||
               role?.StartsWith(
                   "finger.",
                   StringComparison.Ordinal) == true;
    }

    private static Dictionary<string, BoneMapEntry>
        BuildMappedRowsByTargetRole(
            RigDefinition source,
            RigDefinition target,
            RetargetMap map)
    {
        var rows = new Dictionary<string, BoneMapEntry>(
            StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (BoneMapEntry entry in map.Entries)
        {
            if (entry.MappingKind != RetargetMappingKind.Bone ||
                entry.SourceBoneIndex >= source.BoneCount ||
                entry.TargetBoneIndex >= target.BoneCount)
            {
                continue;
            }

            string? role = HumanoidBoneSemanticClassifier.Classify(
                target.Bones[entry.TargetBoneIndex].SemanticRole ??
                target.Bones[entry.TargetBoneIndex].Name)?.Role;
            if (role is null || ambiguous.Contains(role))
            {
                continue;
            }

            if (!rows.TryAdd(role, entry))
            {
                rows.Remove(role);
                ambiguous.Add(role);
            }
        }

        return rows;
    }

    private static bool TryCreateBodyFrames(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        out QuaternionD sourceBody,
        out QuaternionD sourceBindBody,
        out QuaternionD targetBindBody,
        out QuaternionD targetBody,
        out Vector3D sourceRight,
        out Vector3D targetRight)
    {
        sourceBody = QuaternionD.Identity;
        sourceBindBody = QuaternionD.Identity;
        targetBindBody = QuaternionD.Identity;
        targetBody = QuaternionD.Identity;
        sourceRight = Vector3D.Zero;
        targetRight = Vector3D.Zero;

        if (!TryGetRow(rows, "arm.left.clavicle", out BoneMapEntry left) ||
            !TryGetRow(rows, "arm.right.clavicle", out BoneMapEntry right) ||
            !TryGetRow(rows, "body.pelvis", out BoneMapEntry pelvis) ||
            !TryGetHighestSpineRow(rows, out BoneMapEntry spine))
        {
            return false;
        }

        Vector3D sourcePoseRight =
            Position(sourcePose, right.SourceBoneIndex) -
            Position(sourcePose, left.SourceBoneIndex);
        Vector3D sourcePoseUp =
            Position(sourcePose, spine.SourceBoneIndex) -
            Position(sourcePose, pelvis.SourceBoneIndex);
        Vector3D sourceBindRight =
            Position(sourceBind, right.SourceBoneIndex) -
            Position(sourceBind, left.SourceBoneIndex);
        Vector3D sourceBindUp =
            Position(sourceBind, spine.SourceBoneIndex) -
            Position(sourceBind, pelvis.SourceBoneIndex);
        Vector3D targetPoseRight =
            Position(targetBind, right.TargetBoneIndex) -
            Position(targetBind, left.TargetBoneIndex);
        Vector3D targetPoseUp =
            Position(targetBind, spine.TargetBoneIndex) -
            Position(targetBind, pelvis.TargetBoneIndex);

        if (!TryFrame(
                sourcePoseRight,
                sourcePoseUp,
                out sourceBody) ||
            !TryFrame(
                sourceBindRight,
                sourceBindUp,
                out sourceBindBody) ||
            !TryFrame(
                targetPoseRight,
                targetPoseUp,
                out targetBindBody))
        {
            return false;
        }

        targetBody = (
            targetBindBody *
            sourceBindBody.Inverse() *
            sourceBody
        ).Normalized();
        sourceRight = sourcePoseRight.Normalized();
        targetRight = targetPoseRight.Normalized();
        return true;
    }

    private static void AddPelvis(
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD targetBindBody,
        QuaternionD targetBody,
        Dictionary<int, QuaternionD> desired)
    {
        if (!TryGetRow(rows, "body.pelvis", out BoneMapEntry pelvis) ||
            pelvis.TransferPolicy !=
                RetargetTransferPolicy.AnatomicalDirection)
        {
            return;
        }

        QuaternionD rollOffset = (
            targetBindBody.Inverse() *
            targetBindGlobals[pelvis.TargetBoneIndex]
        ).Normalized();
        desired[pelvis.TargetBoneIndex] =
            (targetBody * rollOffset).Normalized();
    }

    private static void AddAxialChains(
        SkeletonPose sourcePose,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD sourceBody,
        QuaternionD targetBody,
        Vector3D sourceRight,
        Vector3D targetBindRight,
        Dictionary<int, QuaternionD> desired)
    {
        foreach ((string boneRole, string childRole) in AxialChains)
        {
            if (!TryGetRow(rows, boneRole, out BoneMapEntry bone) ||
                !TryGetRow(rows, childRole, out BoneMapEntry child) ||
                bone.TransferPolicy !=
                    RetargetTransferPolicy.AnatomicalDirection)
            {
                continue;
            }

            Vector3D sourceDirection =
                Position(sourcePose, child.SourceBoneIndex) -
                Position(sourcePose, bone.SourceBoneIndex);
            Vector3D targetBindDirection =
                Position(targetBind, child.TargetBoneIndex) -
                Position(targetBind, bone.TargetBoneIndex);
            if (!TryFrame(
                    sourceDirection,
                    sourceRight,
                    out QuaternionD sourceFrame) ||
                !TryFrame(
                    targetBindDirection,
                    targetBindRight,
                    out QuaternionD targetBindFrame))
            {
                continue;
            }

            QuaternionD sourceRelative =
                (sourceBody.Inverse() * sourceFrame).Normalized();
            QuaternionD targetFrame =
                (targetBody * sourceRelative).Normalized();
            QuaternionD rollOffset = (
                targetBindFrame.Inverse() *
                targetBindGlobals[bone.TargetBoneIndex]
            ).Normalized();
            desired[bone.TargetBoneIndex] =
                (targetFrame * rollOffset).Normalized();
        }
    }

    private static void AddHead(
        SkeletonPose sourcePose,
        SkeletonPose sourceBind,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD sourceBody,
        QuaternionD targetBody,
        Vector3D sourceRight,
        Vector3D targetRight,
        Dictionary<int, QuaternionD> desired)
    {
        if (!TryGetRow(rows, "body.head", out BoneMapEntry head) ||
            head.TransferPolicy !=
                RetargetTransferPolicy.AnatomicalDirection)
        {
            return;
        }

        if (!TryGetRow(
                rows,
                "body.neck.0",
                out BoneMapEntry neck) &&
            !TryGetRow(
                rows,
                "body.neck.1",
                out neck))
        {
            return;
        }

        int targetEnd = FindHeadEnd(
            targetBind.Rig,
            head.TargetBoneIndex);

        Vector3D sourceDirection;
        int sourceEnd = FindHeadEnd(
            sourcePose.Rig,
            head.SourceBoneIndex);
        if (sourceEnd >= 0)
        {
            sourceDirection =
                Position(sourcePose, sourceEnd) -
                Position(sourcePose, head.SourceBoneIndex);
        }
        else
        {
            QuaternionD[] sourcePoseGlobals =
                ComputeGlobalRotations(sourcePose);
            QuaternionD[] sourceBindGlobals =
                ComputeGlobalRotations(sourceBind);
            Vector3D incomingBindDirection =
                Position(sourceBind, head.SourceBoneIndex) -
                Position(sourceBind, neck.SourceBoneIndex);
            if (!incomingBindDirection.TryNormalize(
                    out Vector3D incomingBind,
                    DirectionEpsilon))
            {
                return;
            }

            Vector3D localHeadAxis =
                sourceBindGlobals[head.SourceBoneIndex]
                    .Inverse()
                    .Rotate(incomingBind);
            sourceDirection =
                sourcePoseGlobals[head.SourceBoneIndex]
                    .Rotate(localHeadAxis);
        }

        Vector3D targetBindDirection = targetEnd >= 0
            ? Position(targetBind, targetEnd) -
              Position(targetBind, head.TargetBoneIndex)
            : Position(targetBind, head.TargetBoneIndex) -
              Position(targetBind, neck.TargetBoneIndex);
        if (!TryFrame(
                sourceDirection,
                sourceRight,
                out QuaternionD sourceFrame) ||
            !TryFrame(
                targetBindDirection,
                targetRight,
                out QuaternionD targetBindFrame))
        {
            return;
        }

        QuaternionD sourceRelative =
            (sourceBody.Inverse() * sourceFrame).Normalized();
        QuaternionD targetFrame =
            (targetBody * sourceRelative).Normalized();
        QuaternionD rollOffset = (
            targetBindFrame.Inverse() *
            targetBindGlobals[head.TargetBoneIndex]
        ).Normalized();
        desired[head.TargetBoneIndex] =
            (targetFrame * rollOffset).Normalized();
    }

    private static void AddClavicles(
        SkeletonPose sourcePose,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD sourceBody,
        QuaternionD targetBindBody,
        QuaternionD targetBody,
        Dictionary<int, QuaternionD> desired)
    {
        foreach (string side in new[] { "left", "right" })
        {
            if (!TryGetRow(
                    rows,
                    $"arm.{side}.clavicle",
                    out BoneMapEntry clavicle) ||
                !TryGetRow(
                    rows,
                    $"arm.{side}.upper",
                    out BoneMapEntry upper) ||
                clavicle.TransferPolicy !=
                    RetargetTransferPolicy.AnatomicalDirection)
            {
                continue;
            }

            Vector3D sourceDirection =
                Position(sourcePose, upper.SourceBoneIndex) -
                Position(sourcePose, clavicle.SourceBoneIndex);
            Vector3D targetBindDirection =
                Position(targetBind, upper.TargetBoneIndex) -
                Position(targetBind, clavicle.TargetBoneIndex);
            Vector3D sourceUp = sourceBody.Rotate(Vector3D.UnitY);
            Vector3D targetUp =
                targetBindBody.Rotate(Vector3D.UnitY);
            if (!TryFrame(
                    sourceDirection,
                    sourceUp,
                    out QuaternionD sourceFrame) ||
                !TryFrame(
                    targetBindDirection,
                    targetUp,
                    out QuaternionD targetBindFrame))
            {
                continue;
            }

            QuaternionD sourceRelative =
                (sourceBody.Inverse() * sourceFrame).Normalized();
            QuaternionD targetFrame =
                (targetBody * sourceRelative).Normalized();
            QuaternionD rollOffset = (
                targetBindFrame.Inverse() *
                targetBindGlobals[clavicle.TargetBoneIndex]
            ).Normalized();
            desired[clavicle.TargetBoneIndex] =
                (targetFrame * rollOffset).Normalized();
        }
    }

    private static void AddLimbs(
        SkeletonPose sourcePose,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD sourceBody,
        QuaternionD targetBody,
        Dictionary<int, QuaternionD> desired)
    {
        foreach ((string rootRole, string midRole, string endRole)
                 in LimbChains)
        {
            if (!TryGetRow(rows, rootRole, out BoneMapEntry root) ||
                !TryGetRow(rows, midRole, out BoneMapEntry mid) ||
                !TryGetRow(rows, endRole, out BoneMapEntry end))
            {
                continue;
            }

            Vector3D sourceRootDirection =
                Position(sourcePose, mid.SourceBoneIndex) -
                Position(sourcePose, root.SourceBoneIndex);
            Vector3D sourceMidDirection =
                Position(sourcePose, end.SourceBoneIndex) -
                Position(sourcePose, mid.SourceBoneIndex);
            Vector3D targetBindRootDirection =
                Position(targetBind, mid.TargetBoneIndex) -
                Position(targetBind, root.TargetBoneIndex);
            Vector3D targetBindMidDirection =
                Position(targetBind, end.TargetBoneIndex) -
                Position(targetBind, mid.TargetBoneIndex);

            if (!TryFrame(
                    sourceRootDirection,
                    sourceMidDirection,
                    out QuaternionD sourceLimbFrame) ||
                !TryFrame(
                    targetBindRootDirection,
                    targetBindMidDirection,
                    out QuaternionD targetBindLimbFrame))
            {
                continue;
            }

            QuaternionD sourceRelative =
                (sourceBody.Inverse() * sourceLimbFrame).Normalized();
            QuaternionD targetLimbFrame =
                (targetBody * sourceRelative).Normalized();
            if (root.TransferPolicy ==
                RetargetTransferPolicy.AnatomicalDirection)
            {
                QuaternionD rootRollOffset = (
                    targetBindLimbFrame.Inverse() *
                    targetBindGlobals[root.TargetBoneIndex]
                ).Normalized();
                desired[root.TargetBoneIndex] =
                    (targetLimbFrame * rootRollOffset).Normalized();
            }

            if (mid.TransferPolicy !=
                RetargetTransferPolicy.AnatomicalDirection)
            {
                continue;
            }

            Vector3D sourceMidRelative =
                sourceBody.Inverse().Rotate(
                    sourceMidDirection.Normalized());
            Vector3D desiredMidDirection =
                targetBody.Rotate(sourceMidRelative).Normalized();
            Vector3D desiredPlaneNormal =
                targetLimbFrame.Rotate(Vector3D.UnitZ).Normalized();
            Vector3D targetBindPlaneNormal =
                targetBindLimbFrame.Rotate(Vector3D.UnitZ).Normalized();
            if (!TryMidFrame(
                    desiredMidDirection,
                    desiredPlaneNormal,
                    out QuaternionD targetMidFrame) ||
                !TryMidFrame(
                    targetBindMidDirection,
                    targetBindPlaneNormal,
                    out QuaternionD targetBindMidFrame))
            {
                continue;
            }

            QuaternionD midRollOffset = (
                targetBindMidFrame.Inverse() *
                targetBindGlobals[mid.TargetBoneIndex]
            ).Normalized();
            desired[mid.TargetBoneIndex] =
                (targetMidFrame * midRollOffset).Normalized();
        }
    }

    private static void AddTerminals(
        SkeletonPose sourcePose,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD sourceBody,
        QuaternionD targetBody,
        Dictionary<int, QuaternionD> desired)
    {
        foreach ((
                     string rootRole,
                     string midRole,
                     string endRole,
                     string terminalRole)
                 in TerminalChains)
        {
            if (!TryGetRow(rows, rootRole, out BoneMapEntry root) ||
                !TryGetRow(rows, midRole, out BoneMapEntry mid) ||
                !TryGetRow(rows, endRole, out BoneMapEntry end) ||
                !TryGetTerminalRow(
                    rows,
                    endRole,
                    terminalRole,
                    out BoneMapEntry terminal) ||
                end.TransferPolicy !=
                    RetargetTransferPolicy.AnatomicalDirection)
            {
                continue;
            }

            Vector3D sourceRootDirection =
                Position(sourcePose, mid.SourceBoneIndex) -
                Position(sourcePose, root.SourceBoneIndex);
            Vector3D sourceMidDirection =
                Position(sourcePose, end.SourceBoneIndex) -
                Position(sourcePose, mid.SourceBoneIndex);
            Vector3D sourceTerminalDirection =
                Position(sourcePose, terminal.SourceBoneIndex) -
                Position(sourcePose, end.SourceBoneIndex);
            Vector3D targetBindRootDirection =
                Position(targetBind, mid.TargetBoneIndex) -
                Position(targetBind, root.TargetBoneIndex);
            Vector3D targetBindMidDirection =
                Position(targetBind, end.TargetBoneIndex) -
                Position(targetBind, mid.TargetBoneIndex);
            Vector3D targetBindTerminalDirection =
                Position(targetBind, terminal.TargetBoneIndex) -
                Position(targetBind, end.TargetBoneIndex);

            if (!TryFrame(
                    sourceRootDirection,
                    sourceMidDirection,
                    out QuaternionD sourceLimbFrame) ||
                !TryFrame(
                    targetBindRootDirection,
                    targetBindMidDirection,
                    out QuaternionD targetBindLimbFrame) ||
                !TryFrame(
                    sourceTerminalDirection,
                    sourceLimbFrame.Rotate(Vector3D.UnitZ),
                    out QuaternionD sourceTerminalFrame) ||
                !TryFrame(
                    targetBindTerminalDirection,
                    targetBindLimbFrame.Rotate(Vector3D.UnitZ),
                    out QuaternionD targetBindTerminalFrame))
            {
                continue;
            }

            QuaternionD sourceRelative = (
                sourceBody.Inverse() *
                sourceTerminalFrame
            ).Normalized();
            QuaternionD targetTerminalFrame = (
                targetBody *
                sourceRelative
            ).Normalized();
            QuaternionD rollOffset = (
                targetBindTerminalFrame.Inverse() *
                targetBindGlobals[end.TargetBoneIndex]
            ).Normalized();
            desired[end.TargetBoneIndex] =
                (targetTerminalFrame * rollOffset).Normalized();
        }
    }

    private static void AddFingers(
        SkeletonPose sourcePose,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        Dictionary<int, QuaternionD> desired)
    {
        foreach (string side in new[] { "left", "right" })
        {
            if (!TryGetRow(
                    rows,
                    $"hand.{side}",
                    out BoneMapEntry hand) ||
                !TryCreatePalmFrame(
                    sourcePose,
                    rows,
                    side,
                    source: true,
                    out QuaternionD sourcePalm) ||
                !TryCreatePalmFrame(
                    targetBind,
                    rows,
                    side,
                    source: false,
                    out QuaternionD targetBindPalm))
            {
                continue;
            }

            QuaternionD targetHandGlobal =
                desired.TryGetValue(
                    hand.TargetBoneIndex,
                    out QuaternionD solvedHand)
                    ? solvedHand
                    : targetBindGlobals[hand.TargetBoneIndex];
            QuaternionD targetPalmRelativeToHand = (
                targetBindGlobals[hand.TargetBoneIndex].Inverse() *
                targetBindPalm
            ).Normalized();
            QuaternionD targetPalm = (
                targetHandGlobal *
                targetPalmRelativeToHand
            ).Normalized();

            foreach (string digit in FingerDigits)
            {
                AddFingerChain(
                    sourcePose,
                    targetBind,
                    rows,
                    targetBindGlobals,
                    sourcePalm,
                    targetPalm,
                    side,
                    digit,
                    desired);
            }
        }
    }

    private static void AddFingerChain(
        SkeletonPose sourcePose,
        SkeletonPose targetBind,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        QuaternionD[] targetBindGlobals,
        QuaternionD sourcePalm,
        QuaternionD targetPalm,
        string side,
        string digit,
        Dictionary<int, QuaternionD> desired)
    {
        BoneMapEntry[] segments = new BoneMapEntry[3];
        for (int segment = 1; segment <= segments.Length; segment++)
        {
            if (!TryGetRow(
                    rows,
                    $"finger.{side}.{digit}.{segment}",
                    out segments[segment - 1]) ||
                segments[segment - 1].TransferPolicy !=
                    RetargetTransferPolicy.AnatomicalDirection)
            {
                return;
            }
        }

        if (!TryParseFingerRole(
                sourcePose.Rig,
                segments[0].SourceBoneIndex,
                out string sourceSide,
                out string sourceDigit,
                out int sourceSegment) ||
            sourceSegment != 1)
        {
            return;
        }

        for (int segment = 1; segment < segments.Length; segment++)
        {
            if (!TryParseFingerRole(
                    sourcePose.Rig,
                    segments[segment].SourceBoneIndex,
                    out string nextSide,
                    out string nextDigit,
                    out int nextSegment) ||
                nextSide != sourceSide ||
                nextDigit != sourceDigit ||
                nextSegment != segment + 1)
            {
                return;
            }
        }

        int sourceTerminal = FindUniqueBoneByRole(
            sourcePose.Rig,
            $"finger.{sourceSide}.{sourceDigit}.4");
        if (sourceTerminal < 0)
        {
            return;
        }

        for (int segment = 0; segment < segments.Length; segment++)
        {
            BoneMapEntry current = segments[segment];
            int sourceEnd = segment < segments.Length - 1
                ? segments[segment + 1].SourceBoneIndex
                : sourceTerminal;
            Vector3D sourceDirection =
                Position(sourcePose, sourceEnd) -
                Position(sourcePose, current.SourceBoneIndex);
            Vector3D targetBindDirection = segment <
                segments.Length - 1
                ? Position(
                      targetBind,
                      segments[segment + 1].TargetBoneIndex) -
                  Position(targetBind, current.TargetBoneIndex)
                : Position(
                      targetBind,
                      current.TargetBoneIndex) -
                  Position(
                      targetBind,
                      segments[segment - 1].TargetBoneIndex);
            if (!sourceDirection.TryNormalize(
                    out Vector3D sourceDirectionGlobal,
                    DirectionEpsilon) ||
                !targetBindDirection.TryNormalize(
                    out Vector3D targetBindDirectionGlobal,
                    DirectionEpsilon))
            {
                return;
            }

            Vector3D sourceDirectionPalm =
                sourcePalm.Inverse().Rotate(
                    sourceDirectionGlobal);
            Vector3D desiredDirectionGlobal =
                targetPalm.Rotate(sourceDirectionPalm);
            QuaternionD swing = QuaternionD.FromToRotation(
                targetBindDirectionGlobal,
                desiredDirectionGlobal,
                DirectionEpsilon);
            desired[current.TargetBoneIndex] = (
                swing *
                targetBindGlobals[current.TargetBoneIndex]
            ).Normalized();
        }
    }

    private static bool TryCreatePalmFrame(
        SkeletonPose pose,
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        string side,
        bool source,
        out QuaternionD frame)
    {
        frame = QuaternionD.Identity;
        if (!TryGetRow(
                rows,
                $"hand.{side}",
                out BoneMapEntry hand))
        {
            return false;
        }

        var rootPositions = new List<Vector3D>(4);
        var seenSourceRoots = new HashSet<int>();
        foreach (string digit in
                 new[] { "index", "middle", "ring", "little" })
        {
            if (!TryGetRow(
                    rows,
                    $"finger.{side}.{digit}.1",
                    out BoneMapEntry root))
            {
                continue;
            }

            // A source rig may omit one retail digit entirely. Suggested
            // mappings deliberately fan that target chain out from the
            // nearest available source digit. Count that source root once in
            // both palm frames so the source and target bases are built from
            // the same anatomical samples.
            if (!seenSourceRoots.Add(root.SourceBoneIndex))
            {
                continue;
            }

            rootPositions.Add(Position(
                pose,
                source
                    ? root.SourceBoneIndex
                    : root.TargetBoneIndex));
        }

        if (rootPositions.Count < 2)
        {
            return false;
        }

        Vector3D handPosition = Position(
            pose,
            source
                ? hand.SourceBoneIndex
                : hand.TargetBoneIndex);
        Vector3D meanRoot =
            rootPositions.Aggregate(
                Vector3D.Zero,
                static (sum, value) => sum + value) /
            rootPositions.Count;
        Vector3D forward = meanRoot - handPosition;
        Vector3D towardIndex =
            rootPositions[0] -
            rootPositions[^1];
        return TryFrame(forward, towardIndex, out frame);
    }

    private static bool TryParseFingerRole(
        RigDefinition rig,
        int boneIndex,
        out string side,
        out string digit,
        out int segment)
    {
        side = string.Empty;
        digit = string.Empty;
        segment = 0;
        if (boneIndex < 0 ||
            boneIndex >= rig.Bones.Length)
        {
            return false;
        }

        string? role = HumanoidBoneSemanticClassifier.Classify(
            rig.Bones[boneIndex].SemanticRole ??
            rig.Bones[boneIndex].Name)?.Role;
        string[]? parts = role?.Split('.');
        if (parts is not { Length: 4 } ||
            parts[0] != "finger" ||
            !int.TryParse(parts[3], out segment))
        {
            return false;
        }

        side = parts[1];
        digit = parts[2];
        return segment is >= 1 and <= 4;
    }

    private static bool TryGetTerminalRow(
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        string endRole,
        string preferredTerminalRole,
        out BoneMapEntry terminal)
    {
        if (TryGetRow(
                rows,
                preferredTerminalRole,
                out terminal))
        {
            return true;
        }

        string? side = endRole switch
        {
            "hand.left" => "left",
            "hand.right" => "right",
            _ => null,
        };
        if (side is not null)
        {
            foreach (string digit in
                     new[] { "index", "ring", "little", "thumb" })
            {
                if (TryGetRow(
                        rows,
                        $"finger.{side}.{digit}.1",
                        out terminal))
                {
                    return true;
                }
            }
        }

        terminal = null!;
        return false;
    }

    private static bool TryFrame(
        Vector3D primary,
        Vector3D secondary,
        out QuaternionD frame)
    {
        frame = QuaternionD.Identity;
        if (!primary.TryNormalize(out Vector3D x, DirectionEpsilon))
        {
            return false;
        }

        Vector3D projected =
            secondary -
            (Vector3D.Dot(secondary, x) * x);
        if (!projected.TryNormalize(
                out Vector3D y,
                DirectionEpsilon))
        {
            Vector3D fallback =
                Math.Abs(Vector3D.Dot(Vector3D.UnitY, x)) < 0.9
                    ? Vector3D.UnitY
                    : Vector3D.UnitX;
            projected =
                fallback -
                (Vector3D.Dot(fallback, x) * x);
            if (!projected.TryNormalize(
                    out y,
                    DirectionEpsilon))
            {
                return false;
            }
        }

        if (!Vector3D.Cross(x, y).TryNormalize(
                out Vector3D z,
                DirectionEpsilon))
        {
            return false;
        }

        y = Vector3D.Cross(z, x).Normalized(DirectionEpsilon);
        frame = QuaternionFromColumns(x, y, z);
        return true;
    }

    private static bool TryMidFrame(
        Vector3D direction,
        Vector3D planeNormal,
        out QuaternionD frame)
    {
        frame = QuaternionD.Identity;
        if (!direction.TryNormalize(
                out Vector3D x,
                DirectionEpsilon))
        {
            return false;
        }

        Vector3D projectedNormal =
            planeNormal -
            (Vector3D.Dot(planeNormal, x) * x);
        if (!projectedNormal.TryNormalize(
                out Vector3D z,
                DirectionEpsilon) ||
            !Vector3D.Cross(z, x).TryNormalize(
                out Vector3D y,
                DirectionEpsilon))
        {
            return false;
        }

        z = Vector3D.Cross(x, y).Normalized(DirectionEpsilon);
        frame = QuaternionFromColumns(x, y, z);
        return true;
    }

    private static QuaternionD QuaternionFromColumns(
        Vector3D x,
        Vector3D y,
        Vector3D z) =>
        QuaternionD.FromRotationMatrix(
            new TransformMatrix(
                x.X, y.X, z.X, 0.0,
                x.Y, y.Y, z.Y, 0.0,
                x.Z, y.Z, z.Z, 0.0,
                0.0, 0.0, 0.0, 1.0));

    private static bool TryGetRow(
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        string role,
        out BoneMapEntry row) =>
        rows.TryGetValue(role, out row!);

    private static bool TryGetHighestSpineRow(
        IReadOnlyDictionary<string, BoneMapEntry> rows,
        out BoneMapEntry row)
    {
        foreach (string role in
                 new[]
                 {
                     "body.spine.3",
                     "body.spine.2",
                     "body.spine.1",
                     "body.spine.0",
                 })
        {
            if (rows.TryGetValue(role, out row!))
            {
                return true;
            }
        }

        row = null!;
        return false;
    }

    private static int FindHeadEnd(
        RigDefinition rig,
        int headIndex)
    {
        int result = -1;
        foreach (BoneDefinition bone in rig.Bones)
        {
            if (bone.ParentIndex != headIndex)
            {
                continue;
            }

            string compact = new(
                bone.Name
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant)
                    .ToArray());
            if (!compact.Contains(
                    "headtopend",
                    StringComparison.Ordinal) &&
                !compact.Contains(
                    "headend",
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (result >= 0)
            {
                return -1;
            }

            result = bone.Index;
        }

        return result;
    }

    private static int FindUniqueBoneByRole(
        RigDefinition rig,
        string role)
    {
        int result = -1;
        foreach (BoneDefinition bone in rig.Bones)
        {
            string? candidate =
                HumanoidBoneSemanticClassifier.Classify(
                    bone.SemanticRole ?? bone.Name)?.Role;
            if (!string.Equals(
                    candidate,
                    role,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (result >= 0)
            {
                return -1;
            }

            result = bone.Index;
        }

        return result;
    }

    private static Vector3D Position(
        SkeletonPose pose,
        int boneIndex) =>
        pose.GlobalMatrices[boneIndex].Translation;

    private static QuaternionD[] ComputeGlobalRotations(
        SkeletonPose pose)
    {
        var result = new QuaternionD[pose.Rig.BoneCount];
        for (int index = 0; index < result.Length; index++)
        {
            int parent = pose.Rig.Bones[index].ParentIndex;
            QuaternionD local =
                pose.LocalTransforms[index].Rotation.Normalized();
            result[index] = parent < 0
                ? local
                : (result[parent] * local).Normalized();
        }

        return result;
    }
}
