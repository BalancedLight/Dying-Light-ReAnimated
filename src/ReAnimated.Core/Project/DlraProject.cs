using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Project;

public enum ProjectAssetKind
{
    SourceAnimation,
    RetailGameResource,
    CustomModelSource,
}

public enum Dl1RootMotionMode
{
    Recorded,
    InPlace,
    Bip01,
    MotionAccumulator,
}

/// <summary>
/// Selects which persisted preview pipeline is active without discarding the
/// stored DL1 profile or its optional game-validation fingerprint.
/// </summary>
public enum ProjectPreviewMode
{
    Dl1Profile,
    Raw,
}

/// <summary>
/// Selects which representation of a project-owned custom-model package is
/// shown in the independent Models workspace.
/// </summary>
public enum ProjectCustomModelPreviewMode
{
    Dl1Output,
    SourceFbx,
}

/// <summary>
/// Stable schema-2/3 workflow destinations. Face authoring is part of
/// Retarget/Edit and first-person preview is a Playback presentation mode, so
/// neither is persisted as an independent top-level workspace.
/// </summary>
public enum ProjectWorkflowTab
{
    Models,
    Animations,
    Playback,
    RetargetEdit,
    Export,
}

public enum ProjectMappingReviewOrigin
{
    None,
    Explicit,
    Assisted,
}

public enum ProjectAnimationBindingMode
{
    ExactDirect,
    CompatibleDirect,
    Retarget,
}

public enum ProjectAnimationLibraryMode
{
    CustomAdditive,
    ExistingScriptExtension,
}

public enum ProjectAnimationSequenceCollisionPolicy
{
    Reject,
    ReplaceExisting,
}

public enum ProjectAnimationLibraryImportKind
{
    ProjectLibrary,
    RetailScript,
}

public enum ProjectAnimationSourceOriginKind
{
    ImportedFbxRig,
    OwningCustomModel,
    BoundRetailModel,
    BoundProjectModel,
    UnresolvedLegacy,
}

[Flags]
public enum RetargetTransformComponents
{
    None = 0,
    Translation = 1 << 0,
    Rotation = 1 << 1,
    Scale = 1 << 2,
    All = Translation | Rotation | Scale,
}

/// <summary>
/// Exact compatibility seam for schema-1/2 component policies. New schema-3
/// rows persist flags so every TRS combination is representable; legacy rows
/// remain readable without changing their meaning.
/// </summary>
public static class RetargetTransformComponentsCompatibility
{
    public static RetargetTransformComponents FromLegacy(
        RetargetComponentPolicy policy) =>
        policy switch
        {
            RetargetComponentPolicy.FullTransform =>
                RetargetTransformComponents.All,
            RetargetComponentPolicy.Rotation =>
                RetargetTransformComponents.Rotation,
            RetargetComponentPolicy.Translation =>
                RetargetTransformComponents.Translation,
            RetargetComponentPolicy.RotationTranslation =>
                RetargetTransformComponents.Rotation |
                RetargetTransformComponents.Translation,
            RetargetComponentPolicy.Scale =>
                RetargetTransformComponents.Scale,
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

    public static bool TryToLegacy(
        RetargetTransformComponents components,
        out RetargetComponentPolicy policy)
    {
        switch (components)
        {
            case RetargetTransformComponents.All:
                policy = RetargetComponentPolicy.FullTransform;
                return true;
            case RetargetTransformComponents.Rotation:
                policy = RetargetComponentPolicy.Rotation;
                return true;
            case RetargetTransformComponents.Translation:
                policy = RetargetComponentPolicy.Translation;
                return true;
            case RetargetTransformComponents.Rotation |
                 RetargetTransformComponents.Translation:
                policy = RetargetComponentPolicy.RotationTranslation;
                return true;
            case RetargetTransformComponents.Scale:
                policy = RetargetComponentPolicy.Scale;
                return true;
            default:
                policy = default;
                return false;
        }
    }
}

public sealed record ProjectAnimationLibraryImport
{
    public ProjectAnimationLibraryImportKind Kind { get; init; }

    public Guid? ProjectLibraryId { get; init; }

    public ProjectRetailAssetIdentity? RetailScriptIdentity { get; init; }

    internal void Validate(string parameterName)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "An animation-library import has an unsupported kind.");
        }

        bool projectImport = ProjectLibraryId is { } projectLibraryId &&
            projectLibraryId != Guid.Empty &&
            RetailScriptIdentity is null;
        bool retailImport = ProjectLibraryId is null &&
            RetailScriptIdentity is not null;
        if (Kind == ProjectAnimationLibraryImportKind.ProjectLibrary
                ? !projectImport
                : !retailImport)
        {
            throw new ArgumentException(
                "An animation-library import must identify exactly one project library or fingerprinted retail script.",
                parameterName);
        }

        if (RetailScriptIdentity is { } retail)
        {
            retail.Validate(parameterName);
            if (retail.ResourceType != 322)
            {
                throw new ArgumentException(
                    "A retail animation-library import must identify a type-322 script resource.",
                    parameterName);
            }
        }
    }
}

public sealed record ProjectAnimationLibrary
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Extensionless type-322 resource identity.
    /// </summary>
    public string ResourceName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public ProjectAnimationLibraryMode Mode { get; init; } =
        ProjectAnimationLibraryMode.CustomAdditive;

    /// <summary>
    /// Required only when extending an existing retail script. The full
    /// fingerprint prevents a same-name minimal script from shadowing stock
    /// sequences whose source was not preserved.
    /// </summary>
    public ProjectRetailAssetIdentity? ExistingScriptIdentity { get; init; }

    public ImmutableArray<ProjectAnimationLibraryImport> Imports
    { get; init; } = [];

    public ProjectAnimationSequenceCollisionPolicy CollisionPolicy
    { get; init; } = ProjectAnimationSequenceCollisionPolicy.Reject;

    internal void Validate(string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException(
                "Animation-library identifiers cannot be empty.",
                parameterName);
        }

        ValidateAnimationResourceName(ResourceName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName, parameterName);
        if (DisplayName.Length > 256 ||
            !Enum.IsDefined(Mode) ||
            !Enum.IsDefined(CollisionPolicy) ||
            Imports.IsDefault)
        {
            throw new ArgumentException(
                "Animation-library display, mode, imports, or collision policy is invalid.",
                parameterName);
        }

        if (Mode == ProjectAnimationLibraryMode.ExistingScriptExtension)
        {
            if (ExistingScriptIdentity is null)
            {
                throw new ArgumentException(
                    "An existing-script extension requires its fingerprinted retail script identity.",
                    parameterName);
            }

            ExistingScriptIdentity.Validate(parameterName);
            if (ExistingScriptIdentity.ResourceType != 322 ||
                !string.Equals(
                    ExistingScriptIdentity.ResourceName,
                    ResourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "An existing-script extension must preserve the matching type-322 retail resource.",
                    parameterName);
            }
        }
        else if (ExistingScriptIdentity is not null)
        {
            throw new ArgumentException(
                "A custom additive animation library cannot carry an existing-script identity.",
                parameterName);
        }

        if (Mode == ProjectAnimationLibraryMode.CustomAdditive &&
            ResourceName.Contains("_dlc", StringComparison.OrdinalIgnoreCase) &&
            !TryGetDlcNumber(ResourceName, out _))
        {
            throw new ArgumentException(
                "A DLC animation-script resource must end with '_dlc' followed by decimal digits, for example anims_man_all_dlc60.",
                parameterName);
        }

        foreach (ProjectAnimationLibraryImport import in Imports)
        {
            import.Validate(parameterName);
        }
    }

    internal static void ValidateAnimationResourceName(
        string name,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (name.Length > 128 ||
            name is "." or ".." ||
            Path.IsPathRooted(name) ||
            Path.HasExtension(name) ||
            name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ArgumentException(
                "Animation resource names must be extensionless single path components of at most 128 characters.",
                parameterName);
        }
    }

    public static bool TryGetDlcNumber(
        string resourceName,
        out int dlcNumber)
    {
        dlcNumber = 0;
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return false;
        }

        int marker = resourceName.LastIndexOf(
            "_dlc",
            StringComparison.OrdinalIgnoreCase);
        if (marker <= 0 || marker + 4 >= resourceName.Length)
        {
            return false;
        }

        ReadOnlySpan<char> suffix = resourceName.AsSpan(marker + 4);
        foreach (char character in suffix)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(
            suffix,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out dlcNumber);
    }
}

public sealed record ProjectAnimationSourcePresentation
{
    public ProjectAnimationSourceOriginKind OriginKind { get; init; }

    public string OriginName { get; init; } = string.Empty;

    public Guid? OwningModelId { get; init; }

    public Guid ProjectAssetId { get; init; }

    public string? SourceRigIdentity { get; init; }

    internal void Validate(
        ProjectAnimationSource source,
        IReadOnlyDictionary<Guid, ProjectModelEntry> models,
        string parameterName)
    {
        if (!Enum.IsDefined(OriginKind) ||
            ProjectAssetId == Guid.Empty ||
            ProjectAssetId != source.SourceAssetId ||
            string.IsNullOrWhiteSpace(OriginName) ||
            OriginName.Length > 512 ||
            (SourceRigIdentity is { } rigIdentity &&
                (string.IsNullOrWhiteSpace(rigIdentity) ||
                 rigIdentity.Length > 512)))
        {
            throw new ArgumentException(
                "Animation-source presentation metadata is invalid or disagrees with its immutable source.",
                parameterName);
        }

        if (OwningModelId is { } modelId)
        {
            if (OriginKind != ProjectAnimationSourceOriginKind.OwningCustomModel ||
                !models.TryGetValue(modelId, out ProjectModelEntry? model) ||
                model.AssetId != ProjectAssetId)
            {
                throw new ArgumentException(
                    "An animation source may name an owning model only when that model owns the source package.",
                    parameterName);
            }
        }
        else if (OriginKind == ProjectAnimationSourceOriginKind.OwningCustomModel)
        {
            throw new ArgumentException(
                "An owning custom-model source presentation requires its project model identifier.",
                parameterName);
        }
    }
}

