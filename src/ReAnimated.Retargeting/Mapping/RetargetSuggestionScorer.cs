using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Retargeting.Mapping;

/// <summary>
/// Produces explainable, versioned confidence evidence for automatic mapping
/// proposals. Scores are advisory until an author explicitly applies the
/// assisted-review policy.
/// </summary>
public static class RetargetSuggestionScorer
{
    public const string PolicyVersion = "dlra-assisted-review-v1";

    public const double AssistedReviewThreshold = 0.90;

    public static RetargetMap Score(
        RigDefinition source,
        RigDefinition target,
        RetargetMap proposal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(proposal);
        EnsureRigPair(source, target, proposal);

        Dictionary<int, BoneMapEntry> byTarget = proposal.Entries
            .ToDictionary(static entry => entry.TargetBoneIndex);
        ImmutableArray<BoneMapEntry> scored = proposal.Entries
            .OrderBy(static entry => entry.TargetBoneIndex)
            .Select(entry => ScoreEntry(source, target, entry, byTarget))
            .ToImmutableArray();
        return new RetargetMap(
            proposal.SourceRigId,
            proposal.TargetRigId,
            scored,
            proposal.ReviewedTargetBindBoneIndices);
    }

    public static RetargetMap ApplyAssistedReview(
        RigDefinition source,
        RigDefinition target,
        RetargetMap proposal)
    {
        RetargetMap scored = Score(source, target, proposal);
        ImmutableArray<BoneMapEntry> reviewed = scored.Entries
            .Select(entry => IsEligibleForAssistedReview(entry)
                ? Copy(
                    entry,
                    isLocked: true,
                    isReviewed: true,
                    reviewOrigin: MappingReviewOrigin.Assisted)
                : entry)
            .ToImmutableArray();
        return new RetargetMap(
            scored.SourceRigId,
            scored.TargetRigId,
            reviewed,
            scored.ReviewedTargetBindBoneIndices);
    }

    /// <summary>
    /// Assisted approvals are not durable author decisions: a changed rig,
    /// scorer, evidence set, or policy clears them back to review-required.
    /// Explicit reviews are preserved unchanged.
    /// </summary>
    public static RetargetMap RevalidateAssistedApprovals(
        RigDefinition source,
        RigDefinition target,
        RetargetMap mapping)
    {
        RetargetMap rescored = Score(source, target, mapping);
        Dictionary<int, BoneMapEntry> rescoredByTarget = rescored.Entries
            .ToDictionary(static entry => entry.TargetBoneIndex);
        ImmutableArray<BoneMapEntry> entries = mapping.Entries
            .OrderBy(static entry => entry.TargetBoneIndex)
            .Select(entry =>
            {
                if (entry.ReviewOrigin != MappingReviewOrigin.Assisted)
                {
                    return entry;
                }

                BoneMapEntry current = rescoredByTarget[entry.TargetBoneIndex];
                bool stillValid =
                    string.Equals(
                        entry.ScorerVersion,
                        PolicyVersion,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        entry.EvidenceFingerprint,
                        current.EvidenceFingerprint,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsEligibleForAssistedReview(current);
                return stillValid
                    ? Copy(
                        current,
                        isLocked: true,
                        isReviewed: true,
                        reviewOrigin: MappingReviewOrigin.Assisted)
                    : Copy(
                        current,
                        isLocked: false,
                        isReviewed: false,
                        reviewOrigin: MappingReviewOrigin.None);
            })
            .ToImmutableArray();
        return new RetargetMap(
            mapping.SourceRigId,
            mapping.TargetRigId,
            entries,
            mapping.ReviewedTargetBindBoneIndices);
    }

    public static bool IsEligibleForAssistedReview(BoneMapEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Confidence < AssistedReviewThreshold ||
            entry.MappingKind != RetargetMappingKind.Bone ||
            entry.Method is BoneMappingMethod.Structural or
                BoneMappingMethod.Composed or
                BoneMappingMethod.Distributed or
                BoneMappingMethod.Manual ||
            string.IsNullOrWhiteSpace(entry.EvidenceFingerprint) ||
            !string.Equals(
                entry.ScorerVersion,
                PolicyVersion,
                StringComparison.Ordinal))
        {
            return false;
        }

        HashSet<MappingEvidenceKind> evidence = entry.Evidence
            .Select(static row => row.Kind)
            .ToHashSet();
        return entry.Method switch
        {
            BoneMappingMethod.DescriptorHash =>
                evidence.Contains(MappingEvidenceKind.DescriptorIdentity) &&
                evidence.Contains(MappingEvidenceKind.TransferPolicyAgreement),
            BoneMappingMethod.ExactName =>
                evidence.Contains(MappingEvidenceKind.ExactName) &&
                evidence.Contains(MappingEvidenceKind.TransferPolicyAgreement),
            BoneMappingMethod.NormalizedName =>
                evidence.Contains(MappingEvidenceKind.NormalizedName) &&
                evidence.Contains(MappingEvidenceKind.ParentChainAgreement) &&
                evidence.Contains(MappingEvidenceKind.TransferPolicyAgreement),
            BoneMappingMethod.Semantic when
                evidence.Contains(MappingEvidenceKind.DeclaredSemanticRole) =>
                    evidence.Contains(MappingEvidenceKind.TransferPolicyAgreement),
            BoneMappingMethod.Semantic =>
                evidence.Contains(MappingEvidenceKind.InferredHumanoidRole) &&
                evidence.Contains(MappingEvidenceKind.SideAgreement) &&
                evidence.Contains(MappingEvidenceKind.ParentChainAgreement) &&
                evidence.Contains(MappingEvidenceKind.TransferPolicyAgreement),
            _ => false,
        };
    }

