using System.Collections.Immutable;
using System.IO;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

internal sealed record CustomModelProjectIdentityRepairResult(
    DlraProject Project,
    bool WasRepaired,
    string RuntimeRigSignature,
    string AnimationSkeletonSignature);

/// <summary>
/// Repairs the short-lived schema-2 state that persisted a .dlrmodel
/// reimport-contract signature where exact runtime rig identity was required.
/// Only records tied to the exact hash-verified package asset are eligible.
/// </summary>
internal static class CustomModelProjectIdentityRepair
{
    public static CustomModelProjectIdentityRepairResult Repair(
        DlraProject project,
        ProjectAssetReference packageAsset,
        FbxModelAuthoringImportResult imported)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(packageAsset);
        ArgumentNullException.ThrowIfNull(imported);
        if (packageAsset.Kind != ProjectAssetKind.CustomModelSource)
        {
            throw new ArgumentException(
                "Identity repair requires a custom-model package asset.",
                nameof(packageAsset));
        }

        RigDefinition rig = imported.Rig ??
            throw new InvalidDataException(
                "A static custom model cannot repair an animation-rig identity.");
        ProjectModelEntry[] packageModels = project.Models
            .Where(model => model.AssetId == packageAsset.Id)
            .ToArray();
        if (packageModels.Length > 1)
        {
            throw new InvalidDataException(
                "The custom-model runtime identity repair found multiple project-model owners for one package asset.");
        }

        ProjectAnimationSource[] embeddedPackageSources = project
            .AnimationSources
            .Where(source =>
                source.SourceAssetId == packageAsset.Id &&
                source.EmbeddedCustomModelStack is not null)
            .ToArray();
        if (packageModels.Length == 0 &&
            embeddedPackageSources.Length == 0)
        {
            throw new InvalidDataException(
                "The custom-model package is neither a current project model nor an immutable embedded animation source.");
        }

        Dictionary<Guid, CustomModelAnimationClip> packageClips = imported
            .Package.Document.AnimationClips
            .ToDictionary(static clip => clip.Id);
        foreach (ProjectAnimationSource source in embeddedPackageSources)
        {
            ProjectEmbeddedAnimationStackIdentity embedded =
                source.EmbeddedCustomModelStack!;
            if (!packageClips.TryGetValue(
                    embedded.ClipId,
                    out CustomModelAnimationClip? clip) ||
                clip.FbxObjectId != embedded.FbxObjectId ||
                !string.Equals(
                    clip.SourceFingerprint,
                    embedded.StackFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Embedded animation source '{source.Name}' no longer matches the package stack identity and cannot be repaired automatically.");
            }
        }

        string runtime = RigSignature.Compute(rig);
        string skeleton = AnimationSkeletonSignature.Compute(rig);
        string authoring = imported.Package.Document.RigSignature;
        // A model entry that carries no rig signature at all is not
        // ambiguous: there is no competing identity to choose between, so the
        // package's own contract is adopted. Only a signature that disagrees
        // with both the authoring contract and the reconstructed runtime rig
        // is genuinely unresolvable.
        if (packageModels.Length == 1 &&
            !string.IsNullOrWhiteSpace(packageModels[0].RigSignature) &&
            !IsKnown(packageModels[0].RigSignature, authoring, runtime))
        {
            throw new InvalidDataException(
                "The project-model rig signature matches neither the package authoring contract nor the reconstructed runtime rig; automatic repair is ambiguous.");
        }

        bool repaired = false;

        ImmutableArray<ProjectModelEntry> models = project.Models
            .Select(model =>
            {
                if (model.AssetId != packageAsset.Id)
                {
                    return model;
                }

                bool missingIdentity =
                    string.IsNullOrWhiteSpace(model.RigSignature);
                bool knownLegacy = string.Equals(
                    model.RigSignature,
                    authoring,
                    StringComparison.OrdinalIgnoreCase);
                bool knownRuntime = string.Equals(
                    model.RigSignature,
                    runtime,
                    StringComparison.OrdinalIgnoreCase);
                if (!missingIdentity && !knownLegacy && !knownRuntime)
                {
                    return model;
                }

                ProjectModelEntry updated = model with
                {
                    RigSignature = runtime,
                    AuthoringRigContractSignature = authoring,
                    AnimationSkeletonSignature = skeleton,
                    // The package reconstructs a rig, so the entry cannot be
                    // a static prop no matter what it recorded.
                    IsStatic = false,
                };
                repaired |= !Equals(updated, model);
                return updated;
            })
            .ToImmutableArray();

        ImmutableArray<ProjectAnimationSource> sources = project
            .AnimationSources
            .Select(source =>
            {
                ProjectAnimationSource updated = RepairSource(
                    source,
                    packageAsset.Id,
                    authoring,
                    runtime,
                    skeleton);
                repaired |= !Equals(updated, source);
                return updated;
            })
            .ToImmutableArray();
        Dictionary<Guid, ProjectAnimationSource> sourceById = sources
            .ToDictionary(static source => source.Id);
        Dictionary<Guid, ProjectModelEntry> modelById = models
            .ToDictionary(static model => model.Id);
        ImmutableArray<ProjectAnimationVariant> variants = project
            .AnimationVariants
            .Select(variant =>
            {
                ProjectAnimationVariant updated = RepairVariant(
                    variant,
                    sourceById,
                    modelById,
                    authoring,
                    runtime,
                    skeleton,
                    packageAsset.Id);
                repaired |= !Equals(updated, variant);
                return updated;
            })
            .ToImmutableArray();
        ImmutableArray<ProjectAnimation> compatibility = project.Animations
            .Select(animation =>
            {
                ProjectAnimation updated = RepairCompatibilityAnimation(
                    animation,
                    packageAsset.Id,
                    authoring,
                    runtime,
                    skeleton);
                repaired |= !Equals(updated, animation);
                return updated;
            })
            .ToImmutableArray();

        DlraProject result = project with
        {
            Models = models,
            AnimationSources = sources,
            AnimationVariants = variants,
            Animations = compatibility,
        };
        result.Validate();
        return new CustomModelProjectIdentityRepairResult(
            result,
            repaired,
            runtime,
            skeleton);
    }

