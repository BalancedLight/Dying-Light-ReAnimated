using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

/// <summary>
/// Applies a validated custom-model replacement without discarding authored
/// animation work. Contract changes invalidate only the reviews that depended
/// on the changed target contract; mappings, edit layers, IK, and attachments
/// remain available for review and repair.
/// </summary>
public static class ProjectModelReimportReconciler
{
    public static DlraProject Apply(
        DlraProject project,
        ProjectModelEntry replacementModel)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(replacementModel);
        ValidateOptionalSha256(
            replacementModel.RigSignature,
            nameof(replacementModel));
        ValidateOptionalSha256(
            replacementModel.AuthoringRigContractSignature,
            nameof(replacementModel));
        ValidateOptionalSha256(
            replacementModel.AnimationSkeletonSignature,
            nameof(replacementModel));
        ValidateOptionalSha256(
            replacementModel.MorphSignature,
            nameof(replacementModel));

        int modelIndex = -1;
        for (var index = 0; index < project.Models.Length; index++)
        {
            if (project.Models[index].Id == replacementModel.Id)
            {
                modelIndex = index;
                break;
            }
        }

        if (modelIndex < 0)
        {
            throw new ArgumentException(
                "The replacement must identify an existing project model.",
                nameof(replacementModel));
        }

        ProjectModelEntry previousModel = project.Models[modelIndex];
        string? previousAuthoringContract =
            previousModel.AuthoringRigContractSignature ??
            previousModel.RigSignature;
        string? replacementAuthoringContract =
            replacementModel.AuthoringRigContractSignature ??
            replacementModel.RigSignature;
        bool rigChanged = !string.Equals(
            previousAuthoringContract,
            replacementAuthoringContract,
            StringComparison.OrdinalIgnoreCase);
        bool morphChanged = !string.Equals(
            previousModel.MorphSignature,
            replacementModel.MorphSignature,
            StringComparison.OrdinalIgnoreCase);
        ImmutableArray<ProjectModelEntry> models = project.Models.SetItem(
            modelIndex,
            replacementModel);
        // Reimport replaces a model target. Existing animation sources remain
        // immutable and continue to point at the exact package bytes from
        // which they were sampled. ReconcileEmbeddedCustomModelStacks adds
        // source records for checked stacks in the replacement package after
        // this target-only reconciliation step.
        ImmutableArray<ProjectAnimationSource> sources =
            project.AnimationSources;
        Dictionary<Guid, ProjectAnimationSource> sourceById = sources
            .ToDictionary(static source => source.Id);

        ImmutableArray<ProjectAnimationVariant> variants = project
            .AnimationVariants
            .Select(variant => variant.TargetModelId == previousModel.Id
                ? InvalidateVariant(
                    variant,
                    sourceById.GetValueOrDefault(variant.SourceId),
                    replacementModel.RigSignature,
                    replacementModel.AnimationSkeletonSignature,
                    rigChanged,
                    morphChanged)
                : variant)
            .ToImmutableArray();

        ImmutableArray<ProjectAnimation> compatibilityAnimations = project
            .Animations
            .Select(animation =>
                animation.TargetAssetId == previousModel.AssetId ||
                animation.TargetAssetId == replacementModel.AssetId
                    ? InvalidateCompatibilityAnimation(
                        animation,
                        replacementModel.AssetId,
                        replacementModel.RigSignature,
                        replacementModel.AnimationSkeletonSignature,
                        rigChanged,
                        morphChanged)
                    : animation)
            .ToImmutableArray();

