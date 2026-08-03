using System.Collections.Immutable;
using System.Text;
using ReAnimated.Core.Domain;

namespace ReAnimated.Retargeting.Mapping;

public enum BoneMappingMethod
{
    DescriptorHash,
    ExactName,
    NormalizedName,
    Semantic,
    Structural,
    Manual,
    Composed,
    Distributed,
}

public sealed record BoneMapEntry
{
    public BoneMapEntry(
        int sourceBoneIndex,
        int targetBoneIndex,
        BoneMappingMethod method,
        double confidence,
        bool isLocked = false,
        bool isReviewed = false,
        RetargetMappingKind mappingKind = RetargetMappingKind.Bone,
        RetargetTransferPolicy transferPolicy =
            RetargetTransferPolicy.GlobalBindBasis,
        RetargetComponentPolicy componentPolicy =
            RetargetComponentPolicy.FullTransform)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(targetBoneIndex);
        if (!double.IsFinite(confidence) || confidence < 0.0 || confidence > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                "Mapping confidence must be between zero and one.");
        }

        if (!Enum.IsDefined(mappingKind) ||
            !Enum.IsDefined(transferPolicy) ||
            !Enum.IsDefined(componentPolicy))
        {
            throw new ArgumentException(
                "The mapping kind, transfer policy, and component policy must be supported values.");
        }

        SourceBoneIndex = sourceBoneIndex;
        TargetBoneIndex = targetBoneIndex;
        Method = method;
        Confidence = confidence;
        IsLocked = isLocked;
        IsReviewed = isReviewed;
        MappingKind = mappingKind;
        TransferPolicy = transferPolicy;
        ComponentPolicy = componentPolicy;
    }

    public int SourceBoneIndex { get; }

    public int TargetBoneIndex { get; }

    public BoneMappingMethod Method { get; }

    public double Confidence { get; }

    public bool IsLocked { get; }

    public bool IsReviewed { get; }

    public RetargetMappingKind MappingKind { get; }

    public RetargetTransferPolicy TransferPolicy { get; }

    public RetargetComponentPolicy ComponentPolicy { get; }
}

/// <summary>
/// An immutable source-to-target map. Multiple target bones may intentionally
/// consume one source bone, but each target may appear only once.
/// </summary>
public sealed class RetargetMap
{
    private readonly ImmutableDictionary<int, BoneMapEntry> _byTarget;

    public RetargetMap(
        string sourceRigId,
        string targetRigId,
        IEnumerable<BoneMapEntry> entries,
        IEnumerable<int>? reviewedTargetBindBoneIndices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRigId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRigId);
        ArgumentNullException.ThrowIfNull(entries);

        ImmutableArray<BoneMapEntry> array = entries.ToImmutableArray();
        ImmutableArray<int> reviewedTargetBindBones =
            reviewedTargetBindBoneIndices?.ToImmutableArray() ?? [];
        if (array.Select(static entry => entry.TargetBoneIndex).Distinct().Count() != array.Length)
        {
            throw new ArgumentException("A target bone can be mapped only once.", nameof(entries));
        }

        if (reviewedTargetBindBones.Any(static index => index < 0) ||
            reviewedTargetBindBones.Distinct().Count() !=
                reviewedTargetBindBones.Length)
        {
            throw new ArgumentException(
                "Reviewed target-bind rows must be distinct non-negative indexes.",
                nameof(reviewedTargetBindBoneIndices));
        }

        if (reviewedTargetBindBones.Any(index =>
                array.Any(entry => entry.TargetBoneIndex == index)))
        {
            throw new ArgumentException(
                "A mapped target bone cannot also be reviewed as a target-bind fallback.",
                nameof(reviewedTargetBindBoneIndices));
        }

        SourceRigId = sourceRigId;
        TargetRigId = targetRigId;
        Entries = array;
        ReviewedTargetBindBoneIndices = reviewedTargetBindBones;
        _byTarget = array.ToImmutableDictionary(static entry => entry.TargetBoneIndex);
    }

    public string SourceRigId { get; }

    public string TargetRigId { get; }

    public ImmutableArray<BoneMapEntry> Entries { get; }

    public ImmutableArray<int> ReviewedTargetBindBoneIndices { get; }

    public bool TryGetTargetEntry(int targetBoneIndex, out BoneMapEntry? entry) =>
        _byTarget.TryGetValue(targetBoneIndex, out entry);
}

