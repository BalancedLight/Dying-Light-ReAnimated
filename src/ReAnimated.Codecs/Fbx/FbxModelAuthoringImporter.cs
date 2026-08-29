using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Project;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxModelAuthoringImportOptions
{
    public CustomModelRigMode RigMode { get; init; } = CustomModelRigMode.Auto;

    public int MaximumMeshes { get; init; } = 65_536;

    public int MaximumMaterials { get; init; } = 65_536;

    public long MaximumTextureBytes { get; init; } = 256L * 1024 * 1024;

    public string? ExternalTextureSearchRoot { get; init; }

    public int MaximumExpandedVertices { get; init; } = 20_000_000;

    public int MaximumMorphChannels { get; init; } = 4_096;

    public int MaximumMorphAffectedControlPoints { get; init; } = 20_000_000;

    public long MaximumDecodedMorphDeltaBytes { get; init; } = 256L * 1024 * 1024;

    public int MaximumAnimationFramesPerStack { get; init; } = 1_000_000;

    public int MaximumSampledTransformKeysPerStack { get; init; } = 8_000_000;

    public bool DecodeAnimationClips { get; init; } = true;

    /// <summary>
    /// Skips every blend shape while retaining the mesh, materials, and rig.
    /// This is an explicit recovery path for models whose morph data cannot be
    /// represented by DL1; callers must obtain user approval before enabling it.
    /// </summary>
    public bool IgnoreMorphChannels { get; init; }
}

public readonly record struct FbxModelVertex(
    Vector3D Position,
    Vector3D Normal,
    double TextureCoordinateU,
    double TextureCoordinateV,
    ImmutableArray<int> BoneIndices,
    ImmutableArray<double> BoneWeights);

public sealed record FbxModelSurface(
    string Id,
    string MeshName,
    Guid MaterialId,
    ImmutableArray<FbxModelVertex> Vertices,
    ImmutableArray<uint> Indices,
    ImmutableArray<int> PaletteBoneIndices,
    ImmutableArray<TransformMatrix> InverseBindMatrices,
    bool IsSkinned)
{
    public ImmutableArray<FbxModelMorphTarget> MorphTargets { get; init; } = [];
}

public sealed record FbxModelMorphTarget(
    string Name,
    uint DescriptorHash,
    long BlendShapeChannelObjectId,
    long ShapeObjectId,
    ImmutableArray<Vector3D> PositionDeltas);

public sealed record FbxModelAuthoringImportResult(
    CustomModelPackage Package,
    RigDefinition? Rig,
    ImmutableArray<FbxModelSurface> Surfaces,
    ImmutableDictionary<Guid, AnimationClip> AnimationClips,
    FbxStrictExportInspection Inspection);

[Flags]
public enum CustomModelReimportContractChange
{
    None = 0,
    Rig = 1 << 0,
    Morph = 1 << 1,
}

/// <summary>
/// Read-only validation result for a proposed FBX replacement. Callers keep
/// project variants intact and use the explicit change flags to mark only the
/// affected bone/helper or facial mappings stale before committing bytes.
/// </summary>
public sealed record CustomModelReimportPreview(
    FbxModelAuthoringImportResult Replacement,
    string ExistingSourceRigSignature,
    string ReplacementSourceRigSignature,
    string ExistingMorphSignature,
    string ReplacementMorphSignature,
    CustomModelReimportContractChange Changes)
{
    public bool CanPreserveVariantReviews =>
        Changes == CustomModelReimportContractChange.None;

    public bool BoneAndHelperMappingsBecomeStale =>
        Changes.HasFlag(CustomModelReimportContractChange.Rig);

    public bool FacialMappingsBecomeStale =>
        Changes.HasFlag(CustomModelReimportContractChange.Morph);
}

/// <summary>
/// Bounded binary-FBX model importer. It retains an immutable source snapshot,
/// resolves the exact authored hierarchy, reduces weights deterministically to
/// DL1's four-influence vertex contract, and partitions draws by material,
/// 256-entry skin palette, and 65,535 expanded vertices.
/// </summary>
public static class FbxModelAuthoringImporter
{
    private const int MaximumInfluencesPerVertex = 4;
    private const int MaximumPaletteEntries = 256;
    private const int MaximumVerticesPerDraw = 65_535;
    private const string SharedBaseColorAtlasDiagnosticCode =
        "model_shared_embedded_base_color_atlas_inferred";
    private const string ExternalTextureDiagnosticCode =
        "model_external_texture_not_embedded";

