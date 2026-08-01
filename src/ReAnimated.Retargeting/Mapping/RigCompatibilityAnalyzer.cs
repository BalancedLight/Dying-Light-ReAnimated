using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Retargeting.Mapping;

public enum CompatibilityClassification
{
    ExactIdentity,
    TargetCompatibleSourceSuperset,
    Retargetable,
    TargetWithBindFallback,
    Incompatible,
}

public enum CompatibilityDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record CompatibilityDiagnostic(
    string Code,
    CompatibilityDiagnosticSeverity Severity,
    string Message,
    string? SourceBoneName = null,
    string? TargetBoneName = null);

public sealed class CompatibilityReport
{
    public CompatibilityReport(
        CompatibilityClassification classification,
        IEnumerable<CompatibilityDiagnostic> diagnostics,
        IEnumerable<int> unmappedRequiredTargetBones,
        IEnumerable<int> bindFallbackTargetBones,
        IEnumerable<int> extraSourceBones)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(unmappedRequiredTargetBones);
        ArgumentNullException.ThrowIfNull(bindFallbackTargetBones);
        ArgumentNullException.ThrowIfNull(extraSourceBones);

        Classification = classification;
        Diagnostics = diagnostics.ToImmutableArray();
        UnmappedRequiredTargetBones = unmappedRequiredTargetBones.ToImmutableArray();
        BindFallbackTargetBones = bindFallbackTargetBones.ToImmutableArray();
        ExtraSourceBones = extraSourceBones.ToImmutableArray();
    }

    public CompatibilityClassification Classification { get; }

    public ImmutableArray<CompatibilityDiagnostic> Diagnostics { get; }

    public ImmutableArray<int> UnmappedRequiredTargetBones { get; }

    public ImmutableArray<int> BindFallbackTargetBones { get; }

    public ImmutableArray<int> ExtraSourceBones { get; }

    public bool CanEvaluate =>
        Diagnostics.All(
            static diagnostic =>
                diagnostic.Severity != CompatibilityDiagnosticSeverity.Error);
}

public static class RigCompatibilityAnalyzer
{
    public static CompatibilityReport Analyze(
        RigDefinition source,
        RigDefinition target,
        RetargetMap map,
        IEnumerable<int>? reviewedTargetBindBones = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(map);
        HashSet<int> targetBindBones =
            (reviewedTargetBindBones ??
             map.ReviewedTargetBindBoneIndices)
            .ToHashSet();
        if (targetBindBones.Any(
                targetBoneIndex =>
                    (uint)targetBoneIndex >= (uint)target.BoneCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reviewedTargetBindBones),
                "A reviewed target-bind bone is outside the target rig.");
        }

        var diagnostics = ImmutableArray.CreateBuilder<CompatibilityDiagnostic>();
        if (!string.Equals(map.SourceRigId, source.Id, StringComparison.Ordinal))
        {
            diagnostics.Add(
                new(
                    "source_rig_identity_mismatch",
                    CompatibilityDiagnosticSeverity.Error,
                    $"The map expects source rig '{map.SourceRigId}', not '{source.Id}'."));
        }

        if (!string.Equals(map.TargetRigId, target.Id, StringComparison.Ordinal))
        {
            diagnostics.Add(
                new(
                    "target_rig_identity_mismatch",
                    CompatibilityDiagnosticSeverity.Error,
                    $"The map expects target rig '{map.TargetRigId}', not '{target.Id}'."));
        }