public static class RetargetMapBuilder
{
    /// <summary>
    /// Builds a conservative automatic proposal in DL1 review order:
    /// descriptor hash, exact/normalized name, declared semantic role, and
    /// finally an unambiguous structural signature. Structural rows carry a
    /// deliberately review-level confidence and are never silently promoted
    /// to exact mappings.
    /// </summary>
    public static RetargetMap CreateSuggested(
        RigDefinition source,
        RigDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var entries = ImmutableArray.CreateBuilder<BoneMapEntry>();
        HashSet<int> mappedSources = [];
        HashSet<int> mappedTargets = [];

        AddUniqueMatches(
            source,
            target,
            entries,
            mappedSources,
            mappedTargets,
            static bone => bone.DescriptorHash?.ToString(
                "X8",
                System.Globalization.CultureInfo.InvariantCulture),
            BoneMappingMethod.DescriptorHash,
            1.0);

        AddNameMatches(
            source,
            target,
            entries,
            mappedSources,
            mappedTargets);

        AddUniqueMatches(
            source,
            target,
            entries,
            mappedSources,
            mappedTargets,
            static bone => string.IsNullOrWhiteSpace(bone.SemanticRole)
                ? null
                : bone.SemanticRole.Trim().ToUpperInvariant(),
            BoneMappingMethod.Semantic,
            0.9);

        AddUniqueMatches(
            source,
            target,
            entries,
            mappedSources,
            mappedTargets,
            static bone =>
                HumanoidBoneSemanticClassifier
                    .Classify(
                        bone.SemanticRole ??
                        bone.Name)
                    ?.Role,
            BoneMappingMethod.Semantic,
            0.82);

        AddDistributedFingerMatches(
            source,
            target,
            entries,
            mappedTargets);

        AddStructuralMatches(
            source,
            target,
            entries,
            mappedSources,
            mappedTargets);

        AddHelperMatches(
            source,
            target,
            entries,
            mappedTargets);

        return new RetargetMap(
            source.Id,
            target.Id,
            entries
                .Select(entry =>
                    ApplyAutomaticBodyTransferPolicy(
                        entry,
                        source,
                        target))
                .OrderBy(static entry => entry.TargetBoneIndex)
                .ToImmutableArray());
    }