    private static BoneMapEntry ScoreEntry(
        RigDefinition source,
        RigDefinition target,
        BoneMapEntry entry,
        IReadOnlyDictionary<int, BoneMapEntry> byTarget)
    {
        BoneDefinition sourceBone = source.Bones[entry.SourceBoneIndex];
        BoneDefinition targetBone = target.Bones[entry.TargetBoneIndex];
        var evidence = ImmutableArray.CreateBuilder<MappingEvidence>();
        double confidence = entry.Confidence;

        switch (entry.Method)
        {
            case BoneMappingMethod.DescriptorHash:
                if (HasUniqueDescriptorIdentity(
                        source,
                        target,
                        sourceBone,
                        targetBone))
                {
                    evidence.Add(new(
                        MappingEvidenceKind.DescriptorIdentity,
                        $"Unique descriptor 0x{targetBone.DescriptorHash.GetValueOrDefault():X8} identifies both rows."));
                    confidence = 1.0;
                }
                else
                {
                    evidence.Add(new(
                        MappingEvidenceKind.StructuralSignature,
                        "The proposed descriptor is missing, differs, or is not unique on both rigs."));
                    confidence = Math.Min(confidence, 0.89);
                }
                break;
            case BoneMappingMethod.ExactName:
                if (HasUniqueExactNameIdentity(
                        source,
                        target,
                        sourceBone,
                        targetBone))
                {
                    evidence.Add(new(
                        MappingEvidenceKind.ExactName,
                        $"Unique exact name '{targetBone.Name}' identifies both rows."));
                    confidence = 1.0;
                }
                else
                {
                    evidence.Add(new(
                        MappingEvidenceKind.StructuralSignature,
                        "The proposed exact name differs or is ambiguous on at least one rig."));
                    confidence = Math.Min(confidence, 0.89);
                }
                break;
            case BoneMappingMethod.NormalizedName:
                if (HasUniqueNormalizedNameIdentity(
                        source,
                        target,
                        sourceBone,
                        targetBone))
                {
                    evidence.Add(new(
                        MappingEvidenceKind.NormalizedName,
                        $"Unique normalized name matches '{sourceBone.Name}' to '{targetBone.Name}'."));
                    confidence = HasMappedParentAgreement(source, target, entry, byTarget)
                        ? 0.95
                        : Math.Min(confidence, 0.89);
                }
                else
                {
                    evidence.Add(new(
                        MappingEvidenceKind.StructuralSignature,
                        "The proposed normalized name differs or is ambiguous on at least one rig."));
                    confidence = Math.Min(confidence, 0.89);
                }
                break;
            case BoneMappingMethod.Semantic:
                if (HasUniqueDeclaredSemanticAgreement(
                        source,
                        target,
                        sourceBone,
                        targetBone))
                {
                    evidence.Add(new(
                        MappingEvidenceKind.DeclaredSemanticRole,
                        $"Unique declared semantic role '{targetBone.SemanticRole}' identifies the row."));
                    confidence = Math.Max(confidence, 0.90);
                }
                else
                {
                    string? role = SemanticRole(targetBone);
                    evidence.Add(new(
                        MappingEvidenceKind.InferredHumanoidRole,
                        $"Humanoid classifier inferred role '{role ?? "unknown"}'."));
                    bool side = HasSideAgreement(sourceBone, targetBone);
                    bool parent = HasMappedParentAgreement(source, target, entry, byTarget);
                    bool policy = IsSupportedAutomaticPolicy(entry);
                    if (side)
                    {
                        evidence.Add(new(
                            MappingEvidenceKind.SideAgreement,
                            "Source and target roles agree on left/right ownership."));
                    }

                    if (side && parent && policy)
                    {
                        confidence = 0.90;
                    }
                    else
                    {
                        confidence = Math.Min(confidence, 0.82);
                    }
                }
                break;
            case BoneMappingMethod.Structural:
                evidence.Add(new(
                    MappingEvidenceKind.StructuralSignature,
                    "A unique depth/parent/child structural signature proposed this row."));
                confidence = Math.Min(confidence, 0.70);
                break;
            case BoneMappingMethod.Distributed:
                evidence.Add(new(
                    MappingEvidenceKind.DistributedFanOut,
                    "A neighboring complete chain supplies this missing target chain."));
                confidence = Math.Min(confidence, 0.75);
                break;
            case BoneMappingMethod.Manual:
                evidence.Add(new(
                    MappingEvidenceKind.ManualSelection,
                    "The author selected this source and target pair."));
                break;
            default:
                evidence.Add(new(
                    MappingEvidenceKind.StructuralSignature,
                    $"The {entry.Method} proposal requires author review."));
                break;
        }

        if (entry.MappingKind == RetargetMappingKind.HelperOverride &&
            entry.Method is not (
                BoneMappingMethod.ExactName or
                BoneMappingMethod.NormalizedName))
        {
            evidence.Add(new(
                MappingEvidenceKind.HelperFallback,
                "A helper-role fallback selected the source row."));
            confidence = Math.Min(confidence, 0.70);
        }

        if (HasMappedParentAgreement(source, target, entry, byTarget))
        {
            evidence.Add(new(
                MappingEvidenceKind.ParentChainAgreement,
                "The mapped parent chain is consistent on both rigs."));
        }

        if (IsSupportedAutomaticPolicy(entry))
        {
            evidence.Add(new(
                MappingEvidenceKind.TransferPolicyAgreement,
                $"{entry.TransferPolicy}/{entry.TransformComponents} is a supported automatic policy for this row."));
        }

        ImmutableArray<MappingEvidence> rows = evidence
            .Distinct()
            .ToImmutableArray();
        string fingerprint = ComputeEvidenceFingerprint(
            source,
            target,
            entry,
            confidence,
            rows);
        MappingReviewOrigin origin = entry.ReviewOrigin;
        bool reviewed = entry.IsReviewed;
        bool locked = entry.IsLocked;
        if (origin == MappingReviewOrigin.Assisted)
        {
            // Score never silently renews an assisted decision. Revalidation
            // compares its prior fingerprint through the dedicated method.
            origin = MappingReviewOrigin.None;
            reviewed = false;
            locked = false;
        }

        return new BoneMapEntry(
            entry.SourceBoneIndex,
            entry.TargetBoneIndex,
            entry.Method,
            confidence,
            locked,
            reviewed,
            entry.MappingKind,
            entry.TransferPolicy,
            entry.ComponentPolicy,
            rows,
            origin,
            PolicyVersion,
            fingerprint,
            entry.TransformComponents);
    }