public sealed record ProjectExportSelection
{
    public ImmutableArray<Guid> ModelIds { get; init; } = [];

    public ImmutableArray<Guid> AnimationVariantIds { get; init; } = [];

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectModelEntry> models,
        IReadOnlyDictionary<Guid, ProjectAnimationVariant> variants)
    {
        if (ModelIds.IsDefault || AnimationVariantIds.IsDefault ||
            ModelIds.Any(static id => id == Guid.Empty) ||
            AnimationVariantIds.Any(static id => id == Guid.Empty) ||
            ModelIds.Distinct().Count() != ModelIds.Length ||
            AnimationVariantIds.Distinct().Count() !=
                AnimationVariantIds.Length ||
            ModelIds.Any(id => !models.ContainsKey(id)) ||
            AnimationVariantIds.Any(id => !variants.ContainsKey(id)))
        {
            throw new ProjectFormatException(
                "The export selection contains an invalid, duplicate, or unknown model/variant identifier.");
        }
    }
}

public sealed record ProjectModelEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid AssetId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? RigSignature { get; init; }

    /// <summary>
    /// Mesh-independent custom-model hierarchy/bind contract used only for
    /// reimport staleness. Retail models may leave this null.
    /// </summary>
    public string? AuthoringRigContractSignature { get; init; }

    /// <summary>
    /// Asset-independent animation skeleton identity. This never replaces the
    /// stronger runtime rig signature above.
    /// </summary>
    public string? AnimationSkeletonSignature { get; init; }

    /// <summary>
    /// DL1-output rig identity. This is deliberately separate from the source
    /// FBX runtime and authoring-contract signatures.
    /// </summary>
    public string? Dl1OutputRigSignature { get; init; }

    public string? Dl1DescriptorInventoryFingerprint { get; init; }

    /// <summary>
    /// Project library used by the model's ASCR link. Schema-1/2 project files
    /// did not expose the alias stored inside a .dlrmodel package, so migration
    /// deliberately leaves this null instead of opening assets or guessing.
    /// </summary>
    public Guid? RootAnimationLibraryId { get; init; }

    public string? MorphSignature { get; init; }

    public bool IsStatic { get; init; }

    /// <summary>
    /// Number of exportable, unweighted camera helpers named exactly
    /// EyeCamera in the model contract. Preview camera selection is editor
    /// state and deliberately does not affect this export-readiness fact.
    /// </summary>
    public int ExportableEyeCameraHelperCount { get; init; }

    /// <summary>
    /// An editor camera may use any rig node. Game-ready FPP export still
    /// requires a separately authored helper named exactly EyeCamera.
    /// </summary>
    public string? PreviewCameraNodeName { get; init; }

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds,
        string parameterName)
    {
        if (Id == Guid.Empty || AssetId == Guid.Empty)
        {
            throw new ArgumentException(
                "Project model and model-asset identifiers cannot be empty.",
                parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        if (!assetKinds.TryGetValue(AssetId, out ProjectAssetKind kind) ||
            kind is not (
                ProjectAssetKind.RetailGameResource or
                ProjectAssetKind.CustomModelSource))
        {
            throw new ArgumentException(
                $"Project model '{Name}' must reference a retail or custom-model asset.",
                parameterName);
        }

        if (!IsStatic && string.IsNullOrWhiteSpace(RigSignature))
        {
            throw new ArgumentException(
                $"Rigged project model '{Name}' requires a persisted rig signature.",
                parameterName);
        }

        ValidateOptionalSha256(RigSignature, "rig", parameterName);
        ValidateOptionalSha256(
            AuthoringRigContractSignature,
            "authoring rig contract",
            parameterName);
        ValidateOptionalSha256(
            AnimationSkeletonSignature,
            "animation skeleton",
            parameterName);
        ValidateOptionalSha256(
            Dl1OutputRigSignature,
            "DL1-output rig",
            parameterName);
        ValidateOptionalSha256(
            Dl1DescriptorInventoryFingerprint,
            "DL1 descriptor inventory",
            parameterName);
        if (RootAnimationLibraryId == Guid.Empty)
        {
            throw new ArgumentException(
                "A root animation-library identifier cannot be empty.",
                parameterName);
        }
        ValidateOptionalSha256(MorphSignature, "morph", parameterName);
        ArgumentOutOfRangeException.ThrowIfNegative(
            ExportableEyeCameraHelperCount,
            parameterName);
        if (PreviewCameraNodeName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                PreviewCameraNodeName,
                parameterName);
        }
    }

    private static void ValidateOptionalSha256(
        string? value,
        string description,
        string parameterName)
    {
        if (value is not null)
        {
            try
            {
                ProjectAssetReference.ValidateSha256(value, parameterName);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"A project model {description} signature must be a SHA-256 value.",
                    parameterName,
                    exception);
            }
        }
    }
}

/// <summary>
/// Stable identity of one animation stack embedded in a project-owned custom
/// model package. The model package is the source asset; no duplicate
/// SourceAnimation asset is created for the stack.
/// </summary>
public sealed record ProjectEmbeddedAnimationStackIdentity
{
    public Guid ClipId { get; init; }

    public long FbxObjectId { get; init; }

    public string StackFingerprint { get; init; } = string.Empty;

    public string SourceRigSignature { get; init; } = string.Empty;

    public string? SourceAnimationSkeletonSignature { get; init; }

    public AnimationSourceRoles Roles { get; init; } =
        AnimationSourceRoles.Body;

    public ProjectMorphSourceValueUnit FacialSourceValueUnit { get; init; } =
        ProjectMorphSourceValueUnit.Percent;

    internal void Validate(string parameterName)
    {
        if (ClipId == Guid.Empty)
        {
            throw new ArgumentException(
                "An embedded animation stack requires a stable clip identifier.",
                parameterName);
        }

        ProjectAssetReference.ValidateSha256(
            StackFingerprint,
            parameterName);
        ProjectAssetReference.ValidateSha256(
            SourceRigSignature,
            parameterName);
        if (SourceAnimationSkeletonSignature is not null)
        {
            ProjectAssetReference.ValidateSha256(
                SourceAnimationSkeletonSignature,
                parameterName);
        }
        const AnimationSourceRoles knownRoles =
            AnimationSourceRoles.Body |
            AnimationSourceRoles.Facial |
            AnimationSourceRoles.Auxiliary;
        if (Roles == AnimationSourceRoles.None ||
            (Roles & ~knownRoles) != 0 ||
            !Enum.IsDefined(FacialSourceValueUnit))
        {
            throw new ArgumentException(
                "An embedded animation stack contains unsupported roles or facial units.",
                parameterName);
        }
    }
}

