using System.Collections.Immutable;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public enum CustomModelRigMode
{
    Auto,
    StaticProp,
    ExactFbxRig,
    Dl1HumanoidFit,
}

public enum CustomModelTextureSemantic
{
    BaseColor,
    Normal,
    Specular,
    Mask,
}

public enum CustomModelTextureSourceKind
{
    EmbeddedFbx,
    ExternalFbx,
    UserOverride,
    ExistingDl1Material,
}

/// <summary>
/// Declares how authored texture samples are interpreted before DL1 output.
/// Color textures are sRGB while data textures, including normals, remain
/// linear. The declaration is stored in the portable model package so build
/// results never depend on an editor-global texture setting.
/// </summary>
public enum CustomModelTextureColorSpace
{
    Auto,
    Srgb,
    Linear,
}

/// <summary>
/// Declares the source channel convention of a normal-map binding. DL1's
/// standard material samples tangent X from alpha and tangent Y from green;
/// ordinary RGB normal maps must therefore be repacked before compilation.
/// </summary>
public enum CustomModelNormalMapConvention
{
    RgbOpenGl,
    RgbDirectX,
    Dl1AlphaGreen,
}

/// <summary>
/// Identifies an editor-authored, unweighted child retained separately from
/// the immutable hierarchy decoded from the embedded FBX snapshot.
/// </summary>
public enum CustomModelAuthoredHelperKind
{
    Helper,
    Camera,
    Prop,
}

public enum CustomModelImportSeverity
{
    Information,
    Warning,
    Error,
    Blocker,
}

public enum CustomModelBuildState
{
    NotBuilt,
    AuthoringDraft,
    CompilerReady,
    CompilerValidated,
    // Retained so existing schema-1 packages can still be read. New builds do
    // not claim game readiness without a separate installed-editor/game gate.
    GameReady,
    Blocked,
}

public sealed record CustomModelImportDiagnostic
{
    public string Code { get; init; } = string.Empty;

    public CustomModelImportSeverity Severity { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? Subject { get; init; }

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Code, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Message, parameterName);
        if (!Enum.IsDefined(Severity))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Unsupported model diagnostic severity.");
        }
    }
}

public sealed record CustomModelSourceIdentity
{
    public string OriginalFileName { get; init; } = string.Empty;

    public string ContentSha256 { get; init; } = string.Empty;

    public int FbxVersion { get; init; }

    public string EmbeddedEntryPath { get; init; } = CustomModelPackage.SourceFbxEntryPath;

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OriginalFileName, parameterName);
        if (!string.Equals(Path.GetExtension(OriginalFileName), ".fbx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Custom models must retain a binary FBX source snapshot.", parameterName);
        }

        ProjectAssetReference.ValidateSha256(ContentSha256, parameterName);
        if (FbxVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "FBX versions must be positive.");
        }

        ValidatePackageEntryPath(EmbeddedEntryPath, parameterName);
    }

    internal static void ValidatePackageEntryPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) ||
            normalized.StartsWith('/') ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or ".."))
        {
            throw new ArgumentException("Model-package paths must be portable relative paths.", parameterName);
        }
    }
}

public sealed record CustomModelAxisSystem
{
    public int UpAxis { get; init; } = 1;

    public int UpAxisSign { get; init; } = 1;

    public int FrontAxis { get; init; } = 2;

    public int FrontAxisSign { get; init; } = -1;

    public int CoordinateAxis { get; init; }

    public int CoordinateAxisSign { get; init; } = 1;

    public double UnitScaleFactor { get; init; } = 1.0;

    internal void Validate(string parameterName)
    {
        int[] axes = [UpAxis, FrontAxis, CoordinateAxis];
        if (axes.Any(static axis => axis is < 0 or > 2) || axes.Distinct().Count() != 3)
        {
            throw new ArgumentException("FBX axis declarations must name three distinct axes.", parameterName);
        }

        if (UpAxisSign is not (-1 or 1) ||
            FrontAxisSign is not (-1 or 1) ||
            CoordinateAxisSign is not (-1 or 1) ||
            !double.IsFinite(UnitScaleFactor) ||
            UnitScaleFactor <= 0.0)
        {
            throw new ArgumentException("FBX axis signs and unit scale are invalid.", parameterName);
        }
    }
}

