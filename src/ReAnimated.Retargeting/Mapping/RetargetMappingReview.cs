using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Retargeting.Mapping;

public sealed class RetargetMappingReviewReport
{
    internal RetargetMappingReviewReport(
        IEnumerable<CompatibilityDiagnostic> diagnostics,
        CompatibilityReport compatibility)
    {
        Diagnostics = diagnostics.ToImmutableArray();
        Compatibility = compatibility;
    }

    public ImmutableArray<CompatibilityDiagnostic> Diagnostics { get; }

    public CompatibilityReport Compatibility { get; }

    public bool IsReady =>
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != CompatibilityDiagnosticSeverity.Error);

    public int ExplicitReviewRequiredCount =>
        Diagnostics.Count(static diagnostic =>
            diagnostic.Code == "mapping_row_requires_review");

    public int RequiredTargetBindReviewCount =>
        Diagnostics.Count(static diagnostic =>
            diagnostic.Code == "required_target_unmapped");
}

/// <summary>
/// Fail-closed review policy shared by WPF and CLI export. Descriptor-hash and
/// exact-name rows are accepted automatically only when both their claimed
/// identity and their default transfer/component policy still verify. A valid
/// identity with an edited policy can be explicitly reviewed; a false identity
/// claim cannot. Every other row and every required target-bind fallback needs
/// durable explicit review.
/// </summary>
public static class RetargetMappingReview
{
    public static RetargetMappingReviewReport Analyze(
        RigDefinition source,
        RigDefinition target,
        RetargetMap map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(map);

        CompatibilityReport compatibility =
            RigCompatibilityAnalyzer.Analyze(source, target, map);
        var diagnostics =
            ImmutableArray.CreateBuilder<CompatibilityDiagnostic>();
        diagnostics.AddRange(compatibility.Diagnostics);

        foreach (BoneMapEntry entry in map.Entries)
        {
            if ((uint)entry.SourceBoneIndex >= (uint)source.BoneCount ||
                (uint)entry.TargetBoneIndex >= (uint)target.BoneCount)
            {
                continue;
            }

            BoneDefinition sourceBone = source.Bones[entry.SourceBoneIndex];
            BoneDefinition targetBone = target.Bones[entry.TargetBoneIndex];
            bool deterministicIdentity =
                IsVerifiedDeterministicIdentity(
                    source,
                    target,
                    entry);
            bool automaticPolicy =
                deterministicIdentity &&
                IsVerifiedAutomaticPolicy(target, entry);
            if (entry.Method is
                    BoneMappingMethod.DescriptorHash or
                    BoneMappingMethod.ExactName &&
                !deterministicIdentity)
            {
                diagnostics.Add(
                    new CompatibilityDiagnostic(
                        "deterministic_mapping_identity_mismatch",
                        CompatibilityDiagnosticSeverity.Error,
                        $"Mapping '{sourceBone.Name}' -> '{targetBone.Name}' claims {entry.Method} but the loaded rig identities do not verify it.",
                        sourceBone.Name,
                        targetBone.Name));
                continue;
            }

            if (!automaticPolicy && !entry.IsReviewed)
            {
                diagnostics.Add(
                    new CompatibilityDiagnostic(
                        "mapping_row_requires_review",
                        CompatibilityDiagnosticSeverity.Error,
                        $"Mapping '{sourceBone.Name}' -> '{targetBone.Name}' was proposed by {entry.Method} with {entry.TransferPolicy}/{entry.ComponentPolicy} policy and requires explicit review before export.",
                        sourceBone.Name,
                        targetBone.Name));
            }
        }

        return new RetargetMappingReviewReport(
            diagnostics,
            compatibility);
    }

    /// <summary>
    /// Returns true only when both the claimed identity and the transfer policy
    /// are safe to accept without explicit review.
    /// </summary>
    public static bool IsVerifiedDeterministicMatch(
        RigDefinition source,
        RigDefinition target,
        BoneMapEntry entry)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(entry);
        return IsVerifiedDeterministicIdentity(
                source,
                target,
                entry) &&
            IsVerifiedAutomaticPolicy(target, entry);
    }

    /// <summary>
    /// Verifies only the row's claimed deterministic source/target identity.
    /// Transfer and component-policy edits are intentionally reviewed
    /// separately so a valid identity does not become a false identity error.
    /// </summary>
    public static bool IsVerifiedDeterministicIdentity(
        RigDefinition source,
        RigDefinition target,
        BoneMapEntry entry)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(entry);
        if ((uint)entry.SourceBoneIndex >= (uint)source.BoneCount ||
            (uint)entry.TargetBoneIndex >= (uint)target.BoneCount)
        {
            return false;
        }

        BoneDefinition sourceBone = source.Bones[entry.SourceBoneIndex];
        BoneDefinition targetBone = target.Bones[entry.TargetBoneIndex];
        if (entry.MappingKind == RetargetMappingKind.HelperOverride)
        {
            return entry.Method == BoneMappingMethod.ExactName &&
                targetBone.Kind is
                    BoneKind.Helper or BoneKind.Camera or BoneKind.Prop &&
                source.GetBoneIndices(sourceBone.Name).Length == 1 &&
                target.GetBoneIndices(targetBone.Name).Length == 1 &&
                string.Equals(
                    sourceBone.Name,
                    targetBone.Name,
                    StringComparison.OrdinalIgnoreCase);
        }

        return entry.Method switch
        {
            BoneMappingMethod.DescriptorHash =>
                sourceBone.DescriptorHash.HasValue &&
                sourceBone.DescriptorHash ==
                    targetBone.DescriptorHash,
            BoneMappingMethod.ExactName =>
                string.Equals(
                    sourceBone.Name,
                    targetBone.Name,
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsVerifiedAutomaticPolicy(
        RigDefinition target,
        BoneMapEntry entry)
    {
        if ((uint)entry.TargetBoneIndex >= (uint)target.BoneCount)
        {
            return false;
        }

        if (entry.MappingKind == RetargetMappingKind.HelperOverride)
        {
            BoneDefinition targetBone =
                target.Bones[entry.TargetBoneIndex];
            return entry.TransferPolicy ==
                    RetargetTransferPolicy.RestRelative &&
                entry.ComponentPolicy ==
                    RetargetMapBuilder.GetDefaultHelperComponentPolicy(
                        targetBone.Name);
        }

        return entry.TransferPolicy ==
                RetargetTransferPolicy.GlobalBindBasis &&
            entry.ComponentPolicy ==
                RetargetComponentPolicy.FullTransform;
    }

}