public sealed record ProjectAnimationSource
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public Guid SourceAssetId { get; init; }

    public ProjectAnimationSourceBinding? SourceBinding { get; init; }

    public ProjectEmbeddedAnimationStackIdentity? EmbeddedCustomModelStack
    { get; init; }

    /// <summary>
    /// Schema-1 local ANM2 documents could omit their exact source-model
    /// binding. They remain visible after migration but cannot become
    /// authoritative until Rebind Source replaces this fail-closed marker.
    /// </summary>
    public bool RequiresSourceRebind { get; init; }

    public string? LegacySourceRigSignature { get; init; }

    public string? SourceAnimationSkeletonSignature { get; init; }

    public ProjectAnimationSourcePresentation? Presentation { get; init; }

    public Guid? MimicAssetId { get; init; }

    public ProjectAnimationSourceBinding? FacialAnimationSourceBinding
    { get; init; }

    public Guid? FacialSourceAssetId { get; init; }

    public ProjectMorphSourceValueUnit? FacialSourceValueUnit { get; init; }

    public FacialClipTiming? FacialTiming { get; init; }

    public FrameRate FrameRate { get; init; } = new(30, 1);

    public long FrameCount { get; init; } = 1;

    /// <summary>
    /// Retains a schema-1 grouping marker for migration diagnostics. Multiple
    /// migrated sources may carry the same value when a legacy group contained
    /// conflicting immutable source identities.
    /// </summary>
    public Guid? LegacyVariantGroupId { get; init; }

    public string? MigrationNote { get; init; }

    public string SourceRigSignature =>
        SourceBinding?.SourceRigSignature ??
        EmbeddedCustomModelStack?.SourceRigSignature ??
        LegacySourceRigSignature ??
        string.Empty;

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds,
        string parameterName)
    {
        if (Id == Guid.Empty || SourceAssetId == Guid.Empty)
        {
            throw new ArgumentException(
                "Animation-source identifiers cannot be empty.",
                parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        if (FrameRate.Numerator <= 0 ||
            FrameRate.Denominator <= 0 ||
            FrameCount <= 0)
        {
            throw new ArgumentException(
                $"Animation source '{Name}' has invalid timing.",
                parameterName);
        }

        bool ordinary = SourceBinding is not null;
        bool embedded = EmbeddedCustomModelStack is not null;
        if ((!RequiresSourceRebind && ordinary == embedded) ||
            (RequiresSourceRebind && (ordinary || embedded)))
        {
            throw new ArgumentException(
                $"Animation source '{Name}' must declare exactly one ordinary, embedded-stack, or unresolved legacy identity.",
                parameterName);
        }

        if (ordinary)
        {
            SourceBinding!.Validate(assetKinds, parameterName);
            if (SourceBinding.AssetId != SourceAssetId)
            {
                throw new ArgumentException(
                    $"Animation source '{Name}' binding disagrees with its source asset.",
                    parameterName);
            }
        }
        else if (embedded)
        {
            if (!assetKinds.TryGetValue(
                    SourceAssetId,
                    out ProjectAssetKind sourceKind) ||
                sourceKind != ProjectAssetKind.CustomModelSource)
            {
                throw new ArgumentException(
                    $"Embedded animation source '{Name}' must reference its custom-model package directly.",
                    parameterName);
            }

            EmbeddedCustomModelStack!.Validate(parameterName);
        }
        else if (!assetKinds.TryGetValue(
                     SourceAssetId,
                     out ProjectAssetKind unresolvedKind) ||
                 unresolvedKind != ProjectAssetKind.SourceAnimation)
        {
            throw new ArgumentException(
                $"Unresolved animation source '{Name}' must reference a local source-animation asset.",
                parameterName);
        }

        if (LegacySourceRigSignature is { } legacySignature &&
            legacySignature.Length > 512)
        {
            throw new ArgumentException(
                "A legacy source-rig signature cannot exceed 512 characters.",
                parameterName);
        }

        if (SourceAnimationSkeletonSignature is { } skeletonSignature)
        {
            ProjectAssetReference.ValidateSha256(
                skeletonSignature,
                parameterName);
        }

        if (LegacyVariantGroupId == Guid.Empty)
        {
            throw new ArgumentException(
                "A legacy variant-group identifier cannot be empty.",
                parameterName);
        }

        if (MigrationNote is { Length: > 1_024 })
        {
            throw new ArgumentException(
                "An animation-source migration note cannot exceed 1024 characters.",
                parameterName);
        }

        ValidateFacialSource(assetKinds, parameterName);
    }

    private void ValidateFacialSource(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds,
        string parameterName)
    {
        ProjectAssetKind? mimicKind = null;
        if (MimicAssetId is { } mimicAssetId)
        {
            if (!assetKinds.TryGetValue(
                    mimicAssetId,
                    out ProjectAssetKind resolvedMimicKind))
            {
                throw new ArgumentException(
                    $"Animation source '{Name}' refers to an unknown mimic asset.",
                    parameterName);
            }

            mimicKind = resolvedMimicKind;
        }

        if (FacialAnimationSourceBinding is { } facialBinding)
        {
            facialBinding.Validate(assetKinds, parameterName);
            if (MimicAssetId != facialBinding.AssetId ||
                (facialBinding.Roles & AnimationSourceRoles.Facial) == 0)
            {
                throw new ArgumentException(
                    $"Animation source '{Name}' facial binding disagrees with its mimic asset or has no facial role.",
                    parameterName);
            }
        }
        else if (MimicAssetId is not null &&
                 mimicKind != ProjectAssetKind.SourceAnimation)
        {
            throw new ArgumentException(
                $"Animation source '{Name}' has a retail facial source without an immutable facial binding.",
                parameterName);
        }

        if (FacialSourceAssetId is { } facialSourceAssetId)
        {
            if (!assetKinds.TryGetValue(
                    facialSourceAssetId,
                    out ProjectAssetKind facialKind) ||
                facialKind != ProjectAssetKind.SourceAnimation)
            {
                throw new ArgumentException(
                    $"Animation source '{Name}' refers to an unknown facial FBX source asset.",
                    parameterName);
            }

            if (MimicAssetId is not null ||
                FacialAnimationSourceBinding is not null)
            {
                throw new ArgumentException(
                    $"Animation source '{Name}' cannot use both a facial FBX and mimic ANM2.",
                    parameterName);
            }

            if (FacialSourceValueUnit is not { } valueUnit ||
                !Enum.IsDefined(valueUnit))
            {
                throw new ArgumentException(
                    $"Animation source '{Name}' requires an explicit facial FBX source-value unit.",
                    parameterName);
            }
        }
        else if (FacialSourceValueUnit is { } primaryValueUnit &&
                 (!Enum.IsDefined(primaryValueUnit) ||
                  !IsPrimaryFbxFacialSource()))
        {
            throw new ArgumentException(
                $"Animation source '{Name}' declares facial units without a facial FBX primary or attached source.",
                parameterName);
        }

        FacialTiming?.Validate(FrameCount);
    }

    private bool IsPrimaryFbxFacialSource() =>
        SourceBinding is
        {
            Kind: AnimationSourceKind.LocalFbx,
        } binding &&
        (binding.Roles & AnimationSourceRoles.Facial) != 0 ||
        EmbeddedCustomModelStack is { } embedded &&
        (embedded.Roles & AnimationSourceRoles.Facial) != 0;
}

public sealed record ProjectAnimationVariant
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid SourceId { get; init; }

    public string Name { get; init; } = string.Empty;

    public Guid TargetModelId { get; init; }

    public string TargetRigId { get; init; } = string.Empty;

    public string? TargetRigSignature { get; init; }

    public string? TargetAnimationSkeletonSignature { get; init; }

    public ProjectAnimationBindingMode BindingMode { get; init; } =
        ProjectAnimationBindingMode.ExactDirect;

    public DirectRigBinding? DirectBinding { get; init; }

    public string? BindingEvidenceFingerprint { get; init; }

    public string? BindingPolicyVersion { get; init; }

    public Guid? OwningAnimationLibraryId { get; init; }

    /// <summary>
    /// Stable target-specific output filename, including the .anm2 suffix.
    /// Null means the variant still needs an explicit output assignment.
    /// </summary>
    public string? OutputAnm2Name { get; init; }

    public string? MappingFingerprint { get; init; }

    public string? MimicProfileId { get; init; }

    public string? MimicMappingFingerprint { get; init; }

    public Dl1RootMotionMode RootMotionMode { get; init; } =
        Dl1RootMotionMode.Recorded;

    public string? RootBoneName { get; init; }

    public bool PreviewMotionAccumulationEnabled { get; init; }

    public ImmutableArray<ProjectBoneMapping> BoneMappings { get; init; } = [];

    public ImmutableArray<ProjectTargetBindReview> TargetBindReviews { get; init; } = [];

    public ImmutableArray<BoneEditLayer> EditLayers { get; init; } = [];

    public ImmutableArray<ProjectMorphBinding> MorphBindings { get; init; } = [];

    public ImmutableArray<MorphEditLayer> MorphEditLayers { get; init; } = [];

    public ImmutableArray<ProjectIkLayer> IkLayers { get; init; } = [];

    public ImmutableArray<AttachmentBinding> Attachments { get; init; } = [];

    public bool IncludeInPackage { get; init; } = true;

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds,
        IReadOnlyDictionary<Guid, ProjectModelEntry> models,
        IReadOnlyDictionary<Guid, ProjectAnimationSource> sources,
        string parameterName)
    {
        if (Id == Guid.Empty || SourceId == Guid.Empty ||
            TargetModelId == Guid.Empty)
        {
            throw new ArgumentException(
                "Animation-variant, source, and target-model identifiers cannot be empty.",
                parameterName);
        }

        if (!sources.TryGetValue(
                SourceId,
                out ProjectAnimationSource? source))
        {
            throw new ArgumentException(
                $"Animation variant '{Name}' refers to an unknown immutable source.",
                parameterName);
        }

        if (!models.TryGetValue(
                TargetModelId,
                out ProjectModelEntry? model))
        {
            throw new ArgumentException(
                $"Animation variant '{Name}' refers to an unknown project model.",
                parameterName);
        }

        if (model.IsStatic || string.IsNullOrWhiteSpace(model.RigSignature))
        {
            throw new ArgumentException(
                $"Animation variant '{Name}' cannot target model '{model.Name}' without a valid rig contract.",
                parameterName);
        }

        if (!Enum.IsDefined(BindingMode))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Animation variant '{Name}' has an unsupported binding mode.");
        }
        ValidateOptionalSha256(
            TargetAnimationSkeletonSignature,
            "target animation skeleton signature",
            parameterName);
        ValidateOptionalSha256(
            BindingEvidenceFingerprint,
            "binding evidence fingerprint",
            parameterName);
        if (OwningAnimationLibraryId == Guid.Empty)
        {
            throw new ArgumentException(
                $"Animation variant '{Name}' has an empty owning-library identifier.",
                parameterName);
        }
        if (OutputAnm2Name is { } outputName &&
            (string.IsNullOrWhiteSpace(
                 Path.GetFileNameWithoutExtension(outputName)) ||
             Path.IsPathRooted(outputName) ||
             outputName.IndexOfAny(['/', '\\', ':']) >= 0 ||
             !string.Equals(
                Path.GetExtension(outputName),
                ".anm2",
                StringComparison.OrdinalIgnoreCase) ||
             Path.GetFileName(outputName) != outputName ||
             outputName.Length > 132 ||
             outputName is ".anm2"))
        {
            throw new ArgumentException(
                $"Animation variant '{Name}' has an invalid target-specific ANM2 filename.",
                parameterName);
        }
        if (BindingMode == ProjectAnimationBindingMode.CompatibleDirect)
        {
            if (DirectBinding is null ||
                !string.Equals(
                    BindingEvidenceFingerprint,
                    DirectBinding.EvidenceFingerprint,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    BindingPolicyVersion,
                    DirectBinding.Policy,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    source.SourceAnimationSkeletonSignature,
                    DirectBinding.SourceSkeletonSignature,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    TargetAnimationSkeletonSignature,
                    DirectBinding.TargetSkeletonSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Animation variant '{Name}' has incomplete or stale compatible-direct evidence.",
                    parameterName);
            }
        }
        else if (DirectBinding is not null)
        {
            throw new ArgumentException(
                $"Animation variant '{Name}' stores direct-binding rows for a non-compatible binding mode.",
                parameterName);
        }

        Guid targetAssetId = model.AssetId;

        Dictionary<Guid, ProjectAssetKind> validationKinds =
            new(assetKinds);
        ProjectAnimationSourceBinding? sourceBinding;
        if (source.SourceBinding is { } ordinaryBinding)
        {
            sourceBinding = ordinaryBinding;
        }
        else if (source.EmbeddedCustomModelStack is { } embedded)
        {
            // Reuse the mature legacy variant validator while treating the
            // package-backed stack as an FBX animation source in this local
            // validation view. The persisted asset remains CustomModelSource.
            Guid embeddedValidationAssetId = embedded.ClipId;
            if (validationKinds.ContainsKey(embeddedValidationAssetId))
            {
                embeddedValidationAssetId = new Guid(
                    embeddedValidationAssetId.ToByteArray()
                        .Select(static value => (byte)(value ^ 0x5a))
                        .ToArray());
            }

            validationKinds[embeddedValidationAssetId] =
                ProjectAssetKind.SourceAnimation;
            sourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalFbx,
                AssetId = embeddedValidationAssetId,
                Roles = embedded.Roles,
                SourceRigSignature = embedded.SourceRigSignature,
                TimingProvenance =
                    AnimationTimingProvenance.EmbeddedFbx,
                SourceRangeStartFrame = 0,
                SourceRangeEndFrame = source.FrameCount - 1,
                TimingDetail =
                    $"Embedded custom-model stack {embedded.FbxObjectId}",
            };
        }
        else
        {
            sourceBinding = null;
        }

        var compatibility = new ProjectAnimation
        {
            Id = Id,
            VariantGroupId = SourceId,
            Name = Name,
            SourceAssetId = sourceBinding?.AssetId ??
                source.SourceAssetId,
            SourceBinding = sourceBinding,
            MimicAssetId = source.MimicAssetId,
            FacialAnimationSourceBinding =
                source.FacialAnimationSourceBinding,
            FacialSourceAssetId = source.FacialSourceAssetId,
            FacialSourceValueUnit = source.FacialSourceValueUnit,
            FacialTiming = source.FacialTiming,
            TargetAssetId = targetAssetId,
            TargetRigId = TargetRigId,
            SourceRigSignature = source.SourceRigSignature,
            TargetRigSignature = TargetRigSignature,
            SourceAnimationSkeletonSignature =
                source.SourceAnimationSkeletonSignature,
            TargetAnimationSkeletonSignature =
                TargetAnimationSkeletonSignature,
            BindingMode = BindingMode,
            DirectBinding = DirectBinding,
            BindingEvidenceFingerprint = BindingEvidenceFingerprint,
            BindingPolicyVersion = BindingPolicyVersion,
            MappingFingerprint = MappingFingerprint,
            MimicProfileId = MimicProfileId,
            MimicMappingFingerprint = MimicMappingFingerprint,
            FrameRate = source.FrameRate,
            FrameCount = source.FrameCount,
            RootMotionMode = RootMotionMode,
            RootBoneName = RootBoneName,
            PreviewMotionAccumulationEnabled =
                PreviewMotionAccumulationEnabled,
            BoneMappings = BoneMappings,
            TargetBindReviews = TargetBindReviews,
            EditLayers = EditLayers,
            MorphBindings = MorphBindings,
            MorphEditLayers = MorphEditLayers,
            IkLayers = IkLayers,
            Attachments = Attachments,
        };
        compatibility.Validate(validationKinds, parameterName);
    }

    private static void ValidateOptionalSha256(
        string? value,
        string description,
        string parameterName)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            ProjectAssetReference.ValidateSha256(value, parameterName);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                $"Animation variant {description} must be a SHA-256 value.",
                parameterName,
                exception);
        }
    }
}