public sealed record CustomModelBone
{
    public int Index { get; init; }

    public long FbxObjectId { get; init; }

    public string Name { get; init; } = string.Empty;

    public int ParentIndex { get; init; } = -1;

    public TransformTRS LocalBindTransform { get; init; } = TransformTRS.Identity;

    /// <summary>
    /// Exact authored local bind matrix after FBX axis/unit normalization.
    /// DL1 model output retains this matrix even when inherited non-uniform
    /// scaling introduces shear that cannot be represented by editor TRS.
    /// </summary>
    public TransformMatrix ExactLocalBindMatrix { get; init; } = TransformMatrix.Identity;

    public BoneKind Kind { get; init; } = BoneKind.Deform;

    public bool IsWeighted { get; init; }

    internal void Validate(int expectedIndex, string parameterName)
    {
        if (Index != expectedIndex || ParentIndex >= Index || ParentIndex < -1)
        {
            throw new ArgumentException("Custom-model bones must use a topological contiguous order.", parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        if (!LocalBindTransform.IsFinite ||
            Math.Abs(LocalBindTransform.Scale.X) <= 1e-12 ||
            Math.Abs(LocalBindTransform.Scale.Y) <= 1e-12 ||
            Math.Abs(LocalBindTransform.Scale.Z) <= 1e-12 ||
            !ExactLocalBindMatrix.IsFinite ||
            Math.Abs(ExactLocalBindMatrix.M41) > 1e-9 ||
            Math.Abs(ExactLocalBindMatrix.M42) > 1e-9 ||
            Math.Abs(ExactLocalBindMatrix.M43) > 1e-9 ||
            Math.Abs(ExactLocalBindMatrix.M44 - 1.0) > 1e-9 ||
            Math.Abs(ExactLocalBindMatrix.LinearDeterminant) <= 1e-12)
        {
            throw new ArgumentException($"Bone '{Name}' has an invalid exact or preview bind transform.", parameterName);
        }
    }
}

public sealed record CustomModelAuthoredHelper
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Parent row in the effective hierarchy: imported FBX bones first,
    /// followed by earlier authored helpers in manifest order.
    /// </summary>
    public int ParentNodeIndex { get; init; }

    public TransformTRS LocalTransform { get; init; } = TransformTRS.Identity;

    public TransformMatrix ExactLocalMatrix { get; init; } = TransformMatrix.Identity;

    public CustomModelAuthoredHelperKind Kind { get; init; }

    internal void Validate(int effectiveIndex, string parameterName)
    {
        if (Id == Guid.Empty || ParentNodeIndex < 0 || ParentNodeIndex >= effectiveIndex)
        {
            throw new ArgumentException(
                "Authored helpers require a non-empty identifier and an earlier parent row.",
                parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        if (!Enum.IsDefined(Kind) ||
            !LocalTransform.IsFinite ||
            Math.Abs(LocalTransform.Scale.X) <= 1e-12 ||
            Math.Abs(LocalTransform.Scale.Y) <= 1e-12 ||
            Math.Abs(LocalTransform.Scale.Z) <= 1e-12 ||
            !ExactLocalMatrix.IsFinite ||
            Math.Abs(ExactLocalMatrix.M41) > 1e-9 ||
            Math.Abs(ExactLocalMatrix.M42) > 1e-9 ||
            Math.Abs(ExactLocalMatrix.M43) > 1e-9 ||
            Math.Abs(ExactLocalMatrix.M44 - 1.0) > 1e-9 ||
            Math.Abs(ExactLocalMatrix.LinearDeterminant) <= 1e-12)
        {
            throw new ArgumentException(
                $"Authored helper '{Name}' has an invalid kind or local transform.",
                parameterName);
        }
    }

    internal BoneKind ToBoneKind() => Kind switch
    {
        CustomModelAuthoredHelperKind.Helper => BoneKind.Helper,
        CustomModelAuthoredHelperKind.Camera => BoneKind.Camera,
        CustomModelAuthoredHelperKind.Prop => BoneKind.Prop,
        _ => throw new InvalidOperationException("The authored helper kind is unsupported."),
    };
}

public sealed record CustomModelCameraMetadata
{
    /// <summary>
    /// Stable hierarchy name used by editor camera preview. This may point to
    /// any imported or authored node. Game-ready FPP output is a separate,
    /// exact EyeCamera contract.
    /// </summary>
    public string? ActivePreviewNodeName { get; init; }

    internal void Validate(
        IReadOnlyCollection<string> effectiveNodeNames,
        string parameterName)
    {
        if (ActivePreviewNodeName is null)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ActivePreviewNodeName, parameterName);
        if (!effectiveNodeNames.Contains(ActivePreviewNodeName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Preview camera node '{ActivePreviewNodeName}' is absent from the effective hierarchy.",
                parameterName);
        }
    }
}

public sealed record CustomModelMorphChannel
{
    public int Index { get; init; }

    public long BlendShapeChannelObjectId { get; init; }

    public long ShapeObjectId { get; init; }

    public string Name { get; init; } = string.Empty;

    public uint DescriptorHash { get; init; }

    public ImmutableArray<long> GeometryObjectIds { get; init; } = [];

    internal void Validate(int expectedIndex, string parameterName)
    {
        if (Index != expectedIndex ||
            BlendShapeChannelObjectId == 0 ||
            ShapeObjectId == 0 ||
            GeometryObjectIds.IsDefaultOrEmpty ||
            GeometryObjectIds.Any(static id => id == 0))
        {
            throw new ArgumentException(
                "Custom-model morph channels require contiguous indexes and concrete FBX object identities.",
                parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
    }
}

public sealed record CustomModelMeshPart
{
    public string Name { get; init; } = string.Empty;

    public long GeometryObjectId { get; init; }

    public long ModelObjectId { get; init; }

    public int ControlPointCount { get; init; }

    public int PolygonCount { get; init; }

    public int TriangleCount { get; init; }

    public int ExpandedVertexCount { get; init; }

    public int MaterialSlotCount { get; init; }

    public int MaximumSourceInfluences { get; init; }

    public int MaximumRetainedInfluences { get; init; }

    public double MaximumDiscardedWeight { get; init; }

    public int RequiredPaletteSize { get; init; }

    public ImmutableArray<int> SourceMaterialIndices { get; init; } = [];

    internal void Validate(string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        int[] counts =
        [
            ControlPointCount,
            PolygonCount,
            TriangleCount,
            ExpandedVertexCount,
            MaterialSlotCount,
            MaximumSourceInfluences,
            MaximumRetainedInfluences,
            RequiredPaletteSize,
        ];
        if (counts.Any(static count => count < 0) ||
            !double.IsFinite(MaximumDiscardedWeight) ||
            MaximumDiscardedWeight < 0.0 ||
            SourceMaterialIndices.IsDefault ||
            SourceMaterialIndices.Any(static index => index < 0))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Mesh '{Name}' contains invalid counts or source material indices.");
        }
    }
}

public sealed record CustomModelTextureBinding
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public CustomModelTextureSemantic Semantic { get; init; }

    public CustomModelTextureSourceKind SourceKind { get; init; }

    public CustomModelTextureColorSpace ColorSpace { get; init; } =
        CustomModelTextureColorSpace.Auto;

    /// <summary>
    /// Source-channel convention used when <see cref="Semantic"/> is
    /// <see cref="CustomModelTextureSemantic.Normal"/>. Other semantics
    /// retain the default value and do not interpret it.
    /// </summary>
    public CustomModelNormalMapConvention NormalMapConvention { get; init; } =
        CustomModelNormalMapConvention.RgbOpenGl;

    public string DisplayName { get; init; } = string.Empty;

    public string? PackageEntryPath { get; init; }

    public string? OriginalReference { get; init; }

    public string ContentSha256 { get; init; } = string.Empty;

    public string MediaType { get; init; } = "application/octet-stream";

    internal void Validate(string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("Texture identifiers cannot be empty.", parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(MediaType, parameterName);
        if (!Enum.IsDefined(Semantic) ||
            !Enum.IsDefined(SourceKind) ||
            !Enum.IsDefined(ColorSpace) ||
            !Enum.IsDefined(NormalMapConvention))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Texture bindings contain an unsupported semantic, source, color-space, or normal-map convention.");
        }

        if (Semantic == CustomModelTextureSemantic.BaseColor &&
            ColorSpace == CustomModelTextureColorSpace.Linear)
        {
            throw new ArgumentException("Base-color textures must be authored as sRGB.", parameterName);
        }

        if (Semantic != CustomModelTextureSemantic.BaseColor &&
            ColorSpace == CustomModelTextureColorSpace.Srgb)
        {
            throw new ArgumentException("Normal, specular, and mask textures must be authored as linear data.", parameterName);
        }

        if (Semantic != CustomModelTextureSemantic.Normal &&
            NormalMapConvention != CustomModelNormalMapConvention.RgbOpenGl)
        {
            throw new ArgumentException(
                "A normal-map channel convention can only be assigned to normal textures.",
                parameterName);
        }

        ProjectAssetReference.ValidateSha256(ContentSha256, parameterName);
        if (PackageEntryPath is { } entryPath)
        {
            CustomModelSourceIdentity.ValidatePackageEntryPath(entryPath, parameterName);
        }
    }
}

public sealed record CustomModelMaterial
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string? ExistingDl1MaterialReference { get; init; }

    public ImmutableArray<CustomModelTextureBinding> Textures { get; init; } = [];

    internal void Validate(string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("Material identifiers cannot be empty.", parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name, parameterName);
        foreach (CustomModelTextureBinding texture in Textures)
        {
            texture.Validate(parameterName);
        }

        if (Textures.GroupBy(static texture => texture.Semantic).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException($"Material '{Name}' contains duplicate texture semantics.", parameterName);
        }
    }
}

public sealed record CustomModelAnimationClip
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public long FbxObjectId { get; init; }