    private static void AddDistributedFingerMatches(
        RigDefinition source,
        RigDefinition target,
        ImmutableArray<BoneMapEntry>.Builder entries,
        HashSet<int> mappedTargets)
    {
        var sourceFingerRows = source.Bones
            .Select(bone => (
                Bone: bone,
                Role: HumanoidBoneSemanticClassifier.Classify(
                    bone.SemanticRole ??
                    bone.Name)?.Role))
            .Where(static row =>
                row.Role?.StartsWith(
                    "finger.",
                    StringComparison.Ordinal) == true)
            .ToArray();
        Dictionary<string, int> sourceRoleCounts =
            sourceFingerRows
                .GroupBy(
                    static row => row.Role!,
                    StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Count(),
                    StringComparer.Ordinal);
        Dictionary<string, int> sourceByRole = sourceFingerRows
            .GroupBy(
                static row => row.Role!,
                StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single().Bone.Index,
                StringComparer.Ordinal);

        var targetFingerRows = new List<(
            BoneDefinition Bone,
            string Side,
            string Digit,
            int Segment)>();
        foreach (BoneDefinition targetBone in target.Bones)
        {
            if (IsHelperTarget(targetBone))
            {
                continue;
            }

            string? targetRole =
                HumanoidBoneSemanticClassifier.Classify(
                    targetBone.SemanticRole ??
                    targetBone.Name)?.Role;
            if (!TryParseFingerRole(
                    targetRole,
                    out string side,
                    out string digit,
                    out int segment) ||
                segment is < 1 or > 3)
            {
                continue;
            }

            targetFingerRows.Add((
                targetBone,
                side,
                digit,
                segment));
        }

        foreach (IGrouping<
                     (string Side, string Digit),
                     (BoneDefinition Bone,
                      string Side,
                      string Digit,
                      int Segment)> chain in
                 targetFingerRows.GroupBy(
                     static row => (row.Side, row.Digit)))
        {
            // Never replace an exact/semantic partial chain, and never hide an
            // ambiguous same-digit source chain behind an unrelated digit.
            if (chain.Any(row =>
                    mappedTargets.Contains(row.Bone.Index)) ||
                chain.Any(row =>
                    sourceRoleCounts.ContainsKey(
                        $"finger.{row.Side}.{row.Digit}.{row.Segment}")))
            {
                continue;
            }

            string? selectedSourceDigit = null;
            foreach (string sourceDigit in
                     NearestFingerDigits(chain.Key.Digit))
            {
                if (chain.All(row =>
                        sourceByRole.ContainsKey(
                            $"finger.{row.Side}.{sourceDigit}.{row.Segment}")))
                {
                    selectedSourceDigit = sourceDigit;
                    break;
                }
            }

            if (selectedSourceDigit is null)
            {
                continue;
            }

            foreach (var row in chain)
            {
                int sourceIndex = sourceByRole[
                    $"finger.{row.Side}.{selectedSourceDigit}.{row.Segment}"];
                entries.Add(
                    new BoneMapEntry(
                        sourceIndex,
                        row.Bone.Index,
                        BoneMappingMethod.Distributed,
                        0.65,
                        transferPolicy:
                            RetargetTransferPolicy.AnatomicalDirection,
                        componentPolicy:
                            RetargetComponentPolicy.Rotation));
                mappedTargets.Add(row.Bone.Index);
            }
        }
    }

    private static bool TryParseFingerRole(
        string? role,
        out string side,
        out string digit,
        out int segment)
    {
        side = string.Empty;
        digit = string.Empty;
        segment = 0;
        if (role is null)
        {
            return false;
        }

        string[] parts = role.Split('.');
        if (parts.Length != 4 ||
            parts[0] != "finger" ||
            !int.TryParse(parts[3], out segment))
        {
            return false;
        }

        side = parts[1];
        digit = parts[2];
        return true;
    }

    private static IEnumerable<string> NearestFingerDigits(
        string digit) =>
        digit switch
        {
            "thumb" =>
                ["index", "middle", "ring", "little"],
            "index" =>
                ["middle", "ring", "little", "thumb"],
            "middle" =>
                ["index", "ring", "little", "thumb"],
            "ring" =>
                ["middle", "index", "little", "thumb"],
            "little" =>
                ["ring", "middle", "index", "thumb"],
            _ => [],
        };

    public static RetargetMap CreateNameBased(
        RigDefinition source,
        RigDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        Dictionary<string, int[]> sourceByName = source.Bones
            .GroupBy(
                static bone => NormalizeBoneName(bone.Name),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static bone => bone.Index)
                    .ToArray(),
                StringComparer.Ordinal);
        Dictionary<string, int> targetNameCounts = target.Bones
            .GroupBy(
                static bone => NormalizeBoneName(bone.Name),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);

        var entries = ImmutableArray.CreateBuilder<BoneMapEntry>();
        foreach (BoneDefinition targetBone in target.Bones)
        {
            string normalized = NormalizeBoneName(targetBone.Name);
            if (!sourceByName.TryGetValue(
                    normalized,
                    out int[]? sourceIndexes) ||
                sourceIndexes.Length != 1 ||
                targetNameCounts[normalized] != 1)
            {
                continue;
            }

            int sourceIndex = sourceIndexes[0];
            bool exact = string.Equals(
                source.Bones[sourceIndex].Name,
                targetBone.Name,
                StringComparison.OrdinalIgnoreCase);
            entries.Add(
                new BoneMapEntry(
                    sourceIndex,
                    targetBone.Index,
                    exact ? BoneMappingMethod.ExactName : BoneMappingMethod.NormalizedName,
                    exact ? 1.0 : 0.95,
                    mappingKind: IsHelperTarget(targetBone)
                        ? RetargetMappingKind.HelperOverride
                        : RetargetMappingKind.Bone,
                    transferPolicy: IsHelperTarget(targetBone)
                        ? RetargetTransferPolicy.RestRelative
                        : RetargetTransferPolicy.GlobalBindBasis,
                    componentPolicy: IsHelperTarget(targetBone)
                        ? GetDefaultHelperComponentPolicy(targetBone.Name)
                        : RetargetComponentPolicy.FullTransform));
        }