        var validEntries = new List<BoneMapEntry>(map.Entries.Length);
        foreach (BoneMapEntry entry in map.Entries)
        {
            if (entry.SourceBoneIndex >= source.BoneCount ||
                entry.TargetBoneIndex >= target.BoneCount)
            {
                diagnostics.Add(
                    new(
                        "mapping_index_out_of_range",
                        CompatibilityDiagnosticSeverity.Error,
                        $"Mapping {entry.SourceBoneIndex} -> {entry.TargetBoneIndex} is outside its rig."));
                continue;
            }

            validEntries.Add(entry);
            BoneDefinition sourceBone =
                source.Bones[entry.SourceBoneIndex];
            BoneDefinition targetBone =
                target.Bones[entry.TargetBoneIndex];
            if (entry.MappingKind == RetargetMappingKind.HelperOverride &&
                targetBone.Kind is
                    BoneKind.Root or BoneKind.Deform)
            {
                diagnostics.Add(
                    new(
                        "helper_override_targets_body_bone",
                        CompatibilityDiagnosticSeverity.Warning,
                        $"Helper override targets body bone '{targetBone.Name}'. Confirm that this is intentional.",
                        sourceBone.Name,
                        targetBone.Name));
            }

            if (entry.MappingKind == RetargetMappingKind.HelperOverride &&
                entry.ComponentPolicy ==
                    RetargetComponentPolicy.FullTransform)
            {
                diagnostics.Add(
                    new(
                        "helper_full_transform_changes_scale",
                        CompatibilityDiagnosticSeverity.Warning,
                        $"Full-transform helper mapping for '{targetBone.Name}' can overwrite its target bind scale.",
                        sourceBone.Name,
                        targetBone.Name));

                if (targetBone.Kind == BoneKind.Camera)
                {
                    diagnostics.Add(
                        new(
                            "camera_helper_full_transform_unsafe",
                            CompatibilityDiagnosticSeverity.Warning,
                            $"Camera helper '{targetBone.Name}' uses full-transform transfer. Prefer its component-limited DL1 helper profile unless this row was deliberately reviewed.",
                            sourceBone.Name,
                            targetBone.Name));
                }
            }

            if (entry.Confidence < 0.75)
            {
                diagnostics.Add(
                    new(
                        "mapping_requires_review",
                        CompatibilityDiagnosticSeverity.Warning,
                        $"The mapping for '{targetBone.Name}' has low confidence.",
                        sourceBone.Name,
                        targetBone.Name));
            }
        }

        BoneMapEntry[] baseEntries = validEntries
            .Where(static entry =>
                entry.MappingKind == RetargetMappingKind.Bone)
            .ToArray();
        BoneMapEntry[] helperEntries = validEntries
            .Where(static entry =>
                entry.MappingKind == RetargetMappingKind.HelperOverride)
            .ToArray();
        HashSet<int> mappedTargets = validEntries
            .Select(static entry => entry.TargetBoneIndex)
            .ToHashSet();
        HashSet<int> mappedSources = baseEntries
            .Select(static entry => entry.SourceBoneIndex)
            .ToHashSet();

        foreach (IGrouping<int, BoneMapEntry> fanout in helperEntries
                     .GroupBy(static entry => entry.SourceBoneIndex)
                     .Where(group =>
                         group.Count() +
                         baseEntries.Count(entry =>
                             entry.SourceBoneIndex == group.Key) >
                         1))
        {
            string sourceName = source.Bones[fanout.Key].Name;
            string targets = string.Join(
                ", ",
                baseEntries
                    .Where(entry =>
                        entry.SourceBoneIndex == fanout.Key)
                    .Concat(fanout)
                    .Select(entry =>
                        target.Bones[entry.TargetBoneIndex].Name));
            diagnostics.Add(
                new(
                    "helper_source_fanout",
                    CompatibilityDiagnosticSeverity.Information,
                    $"Source bone '{sourceName}' drives multiple target tracks: {targets}.",
                    SourceBoneName: sourceName));
        }

        int[] missingRequired = target.Bones
            .Where(bone =>
                bone.RequiredForExport &&
                !mappedTargets.Contains(bone.Index) &&
                !targetBindBones.Contains(bone.Index))
            .Select(static bone => bone.Index)
            .ToArray();
        int[] bindFallback = target.Bones
            .Where(bone =>
                !mappedTargets.Contains(bone.Index) &&
                (!bone.RequiredForExport ||
                 targetBindBones.Contains(bone.Index)))
            .Select(static bone => bone.Index)
            .ToArray();
        int[] extraSource = source.Bones
            .Where(bone => !mappedSources.Contains(bone.Index))
            .Select(static bone => bone.Index)
            .ToArray();

        foreach (int targetIndex in missingRequired)
        {
            diagnostics.Add(
                new(
                    "required_target_unmapped",
                    CompatibilityDiagnosticSeverity.Error,
                    $"Required target bone '{target.Bones[targetIndex].Name}' is not mapped.",
                    TargetBoneName: target.Bones[targetIndex].Name));
        }