    public string SourceName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public bool Included { get; init; } = true;

    public FrameRate FrameRate { get; init; } = new(30, 1);

    public long StartFrame { get; init; }

    public long FrameCount { get; init; }

    public Dl1RootMotionMode RootMotionMode { get; init; } = Dl1RootMotionMode.Recorded;

    public string? RootBoneName { get; init; }

    public string SourceFingerprint { get; init; } = string.Empty;

    public bool HasSkeletalTracks { get; init; }

    public bool HasMorphTracks { get; init; }

    public string FacialSourceValueUnit { get; init; } = "percent";

    internal void Validate(string parameterName)
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("Animation-stack identifiers cannot be empty.", parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SourceName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(DisplayName, parameterName);
        if (StartFrame < 0 || FrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Animation stack '{DisplayName}' has an invalid range.");
        }

        ProjectAssetReference.ValidateSha256(SourceFingerprint, parameterName);
        if (FacialSourceValueUnit is not ("percent" or "normalized"))
        {
            throw new ArgumentException(
                "Custom-model facial source values must declare percent or normalized units.",
                parameterName);
        }
        if (RootBoneName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(RootBoneName, parameterName);
        }
    }
}

public sealed record CustomModelBuildReceipt
{
    public string InputFingerprint { get; init; } = string.Empty;