    private static ProjectAnimationSource RepairSource(
        ProjectAnimationSource source,
        Guid packageAssetId,
        string authoring,
        string runtime,
        string skeleton)
    {
        ProjectAnimationSource updated = source;
        if (source.SourceAssetId == packageAssetId &&
            source.EmbeddedCustomModelStack is { } embedded &&
            IsKnown(embedded.SourceRigSignature, authoring, runtime))
        {
            updated = source with
            {
                EmbeddedCustomModelStack = embedded with
                {
                    SourceRigSignature = runtime,
                    SourceAnimationSkeletonSignature = skeleton,
                },
                SourceAnimationSkeletonSignature = skeleton,
            };
        }
        else if (source.SourceBinding is { } binding &&
                 binding.RetailSourceModelAssetId == packageAssetId &&
                 IsKnown(binding.SourceRigSignature, authoring, runtime))
        {
            updated = source with
            {
                SourceBinding = binding with
                {
                    SourceRigSignature = runtime,
                },
                SourceAnimationSkeletonSignature = skeleton,
            };
        }

        return updated;
    }

    private static ProjectAnimationVariant RepairVariant(
        ProjectAnimationVariant variant,
        Dictionary<Guid, ProjectAnimationSource> sources,
        Dictionary<Guid, ProjectModelEntry> models,
        string authoring,
        string runtime,
        string skeleton,
        Guid packageAssetId)
    {
        if (!models.TryGetValue(
                variant.TargetModelId,
                out ProjectModelEntry? model) ||
            model.AssetId != packageAssetId ||
            !IsKnown(variant.TargetRigSignature, authoring, runtime))
        {
            return variant;
        }

        ProjectAnimationSource? source = sources.GetValueOrDefault(
            variant.SourceId);
        bool exact = source is not null && string.Equals(
            source.SourceRigSignature,
            runtime,
            StringComparison.OrdinalIgnoreCase);
        ProjectAnimationVariant updated = variant with
        {
            TargetRigSignature = runtime,
            TargetAnimationSkeletonSignature = skeleton,
            BindingMode = exact
                ? ProjectAnimationBindingMode.ExactDirect
                : variant.BindingMode,
            DirectBinding = exact ? null : variant.DirectBinding,
            BindingEvidenceFingerprint = exact
                ? null
                : variant.BindingEvidenceFingerprint,
            BindingPolicyVersion = exact
                ? null
                : variant.BindingPolicyVersion,
        };
        return updated;
    }

    private static ProjectAnimation RepairCompatibilityAnimation(
        ProjectAnimation animation,
        Guid packageAssetId,
        string authoring,
        string runtime,
        string skeleton)
    {
        ProjectAnimation updated = animation;
        ProjectAnimationSourceBinding? sourceBinding = animation.SourceBinding;
        if (sourceBinding is not null &&
            (animation.SourceAssetId == packageAssetId ||
             sourceBinding.RetailSourceModelAssetId == packageAssetId) &&
            IsKnown(sourceBinding.SourceRigSignature, authoring, runtime))
        {
            updated = updated with
            {
                SourceBinding = sourceBinding with
                {
                    SourceRigSignature = runtime,
                },
                SourceRigSignature = runtime,
                SourceAnimationSkeletonSignature = skeleton,
            };
        }

        if (animation.TargetAssetId == packageAssetId &&
            IsKnown(animation.TargetRigSignature, authoring, runtime))
        {
            bool exact = string.Equals(
                updated.SourceRigSignature,
                runtime,
                StringComparison.OrdinalIgnoreCase);
            updated = updated with
            {
                TargetRigSignature = runtime,
                TargetAnimationSkeletonSignature = skeleton,
                BindingMode = exact
                    ? ProjectAnimationBindingMode.ExactDirect
                    : updated.BindingMode,
                DirectBinding = exact ? null : updated.DirectBinding,
                BindingEvidenceFingerprint = exact
                    ? null
                    : updated.BindingEvidenceFingerprint,
                BindingPolicyVersion = exact
                    ? null
                    : updated.BindingPolicyVersion,
            };
        }

        return updated;
    }

    private static bool IsKnown(
        string? value,
        string authoring,
        string runtime) =>
        string.Equals(
            value,
            authoring,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            value,
            runtime,
            StringComparison.OrdinalIgnoreCase);
}