public sealed record ProjectWorkflowState
{
    public ProjectWorkflowTab ActiveTab { get; init; } =
        ProjectWorkflowTab.Models;

    public Guid? SelectedModelId { get; init; }

    public Guid? SelectedAnimationSourceId { get; init; }

    public Guid? SelectedAnimationVariantId { get; init; }

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectModelEntry> models,
        IReadOnlyDictionary<Guid, ProjectAnimationSource> sources,
        IReadOnlyDictionary<Guid, ProjectAnimationVariant> variants)
    {
        if (!Enum.IsDefined(ActiveTab))
        {
            throw new ProjectFormatException(
                $"Unsupported project workflow tab '{ActiveTab}'.");
        }

        if (SelectedModelId is { } modelId &&
            !models.ContainsKey(modelId))
        {
            throw new ProjectFormatException(
                "The selected workflow model does not exist in the project model library.");
        }

        if (SelectedAnimationSourceId is { } sourceId &&
            !sources.ContainsKey(sourceId))
        {
            throw new ProjectFormatException(
                "The selected workflow animation source does not exist.");
        }

        if (SelectedAnimationVariantId is { } variantId)
        {
            if (!variants.TryGetValue(
                    variantId,
                    out ProjectAnimationVariant? variant))
            {
                throw new ProjectFormatException(
                    "The selected workflow animation variant does not exist.");
            }

            if (SelectedAnimationSourceId is { } selectedSourceId &&
                variant.SourceId != selectedSourceId)
            {
                throw new ProjectFormatException(
                    "The selected workflow variant does not belong to the selected source.");
            }
        }
    }
}

/// <summary>
/// Optional schema-1 state for the independent Models workspace. The model
/// itself remains in a project-relative, fingerprinted .dlrmodel asset; this
/// record contains only the UI state needed to resume that authoring session.
/// </summary>
public sealed record ProjectModelsWorkspaceState
{
    public Guid PackageAssetId { get; init; }

    public Guid? SelectedAnimationClipId { get; init; }

    public ProjectCustomModelPreviewMode PreviewMode { get; init; } =
        ProjectCustomModelPreviewMode.Dl1Output;

    public bool ShowMeshes { get; init; } = true;

    public bool ShowBones { get; init; } = true;

    public bool ShowHelpers { get; init; } = true;

    public bool ShowCameraHelpers { get; init; } = true;

    public bool ShowPropHelpers { get; init; } = true;

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds)
    {
        if (PackageAssetId == Guid.Empty ||
            !assetKinds.TryGetValue(PackageAssetId, out ProjectAssetKind kind) ||
            kind != ProjectAssetKind.CustomModelSource)
        {
            throw new ProjectFormatException(
                "The Models workspace must reference a project-owned custom-model source asset.");
        }

        if (SelectedAnimationClipId == Guid.Empty)
        {
            throw new ProjectFormatException(
                "The selected Models-workspace animation identifier cannot be empty.");
        }

        if (!Enum.IsDefined(PreviewMode))
        {
            throw new ProjectFormatException(
                $"Unsupported Models-workspace preview mode '{PreviewMode}'.");
        }
    }
}

public sealed record ProjectRetailAssetIdentity
{
    public string InstallFingerprint { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderPack { get; init; } = string.Empty;

    public int ResourceType { get; init; }

    public int? ResourceIndex { get; init; }

    public string ResourceName { get; init; } = string.Empty;

    public int Precedence { get; init; }

    public string ContentSha256 { get; init; } = string.Empty;

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstallFingerprint, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderId, parameterName);
        ProjectAssetReference.ValidatePortableRelativePath(ProviderPack, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ResourceName, parameterName);
        if (ResourceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Retail resource indexes cannot be negative.");
        }

        ProjectAssetReference.ValidateSha256(ContentSha256, parameterName);
    }
}

public sealed record ProjectAssetReference
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public ProjectAssetKind Kind { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public string? ResourceId { get; init; }

    public string? ContentSha256 { get; init; }

    public ProjectRetailAssetIdentity? RetailIdentity { get; init; }

    internal void Validate(string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("Asset identifiers cannot be empty.", parameterName);
        }

        ValidatePortableRelativePath(RelativePath, parameterName);
        if (ContentSha256 is not null)
        {
            ValidateSha256(ContentSha256, parameterName);
        }

        if (RetailIdentity is not null)
        {
            if (Kind != ProjectAssetKind.RetailGameResource)
            {
                throw new ArgumentException(
                    "Only retail-game project assets may carry a retail identity.",
                    parameterName);
            }

            RetailIdentity.Validate(parameterName);
        }
    }

    internal static void ValidatePortableRelativePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (Path.IsPathRooted(path) ||
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment == ".."))
        {
            throw new ArgumentException(
                "Project asset paths must be portable, project-relative paths.",
                parameterName);
        }
    }

    internal static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Asset SHA-256 values must contain exactly 64 hexadecimal characters.",
                parameterName);
        }
    }
}

public sealed record ProjectBoneMapping
{
    public string SourceBoneName { get; init; } = string.Empty;

    public string TargetBoneName { get; init; } = string.Empty;

    public string Method { get; init; } = "manual";

    public double Confidence { get; init; }

    public string Evidence { get; init; } =
        "No mapping evidence recorded.";

    public ProjectMappingReviewOrigin ReviewOrigin { get; init; }

    public string ScorerVersion { get; init; } = "unscored-v1";

    public string EvidenceFingerprint { get; init; } = new('0', 64);

    public bool IsLocked { get; init; }

    public bool IsReviewed { get; init; }

    public RetargetMappingKind MappingKind { get; init; } =
        RetargetMappingKind.Bone;

    public RetargetTransferPolicy TransferPolicy { get; init; } =
        RetargetTransferPolicy.GlobalBindBasis;

    public RetargetComponentPolicy ComponentPolicy { get; init; } =
        RetargetComponentPolicy.FullTransform;

    /// <summary>
    /// Schema-3 flags. Null is accepted only as the in-memory representation
    /// of a schema-1/2 row before serializer normalization.
    /// </summary>
    public RetargetTransformComponents? TransformComponents { get; init; }

