using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Project;

public enum ProjectAssetKind
{
    SourceAnimation,
    RetailGameResource,
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
        if (value.Length != 64 ||
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

    public bool IsLocked { get; init; }

    public bool IsReviewed { get; init; }

    public RetargetMappingKind MappingKind { get; init; } =
        RetargetMappingKind.Bone;

    public RetargetTransferPolicy TransferPolicy { get; init; } =
        RetargetTransferPolicy.GlobalBindBasis;

    public RetargetComponentPolicy ComponentPolicy { get; init; } =
        RetargetComponentPolicy.FullTransform;

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceBoneName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetBoneName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Method, parameterName);
        if (!Enum.IsDefined(MappingKind) ||
            !Enum.IsDefined(TransferPolicy) ||
            !Enum.IsDefined(ComponentPolicy))
        {
            throw new ArgumentException(
                "Bone mappings contain an unsupported mapping, transfer, or component policy.",
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

        if (!double.IsFinite(Confidence) ||
            Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Morph binding confidence must be between zero and one.");
        }

        if (IsLocked && !IsReviewed)
        {
            throw new ArgumentException(
                "A morph binding cannot be locked until it has been reviewed.",
                parameterName);
        }
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

        if (FacialSourceAssetId.HasValue !=
            FacialSourceValueUnit.HasValue)
        {
            throw new ArgumentException(
                $"Animation '{Name}' must store both its facial FBX asset and explicit source-value unit.",
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
             targetKind != ProjectAssetKind.RetailGameResource))
        {
            throw new ArgumentException(
                $"Animation '{Name}' refers to an unknown retail target asset.",
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
    public const int CurrentSchemaVersion = 1;

    public const string FormatIdentifier = "dl-reanimated-csharp-project";

    public const string DyingLight1Game = "dying-light-1";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Format { get; init; } = FormatIdentifier;

    public Guid ProjectId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Untitled";

    public string Game { get; init; } = DyingLight1Game;

    public ImmutableArray<ProjectAssetReference> Assets { get; init; } = [];

    public ImmutableArray<ProjectAnimation> Animations { get; init; } = [];

    public Guid? ActiveAnimationId { get; init; }

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
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ProjectFormatException(
                $"Only fresh schema-{CurrentSchemaVersion} projects are supported.");
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

        if (Assets.IsDefault || Animations.IsDefault)
        {
            throw new ProjectFormatException("Project collections must be initialized.");
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
            throw new ProjectFormatException("Project asset identifiers must be unique.");
        }

        foreach (ProjectAnimation animation in Animations)
        {
            animation.Validate(assetKinds, nameof(Animations));
        }

        if (Animations.Select(static animation => animation.Id).Distinct().Count() !=
            Animations.Length)
        {
            throw new ProjectFormatException("Project animation identifiers must be unique.");
        }

        if (ActiveAnimationId is { } activeAnimationId &&
            Animations.All(animation => animation.Id != activeAnimationId))
        {
            throw new ProjectFormatException(
                "The active animation identifier does not exist in the project animation library.");
        }
    }
}