        return new RetargetMap(
            source.Id,
            target.Id,
            entries
                .Select(entry =>
                    ApplyAutomaticBodyTransferPolicy(
                        entry,
                        source,
                        target))
                .ToImmutableArray());
    }

    private static BoneMapEntry ApplyAutomaticBodyTransferPolicy(
        BoneMapEntry entry,
        RigDefinition source,
        RigDefinition target)
    {
        if (entry.MappingKind != RetargetMappingKind.Bone ||
            HasTargetCompatibleBindChain(
                source,
                target,
                entry) ||
            target.Bones[entry.TargetBoneIndex].ParentIndex < 0)
        {
            return entry;
        }

        // Cross-skeleton anatomical matches do not own target lengths or
        // scale. The validated DL1 humanoid path reconstructs torso and limb
        // rotations from joint directions in the animated body frame; this
        // accounts for the Mixamo T-pose versus DL1 authored bind without
        // copying source translations. Rows outside that validated semantic
        // subset keep the established target-local rotation delta.
        bool useAnatomicalDirection =
            HumanoidAnatomicalRetargeter.SupportsTargetRole(
                target.Bones[entry.TargetBoneIndex].SemanticRole ??
                target.Bones[entry.TargetBoneIndex].Name);
        return new BoneMapEntry(
            entry.SourceBoneIndex,
            entry.TargetBoneIndex,
            entry.Method,
            entry.Confidence,
            entry.IsLocked,
            entry.IsReviewed,
            entry.MappingKind,
            useAnatomicalDirection
                ? RetargetTransferPolicy.AnatomicalDirection
                : RetargetTransferPolicy.RotationDelta,
            RetargetComponentPolicy.Rotation);
    }

    private static bool HasTargetCompatibleBindChain(
        RigDefinition source,
        RigDefinition target,
        BoneMapEntry entry)
    {
        int sourceIndex = entry.SourceBoneIndex;
        int targetIndex = entry.TargetBoneIndex;
        while (sourceIndex >= 0 && targetIndex >= 0)
        {
            if ((uint)sourceIndex >= (uint)source.BoneCount ||
                (uint)targetIndex >= (uint)target.BoneCount)
            {
                return false;
            }

            BoneDefinition sourceBone = source.Bones[sourceIndex];
            BoneDefinition targetBone = target.Bones[targetIndex];
            if (!string.Equals(
                    sourceBone.Name,
                    targetBone.Name,
                    StringComparison.OrdinalIgnoreCase) ||
                !sourceBone.LocalBindPose
                    .ToMatrix()
                    .NearlyEquals(
                        targetBone.LocalBindPose.ToMatrix(),
                        1e-6))
            {
                return false;
            }

            sourceIndex = sourceBone.ParentIndex;
            targetIndex = targetBone.ParentIndex;
        }

        // Both chains must reach their roots together. An extra ancestor on
        // either side changes the global bind basis even when the leaf names
        // and local transforms happen to match.
        return sourceIndex < 0 && targetIndex < 0;
    }

    private static void AddNameMatches(
        RigDefinition source,
        RigDefinition target,
        ImmutableArray<BoneMapEntry>.Builder entries,
        HashSet<int> mappedSources,
        HashSet<int> mappedTargets)
    {
        var sourcesByName = source.Bones
            .GroupBy(
                static bone => NormalizeBoneName(bone.Name),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static bone => bone.Index).ToArray(),
                StringComparer.Ordinal);

        foreach (BoneDefinition targetBone in target.Bones)
        {
            if (mappedTargets.Contains(targetBone.Index) ||
                IsHelperTarget(targetBone))
            {
                continue;
            }

            string key = NormalizeBoneName(targetBone.Name);
            if (!sourcesByName.TryGetValue(key, out int[]? candidates))
            {
                continue;
            }

            int[] available = candidates
                .Where(candidate => !mappedSources.Contains(candidate))
                .ToArray();
            if (available.Length != 1)
            {
                continue;
            }

            int sourceIndex = available[0];
            bool exact = string.Equals(
                source.Bones[sourceIndex].Name,
                targetBone.Name,
                StringComparison.OrdinalIgnoreCase);
            entries.Add(
                new BoneMapEntry(
                    sourceIndex,
                    targetBone.Index,
                    exact
                        ? BoneMappingMethod.ExactName
                        : BoneMappingMethod.NormalizedName,
                    exact ? 1.0 : 0.95));
            mappedSources.Add(sourceIndex);
            mappedTargets.Add(targetBone.Index);
        }
    }

    private static void AddUniqueMatches(
        RigDefinition source,
        RigDefinition target,
        ImmutableArray<BoneMapEntry>.Builder entries,
        HashSet<int> mappedSources,
        HashSet<int> mappedTargets,
        Func<BoneDefinition, string?> keySelector,
        BoneMappingMethod method,
        double confidence)
    {
        Dictionary<string, int[]> sourcesByKey = source.Bones
            .Select(bone => (Bone: bone, Key: keySelector(bone)))
            .Where(static row => row.Key is not null)
            .GroupBy(
                static row => row.Key!,
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static row => row.Bone.Index)
                    .ToArray(),
                StringComparer.Ordinal);
        Dictionary<string, int> targetKeyCounts = target.Bones
            .Select(bone => keySelector(bone))
            .Where(static key => key is not null)
            .GroupBy(static key => key!, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);

        foreach (BoneDefinition targetBone in target.Bones)
        {
            if (mappedTargets.Contains(targetBone.Index) ||
                IsHelperTarget(targetBone))
            {
                continue;
            }

            string? key = keySelector(targetBone);
            if (key is null ||
                !targetKeyCounts.TryGetValue(key, out int targetCount) ||
                targetCount != 1 ||
                !sourcesByKey.TryGetValue(key, out int[]? candidates))
            {
                continue;
            }

            int[] available = candidates
                .Where(candidate => !mappedSources.Contains(candidate))
                .ToArray();
            if (available.Length != 1)
            {
                continue;
            }

            int sourceIndex = available[0];
            entries.Add(
                new BoneMapEntry(
                    sourceIndex,
                    targetBone.Index,
                    method,
                    confidence));
            mappedSources.Add(sourceIndex);
            mappedTargets.Add(targetBone.Index);
        }
    }

    private static void AddStructuralMatches(
        RigDefinition source,
        RigDefinition target,
        ImmutableArray<BoneMapEntry>.Builder entries,
        HashSet<int> mappedSources,
        HashSet<int> mappedTargets)
    {
        int[] sourceDepths = ComputeDepths(source);
        int[] targetDepths = ComputeDepths(target);
        int[] sourceChildCounts = ComputeChildCounts(source);
        int[] targetChildCounts = ComputeChildCounts(target);

        bool progress;
        do
        {
            progress = false;
            foreach (BoneDefinition targetBone in target.Bones)
            {
                if (mappedTargets.Contains(targetBone.Index) ||
                    IsHelperTarget(targetBone))
                {
                    continue;
                }

                int? mappedSourceParent = FindMappedSourceParent(
                    targetBone.ParentIndex,
                    entries);
                int[] candidates = source.Bones
                    .Where(sourceBone =>
                        !mappedSources.Contains(sourceBone.Index) &&
                        sourceBone.Kind == targetBone.Kind &&
                        sourceDepths[sourceBone.Index] ==
                            targetDepths[targetBone.Index] &&
                        sourceChildCounts[sourceBone.Index] ==
                            targetChildCounts[targetBone.Index] &&
                        (!mappedSourceParent.HasValue ||
                         sourceBone.ParentIndex == mappedSourceParent.Value))
                    .Select(static bone => bone.Index)
                    .ToArray();
                if (candidates.Length != 1)
                {
                    continue;
                }

                int sourceIndex = candidates[0];
                entries.Add(
                    new BoneMapEntry(
                        sourceIndex,
                        targetBone.Index,
                        BoneMappingMethod.Structural,
                        0.7));
                mappedSources.Add(sourceIndex);
                mappedTargets.Add(targetBone.Index);
                progress = true;
            }
        }
        while (progress);
    }

    private static void AddHelperMatches(
        RigDefinition source,
        RigDefinition target,
        ImmutableArray<BoneMapEntry>.Builder entries,
        HashSet<int> mappedTargets)
    {
        Dictionary<string, int[]> sourcesByName = source.Bones
            .GroupBy(
                static bone => NormalizeBoneName(bone.Name),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static bone => bone.Index)
                    .ToArray(),
                StringComparer.Ordinal);
        Dictionary<string, int> targetNameCounts = target.Bones
            .GroupBy(
                static bone => NormalizeBoneName(bone.Name),
                StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Count(),
                StringComparer.Ordinal);

        foreach (BoneDefinition targetBone in target.Bones)
        {
            if (!IsHelperTarget(targetBone) ||
                mappedTargets.Contains(targetBone.Index))
            {
                continue;
            }

            string targetKey = NormalizeBoneName(targetBone.Name);
            if (targetNameCounts[targetKey] != 1)
            {
                continue;
            }

            int sourceIndex = -1;
            BoneMappingMethod method = BoneMappingMethod.Semantic;
            double confidence = 0.7;
            if (sourcesByName.TryGetValue(
                    targetKey,
                    out int[]? exactCandidates))
            {
                // A duplicated same-name helper is ambiguous. Do not silently
                // fall back to a body bone in that case.
                if (exactCandidates.Length != 1)
                {
                    continue;
                }

                sourceIndex = exactCandidates[0];
                bool exact = string.Equals(
                    source.Bones[sourceIndex].Name,
                    targetBone.Name,
                    StringComparison.OrdinalIgnoreCase);
                method = exact
                    ? BoneMappingMethod.ExactName
                    : BoneMappingMethod.NormalizedName;
                confidence = exact ? 1.0 : 0.95;
            }
            else
            {
                sourceIndex = FindSuggestedHelperSource(
                    source,
                    targetKey);
            }

            if (sourceIndex < 0)
            {
                continue;
            }

            entries.Add(
                new BoneMapEntry(
                    sourceIndex,
                    targetBone.Index,
                    method,
                    confidence,
                    mappingKind: RetargetMappingKind.HelperOverride,
                    transferPolicy: RetargetTransferPolicy.RestRelative,
                    componentPolicy: GetDefaultHelperComponentPolicy(
                        targetBone.Name)));
            mappedTargets.Add(targetBone.Index);
        }
    }

    private static int FindSuggestedHelperSource(
        RigDefinition source,
        string normalizedTargetName)
    {
        string[] suggestions = normalizedTargetName switch
        {
            "REFCAMERA" => ["REFCAMERA", "CAMERA", "HEAD"],
            "EYECAMERA" => ["EYECAMERA", "CAMERA", "HEAD"],
            "HEADEND" => ["HEADEND", "HEAD"],
            "EYES" => ["EYES", "HEAD"],
            "LHANDHOLDER" => ["LEFTHANDHOLDER", "LEFTHAND"],
            "RHANDHOLDER" => ["RIGHTHANDHOLDER", "RIGHTHAND"],
            "LNORMAL" or "LNORMAL2" => ["LEFTSHOULDER", "LEFTARM"],
            "RNORMAL" or "RNORMAL2" => ["RIGHTSHOULDER", "RIGHTARM"],
            "LFINGER01EXTRA" => ["LEFTHANDTHUMB1", "LEFTHAND"],
            "RFINGER01EXTRA" => ["RIGHTHANDTHUMB1", "RIGHTHAND"],
            "LFORETWIST" or "LFORETWIST1" or "LFORETWISTT" =>
                ["LEFTFOREARM"],
            "RFORETWIST" or "RFORETWIST1" or "RFORETWISTT" =>
                ["RIGHTFOREARM"],
            "LUPARMTWIST" => ["LEFTARM"],
            "RUPARMTWIST" => ["RIGHTARM"],
            "LTHIGHTWIST" => ["LEFTUPLEG"],
            "RTHIGHTWIST" => ["RIGHTUPLEG"],
            "PROPSHOLDER1" => ["PROPHOLDER1", "RIGHTHAND", "LEFTHAND"],
            "PROPSHOLDER2" => ["PROPHOLDER2", "LEFTHAND", "RIGHTHAND"],
            "FLASHLIGHT" => ["FLASHLIGHT", "RIGHTHAND"],
            _ => [],
        };

        foreach (string suggestion in suggestions)
        {
            int[] exact = source.Bones
                .Where(bone =>
                    NormalizeBoneName(bone.Name) == suggestion)
                .Select(static bone => bone.Index)
                .ToArray();
            if (exact.Length == 1)
            {
                return exact[0];
            }

            HumanoidBoneSemanticMatch? semantic =
                HumanoidBoneSemanticClassifier.Classify(suggestion);
            if (semantic is null)
            {
                continue;
            }

            int[] semanticCandidates = source.Bones
                .Where(bone =>
                    HumanoidBoneSemanticClassifier.Classify(
                        bone.SemanticRole ??
                        bone.Name)?.Role == semantic.Role)
                .Select(static bone => bone.Index)
                .ToArray();
            if (semanticCandidates.Length == 1)
            {
                return semanticCandidates[0];
            }
        }

        return -1;
    }

    private static bool IsHelperTarget(BoneDefinition bone) =>
        bone.Kind is BoneKind.Helper or BoneKind.Camera or BoneKind.Prop;

    public static RetargetComponentPolicy GetDefaultHelperComponentPolicy(
        string targetName) =>
        NormalizeBoneName(targetName) switch
        {
            "REFCAMERA" => RetargetComponentPolicy.Translation,
            "EYECAMERA" or "EYES" or "LHANDHOLDER" or "RHANDHOLDER" or
            "PROPSHOLDER1" or "PROPSHOLDER2" or "FLASHLIGHT" =>
                RetargetComponentPolicy.RotationTranslation,
            "HEADEND" or "LNORMAL" or "LNORMAL2" or "RNORMAL" or
            "RNORMAL2" or "LFINGER01EXTRA" or "RFINGER01EXTRA" or
            "LFORETWIST" or "LFORETWIST1" or "LFORETWISTT" or
            "RFORETWIST" or "RFORETWIST1" or "RFORETWISTT" or
            "LUPARMTWIST" or "RUPARMTWIST" or "LTHIGHTWIST" or
            "RTHIGHTWIST" =>
                RetargetComponentPolicy.Rotation,
            _ => RetargetComponentPolicy.FullTransform,
        };

    private static int? FindMappedSourceParent(
        int targetParentIndex,
        ImmutableArray<BoneMapEntry>.Builder entries)
    {
        if (targetParentIndex < 0)
        {
            return -1;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index].TargetBoneIndex == targetParentIndex)
            {
                return entries[index].SourceBoneIndex;
            }
        }

        return null;
    }

    private static int[] ComputeDepths(RigDefinition rig)
    {
        int[] depths = new int[rig.BoneCount];
        foreach (BoneDefinition bone in rig.Bones)
        {
            depths[bone.Index] = bone.ParentIndex < 0
                ? 0
                : checked(depths[bone.ParentIndex] + 1);
        }

        return depths;
    }

    private static int[] ComputeChildCounts(RigDefinition rig)
    {
        int[] counts = new int[rig.BoneCount];
        foreach (BoneDefinition bone in rig.Bones)
        {
            if (bone.ParentIndex >= 0)
            {
                counts[bone.ParentIndex]++;
            }
        }

        return counts;
    }

    public static string NormalizeBoneName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Normalize(NormalizationForm.FormKC);
        int namespaceSeparator = normalized.LastIndexOf(':');
        if (namespaceSeparator >= 0 && namespaceSeparator < normalized.Length - 1)
        {
            normalized = normalized[(namespaceSeparator + 1)..];
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}