    [JsonIgnore]
    public RetargetTransformComponents EffectiveTransformComponents =>
        TransformComponents ??
        RetargetTransformComponentsCompatibility.FromLegacy(ComponentPolicy);

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceBoneName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetBoneName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Method, parameterName);
        ValidateMappingProvenance(
            Confidence,
            Evidence,
            ReviewOrigin,
            ScorerVersion,
            EvidenceFingerprint,
            IsReviewed,
            IsLocked,
            parameterName);
        RetargetTransformComponents effective = EffectiveTransformComponents;
        if (!Enum.IsDefined(MappingKind) ||
            !Enum.IsDefined(TransferPolicy) ||
            !Enum.IsDefined(ComponentPolicy) ||
            effective == RetargetTransformComponents.None ||
            (effective & ~RetargetTransformComponents.All) != 0)
        {
            throw new ArgumentException(
                "Bone mappings contain an unsupported mapping, transfer, or component policy.",
                parameterName);
        }
    }

    internal static void ValidateMappingProvenance(
        double confidence,
        string evidence,
        ProjectMappingReviewOrigin reviewOrigin,
        string scorerVersion,
        string evidenceFingerprint,
        bool isReviewed,
        bool isLocked,
        string parameterName)
    {
        if (!double.IsFinite(confidence) ||
            confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Mapping confidence must be between zero and one.");
        }

        if (string.IsNullOrWhiteSpace(evidence) ||
            evidence.Length > 2_048)
        {
            throw new ArgumentException(
                "Mapping evidence must contain between 1 and 2048 characters.",
                parameterName);
        }

        if (!Enum.IsDefined(reviewOrigin) ||
            string.IsNullOrWhiteSpace(scorerVersion) ||
            scorerVersion.Length > 128)
        {
            throw new ArgumentException(
                "Mapping review origin and scorer version must be valid.",
                parameterName);
        }

        ProjectAssetReference.ValidateSha256(
            evidenceFingerprint,
            parameterName);
        if (isLocked && !isReviewed)
        {
            throw new ArgumentException(
                "A mapping cannot be locked until it has been reviewed.",
                parameterName);
        }

        if (reviewOrigin == ProjectMappingReviewOrigin.None &&
            (isReviewed || isLocked))
        {
            throw new ArgumentException(
                "A reviewed mapping must record whether review was explicit or assisted.",
                parameterName);
        }

        if (reviewOrigin == ProjectMappingReviewOrigin.Explicit &&
            !isReviewed)
        {
            throw new ArgumentException(
                "An explicit mapping review origin requires a reviewed row.",
                parameterName);
        }

        if (reviewOrigin == ProjectMappingReviewOrigin.Assisted &&
            (!isReviewed || !isLocked || confidence < 0.90))
        {
            throw new ArgumentException(
                "An assisted mapping approval must be reviewed, locked, and score at least 0.90.",
                parameterName);
        }
    }
}

public sealed record ProjectTargetBindReview
{
    public int TargetBoneIndex { get; init; }

    public string TargetBoneName { get; init; } = string.Empty;

    internal void Validate(string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            TargetBoneIndex,
            parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            TargetBoneName,
            parameterName);
    }
}

/// <summary>
/// Records the unit declared when the source scalar was imported. Project
/// animation clips contain authored normalized values; this field preserves
/// the explicit source interpretation needed to reproduce the import.
/// </summary>
public enum ProjectMorphSourceValueUnit
{
    Normalized,
    Percent,
}

public sealed record ProjectMorphBinding
{
    public string SourceChannel { get; init; } = string.Empty;

    public ProjectMorphSourceValueUnit SourceValueUnit { get; init; } =
        ProjectMorphSourceValueUnit.Normalized;

    public string TargetMorph { get; init; } = string.Empty;

    public uint? TargetDescriptorHash { get; init; }

    public double Weight { get; init; } = 1.0;

    public double Bias { get; init; }

    public bool Enabled { get; init; } = true;

    public double Confidence { get; init; } = 1.0;

    public string Evidence { get; init; } =
        "No mapping evidence recorded.";

    public ProjectMappingReviewOrigin ReviewOrigin { get; init; }

    public string ScorerVersion { get; init; } = "unscored-v1";

    public string EvidenceFingerprint { get; init; } = new('0', 64);

    public string Method { get; init; } = "manual";

    /// <summary>
    /// Records that an author has inspected the suggested source-to-target
    /// relationship. The safe default is false so a mapping deserialized from
    /// a project that predates this field cannot become exportable by default.
    /// </summary>
    public bool IsReviewed { get; init; }

    /// <summary>
    /// Records the author's explicit decision to lock this reviewed mapping
    /// for deterministic export. Preview may still evaluate an unlocked
    /// suggestion so it can be reviewed visually.
    /// </summary>
    public bool IsLocked { get; init; }

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceChannel, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetMorph, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Method, parameterName);
        if (!Enum.IsDefined(SourceValueUnit))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The morph binding source value unit is invalid.");
        }

        if (!double.IsFinite(Weight) ||
            !double.IsFinite(Bias))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Morph binding weights and biases must be finite.");
        }

        ProjectBoneMapping.ValidateMappingProvenance(
            Confidence,
            Evidence,
            ReviewOrigin,
            ScorerVersion,
            EvidenceFingerprint,
            IsReviewed,
            IsLocked,
            parameterName);
    }
}

public sealed record ProjectIkLayer
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string ChainName { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public double Weight { get; init; } = 1.0;

    public bool BakeToEditLayer { get; init; }

    public ImmutableArray<ProjectIkKeyframe> Keyframes { get; init; } = [];

    internal void Validate(
        long frameCount,
        string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("IK layer identifiers cannot be empty.", parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ChainName, parameterName);
        if (!double.IsFinite(Weight) || Weight is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "IK layer weight must be between zero and one.");
        }

        if (Keyframes.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "IK layers require at least one effector key.",
                parameterName);
        }

        for (var index = 0; index < Keyframes.Length; index++)
        {
            Keyframes[index].Validate(frameCount, parameterName);
            if (index > 0 &&
                Keyframes[index].Frame <= Keyframes[index - 1].Frame)
            {
                throw new ArgumentException(
                    "IK keyframes must be strictly increasing.",
                    parameterName);
            }
        }
    }
}

public sealed record ProjectIkKeyframe
{
    public double Frame { get; init; }

    public Vector3D Effector { get; init; }

    public Vector3D Pole { get; init; }

    public QuaternionD? EndOrientation { get; init; }

    internal void Validate(long frameCount, string parameterName)
    {
        if (!double.IsFinite(Frame) ||
            Frame < 0 ||
            Frame > frameCount - 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "IK keyframes must lie within the animation range.");
        }

        if (!Effector.IsFinite ||
            !Pole.IsFinite ||
            (EndOrientation.HasValue &&
             !EndOrientation.Value.IsFinite))
        {
            throw new ArgumentException(
                "IK effector, pole, and orientation values must be finite.",
                parameterName);
        }
    }
}