    public static async Task<FbxModelAuthoringImportResult> ImportFileAsync(
        string path,
        FbxModelAuthoringImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".fbx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Models workspace first release accepts binary .fbx files only.");
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        FbxModelAuthoringImportOptions fileOptions =
            (options ?? new FbxModelAuthoringImportOptions()) with
            {
                ExternalTextureSearchRoot = Path.GetDirectoryName(fullPath),
            };
        return Import(bytes, Path.GetFileName(fullPath), fileOptions, cancellationToken);
    }

    public static FbxModelAuthoringImportResult Import(
        ReadOnlySpan<byte> sourceFbx,
        string originalFileName,
        FbxModelAuthoringImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        options ??= new FbxModelAuthoringImportOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] sourceBytes = sourceFbx.ToArray();
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
        Guid modelId = CreateDeterministicGuid(SHA256.HashData(sourceBytes));
        FbxBinaryDocument binary = FbxBinaryReader.Read(sourceBytes, cancellationToken: cancellationToken);
        FbxSemanticScene scene = FbxSemanticScene.Parse(binary, cancellationToken);
        FbxStrictExportInspection inspection = FbxStrictExportInspector.Inspect(binary, cancellationToken);
        TransformMatrix basis = FbxCoreAnimationAdapter.BuildGlobalSettingsBasis(scene.GlobalSettings);
        TransformMatrix inverseBasis = basis.InvertedAffine();
        double metersPerUnit = scene.MetersPerUnit;
        ImmutableDictionary<long, FbxNode> objects = scene.ObjectNodes;
        ImmutableDictionary<long, TransformMatrix> rawBindPose = scene.ReadBindPoseGlobals(cancellationToken);
        ImmutableDictionary<long, TransformMatrix> rawEvaluatedGlobals =
            FbxTransformEvaluator.EvaluateModelGlobals(
                scene,
                tick: 0,
                bindings: null,
                useAnimation: false,
                cancellationToken: cancellationToken);

        HashSet<long> weightedBoneIds = ReadWeightedBoneIds(scene, objects, cancellationToken);
        CustomModelRigMode resolvedRigMode = ResolveRigMode(options.RigMode, weightedBoneIds, scene);
        (RigDefinition? rig, ImmutableArray<CustomModelBone> bones, ImmutableDictionary<long, int> boneIndexByModel) =
            BuildRig(
                modelId,
                originalFileName,
                sourceSha256,
                resolvedRigMode,
                scene,
                rawBindPose,
                rawEvaluatedGlobals,
                weightedBoneIds,
                metersPerUnit,
                basis,
                inverseBasis,
                cancellationToken);

        var diagnostics = ImmutableArray.CreateBuilder<CustomModelImportDiagnostic>();
        if (options.IgnoreMorphChannels)
        {
            diagnostics.Add(new CustomModelImportDiagnostic
            {
                Code = "model_morph_channels_skipped",
                Severity = CustomModelImportSeverity.Warning,
                Message = "All FBX blend shapes were skipped at the user's request. The mesh, materials, and rig were imported, but facial/morph animation is unavailable.",
            });
        }
        int projectedBindCount = bones.Count(static bone =>
            !bone.ExactLocalBindMatrix.NearlyEquals(bone.LocalBindTransform.ToMatrix(), 1e-7));
        if (projectedBindCount > 0)
        {
            diagnostics.Add(new CustomModelImportDiagnostic
            {
                Code = "model_affine_bind_preview_projection",
                Severity = CustomModelImportSeverity.Warning,
                Message = $"{projectedBindCount:N0} bone bind transforms contain authored affine shear. Exact matrices are retained for model output; animation preview uses an orthonormal TRS projection.",
            });
        }
        if (options.RigMode == CustomModelRigMode.Dl1HumanoidFit)
        {
            diagnostics.Add(new CustomModelImportDiagnostic
            {
                Code = "model_humanoid_fit_requires_review",
                Severity = CustomModelImportSeverity.Warning,
                Message = "DL1 Humanoid Fit retained the exact FBX hierarchy and requires explicit mapping review before game-ready export.",
            });
        }

        (ImmutableArray<CustomModelMaterial> materials,
         ImmutableDictionary<long, Guid> materialIds,
         ImmutableDictionary<string, ImmutableArray<byte>> texturePayloads) =
            ReadMaterials(
                scene,
                objects,
                originalFileName,
                options,
                diagnostics,
                cancellationToken);
        RefreshTextureBindingDiagnostics(
            materials,
            texturePayloads,
            diagnostics);

        ImmutableArray<CustomModelMeshPart> meshParts;
        ImmutableArray<FbxModelSurface> surfaces;
        ImmutableArray<CustomModelMorphChannel> morphChannels;
        (meshParts, surfaces, morphChannels) = ReadMeshes(
            scene,
            objects,
            inspection,
            rig,
            bones,
            boneIndexByModel,
            materialIds,
            materials,
            rawBindPose,
            rawEvaluatedGlobals,
            metersPerUnit,
            basis,
            options,
            diagnostics,
            cancellationToken);

        if (rig is not null)
        {
            var runtimeDocument = new CustomModelDocument
            {
                ModelId = modelId,
                Name = Path.GetFileNameWithoutExtension(originalFileName),
                RigMode = resolvedRigMode,
                Source = new CustomModelSourceIdentity
                {
                    OriginalFileName = Path.GetFileName(originalFileName),
                    ContentSha256 = sourceSha256,
                    FbxVersion = checked((int)binary.Version),
                },
                RigSignature = CustomModelContractSignatures.ComputeRig(bones),
                MorphSignature = ComputeMorphSignature(morphChannels, surfaces),
                Bones = bones,
                MorphChannels = morphChannels,
                Meshes = meshParts,
                Materials = materials,
            };
            rig = runtimeDocument.CreateRigDefinition();
        }

        (ImmutableArray<CustomModelAnimationClip> clipMetadata,
         ImmutableDictionary<Guid, AnimationClip> clips) = ReadAnimationClips(
            binary,
            scene,
            modelId,
            sourceSha256,
            rig,
            options,
            diagnostics,
            cancellationToken);

        string rigSignature = CustomModelContractSignatures.ComputeRig(bones);
        string morphSignature = ComputeMorphSignature(morphChannels, surfaces);
        var document = new CustomModelDocument
        {
            ModelId = modelId,
            Name = Path.GetFileNameWithoutExtension(originalFileName),
            RigMode = resolvedRigMode,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = Path.GetFileName(originalFileName),
                ContentSha256 = sourceSha256,
                FbxVersion = checked((int)binary.Version),
            },
            AxisSystem = ReadAxisSystem(scene),
            RigSignature = rigSignature,
            MorphSignature = morphSignature,
            IgnoreMorphChannels = options.IgnoreMorphChannels,
            Bones = bones,
            MorphChannels = morphChannels,
            Meshes = meshParts,
            Materials = materials,
            AnimationClips = clipMetadata,
            Diagnostics = diagnostics.ToImmutable(),
            BuildSettings = new CustomModelBuildSettings
            {
                ResourceName = Dl1SourceModelWriter.SanitizeName(
                    Path.GetFileNameWithoutExtension(originalFileName),
                    55),
            },
        };
        document.Validate();
        var package = new CustomModelPackage(
            document,
            ImmutableArray.Create(sourceBytes),
            texturePayloads);
        return new FbxModelAuthoringImportResult(package, rig, surfaces, clips, inspection);
    }

    /// <summary>
    /// Re-decodes the immutable FBX snapshot and reapplies only the authored
    /// model-package state. Geometry, hierarchy, binds, and animation samples
    /// always come from the embedded FBX bytes; editable material bindings and
    /// stack settings, authored helpers, and preview camera selection come from
    /// the validated schema-2 package manifest. Schema-1 packages are migrated
    /// in memory before reaching this boundary.
    /// </summary>
    public static FbxModelAuthoringImportResult ImportPackage(
        CustomModelPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Document.Validate();
        bool ignoreMorphChannels = package.Document.IgnoreMorphChannels ||
            package.Document.Diagnostics.Any(static diagnostic =>
                string.Equals(
                    diagnostic.Code,
                    "model_morph_channels_skipped",
                    StringComparison.Ordinal));
        FbxModelAuthoringImportResult decoded = Import(
            package.SourceFbx.AsSpan(),
            package.Document.Source.OriginalFileName,
            new FbxModelAuthoringImportOptions
            {
                RigMode = package.Document.RigMode,
                IgnoreMorphChannels = ignoreMorphChannels,
            },
            cancellationToken);

        Dictionary<string, CustomModelAnimationClip> savedAnimations = package.Document.AnimationClips
            .ToDictionary(static clip => clip.SourceFingerprint, StringComparer.Ordinal);
        ImmutableArray<CustomModelAnimationClip> animations = decoded.Package.Document.AnimationClips
            .Select(clip => savedAnimations.TryGetValue(clip.SourceFingerprint, out CustomModelAnimationClip? saved)
                ? clip with
                {
                    DisplayName = saved.DisplayName,
                    Included = saved.Included,
                    FrameRate = saved.FrameRate,
                    RootMotionMode = saved.RootMotionMode,
                    RootBoneName = saved.RootBoneName,
                }
                : clip)
            .ToImmutableArray();
        var savedMaterials = package.Document.Materials.ToBuilder();
        var normalizedDiagnostics = decoded.Package.Document.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Code != SharedBaseColorAtlasDiagnosticCode &&
                diagnostic.Code != ExternalTextureDiagnosticCode)
            .ToImmutableArray()
            .ToBuilder();
        if (package.Document.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == SharedBaseColorAtlasDiagnosticCode))
        {
            normalizedDiagnostics.AddRange(package.Document.Diagnostics.Where(static diagnostic =>
                diagnostic.Code == SharedBaseColorAtlasDiagnosticCode));
        }
        else
        {
            InferSingleEmbeddedBaseColorAtlas(savedMaterials, normalizedDiagnostics);
        }
        normalizedDiagnostics.AddRange(BuildTextureBindingDiagnostics(
            savedMaterials,
            package.TexturePayloads,
            package.Document.Diagnostics));
        CustomModelDocument document = decoded.Package.Document with
        {
            ModelId = package.Document.ModelId,
            Name = package.Document.Name,
            Materials = savedMaterials.ToImmutable(),
            AnimationClips = animations,
            Diagnostics = normalizedDiagnostics.ToImmutable(),
            BuildSettings = package.Document.BuildSettings,
            LastBuildReceipt = null,
        };
        document = ReapplyAuthoredHierarchyLayer(package.Document, document);
        bool buildReceiptCompatible = string.Equals(
                package.Document.RigSignature,
                document.RigSignature,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                package.Document.MorphSignature,
                document.MorphSignature,
                StringComparison.OrdinalIgnoreCase);
        document = document with
        {
            LastBuildReceipt = buildReceiptCompatible
                ? package.Document.LastBuildReceipt
                : null,
        };
        RigDefinition? reopenedRig = document.Bones.IsEmpty
            ? null
            : document.CreateRigDefinition();
        document.Validate();
        return decoded with
        {
            Package = new CustomModelPackage(document, package.SourceFbx, package.TexturePayloads),
            Rig = reopenedRig,
        };
    }

    public static CustomModelReimportPreview PreviewReimport(
        CustomModelPackage existing,
        ReadOnlySpan<byte> replacementFbx,
        string replacementFileName,
        FbxModelAuthoringImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existing);
        existing.Document.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementFileName);
        FbxModelAuthoringImportOptions effectiveOptions =
            (options ?? new FbxModelAuthoringImportOptions()) with
            {
                RigMode = existing.Document.RigMode,
                IgnoreMorphChannels = options?.IgnoreMorphChannels ??
                    existing.Document.IgnoreMorphChannels,
            };
        FbxModelAuthoringImportResult replacement = Import(
            replacementFbx,
            replacementFileName,
            effectiveOptions,
            cancellationToken);
        string existingSourceRigSignature =
            CustomModelContractSignatures.ComputeRig(existing.Document.Bones);
        string replacementSourceRigSignature =
            CustomModelContractSignatures.ComputeRig(
                replacement.Package.Document.Bones);
        CustomModelReimportContractChange changes =
            CustomModelReimportContractChange.None;
        if (!string.Equals(
                existingSourceRigSignature,
                replacementSourceRigSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            changes |= CustomModelReimportContractChange.Rig;
        }

        if (!string.Equals(
                existing.Document.MorphSignature,
                replacement.Package.Document.MorphSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            changes |= CustomModelReimportContractChange.Morph;
        }

        CustomModelDocument replacementDocument = ReapplyAuthoredHierarchyLayer(
            existing.Document,
            replacement.Package.Document with
            {
                ModelId = existing.Document.ModelId,
                Name = existing.Document.Name,
                BuildSettings = existing.Document.BuildSettings,
                LastBuildReceipt = null,
            });
        replacement = replacement with
        {
            Package = new CustomModelPackage(
                replacementDocument,
                replacement.Package.SourceFbx,
                replacement.Package.TexturePayloads),
            Rig = replacementDocument.Bones.IsEmpty
                ? null
                : replacementDocument.CreateRigDefinition(),
        };

        return new CustomModelReimportPreview(
            replacement,
            existingSourceRigSignature,
            replacementSourceRigSignature,
            existing.Document.MorphSignature,
            replacement.Package.Document.MorphSignature,
            changes);
    }

    /// <summary>
    /// Reparents the separate authored-helper layer by stable hierarchy name.
    /// Imported FBX row indexes are decode details and must never be persisted
    /// as the only reimport identity. A missing parent fails the validation
    /// preview before any package replacement can occur.
    /// </summary>
    internal static CustomModelDocument ReapplyAuthoredHierarchyLayer(
        CustomModelDocument existing,
        CustomModelDocument replacement)
    {
        ImmutableArray<CustomModelBone> existingEffective =
            existing.CreateEffectiveBones();
        var replacementIndexes = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (CustomModelBone bone in replacement.Bones)
        {
            replacementIndexes.Add(bone.Name, bone.Index);
        }

        var helpers = ImmutableArray.CreateBuilder<CustomModelAuthoredHelper>(
            existing.AuthoredHelpers.Length);
        foreach (CustomModelAuthoredHelper helper in existing.AuthoredHelpers)
        {
            if ((uint)helper.ParentNodeIndex >= (uint)existingEffective.Length)
            {
                throw new InvalidDataException(
                    $"Authored helper '{helper.Name}' has an invalid saved parent row.");
            }

            string parentName = existingEffective[helper.ParentNodeIndex].Name;
            if (!replacementIndexes.TryGetValue(parentName, out int replacementParentIndex))
            {
                throw new InvalidDataException(
                    $"Cannot reimport the model because authored helper '{helper.Name}' " +
                    $"was parented to '{parentName}', and that stable node name is absent " +
                    "from the replacement hierarchy.");
            }

            CustomModelAuthoredHelper remapped = helper with
            {
                ParentNodeIndex = replacementParentIndex,
            };
            helpers.Add(remapped);
            replacementIndexes.Add(
                remapped.Name,
                checked(replacement.Bones.Length + helpers.Count - 1));
        }

        string? previewNodeName = existing.Camera.ActivePreviewNodeName;
        if (previewNodeName is not null &&
            !replacementIndexes.ContainsKey(previewNodeName))
        {
            throw new InvalidDataException(
                $"Cannot reimport the model because preview camera node " +
                $"'{previewNodeName}' is absent from the replacement hierarchy.");
        }

        CustomModelDocument layered = replacement with
        {
            AuthoredHelpers = helpers.MoveToImmutable(),
            Camera = existing.Camera,
            LastBuildReceipt = null,
        };
        layered = layered with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(
                layered.CreateEffectiveBones()),
        };
        layered.Validate();
        return layered;
    }

    private static void ValidateOptions(FbxModelAuthoringImportOptions options)
    {
        if (!Enum.IsDefined(options.RigMode) ||
            options.MaximumMeshes <= 0 ||
            options.MaximumMaterials <= 0 ||
            options.MaximumExpandedVertices <= 0 ||
            options.MaximumMorphChannels <= 0 ||
            options.MaximumMorphAffectedControlPoints <= 0 ||
            options.MaximumDecodedMorphDeltaBytes <= 0 ||
            options.MaximumAnimationFramesPerStack <= 0 ||
            options.MaximumSampledTransformKeysPerStack <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FBX model-import limits and rig mode are invalid.");
        }
    }

    private static CustomModelRigMode ResolveRigMode(
        CustomModelRigMode requested,
        HashSet<long> weightedBoneIds,
        FbxSemanticScene scene)
    {
        if (requested != CustomModelRigMode.Auto)
        {
            return requested;
        }

        return weightedBoneIds.Count == 0 && !scene.Models.Values.Any(static model => model.IsLimb)
            ? CustomModelRigMode.StaticProp
            : CustomModelRigMode.ExactFbxRig;
    }

    private static (
        RigDefinition? Rig,
        ImmutableArray<CustomModelBone> Bones,
        ImmutableDictionary<long, int> BoneIndexByModel)
        BuildRig(
            Guid modelId,
            string originalFileName,
            string sourceSha256,
            CustomModelRigMode rigMode,
            FbxSemanticScene scene,
            ImmutableDictionary<long, TransformMatrix> rawBindPose,
            ImmutableDictionary<long, TransformMatrix> rawEvaluatedGlobals,
            HashSet<long> weightedBoneIds,
            double metersPerUnit,
            TransformMatrix basis,
            TransformMatrix inverseBasis,
            CancellationToken cancellationToken)
    {
        if (rigMode == CustomModelRigMode.StaticProp)
        {
            return (null, [], ImmutableDictionary<long, int>.Empty);
        }

        ImmutableArray<long> orderedBones =
            FbxCoreAnimationAdapter.BuildOrderedLimbModels(scene, cancellationToken);
        ImmutableDictionary<long, int> boneIndexByModel = orderedBones
            .Select((modelIdValue, index) => (modelIdValue, index))
            .ToImmutableDictionary(static pair => pair.modelIdValue, static pair => pair.index);
        ImmutableDictionary<long, long?> parentByModel = orderedBones.ToImmutableDictionary(
            static modelIdValue => modelIdValue,
            scene.GetNearestLimbParentId);
        ImmutableDictionary<long, TransformMatrix> rawGlobals = rawEvaluatedGlobals.SetItems(
            rawBindPose.Where(pair => boneIndexByModel.ContainsKey(pair.Key)));
        ImmutableDictionary<long, TransformMatrix> globals = FbxCoreAnimationAdapter.NormalizeGlobals(
            rawGlobals,
            orderedBones,
            metersPerUnit,
            basis,
            inverseBasis);

        var customBones = ImmutableArray.CreateBuilder<CustomModelBone>(orderedBones.Length);
        var domainBones = ImmutableArray.CreateBuilder<BoneDefinition>(orderedBones.Length);
        foreach (long modelObjectId in orderedBones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxModelObject model = scene.Models[modelObjectId];
            long? parentModelId = parentByModel[modelObjectId];
            int parentIndex = parentModelId.HasValue ? boneIndexByModel[parentModelId.Value] : -1;
            TransformMatrix local = parentModelId.HasValue
                ? FbxCoreAnimationAdapter.MakeRelative(
                    globals[parentModelId.Value],
                    globals[modelObjectId],
                    model.Name,
                    "custom-model bind")
                : globals[modelObjectId];
            TransformTRS localTrs;
            try
            {
                localTrs = local.Decompose();
            }
            catch (InvalidOperationException)
            {
                localTrs = FbxCoreAnimationAdapter.ProjectAffineToTrs(local, model.Name, "custom-model bind");
            }
            BoneKind kind = ClassifyBone(model.Name, parentIndex, weightedBoneIds.Contains(modelObjectId));
            int index = boneIndexByModel[modelObjectId];
            customBones.Add(new CustomModelBone
            {
                Index = index,
                FbxObjectId = modelObjectId,
                Name = model.Name,
                ParentIndex = parentIndex,
                LocalBindTransform = localTrs,
                ExactLocalBindMatrix = local,
                Kind = kind,
                IsWeighted = weightedBoneIds.Contains(modelObjectId),
            });
            domainBones.Add(new BoneDefinition(
                index,
                model.Name,
                parentIndex,
                localTrs,
                kind,
                requiredForExport: true));
        }

        var rig = new RigDefinition(
            $"custom:{modelId:N}",
            Path.GetFileNameWithoutExtension(originalFileName),
            domainBones.ToImmutable(),
            sourceAssetFingerprint: new SourceAssetFingerprint(
                CustomModelPackage.SourceFbxEntryPath,
                sourceSha256,
                modelId.ToString("N")));
        return (rig, customBones.ToImmutable(), boneIndexByModel);
    }

    private static BoneKind ClassifyBone(string name, int parentIndex, bool isWeighted)
    {
        if (string.Equals(name, Dl1PreviewContract.EyeCameraBoneName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, Dl1PreviewContract.ReferenceCameraBoneName, StringComparison.OrdinalIgnoreCase))
        {
            return BoneKind.Camera;
        }

        if (name.Contains("propholder", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("weapon", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("holder", StringComparison.OrdinalIgnoreCase))
        {
            return BoneKind.Prop;
        }

        if (parentIndex < 0)
        {
            return BoneKind.Root;
        }

        return isWeighted ? BoneKind.Deform : BoneKind.Helper;
    }

    private static (
        ImmutableArray<CustomModelMaterial> Materials,
        ImmutableDictionary<long, Guid> MaterialIds,
        ImmutableDictionary<string, ImmutableArray<byte>> TexturePayloads)
        ReadMaterials(
            FbxSemanticScene scene,
            ImmutableDictionary<long, FbxNode> objects,
            string originalFileName,
            FbxModelAuthoringImportOptions options,
            ImmutableArray<CustomModelImportDiagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        long[] materialObjectIds = objects
            .Where(static pair => IsObject(pair.Value, "Material"))
            .Select(static pair => pair.Key)
            .Order()
            .ToArray();
        if (materialObjectIds.Length > options.MaximumMaterials)
        {
            throw new InvalidDataException(
                $"FBX contains {materialObjectIds.Length:N0} materials; the configured limit is {options.MaximumMaterials:N0}.");
        }

        var materials = ImmutableArray.CreateBuilder<CustomModelMaterial>();
        var materialIds = ImmutableDictionary.CreateBuilder<long, Guid>();
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(
            StringComparer.Ordinal);
        foreach (long materialObjectId in materialObjectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxNode materialNode = objects[materialObjectId];
            string materialName = ReadObjectName(materialNode, $"Material {materialObjectId}");
            Guid materialId = CreateStableObjectGuid("material", materialObjectId, materialName);
            materialIds.Add(materialObjectId, materialId);

            var texturesBySemantic = new Dictionary<CustomModelTextureSemantic, CustomModelTextureBinding>();
            foreach (FbxConnection textureConnection in scene.GetChildren(materialObjectId)
                         .Where(connection =>
                             connection.Kind is "OO" or "OP" &&
                             objects.TryGetValue(connection.ChildId, out FbxNode? child) &&
                             IsObject(child, "Texture"))
                         .OrderBy(static connection => connection.ChildId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FbxNode textureNode = objects[textureConnection.ChildId];
                string textureName = ReadObjectName(textureNode, $"Texture {textureConnection.ChildId}");
                CustomModelTextureSemantic semantic = ClassifyTextureSemantic(
                    textureConnection.PropertyName,
                    textureName,
                    out string? inferredToken);
                if (inferredToken is not null)
                {
                    diagnostics.Add(new CustomModelImportDiagnostic
                    {
                        Code = "model_texture_semantic_inferred_from_name",
                        Severity = CustomModelImportSeverity.Information,
                        Subject = materialName,
                        Message = $"Texture '{textureName}' was classified as {semantic} from the whole token '{inferredToken}'.",
                    });
                }
                if (texturesBySemantic.ContainsKey(semantic))
                {
                    diagnostics.Add(new CustomModelImportDiagnostic
                    {
                        Code = "model_duplicate_texture_semantic",
                        Severity = CustomModelImportSeverity.Warning,
                        Subject = materialName,
                        Message = $"Material '{materialName}' has more than one {semantic} texture; the first deterministic connection is retained.",
                    });
                    continue;
                }

                (ImmutableArray<byte> content, string? originalReference) =
                    ReadTexturePayload(scene, objects, textureConnection.ChildId, textureNode);
                bool embeddedInFbx = !content.IsDefaultOrEmpty;
                ImmutableArray<string> probedPaths = [];
                string? resolvedPath = null;
                if (!embeddedInFbx &&
                    options.ExternalTextureSearchRoot is not null &&
                    originalReference is not null)
                {
                    (content, resolvedPath, probedPaths) = ResolveExternalTexture(
                        options.ExternalTextureSearchRoot,
                        originalFileName,
                        originalReference,
                        options.MaximumTextureBytes,
                        cancellationToken);
                    if (resolvedPath is not null)
                    {
                        diagnostics.Add(new CustomModelImportDiagnostic
                        {
                            Code = "model_external_texture_resolved",
                            Severity = CustomModelImportSeverity.Information,
                            Subject = materialName,
                            Message = $"Texture '{textureName}' was embedded from '{resolvedPath}'.",
                        });
                    }
                }

                bool embedded = !content.IsDefaultOrEmpty;
                string extension = DetectTextureExtension(content, originalReference);
                string mediaType = TextureMediaType(extension);
                string contentHash = embedded
                    ? Convert.ToHexString(SHA256.HashData(content.AsSpan())).ToLowerInvariant()
                    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        originalReference ?? $"fbx-texture:{textureConnection.ChildId}"))).ToLowerInvariant();
                string? packageEntryPath = embedded
                    ? $"textures/{contentHash}{extension}"
                    : null;
                if (packageEntryPath is not null)
                {
                    payloads[packageEntryPath] = content;
                }

                if (!embedded)
                {
                    diagnostics.Add(new CustomModelImportDiagnostic
                    {
                        Code = "model_external_texture_not_embedded",
                        Severity = CustomModelImportSeverity.Warning,
                        Subject = materialName,
                        Message = originalReference is null
                            ? $"Texture '{textureName}' has no embedded bytes or portable file reference; select a {semantic} texture in the Models workspace."
                            : $"Texture '{textureName}' references '{originalReference}' and was not embedded. " +
                              (probedPaths.IsEmpty
                                  ? "The reference was rooted, unsafe, or unsupported."
                                  : $"Probed: {string.Join("; ", probedPaths)}."),
                    });
                }

                texturesBySemantic.Add(semantic, new CustomModelTextureBinding
                {
                    Id = CreateStableObjectGuid("texture", textureConnection.ChildId, $"{materialObjectId}:{semantic}"),
                    Semantic = semantic,
                    SourceKind = embeddedInFbx
                        ? CustomModelTextureSourceKind.EmbeddedFbx
                        : CustomModelTextureSourceKind.ExternalFbx,
                    DisplayName = textureName,
                    PackageEntryPath = packageEntryPath,
                    OriginalReference = originalReference,
                    ContentSha256 = contentHash,
                    MediaType = mediaType,
                });
            }

            materials.Add(new CustomModelMaterial
            {
                Id = materialId,
                Name = materialName,
                Textures = texturesBySemantic
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair => pair.Value)
                    .ToImmutableArray(),
            });
        }

        InferSingleEmbeddedBaseColorAtlas(materials, diagnostics);

        if (materials.Count == 0)
        {
            Guid defaultId = CreateStableObjectGuid("material", 0, "Default material");
            materials.Add(new CustomModelMaterial
            {
                Id = defaultId,
                Name = "Default material",
            });
            diagnostics.Add(new CustomModelImportDiagnostic
            {
                Code = "model_default_material_created",
                Severity = CustomModelImportSeverity.Information,
                Message = "The FBX has no material objects; a deterministic default preview material was created.",
            });
        }

        return (materials.ToImmutable(), materialIds.ToImmutable(), payloads.ToImmutable());
    }

    /// <summary>
    /// Blender can serialize a shared image only on the first FBX material even
    /// though several material slots use that same UV atlas. We only repair the
    /// unambiguous form of that export: one distinct embedded base-color image
    /// in the entire FBX and otherwise completely untextured material slots.
    /// The inference is recorded so authoring never silently invents a binding.
    /// </summary>
    internal static void InferSingleEmbeddedBaseColorAtlas(
        ImmutableArray<CustomModelMaterial>.Builder materials,
        ImmutableArray<CustomModelImportDiagnostic>.Builder diagnostics)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CustomModelTextureBinding[] embeddedBaseColors = materials
            .SelectMany(static material => material.Textures)
            .Where(static texture =>
                texture.Semantic == CustomModelTextureSemantic.BaseColor &&
                texture.SourceKind == CustomModelTextureSourceKind.EmbeddedFbx &&
                texture.PackageEntryPath is not null)
            .ToArray();
        CustomModelTextureBinding[] distinctAtlases = embeddedBaseColors
            .DistinctBy(static texture => texture.ContentSha256, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctAtlases.Length != 1)
        {
            return;
        }

        CustomModelTextureBinding source = distinctAtlases[0];
        var inferredMaterialNames = ImmutableArray.CreateBuilder<string>();
        for (int index = 0; index < materials.Count; index++)
        {
            CustomModelMaterial material = materials[index];
            if (!material.Textures.IsDefaultOrEmpty)
            {
                continue;
            }

            CustomModelTextureBinding inferred = source with
            {
                Id = CreateStableObjectGuid(
                    "shared-base-color-atlas",
                    0,
                    $"{source.Id:N}:{material.Id:N}"),
            };
            materials[index] = material with
            {
                Textures = [inferred],
            };
            inferredMaterialNames.Add(material.Name);
        }

        if (inferredMaterialNames.Count > 0)
        {
            diagnostics.Add(new CustomModelImportDiagnostic
            {
                Code = SharedBaseColorAtlasDiagnosticCode,
                Severity = CustomModelImportSeverity.Information,
                Subject = string.Join(", ", inferredMaterialNames),
                Message =
                    $"The FBX embeds one base-color atlas and leaves {inferredMaterialNames.Count:N0} material slot(s) completely untextured. " +
                    $"ReAnimated reuses '{source.DisplayName}' for {string.Join(", ", inferredMaterialNames.Select(static name => $"'{name}'"))}; this inferred binding remains visible and editable in Materials.",
            });
        }
    }

    private static void RefreshTextureBindingDiagnostics(
        IEnumerable<CustomModelMaterial> materials,
        IReadOnlyDictionary<string, ImmutableArray<byte>> payloads,
        ImmutableArray<CustomModelImportDiagnostic>.Builder diagnostics)
    {
        ImmutableArray<CustomModelImportDiagnostic> existing = diagnostics.ToImmutable();
        ImmutableArray<CustomModelImportDiagnostic> retained = existing
            .Where(static diagnostic => diagnostic.Code != ExternalTextureDiagnosticCode)
            .ToImmutableArray();
        diagnostics.Clear();
        diagnostics.AddRange(retained);
        diagnostics.AddRange(BuildTextureBindingDiagnostics(
            materials,
            payloads,
            existing));
    }

    internal static ImmutableArray<CustomModelImportDiagnostic>
        BuildTextureBindingDiagnostics(
            IEnumerable<CustomModelMaterial> materials,
            IReadOnlyDictionary<string, ImmutableArray<byte>> payloads,
            IEnumerable<CustomModelImportDiagnostic>? existingDiagnostics = null)
    {
        CustomModelImportDiagnostic[] existing =
            existingDiagnostics?
                .Where(static diagnostic =>
                    diagnostic.Code == ExternalTextureDiagnosticCode)
                .ToArray() ?? [];
        var result = ImmutableArray.CreateBuilder<CustomModelImportDiagnostic>();
        foreach (CustomModelMaterial material in materials)
        {
            foreach (CustomModelTextureBinding binding in material.Textures)
            {
                bool hasPayload = binding.PackageEntryPath is { } entryPath &&
                    payloads.TryGetValue(entryPath, out ImmutableArray<byte> payload) &&
                    !payload.IsDefaultOrEmpty;
                if (hasPayload)
                {
                    continue;
                }

                CustomModelImportDiagnostic? prior = existing.FirstOrDefault(diagnostic =>
                    string.Equals(diagnostic.Subject, material.Name, StringComparison.Ordinal));
                result.Add(prior ?? new CustomModelImportDiagnostic
                {
                    Code = ExternalTextureDiagnosticCode,
                    Severity = CustomModelImportSeverity.Warning,
                    Subject = material.Name,
                    Message = $"Texture '{binding.DisplayName}' has no packaged bytes; select or embed its {binding.Semantic} image before build.",
                });
            }
        }

        return result.ToImmutable();
    }

    private static (ImmutableArray<byte> Content, string? OriginalReference) ReadTexturePayload(
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objects,
        long textureObjectId,
        FbxNode textureNode)
    {
        string? reference = ReadFirstStringChild(textureNode, "RelativeFilename") ??
            ReadFirstStringChild(textureNode, "FileName") ??
            ReadFirstStringChild(textureNode, "Filename");
        foreach (FbxConnection videoConnection in scene.GetChildren(textureObjectId)
                     .Where(connection =>
                         connection.Kind == "OO" &&
                         objects.TryGetValue(connection.ChildId, out FbxNode? child) &&
                         IsObject(child, "Video"))
                     .OrderBy(static connection => connection.ChildId))
        {
            FbxNode videoNode = objects[videoConnection.ChildId];
            reference ??= ReadFirstStringChild(videoNode, "RelativeFilename") ??
                ReadFirstStringChild(videoNode, "FileName") ??
                ReadFirstStringChild(videoNode, "Filename");
            ImmutableArray<byte> content = ReadByteArray(videoNode.FindChild("Content"));
            if (!content.IsDefaultOrEmpty)
            {
                return (content, reference);
            }
        }

        return (ReadByteArray(textureNode.FindChild("Content")), reference);
    }

    private static ImmutableArray<byte> ReadByteArray(FbxNode? node)
    {
        if (node is null || node.Properties.IsEmpty)
        {
            return [];
        }

        return node.Properties[0].Value switch
        {
            ImmutableArray<byte> bytes => bytes,
            byte[] bytes => bytes.ToImmutableArray(),
            _ => [],
        };
    }

    internal static CustomModelTextureSemantic ClassifyTextureSemantic(
        string propertyName,
        string textureName,
        out string? inferredToken)
    {
        inferredToken = null;
        string property = propertyName.Trim();
        if (property.Equals("DiffuseColor", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("Maya|baseColor", StringComparison.OrdinalIgnoreCase))
        {
            return CustomModelTextureSemantic.BaseColor;
        }

        if (property.Equals("NormalMap", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("Bump", StringComparison.OrdinalIgnoreCase))
        {
            return CustomModelTextureSemantic.Normal;
        }

        if (property.Equals("SpecularColor", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("ShininessExponent", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("ReflectionFactor", StringComparison.OrdinalIgnoreCase))
        {
            return CustomModelTextureSemantic.Specular;
        }

        if (property.Equals("TransparentColor", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("TransparencyFactor", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("Opacity", StringComparison.OrdinalIgnoreCase))
        {
            return CustomModelTextureSemantic.Mask;
        }

        foreach (string token in TextureNameTokens(textureName))
        {
            CustomModelTextureSemantic? semantic = token switch
            {
                "normal" or "nrm" or "bump" => CustomModelTextureSemantic.Normal,
                "spec" or "specular" or "rough" or "roughness" or
                    "metal" or "metallic" => CustomModelTextureSemantic.Specular,
                "mask" or "alpha" or "opacity" => CustomModelTextureSemantic.Mask,
                "diffuse" or "albedo" or "basecolor" => CustomModelTextureSemantic.BaseColor,
                _ => null,
            };
            if (semantic is not null)
            {
                inferredToken = token;
                return semantic.Value;
            }
        }

        return CustomModelTextureSemantic.BaseColor;
    }

    private static IEnumerable<string> TextureNameTokens(string value)
    {
        var token = new StringBuilder();
        char previous = '\0';
        foreach (char character in value.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                if (token.Length > 0 && char.IsUpper(character) && char.IsLower(previous))
                {
                    yield return token.ToString().ToLowerInvariant();
                    token.Clear();
                }

                token.Append(character);
            }
            else if (token.Length > 0)
            {
                string result = token.ToString().ToLowerInvariant();
                token.Clear();
                if (result != "tok")
                {
                    yield return result;
                }
            }

            previous = character;
        }

        if (token.Length > 0)
        {
            string result = token.ToString().ToLowerInvariant();
            if (result != "tok")
            {
                yield return result;
            }
        }
    }

    internal static (
        ImmutableArray<byte> Content,
        string? ResolvedPath,
        ImmutableArray<string> ProbedPaths)
        ResolveExternalTexture(
            string searchRoot,
            string originalFileName,
            string reference,
            long maximumTextureBytes,
            CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(searchRoot);
        if (Path.IsPathRooted(reference) || DirectoryHasReparsePoint(root, root))
        {
            return ([], null, []);
        }

        string normalizedReference = reference.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string basename = Path.GetFileName(normalizedReference);
        string extension = Path.GetExtension(basename).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(basename) ||
            !CustomModelTextureDecoder.SupportedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            return ([], null, []);
        }

        string fbxStem = Path.GetFileNameWithoutExtension(originalFileName);
        string[] rawCandidates =
        [
            Path.Combine(root, basename),
            Path.Combine(root, $"{fbxStem}.fbm", basename),
            Path.Combine(root, "textures", basename),
            Path.Combine(root, normalizedReference),
        ];
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var probed = ImmutableArray.CreateBuilder<string>();
        foreach (string rawCandidate in rawCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string candidate;
            try
            {
                candidate = Path.GetFullPath(rawCandidate);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                DirectoryHasReparsePoint(root, candidate))
            {
                continue;
            }

            probed.Add(candidate);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var info = new FileInfo(candidate);
            if (info.Length <= 0 || info.Length > maximumTextureBytes)
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(candidate);
            if (bytes.LongLength != info.Length || bytes.LongLength > maximumTextureBytes)
            {
                continue;
            }

            return (bytes.ToImmutableArray(), candidate, probed.ToImmutable());
        }

        return ([], null, probed.ToImmutable());
    }

    private static bool DirectoryHasReparsePoint(string root, string candidate)
    {
        string current = Path.GetFullPath(root);
        string relative = Path.GetRelativePath(current, Path.GetFullPath(candidate));
        if (relative == ".")
        {
            return new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }

        foreach (string part in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(current);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
        }

        return false;
    }

    internal static string DetectTextureExtension(ImmutableArray<byte> content, string? reference)
    {
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (content.Length >= pngSignature.Length &&
            content.AsSpan(0, pngSignature.Length).SequenceEqual(pngSignature))
        {
            return ".png";
        }

        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return ".jpg";
        }

        if (content.Length >= 4 && content.AsSpan(0, 4).SequenceEqual("DDS "u8))
        {
            return ".dds";
        }

        string extension = Path.GetExtension(reference ?? string.Empty).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".dds" or ".tga" or ".bmp"
            ? extension
            : ".bin";
    }

    private static string TextureMediaType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".dds" => "image/vnd-ms.dds",
        ".tga" => "image/x-tga",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    private static (
        ImmutableArray<CustomModelMeshPart> MeshParts,
        ImmutableArray<FbxModelSurface> Surfaces,
        ImmutableArray<CustomModelMorphChannel> MorphChannels)
        ReadMeshes(
            FbxSemanticScene scene,
            ImmutableDictionary<long, FbxNode> objects,
            FbxStrictExportInspection inspection,
            RigDefinition? rig,
            ImmutableArray<CustomModelBone> bones,
            ImmutableDictionary<long, int> boneIndexByModel,
            ImmutableDictionary<long, Guid> materialIds,
            ImmutableArray<CustomModelMaterial> materials,
            ImmutableDictionary<long, TransformMatrix> rawBindPose,
            ImmutableDictionary<long, TransformMatrix> rawEvaluatedGlobals,
            double metersPerUnit,
            TransformMatrix basis,
            FbxModelAuthoringImportOptions options,
            ImmutableArray<CustomModelImportDiagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        _ = inspection;
        (long ObjectId, FbxNode Node)[] geometries = objects
            .Where(static pair => IsObject(pair.Value, "Geometry", "Mesh"))
            .Select(static pair => (pair.Key, pair.Value))
            .OrderBy(static pair => pair.Key)
            .ToArray();
        if (geometries.Length == 0)
        {
            throw new InvalidDataException("FBX model import requires at least one mesh Geometry object.");
        }

        if (geometries.Length > options.MaximumMeshes)
        {
            throw new InvalidDataException(
                $"FBX contains {geometries.Length:N0} mesh geometries; the configured limit is {options.MaximumMeshes:N0}.");
        }

        ImmutableArray<TransformMatrix> rigBindGlobals =
            rig is null ? [] : ComputeExactBindGlobals(bones);
        ImmutableArray<TransformMatrix> rigInverseBindGlobals = rigBindGlobals
            .Select(static matrix => matrix.InvertedAffine())
            .ToImmutableArray();
        var meshParts = ImmutableArray.CreateBuilder<CustomModelMeshPart>(geometries.Length);
        var surfaces = ImmutableArray.CreateBuilder<FbxModelSurface>();
        var morphChannels = ImmutableArray.CreateBuilder<CustomModelMorphChannel>();
        var morphNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var morphDescriptors = new Dictionary<uint, string>();
        long affectedMorphControlPointCount = 0;
        long decodedMorphDeltaBytes = 0;
        long expandedVertexTotal = 0;
        foreach ((long geometryObjectId, FbxNode geometry) in geometries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string geometryName = ReadObjectName(geometry, $"Geometry {geometryObjectId}");
            long? modelObjectId = ResolveMeshModel(scene, geometryObjectId);
            string meshName = modelObjectId.HasValue
                ? scene.Models[modelObjectId.Value].Name
                : geometryName;
            ImmutableArray<double> rawVertices = FbxSemanticValues.ReadDoubleArray(
                geometry.FindChild("Vertices"),
                $"Geometry '{geometryName}' Vertices");
            if (rawVertices.IsEmpty || rawVertices.Length % 3 != 0 || rawVertices.Any(static value => !double.IsFinite(value)))
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' Vertices must contain complete finite triples.");
            }

            var controlPoints = ImmutableArray.CreateBuilder<Vector3D>(rawVertices.Length / 3);
            for (int offset = 0; offset < rawVertices.Length; offset += 3)
            {
                controlPoints.Add(new Vector3D(rawVertices[offset], rawVertices[offset + 1], rawVertices[offset + 2]));
            }

            ImmutableArray<long> polygonVertexIndices = FbxSemanticValues.ReadInt64Array(
                geometry.FindChild("PolygonVertexIndex"),
                $"Geometry '{geometryName}' PolygonVertexIndex");
            ImmutableArray<FbxPolygon> polygons = ReadPolygons(
                polygonVertexIndices,
                controlPoints.Count,
                geometryName);
            FbxVectorLayer? normalLayer = ReadVectorLayer(
                geometry.FindChildren("LayerElementNormal").FirstOrDefault(),
                "Normals",
                "NormalsIndex",
                3,
                geometryName);
            FbxVectorLayer? uvLayer = ReadVectorLayer(
                geometry.FindChildren("LayerElementUV").FirstOrDefault(),
                "UV",
                "UVIndex",
                2,
                geometryName);
            FbxMaterialLayer? materialLayer = ReadMaterialLayer(
                geometry.FindChildren("LayerElementMaterial").FirstOrDefault(),
                geometryName);
            ImmutableArray<Guid> modelMaterialIds = ResolveModelMaterials(
                scene,
                objects,
                modelObjectId,
                materialIds,
                materials[0].Id);
            ImmutableArray<ImmutableArray<FbxBoneInfluence>> influences = ReadControlPointInfluences(
                scene,
                objects,
                geometryObjectId,
                controlPoints.Count,
                rig,
                boneIndexByModel,
                geometryName,
                out int maximumSourceInfluences,
                out int maximumRetainedInfluences,
                out double maximumDiscardedWeight,
                diagnostics,
                cancellationToken);

            TransformMatrix rawMeshGlobal = modelObjectId.HasValue
                ? rawBindPose.GetValueOrDefault(
                    modelObjectId.Value,
                    rawEvaluatedGlobals.GetValueOrDefault(modelObjectId.Value, TransformMatrix.Identity))
                : TransformMatrix.Identity;
            TransformMatrix geometric = modelObjectId.HasValue
                ? ReadGeometricTransform(scene.Models[modelObjectId.Value])
                : TransformMatrix.Identity;
            TransformMatrix rawBake = rawMeshGlobal * geometric;
            TransformMatrix rawNormalTransform = rawBake.InvertedAffine();
            ImmutableArray<FbxGeometryMorphDraft> geometryMorphs = options.IgnoreMorphChannels
                ? []
                : ReadGeometryMorphs(
                    scene,
                    objects,
                    geometryObjectId,
                    geometryName,
                    controlPoints.Count,
                    rawBake,
                    metersPerUnit,
                    basis,
                    options,
                    morphChannels,
                    morphNames,
                    morphDescriptors,
                    ref affectedMorphControlPointCount,
                    ref decodedMorphDeltaBytes,
                    cancellationToken);
            var transformedControlPoints = ImmutableArray.CreateBuilder<Vector3D>(controlPoints.Count);
            foreach (Vector3D controlPoint in controlPoints)
            {
                Vector3D rawWorld = rawBake.TransformPoint(controlPoint);
                transformedControlPoints.Add(basis.TransformDirection(rawWorld * metersPerUnit));
            }

            bool reverseWinding = rawBake.LinearDeterminant * basis.LinearDeterminant < 0.0;
            var triangles = ImmutableArray.CreateBuilder<FbxExpandedTriangle>();
            var sourceMaterialSlots = ImmutableHashSet.CreateBuilder<int>();
            for (int polygonIndex = 0; polygonIndex < polygons.Length; polygonIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FbxPolygon polygon = polygons[polygonIndex];
                ImmutableArray<(int A, int B, int C)> polygonTriangles = TriangulatePolygon(
                    polygon,
                    transformedControlPoints,
                    geometryName,
                    polygonIndex);
                int sourceMaterialIndex = ResolveMaterialIndex(
                    materialLayer,
                    polygonIndex,
                    polygon.Corners[0].PolygonVertexIndex);
                if (sourceMaterialIndex < 0 || sourceMaterialIndex >= modelMaterialIds.Length)
                {
                    diagnostics.Add(new CustomModelImportDiagnostic
                    {
                        Code = "model_material_slot_out_of_range",
                        Severity = CustomModelImportSeverity.Warning,
                        Subject = meshName,
                        Message = $"Mesh '{meshName}' polygon {polygonIndex} references material slot {sourceMaterialIndex}; the default slot is used.",
                    });
                    sourceMaterialIndex = 0;
                }

                sourceMaterialSlots.Add(sourceMaterialIndex);
                foreach ((int first, int second, int third) in polygonTriangles)
                {
                    int a = first;
                    int b = reverseWinding ? third : second;
                    int c = reverseWinding ? second : third;
                    FbxPolygonCorner ca = polygon.Corners[a];
                    FbxPolygonCorner cb = polygon.Corners[b];
                    FbxPolygonCorner cc = polygon.Corners[c];
                    Vector3D pa = transformedControlPoints[ca.ControlPointIndex];
                    Vector3D pb = transformedControlPoints[cb.ControlPointIndex];
                    Vector3D pc = transformedControlPoints[cc.ControlPointIndex];
                    Vector3D faceNormal = Vector3D.Cross(pb - pa, pc - pa).Normalized();
                    triangles.Add(new FbxExpandedTriangle(
                        sourceMaterialIndex,
                        BuildExpandedCorner(ca, polygonIndex, pa, faceNormal, normalLayer, uvLayer, influences, rawNormalTransform, basis),
                        BuildExpandedCorner(cb, polygonIndex, pb, faceNormal, normalLayer, uvLayer, influences, rawNormalTransform, basis),
                        BuildExpandedCorner(cc, polygonIndex, pc, faceNormal, normalLayer, uvLayer, influences, rawNormalTransform, basis)));
                }
            }

            int firstSurfaceIndex = surfaces.Count;
            int partitionIndex = 0;
            foreach (IGrouping<int, FbxExpandedTriangle> materialGroup in triangles
                         .GroupBy(static triangle => triangle.MaterialIndex)
                         .OrderBy(static group => group.Key))
            {
                var pending = new List<FbxExpandedTriangle>();
                var palette = new HashSet<int>();
                foreach (FbxExpandedTriangle triangle in materialGroup)
                {
                    HashSet<int> triangleBones = triangle.Corners
                        .SelectMany(static corner => corner.Influences)
                        .Where(static influence => influence.Weight > 0.0)
                        .Select(static influence => influence.BoneIndex)
                        .ToHashSet();
                    int addedBones = triangleBones.Count(bone => !palette.Contains(bone));
                    if (pending.Count > 0 &&
                        (palette.Count + addedBones > MaximumPaletteEntries ||
                         checked((pending.Count + 1) * 3) > MaximumVerticesPerDraw))
                    {
                        surfaces.Add(CreateSurface(
                            meshName,
                            materialGroup.Key,
                            partitionIndex++,
                            modelMaterialIds[materialGroup.Key],
                            pending,
                            palette,
                            rigInverseBindGlobals,
                            geometryMorphs));
                        pending.Clear();
                        palette.Clear();
                    }

                    if (triangleBones.Count > MaximumPaletteEntries)
                    {
                        throw new InvalidDataException(
                            $"Mesh '{meshName}' has one triangle requiring {triangleBones.Count:N0} bones; DL1 draw palettes support {MaximumPaletteEntries:N0}.");
                    }

                    pending.Add(triangle);
                    palette.UnionWith(triangleBones);
                }

                if (pending.Count > 0)
                {
                    surfaces.Add(CreateSurface(
                        meshName,
                        materialGroup.Key,
                        partitionIndex++,
                        modelMaterialIds[materialGroup.Key],
                        pending,
                        palette,
                        rigInverseBindGlobals,
                        geometryMorphs));
                }
            }

            int surfaceCount = surfaces.Count - firstSurfaceIndex;
            int expandedCount = triangles.Count * 3;
            expandedVertexTotal = checked(expandedVertexTotal + expandedCount);
            if (expandedVertexTotal > options.MaximumExpandedVertices)
            {
                throw new InvalidDataException(
                    $"Expanded FBX model geometry exceeds the configured {options.MaximumExpandedVertices:N0}-vertex limit.");
            }

            int requiredPaletteSize = surfaces
                .Skip(firstSurfaceIndex)
                .Take(surfaceCount)
                .Select(static surface => surface.PaletteBoneIndices.Length)
                .DefaultIfEmpty(0)
                .Max();
            meshParts.Add(new CustomModelMeshPart
            {
                Name = meshName,
                GeometryObjectId = geometryObjectId,
                ModelObjectId = modelObjectId ?? 0,
                ControlPointCount = controlPoints.Count,
                PolygonCount = polygons.Length,
                TriangleCount = triangles.Count,
                ExpandedVertexCount = expandedCount,
                MaterialSlotCount = modelMaterialIds.Length,
                MaximumSourceInfluences = maximumSourceInfluences,
                MaximumRetainedInfluences = maximumRetainedInfluences,
                MaximumDiscardedWeight = maximumDiscardedWeight,
                RequiredPaletteSize = requiredPaletteSize,
                SourceMaterialIndices = sourceMaterialSlots.Order().ToImmutableArray(),
            });
        }

        return (meshParts.ToImmutable(), surfaces.ToImmutable(), morphChannels.ToImmutable());
    }

    private static ImmutableArray<TransformMatrix> ComputeExactBindGlobals(
        ImmutableArray<CustomModelBone> bones)
    {
        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(bones.Length);
        for (int index = 0; index < bones.Length; index++)
        {
            CustomModelBone bone = bones[index];
            globals.Add(
                bone.ParentIndex < 0
                    ? bone.ExactLocalBindMatrix
                    : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix);
        }

        return globals.MoveToImmutable();
    }

    private static FbxModelSurface CreateSurface(
        string meshName,
        int materialIndex,
        int partitionIndex,
        Guid materialId,
        IReadOnlyList<FbxExpandedTriangle> triangles,
        HashSet<int> paletteSet,
        ImmutableArray<TransformMatrix> globalInverseBindMatrices,
        ImmutableArray<FbxGeometryMorphDraft> geometryMorphs)
    {
        ImmutableArray<int> palette = paletteSet.Order().ToImmutableArray();
        ImmutableDictionary<int, int> localPaletteIndices = palette
            .Select((globalIndex, localIndex) => (globalIndex, localIndex))
            .ToImmutableDictionary(static pair => pair.globalIndex, static pair => pair.localIndex);
        var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>(triangles.Count * 3);
        var indices = ImmutableArray.CreateBuilder<uint>(triangles.Count * 3);
        var expandedControlPoints = ImmutableArray.CreateBuilder<int>(triangles.Count * 3);
        foreach (FbxExpandedTriangle triangle in triangles)
        {
            foreach (FbxExpandedCorner corner in triangle.Corners)
            {
                uint index = checked((uint)vertices.Count);
                indices.Add(index);
                expandedControlPoints.Add(corner.ControlPointIndex);
                vertices.Add(new FbxModelVertex(
                    corner.Position,
                    corner.Normal,
                    corner.U,
                    corner.V,
                    corner.Influences.Select(influence => localPaletteIndices[influence.BoneIndex]).ToImmutableArray(),
                    corner.Influences.Select(static influence => influence.Weight).ToImmutableArray()));
            }
        }

        ImmutableArray<TransformMatrix> inverseBinds = palette
            .Select(index => globalInverseBindMatrices[index])
            .ToImmutableArray();
        ImmutableArray<int> expandedControlPointArray = expandedControlPoints.ToImmutable();
        return new FbxModelSurface(
            $"{meshName}/material-{materialIndex}/draw-{partitionIndex}",
            meshName,
            materialId,
            vertices.ToImmutable(),
            indices.ToImmutable(),
            palette,
            inverseBinds,
            !palette.IsEmpty)
        {
            MorphTargets = geometryMorphs.Select(morph => new FbxModelMorphTarget(
                morph.Name,
                morph.DescriptorHash,
                morph.BlendShapeChannelObjectId,
                morph.ShapeObjectId,
                expandedControlPointArray
                    .Select(controlPoint => morph.DeltasByControlPoint.GetValueOrDefault(
                        controlPoint,
                        Vector3D.Zero))
                    .ToImmutableArray()))
                .ToImmutableArray(),
        };
    }

    private readonly record struct FbxPolygonCorner(int ControlPointIndex, int PolygonVertexIndex);

    private sealed record FbxPolygon(ImmutableArray<FbxPolygonCorner> Corners);

    private sealed record FbxVectorLayer(
        string Mapping,
        string Reference,
        int ComponentCount,
        ImmutableArray<double> Values,
        ImmutableArray<long> Indices);

    private sealed record FbxMaterialLayer(
        string Mapping,
        string Reference,
        ImmutableArray<long> Values,
        ImmutableArray<long> Indices);

    private readonly record struct FbxBoneInfluence(int BoneIndex, double Weight);

    private sealed record FbxExpandedCorner(
        int ControlPointIndex,
        Vector3D Position,
        Vector3D Normal,
        double U,
        double V,
        ImmutableArray<FbxBoneInfluence> Influences);

    private sealed record FbxExpandedTriangle(
        int MaterialIndex,
        FbxExpandedCorner First,
        FbxExpandedCorner Second,
        FbxExpandedCorner Third)
    {
        public ImmutableArray<FbxExpandedCorner> Corners => [First, Second, Third];
    }

    private sealed record FbxGeometryMorphDraft(
        string Name,
        uint DescriptorHash,
        long BlendShapeChannelObjectId,
        long ShapeObjectId,
        ImmutableDictionary<int, Vector3D> DeltasByControlPoint);

    private static ImmutableArray<FbxGeometryMorphDraft> ReadGeometryMorphs(
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objects,
        long geometryObjectId,
        string geometryName,
        int controlPointCount,
        TransformMatrix rawBake,
        double metersPerUnit,
        TransformMatrix basis,
        FbxModelAuthoringImportOptions options,
        ImmutableArray<CustomModelMorphChannel>.Builder morphChannels,
        HashSet<string> morphNames,
        Dictionary<uint, string> morphDescriptors,
        ref long affectedControlPointCount,
        ref long decodedDeltaBytes,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<FbxGeometryMorphDraft>();
        long[] blendShapeIds = scene.GetChildren(geometryObjectId)
            .Where(connection =>
                string.Equals(connection.Kind, "OO", StringComparison.Ordinal) &&
                objects.TryGetValue(connection.ChildId, out FbxNode? node) &&
                IsObject(node, "Deformer", "BlendShape"))
            .Select(static connection => connection.ChildId)
            .Distinct()
            .ToArray();

        foreach (long blendShapeId in blendShapeIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long[] channelIds = scene.GetChildren(blendShapeId)
                .Where(connection =>
                    string.Equals(connection.Kind, "OO", StringComparison.Ordinal) &&
                    objects.TryGetValue(connection.ChildId, out FbxNode? node) &&
                    IsObject(node, "Deformer", "BlendShapeChannel"))
                .Select(static connection => connection.ChildId)
                .Distinct()
                .ToArray();

            foreach (long channelId in channelIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (morphChannels.Count >= options.MaximumMorphChannels)
                {
                    throw new InvalidDataException(
                        $"FBX contains more than {options.MaximumMorphChannels:N0} morph channels.");
                }

                FbxNode channel = objects[channelId];
                string channelName = ReadObjectName(
                    channel,
                    $"BlendShapeChannel {channelId}");
                long[] shapeIds = scene.GetChildren(channelId)
                    .Where(connection =>
                        string.Equals(connection.Kind, "OO", StringComparison.Ordinal) &&
                        objects.TryGetValue(connection.ChildId, out FbxNode? node) &&
                        IsObject(node, "Geometry", "Shape"))
                    .Select(static connection => connection.ChildId)
                    .Distinct()
                    .ToArray();
                ImmutableArray<double> fullWeights = FbxSemanticValues.ReadDoubleArray(
                    channel.FindChild("FullWeights"),
                    $"BlendShapeChannel '{channelName}' FullWeights");
                if (shapeIds.Length != 1 || fullWeights.Length > 1)
                {
                    throw new InvalidDataException(
                        $"BlendShapeChannel '{channelName}' uses progressive or multiple shapes; " +
                        "bake it to one shape per channel before import.");
                }

                long shapeId = shapeIds[0];
                FbxNode shape = objects[shapeId];
                ImmutableArray<long> indexes = FbxSemanticValues.ReadInt64Array(
                    shape.FindChild("Indexes"),
                    $"Shape '{channelName}' Indexes");
                ImmutableArray<double> vertices = FbxSemanticValues.ReadDoubleArray(
                    shape.FindChild("Vertices"),
                    $"Shape '{channelName}' Vertices");
                if (indexes.IsEmpty || vertices.IsEmpty ||
                    vertices.Length % 3 != 0 ||
                    indexes.Length != vertices.Length / 3 ||
                    vertices.Any(static value => !double.IsFinite(value)))
                {
                    throw new InvalidDataException(
                        $"Shape '{channelName}' must contain matching finite Indexes and XYZ delta arrays.");
                }

                affectedControlPointCount = checked(affectedControlPointCount + indexes.Length);
                decodedDeltaBytes = checked(decodedDeltaBytes + (vertices.Length * sizeof(double)));
                if (affectedControlPointCount > options.MaximumMorphAffectedControlPoints ||
                    decodedDeltaBytes > options.MaximumDecodedMorphDeltaBytes)
                {
                    throw new InvalidDataException(
                        $"FBX morph deltas exceed the configured bounded allocation budget " +
                        $"({options.MaximumMorphAffectedControlPoints:N0} affected control points, " +
                        $"{options.MaximumDecodedMorphDeltaBytes:N0} decoded bytes).");
                }

                if (!morphNames.Add(channelName))
                {
                    throw new InvalidDataException(
                        $"FBX BlendShapeChannel name '{channelName}' is duplicated; " +
                        "channel names must be unique for DL1 morph descriptors.");
                }

                uint descriptor = Dl1NameHash.Compute(channelName);
                if (morphDescriptors.TryGetValue(descriptor, out string? collidingName))
                {
                    throw new InvalidDataException(
                        $"FBX BlendShapeChannels '{collidingName}' and '{channelName}' collide at " +
                        $"DL1 descriptor 0x{descriptor:X8}; rename one channel before import.");
                }

                morphDescriptors.Add(descriptor, channelName);
                var deltas = ImmutableDictionary.CreateBuilder<int, Vector3D>();
                for (int deltaIndex = 0; deltaIndex < indexes.Length; deltaIndex++)
                {
                    long rawControlPointIndex = indexes[deltaIndex];
                    if (rawControlPointIndex < 0 || rawControlPointIndex >= controlPointCount)
                    {
                        throw new InvalidDataException(
                            $"Shape '{channelName}' index {rawControlPointIndex} is outside Geometry " +
                            $"'{geometryName}'s {controlPointCount:N0}-control-point buffer.");
                    }

                    int controlPointIndex = checked((int)rawControlPointIndex);
                    Vector3D rawDelta = new(
                        vertices[(deltaIndex * 3) + 0],
                        vertices[(deltaIndex * 3) + 1],
                        vertices[(deltaIndex * 3) + 2]);
                    Vector3D normalizedDelta = basis.TransformDirection(
                        rawBake.TransformDirection(rawDelta) * metersPerUnit);
                    if (!normalizedDelta.IsFinite ||
                        !deltas.TryAdd(controlPointIndex, normalizedDelta))
                    {
                        throw new InvalidDataException(
                            $"Shape '{channelName}' contains a non-finite delta or duplicate control-point index " +
                            $"{controlPointIndex}.");
                    }
                }

                var draft = new FbxGeometryMorphDraft(
                    channelName,
                    descriptor,
                    channelId,
                    shapeId,
                    deltas.ToImmutable());
                result.Add(draft);
                morphChannels.Add(new CustomModelMorphChannel
                {
                    Index = morphChannels.Count,
                    BlendShapeChannelObjectId = channelId,
                    ShapeObjectId = shapeId,
                    Name = channelName,
                    DescriptorHash = descriptor,
                    GeometryObjectIds = [geometryObjectId],
                });
            }
        }

        return result.ToImmutable();
    }

    private static long? ResolveMeshModel(FbxSemanticScene scene, long geometryObjectId)
    {
        long[] candidates = scene.GetParents(geometryObjectId)
            .Where(connection =>
                connection.Kind == "OO" &&
                scene.Models.TryGetValue(connection.ParentId, out FbxModelObject? model) &&
                string.Equals(model.Subtype, "Mesh", StringComparison.Ordinal))
            .Select(static connection => connection.ParentId)
            .Distinct()
            .ToArray();
        return candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidDataException(
                $"FBX Geometry {geometryObjectId} is connected to multiple Mesh Models."),
        };
    }

    private static ImmutableArray<FbxPolygon> ReadPolygons(
        ImmutableArray<long> polygonVertexIndices,
        int controlPointCount,
        string geometryName)
    {
        var result = ImmutableArray.CreateBuilder<FbxPolygon>();
        var corners = ImmutableArray.CreateBuilder<FbxPolygonCorner>();
        for (int polygonVertexIndex = 0; polygonVertexIndex < polygonVertexIndices.Length; polygonVertexIndex++)
        {
            long raw = polygonVertexIndices[polygonVertexIndex];
            long decoded = raw < 0 ? ~raw : raw;
            if (decoded < 0 || decoded >= controlPointCount)
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' polygon vertex {decoded} is outside its {controlPointCount:N0}-vertex buffer.");
            }

            corners.Add(new FbxPolygonCorner(checked((int)decoded), polygonVertexIndex));
            if (raw >= 0)
            {
                continue;
            }

            if (corners.Count < 3)
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' contains a polygon with fewer than three vertices.");
            }

            result.Add(new FbxPolygon(corners.ToImmutable()));
            corners.Clear();
        }

        if (corners.Count != 0)
        {
            throw new InvalidDataException(
                $"Geometry '{geometryName}' PolygonVertexIndex does not terminate its final polygon.");
        }

        return result.ToImmutable();
    }

    private static FbxVectorLayer? ReadVectorLayer(
        FbxNode? layer,
        string valuesName,
        string indicesName,
        int componentCount,
        string geometryName)
    {
        if (layer is null)
        {
            return null;
        }

        ImmutableArray<double> values = FbxSemanticValues.ReadDoubleArray(
            layer.FindChild(valuesName),
            $"Geometry '{geometryName}' {valuesName}");
        ImmutableArray<long> indices = FbxSemanticValues.ReadInt64Array(
            layer.FindChild(indicesName),
            $"Geometry '{geometryName}' {indicesName}");
        if (values.IsEmpty || values.Length % componentCount != 0 || values.Any(static value => !double.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"Geometry '{geometryName}' {valuesName} must contain complete finite {componentCount}-component values.");
        }

        return new FbxVectorLayer(
            ReadLayerString(layer, "MappingInformationType", "ByPolygonVertex"),
            ReadLayerString(layer, "ReferenceInformationType", indices.IsEmpty ? "Direct" : "IndexToDirect"),
            componentCount,
            values,
            indices);
    }

    private static FbxMaterialLayer? ReadMaterialLayer(FbxNode? layer, string geometryName)
    {
        if (layer is null)
        {
            return null;
        }

        ImmutableArray<long> values = FbxSemanticValues.ReadInt64Array(
            layer.FindChild("Materials"),
            $"Geometry '{geometryName}' Materials");
        ImmutableArray<long> indices = FbxSemanticValues.ReadInt64Array(
            layer.FindChild("MaterialsIndex") ?? layer.FindChild("MaterialIndex"),
            $"Geometry '{geometryName}' MaterialsIndex");
        return new FbxMaterialLayer(
            ReadLayerString(layer, "MappingInformationType", "ByPolygon"),
            ReadLayerString(layer, "ReferenceInformationType", indices.IsEmpty ? "Direct" : "IndexToDirect"),
            values,
            indices);
    }

    private static string ReadLayerString(FbxNode layer, string childName, string fallback) =>
        layer.FindChild(childName)?.FirstString() ?? fallback;

    private static ImmutableArray<Guid> ResolveModelMaterials(
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objects,
        long? modelObjectId,
        ImmutableDictionary<long, Guid> materialIds,
        Guid defaultMaterialId)
    {
        if (!modelObjectId.HasValue)
        {
            return [defaultMaterialId];
        }

        ImmutableArray<Guid> result = scene.GetChildren(modelObjectId.Value)
            .Where(connection =>
                connection.Kind == "OO" &&
                objects.TryGetValue(connection.ChildId, out FbxNode? child) &&
                IsObject(child, "Material") &&
                materialIds.ContainsKey(connection.ChildId))
            .Select(connection => materialIds[connection.ChildId])
            .Distinct()
            .ToImmutableArray();
        return result.IsEmpty ? [defaultMaterialId] : result;
    }

    private static int ResolveMaterialIndex(
        FbxMaterialLayer? layer,
        int polygonIndex,
        int polygonVertexIndex)
    {
        if (layer is null || layer.Values.IsEmpty)
        {
            return 0;
        }

        int mappedIndex = ResolveMappedIndex(layer.Mapping, 0, polygonIndex, polygonVertexIndex);
        // Unlike normal/UV layers, FBX LayerElementMaterial stores the model's
        // material-slot number directly in `Materials`.  Exporters commonly
        // still label the layer IndexToDirect while omitting a separate index
        // table entirely.  A rare explicit MaterialsIndex table is honored,
        // but its value indexes the Materials table rather than an undeclared
        // direct-value array.
        int valueIndex = layer.Indices.IsEmpty
            ? mappedIndex
            : checked((int)ReadLayerIndex(layer.Indices, mappedIndex, "material index"));
        if (layer.Values.Length == 1 && valueIndex > 0)
        {
            valueIndex = 0;
        }

        return checked((int)ReadLayerIndex(layer.Values, valueIndex, "material slot"));
    }

    private static FbxExpandedCorner BuildExpandedCorner(
        FbxPolygonCorner corner,
        int polygonIndex,
        Vector3D position,
        Vector3D faceNormal,
        FbxVectorLayer? normalLayer,
        FbxVectorLayer? uvLayer,
        ImmutableArray<ImmutableArray<FbxBoneInfluence>> influences,
        TransformMatrix rawNormalTransform,
        TransformMatrix basis)
    {
        Vector3D normal = faceNormal;
        if (normalLayer is not null)
        {
            ImmutableArray<double> values = ResolveVectorLayerValue(
                normalLayer,
                corner.ControlPointIndex,
                polygonIndex,
                corner.PolygonVertexIndex);
            // Normals use the transpose of inverse(model * geometric). The explicit
            // component form avoids introducing a second matrix convention.
            Vector3D rawNormal = new(
                (rawNormalTransform.M11 * values[0]) + (rawNormalTransform.M21 * values[1]) + (rawNormalTransform.M31 * values[2]),
                (rawNormalTransform.M12 * values[0]) + (rawNormalTransform.M22 * values[1]) + (rawNormalTransform.M32 * values[2]),
                (rawNormalTransform.M13 * values[0]) + (rawNormalTransform.M23 * values[1]) + (rawNormalTransform.M33 * values[2]));
            normal = basis.TransformDirection(rawNormal).Normalized();
        }

        double u = 0.0;
        double v = 0.0;
        if (uvLayer is not null)
        {
            ImmutableArray<double> values = ResolveVectorLayerValue(
                uvLayer,
                corner.ControlPointIndex,
                polygonIndex,
                corner.PolygonVertexIndex);
            u = values[0];
            v = values[1];
        }

        return new FbxExpandedCorner(
            corner.ControlPointIndex,
            position,
            normal,
            u,
            v,
            influences[corner.ControlPointIndex]);
    }

    private static ImmutableArray<double> ResolveVectorLayerValue(
        FbxVectorLayer layer,
        int controlPointIndex,
        int polygonIndex,
        int polygonVertexIndex)
    {
        int mappedIndex = ResolveMappedIndex(
            layer.Mapping,
            controlPointIndex,
            polygonIndex,
            polygonVertexIndex);
        int directIndex = layer.Reference is "IndexToDirect" or "Index"
            ? checked((int)ReadLayerIndex(layer.Indices, mappedIndex, "layer index"))
            : mappedIndex;
        int valueOffset = checked(directIndex * layer.ComponentCount);
        if (valueOffset < 0 || valueOffset + layer.ComponentCount > layer.Values.Length)
        {
            throw new InvalidDataException("FBX layer mapping resolves outside its direct value array.");
        }

        return layer.Values.Skip(valueOffset).Take(layer.ComponentCount).ToImmutableArray();
    }

    private static int ResolveMappedIndex(
        string mapping,
        int controlPointIndex,
        int polygonIndex,
        int polygonVertexIndex) => mapping switch
        {
            "ByVertice" or "ByVertex" or "ByControlPoint" => controlPointIndex,
            "ByPolygonVertex" => polygonVertexIndex,
            "ByPolygon" => polygonIndex,
            "AllSame" => 0,
            _ => throw new InvalidDataException($"Unsupported FBX layer mapping '{mapping}'."),
        };

    private static long ReadLayerIndex(ImmutableArray<long> values, int index, string label)
    {
        if (index < 0 || index >= values.Length)
        {
            throw new InvalidDataException($"FBX {label} {index} is outside its {values.Length:N0}-entry table.");
        }

        return values[index];
    }

    private static ImmutableArray<ImmutableArray<FbxBoneInfluence>> ReadControlPointInfluences(
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objects,
        long geometryObjectId,
        int controlPointCount,
        RigDefinition? rig,
        ImmutableDictionary<long, int> boneIndexByModel,
        string geometryName,
        out int maximumSourceInfluences,
        out int maximumRetainedInfluences,
        out double maximumDiscardedWeight,
        ImmutableArray<CustomModelImportDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var rows = Enumerable.Range(0, controlPointCount)
            .Select(static _ => new Dictionary<int, double>())
            .ToArray();
        long[] skinObjectIds = scene.GetChildren(geometryObjectId)
            .Where(connection =>
                connection.Kind == "OO" &&
                objects.TryGetValue(connection.ChildId, out FbxNode? child) &&
                IsObject(child, "Deformer", "Skin"))
            .Select(static connection => connection.ChildId)
            .Distinct()
            .ToArray();
        foreach (long skinObjectId in skinObjectIds)
        {
            foreach (FbxConnection clusterConnection in scene.GetChildren(skinObjectId)
                         .Where(connection =>
                             connection.Kind == "OO" &&
                             objects.TryGetValue(connection.ChildId, out FbxNode? child) &&
                             IsObject(child, "Deformer", "Cluster")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long clusterObjectId = clusterConnection.ChildId;
                FbxNode cluster = objects[clusterObjectId];
                long[] boneIds = scene.GetChildren(clusterObjectId)
                    .Where(connection =>
                        connection.Kind == "OO" &&
                        scene.Models.TryGetValue(connection.ChildId, out FbxModelObject? model) &&
                        model.IsLimb)
                    .Select(static connection => connection.ChildId)
                    .Distinct()
                    .ToArray();
                if (boneIds.Length != 1 || !boneIndexByModel.TryGetValue(boneIds[0], out int boneIndex))
                {
                    throw new InvalidDataException(
                        $"Geometry '{geometryName}' skin Cluster {clusterObjectId} does not resolve to exactly one imported LimbNode.");
                }

                ImmutableArray<long> indices = FbxSemanticValues.ReadInt64Array(
                    cluster.FindChild("Indexes"),
                    $"Cluster {clusterObjectId} Indexes");
                ImmutableArray<double> weights = FbxSemanticValues.ReadDoubleArray(
                    cluster.FindChild("Weights"),
                    $"Cluster {clusterObjectId} Weights");
                if (indices.Length != weights.Length)
                {
                    throw new InvalidDataException(
                        $"Geometry '{geometryName}' Cluster {clusterObjectId} must contain equal Indexes and Weights arrays.");
                }

                for (int influenceIndex = 0; influenceIndex < indices.Length; influenceIndex++)
                {
                    long controlPointIndex = indices[influenceIndex];
                    double weight = weights[influenceIndex];
                    if (controlPointIndex < 0 || controlPointIndex >= controlPointCount ||
                        !double.IsFinite(weight) || weight <= 0.0)
                    {
                        throw new InvalidDataException(
                            $"Geometry '{geometryName}' Cluster {clusterObjectId} contains an invalid influence.");
                    }

                    Dictionary<int, double> controlPoint = rows[checked((int)controlPointIndex)];
                    controlPoint[boneIndex] = controlPoint.GetValueOrDefault(boneIndex) + weight;
                }
            }
        }

        bool isSkinned = skinObjectIds.Length > 0;
        maximumSourceInfluences = 0;
        maximumRetainedInfluences = 0;
        maximumDiscardedWeight = 0.0;
        var result = ImmutableArray.CreateBuilder<ImmutableArray<FbxBoneInfluence>>(controlPointCount);
        for (int controlPointIndex = 0; controlPointIndex < rows.Length; controlPointIndex++)
        {
            KeyValuePair<int, double>[] ordered = rows[controlPointIndex]
                .Where(static pair => pair.Value > 1e-12)
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .ToArray();
            maximumSourceInfluences = Math.Max(maximumSourceInfluences, ordered.Length);
            if (isSkinned && ordered.Length == 0)
            {
                throw new InvalidDataException(
                    $"Skinned Geometry '{geometryName}' control point {controlPointIndex} has no positive bone influence. Root fallback is intentionally not inferred.");
            }

            KeyValuePair<int, double>[] retained = ordered.Take(MaximumInfluencesPerVertex).ToArray();
            double retainedTotal = retained.Sum(static pair => pair.Value);
            if (retained.Length > 0 && (!double.IsFinite(retainedTotal) || retainedTotal <= 1e-12))
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' control point {controlPointIndex} has non-normalizable skin weights.");
            }

            double discarded = ordered.Skip(MaximumInfluencesPerVertex).Sum(static pair => pair.Value);
            maximumDiscardedWeight = Math.Max(maximumDiscardedWeight, discarded);
            maximumRetainedInfluences = Math.Max(maximumRetainedInfluences, retained.Length);
            result.Add(retained
                .Select(pair => new FbxBoneInfluence(pair.Key, pair.Value / retainedTotal))
                .ToImmutableArray());
        }

        if (maximumSourceInfluences > MaximumInfluencesPerVertex)
        {
            diagnostics.Add(new CustomModelImportDiagnostic
            {
                Code = "model_skin_weights_reduced_to_top4",
                Severity = CustomModelImportSeverity.Warning,
                Subject = geometryName,
                Message = $"Mesh '{geometryName}' uses up to {maximumSourceInfluences} influences per control point. DL1 preview/build retains the deterministic top four; maximum discarded source weight is {maximumDiscardedWeight:0.######}.",
            });
        }

        if (isSkinned && rig is null)
        {
            throw new InvalidDataException(
                $"Geometry '{geometryName}' contains a Skin deformer but the selected model mode has no rig.");
        }

        return result.ToImmutable();
    }

    private static TransformMatrix ReadGeometricTransform(FbxModelObject model)
    {
        Vector3D translation = ReadModelVector(model, "GeometricTranslation", Vector3D.Zero);
        Vector3D rotation = ReadModelVector(model, "GeometricRotation", Vector3D.Zero);
        Vector3D scale = ReadModelVector(model, "GeometricScaling", Vector3D.One);
        int rawOrder = FbxSemanticValues.TryGetInt32(model.Properties, "RotationOrder") ?? 0;
        FbxEulerOrder order = rawOrder is >= 0 and <= 5 ? (FbxEulerOrder)rawOrder : FbxEulerOrder.Xyz;
        return TransformMatrix.CreateTranslation(translation) *
            FbxTransformEvaluator.EvaluateEuler(rotation, order) *
            TransformMatrix.CreateScale(scale);
    }

    private static Vector3D ReadModelVector(FbxModelObject model, string name, Vector3D fallback)
    {
        if (!model.Properties.TryGetValue(name, out ImmutableArray<object> values) || values.Length < 3)
        {
            return fallback;
        }

        try
        {
            Vector3D value = new(
                Convert.ToDouble(values[0], CultureInfo.InvariantCulture),
                Convert.ToDouble(values[1], CultureInfo.InvariantCulture),
                Convert.ToDouble(values[2], CultureInfo.InvariantCulture));
            return value.IsFinite
                ? value
                : throw new InvalidDataException($"FBX Model '{model.Name}' {name} contains non-finite values.");
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException($"FBX Model '{model.Name}' {name} is not numeric.", exception);
        }
    }

    private static ImmutableArray<(int A, int B, int C)> TriangulatePolygon(
        FbxPolygon polygon,
        ImmutableArray<Vector3D>.Builder positions,
        string geometryName,
        int polygonIndex)
    {
        if (polygon.Corners.Length == 3)
        {
            return [(0, 1, 2)];
        }

        Vector3D normal = Vector3D.Zero;
        for (int index = 0; index < polygon.Corners.Length; index++)
        {
            Vector3D current = positions[polygon.Corners[index].ControlPointIndex];
            Vector3D next = positions[polygon.Corners[(index + 1) % polygon.Corners.Length].ControlPointIndex];
            normal += new Vector3D(
                (current.Y - next.Y) * (current.Z + next.Z),
                (current.Z - next.Z) * (current.X + next.X),
                (current.X - next.X) * (current.Y + next.Y));
        }

        if (!normal.TryNormalize(out Vector3D unitNormal))
        {
            throw new InvalidDataException(
                $"Geometry '{geometryName}' polygon {polygonIndex} is degenerate and cannot be triangulated.");
        }

        int dominantAxis = Math.Abs(unitNormal.X) >= Math.Abs(unitNormal.Y) && Math.Abs(unitNormal.X) >= Math.Abs(unitNormal.Z)
            ? 0
            : Math.Abs(unitNormal.Y) >= Math.Abs(unitNormal.Z) ? 1 : 2;
        (double X, double Y)[] projected = polygon.Corners
            .Select(corner => Project(positions[corner.ControlPointIndex], dominantAxis))
            .ToArray();
        double signedArea = 0.0;
        for (int index = 0; index < projected.Length; index++)
        {
            (double x1, double y1) = projected[index];
            (double x2, double y2) = projected[(index + 1) % projected.Length];
            signedArea += (x1 * y2) - (x2 * y1);
        }

        if (Math.Abs(signedArea) <= 1e-14)
        {
            throw new InvalidDataException(
                $"Geometry '{geometryName}' polygon {polygonIndex} has zero projected area.");
        }

        bool counterClockwise = signedArea > 0.0;
        var remaining = Enumerable.Range(0, polygon.Corners.Length).ToList();
        var triangles = ImmutableArray.CreateBuilder<(int A, int B, int C)>(polygon.Corners.Length - 2);
        while (remaining.Count > 3)
        {
            bool clipped = false;
            for (int candidate = 0; candidate < remaining.Count; candidate++)
            {
                int previous = remaining[(candidate + remaining.Count - 1) % remaining.Count];
                int current = remaining[candidate];
                int next = remaining[(candidate + 1) % remaining.Count];
                double cross = Cross2D(projected[previous], projected[current], projected[next]);
                if (counterClockwise ? cross <= 1e-14 : cross >= -1e-14)
                {
                    continue;
                }

                bool containsOther = remaining.Any(index =>
                    index != previous && index != current && index != next &&
                    PointInTriangle(projected[index], projected[previous], projected[current], projected[next], counterClockwise));
                if (containsOther)
                {
                    continue;
                }

                triangles.Add((previous, current, next));
                remaining.RemoveAt(candidate);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' polygon {polygonIndex} is self-intersecting or non-planar beyond deterministic triangulation tolerance.");
            }
        }

        triangles.Add((remaining[0], remaining[1], remaining[2]));
        return triangles.ToImmutable();
    }

    private static (double X, double Y) Project(Vector3D value, int dominantAxis) => dominantAxis switch
    {
        0 => (value.Y, value.Z),
        1 => (value.X, value.Z),
        _ => (value.X, value.Y),
    };

    private static double Cross2D(
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c) =>
        ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static bool PointInTriangle(
        (double X, double Y) point,
        (double X, double Y) a,
        (double X, double Y) b,
        (double X, double Y) c,
        bool counterClockwise)
    {
        double ab = Cross2D(a, b, point);
        double bc = Cross2D(b, c, point);
        double ca = Cross2D(c, a, point);
        return counterClockwise
            ? ab >= -1e-14 && bc >= -1e-14 && ca >= -1e-14
            : ab <= 1e-14 && bc <= 1e-14 && ca <= 1e-14;
    }

    private static (
        ImmutableArray<CustomModelAnimationClip> Metadata,
        ImmutableDictionary<Guid, AnimationClip> Clips)
        ReadAnimationClips(
            FbxBinaryDocument binary,
            FbxSemanticScene scene,
            Guid modelId,
            string sourceSha256,
            RigDefinition? rig,
            FbxModelAuthoringImportOptions options,
            ImmutableArray<CustomModelImportDiagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (scene.AnimationStacks.IsEmpty)
        {
            return ([], ImmutableDictionary<Guid, AnimationClip>.Empty);
        }

        ImmutableDictionary<long, FbxAnimationStackActivity> activityById = scene
            .AnalyzeAnimationStacks(cancellationToken)
            .ToImmutableDictionary(static activity => activity.Stack.ObjectId);
        var metadata = ImmutableArray.CreateBuilder<CustomModelAnimationClip>(scene.AnimationStacks.Length);
        var clips = ImmutableDictionary.CreateBuilder<Guid, AnimationClip>();
        foreach (FbxAnimationStackInfo stack in scene.AnimationStacks
                     .OrderBy(static stack => stack.ObjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxAnimationStackActivity activity = activityById[stack.ObjectId];
            ImmutableArray<FbxAnimationCurveBinding> bindings = activity.Usable
                ? scene.ReadAnimationBindings(stack, cancellationToken)
                : [];
            FbxDeclaredTimebase timebase = scene.ResolveDeclaredTimebase(bindings, cancellationToken);
            long frameCount = EstimateFrameCount(stack, timebase.FrameRate);
            Guid clipId = CreateStableObjectGuid("animation-stack", stack.ObjectId, $"{modelId:N}:{stack.Name}");
            string clipFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"dlrmodel-clip-v1\0{sourceSha256}\0{stack.ObjectId}\0{stack.Name}\0{stack.StartTick}\0{stack.StopTick}\0{timebase.FrameRate.Numerator}/{timebase.FrameRate.Denominator}")))
                .ToLowerInvariant();
            bool included = activity.Usable && rig is not null;
            if (!activity.Usable)
            {
                diagnostics.Add(new CustomModelImportDiagnostic
                {
                    Code = "model_animation_stack_requires_bake",
                    Severity = CustomModelImportSeverity.Warning,
                    Subject = stack.Name,
                    Message = $"Animation stack '{stack.Name}' is retained but disabled: {activity.UnavailableReason}",
                });
            }
            else if (rig is null)
            {
                diagnostics.Add(new CustomModelImportDiagnostic
                {
                    Code = "model_animation_stack_without_rig",
                    Severity = CustomModelImportSeverity.Warning,
                    Subject = stack.Name,
                    Message = $"Animation stack '{stack.Name}' is retained as metadata, but static-prop mode has no playback rig.",
                });
            }

            AnimationClip? decoded = null;
            bool hasSkeletalTracks = activity.SkeletalBindingCount > 0;
            bool hasMorphTracks = false;
            if (included && options.DecodeAnimationClips)
            {
                try
                {
                    FbxCoreAnimationImportResult imported = FbxCoreAnimationAdapter.Import(
                        binary,
                        new FbxCoreAnimationImportOptions
                        {
                            RigId = rig!.Id,
                            RigDisplayName = rig.DisplayName,
                            AnimationStackName = stack.Name,
                            SamplingFrameRate = timebase.FrameRate,
                            MaximumSampleFrames = options.MaximumAnimationFramesPerStack,
                            MaximumSampledTransformKeys = options.MaximumSampledTransformKeysPerStack,
                            ProjectAffineShearToTrs = true,
                            SourceAssetFingerprint = rig.SourceAssetFingerprint,
                        },
                        cancellationToken);
                    if (!HasCompatibleRigOrder(rig, imported.Rig))
                    {
                        throw new InvalidDataException(
                            $"Animation stack '{stack.Name}' decoded a bone order incompatible with the imported model rig.");
                    }

                    decoded = imported.Clip;
                    if (!rig.MorphChannels.IsEmpty)
                    {
                        FbxFacialAnimationImportResult facial =
                            FbxFacialAnimationAdapter.Import(
                                binary,
                                new FbxFacialAnimationImportOptions
                                {
                                    AnimationStackName = stack.Name,
                                    SamplingFrameRate = timebase.FrameRate,
                                    DefaultSourceValueUnit =
                                        FbxFacialSourceValueUnit.Percent,
                                    MaximumChannels = options.MaximumMorphChannels,
                                    MaximumRawCurveKeys =
                                        options.MaximumSampledTransformKeysPerStack,
                                    MaximumSampleFrames =
                                        options.MaximumAnimationFramesPerStack,
                                    MaximumSampledScalarKeys =
                                        options.MaximumSampledTransformKeysPerStack,
                                },
                                cancellationToken);
                        HashSet<string> rigMorphNames = rig.MorphChannels
                            .Select(static morph => morph.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        HashSet<string> boundMorphNames = facial.Channels
                            .Where(static channel => channel.Binding is not null)
                            .Select(static channel => channel.Name)
                            .Where(rigMorphNames.Contains)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        ImmutableArray<ScalarTrack> selectedFacialTracks =
                            facial.Clip.ScalarTracks
                                .Where(track => boundMorphNames.Contains(
                                    track.ChannelName))
                                .ToImmutableArray();
                        hasMorphTracks = !selectedFacialTracks.IsEmpty;
                        if (hasMorphTracks)
                        {
                            var facialClip = new AnimationClip(
                                facial.Clip.Name,
                                facial.Clip.FrameRate,
                                facial.Clip.FrameCount,
                                scalarTracks: selectedFacialTracks);
                            decoded = AnimationClipSynchronization.Synchronize(
                                decoded,
                                facialClip);
                        }
                    }

                    frameCount = decoded.FrameCount;
                    clips.Add(clipId, decoded);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or InvalidOperationException or OverflowException or ArgumentException)
                {
                    included = false;
                    diagnostics.Add(new CustomModelImportDiagnostic
                    {
                        Code = "model_animation_stack_decode_failed",
                        Severity = CustomModelImportSeverity.Warning,
                        Subject = stack.Name,
                        Message = $"Animation stack '{stack.Name}' is retained but disabled because its clip could not be prepared: {exception.Message}",
                    });
                }
            }

            metadata.Add(new CustomModelAnimationClip
            {
                Id = clipId,
                FbxObjectId = stack.ObjectId,
                SourceName = stack.Name,
                DisplayName = stack.Name,
                Included = included,
                FrameRate = decoded?.FrameRate ?? timebase.FrameRate,
                StartFrame = 0,
                FrameCount = Math.Max(1, frameCount),
                RootMotionMode = Dl1RootMotionMode.Recorded,
                RootBoneName = rig?.Bones.FirstOrDefault(static bone => bone.ParentIndex < 0)?.Name,
                SourceFingerprint = clipFingerprint,
                HasSkeletalTracks = hasSkeletalTracks,
                HasMorphTracks = hasMorphTracks,
                FacialSourceValueUnit = "percent",
            });
        }

        return (metadata.ToImmutable(), clips.ToImmutable());
    }

    private static long EstimateFrameCount(FbxAnimationStackInfo stack, FrameRate frameRate)
    {
        long durationTicks = Math.Max(0, stack.StopTick - stack.StartTick);
        decimal frameSpan = (decimal)durationTicks * frameRate.Numerator /
            ((decimal)FbxBinaryDocument.TicksPerSecond * frameRate.Denominator);
        return checked(decimal.ToInt64(decimal.Round(frameSpan, 0, MidpointRounding.AwayFromZero)) + 1);
    }

    private static bool HasCompatibleRigOrder(RigDefinition expected, RigDefinition actual) =>
        expected.BoneCount == actual.BoneCount &&
        expected.Bones.Zip(actual.Bones).All(static pair =>
            pair.First.ParentIndex == pair.Second.ParentIndex &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal));

    private static Guid CreateStableObjectGuid(string kind, long objectId, string discriminator) =>
        CreateDeterministicGuid(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"dlrmodel-{kind}-v1\0{objectId}\0{discriminator}")));

    private static string ReadObjectName(FbxNode node, string label)
    {
        if (node.Properties.Length < 2)
        {
            throw new InvalidDataException($"{label} has no object name.");
        }

        string name = FbxBinaryDocument.CleanObjectName(
            FbxSemanticValues.ConvertString(node.Properties[1].Value));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException($"{label} has an empty object name.");
        }

        return name;
    }

    private static string? ReadFirstStringChild(FbxNode node, string childName)
    {
        FbxNode? child = node.FindChild(childName);
        if (child is null || child.Properties.IsEmpty)
        {
            return null;
        }

        string value = Convert.ToString(child.Properties[0].Value, CultureInfo.InvariantCulture) ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static HashSet<long> ReadWeightedBoneIds(
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objects,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<long>();
        foreach ((long objectId, FbxNode node) in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsObject(node, "Deformer", "Cluster"))
            {
                continue;
            }

            ImmutableArray<double> weights = FbxSemanticValues.ReadDoubleArray(
                node.FindChild("Weights"),
                $"Cluster {objectId} Weights");
            if (!weights.Any(static weight => weight > 1e-12))
            {
                continue;
            }

            foreach (FbxConnection connection in scene.GetChildren(objectId))
            {
                if (connection.Kind == "OO" &&
                    scene.Models.TryGetValue(connection.ChildId, out FbxModelObject? model) &&
                    model.IsLimb)
                {
                    result.Add(model.ObjectId);
                }
            }
        }

        return result;
    }

    private static CustomModelAxisSystem ReadAxisSystem(FbxSemanticScene scene) =>
        new()
        {
            UpAxis = FbxSemanticValues.TryGetInt32(scene.GlobalSettings, "UpAxis") ?? 1,
            UpAxisSign = FbxSemanticValues.TryGetInt32(scene.GlobalSettings, "UpAxisSign") ?? 1,
            FrontAxis = FbxSemanticValues.TryGetInt32(scene.GlobalSettings, "FrontAxis") ?? 2,
            FrontAxisSign = FbxSemanticValues.TryGetInt32(scene.GlobalSettings, "FrontAxisSign") ?? -1,
            CoordinateAxis = FbxSemanticValues.TryGetInt32(scene.GlobalSettings, "CoordAxis") ?? 0,
            CoordinateAxisSign = FbxSemanticValues.TryGetInt32(scene.GlobalSettings, "CoordAxisSign") ?? 1,
            UnitScaleFactor = FbxSemanticValues.GetDouble(scene.GlobalSettings, "UnitScaleFactor", 1.0),
        };

    private static Guid CreateDeterministicGuid(ReadOnlySpan<byte> hash)
    {
        Span<byte> bytes = stackalloc byte[16];
        hash[..16].CopyTo(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static string ComputeMorphSignature(
        ImmutableArray<CustomModelMorphChannel> channels,
        ImmutableArray<FbxModelSurface> surfaces)
    {
        if (channels.IsEmpty)
        {
            return CustomModelDocument.EmptyMorphSignature;
        }

        var canonical = new StringBuilder("dlra-custom-morph-v1\0");
        foreach (CustomModelMorphChannel channel in channels)
        {
            canonical.Append(channel.Index).Append('\0')
                .Append(channel.Name).Append('\0')
                .Append(channel.DescriptorHash.ToString("X8", CultureInfo.InvariantCulture)).Append('\0');
        }

        foreach (FbxModelSurface surface in surfaces)
        {
            canonical.Append(surface.Id).Append('\0');
            foreach (FbxModelMorphTarget target in surface.MorphTargets)
            {
                canonical.Append(target.DescriptorHash.ToString("X8", CultureInfo.InvariantCulture)).Append('\0');
                foreach (Vector3D delta in target.PositionDeltas)
                {
                    canonical.Append(delta.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(delta.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(delta.Z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                }

                canonical.Append('\0');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static bool IsObject(FbxNode node, string name, string? subtype = null)
    {
        if (!string.Equals(node.Name, name, StringComparison.Ordinal))
        {
            return false;
        }

        return subtype is null ||
            node.Properties.Length >= 3 &&
            string.Equals(
                Convert.ToString(node.Properties[2].Value, CultureInfo.InvariantCulture),
                subtype,
                StringComparison.Ordinal);
    }
}