    private static bool HasMappedParentAgreement(
        RigDefinition source,
        RigDefinition target,
        BoneMapEntry entry,
        IReadOnlyDictionary<int, BoneMapEntry> byTarget)
    {
        int sourceParent = source.Bones[entry.SourceBoneIndex].ParentIndex;
        int targetParent = target.Bones[entry.TargetBoneIndex].ParentIndex;
        if (sourceParent < 0 || targetParent < 0)
        {
            return sourceParent < 0 && targetParent < 0;
        }

        return byTarget.TryGetValue(targetParent, out BoneMapEntry? parent) &&
            parent.SourceBoneIndex == sourceParent;
    }

    private static bool HasSideAgreement(
        BoneDefinition source,
        BoneDefinition target)
    {
        string? sourceRole = SemanticRole(source);
        string? targetRole = SemanticRole(target);
        string? sourceSide = Side(sourceRole);
        string? targetSide = Side(targetRole);
        return sourceSide is null
            ? targetSide is null
            : string.Equals(sourceSide, targetSide, StringComparison.Ordinal);
    }

    private static bool HasDeclaredSemanticAgreement(
        BoneDefinition source,
        BoneDefinition target) =>
        !string.IsNullOrWhiteSpace(source.SemanticRole) &&
        !string.IsNullOrWhiteSpace(target.SemanticRole) &&
        string.Equals(
            source.SemanticRole.Trim(),
            target.SemanticRole.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasUniqueDeclaredSemanticAgreement(
        RigDefinition source,
        RigDefinition target,
        BoneDefinition sourceBone,
        BoneDefinition targetBone)
    {
        if (!HasDeclaredSemanticAgreement(sourceBone, targetBone))
        {
            return false;
        }

        string role = sourceBone.SemanticRole!.Trim();
        return source.Bones.Count(bone => string.Equals(
                   bone.SemanticRole?.Trim(),
                   role,
                   StringComparison.OrdinalIgnoreCase)) == 1 &&
            target.Bones.Count(bone => string.Equals(
                bone.SemanticRole?.Trim(),
                role,
                StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private static bool HasUniqueDescriptorIdentity(
        RigDefinition source,
        RigDefinition target,
        BoneDefinition sourceBone,
        BoneDefinition targetBone)
    {
        if (sourceBone.DescriptorHash is not { } descriptor ||
            targetBone.DescriptorHash != descriptor)
        {
            return false;
        }

        return source.Bones.Count(bone =>
                   bone.DescriptorHash == descriptor) == 1 &&
            target.Bones.Count(bone =>
                bone.DescriptorHash == descriptor) == 1;
    }

    private static bool HasUniqueExactNameIdentity(
        RigDefinition source,
        RigDefinition target,
        BoneDefinition sourceBone,
        BoneDefinition targetBone) =>
        string.Equals(
            sourceBone.Name,
            targetBone.Name,
            StringComparison.OrdinalIgnoreCase) &&
        source.Bones.Count(bone => string.Equals(
            bone.Name,
            sourceBone.Name,
            StringComparison.OrdinalIgnoreCase)) == 1 &&
        target.Bones.Count(bone => string.Equals(
            bone.Name,
            targetBone.Name,
            StringComparison.OrdinalIgnoreCase)) == 1;

    private static bool HasUniqueNormalizedNameIdentity(
        RigDefinition source,
        RigDefinition target,
        BoneDefinition sourceBone,
        BoneDefinition targetBone)
    {
        string sourceName = RetargetMapBuilder.NormalizeBoneName(
            sourceBone.Name);
        string targetName = RetargetMapBuilder.NormalizeBoneName(
            targetBone.Name);
        return string.Equals(sourceName, targetName, StringComparison.Ordinal) &&
            source.Bones.Count(bone => string.Equals(
                RetargetMapBuilder.NormalizeBoneName(bone.Name),
                sourceName,
                StringComparison.Ordinal)) == 1 &&
            target.Bones.Count(bone => string.Equals(
                RetargetMapBuilder.NormalizeBoneName(bone.Name),
                targetName,
                StringComparison.Ordinal)) == 1;
    }

    private static string? SemanticRole(BoneDefinition bone) =>
        HumanoidBoneSemanticClassifier.Classify(
            bone.SemanticRole ?? bone.Name)?.Role;

    private static string? Side(string? role)
    {
        if (role is null)
        {
            return null;
        }

        string[] parts = role.Split('.');
        return parts.FirstOrDefault(static part => part is "left" or "right");
    }

    private static bool IsSupportedAutomaticPolicy(BoneMapEntry entry) =>
        entry.MappingKind == RetargetMappingKind.Bone &&
        ((entry.TransferPolicy == RetargetTransferPolicy.GlobalBindBasis &&
          entry.TransformComponents == RetargetTransformComponents.All) ||
         (entry.TransferPolicy is
              RetargetTransferPolicy.AnatomicalDirection or
              RetargetTransferPolicy.RotationDelta &&
          entry.TransformComponents == RetargetTransformComponents.Rotation));

    private static BoneMapEntry Copy(
        BoneMapEntry entry,
        bool isLocked,
        bool isReviewed,
        MappingReviewOrigin reviewOrigin) =>
        new(
            entry.SourceBoneIndex,
            entry.TargetBoneIndex,
            entry.Method,
            entry.Confidence,
            isLocked,
            isReviewed,
            entry.MappingKind,
            entry.TransferPolicy,
            entry.ComponentPolicy,
            entry.Evidence,
            reviewOrigin,
            entry.ScorerVersion,
            entry.EvidenceFingerprint,
            entry.TransformComponents);

    private static string ComputeEvidenceFingerprint(
        RigDefinition source,
        RigDefinition target,
        BoneMapEntry entry,
        double confidence,
        ImmutableArray<MappingEvidence> evidence)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        Append(hash, PolicyVersion);
        Append(hash, RigSignature.Compute(source));
        Append(hash, RigSignature.Compute(target));
        Append(hash, entry.SourceBoneIndex);
        Append(hash, entry.TargetBoneIndex);
        Append(hash, (int)entry.Method);
        Append(hash, (int)entry.MappingKind);
        Append(hash, (int)entry.TransferPolicy);
        Append(hash, (int)entry.TransformComponents);
        Append(hash, BitConverter.DoubleToInt64Bits(confidence));
        foreach (MappingEvidence row in evidence
                     .OrderBy(static row => row.Kind)
                     .ThenBy(static row => row.Detail, StringComparer.Ordinal))
        {
            Append(hash, (int)row.Kind);
            Append(hash, row.Detail);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void EnsureRigPair(
        RigDefinition source,
        RigDefinition target,
        RetargetMap mapping)
    {
        if (!string.Equals(source.Id, mapping.SourceRigId, StringComparison.Ordinal) ||
            !string.Equals(target.Id, mapping.TargetRigId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The mapping does not belong to the supplied rig pair.",
                nameof(mapping));
        }
    }
}