    public string ToolFingerprint { get; init; } = string.Empty;

    public string? CompilerFingerprint { get; init; }

    public string OutputManifestFingerprint { get; init; } = string.Empty;

    public CustomModelBuildState State { get; init; }

    public DateTimeOffset CompletedUtc { get; init; }

    public ImmutableArray<string> BlockingReasons { get; init; } = [];

    internal void Validate(string parameterName)
    {
        ProjectAssetReference.ValidateSha256(InputFingerprint, parameterName);
        ProjectAssetReference.ValidateSha256(ToolFingerprint, parameterName);
        ProjectAssetReference.ValidateSha256(OutputManifestFingerprint, parameterName);
        if (CompilerFingerprint is { } compilerFingerprint)
        {
            ProjectAssetReference.ValidateSha256(compilerFingerprint, parameterName);
        }
    }
}

public sealed record CustomModelBuildSettings
{
    /// <summary>
    /// Canonical Developer Tools character-directory identity. An empty value
    /// means derive it from <see cref="ResourceName"/> for schema-1 packages
    /// written before this field was introduced.
    /// </summary>
    public string CharacterId { get; init; } = string.Empty;

    public string ResourceName { get; init; } = "custom_model";

    public string SurfaceName { get; init; } = "default";

    /// <summary>
    /// Extensionless type-322 AnimationScr resource identity. Loose ASCR
    /// output references the corresponding virtual <c>{identity}.scr</c> file.
    /// </summary>
    public string? AnimationScriptAlias { get; init; }