public sealed record ProjectAnimation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Stable identity shared by clean target variants of one immutable
    /// animation source. This is additive within the C# schema-1 format.
    /// </summary>
    public Guid? VariantGroupId { get; init; }

    public string Name { get; init; } = string.Empty;

    public Guid SourceAssetId { get; init; }

    /// <summary>
    /// Exact immutable interpretation of the source asset. Older C# schema-1
    /// projects may omit this field; ANM2 playback then fails closed until the
    /// user creates a new document through Rebind Source.
    /// </summary>
    public ProjectAnimationSourceBinding? SourceBinding { get; init; }

    public Guid? MimicAssetId { get; init; }

    /// <summary>
    /// Immutable interpretation of a separate ANM2 facial source. Mixed
    /// body/facial primary sources keep this null because their partition is
    /// already stored in <see cref="SourceBinding"/>.
    /// </summary>
    public ProjectAnimationSourceBinding? FacialAnimationSourceBinding
    { get; init; }

    /// <summary>
    /// Project-relative user-authored FBX whose sampled scalar curves feed the
    /// reviewed DL1 morph bindings. This is mutually exclusive with an
    /// imported mimic ANM2 so preview and export have one facial source.
    /// </summary>
    public Guid? FacialSourceAssetId { get; init; }

    public ProjectMorphSourceValueUnit? FacialSourceValueUnit { get; init; }

    public FacialClipTiming? FacialTiming { get; init; }

    public Guid? TargetAssetId { get; init; }

    public string TargetRigId { get; init; } = string.Empty;

    public string? SourceRigSignature { get; init; }

    public string? TargetRigSignature { get; init; }

    public string? SourceAnimationSkeletonSignature { get; init; }

    public string? TargetAnimationSkeletonSignature { get; init; }

    public ProjectAnimationBindingMode BindingMode { get; init; } =
        ProjectAnimationBindingMode.ExactDirect;

    public DirectRigBinding? DirectBinding { get; init; }

    public string? BindingEvidenceFingerprint { get; init; }

    public string? BindingPolicyVersion { get; init; }

    public string? MappingFingerprint { get; init; }

    public string? MimicProfileId { get; init; }

    public string? MimicMappingFingerprint { get; init; }

    public FrameRate FrameRate { get; init; } = new(30, 1);

    public long FrameCount { get; init; } = 1;

    public Dl1RootMotionMode RootMotionMode { get; init; } =
        Dl1RootMotionMode.Recorded;

    /// <summary>
    /// Optional explicit skeletal-root track for this target variant. A null
    /// value uses the target rig's versioned DL1 root-role resolution. The
    /// value belongs to the animation variant because different retail rigs
    /// may expose different root track names.
    /// </summary>
    public string? RootBoneName { get; init; }

    /// <summary>
    /// Preview-only actor/world accumulation. This is deliberately separate
    /// from the exportable root policy above.
    /// </summary>
    public bool PreviewMotionAccumulationEnabled { get; init; }

    public ImmutableArray<ProjectBoneMapping> BoneMappings { get; init; } = [];

    public ImmutableArray<ProjectTargetBindReview> TargetBindReviews { get; init; } = [];

    public ImmutableArray<BoneEditLayer> EditLayers { get; init; } = [];

    public ImmutableArray<ProjectMorphBinding> MorphBindings { get; init; } = [];

    public ImmutableArray<MorphEditLayer> MorphEditLayers { get; init; } = [];

    public ImmutableArray<ProjectIkLayer> IkLayers { get; init; } = [];

    public ImmutableArray<AttachmentBinding> Attachments { get; init; } = [];

    internal void Validate(
        IReadOnlyDictionary<Guid, ProjectAssetKind> assetKinds,
        string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("Animation identifiers cannot be empty.", parameterName);
        }

        if (VariantGroupId == Guid.Empty)
        {
            throw new ArgumentException(
                "Animation variant-group identifiers cannot be empty.",
                parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetRigId, parameterName);
        if (MimicProfileId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                MimicProfileId,
                parameterName);
        }

        ValidateOptionalSha256(
            MimicMappingFingerprint,
            "mimic mapping fingerprint",
            parameterName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FrameCount, parameterName);
        if (FrameRate.Numerator <= 0 || FrameRate.Denominator <= 0)
        {
            throw new ArgumentException(
                $"Animation '{Name}' has an invalid frame rate.",
                parameterName);
        }

        if (!assetKinds.TryGetValue(
                SourceAssetId,
                out ProjectAssetKind sourceKind) ||
            sourceKind is not (
                ProjectAssetKind.SourceAnimation or
                ProjectAssetKind.RetailGameResource))
        {
            throw new ArgumentException(
                $"Animation '{Name}' refers to an unknown source animation asset.",
                parameterName);
        }

        if (SourceBinding is { } sourceBinding)
        {
            sourceBinding.Validate(assetKinds, parameterName);
            if (sourceBinding.AssetId != SourceAssetId ||
                !string.Equals(
                    sourceBinding.SourceRigSignature,
                    SourceRigSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Animation '{Name}' source binding disagrees with its source asset or rig signature.",
                    parameterName);
            }
        }
        else if (sourceKind != ProjectAssetKind.SourceAnimation)
        {
            throw new ArgumentException(
                $"Animation '{Name}' has a retail source without an immutable source binding.",
                parameterName);
        }

        ProjectAssetKind? mimicKind = null;
        if (MimicAssetId is { } mimicAssetId)
        {
            if (!assetKinds.TryGetValue(
                    mimicAssetId,
                    out ProjectAssetKind resolvedMimicKind))
            {
                throw new ArgumentException(
                    $"Animation '{Name}' refers to an unknown mimic source-animation asset.",
                    parameterName);
            }

            mimicKind = resolvedMimicKind;
        }

        if (FacialAnimationSourceBinding is { } facialBinding)
        {
            facialBinding.Validate(assetKinds, parameterName);
            if (MimicAssetId != facialBinding.AssetId ||
                (facialBinding.Roles & AnimationSourceRoles.Facial) == 0)
            {
                throw new ArgumentException(
                    $"Animation '{Name}' facial source binding disagrees with its mimic asset or has no facial role.",
                    parameterName);
            }
        }
        else if (MimicAssetId is not null &&
                 mimicKind != ProjectAssetKind.SourceAnimation)
        {
            throw new ArgumentException(
                $"Animation '{Name}' has a retail facial source without an immutable facial binding.",
                parameterName);
        }

        if (FacialSourceAssetId is { } facialSourceAssetId &&
            (!assetKinds.TryGetValue(
                 facialSourceAssetId,
                 out ProjectAssetKind facialSourceKind) ||
             facialSourceKind != ProjectAssetKind.SourceAnimation))
        {
            throw new ArgumentException(
                $"Animation '{Name}' refers to an unknown facial FBX source-animation asset.",
                parameterName);
        }

        if (MimicAssetId is not null &&
            FacialSourceAssetId is not null)
        {
            throw new ArgumentException(
                $"Animation '{Name}' cannot use both an imported mimic ANM2 and a facial FBX source.",
                parameterName);
        }

        bool primaryFbxFacial = SourceBinding is
            {
                Kind: AnimationSourceKind.LocalFbx,
            } primaryBinding &&
            (primaryBinding.Roles & AnimationSourceRoles.Facial) != 0;
        if (FacialSourceAssetId.HasValue &&
            !FacialSourceValueUnit.HasValue ||
            !FacialSourceAssetId.HasValue &&
            FacialSourceValueUnit.HasValue &&
            !primaryFbxFacial)
        {
            throw new ArgumentException(
                $"Animation '{Name}' must bind an explicit facial FBX source-value unit to either its attached facial FBX or its primary combined FBX source.",
                parameterName);
        }

        if (FacialSourceValueUnit is { } facialSourceValueUnit &&
            !Enum.IsDefined(facialSourceValueUnit))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Animation '{Name}' contains an unsupported facial FBX source-value unit.");
        }

        FacialTiming?.Validate(FrameCount);

        if (!Enum.IsDefined(BindingMode))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Animation '{Name}' has an unsupported binding mode.");
        }
        ValidateOptionalSha256(
            SourceAnimationSkeletonSignature,
            "source animation skeleton signature",
            parameterName);
        ValidateOptionalSha256(
            TargetAnimationSkeletonSignature,
            "target animation skeleton signature",
            parameterName);
        ValidateOptionalSha256(
            BindingEvidenceFingerprint,
            "binding evidence fingerprint",
            parameterName);
        if (BindingMode == ProjectAnimationBindingMode.CompatibleDirect)
        {
            if (DirectBinding is null ||
                !string.Equals(
                    BindingEvidenceFingerprint,
                    DirectBinding.EvidenceFingerprint,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    BindingPolicyVersion,
                    DirectBinding.Policy,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Animation '{Name}' has incomplete compatible-direct evidence.",
                    parameterName);
            }
        }
        else if (DirectBinding is not null)
        {
            throw new ArgumentException(
                $"Animation '{Name}' stores direct-binding rows for a non-compatible binding mode.",
                parameterName);
        }

        if (!Enum.IsDefined(RootMotionMode))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Animation '{Name}' contains an unsupported DL1 root-motion mode.");
        }

        if (RootBoneName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                RootBoneName,
                parameterName);
        }

        if (FacialSourceAssetId is not null &&
            (string.IsNullOrWhiteSpace(MimicProfileId) ||
             string.IsNullOrWhiteSpace(MimicMappingFingerprint)))
        {
            throw new ArgumentException(
                $"Animation '{Name}' must bind its facial FBX source to a versioned mimic profile and mapping fingerprint.",
                parameterName);
        }

        if (TargetAssetId is { } targetAssetId &&
            (!assetKinds.TryGetValue(
                 targetAssetId,
                 out ProjectAssetKind targetKind) ||
             targetKind is not (
                 ProjectAssetKind.RetailGameResource or
                 ProjectAssetKind.CustomModelSource)))
        {
            throw new ArgumentException(
                $"Animation '{Name}' refers to an unknown retail or custom-model target asset.",
                parameterName);
        }

        if (BoneMappings.IsDefault ||
            TargetBindReviews.IsDefault ||
            EditLayers.IsDefault ||
            MorphBindings.IsDefault ||
            MorphEditLayers.IsDefault ||
            IkLayers.IsDefault ||
            Attachments.IsDefault)
        {
            throw new ArgumentException(
                "Animation mapping and edit-layer collections must be initialized.",
                parameterName);
        }

        foreach (ProjectBoneMapping mapping in BoneMappings)
        {
            mapping.Validate(parameterName);
        }

        foreach (ProjectTargetBindReview review in TargetBindReviews)
        {
            review.Validate(parameterName);
        }

        if (BoneMappings
            .Select(static mapping => mapping.TargetBoneName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != BoneMappings.Length)
        {
            throw new ArgumentException(
                $"Animation '{Name}' maps a target bone more than once.",
                parameterName);
        }

        if (TargetBindReviews
            .Select(static review => review.TargetBoneIndex)
            .Distinct()
            .Count() != TargetBindReviews.Length)
        {
            throw new ArgumentException(
                $"Animation '{Name}' reviews a target-bind bone more than once.",
                parameterName);
        }

        foreach (BoneEditLayer layer in EditLayers)
        {
            foreach (BoneEditTrack track in layer.Tracks)
            {
                if (track.Keyframes[^1].Frame > FrameCount - 1)
                {
                    throw new ArgumentException(
                        $"Edit layer '{layer.Name}' contains a key beyond the animation range.",
                        parameterName);
                }
            }
        }

        foreach (ProjectMorphBinding binding in MorphBindings)
        {
            binding.Validate(parameterName);
        }

        if (MorphBindings
            .Select(static binding =>
                binding.SourceChannel + "\0" +
                binding.TargetMorph)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != MorphBindings.Length)
        {
            throw new ArgumentException(
                $"Animation '{Name}' contains a duplicate source-to-target morph binding.",
                parameterName);
        }

        foreach (MorphEditLayer layer in MorphEditLayers)
        {
            foreach (MorphEditTrack track in layer.Tracks)
            {
                if (track.Keyframes[^1].Frame > FrameCount - 1)
                {
                    throw new ArgumentException(
                        $"Facial layer '{layer.Name}' contains a key beyond the animation range.",
                        parameterName);
                }
            }
        }

        if (MorphEditLayers
            .Select(static layer => layer.Id)
            .Distinct()
            .Count() != MorphEditLayers.Length)
        {
            throw new ArgumentException(
                $"Animation '{Name}' contains duplicate facial layer identifiers.",
                parameterName);
        }

        foreach (ProjectIkLayer layer in IkLayers)
        {
            layer.Validate(FrameCount, parameterName);
        }

        if (IkLayers.Select(static layer => layer.Id).Distinct().Count() !=
            IkLayers.Length)
        {
            throw new ArgumentException(
                $"Animation '{Name}' contains duplicate IK layer identifiers.",
                parameterName);
        }

        foreach (AttachmentBinding attachment in Attachments)
        {
            if (!assetKinds.ContainsKey(attachment.AssetId))
            {
                throw new ArgumentException(
                    $"Attachment '{attachment.Name}' refers to an unknown asset.",
                    parameterName);
            }
        }

        if (Attachments.Length > AttachmentBinding.MaximumPerAnimation)
        {
            throw new ArgumentException(
                $"Animation '{Name}' contains {Attachments.Length} attachments; the bounded DL1 authoring limit is {AttachmentBinding.MaximumPerAnimation}.",
                parameterName);
        }

        if (Attachments.Select(static attachment => attachment.Id).Distinct().Count() !=
            Attachments.Length)
        {
            throw new ArgumentException(
                $"Animation '{Name}' contains duplicate attachment identifiers.",
                parameterName);
        }
    }

    private static void ValidateOptionalSha256(
        string? value,
        string description,
        string parameterName)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                $"Animation {description} must contain 64 hexadecimal characters.",
                parameterName);
        }
    }
}