        return project with
        {
            Models = models,
            AnimationSources = sources,
            AnimationVariants = variants,
            Animations = compatibilityAnimations,
        };
    }

    private static ProjectAnimationVariant InvalidateVariant(
        ProjectAnimationVariant variant,
        ProjectAnimationSource? source,
        string? replacementRigSignature,
        string? replacementSkeletonSignature,
        bool rigChanged,
        bool morphChanged)
    {
        bool sourceKnown = source is not null;
        bool exactSource = sourceKnown &&
            string.Equals(
                source!.SourceRigSignature,
                replacementRigSignature,
                StringComparison.OrdinalIgnoreCase);
        bool compatibleEvidence = !rigChanged &&
            HasCurrentCompatibleEvidence(
                variant.BindingMode,
                variant.DirectBinding,
                source?.SourceAnimationSkeletonSignature,
                replacementSkeletonSignature,
                variant.BindingEvidenceFingerprint,
                variant.BindingPolicyVersion);
        bool preserveUnresolvedLegacy = !sourceKnown && !rigChanged;
        bool staleDirect = sourceKnown && !exactSource &&
            !compatibleEvidence &&
            variant.BindingMode != ProjectAnimationBindingMode.Retarget;
        return variant with
        {
            TargetRigSignature = replacementRigSignature,
            TargetAnimationSkeletonSignature =
                replacementSkeletonSignature,
            BindingMode = exactSource
                ? ProjectAnimationBindingMode.ExactDirect
                : compatibleEvidence
                    ? ProjectAnimationBindingMode.CompatibleDirect
                    : preserveUnresolvedLegacy
                        ? variant.BindingMode
                        : ProjectAnimationBindingMode.Retarget,
            DirectBinding = compatibleEvidence || preserveUnresolvedLegacy
                ? variant.DirectBinding
                : null,
            BindingEvidenceFingerprint = compatibleEvidence ||
                preserveUnresolvedLegacy
                ? variant.BindingEvidenceFingerprint
                : null,
            BindingPolicyVersion = compatibleEvidence ||
                preserveUnresolvedLegacy
                ? variant.BindingPolicyVersion
                : null,
            MappingFingerprint = exactSource || rigChanged || staleDirect
                ? null
                : variant.MappingFingerprint,
            BoneMappings = rigChanged
                ? InvalidateBoneReviews(variant.BoneMappings)
                : variant.BoneMappings,
            TargetBindReviews = rigChanged
                ? []
                : variant.TargetBindReviews,
            MorphBindings = morphChanged
                ? InvalidateMorphReviews(variant.MorphBindings)
                : variant.MorphBindings,
        };
    }

    private static ProjectAnimation InvalidateCompatibilityAnimation(
        ProjectAnimation animation,
        Guid replacementAssetId,
        string? replacementRigSignature,
        string? replacementSkeletonSignature,
        bool rigChanged,
        bool morphChanged)
    {
        bool sourceKnown = !string.IsNullOrWhiteSpace(
            animation.SourceRigSignature);
        bool exactSource = sourceKnown && string.Equals(
            animation.SourceRigSignature,
            replacementRigSignature,
            StringComparison.OrdinalIgnoreCase);
        bool compatibleEvidence = !rigChanged &&
            HasCurrentCompatibleEvidence(
                animation.BindingMode,
                animation.DirectBinding,
                animation.SourceAnimationSkeletonSignature,
                replacementSkeletonSignature,
                animation.BindingEvidenceFingerprint,
                animation.BindingPolicyVersion);
        bool preserveUnresolvedLegacy = !sourceKnown && !rigChanged;
        bool staleDirect = sourceKnown && !exactSource &&
            !compatibleEvidence &&
            animation.BindingMode != ProjectAnimationBindingMode.Retarget;
        return animation with
        {
            TargetAssetId = replacementAssetId,
            TargetRigSignature = replacementRigSignature,
            TargetAnimationSkeletonSignature =
                replacementSkeletonSignature,
            BindingMode = exactSource
                ? ProjectAnimationBindingMode.ExactDirect
                : compatibleEvidence
                    ? ProjectAnimationBindingMode.CompatibleDirect
                    : preserveUnresolvedLegacy
                        ? animation.BindingMode
                        : ProjectAnimationBindingMode.Retarget,
            DirectBinding = compatibleEvidence || preserveUnresolvedLegacy
                ? animation.DirectBinding
                : null,
            BindingEvidenceFingerprint = compatibleEvidence ||
                preserveUnresolvedLegacy
                ? animation.BindingEvidenceFingerprint
                : null,
            BindingPolicyVersion = compatibleEvidence ||
                preserveUnresolvedLegacy
                ? animation.BindingPolicyVersion
                : null,
            MappingFingerprint = exactSource || rigChanged || staleDirect
                ? null
                : animation.MappingFingerprint,
            BoneMappings = rigChanged
                ? InvalidateBoneReviews(animation.BoneMappings)
                : animation.BoneMappings,
            TargetBindReviews = rigChanged
                ? []
                : animation.TargetBindReviews,
            MorphBindings = morphChanged
                ? InvalidateMorphReviews(animation.MorphBindings)
                : animation.MorphBindings,
        };
    }

    private static bool HasCurrentCompatibleEvidence(
        ProjectAnimationBindingMode bindingMode,
        DirectRigBinding? directBinding,
        string? sourceSkeletonSignature,
        string? targetSkeletonSignature,
        string? evidenceFingerprint,
        string? policyVersion) =>
        bindingMode == ProjectAnimationBindingMode.CompatibleDirect &&
        directBinding is not null &&
        string.Equals(
            sourceSkeletonSignature,
            directBinding.SourceSkeletonSignature,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            targetSkeletonSignature,
            directBinding.TargetSkeletonSignature,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            evidenceFingerprint,
            directBinding.EvidenceFingerprint,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            policyVersion,
            directBinding.Policy,
            StringComparison.Ordinal);

    private static void ValidateOptionalSha256(
        string? value,
        string parameterName)
    {
        if (value is not null)
        {
            ProjectAssetReference.ValidateSha256(value, parameterName);
        }
    }

    private static ImmutableArray<ProjectBoneMapping> InvalidateBoneReviews(
        ImmutableArray<ProjectBoneMapping> mappings) =>
        mappings
            .Select(static mapping => mapping with
            {
                ReviewOrigin = ProjectMappingReviewOrigin.None,
                IsReviewed = false,
                IsLocked = false,
            })
            .ToImmutableArray();

    private static ImmutableArray<ProjectMorphBinding> InvalidateMorphReviews(
        ImmutableArray<ProjectMorphBinding> mappings) =>
        mappings
            .Select(static mapping => mapping with
            {
                ReviewOrigin = ProjectMappingReviewOrigin.None,
                IsReviewed = false,
                IsLocked = false,
            })
            .ToImmutableArray();
}