    /// <summary>Use an existing game animation bank without authoring a local replacement.</summary>
    public bool ReferenceExistingAnimationLibrary { get; init; }

    /// <summary>
    /// Converts the FBX lower-left texture-coordinate origin to the
    /// Direct3D/DL1 upper-left origin at preview and source-MSH boundaries.
    /// Source FBX coordinates remain byte-for-byte unchanged in the package.
    /// </summary>
    public bool FlipTextureCoordinateV { get; init; } = true;

    internal void Validate(string parameterName)
    {
        if (!string.IsNullOrEmpty(CharacterId))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(CharacterId, parameterName);
            if (CharacterId is "." or ".." ||
                CharacterId.IndexOfAny(['/', '\\', ':']) >= 0 ||
                Path.IsPathRooted(CharacterId))
            {
                throw new ArgumentException(
                    "Custom-model character IDs must be one safe directory component.",
                    parameterName);
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(ResourceName, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(SurfaceName, parameterName);
        if (Encoding.UTF8.GetByteCount(ResourceName) > 55 ||
            Encoding.UTF8.GetByteCount(SurfaceName) > 63)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Custom-model resource and surface names exceed DL1 source-format limits.");
        }

        if (AnimationScriptAlias is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(AnimationScriptAlias, parameterName);
        }
        if (ReferenceExistingAnimationLibrary && string.IsNullOrWhiteSpace(AnimationScriptAlias))
            throw new ArgumentException("An existing animation-bank reference requires its alias.", parameterName);
    }
}

/// <summary>
/// Portable metadata stored inside a .dlrmodel container. The package
/// embeds only user-owned source FBX and explicitly supplied texture bytes.
/// Retail DL1 resources remain fingerprint references.
/// </summary>
public sealed record CustomModelDocument
{
    public const int CurrentSchemaVersion = 5;

    public const string EmptyMorphSignature =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public const string CurrentFormat = "dl-reanimated-csharp-model";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Format { get; init; } = CurrentFormat;