public sealed record Dl1ProjectSettings
{
    public string? InstallFingerprint { get; init; }

    public string? ValidatedBuildFingerprint { get; init; }

    public ImmutableArray<string> AdditionalRpackRoots { get; init; } = [];

    /// <summary>
    /// Keeps the camera-helper overlay choice with the project preview
    /// configuration. This affects only editor visualization.
    /// </summary>
    public bool ShowCameraHelpers { get; init; } = true;

    /// <summary>
    /// Enables the explicitly supplied FPP projection capture. A missing
    /// capture remains a valid, fail-closed editor state so a project can
    /// remember that runtime-derived values are required without inventing
    /// fallback numbers.
    /// </summary>
    public bool UseFppProjectionCapture { get; init; }

    /// <summary>
    /// User-supplied runtime capture values. These are non-proprietary numeric
    /// authoring settings and never imply that the project is game validated.
    /// </summary>
    public Dl1FppProjectionCapture? FppProjectionCapture { get; init; }

    /// <summary>
    /// Enables the explicitly supplied external movie reference camera.
    /// This is an authoring input for the DL1 movie context; it is not a
    /// trusted game-validation capture.
    /// </summary>
    public bool UseMovieReferenceCameraCapture { get; init; }

    /// <summary>
    /// User-supplied transform and lens for the external IBaseCamera used by
    /// DL1 movie preview. A rig helper named RefCamera is intentionally not a
    /// substitute for this snapshot.
    /// </summary>
    public Dl1MovieReferenceCameraCapture? MovieReferenceCameraCapture
    {
        get;
        init;
    }

    internal void Validate()
    {
        if (AdditionalRpackRoots.IsDefault)
        {
            throw new ProjectFormatException(
                "Additional RP6L root collection must be initialized.");
        }

        foreach (string root in AdditionalRpackRoots)
        {
            ProjectAssetReference.ValidatePortableRelativePath(
                root,
                nameof(AdditionalRpackRoots));
        }

        FppProjectionCapture?.Validate();
        MovieReferenceCameraCapture?.Validate();
    }
}

/// <summary>
/// Explicit FPP lens values copied from a user/runtime capture. The scene
/// camera uses a vertical field of view. DL1's hands projection records its
/// own field-of-view axis and always has an infinite far plane.
/// </summary>
public sealed record Dl1FppProjectionCapture
{
    public string? CaptureLabel { get; init; }

    public double SceneVerticalFieldOfViewDegrees { get; init; }

    public double SceneAspectRatio { get; init; }

    public double SceneNearClipMeters { get; init; }

    public double HandsFieldOfViewDegrees { get; init; }

    public Dl1ProjectionFovAxis HandsFieldOfViewAxis { get; init; }

    public double HandsAspectRatio { get; init; }

    public double HandsNearClipMeters { get; init; }

    public Dl1FppProjectionSnapshot CreateSnapshot(
        double editorFarClipMeters)
    {
        var sceneLens = new CameraLens(
            SceneVerticalFieldOfViewDegrees,
            SceneAspectRatio,
            SceneNearClipMeters,
            editorFarClipMeters);
        var handsProjection = new Dl1ProjectionParameters(
            HandsFieldOfViewDegrees,
            HandsFieldOfViewAxis,
            HandsAspectRatio,
            HandsNearClipMeters,
            Dl1ProjectionFarPlane.Infinite);
        return new Dl1FppProjectionSnapshot(
            sceneLens,
            handsProjection);
    }

    internal void Validate()
    {
        if (CaptureLabel is { Length: > 256 })
        {
            throw new ProjectFormatException(
                "An FPP projection capture label cannot exceed 256 characters.");
        }

        try
        {
            _ = CreateSnapshot(CameraLens.Default.FarClipMeters);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException(
                "The stored FPP projection capture is invalid.",
                exception);
        }
    }
}

/// <summary>
/// Explicit editor snapshot of the external IBaseCamera registered for DL1
/// movie playback. The camera transform is stored as translation plus an XYZW
/// quaternion with unit scale so matrix conventions remain inside the core
/// transform wrapper.
/// </summary>
public sealed record Dl1MovieReferenceCameraCapture
{
    public string? CaptureLabel { get; init; }

    public TransformTRS WorldTransform { get; init; } =
        TransformTRS.Identity;

    public CameraLens Lens { get; init; } = CameraLens.Default;

    public Dl1MovieReferenceCameraSnapshot CreateSnapshot()
    {
        const double unitScaleTolerance = 1e-9;
        if (!WorldTransform.IsFinite ||
            Math.Abs(WorldTransform.Scale.X - 1.0) >
                unitScaleTolerance ||
            Math.Abs(WorldTransform.Scale.Y - 1.0) >
                unitScaleTolerance ||
            Math.Abs(WorldTransform.Scale.Z - 1.0) >
                unitScaleTolerance)
        {
            throw new ArgumentException(
                "A movie reference camera requires a finite transform with unit scale.",
                nameof(WorldTransform));
        }

        TransformTRS normalized = WorldTransform.Normalized();
        return new Dl1MovieReferenceCameraSnapshot(
            normalized.ToMatrix(),
            Lens);
    }

    internal void Validate()
    {
        if (CaptureLabel is { Length: > 256 })
        {
            throw new ProjectFormatException(
                "A movie reference-camera capture label cannot exceed 256 characters.");
        }

        try
        {
            _ = CreateSnapshot();
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException)
        {
            throw new ProjectFormatException(
                "The stored movie reference-camera capture is invalid.",
                exception);
        }
    }
}

public sealed record DlraProject
{
    public const int CurrentSchemaVersion = 3;

    public const string FormatIdentifier = "dl-reanimated-csharp-project";

    public const string DyingLight1Game = "dying-light-1";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Format { get; init; } = FormatIdentifier;

    public Guid ProjectId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Untitled";

    public string Game { get; init; } = DyingLight1Game;

    public ImmutableArray<ProjectAssetReference> Assets { get; init; } = [];

    public ImmutableArray<ProjectModelEntry> Models { get; init; } = [];

    public ImmutableArray<ProjectAnimationSource> AnimationSources { get; init; } = [];

    public ImmutableArray<ProjectAnimationVariant> AnimationVariants { get; init; } = [];

    public ImmutableArray<ProjectAnimationLibrary> AnimationLibraries
    { get; init; } = [];

    public ProjectExportSelection ExportSelection { get; init; } = new();

    /// <summary>
    /// Compatibility projection used while the WPF surface moves to the
    /// source/variant domain. Package-backed embedded stacks exist
    /// only in AnimationSources and therefore never require a duplicate
    /// SourceAnimation asset or legacy projection row.
    /// </summary>
    public ImmutableArray<ProjectAnimation> Animations { get; init; } = [];

    public Guid? ActiveAnimationId { get; init; }

    public ProjectModelsWorkspaceState? ModelsWorkspace { get; init; }

    public ProjectWorkflowState Workflow { get; init; } = new();

    public Dl1ProjectSettings Dl1Settings { get; init; } = new();

    public ProjectPreviewMode PreviewMode { get; init; } =
        ProjectPreviewMode.Dl1Profile;

    public PreviewProfile PreviewProfile { get; init; } =
        PreviewProfile.ThirdPersonAuthoring;