        foreach (int targetIndex in bindFallback)
        {
            bool required = target.Bones[targetIndex].RequiredForExport;
            diagnostics.Add(
                new(
                    required
                        ? "reviewed_required_target_bind"
                        : "optional_target_bind_fallback",
                    CompatibilityDiagnosticSeverity.Warning,
                    required
                        ? $"Required target bone '{target.Bones[targetIndex].Name}' is explicitly owned by its target bind-local track."
                        : $"Optional target bone '{target.Bones[targetIndex].Name}' will remain at bind pose.",
                    TargetBoneName: target.Bones[targetIndex].Name));
        }

        if (extraSource.Length > 0)
        {
            diagnostics.Add(
                new(
                    "source_has_extra_bones",
                    CompatibilityDiagnosticSeverity.Information,
                    $"The source has {extraSource.Length} unmapped bone(s)."));
        }

        Dictionary<int, int> sourceByTarget = baseEntries.ToDictionary(
            static entry => entry.TargetBoneIndex,
            static entry => entry.SourceBoneIndex);
        int hierarchyMismatchCount = 0;
        foreach (BoneMapEntry entry in baseEntries)
        {
            int targetParent = target.Bones[entry.TargetBoneIndex].ParentIndex;
            if (targetParent < 0 ||
                !sourceByTarget.TryGetValue(targetParent, out int expectedSourceParent))
            {
                continue;
            }

            int actualSourceParent = source.Bones[entry.SourceBoneIndex].ParentIndex;
            if (actualSourceParent == expectedSourceParent)
            {
                continue;
            }

            hierarchyMismatchCount++;
            diagnostics.Add(
                new(
                    "mapped_hierarchy_differs",
                    CompatibilityDiagnosticSeverity.Warning,
                    $"Mapped ancestry differs for target bone '{target.Bones[entry.TargetBoneIndex].Name}'.",
                    source.Bones[entry.SourceBoneIndex].Name,
                    target.Bones[entry.TargetBoneIndex].Name));
        }

        SkeletonPose sourceBindPose = source.CreateBindPose();
        SkeletonPose targetBindPose = target.CreateBindPose();
        int bindMismatchCount = baseEntries.Count(
            entry =>
                !sourceBindPose.GlobalMatrices[entry.SourceBoneIndex].NearlyEquals(
                    targetBindPose.GlobalMatrices[entry.TargetBoneIndex],
                    1e-5));
        if (bindMismatchCount > 0)
        {
            diagnostics.Add(
                new(
                    "mapped_bind_pose_differs",
                    CompatibilityDiagnosticSeverity.Information,
                    $"{bindMismatchCount} mapped bone(s) use different global bind transforms; bind-basis correction is required."));
        }

        int sourceBodyBoneCount = source.Bones.Count(static bone =>
            bone.Kind is BoneKind.Root or BoneKind.Deform);
        int targetBodyBoneCount = target.Bones.Count(static bone =>
            bone.Kind is BoneKind.Root or BoneKind.Deform);
        bool exactNamesAndIndices =
            sourceBodyBoneCount == targetBodyBoneCount &&
            baseEntries.Length == targetBodyBoneCount &&
            baseEntries.All(
                entry =>
                    source.Bones[entry.SourceBoneIndex].Kind is
                        BoneKind.Root or BoneKind.Deform &&
                    target.Bones[entry.TargetBoneIndex].Kind is
                        BoneKind.Root or BoneKind.Deform &&
                    string.Equals(
                        source.Bones[entry.SourceBoneIndex].Name,
                        target.Bones[entry.TargetBoneIndex].Name,
                        StringComparison.OrdinalIgnoreCase));

        CompatibilityClassification classification;
        if (diagnostics.Any(
                static diagnostic =>
                    diagnostic.Severity == CompatibilityDiagnosticSeverity.Error))
        {
            classification = CompatibilityClassification.Incompatible;
        }
        else if (exactNamesAndIndices &&
                 hierarchyMismatchCount == 0 &&
                 bindMismatchCount == 0)
        {
            classification = CompatibilityClassification.ExactIdentity;
        }
        else if (bindFallback.Length > 0)
        {
            classification = CompatibilityClassification.TargetWithBindFallback;
        }
        else if (extraSource.Length > 0 &&
                 hierarchyMismatchCount == 0 &&
                 bindMismatchCount == 0)
        {
            classification = CompatibilityClassification.TargetCompatibleSourceSuperset;
        }
        else
        {
            classification = CompatibilityClassification.Retargetable;
        }

        return new CompatibilityReport(
            classification,
            diagnostics,
            missingRequired,
            bindFallback,
            extraSource);
    }
}