    public Guid ModelId { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = "Untitled model";

    public CustomModelRigMode RigMode { get; init; } = CustomModelRigMode.Auto;

    public CustomModelSourceIdentity Source { get; init; } = new();

    public CustomModelAxisSystem AxisSystem { get; init; } = new();

    public string RigSignature { get; init; } = string.Empty;

    public string MorphSignature { get; init; } = EmptyMorphSignature;

    /// <summary>
    /// Records an approved recovery import where the source FBX blend shapes
    /// were deliberately omitted because they cannot be represented by DL1.
    /// </summary>
    public bool IgnoreMorphChannels { get; init; }

    public ImmutableArray<CustomModelBone> Bones { get; init; } = [];

    public ImmutableArray<CustomModelAuthoredHelper> AuthoredHelpers { get; init; } = [];

    /// <summary>
    /// The authored DL1 rig-conformance settings, when this model was converted
    /// to a Dying Light skeleton. Null for a model that keeps its imported rig.
    /// Only the decisions are stored; the conformed bone table is re-derived
    /// from the retained source FBX.
    /// </summary>
    public CustomModelRigConformance? RigConformance { get; init; }

    public CustomModelCameraMetadata Camera { get; init; } = new();

    public ImmutableArray<CustomModelMorphChannel> MorphChannels { get; init; } = [];

    public FacialPresetLibrary FacialPresets { get; init; } = new();

    public SecondaryMotionDefinition SecondaryMotion { get; init; } = new();

    public ImmutableArray<CustomModelMeshPart> Meshes { get; init; } = [];

    public ImmutableArray<CustomModelMaterial> Materials { get; init; } = [];

    public ImmutableArray<CustomModelAnimationClip> AnimationClips { get; init; } = [];

    public ImmutableArray<CustomModelImportDiagnostic> Diagnostics { get; init; } = [];

    public CustomModelBuildSettings BuildSettings { get; init; } = new();

    public CustomModelBuildReceipt? LastBuildReceipt { get; init; }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || !string.Equals(Format, CurrentFormat, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Only DL ReAnimated C# custom-model schema {CurrentSchemaVersion} is supported.");
        }

        if (ModelId == Guid.Empty)
        {
            throw new ArgumentException("Custom-model identifiers cannot be empty.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        Source.Validate(nameof(Source));
        AxisSystem.Validate(nameof(AxisSystem));
        ProjectAssetReference.ValidateSha256(RigSignature, nameof(RigSignature));
        ProjectAssetReference.ValidateSha256(MorphSignature, nameof(MorphSignature));
        if (Camera is null)
        {
            throw new ArgumentException(
                "Custom-model camera metadata must be initialized.",
                nameof(Camera));
        }
        if (Bones.IsDefault ||
            AuthoredHelpers.IsDefault ||
            MorphChannels.IsDefault ||
            Meshes.IsDefault ||
            Materials.IsDefault ||
            AnimationClips.IsDefault ||
            Diagnostics.IsDefault)
        {
            throw new ArgumentException("Custom-model collections must be initialized.");
        }

        if (RigMode != CustomModelRigMode.StaticProp && Bones.IsEmpty)
        {
            throw new ArgumentException("Rigged custom models must contain at least one bone.");
        }

        for (int index = 0; index < Bones.Length; index++)
        {
            Bones[index].Validate(index, nameof(Bones));
        }

        // FBX can legally contain case-distinct imported node names. Preserve
        // them here so DL1-output preparation can surface its stricter
        // normalized-name collision as a recoverable preview/build diagnostic.
        var effectiveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (CustomModelBone bone in Bones)
        {
            if (!effectiveNames.Add(bone.Name))
            {
                throw new ArgumentException(
                    $"Custom-model hierarchy name '{bone.Name}' is duplicated.",
                    nameof(Bones));
            }
        }

        var helperIds = new HashSet<Guid>();
        for (int helperIndex = 0; helperIndex < AuthoredHelpers.Length; helperIndex++)
        {
            CustomModelAuthoredHelper helper = AuthoredHelpers[helperIndex];
            helper.Validate(Bones.Length + helperIndex, nameof(AuthoredHelpers));
            if (!helperIds.Add(helper.Id) || effectiveNames.Any(name =>
                    string.Equals(name, helper.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"Authored helper '{helper.Name}' has a duplicate identifier or hierarchy name.",
                    nameof(AuthoredHelpers));
            }

            effectiveNames.Add(helper.Name);
        }

        RigConformance?.Validate(nameof(RigConformance));
        Camera.Validate(effectiveNames, nameof(Camera));
        ArgumentNullException.ThrowIfNull(SecondaryMotion);
        SecondaryMotion.Validate(effectiveNames);
        HashSet<string> secondaryOutputs = SecondaryMotion.Groups.SelectMany(static group => group.Particles)
            .Where(static particle => !particle.Fixed && particle.DrivenBoneName is not null)
            .Select(static particle => particle.DrivenBoneName!).ToHashSet(StringComparer.Ordinal);
        if (Bones.Any(bone => secondaryOutputs.Contains(bone.Name) && bone.Kind is BoneKind.Root or BoneKind.Camera or BoneKind.Prop) ||
            AuthoredHelpers.Any(helper => secondaryOutputs.Contains(helper.Name) && helper.Kind is CustomModelAuthoredHelperKind.Camera or CustomModelAuthoredHelperKind.Prop))
            throw new ArgumentException("Secondary motion cannot drive root, camera or prop helper entities.", nameof(SecondaryMotion));
        ArgumentNullException.ThrowIfNull(FacialPresets);
        FacialPresets.Validate(MorphChannels.Select(static morph => morph.Name));

        var morphNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var morphDescriptors = new Dictionary<uint, string>();
        for (int morphIndex = 0; morphIndex < MorphChannels.Length; morphIndex++)
        {
            CustomModelMorphChannel morph = MorphChannels[morphIndex];
            morph.Validate(morphIndex, nameof(MorphChannels));
            if (!morphNames.Add(morph.Name))
            {
                throw new ArgumentException(
                    $"Custom-model morph name '{morph.Name}' is duplicated.",
                    nameof(MorphChannels));
            }

            if (morphDescriptors.TryGetValue(morph.DescriptorHash, out string? existing))
            {
                throw new ArgumentException(
                    $"Custom-model morphs '{existing}' and '{morph.Name}' collide at descriptor 0x{morph.DescriptorHash:X8}.",
                    nameof(MorphChannels));
            }

            morphDescriptors.Add(morph.DescriptorHash, morph.Name);
        }

        if (MorphChannels.IsEmpty != string.Equals(
                MorphSignature,
                EmptyMorphSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The morph signature must be empty exactly when the model has no morph inventory.",
                nameof(MorphSignature));
        }

        foreach (CustomModelMeshPart mesh in Meshes)
        {
            mesh.Validate(nameof(Meshes));
        }

        foreach (CustomModelMaterial material in Materials)
        {
            material.Validate(nameof(Materials));
        }

        foreach (CustomModelAnimationClip stack in AnimationClips)
        {
            stack.Validate(nameof(AnimationClips));
        }

        foreach (CustomModelImportDiagnostic diagnostic in Diagnostics)
        {
            diagnostic.Validate(nameof(Diagnostics));
        }

        BuildSettings.Validate(nameof(BuildSettings));

        LastBuildReceipt?.Validate(nameof(LastBuildReceipt));
    }

    /// <summary>
    /// Creates the editor/runtime view of the imported hierarchy. Descriptor
    /// hashes are retained even when the source FBX contains a collision so
    /// Source FBX preview can remain visible and the stricter DL1 preparation
    /// boundary can report a recoverable diagnostic.
    /// </summary>
    public RigDefinition CreateRigDefinition() =>
        CreateRigDefinition(requireUniqueDl1Descriptors: false);

    /// <summary>
    /// Creates a rig admitted for descriptor-addressed DL1 animation output.
    /// Unlike the source-preview rig, this rejects collisions across bones,
    /// authored helpers, and morph channels before direct ANM2 evaluation.
    /// </summary>
    public RigDefinition CreateDl1AnimationRigDefinition() =>
        CreateRigDefinition(requireUniqueDl1Descriptors: true);

    private RigDefinition CreateRigDefinition(bool requireUniqueDl1Descriptors)
    {
        Validate();
        if (Bones.IsEmpty)
        {
            throw new InvalidOperationException("A static custom model has no animation rig.");
        }

        ImmutableArray<CustomModelBone> effectiveBones = CreateEffectiveBones();
        var descriptorOwners = new Dictionary<uint, string>();
        ImmutableArray<(CustomModelBone Bone, uint Descriptor)> descriptors =
            effectiveBones.Select(bone =>
            {
                uint descriptor = ComputeDl1NameHash(bone.Name);
                if (requireUniqueDl1Descriptors &&
                    descriptorOwners.TryGetValue(
                        descriptor,
                        out string? existing))
                {
                    throw new InvalidDataException(
                        $"Custom-model rig entities '{existing}' and '{bone.Name}' collide at DL1 descriptor 0x{descriptor:X8}.");
                }

                descriptorOwners.TryAdd(descriptor, bone.Name);
                return (bone, descriptor);
            }).ToImmutableArray();
        if (requireUniqueDl1Descriptors)
        {
            foreach (CustomModelMorphChannel morph in MorphChannels)
            {
                if (descriptorOwners.TryGetValue(
                        morph.DescriptorHash,
                        out string? existing))
                {
                    throw new InvalidDataException(
                        $"Custom-model rig entities '{existing}' and '{morph.Name}' collide at DL1 descriptor 0x{morph.DescriptorHash:X8}.");
                }

                descriptorOwners.Add(morph.DescriptorHash, morph.Name);
            }
        }

        return new RigDefinition(
            $"custom:{ModelId:N}",
            Name,
            descriptors.Select(static row => new BoneDefinition(
                row.Bone.Index,
                row.Bone.Name,
                row.Bone.ParentIndex,
                row.Bone.LocalBindTransform,
                row.Bone.Kind,
                requiredForExport: true,
                descriptorHash: row.Descriptor)),
            MorphChannels.Select(static morph => new MorphChannelDefinition(
                morph.Index,
                morph.Name,
                morph.DescriptorHash,
                morph.Name)),
            sourceAssetFingerprint: new SourceAssetFingerprint(
                Source.EmbeddedEntryPath,
                Source.ContentSha256,
                ModelId.ToString("N")));
    }

    private static uint ComputeDl1NameHash(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        uint value = 0;
        foreach (char character in name)
        {
            if (character > 0x7f)
            {
                throw new InvalidDataException(
                    $"Custom-model rig entity '{name}' cannot be hashed for DL1 because implicit descriptor names must contain only ASCII characters.");
            }

            char lower = char.ToLowerInvariant(character);
            value = unchecked((uint)(byte)lower + (41u * value));
        }

        return value;
    }

    public ImmutableArray<CustomModelBone> CreateEffectiveBones()
    {
        var result = Bones.ToBuilder();
        for (int helperIndex = 0; helperIndex < AuthoredHelpers.Length; helperIndex++)
        {
            CustomModelAuthoredHelper helper = AuthoredHelpers[helperIndex];
            result.Add(new CustomModelBone
            {
                Index = Bones.Length + helperIndex,
                FbxObjectId = 0,
                Name = helper.Name,
                ParentIndex = helper.ParentNodeIndex,
                LocalBindTransform = helper.LocalTransform,
                ExactLocalBindMatrix = helper.ExactLocalMatrix,
                Kind = helper.ToBoneKind(),
                IsWeighted = false,
            });
        }

        return result.ToImmutable();
    }

    public bool HasGameReadyEyeCamera => CreateEffectiveBones().Any(static bone =>
        bone.Kind == BoneKind.Camera &&
        !bone.IsWeighted &&
        string.Equals(bone.Name, "EyeCamera", StringComparison.Ordinal));
}

public sealed record CustomModelPackage(
    CustomModelDocument Document,
    ImmutableArray<byte> SourceFbx,
    ImmutableDictionary<string, ImmutableArray<byte>> TexturePayloads)
{
    public const string ManifestEntryPath = "model.json";

    public const string SourceFbxEntryPath = "source/model.fbx";
}
