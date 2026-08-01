using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed record ProjectVariantRecoveryRepair(
    Guid RetainedVariantId,
    Guid SafeVariantId,
    string AnimationName,
    string SourceModelName);

public sealed record ProjectVariantRecoveryNormalizationResult(
    DlraProject Project,
    ImmutableArray<ProjectVariantRecoveryRepair> Repairs)
{
    public bool WasRepaired => !Repairs.IsEmpty;
}

/// <summary>
/// Repairs only the previously persisted unsafe state: an active cross-rig
/// target whose proposal was never fully reviewed. The original variant is
/// retained verbatim and a clean direct-source sibling becomes active.
/// </summary>
public static class ProjectVariantRecoveryNormalizer
{
    public static ProjectVariantRecoveryNormalizationResult Normalize(
        DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project = MainWindowViewModel.NormalizeAnimationVariantGroups(
            project);
        Dictionary<Guid, ProjectAssetReference> assets =
            project.Assets.ToDictionary(static asset => asset.Id);
        ImmutableArray<ProjectAnimation>.Builder animations =
            project.Animations.ToBuilder();
        var repairs = ImmutableArray.CreateBuilder<
            ProjectVariantRecoveryRepair>();
        Guid? activeId = project.ActiveAnimationId;

        for (var index = 0; index < animations.Count; index++)
        {
            ProjectAnimation poisoned = animations[index];
            if (poisoned.Id != activeId ||
                poisoned.SourceBinding is not
                {
                    RetailSourceModelAssetId: { } sourceModelId,
                } binding ||
                poisoned.TargetAssetId is not { } targetId ||
                targetId == sourceModelId ||
                string.Equals(
                    poisoned.SourceRigSignature,
                    poisoned.TargetRigSignature,
                    StringComparison.OrdinalIgnoreCase) ||
                !NeedsExplicitReview(poisoned) ||
                !assets.TryGetValue(
                    sourceModelId,
                    out ProjectAssetReference? sourceModel) ||
                sourceModel is null)
            {
                continue;
            }

            Guid groupId = poisoned.VariantGroupId ??
                AnimationVariantKey.CreateGroupId(
                    poisoned,
                    assets);
            ProjectAnimation? existingDirect = animations
                .FirstOrDefault(candidate =>
                    candidate.Id != poisoned.Id &&
                    candidate.VariantGroupId == groupId &&
                    candidate.TargetAssetId == sourceModelId &&
                    string.Equals(
                        candidate.TargetRigSignature,
                        binding.SourceRigSignature,
                        StringComparison.OrdinalIgnoreCase));
            ProjectAnimation safe = existingDirect ??
                poisoned with
                {
                    Id = Guid.NewGuid(),
                    VariantGroupId = groupId,
                    TargetAssetId = sourceModelId,
                    TargetRigId =
                        $"dl1-retail:{sourceModel.ResourceId ?? sourceModel.Id.ToString("N")}",
                    TargetRigSignature = binding.SourceRigSignature,
                    MappingFingerprint = null,
                    BoneMappings = [],
                    TargetBindReviews = [],
                    EditLayers = [],
                    MorphBindings = [],
                    MorphEditLayers = [],
                    IkLayers = [],
                    Attachments = [],
                };
            animations[index] = poisoned with
            {
                VariantGroupId = groupId,
            };
            if (existingDirect is null)
            {
                animations.Add(safe);
            }

            activeId = safe.Id;
            repairs.Add(new ProjectVariantRecoveryRepair(
                poisoned.Id,
                safe.Id,
                poisoned.Name,
                sourceModel.RetailIdentity?.ResourceName ??
                    sourceModel.ResourceId ??
                    "retail source model"));
        }

        DlraProject normalized = repairs.Count == 0
            ? project
            : project with
            {
                Animations = animations.ToImmutable(),
                ActiveAnimationId = activeId,
            };
        normalized.Validate();
        return new ProjectVariantRecoveryNormalizationResult(
            normalized,
            repairs.ToImmutable());
    }

    private static bool NeedsExplicitReview(
        ProjectAnimation animation) =>
        animation.MappingFingerprint is null ||
        animation.BoneMappings.IsEmpty ||
        animation.BoneMappings.Any(static mapping =>
            !mapping.IsReviewed);
}
