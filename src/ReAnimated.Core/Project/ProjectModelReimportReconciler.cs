using System.Collections.Immutable;

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
        bool rigChanged = !string.Equals(
            previousModel.RigSignature,
            replacementModel.RigSignature,
            StringComparison.OrdinalIgnoreCase);
        bool morphChanged = !string.Equals(
            previousModel.MorphSignature,
            replacementModel.MorphSignature,
            StringComparison.OrdinalIgnoreCase);
        bool assetChanged =
            previousModel.AssetId != replacementModel.AssetId;

        ImmutableArray<ProjectModelEntry> models = project.Models.SetItem(
            modelIndex,
            replacementModel);
        if (!rigChanged && !morphChanged)
        {
            return project with
            {
                Models = models,
                Animations = assetChanged
                    ? RetargetCompatibilityAssets(
                        project.Animations,
                        previousModel.AssetId,
                        replacementModel.AssetId)
                    : project.Animations,
            };
        }

        ImmutableArray<ProjectAnimationVariant> variants = project
            .AnimationVariants
            .Select(variant => variant.TargetModelId == previousModel.Id
                ? InvalidateVariant(
                    variant,
                    replacementModel.RigSignature,
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
                        rigChanged,
                        morphChanged)
                    : animation)
            .ToImmutableArray();

        return project with
        {
            Models = models,
            AnimationVariants = variants,
            Animations = compatibilityAnimations,
        };
    }

    private static ProjectAnimationVariant InvalidateVariant(
        ProjectAnimationVariant variant,
        string? replacementRigSignature,
        bool rigChanged,
        bool morphChanged) =>
        variant with
        {
            TargetRigSignature = rigChanged
                ? replacementRigSignature
                : variant.TargetRigSignature,
            MappingFingerprint = rigChanged
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

    private static ProjectAnimation InvalidateCompatibilityAnimation(
        ProjectAnimation animation,
        Guid replacementAssetId,
        string? replacementRigSignature,
        bool rigChanged,
        bool morphChanged) =>
        animation with
        {
            TargetAssetId = replacementAssetId,
            TargetRigSignature = rigChanged
                ? replacementRigSignature
                : animation.TargetRigSignature,
            MappingFingerprint = rigChanged
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

    private static ImmutableArray<ProjectAnimation>
        RetargetCompatibilityAssets(
            ImmutableArray<ProjectAnimation> animations,
            Guid previousAssetId,
            Guid replacementAssetId) =>
        animations
            .Select(animation =>
                animation.TargetAssetId == previousAssetId
                    ? animation with
                    {
                        TargetAssetId = replacementAssetId,
                    }
                    : animation)
            .ToImmutableArray();

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
