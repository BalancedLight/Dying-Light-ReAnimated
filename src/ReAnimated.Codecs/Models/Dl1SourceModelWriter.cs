using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1SourceModelBuildRequest
{
    public required FbxModelAuthoringImportResult Model { get; init; }

    public required string OutputDirectory { get; init; }

    public required string ResourceName { get; init; }

    public string SurfaceName { get; init; } = "default";

    /// <summary>
    /// Optional existing animation-script resource. The writer never guesses a
    /// stock script for an exact custom bind.
    /// </summary>
    public string? AnimationScriptAlias { get; init; }
    /// <summary>Explicit diagnostic output only; this never grants complete or game-ready status.</summary>
    public bool AllowIncompleteCharacterDiagnostics { get; init; }
}

public sealed record Dl1PreparedMorphTargetExpectation(string Name, ImmutableArray<Vector3D> PositionDeltas);
public sealed record Dl1PreparedMorphSurfaceExpectation(string NodeName, int LodIndex, int VertexCount,
    ImmutableArray<Dl1PreparedMorphTargetExpectation> MorphTargets);

public sealed record Dl1SourceModelBuildResult(
    string ResourceName,
    string SourceMshPath,
    string CharacterDefinitionPath,
    string BoneScriptPath,
    string? AnimationScriptPath,
    string ManifestPath,
    string BlockedOutputsPath,
    CustomModelBuildState State,
    ImmutableArray<string> CustomMaterialReferences,
    ImmutableArray<string> TextureSourceFiles,
    ImmutableDictionary<string, string> OutputSha256,
    ImmutableArray<string> BlockingReasons)
{
    public ImmutableArray<RigProfileDiagnostic> CapabilityDiagnostics { get; init; } = [];
    public ImmutableArray<string> NativeCompanionFiles { get; init; } = [];
    public ImmutableArray<string> NativeCompanionNotes { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<Dl1ResolvedBoneScriptPolicy> BoneScriptPolicies { get; init; }
    [JsonIgnore]
    public Dl1AuthoredRigContract? AuthoredRigContract { get; init; }
    [JsonIgnore]
    public ImmutableArray<Dl1PreparedSkinningSurfaceExpectation> PreparedSkinningExpectations { get; init; } = [];
    [JsonIgnore]
    public ImmutableArray<Dl1PreparedPhysicalNodeExpectation> PreparedPhysicalNodeExpectations { get; init; } = [];
    [JsonIgnore]
    public ImmutableArray<Dl1PreparedMorphSurfaceExpectation> PreparedMorphExpectations { get; init; } = [];
    public string? SkinDefinitionSourcePath { get; init; }
    public ImmutableArray<Dl1SkinGenerationDefinition> PreparedSkinDefinitions { get; init; } = [];
}

/// <summary>
/// Writes the documented Chrome source-MSH, structured CHR v4 character
/// definition, and companion scripts consumed by Techland's editor compiler.
/// Text .skn definitions are source sidecars, distinct from compact .skn/.msh_obj
/// products emitted by the official compiler.
/// </summary>
public static class Dl1SourceModelWriter
{
    private const uint MshMagic = 0x0048_534D;
    private const uint NodeMesh = 1;
    private const uint NodeSkinnedMesh = 2;
    private const uint NodeHelper = 4;
    private const uint NodeBone = 8;
    private const uint NodeAnimated = 1;
    private const int MaximumPhysicalNodes = 32_768;
    private const int MaximumPaletteEntries = 256;
    private const int MaximumVertices = 65_535;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<Dl1SourceModelBuildResult> WriteAsync(
        Dl1SourceModelBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        request.Model.Package.Document.Validate();
        CharacterActorSourceAuthoring.RevalidateAll(request.Model.Package);
        CharacterMaterialFallbackAuthoring.Revalidate(request.Model.Package);
        CharacterTextureFallbackAuthoring.Revalidate(request.Model.Package);
        CharacterFacialAssociationAuthoring.Revalidate(request.Model.Package);
        ValidateMorphInventory(request.Model);
        if (!request.AllowIncompleteCharacterDiagnostics)
        {
            var expressionBlockers = MorphAuthoringEvidence.ExportBlockers(request.Model).AddRange(CharacterBodyRegionAuthoring.ExportBlockers(request.Model));
            if (!expressionBlockers.IsEmpty)
                throw new InvalidDataException("Character expression export is blocked: " + string.Join("; ", expressionBlockers));
        }
        if (!request.AllowIncompleteCharacterDiagnostics && request.Model.Package.Document.CharacterResources is { } inventory && !inventory.IsDependencyComplete)
            throw new InvalidDataException("Complete character export is blocked: " + string.Join("; ", inventory.ExportBlockers));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurfaceName);

        string resourceName = SanitizeName(request.ResourceName, 55);
        string surfaceName = SanitizeName(request.SurfaceName, 63);
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var capabilityDiagnostics = FbxCapabilityProfileAuthoring.ValidateExport(request.Model, cancellationToken);
        PreparedSourceModel prepared = Prepare(request.Model, resourceName, surfaceName, cancellationToken);
        ImmutableArray<Dl1ResolvedBoneScriptPolicy> componentPolicies = request.Model.Package.Document.RiggingSession is not null && prepared.RigContract is not null
            ? Dl1BoneScriptPolicyResolver.Resolve(request.Model.Package.Document, prepared.RigContract) : default;
        Dl1NativeCompanionBuild companions = Dl1NativeCompanionWriter.BuildPreservedPackage(
            request.Model.Package, resourceName, prepared.BoneNames);
        byte[] msh = BuildMsh(prepared);
        ImmutableArray<Dl1PreparedPhysicalNodeExpectation> preparedPhysicalNodeExpectations =
            BuildPreparedPhysicalNodeExpectations(prepared);
        byte[] chr = BuildCharacterDefinition(request.Model.Package, BuildChrObjects(prepared));
        ImmutableArray<Dl1SkinGenerationDefinition> skins=Dl1CharacterSkinAuthoring.FromInventory(request.Model.Package.Document.CharacterResources,prepared.MaterialNames);
        string? skinSource=skins.IsEmpty?null:Dl1SkinDefinitionCodec.GenerateCanonical(skins).Write();
        string bscr = BuildBoneScript(prepared.BoneNames, componentPolicies);
        string? animationScriptAlias = string.IsNullOrWhiteSpace(request.AnimationScriptAlias)
            ? null
            : RequireExactResourceName(request.AnimationScriptAlias, 63, "animation script alias");
        string? ascr = animationScriptAlias is null
            ? null
            : $"AnimScriptAlias(\"{EscapeScriptString(AnimationScriptFileName(animationScriptAlias))}\")\n";
        string blocked =
            "DL ReAnimated generated bounded DL1 source-model compiler inputs.\r\n" +
            "The following requested products are intentionally not fabricated:\r\n" +
            "  - compiled binary .skn (the text skin sidecar is a distinct source input)\r\n" +
            "  - .msh_obj\r\n" +
            "Use Techland's matching Dying Light Developer Tools compiler to create its compact output.\r\n" +
            "A future built-in writer must pass installed-build structural and runtime validation first.\r\n";

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"{resourceName}.msh"] = msh,
            [$"{resourceName}.chr"] = chr,
            [$"{resourceName}.bscr"] = Encoding.UTF8.GetBytes(bscr),
            ["BLOCKED_OUTPUTS.txt"] = Encoding.UTF8.GetBytes(blocked),
        };
        if(skinSource is not null) files[$"{resourceName}.skn"]=Encoding.UTF8.GetBytes(skinSource);
        if (!componentPolicies.IsDefault)
            files[$"{resourceName}.components.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "dl-reanimated-bone-script-policies-v1",
                sourceFbxSha256 = request.Model.Package.Document.Source.ContentSha256,
                authoredContractId = prepared.RigContract!.ContractId,
                policies = componentPolicies,
                runtimeBehaviorVerified = false,
                note = "Portable component values and explicit source tokens; native ownership composition and runtime behavior require separate verification.",
            }, ManifestJsonOptions);
        foreach ((string relativePath, byte[] bytes) in prepared.MaterialFiles)
        {
            files.Add(relativePath, bytes);
        }
        foreach ((string relativePath, byte[] bytes) in companions.Files) files.Add(relativePath, bytes);

        if (ascr is not null)
        {
            files[$"{resourceName}.ascr"] = Encoding.UTF8.GetBytes(ascr);
        }

        ImmutableDictionary<string, string> hashes = files
            .ToImmutableDictionary(
                static pair => pair.Key,
                static pair => Sha256(pair.Value),
                StringComparer.Ordinal);
        var manifest = new
        {
            format = "dl-reanimated-csharp-model-build",
            schemaVersion = 1,
            resourceName,
            input = new
            {
                modelId = request.Model.Package.Document.ModelId,
                sourceFbxSha256 = request.Model.Package.Document.Source.ContentSha256,
                rigSignature = request.Model.Package.Document.RigSignature,
                authoredRig = prepared.RigContract is null
                    ? null
                    : new
                    {
                        prepared.RigContract.ContractId,
                        prepared.RigContract.SkeletonFingerprint,
                        prepared.RigContract.BindFingerprint,
                        prepared.RigContract.DescriptorFingerprint,
                        prepared.RigContract.MorphFingerprint,
                    },
            },
            outputContract = new
            {
                state = CustomModelBuildState.CompilerReady.ToString(),
                sourceMsh = "Chrome source MSH for the official Techland compiler",
                characterDefinition = "Structured DL1 CHR v4 with all original transform variants when available in exact physical object order",
                boneScript = componentPolicies.IsDefault ? "Per-entity POS/ROT, plus root SCL" : "Explicit per-entity studio component and LOD decisions",
                animationScript = ascr is null ? "not authored" : "explicit user-supplied alias",
                materials = "Techland DMT sources with user-owned diffuse/normal/specular DDS dependencies",
                morphTargets = "Chrome LOD 0x0104 records with fixed UTF-8 names and one float3 position delta per expanded draw vertex",
                boneFrames = request.Model.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary
                    ? "Original source bind frames preserved for the explicitly referenced existing animation bank; only declared secondary driven bones use Chrome +X frames"
                    : "Authored Chrome +X frames shared with authored animation conversion",
                unsupported = new[] { ".skn", ".msh_obj" },
            },
            capabilityProfile = new
            {
                reference = request.Model.Package.Document.RiggingSession?.Recipe.Profile,
                definitionPresent = request.Model.Package.Document.RiggingSession?.Recipe.ProfileSnapshot is not null,
                selectedCapabilities = request.Model.Package.Document.RiggingSession?.Recipe.SelectedCapabilityIds ?? [],
                diagnostics = capabilityDiagnostics,
                nativeBehaviorVerified = false,
            },
            counts = new
            {
                bones = prepared.BoneNames.Length,
                helpers = prepared.HelperCount,
                drawSurfaces = prepared.GeometryNodes.Sum(static node => node.Lods.Length),
                sourceLodGroups = request.Model.SourceLodGroups.Length,
                physicalNodes = prepared.Nodes.Length,
                characterObjects = prepared.Nodes.Length,
                materials = prepared.MaterialNames.Length,
                morphChannels = request.Model.Package.Document.MorphChannels.Length,
                morphSurfaceBindings = prepared.GeometryNodes.Sum(static node => node.Lods.Count(static lod => !lod.MorphTargets.IsEmpty)),
                materialSourceFiles = prepared.MaterialFiles.Count,
                nativeCompanionFiles = companions.Files.Count,
            },
            nativeCompanions = new { files = companions.Files.Keys.Order(StringComparer.Ordinal), notes = companions.Notes,
                compiledPhysicsBoundsValidated = false, runtimeValidated = false },
            outputs = hashes.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new { path = pair.Key, sha256 = pair.Value })
                .ToArray(),
        };
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions);
        files["model-build.json"] = manifestBytes;
        hashes = hashes.Add("model-build.json", Sha256(manifestBytes));

        string temporaryDirectory = Path.Combine(
            Path.GetDirectoryName(outputDirectory) ?? outputDirectory,
            $".{Path.GetFileName(outputDirectory)}.dlr-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach ((string relativePath, byte[] bytes) in files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(temporaryDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                await File.WriteAllBytesAsync(stagedPath, bytes, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(outputDirectory);
            foreach (string relativePath in files.Keys.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(temporaryDirectory, relativePath);
                string finalPath = Path.Combine(outputDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                File.Move(stagedPath, finalPath, overwrite: true);
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        return new Dl1SourceModelBuildResult(
            resourceName,
            Path.Combine(outputDirectory, $"{resourceName}.msh"),
            Path.Combine(outputDirectory, $"{resourceName}.chr"),
            Path.Combine(outputDirectory, $"{resourceName}.bscr"),
            ascr is null ? null : Path.Combine(outputDirectory, $"{resourceName}.ascr"),
            Path.Combine(outputDirectory, "model-build.json"),
            Path.Combine(outputDirectory, "BLOCKED_OUTPUTS.txt"),
            CustomModelBuildState.CompilerReady,
            prepared.MaterialFiles.Keys
                .Where(static path => string.Equals(
                    Path.GetExtension(path),
                    ".dmt",
                    StringComparison.OrdinalIgnoreCase))
                .Select(static path => $"{Path.GetFileNameWithoutExtension(path)}.mat")
                .Order(StringComparer.Ordinal)
                .ToImmutableArray(),
            prepared.MaterialFiles.Keys
                .Where(static path => string.Equals(
                    Path.GetExtension(path),
                    ".dds",
                    StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToImmutableArray(),
            hashes,
            [
                ".skn and .msh_obj require the matching official Techland compiler.",
                .. prepared.MaterialNotes,
            ])
        {
            CapabilityDiagnostics = capabilityDiagnostics,
            SkinDefinitionSourcePath=skinSource is null?null:Path.Combine(outputDirectory,$"{resourceName}.skn"),
            PreparedSkinDefinitions=skins,
            NativeCompanionFiles = companions.Files.Keys.Order(StringComparer.Ordinal).ToImmutableArray(),
            BoneScriptPolicies = componentPolicies,
            AuthoredRigContract = prepared.RigContract,
            PreparedSkinningExpectations = BuildCompiledSkinningExpectations(prepared),
            PreparedPhysicalNodeExpectations = preparedPhysicalNodeExpectations,
            PreparedMorphExpectations = prepared.GeometryNodes.SelectMany(node => node.Lods.Select((lod, index) =>
                new Dl1PreparedMorphSurfaceExpectation(node.Name, index, lod.Positions.Length,
                    lod.MorphTargets.Select(target => new Dl1PreparedMorphTargetExpectation(target.Name, target.PositionDeltas)).ToImmutableArray()))).ToImmutableArray(),
            NativeCompanionNotes = companions.Notes,
        };
    }

    private static ImmutableArray<Dl1PreparedPhysicalNodeExpectation> BuildPreparedPhysicalNodeExpectations(
        PreparedSourceModel model)
    {
        var expectations = ImmutableArray.CreateBuilder<Dl1PreparedPhysicalNodeExpectation>(
            model.Nodes.Length);
        for (int physicalIndex = 0; physicalIndex < model.Nodes.Length; physicalIndex++)
        {
            SourceNode node = model.Nodes[physicalIndex];
            expectations.Add(new(
                physicalIndex,
                node.Name,
                checked((short)node.ParentIndex),
                node.Type,
                Dl1PreparedPhysicalNodeReadBackValidator.MapSourceMshType(node.Type),
                ToCompactMatrix(node.LocalMatrix),
                ToCompactMatrix(node.ReferenceMatrix),
                new CompactBounds(
                    (float)node.Bounds.Center.X,
                    (float)node.Bounds.Center.Y,
                    (float)node.Bounds.Center.Z,
                    (float)node.Bounds.HalfExtents.X,
                    (float)node.Bounds.HalfExtents.Y,
                    (float)node.Bounds.HalfExtents.Z)));
        }

        return expectations.MoveToImmutable();
    }

    private static CompactMatrix3x4 ToCompactMatrix(TransformMatrix matrix) => new(
        (float)matrix.M11, (float)matrix.M12, (float)matrix.M13, (float)matrix.M14,
        (float)matrix.M21, (float)matrix.M22, (float)matrix.M23, (float)matrix.M24,
        (float)matrix.M31, (float)matrix.M32, (float)matrix.M33, (float)matrix.M34);

    private static ImmutableArray<Dl1PreparedSkinningSurfaceExpectation> BuildCompiledSkinningExpectations(
        PreparedSourceModel model)
    {
        var expectations = ImmutableArray.CreateBuilder<Dl1PreparedSkinningSurfaceExpectation>(
            model.GeometryNodes.Length);
        foreach (SourceNode node in model.GeometryNodes)
        {
            for (int lodIndex = 0; lodIndex < node.Lods.Length; lodIndex++)
            {
                SourceLod lod = node.Lods[lodIndex];
                bool isSkinned = node.Type == NodeSkinnedMesh;
                if (isSkinned && lod.Skin.Length != lod.Positions.Length ||
                    !isSkinned && !lod.Skin.IsEmpty)
                {
                    throw new InvalidDataException(
                        $"Prepared geometry node '{node.Name}' has inconsistent skin rows for compiled read-back.");
                }

                var vertices = ImmutableArray.CreateBuilder<Dl1PreparedSkinVertexExpectation>(
                    lod.Positions.Length);
                for (int vertexIndex = 0; vertexIndex < lod.Positions.Length; vertexIndex++)
                {
                    var influences = ImmutableArray.CreateBuilder<Dl1PreparedSkinInfluenceExpectation>();
                    if (isSkinned)
                    {
                        SkinVertex skin = lod.Skin[vertexIndex];
                        short[] quantized = QuantizeWeights(skin.Weights.AsSpan());
                        var byEntity = new Dictionary<int, double>();
                        for (int influenceIndex = 0; influenceIndex < quantized.Length; influenceIndex++)
                        {
                            if (quantized[influenceIndex] <= 0)
                                continue;
                            SourceSubset subset = lod.Subsets.Single(part => vertexIndex >= part.FirstVertex && vertexIndex < part.FirstVertex + part.VertexCount);
                            int localPaletteIndex = skin.BoneIndices[influenceIndex];
                            if ((uint)localPaletteIndex >= (uint)subset.Palette.Length)
                                throw new InvalidDataException(
                                    $"Prepared geometry node '{node.Name}' vertex {vertexIndex} has a source influence outside its physical palette.");
                            int entityIndex = subset.Palette[localPaletteIndex];
                            byEntity[entityIndex] = byEntity.GetValueOrDefault(entityIndex) +
                                (quantized[influenceIndex] / 32767.0);
                        }

                        foreach ((int entityIndex, double weight) in byEntity.OrderBy(static pair => pair.Key))
                            influences.Add(new(entityIndex, weight));
                    }

                    Vector3D position = lod.Positions[vertexIndex];
                    vertices.Add(new(
                        new Vector3((float)position.X, (float)position.Y, (float)position.Z),
                        influences.ToImmutable())
                    {
                        Normal = new Vector3((float)lod.Normals[vertexIndex].X, (float)lod.Normals[vertexIndex].Y, (float)lod.Normals[vertexIndex].Z),
                        TextureCoordinate0 = new Vector2((float)lod.Uvs[vertexIndex].U, (float)lod.Uvs[vertexIndex].V),
                    });
                }

                expectations.Add(new(
                    node.Name,
                    LodIndex: lodIndex,
                    isSkinned,
                    lod.Positions.Length,
                    lod.Indices.Select(checkedIndex => checked((int)checkedIndex)).Distinct().Order().ToImmutableArray(),
                    lod.Subsets.Select(subset => new Dl1PreparedSkinSubsetExpectation(subset.Palette, subset.IndexCount)
                    { DeclaredMaterialSlotIndex = checked((ushort)subset.MaterialIndex), DeclaredMaterialReference = model.MaterialNames[subset.MaterialIndex], FirstIndex = subset.FirstIndex }).ToImmutableArray(),
                    vertices.ToImmutable()) { IndexBuffer = lod.Indices });
            }
        }
        return expectations.ToImmutable();
    }

    private static ImmutableArray<Dl1ChrV4ObjectTransform> BuildChrObjects(
        PreparedSourceModel model)
    {
        var objects = ImmutableArray.CreateBuilder<Dl1ChrV4ObjectTransform>(model.Nodes.Length);
        for (int index = 0; index < model.Nodes.Length; index++)
        {
            SourceNode node = model.Nodes[index];
            if (!node.LocalMatrix.IsFinite)
            {
                throw new InvalidDataException(
                    $"Source node '{node.Name}' has a non-finite local transform for CHR output.");
            }

            objects.Add(new Dl1ChrV4ObjectTransform(node.Name, node.LocalMatrix));
        }

        return objects.MoveToImmutable();
    }

    private static void ValidateMorphInventory(
        FbxModelAuthoringImportResult model)
    {
        Dictionary<string, CustomModelMorphChannel> inventory =
            model.Package.Document.MorphChannels.ToDictionary(
                static channel => channel.Name,
                StringComparer.Ordinal);
        var emittedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (FbxModelSurface surface in model.Surfaces)
        {
            var surfaceNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (FbxModelMorphTarget target in surface.MorphTargets)
            {
                if (!surfaceNames.Add(target.Name) ||
                    !inventory.TryGetValue(
                        target.Name,
                        out CustomModelMorphChannel? channel) ||
                    channel.DescriptorHash != target.DescriptorHash)
                {
                    throw new InvalidDataException(
                        $"Surface '{surface.Id}' morph target '{target.Name}' does not match the schema-2 morph inventory.");
                }

                emittedNames.Add(target.Name);
            }
        }

        string[] missing = inventory.Keys
            .Where(name => !emittedNames.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException(
                $"Schema-2 morph channel(s) have no expanded draw-vertex payload: {string.Join(", ", missing)}.");
        }
    }

    private static byte[] BuildCharacterDefinition(CustomModelPackage package, IEnumerable<Dl1ChrV4ObjectTransform> objects)
    {
        var ordered = objects.ToImmutableArray();
        var records = package.Document.CharacterResources?.Resources.Where(r => !r.IsOriginalArchive && r.EntryPath is not null &&
            Path.GetExtension(r.LogicalName).Equals(".chr", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (records.Length > 1) throw new InvalidDataException("Multiple character definitions need an explicit selected output definition.");
        if (records.Length == 0) return Dl1ChrV4Codec.Build(Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant(ordered,"default"));
        var original = Dl1ChrV4Codec.Parse(package.CompanionPayloads[records[0].EntryPath!].AsSpan());
        var indexes = original.ObjectNames.Select((name,index) => (name,index)).ToDictionary(r=>r.name,r=>r.index,StringComparer.Ordinal);
        if (original.ObjectNames.Any(name=>!ordered.Any(o=>o.Name==name))) throw new InvalidDataException("Original CHR object references are missing from the emitted hierarchy.");
        return Dl1ChrV4Codec.Build(new(ordered.Select(o=>o.Name).ToImmutableArray(), original.Variants.Select(v=>v with
            { ObjectTransforms = ordered.Select(o=>indexes.TryGetValue(o.Name,out int index)?v.ObjectTransforms[index]:o.LocalTransform).ToImmutableArray() }).ToImmutableArray()));
    }

    private static PreparedSourceModel Prepare(
        FbxModelAuthoringImportResult model,
        string resourceName,
        string surfaceName,
        CancellationToken cancellationToken)
    {
        CustomModelDocument document = model.Package.Document;
        ImmutableArray<CustomModelBone> sourceBones = document.CreateEffectiveBones();
        ValidateUniqueBoneNames(sourceBones);
        Dl1PreparedAuthoredRig? authoredRig = sourceBones.IsEmpty
            ? null
            : Dl1CustomModelRigPreparer.Prepare(model, cancellationToken);
        var nodes = ImmutableArray.CreateBuilder<SourceNode>();
        var boneNames = ImmutableArray.CreateBuilder<string>();
        int helperCount = 0;
        foreach (Dl1AuthoredRigNode node in authoredRig?.Contract.Nodes ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isHelper = !node.IsDeform;
            if (isHelper)
            {
                helperCount++;
            }

            nodes.Add(new SourceNode(
                node.Name,
                isHelper ? NodeHelper : NodeBone,
                node.ParentPhysicalIndex,
                node.LocalBindMatrix,
                node.InverseGlobalReferenceMatrix,
                new Bounds(node.Bounds.Center, node.Bounds.HalfExtents),
                NodeAnimated,
                []));
            boneNames.Add(node.Name);
        }

        ImmutableArray<CustomModelMaterial> documentMaterials = document.Materials;
        var materialIndexById = ImmutableDictionary.CreateBuilder<Guid, int>();
        for (int index = 0; index < documentMaterials.Length; index++)
        {
            CustomModelMaterial material = documentMaterials[index];
            materialIndexById[material.Id] = index;
        }

        Dl1PreparedMaterialSet materials = Dl1CustomMaterialWriter.Prepare(
            model.Package,
            resourceName,
            cancellationToken);

        ImmutableArray<FbxModelLodNodeLayout> layout = FbxModelLodLayout.Create(model);
        var geometryNodes = ImmutableArray.CreateBuilder<SourceNode>(layout.Length);
        var usedNames = nodes.Select(static node => node.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int nodeIndex = 0; nodeIndex < layout.Length; nodeIndex++)
        {
            FbxModelLodNodeLayout sourceNode = layout[nodeIndex];
            var lods = ImmutableArray.CreateBuilder<SourceLod>(sourceNode.Levels.Length);
            foreach (FbxModelLodLevelLayout level in sourceNode.Levels)
            {
                var draws = ImmutableArray.CreateBuilder<SourceLod>();
                foreach (int surfaceIndex in level.SurfaceIndexes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FbxModelSurface surface = model.Surfaces[surfaceIndex];
                    if (surface.Vertices.IsEmpty || surface.Indices.IsEmpty || surface.Indices.Length % 3 != 0)
                    {
                        throw new InvalidDataException($"Surface '{surface.Id}' has no complete triangle geometry.");
                    }

                    if (surface.Vertices.Length > MaximumVertices || surface.Indices.Any(index => index >= surface.Vertices.Length))
                    {
                        throw new InvalidDataException($"Surface '{surface.Id}' exceeds the source-MSH uint16 vertex contract.");
                    }

                    if (surface.PaletteBoneIndices.Length > MaximumPaletteEntries)
                    {
                        throw new InvalidDataException($"Surface '{surface.Id}' exceeds the 256-entry skin-palette contract.");
                    }

                    int materialIndex = materialIndexById.TryGetValue(surface.MaterialId, out int resolvedMaterial) ? resolvedMaterial : 0;
                    ImmutableArray<int> physicalPalette = authoredRig is null
                        ? surface.PaletteBoneIndices.IsEmpty ? [] : throw new InvalidDataException($"Surface '{surface.Id}' has a skin palette without an authored rig.")
                        : authoredRig.Surfaces[surfaceIndex].PhysicalPalette;
                    draws.Add(BuildLod(surface, materialIndex, physicalPalette, document.BuildSettings.FlipTextureCoordinateV));
                }
                lods.Add(CombineLodDraws(draws.ToImmutable(), sourceNode.Name, level.LodIndex));
            }
            bool skinned = model.Surfaces[sourceNode.Levels[0].SurfaceIndexes[0]].IsSkinned;
            string nodeName;
            if (document.Source.Kind == CustomModelSourceKind.StockCharacter)
            {
                nodeName = RequireExactResourceName(sourceNode.Name, 63, "original mesh entity");
                if (!usedNames.Add(nodeName)) throw new InvalidDataException($"Original mesh entity '{nodeName}' conflicts with another output entity; rename requires a reviewed reference transaction.");
            }
            else nodeName = UniqueName(SanitizeName($"{resourceName}_{sourceNode.Name}_p{nodeIndex:00}", 63), usedNames);
            geometryNodes.Add(new SourceNode(nodeName, skinned ? NodeSkinnedMesh : NodeMesh,
                -1, TransformMatrix.Identity, TransformMatrix.Identity,
                ComputeBounds(lods.SelectMany(static lod => lod.Positions)),
                skinned ? NodeAnimated : 0, lods.ToImmutable()));
        }

        foreach (SourceNode geometryNode in geometryNodes)
        {
            nodes.Add(geometryNode);
        }

        IEnumerable<Vector3D> allPositions = model.Surfaces.SelectMany(static surface => surface.Vertices)
            .Select(static vertex => vertex.Position);
        Bounds modelBounds = ComputeBounds(allPositions, minimumHalfExtent: 0.005);
        nodes.Add(new SourceNode(
            UniqueName(SanitizeName($"{resourceName}_bounds", 63), usedNames),
            NodeMesh,
            -1,
            TransformMatrix.Identity,
            TransformMatrix.Identity,
            modelBounds,
            0,
            []));

        if (nodes.Count == 0 || nodes.Count > MaximumPhysicalNodes)
        {
            throw new InvalidDataException($"Source model contains {nodes.Count:N0} physical nodes; the signed parent index supports 1..{MaximumPhysicalNodes:N0}.");
        }

        ImmutableArray<SourceNode> nodeArray = nodes.ToImmutable();
        ValidateDepthFirstOrder(nodeArray);
        return new PreparedSourceModel(
            materials.MaterialReferences,
            materials.Files,
            materials.Notes,
            [surfaceName],
            nodeArray,
            boneNames.ToImmutable(),
            helperCount,
            geometryNodes.ToImmutable(),
            authoredRig?.Contract);
    }

    private static SourceLod BuildLod(
        FbxModelSurface surface,
        int materialIndex,
        ImmutableArray<int> physicalPalette,
        bool flipTextureCoordinateV)
    {
        for (int index = 0; index < surface.Vertices.Length; index++)
        {
            Vector3D normal = surface.Vertices[index].Normal;
            if (!normal.IsFinite || normal.LengthSquared <= 1e-16)
                throw new InvalidDataException(
                    $"Surface '{surface.Id}' vertex {index} has a missing or degenerate base normal. " +
                    "Review authored normal edits before native output.");
        }
        ImmutableArray<Vector3D> positions = surface.Vertices.Select(static vertex => vertex.Position).ToImmutableArray();
        ImmutableArray<Vector3D> normals = surface.Vertices.Select(static vertex => vertex.Normal.Normalized()).ToImmutableArray();
        ImmutableArray<(double U, double V)> uvs = surface.Vertices
            .Select(vertex => (
                vertex.TextureCoordinateU,
                ConvertTextureCoordinateV(vertex.TextureCoordinateV, flipTextureCoordinateV)))
            .ToImmutableArray();
        (ImmutableArray<Vector3D> tangents, ImmutableArray<Vector3D> bitangents) =
            ComputeTangentBasis(positions, normals, uvs, surface.Indices);
        for (int index = 0; index < normals.Length; index++)
        {
            if (!normals[index].IsFinite || normals[index].LengthSquared <= 1e-16 ||
                !tangents[index].IsFinite || tangents[index].LengthSquared <= 1e-16 ||
                !bitangents[index].IsFinite || bitangents[index].LengthSquared <= 1e-16)
                throw new InvalidDataException(
                    $"Surface '{surface.Id}' vertex {index} has no finite normal/tangent basis. " +
                    "Review the normals, triangles and UVs before native output.");
        }
        ImmutableArray<SkinVertex> skin = surface.IsSkinned
            ? surface.Vertices.Select((vertex, index) =>
            {
                if (vertex.BoneIndices.Length != vertex.BoneWeights.Length ||
                    vertex.BoneIndices.IsEmpty ||
                    vertex.BoneIndices.Length > 4 ||
                    vertex.BoneIndices.Any(local => local < 0 || local >= physicalPalette.Length) ||
                    vertex.BoneWeights.Any(static weight => !double.IsFinite(weight) || weight < 0.0) ||
                    vertex.BoneWeights.Sum() <= 0.0)
                {
                    throw new InvalidDataException($"Surface '{surface.Id}' vertex {index} has invalid normalized skin data.");
                }

                return new SkinVertex(vertex.BoneIndices, vertex.BoneWeights);
            }).ToImmutableArray()
            : [];
        return new SourceLod(
            positions,
            normals,
            tangents,
            bitangents,
            uvs,
            surface.Indices,
            [new SourceSubset(materialIndex, 0, surface.Indices.Length, physicalPalette, 0, positions.Length)],
            skin,
            surface.MorphTargets.Select(target =>
            {
                if (target.PositionDeltas.Length != surface.Vertices.Length ||
                    target.PositionDeltas.Any(static delta => !delta.IsFinite))
                {
                    throw new InvalidDataException(
                        $"Morph target '{target.Name}' does not match surface '{surface.Id}'s expanded vertex buffer.");
                }

                for (int vertexIndex = 0;
                     vertexIndex < target.PositionDeltas.Length;
                     vertexIndex++)
                {
                    ValidateMorphHalfRange(
                        target.PositionDeltas[vertexIndex],
                        target.Name,
                        vertexIndex);
                }

                return new SourceMorphTarget(
                    target.Name,
                    target.PositionDeltas);
            }).ToImmutableArray());
    }

    private static SourceLod CombineLodDraws(ImmutableArray<SourceLod> draws, string nodeName, int lodIndex)
    {
        if (draws.Length == 1) return draws[0];
        int vertexCount = checked(draws.Sum(static draw => draw.Positions.Length));
        if (vertexCount > MaximumVertices)
            throw new InvalidDataException($"LOD group '{nodeName}' level {lodIndex} has {vertexCount} expanded vertices; native uint16 indices support at most {MaximumVertices}. No level was discarded.");
        var subsets = ImmutableArray.CreateBuilder<SourceSubset>();
        var indices = ImmutableArray.CreateBuilder<uint>();
        int vertexOffset = 0;
        int indexOffset = 0;
        foreach (SourceLod draw in draws)
        {
            subsets.AddRange(draw.Subsets.Select(subset => subset with {
                FirstIndex = checked(subset.FirstIndex + indexOffset), FirstVertex = checked(subset.FirstVertex + vertexOffset) }));
            indices.AddRange(draw.Indices.Select(index => checked(index + (uint)vertexOffset)));
            vertexOffset = checked(vertexOffset + draw.Positions.Length);
            indexOffset = checked(indexOffset + draw.Indices.Length);
        }
        string[] targetNames = draws.SelectMany(static draw => draw.MorphTargets).Select(static target => target.Name)
            .Distinct(StringComparer.Ordinal).ToArray();
        var targets = targetNames.Select(name => new SourceMorphTarget(name,
            draws.SelectMany(draw => draw.MorphTargets.SingleOrDefault(target => target.Name == name)?.PositionDeltas
                ?? Enumerable.Repeat(Vector3D.Zero, draw.Positions.Length).ToImmutableArray()).ToImmutableArray())).ToImmutableArray();
        return new SourceLod(draws.SelectMany(static draw => draw.Positions).ToImmutableArray(),
            draws.SelectMany(static draw => draw.Normals).ToImmutableArray(),
            draws.SelectMany(static draw => draw.Tangents).ToImmutableArray(),
            draws.SelectMany(static draw => draw.Bitangents).ToImmutableArray(),
            draws.SelectMany(static draw => draw.Uvs).ToImmutableArray(), indices.ToImmutable(), subsets.ToImmutable(),
            draws.SelectMany(static draw => draw.Skin).ToImmutableArray(), targets);
    }

    private static byte[] BuildMsh(PreparedSourceModel model)
    {
        ImmutableArray<int> descendantCounts = ComputeDescendantCounts(model.Nodes);
        var children = new List<byte[]>
        {
            PackChunk(0x500, JoinFixedNames(model.MaterialNames)),
            PackChunk(0x700, JoinFixedNames(model.SurfaceNames)),
        };
        for (int index = 0; index < model.Nodes.Length; index++)
        {
            children.Add(BuildNodeChunk(model.Nodes[index], descendantCounts[index]));
        }

        byte[] rootPayload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(rootPayload.AsSpan(0, 4), checked((uint)model.Nodes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(rootPayload.AsSpan(4, 4), checked((uint)model.MaterialNames.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(rootPayload.AsSpan(8, 4), checked((uint)model.SurfaceNames.Length));
        return PackChunk(MshMagic, rootPayload, children);
    }

    private static byte[] BuildNodeChunk(SourceNode node, int descendantCount)
    {
        byte[] payload = new byte[0xD0];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), node.Type);
        FixedName(node.Name).CopyTo(payload.AsSpan(4, 64));
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(68, 2), checked((short)node.ParentIndex));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(70, 2), checked((ushort)descendantCount));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(72, 4), checked((uint)node.Lods.Length));
        WriteMatrix3X4(payload.AsSpan(76, 48), node.LocalMatrix);
        WriteMatrix3X4(payload.AsSpan(124, 48), node.ReferenceMatrix);
        WriteSingle(payload.AsSpan(172, 4), node.Bounds.Center.X);
        WriteSingle(payload.AsSpan(176, 4), node.Bounds.Center.Y);
        WriteSingle(payload.AsSpan(180, 4), node.Bounds.Center.Z);
        WriteSingle(payload.AsSpan(184, 4), node.Bounds.HalfExtents.X);
        WriteSingle(payload.AsSpan(188, 4), node.Bounds.HalfExtents.Y);
        WriteSingle(payload.AsSpan(192, 4), node.Bounds.HalfExtents.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(196, 4), node.Flags);
        return PackChunk(0x0003, payload, node.Lods.Select(BuildLodChunk).ToArray());
    }

    private static byte[] BuildLodChunk(SourceLod lod)
    {
        byte[] vertexFormat = new byte[60];
        int offset = 0;
        WriteUInt(vertexFormat, ref offset, 2);
        WriteFloat(vertexFormat, ref offset, 0); WriteFloat(vertexFormat, ref offset, 0);
        WriteFloat(vertexFormat, ref offset, 0); WriteFloat(vertexFormat, ref offset, 1);
        WriteUInt(vertexFormat, ref offset, 12); WriteUInt(vertexFormat, ref offset, 2);
        WriteFloat(vertexFormat, ref offset, 1); WriteUInt(vertexFormat, ref offset, 12);
        WriteUInt(vertexFormat, ref offset, 2); WriteFloat(vertexFormat, ref offset, 1);
        WriteUInt(vertexFormat, ref offset, 12); WriteUInt(vertexFormat, ref offset, 3);
        WriteFloat(vertexFormat, ref offset, 1); WriteUInt(vertexFormat, ref offset, 8);

        var children = new List<byte[]>
        {
            PackChunk(0x160, vertexFormat),
            PackChunk(0x101, PackVector3(lod.Positions)),
        };
        // Preserve the source writer's observed stream order: morph deltas
        // directly follow the base position stream.
        if (!lod.MorphTargets.IsEmpty)
        {
            children.Add(BuildMorphTargetsChunk(lod));
        }

        children.Add(PackChunk(0x102, PackVector3(lod.Normals)));
        children.Add(PackChunk(0x103, PackVector3(lod.Tangents)));
        children.Add(PackChunk(0x195, PackVector3(lod.Bitangents)));
        children.Add(PackChunk(0x120, PackVector2(lod.Uvs)));
        if (!lod.Skin.IsEmpty)
        {
            children.Add(BuildSkinChunk(lod.Skin));
        }

        byte[] indices = new byte[lod.Indices.Length * 2];
        for (int index = 0; index < lod.Indices.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(index * 2, 2), checked((ushort)lod.Indices[index]));
        }

        children.Add(PackChunk(0x140, indices));
        var subsetRecords = new List<byte[]>();
        foreach (SourceSubset part in lod.Subsets)
        {
            byte[] subset = new byte[12 + (part.Palette.Length * 2)];
            BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(0, 2), checked((ushort)part.MaterialIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(subset.AsSpan(2, 4), checked((uint)part.FirstIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(subset.AsSpan(6, 4), checked((uint)part.IndexCount));
            BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(10, 2), checked((ushort)part.Palette.Length));
            for (int index = 0; index < part.Palette.Length; index++)
                BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(12 + (index * 2), 2), checked((ushort)part.Palette[index]));
            subsetRecords.Add(subset);
        }
        // One packed table holds the variable-length material/palette records
        // for all subsets. Repeating the chunk loses the declared table count.
        children.Add(PackChunk(0x151, subsetRecords.SelectMany(static record => record).ToArray()));
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)lod.Positions.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)lod.Indices.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), checked((uint)lod.Subsets.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(12, 4),
            checked((uint)lod.MorphTargets.Length));
        return PackChunk(0x100, payload, children);
    }

    private static byte[] BuildMorphTargetsChunk(SourceLod lod)
    {
        int targetStride = checked(64 + (lod.Positions.Length * 12));
        byte[] payload = new byte[checked(lod.MorphTargets.Length * targetStride)];
        int offset = 0;
        foreach (SourceMorphTarget target in lod.MorphTargets)
        {
            FixedName(target.Name).CopyTo(payload.AsSpan(offset, 64));
            offset += 64;
            byte[] deltas = PackVector3(target.PositionDeltas);
            deltas.CopyTo(payload, offset);
            offset += deltas.Length;
        }

        return PackChunk(0x104, payload);
    }

    private static void ValidateMorphHalfRange(
        Vector3D delta,
        string targetName,
        int vertexIndex)
    {
        static bool IsFiniteHalf(double value)
        {
            float sourceFloat = (float)value;
            return float.IsFinite(sourceFloat) &&
                   Half.IsFinite((Half)sourceFloat);
        }

        if (!IsFiniteHalf(delta.X) ||
            !IsFiniteHalf(delta.Y) ||
            !IsFiniteHalf(delta.Z))
        {
            throw new InvalidDataException(
                $"Morph target '{targetName}' vertex {vertexIndex} exceeds DL1 PC's finite HALF4 morph-delta range.");
        }
    }

    private static byte[] BuildSkinChunk(ImmutableArray<SkinVertex> skin)
    {
        int influenceCount = skin.Max(static row => row.BoneIndices.Length);
        if (influenceCount >= 4)
        {
            byte[] payload = new byte[skin.Length * 12];
            for (int rowIndex = 0; rowIndex < skin.Length; rowIndex++)
            {
                SkinVertex row = skin[rowIndex];
                int[] indices = row.BoneIndices.ToArray();
                double[] weights = row.Weights.ToArray();
                while (indices.Length < 4)
                {
                    Array.Resize(ref indices, indices.Length + 1);
                    Array.Resize(ref weights, weights.Length + 1);
                    indices[^1] = indices.Length > 1 ? indices[^2] : 0;
                }

                short[] quantized = QuantizeWeights(weights.AsSpan(0, 4));
                int baseOffset = rowIndex * 12;
                for (int influence = 0; influence < 4; influence++)
                {
                    payload[baseOffset + influence] = checked((byte)indices[influence]);
                    BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(baseOffset + 4 + (influence * 2), 2), quantized[influence]);
                }
            }

            return PackChunk(0x130, payload);
        }

        int rowSize = influenceCount + (influenceCount > 1 ? influenceCount * 2 : 0);
        byte[] compact = new byte[1 + (skin.Length * rowSize)];
        compact[0] = checked((byte)influenceCount);
        int offset = 1;
        foreach (SkinVertex row in skin)
        {
            int[] indices = row.BoneIndices.ToArray();
            double[] weights = row.Weights.ToArray();
            while (indices.Length < influenceCount)
            {
                Array.Resize(ref indices, indices.Length + 1);
                Array.Resize(ref weights, weights.Length + 1);
                indices[^1] = indices.Length > 1 ? indices[^2] : 0;
            }

            short[] quantized = QuantizeWeights(weights.AsSpan(0, influenceCount));
            for (int influence = 0; influence < influenceCount; influence++)
            {
                compact[offset++] = checked((byte)indices[influence]);
            }

            if (influenceCount > 1)
            {
                for (int influence = 0; influence < influenceCount; influence++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(compact.AsSpan(offset, 2), quantized[influence]);
                    offset += 2;
                }
            }
            else if (quantized[0] != 0x7FFF)
            {
                throw new InvalidDataException("One-influence source-MSH skin rows must quantize to full weight.");
            }
        }

        return PackChunk(0x131, compact);
    }

    private static short[] QuantizeWeights(ReadOnlySpan<double> weights)
    {
        return Dl1SkinWeightQuantization.Encode(weights);
    }

    private static (ImmutableArray<Vector3D> Tangents, ImmutableArray<Vector3D> Bitangents) ComputeTangentBasis(
        ImmutableArray<Vector3D> positions,
        ImmutableArray<Vector3D> normals,
        ImmutableArray<(double U, double V)> uvs,
        ImmutableArray<uint> indices)
    {
        var tangents = new Vector3D[positions.Length];
        var bitangents = new Vector3D[positions.Length];
        for (int offset = 0; offset < indices.Length; offset += 3)
        {
            int a = checked((int)indices[offset]);
            int b = checked((int)indices[offset + 1]);
            int c = checked((int)indices[offset + 2]);
            Vector3D edge1 = positions[b] - positions[a];
            Vector3D edge2 = positions[c] - positions[a];
            double du1 = uvs[b].U - uvs[a].U;
            double dv1 = uvs[b].V - uvs[a].V;
            double du2 = uvs[c].U - uvs[a].U;
            double dv2 = uvs[c].V - uvs[a].V;
            double determinant = (du1 * dv2) - (dv1 * du2);
            Vector3D tangent;
            Vector3D bitangent;
            if (Math.Abs(determinant) > 1e-12)
            {
                double inverse = 1.0 / determinant;
                tangent = ((edge1 * dv2) - (edge2 * dv1)) * inverse;
                bitangent = ((edge2 * du1) - (edge1 * du2)) * inverse;
            }
            else
            {
                tangent = Perpendicular(normals[a]);
                bitangent = Vector3D.Cross(normals[a], tangent);
            }

            foreach (int index in new[] { a, b, c })
            {
                tangents[index] = tangents[index] + tangent;
                bitangents[index] = bitangents[index] + bitangent;
            }
        }

        for (int index = 0; index < tangents.Length; index++)
        {
            Vector3D normal = normals[index].Normalized();
            Vector3D tangent = tangents[index] - (normal * Vector3D.Dot(normal, tangents[index]));
            if (tangent.Length <= 1e-12)
            {
                tangent = Perpendicular(normal);
            }

            tangent = tangent.Normalized();
            Vector3D bitangent = Vector3D.Cross(normal, tangent).Normalized();
            if (Vector3D.Dot(bitangent, bitangents[index]) < 0)
            {
                bitangent = bitangent * -1;
            }

            tangents[index] = tangent;
            bitangents[index] = bitangent;
        }

        return (tangents.ToImmutableArray(), bitangents.ToImmutableArray());
    }

    private static Vector3D Perpendicular(Vector3D normal)
    {
        Vector3D axis = Math.Abs(normal.X) < 0.8 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        return Vector3D.Cross(normal, axis).Normalized();
    }

    private static void ValidateUniqueBoneNames(ImmutableArray<CustomModelBone> bones)
    {
        string[] duplicates = bones.GroupBy(static bone => bone.Name, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidDataException($"Chrome animation entities require unique bone/helper names; duplicates: {string.Join(", ", duplicates.Take(8))}.");
        }

        string[] tooLong = bones.Where(static bone => Encoding.UTF8.GetByteCount(bone.Name) > 63)
            .Select(static bone => bone.Name)
            .ToArray();
        if (tooLong.Length > 0)
        {
            throw new InvalidDataException($"Chrome source-MSH bone/helper names are limited to 63 UTF-8 bytes: {string.Join(", ", tooLong.Take(8))}.");
        }
    }

    private static void ValidateDepthFirstOrder(ImmutableArray<SourceNode> nodes)
    {
        var children = Enumerable.Range(0, nodes.Length).Select(static _ => new List<int>()).ToArray();
        var roots = new List<int>();
        for (int index = 0; index < nodes.Length; index++)
        {
            int parent = nodes[index].ParentIndex;
            if (parent < 0)
            {
                roots.Add(index);
            }
            else
            {
                if (parent >= index)
                {
                    throw new InvalidDataException($"Source node {index} parent {parent} must precede it.");
                }

                children[parent].Add(index);
            }
        }

        var order = new List<int>(nodes.Length);
        void Visit(int index)
        {
            order.Add(index);
            foreach (int child in children[index]) Visit(child);
        }

        foreach (int root in roots) Visit(root);
        if (!order.SequenceEqual(Enumerable.Range(0, nodes.Length)))
        {
            throw new InvalidDataException("Source nodes are not in Chrome depth-first pre-order.");
        }
    }

    private static ImmutableArray<int> ComputeDescendantCounts(ImmutableArray<SourceNode> nodes)
    {
        var children = Enumerable.Range(0, nodes.Length).Select(static _ => new List<int>()).ToArray();
        for (int index = 0; index < nodes.Length; index++)
        {
            if (nodes[index].ParentIndex >= 0) children[nodes[index].ParentIndex].Add(index);
        }

        var counts = new int[nodes.Length];
        int Count(int index)
        {
            int result = children[index].Sum(child => 1 + Count(child));
            if (result > ushort.MaxValue) throw new InvalidDataException("Source-MSH subtree exceeds the uint16 descendant-count field.");
            return counts[index] = result;
        }

        for (int index = 0; index < nodes.Length; index++) Count(index);
        return counts.ToImmutableArray();
    }

    private static Bounds ComputeBounds(IEnumerable<Vector3D> positions, double minimumHalfExtent = 0)
    {
        using IEnumerator<Vector3D> enumerator = positions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return Bounds.Zero;
        }

        Vector3D minimum = enumerator.Current;
        Vector3D maximum = enumerator.Current;
        while (enumerator.MoveNext())
        {
            Vector3D value = enumerator.Current;
            minimum = new Vector3D(Math.Min(minimum.X, value.X), Math.Min(minimum.Y, value.Y), Math.Min(minimum.Z, value.Z));
            maximum = new Vector3D(Math.Max(maximum.X, value.X), Math.Max(maximum.Y, value.Y), Math.Max(maximum.Z, value.Z));
        }

        Vector3D center = (minimum + maximum) * 0.5;
        Vector3D half = (maximum - minimum) * 0.5;
        half = new Vector3D(Math.Max(half.X, minimumHalfExtent), Math.Max(half.Y, minimumHalfExtent), Math.Max(half.Z, minimumHalfExtent));
        return new Bounds(center, half);
    }

    private static byte[] PackChunk(uint id, ReadOnlySpan<byte> payload, IEnumerable<byte[]>? children = null)
    {
        byte[][] childArray = children?.ToArray() ?? [];
        int childSize = childArray.Sum(static child => child.Length);
        int totalSize = checked(16 + payload.Length + childSize);
        byte[] result = new byte[totalSize];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), checked((uint)totalSize));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), checked((uint)payload.Length));
        payload.CopyTo(result.AsSpan(16));
        int offset = 16 + payload.Length;
        foreach (byte[] child in childArray)
        {
            child.CopyTo(result, offset);
            offset += child.Length;
        }

        return result;
    }

    private static byte[] JoinFixedNames(ImmutableArray<string> names)
    {
        byte[] result = new byte[names.Length * 64];
        for (int index = 0; index < names.Length; index++)
        {
            FixedName(names[index]).CopyTo(result.AsSpan(index * 64, 64));
        }

        return result;
    }

    private static byte[] FixedName(string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        if (encoded.Length > 63) throw new InvalidDataException($"Name '{value}' exceeds Chrome's 63-byte field.");
        byte[] result = new byte[64];
        encoded.CopyTo(result, 0);
        return result;
    }

    private static byte[] PackVector3(ImmutableArray<Vector3D> values)
    {
        byte[] result = new byte[values.Length * 12];
        int offset = 0;
        foreach (Vector3D value in values)
        {
            WriteFloat(result, ref offset, value.X);
            WriteFloat(result, ref offset, value.Y);
            WriteFloat(result, ref offset, value.Z);
        }

        return result;
    }

    private static byte[] PackVector2(ImmutableArray<(double U, double V)> values)
    {
        byte[] result = new byte[values.Length * 8];
        int offset = 0;
        foreach ((double u, double v) in values)
        {
            WriteFloat(result, ref offset, u);
            WriteFloat(result, ref offset, v);
        }

        return result;
    }

    private static void WriteMatrix3X4(Span<byte> destination, TransformMatrix value)
    {
        double[] elements =
        [
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
        ];
        for (int index = 0; index < elements.Length; index++)
        {
            WriteSingle(destination.Slice(index * 4, 4), elements[index]);
        }
    }

    private static void WriteSingle(Span<byte> destination, double value)
    {
        float converted = checked((float)value);
        if (!float.IsFinite(converted)) throw new InvalidDataException("Source-model output contains a non-finite or out-of-range float.");
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(converted));
    }

    private static void WriteFloat(byte[] destination, ref int offset, double value)
    {
        WriteSingle(destination.AsSpan(offset, 4), value);
        offset += 4;
    }

    private static void WriteUInt(byte[] destination, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, 4), value);
        offset += 4;
    }

    private static string BuildBoneScript(ImmutableArray<string> boneNames, ImmutableArray<Dl1ResolvedBoneScriptPolicy> policies)
    {
        var builder = new StringBuilder();
        builder.AppendLine("import \"bscr.def\"");
        builder.AppendLine();
        builder.AppendLine("sub main()");
        builder.AppendLine("{");
        for (int index = 0; index < boneNames.Length; index++)
        {
            string components = policies.IsDefault ? index == 0 ? "POS | ROT | SCL" : "POS | ROT" : policies[index].Components;
            string lod = policies.IsDefault ? "LOD_OFF" : policies[index].LodToken;
            builder.Append("    SetBoneAnimTrans(\"")
                .Append(EscapeScriptString(boneNames[index]))
                .Append("\", ")
                .Append(components)
                .Append(", ").Append(lod).AppendLine(");");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EscapeScriptString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    public static string SanitizeName(string value, int maximumUtf8Bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string clean = string.Concat(value.Select(static character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_'));
        clean = string.Join('_', clean.Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0) clean = "model";
        if (Encoding.UTF8.GetByteCount(clean) <= maximumUtf8Bytes) return clean;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clean))).ToLowerInvariant()[..8];
        while (clean.Length > 0 && Encoding.UTF8.GetByteCount($"{clean}_{digest}") > maximumUtf8Bytes)
        {
            clean = clean[..^1];
        }

        return $"{clean.TrimEnd('_')}_{digest}";
    }

    /// <summary>
    /// Validates a DL1 resource identity without silently rewriting it. The
    /// type-322 AnimationScr identity is extensionless; the loose ASCR refers
    /// to its corresponding virtual <c>.scr</c> filename.
    /// </summary>
    internal static string RequireExactResourceName(
        string value,
        int maximumUtf8Bytes,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string exact = value.Trim();
        string normalized = SanitizeName(exact, maximumUtf8Bytes);
        if (!string.Equals(exact, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The {label} '{exact}' is not a valid exact DL1 resource name. " +
                "Use only ASCII letters, digits, and underscores within the format limit.",
                nameof(value));
        }

        return exact;
    }

    /// <summary>
    /// Chrome's type-322 resource identity is extensionless, while an ASCR
    /// declaration names the virtual script file resolved by the engine. The
    /// editor derives the default by replacing the model extension with
    /// <c>.scr</c>, and stock ASCR declarations use the same suffix.
    /// </summary>
    internal static string AnimationScriptFileName(string resourceIdentity) =>
        $"{RequireExactResourceName(resourceIdentity, 63, "animation script resource identity")}.scr";

    internal static double ConvertTextureCoordinateV(double value, bool flip)
    {
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException("Source-model texture coordinates must be finite.");
        }

        return flip ? 1.0 - value : value;
    }

    private static string UniqueName(string proposed, HashSet<string> used)
    {
        string candidate = proposed;
        int suffix = 1;
        while (!used.Add(candidate))
        {
            candidate = SanitizeName($"{proposed}_{suffix++}", 63);
        }

        return candidate;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private readonly record struct Bounds(Vector3D Center, Vector3D HalfExtents)
    {
        public static Bounds Zero => new(Vector3D.Zero, Vector3D.Zero);
    }

    private sealed record SkinVertex(ImmutableArray<int> BoneIndices, ImmutableArray<double> Weights);

    private sealed record SourceMorphTarget(
        string Name,
        ImmutableArray<Vector3D> PositionDeltas);

    private sealed record SourceSubset(int MaterialIndex, int FirstIndex, int IndexCount,
        ImmutableArray<int> Palette, int FirstVertex, int VertexCount);

    private sealed record SourceLod(
        ImmutableArray<Vector3D> Positions,
        ImmutableArray<Vector3D> Normals,
        ImmutableArray<Vector3D> Tangents,
        ImmutableArray<Vector3D> Bitangents,
        ImmutableArray<(double U, double V)> Uvs,
        ImmutableArray<uint> Indices,
        ImmutableArray<SourceSubset> Subsets,
        ImmutableArray<SkinVertex> Skin,
        ImmutableArray<SourceMorphTarget> MorphTargets);

    private sealed record SourceNode(
        string Name,
        uint Type,
        int ParentIndex,
        TransformMatrix LocalMatrix,
        TransformMatrix ReferenceMatrix,
        Bounds Bounds,
        uint Flags,
        ImmutableArray<SourceLod> Lods);

    private sealed record PreparedSourceModel(
        ImmutableArray<string> MaterialNames,
        ImmutableDictionary<string, byte[]> MaterialFiles,
        ImmutableArray<string> MaterialNotes,
        ImmutableArray<string> SurfaceNames,
        ImmutableArray<SourceNode> Nodes,
        ImmutableArray<string> BoneNames,
        int HelperCount,
        ImmutableArray<SourceNode> GeometryNodes,
        Dl1AuthoredRigContract? RigContract);
}


