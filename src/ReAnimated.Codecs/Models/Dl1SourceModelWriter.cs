using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
}

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
    ImmutableArray<string> BlockingReasons);

/// <summary>
/// Writes the documented Chrome source-MSH, structured CHR v4 character
/// definition, and companion scripts consumed by Techland's editor compiler.
/// Compact .skn/.msh_obj products remain outputs of the official compiler.
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
        ValidateMorphInventory(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SurfaceName);

        string resourceName = SanitizeName(request.ResourceName, 55);
        string surfaceName = SanitizeName(request.SurfaceName, 63);
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        PreparedSourceModel prepared = Prepare(request.Model, resourceName, surfaceName, cancellationToken);
        byte[] msh = BuildMsh(prepared);
        byte[] chr = Dl1ChrV4Codec.Build(
            Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant(
                BuildChrObjects(prepared),
                "default"));
        string bscr = BuildBoneScript(prepared.BoneNames);
        string? animationScriptAlias = string.IsNullOrWhiteSpace(request.AnimationScriptAlias)
            ? null
            : RequireExactResourceName(request.AnimationScriptAlias, 63, "animation script alias");
        string? ascr = animationScriptAlias is null
            ? null
            : $"AnimScriptAlias(\"{EscapeScriptString(AnimationScriptFileName(animationScriptAlias))}\")\n";
        string blocked =
            "DL ReAnimated generated bounded DL1 source-model compiler inputs.\r\n" +
            "The following requested products are intentionally not fabricated:\r\n" +
            "  - .skn\r\n" +
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
        foreach ((string relativePath, byte[] bytes) in prepared.MaterialFiles)
        {
            files.Add(relativePath, bytes);
        }

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
                characterDefinition = "Structured DL1 CHR v4 with one default variant in exact physical object order",
                boneScript = "Per-entity POS/ROT, plus root SCL",
                animationScript = ascr is null ? "not authored" : "explicit user-supplied alias",
                materials = "Techland DMT sources with user-owned diffuse/normal/specular DDS dependencies",
                morphTargets = "Chrome LOD 0x0104 records with fixed UTF-8 names and one float3 position delta per expanded draw vertex",
                unsupported = new[] { ".skn", ".msh_obj" },
            },
            counts = new
            {
                bones = prepared.BoneNames.Length,
                helpers = prepared.HelperCount,
                drawSurfaces = prepared.GeometryNodes.Length,
                physicalNodes = prepared.Nodes.Length,
                characterObjects = prepared.Nodes.Length,
                materials = prepared.MaterialNames.Length,
                morphChannels = request.Model.Package.Document.MorphChannels.Length,
                morphSurfaceBindings = request.Model.Surfaces.Count(
                    static surface => !surface.MorphTargets.IsEmpty),
                materialSourceFiles = prepared.MaterialFiles.Count,
            },
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
                await File.WriteAllBytesAsync(stagedPath, bytes, cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(outputDirectory);
            foreach (string relativePath in files.Keys.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string stagedPath = Path.Combine(temporaryDirectory, relativePath);
                string finalPath = Path.Combine(outputDirectory, relativePath);
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
            ]);
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
                null));
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

        var geometryNodes = ImmutableArray.CreateBuilder<SourceNode>(model.Surfaces.Length);
        var usedNames = nodes.Select(static node => node.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int surfaceIndex = 0; surfaceIndex < model.Surfaces.Length; surfaceIndex++)
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

            int materialIndex = materialIndexById.TryGetValue(surface.MaterialId, out int resolvedMaterial)
                ? resolvedMaterial
                : 0;
            ImmutableArray<int> physicalPalette = authoredRig is null
                ? surface.PaletteBoneIndices.IsEmpty
                    ? []
                    : throw new InvalidDataException(
                        $"Surface '{surface.Id}' has a skin palette but the custom model has no authored rig.")
                : authoredRig.Surfaces[surfaceIndex].PhysicalPalette;
            string nodeName = UniqueName(
                SanitizeName($"{resourceName}_{surface.MeshName}_p{surfaceIndex:00}", 63),
                usedNames);
            SourceLod lod = BuildLod(
                surface,
                materialIndex,
                physicalPalette,
                document.BuildSettings.FlipTextureCoordinateV);
            geometryNodes.Add(new SourceNode(
                nodeName,
                surface.IsSkinned ? NodeSkinnedMesh : NodeMesh,
                -1,
                TransformMatrix.Identity,
                TransformMatrix.Identity,
                ComputeBounds(surface.Vertices.Select(static vertex => vertex.Position)),
                surface.IsSkinned ? NodeAnimated : 0,
                lod));
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
            null));

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
        ImmutableArray<Vector3D> positions = surface.Vertices.Select(static vertex => vertex.Position).ToImmutableArray();
        ImmutableArray<Vector3D> normals = surface.Vertices.Select(static vertex => vertex.Normal.Normalized()).ToImmutableArray();
        ImmutableArray<(double U, double V)> uvs = surface.Vertices
            .Select(vertex => (
                vertex.TextureCoordinateU,
                ConvertTextureCoordinateV(vertex.TextureCoordinateV, flipTextureCoordinateV)))
            .ToImmutableArray();
        (ImmutableArray<Vector3D> tangents, ImmutableArray<Vector3D> bitangents) =
            ComputeTangentBasis(positions, normals, uvs, surface.Indices);
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
            materialIndex,
            physicalPalette,
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
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(72, 4), node.Lod is null ? 0u : 1u);
        WriteMatrix3X4(payload.AsSpan(76, 48), node.LocalMatrix);
        WriteMatrix3X4(payload.AsSpan(124, 48), node.ReferenceMatrix);
        WriteSingle(payload.AsSpan(172, 4), node.Bounds.Center.X);
        WriteSingle(payload.AsSpan(176, 4), node.Bounds.Center.Y);
        WriteSingle(payload.AsSpan(180, 4), node.Bounds.Center.Z);
        WriteSingle(payload.AsSpan(184, 4), node.Bounds.HalfExtents.X);
        WriteSingle(payload.AsSpan(188, 4), node.Bounds.HalfExtents.Y);
        WriteSingle(payload.AsSpan(192, 4), node.Bounds.HalfExtents.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(196, 4), node.Flags);
        return PackChunk(0x0003, payload, node.Lod is null ? [] : [BuildLodChunk(node.Lod)]);
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
        byte[] subset = new byte[12 + (lod.Palette.Length * 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(0, 2), checked((ushort)lod.MaterialIndex));
        BinaryPrimitives.WriteUInt32LittleEndian(subset.AsSpan(2, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(subset.AsSpan(6, 4), checked((uint)lod.Indices.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(10, 2), checked((ushort)lod.Palette.Length));
        for (int index = 0; index < lod.Palette.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(12 + (index * 2), 2), checked((ushort)lod.Palette[index]));
        }

        children.Add(PackChunk(0x151, subset));
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)lod.Positions.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), checked((uint)lod.Indices.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), 1);
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
        double total = 0;
        for (int index = 0; index < weights.Length; index++)
        {
            total += Math.Max(0, weights[index]);
        }

        if (!double.IsFinite(total) || total <= 0)
        {
            throw new InvalidDataException("Skin weights must contain one finite positive value.");
        }

        var result = new short[weights.Length];
        int sum = 0;
        int largest = 0;
        double largestValue = double.NegativeInfinity;
        for (int index = 0; index < weights.Length; index++)
        {
            double normalized = Math.Max(0, weights[index]) / total;
            int value = (int)Math.Floor(normalized * 32767.0);
            result[index] = checked((short)value);
            sum += value;
            if (normalized > largestValue)
            {
                largest = index;
                largestValue = normalized;
            }
        }

        result[largest] = checked((short)(result[largest] + (32767 - sum)));
        return result;
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

    private static string BuildBoneScript(ImmutableArray<string> boneNames)
    {
        var builder = new StringBuilder();
        builder.AppendLine("import \"bscr.def\"");
        builder.AppendLine();
        builder.AppendLine("sub main()");
        builder.AppendLine("{");
        for (int index = 0; index < boneNames.Length; index++)
        {
            string components = index == 0 ? "POS | ROT | SCL" : "POS | ROT";
            builder.Append("    SetBoneAnimTrans(\"")
                .Append(EscapeScriptString(boneNames[index]))
                .Append("\", ")
                .Append(components)
                .AppendLine(", LOD_OFF);");
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

    private sealed record SourceLod(
        ImmutableArray<Vector3D> Positions,
        ImmutableArray<Vector3D> Normals,
        ImmutableArray<Vector3D> Tangents,
        ImmutableArray<Vector3D> Bitangents,
        ImmutableArray<(double U, double V)> Uvs,
        ImmutableArray<uint> Indices,
        int MaterialIndex,
        ImmutableArray<int> Palette,
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
        SourceLod? Lod);

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