    public static DlraProject Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new DlraProject { Name = name };
    }

    public void Validate()
    {
        if (SchemaVersion is not (1 or 2 or CurrentSchemaVersion))
        {
            throw new ProjectFormatException(
                $"Only C# schema-1, schema-2, and schema-{CurrentSchemaVersion} projects are supported in memory.");
        }

        if (SchemaVersion == CurrentSchemaVersion &&
            Models.IsEmpty &&
            AnimationSources.IsEmpty &&
            AnimationVariants.IsEmpty &&
            (!Animations.IsEmpty || ModelsWorkspace is not null))
        {
            // Transitional WPF builds still construct the schema-1 projection
            // on a fresh project. Validate the same deterministic migration
            // that SaveAtomic will persist instead of making those callers
            // manufacture partially synchronized source/variant records.
            ProjectSerializer.MigrateSchema1(
                    this with { SchemaVersion = 1 },
                    preserveSourceOnlyCompatibilityRows: true)
                .Validate();
            return;
        }

        if (!string.Equals(Format, FormatIdentifier, StringComparison.Ordinal))
        {
            throw new ProjectFormatException($"Unexpected project format '{Format}'.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new ProjectFormatException("The project identifier cannot be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!string.Equals(Game, DyingLight1Game, StringComparison.Ordinal))
        {
            throw new ProjectFormatException(
                "The first C# project format accepts Dying Light 1 projects only.");
        }

        if (SchemaVersion == 1)
        {
            ValidateSchema1Compatibility();
            return;
        }

        if (Assets.IsDefault ||
            Models.IsDefault ||
            AnimationSources.IsDefault ||
            AnimationVariants.IsDefault ||
            AnimationLibraries.IsDefault ||
            Animations.IsDefault)
        {
            throw new ProjectFormatException("Project collections must be initialized.");
        }

        ArgumentNullException.ThrowIfNull(PreviewProfile);
        ArgumentNullException.ThrowIfNull(Dl1Settings);
        ArgumentNullException.ThrowIfNull(Workflow);
        ArgumentNullException.ThrowIfNull(ExportSelection);
        if (!Enum.IsDefined(PreviewMode))
        {
            throw new ProjectFormatException(
                $"Unsupported preview mode '{PreviewMode}'.");
        }

        Dl1Settings.Validate();

        foreach (ProjectAssetReference asset in Assets)
        {
            asset.Validate(nameof(Assets));
        }

        Dictionary<Guid, ProjectAssetKind> assetKinds;
        try
        {
            assetKinds = Assets.ToDictionary(
                static asset => asset.Id,
                static asset => asset.Kind);
        }
        catch (ArgumentException)
        {
            throw new ProjectFormatException("Project asset identifiers must be unique.");
        }

        foreach (ProjectAnimation animation in Animations)
        {
            animation.Validate(assetKinds, nameof(Animations));
        }

        foreach (ProjectModelEntry model in Models)
        {
            model.Validate(assetKinds, nameof(Models));
        }

        Dictionary<Guid, ProjectModelEntry> models;
        Dictionary<Guid, ProjectAnimationSource> sources;
        Dictionary<Guid, ProjectAnimationVariant> variants;
        try
        {
            models = Models.ToDictionary(static model => model.Id);
            sources = AnimationSources.ToDictionary(
                static source => source.Id);
            variants = AnimationVariants.ToDictionary(
                static variant => variant.Id);
        }
        catch (ArgumentException)
        {
            throw new ProjectFormatException(
                "Project model, animation-source, and animation-variant identifiers must be unique within their libraries.");
        }

        if (Models
            .Select(static model => model.AssetId)
            .Distinct()
            .Count() != Models.Length)
        {
            throw new ProjectFormatException(
                "A project asset cannot appear more than once in the model library.");
        }

        foreach (ProjectAnimationSource source in AnimationSources)
        {
            source.Validate(assetKinds, nameof(AnimationSources));
            source.Presentation?.Validate(
                source,
                models,
                nameof(AnimationSources));
        }

        foreach (ProjectAnimationVariant variant in AnimationVariants)
        {
            variant.Validate(
                assetKinds,
                models,
                sources,
                nameof(AnimationVariants));
        }

        Dictionary<Guid, ProjectAnimationLibrary> libraries;
        try
        {
            libraries = AnimationLibraries.ToDictionary(
                static library => library.Id);
        }
        catch (ArgumentException)
        {
            throw new ProjectFormatException(
                "Project animation-library identifiers must be unique.");
        }

        foreach (ProjectAnimationLibrary library in AnimationLibraries)
        {
            library.Validate(nameof(AnimationLibraries));
        }

        if (AnimationLibraries
                .Select(static library => library.ResourceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != AnimationLibraries.Length)
        {
            throw new ProjectFormatException(
                "Project animation-library resource names must be unique ignoring case.");
        }

        ValidateAnimationLibraryGraph(libraries);
        foreach (ProjectModelEntry model in Models)
        {
            if (model.RootAnimationLibraryId is { } libraryId &&
                !libraries.ContainsKey(libraryId))
            {
                throw new ProjectFormatException(
                    $"Project model '{model.Name}' refers to an unknown root animation library.");
            }
        }

        foreach (ProjectAnimationVariant variant in AnimationVariants)
        {
            if (variant.OwningAnimationLibraryId is { } libraryId &&
                !libraries.ContainsKey(libraryId))
            {
                throw new ProjectFormatException(
                    $"Animation variant '{variant.Name}' refers to an unknown owning animation library.");
            }
        }

        foreach (IGrouping<Guid, ProjectAnimationVariant> group in
                 AnimationVariants
                     .Where(static variant =>
                         variant.OwningAnimationLibraryId is not null &&
                         variant.OutputAnm2Name is not null)
                     .GroupBy(static variant =>
                         variant.OwningAnimationLibraryId!.Value))
        {
            if (group.Select(static variant => variant.OutputAnm2Name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != group.Count())
            {
                string libraryName = libraries.TryGetValue(
                    group.Key,
                    out ProjectAnimationLibrary? library)
                    ? library.DisplayName
                    : group.Key.ToString("N");
                throw new ProjectFormatException(
                    $"Animation library '{libraryName}' contains duplicate target-specific ANM2 identities.");
            }
        }

        ExportSelection.Validate(models, variants);

        Workflow.Validate(models, sources, variants);

        ModelsWorkspace?.Validate(assetKinds);

        if (Animations.Select(static animation => animation.Id).Distinct().Count() !=
            Animations.Length)
        {
            throw new ProjectFormatException("Project animation identifiers must be unique.");
        }

        if (ActiveAnimationId is { } activeAnimationId &&
            Animations.All(animation => animation.Id != activeAnimationId) &&
            AnimationVariants.All(variant => variant.Id != activeAnimationId))
        {
            throw new ProjectFormatException(
                "The active animation identifier does not exist in the project animation library.");
        }
    }

    private static void ValidateAnimationLibraryGraph(
        IReadOnlyDictionary<Guid, ProjectAnimationLibrary> libraries)
    {
        foreach (ProjectAnimationLibrary library in libraries.Values)
        {
            var importedProjectIds = new HashSet<Guid>();
            var importedRetailIdentities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var importedResourceNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ProjectAnimationLibraryImport import in library.Imports)
            {
                if (import.ProjectLibraryId is { } importedId)
                {
                    if (!libraries.ContainsKey(importedId))
                    {
                        throw new ProjectFormatException(
                            $"Animation library '{library.DisplayName}' imports an unknown project library.");
                    }

                    if (!importedProjectIds.Add(importedId))
                    {
                        throw new ProjectFormatException(
                            $"Animation library '{library.DisplayName}' imports the same project library more than once.");
                    }

                    if (!importedResourceNames.Add(
                            libraries[importedId].ResourceName))
                    {
                        throw new ProjectFormatException(
                            $"Animation library '{library.DisplayName}' has ambiguous imported script resource identities.");
                    }
                }
                else if (import.RetailScriptIdentity is { } retail)
                {
                    string identity = string.Join(
                        "|",
                        retail.ProviderId,
                        retail.ProviderPack,
                        retail.ResourceName,
                        retail.ContentSha256);
                    if (!importedRetailIdentities.Add(identity))
                    {
                        throw new ProjectFormatException(
                            $"Animation library '{library.DisplayName}' imports the same retail script more than once.");
                    }


                    if (!importedResourceNames.Add(retail.ResourceName))
                    {
                        throw new ProjectFormatException(
                            $"Animation library '{library.DisplayName}' has ambiguous imported script resource identities.");
                    }
                }
            }
        }

        var states = new Dictionary<Guid, int>();
        foreach (Guid id in libraries.Keys.Order())
        {
            Visit(id);
        }

        void Visit(Guid id)
        {
            if (states.GetValueOrDefault(id) == 2)
            {
                return;
            }

            if (states.GetValueOrDefault(id) == 1)
            {
                throw new ProjectFormatException(
                    "Project animation-library imports contain a cycle.");
            }

            states[id] = 1;
            foreach (Guid dependency in libraries[id].Imports
                         .Where(static import =>
                             import.Kind ==
                                 ProjectAnimationLibraryImportKind.ProjectLibrary)
                         .Select(static import =>
                             import.ProjectLibraryId!.Value))
            {
                Visit(dependency);
            }

            states[id] = 2;
        }
    }

    private void ValidateSchema1Compatibility()
    {
        if (Assets.IsDefault || Animations.IsDefault)
        {
            throw new ProjectFormatException(
                "Schema-1 project collections must be initialized.");
        }

        ArgumentNullException.ThrowIfNull(PreviewProfile);
        ArgumentNullException.ThrowIfNull(Dl1Settings);
        if (!Enum.IsDefined(PreviewMode))
        {
            throw new ProjectFormatException(
                $"Unsupported preview mode '{PreviewMode}'.");
        }

        Dl1Settings.Validate();
        foreach (ProjectAssetReference asset in Assets)
        {
            asset.Validate(nameof(Assets));
        }

        Dictionary<Guid, ProjectAssetKind> assetKinds;
        try
        {
            assetKinds = Assets.ToDictionary(
                static asset => asset.Id,
                static asset => asset.Kind);
        }
        catch (ArgumentException)
        {
            throw new ProjectFormatException(
                "Project asset identifiers must be unique.");
        }

        foreach (ProjectAnimation animation in Animations)
        {
            animation.Validate(assetKinds, nameof(Animations));
        }

        ModelsWorkspace?.Validate(assetKinds);
        if (Animations
                .Select(static animation => animation.Id)
                .Distinct()
                .Count() != Animations.Length)
        {
            throw new ProjectFormatException(
                "Project animation identifiers must be unique.");
        }

        if (ActiveAnimationId is { } activeAnimationId &&
            Animations.All(animation => animation.Id != activeAnimationId))
        {
            throw new ProjectFormatException(
                "The active animation identifier does not exist in the project animation library.");
        }
    }
}
