using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.DL1.Assets.Meshes;

public interface IDl1MeshResourceDecoder
{
    Task<Dl1MeshData> DecodeAsync(
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource,
        Rp6lChunkCache chunkCache,
        CancellationToken cancellationToken = default);
}

public sealed class Dl1MeshDecoder : IDl1MeshResourceDecoder
{
    public Task<Dl1MeshData> DecodeAsync(
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource,
        Rp6lChunkCache chunkCache,
        CancellationToken cancellationToken = default) =>
        Dl1MeshResourceDecoder.DecodeAsync(
            archive,
            resource,
            chunkCache,
            cancellationToken);
}

public static class Dl1MeshResourceDecoder
{
    public static async Task<Dl1MeshData> DecodeAsync(
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource,
        Rp6lChunkCache chunkCache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(chunkCache);
        if (resource.ResourceType != Rp6lResourceTypes.Mesh)
        {
            throw new ArgumentException(
                $"Resource '{resource.Name}' is type {resource.ResourceType}, not a compiled mesh.",
                nameof(resource));
        }

        if (resource.Items.Count is < 3 or 4)
        {
            throw new InvalidDataException(
                $"Mesh '{resource.Name}' has {resource.Items.Count} RP6L items; retail DL1 uses a three-item metadata-only layout or a five-plus-item split-GPU layout.");
        }

        Dl1MeshContainerLayout containerLayout =
            resource.Items.Count switch
            {
                3 => Dl1MeshContainerLayout.ThreeItemMetadataOnly,
                5 => Dl1MeshContainerLayout.FiveItemSplitGpu,
                _ => Dl1MeshContainerLayout.ExtendedSplitGpu,
            };
        Rp6lItemDescriptor metadataItem = resource.Items[0];
        byte[] metadata = await archive.ReadItemBytesAsync(
            metadataItem,
            chunkCache,
            maximumBytes: 64 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        CompactMeshDocument hierarchy =
            CompactMeshDecoder.Decode(metadata);
        List<Dl1MeshDiagnostic> diagnostics = hierarchy.Diagnostics
            .Select(MapDiagnostic)
            .ToList();
        if (hierarchy.SkinnedMeshes.Count > 0 &&
            hierarchy.Bones.Count == 0)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH001",
                Dl1MeshDiagnosticSeverity.Error,
                "The compact object has skinned mesh nodes but no bone entities."));
        }

        List<Dl1MeshBufferReference> buffers = resource.Items
            .Select((item, slot) => new Dl1MeshBufferReference(
                item.Index,
                slot,
                item.StorageGroupId,
                slot switch
                {
                    0 => Dl1MeshBufferRole.CompactMetadata,
                    1 => Dl1MeshBufferRole.VariantDefinitions,
                    2 => Dl1MeshBufferRole.ResolverData,
                    3 => Dl1MeshBufferRole.VertexData,
                    4 => Dl1MeshBufferRole.IndexData,
                    >= 5 => Dl1MeshBufferRole.AuxiliaryGpuData,
                    _ => Dl1MeshBufferRole.Unknown,
                },
                item.HasReadableSize ? item.SizeOrHash : 0))
            .ToList();

        Rp6lItemDescriptor variantItem = resource.Items[1];
        Rp6lItemDescriptor? vertexItem =
            resource.Items.Count >= 5 ? resource.Items[3] : null;
        Rp6lItemDescriptor? indexItem =
            resource.Items.Count >= 5 ? resource.Items[4] : null;
        CompiledMeshGeometryDocument? geometry = null;
        Dl1MeshGeometryProvenance? geometryProvenance = null;
        if (vertexItem is null || indexItem is null)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH002",
                Dl1MeshDiagnosticSeverity.Warning,
                "The compiled mesh uses the legitimate three-item metadata-only layout and has no vertex/index item slots 3 and 4."));
        }
        else
        {
            byte[] variant = await archive.ReadItemBytesAsync(
                variantItem,
                chunkCache,
                maximumBytes: 16 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            byte[] vertices = await archive.ReadItemBytesAsync(
                vertexItem,
                chunkCache,
                maximumBytes: 512 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            byte[] indices = await archive.ReadItemBytesAsync(
                indexItem,
                chunkCache,
                maximumBytes: 512 * 1024 * 1024,
                cancellationToken).ConfigureAwait(false);
            geometryProvenance = Dl1MeshGeometryFingerprint.Create(
                metadata,
                variant,
                vertices,
                indices);
            geometry = CompiledMeshGeometryDecoder.Decode(
                metadata,
                variant,
                vertices,
                indices,
                retailResourceName: resource.Name,
                cancellationToken: cancellationToken);
            diagnostics.AddRange(geometry.Diagnostics.Select(MapDiagnostic));
        }

        IReadOnlyList<CompactMatrix3x4>? worldMatrices =
            hierarchy.IsStructurallyValid
                ? hierarchy.ReconstructGlobalMatrices()
                : null;
        Dl1MeshSurface[] surfaces = geometry is null
            ? []
            : geometry.Surfaces
                .Select(surface =>
                {
                    CompactMeshEntity? surfaceEntity =
                        (uint)surface.EntityIndex <
                        (uint)hierarchy.Entities.Count
                            ? hierarchy.Entities[
                                surface.EntityIndex]
                            : null;
                    CompactMatrix3x4? surfaceWorldMatrix =
                        worldMatrices is not null &&
                        (uint)surface.EntityIndex <
                            (uint)worldMatrices.Count
                            ? worldMatrices[surface.EntityIndex]
                            : null;
                    return MapSurface(
                        surface,
                        surfaceEntity,
                        surfaceWorldMatrix,
                        vertexItem!.Index,
                        indexItem!.Index);
                })
                .ToArray();
        int rigidIndexedPaletteCount = surfaces.Sum(
            static surface => surface.Submeshes.Count(
                static submesh =>
                    submesh.SkinBindingMode ==
                    Dl1SkinBindingMode.RigidIndexedPalette));
        if (rigidIndexedPaletteCount > 0)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH015",
                Dl1MeshDiagnosticSeverity.Information,
                $"{rigidIndexedPaletteCount} submesh(es) use the corpus-inferred DL1 1.55 rigid indexed-palette encoding. Their serialized zero weights remain unchanged; consumers may materialize an implicit unit weight at the valid local X palette selector. This rule is not yet live-game validated."));
        }

        int ignoredPaletteCount = surfaces.Sum(
            static surface => surface.Submeshes.Count(
                static submesh =>
                    submesh.SkinBindingMode ==
                    Dl1SkinBindingMode
                        .StaticEntityTransformIgnoredPalette));
        if (ignoredPaletteCount > 0)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH016",
                Dl1MeshDiagnosticSeverity.Warning,
                $"{ignoredPaletteCount} submesh(es) use the runtime-validated no-BlendIndices declaration path. Their serialized palettes remain preserved but are ignored; preview applies each finite reconstructed entity/world transform as a non-skinned draw, and bone editing is unavailable for those parts."));
        }

        CompiledMeshSkinDefinition? appliedSkin =
            geometry is null
                ? null
                : FindDefaultSkin(geometry, diagnostics);
        IReadOnlyList<Dl1MaterialSlot> materialSlots =
            geometry is null
                ? []
                : BuildMaterialSlots(
                    geometry,
                    appliedSkin,
                    diagnostics);
        int[] skinHiddenEntityIndexes = appliedSkin is null
            ? []
            : appliedSkin.EntityOverrides
                .Where(static entityOverride =>
                    entityOverride.IsHidden)
                .Select(static entityOverride =>
                    entityOverride.EntityIndex)
                .Distinct()
                .Order()
                .ToArray();
        if (appliedSkin is not null)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH018",
                Dl1MeshDiagnosticSeverity.Information,
                $"Applied exact retail skin '{appliedSkin.Name}': {appliedSkin.MaterialOverrides.Count} material substitution(s) and {skinHiddenEntityIndexes.Length} directly hidden hierarchy entity/entities."));
            if (appliedSkin.SurfaceOverrideCount > 0 ||
                appliedSkin.RandomizedChildCount > 0)
            {
                diagnostics.Add(new Dl1MeshDiagnostic(
                    "DL1MESH019",
                    Dl1MeshDiagnosticSeverity.Warning,
                    $"Retail skin '{appliedSkin.Name}' also declares {appliedSkin.SurfaceOverrideCount} surface override(s) and {appliedSkin.RandomizedChildCount} randomized child record(s). Their bounded counts are retained, but those runtime effects are not emulated by this preview."));
            }
        }

        IReadOnlyList<Dl1MorphTarget> morphTargets =
            geometry is null
                ? []
                : BuildMorphTargets(
                    geometry,
                    metadataItem.Index);
        ReAnimated.Core.Domain.RigDefinition? rig = null;
        try
        {
            rig = Dl1RigDefinitionFactory.TryCreate(
                resource.Name,
                hierarchy,
                morphTargets);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            OverflowException)
        {
            bool rawMatrixHelperPreviewOnly =
                Dl1RigPromotionPolicy
                    .CanPublishRawMatrixHelperPreview(
                        hierarchy,
                        surfaces,
                        exception.Message);
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH014",
                rawMatrixHelperPreviewOnly
                    ? Dl1MeshDiagnosticSeverity.Warning
                    : Dl1MeshDiagnosticSeverity.Error,
                rawMatrixHelperPreviewOnly
                    ? $"The helper-only retail hierarchy cannot be represented as an authoring TRS rig: {exception.Message} Raw matrix mesh/helper preview remains available, but animation evaluation, retargeting, bone editing, and export are unavailable for this resource."
                    : $"The retail hierarchy could not be promoted to an authoring rig: {exception.Message}"));
        }

        if (hierarchy.Bones.Count > 0 && rig is null &&
            diagnostics.All(static diagnostic =>
                diagnostic.Code != "DL1MESH014"))
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH014",
                Dl1MeshDiagnosticSeverity.Error,
                "The geometry has bone entities but no bounded animation hierarchy could be derived."));
        }

        if (resource.Items[2].HasReadableSize &&
            resource.Items[2].SizeOrHash > 0)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH010",
                Dl1MeshDiagnosticSeverity.Information,
                "Type-17 resolver data is associated with the resource and intentionally retained as an opaque buffer."));
        }

        if (geometry is not null &&
            geometry.DeclaredMaterialSlotCount > 0)
        {
            int decodedSlotNames = materialSlots.Count(static slot =>
                slot.Index >= 0 &&
                slot.BindingStatus ==
                    Dl1MaterialBindingStatus.DatabaseNameDecoded);
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH011",
                decodedSlotNames == geometry.DeclaredMaterialSlotCount
                    ? Dl1MeshDiagnosticSeverity.Information
                    : Dl1MeshDiagnosticSeverity.Warning,
                $"{decodedSlotNames} of {geometry.DeclaredMaterialSlotCount} declared material-slot database names were decoded. Material-resource and texture resolution remain intentionally unresolved."));
        }

        if (geometry is not null &&
            geometry.MorphChannels.Count > 0)
        {
            int decodedTargetCount = morphTargets.Count(static target =>
                target.PayloadStatus ==
                    Dl1MorphPayloadStatus.VertexDeltasDecoded);
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH012",
                Dl1MeshDiagnosticSeverity.Information,
                $"{decodedTargetCount} of {geometry.MorphChannels.Count} morph channels have decoded per-vertex SHORT4 position deltas; channel names and per-node/per-LOD associations are retained."));
        }

        var decodedMesh = new Dl1MeshData(
            resource.Name,
            containerLayout,
            hierarchy,
            rig,
            buffers,
            surfaces,
            materialSlots,
            morphTargets,
            geometry?.VariantNames ?? [],
            diagnostics)
        {
            GeometryProvenance = geometryProvenance,
            AppliedSkinName = appliedSkin?.Name,
            SkinHiddenEntityIndexes = skinHiddenEntityIndexes,
        };
        foreach (Dl1MeshSurface surface in surfaces)
        {
            if (!Dl1RetailStockGeometryPolicy
                    .TryGetRawGpuNonFiniteUv0Vertices(
                        decodedMesh,
                        surface,
                        out _,
                        out string policyLabel))
            {
                continue;
            }

            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH017",
                Dl1MeshDiagnosticSeverity.Warning,
                $"Surface '{surface.Name}' matches the exact content-fingerprinted stock DL1 1.55 raw-GPU UV anomaly '{policyLabel}'. Raw +/-Infinity UV0 values are retained and published unchanged. The neutral base-color preview is fidelity-limited because the exact retail material technique is not emulated."));
        }

        return decodedMesh with
        {
            Diagnostics = diagnostics.ToArray(),
        };
    }

    private static Dl1MeshSurface MapSurface(
        CompiledMeshSurface surface,
        CompactMeshEntity? surfaceEntity,
        CompactMatrix3x4? surfaceWorldMatrix,
        int vertexItemIndex,
        int indexItemIndex)
    {
        Dl1VertexLayout layout = new(
            surface.VertexLayout.Stride,
            surface.VertexLayout.Elements
                .Select(MapVertexElement)
                .ToArray());
        Dl1MeshVertex[] vertices = surface.Vertices
            .Select(MapVertex)
            .ToArray();
        Dl1MeshSubmesh[] submeshes = surface.Submeshes
            .Select(submesh =>
            {
                var mapped = new Dl1MeshSubmesh(
                    submesh.Index,
                    submesh.FirstIndex,
                    submesh.IndexCount,
                    submesh.DeclaredMaterialSlotIndex ?? -1,
                    submesh.BonePaletteEntityIndexes);
                return mapped with
                {
                    SkinBindingMode =
                        Dl1SkinBindingPolicy.Classify(
                            layout,
                            vertices,
                            surface.Indices,
                            mapped,
                            surfaceEntity,
                            surfaceWorldMatrix),
                };
            })
            .ToArray();
        int materialSlot = submeshes.Length == 1
            ? submeshes[0].MaterialSlotIndex
            : -1;
        return new Dl1MeshSurface(
            surface.Name,
            surface.EntityIndex,
            surface.LodIndex,
            materialSlot,
            layout,
            new Dl1MeshBufferSlice(
                vertexItemIndex,
                surface.VertexByteOffset,
                checked(
                    surface.Vertices.Count *
                    surface.VertexLayout.Stride),
                surface.VertexLayout.Stride),
            new Dl1MeshBufferSlice(
                indexItemIndex,
                surface.IndexByteOffset,
                checked(surface.Indices.Count * sizeof(ushort)),
                sizeof(ushort)),
            surface.Vertices.Count,
            surface.Indices.Count,
            vertices,
            surface.Indices,
            submeshes);
    }

    private static Dl1VertexElement MapVertexElement(
        CompiledVertexElement element) =>
        new(
            element.RawSemantic switch
            {
                (byte)CompiledVertexSemantic.Position =>
                    Dl1VertexSemantic.Position,
                (byte)CompiledVertexSemantic.Normal =>
                    Dl1VertexSemantic.Normal,
                (byte)CompiledVertexSemantic.Tangent =>
                    Dl1VertexSemantic.Tangent,
                (byte)CompiledVertexSemantic.TextureCoordinate =>
                    Dl1VertexSemantic.TextureCoordinate,
                (byte)CompiledVertexSemantic.BlendIndices =>
                    Dl1VertexSemantic.BlendIndices,
                (byte)CompiledVertexSemantic.BlendWeights =>
                    Dl1VertexSemantic.BlendWeights,
                _ => Dl1VertexSemantic.Unknown,
            },
            element.Channel,
            element.RawFormat switch
            {
                (byte)CompiledVertexFormat.Float3 =>
                    Dl1VertexElementFormat.Float3,
                (byte)CompiledVertexFormat.Half2 =>
                    Dl1VertexElementFormat.Half2,
                (byte)CompiledVertexFormat.Half4 =>
                    Dl1VertexElementFormat.Half4,
                (byte)CompiledVertexFormat.Byte4
                    when element.RawSemantic is
                        (byte)CompiledVertexSemantic.BlendWeights or
                        (byte)CompiledVertexSemantic.Color =>
                    Dl1VertexElementFormat.Byte4Normalized,
                (byte)CompiledVertexFormat.Byte4 =>
                    Dl1VertexElementFormat.Byte4,
                (byte)CompiledVertexFormat.SignedNormalizedByte4 =>
                    Dl1VertexElementFormat.Byte4Normalized,
                _ => Dl1VertexElementFormat.Unknown,
            },
            0,
            element.ByteOffset);

    private static Dl1MeshVertex MapVertex(CompiledVertex vertex) =>
        new(
            vertex.Position,
            vertex.Normal,
            vertex.Tangent,
            vertex.TextureCoordinate0,
            vertex.TextureCoordinate1,
            vertex.Color,
            vertex.BlendWeights,
            new Dl1BoneIndex4(
                vertex.LocalBlendIndices.X,
                vertex.LocalBlendIndices.Y,
                vertex.LocalBlendIndices.Z,
                vertex.LocalBlendIndices.W));

    private static Dl1MaterialSlot[] BuildMaterialSlots(
        CompiledMeshGeometryDocument geometry,
        CompiledMeshSkinDefinition? appliedSkin,
        List<Dl1MeshDiagnostic> diagnostics)
    {
        int mappedCount = geometry.Surfaces
            .SelectMany(static surface => surface.Submeshes)
            .Where(static submesh =>
                submesh.DeclaredMaterialSlotIndex.HasValue)
            .Select(static submesh =>
                checked((int)submesh.DeclaredMaterialSlotIndex!.Value + 1))
            .DefaultIfEmpty()
            .Max();
        int count = Math.Max(
            geometry.DeclaredMaterialSlotCount,
            mappedCount);
        if (mappedCount > geometry.DeclaredMaterialSlotCount)
        {
            diagnostics.Add(new Dl1MeshDiagnostic(
                "DL1MESH013",
                Dl1MeshDiagnosticSeverity.Warning,
                $"Submeshes reference {mappedCount} numeric material slots, while the header declares {geometry.DeclaredMaterialSlotCount}."));
        }

        Dictionary<int, CompiledMaterialDatabaseEntry> decoded =
            geometry.MaterialDatabase.Entries
                .Where(static entry => entry.Index >= 0)
                .ToDictionary(
                    static entry => entry.Index,
                    static entry => entry);
        Dictionary<int, CompiledMeshSkinMaterialOverride> replacements =
            [];
        if (appliedSkin is not null)
        {
            foreach (CompiledMeshSkinMaterialOverride replacement in
                     appliedSkin.MaterialOverrides)
            {
                // The runtime applies these in serialized order; preserve its
                // last-write-wins behavior for repeated target slots.
                replacements[replacement.TargetMaterialSlotIndex] =
                    replacement;
            }
        }

        return Enumerable.Range(0, count)
            .Select(index =>
            {
                decoded.TryGetValue(
                    index,
                    out CompiledMaterialDatabaseEntry? declaredEntry);
                if (replacements.TryGetValue(
                        index,
                        out CompiledMeshSkinMaterialOverride?
                            replacement))
                {
                    if (decoded.TryGetValue(
                            replacement
                                .ReplacementMaterialDatabaseEntryIndex,
                            out CompiledMaterialDatabaseEntry?
                                replacementEntry))
                    {
                        return new Dl1MaterialSlot(
                            index,
                            replacementEntry.DatabaseName,
                            replacementEntry.RawLoadValue,
                            null,
                            Dl1MaterialBindingStatus
                                .DatabaseNameDecoded)
                        {
                            DeclaredDatabaseName =
                                declaredEntry?.DatabaseName,
                            SkinReplacementDatabaseEntryIndex =
                                replacementEntry.Index,
                            AppliedSkinName = appliedSkin!.Name,
                        };
                    }

                    diagnostics.Add(new Dl1MeshDiagnostic(
                        "DL1MESH020",
                        Dl1MeshDiagnosticSeverity.Warning,
                        $"Retail skin '{appliedSkin!.Name}' selects material-database entry {replacement.ReplacementMaterialDatabaseEntryIndex} for slot {index}, but that entry's name did not decode."));
                    return new Dl1MaterialSlot(
                        index,
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"material_database_entry_{replacement.ReplacementMaterialDatabaseEntryIndex:D3}"),
                        null,
                        null,
                        Dl1MaterialBindingStatus
                            .DeclaredSlotNameUnresolved)
                    {
                        DeclaredDatabaseName =
                            declaredEntry?.DatabaseName,
                        SkinReplacementDatabaseEntryIndex =
                            replacement
                                .ReplacementMaterialDatabaseEntryIndex,
                        AppliedSkinName = appliedSkin.Name,
                    };
                }

                if (declaredEntry is not null)
                {
                    return new Dl1MaterialSlot(
                        index,
                        declaredEntry.DatabaseName,
                        declaredEntry.RawLoadValue,
                        null,
                        Dl1MaterialBindingStatus.DatabaseNameDecoded)
                    {
                        DeclaredDatabaseName =
                            declaredEntry.DatabaseName,
                    };
                }

                return new Dl1MaterialSlot(
                    index,
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"material_slot_{index:D3}"),
                    null,
                    null,
                    index < geometry.DeclaredMaterialSlotCount
                        ? Dl1MaterialBindingStatus
                            .DeclaredSlotNameUnresolved
                        : Dl1MaterialBindingStatus
                            .SyntheticSurfaceSlot);
            })
            .ToArray();
    }

    private static CompiledMeshSkinDefinition? FindDefaultSkin(
        CompiledMeshGeometryDocument geometry,
        List<Dl1MeshDiagnostic> diagnostics)
    {
        CompiledMeshSkinDefinition[] defaults =
            geometry.SkinDefinitions
                .Where(static skin =>
                    string.Equals(
                        skin.Name,
                        "Default",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (defaults.Length <= 1)
        {
            return defaults.SingleOrDefault();
        }

        diagnostics.Add(new Dl1MeshDiagnostic(
            "DL1MESH021",
            Dl1MeshDiagnosticSeverity.Warning,
            $"The compact mesh declares {defaults.Length} case-insensitive Default skins. Runtime duplicate-name selection is not proven, so no skin visibility or material substitutions were guessed."));
        return null;
    }

    private static Dl1MorphTarget[] BuildMorphTargets(
        CompiledMeshGeometryDocument geometry,
        int metadataItemIndex) =>
        geometry.MorphChannels
            .Select(channel =>
            {
                Dl1MorphBinding[] bindings = geometry.MorphBindings
                    .Select(binding => new
                    {
                        Binding = binding,
                        Targets = binding.TargetDeltas
                            .Where(target =>
                                target.MorphChannelIndex ==
                                    channel.Index)
                            .ToArray(),
                    })
                    .Where(static row => row.Targets.Length > 0)
                    .Select(static row => new Dl1MorphBinding(
                        row.Binding.EntityIndex,
                        row.Binding.LodIndex,
                        row.Binding.VertexCount,
                        row.Binding.DeltaByteStride,
                        row.Binding.PayloadByteOffset,
                        Dl1MorphDeltaEncoding
                            .SignedShort4Scale16384,
                        row.Targets
                            .Select(static target =>
                                target.LocalTargetIndex)
                            .ToArray(),
                        row.Targets
                            .Select(static target =>
                                new Dl1MorphPositionDeltaSet(
                                    target.LocalTargetIndex,
                                    target.PositionDeltas))
                            .ToArray()))
                    .ToArray();
                Dl1MeshBufferSlice[] deltaBuffers = bindings
                    .SelectMany(binding =>
                        binding.LocalTargetIndexes.Select(
                            localTargetIndex =>
                                new Dl1MeshBufferSlice(
                                    metadataItemIndex,
                                    checked(
                                        binding.PayloadByteOffset +
                                        localTargetIndex *
                                        binding.VertexCount *
                                        binding.DeltaByteStride),
                                    checked(
                                        binding.VertexCount *
                                        binding.DeltaByteStride),
                                    binding.DeltaByteStride)))
                    .ToArray();
                return new Dl1MorphTarget(
                    channel.Index,
                    channel.Name,
                    bindings
                        .Select(static binding => binding.EntityIndex)
                        .Distinct()
                        .ToArray(),
                    deltaBuffers,
                    bindings,
                    bindings.Length == 0
                        ? Dl1MorphPayloadStatus.ChannelOnly
                        : bindings.All(static binding =>
                            binding.PositionDeltaSets.Count > 0 &&
                            binding.PositionDeltaSets.All(set =>
                                set.PositionDeltas.Count ==
                                    binding.VertexCount))
                            ? Dl1MorphPayloadStatus
                                .VertexDeltasDecoded
                            : Dl1MorphPayloadStatus
                                .VertexDeltasUnresolved);
            })
            .ToArray();

    private static Dl1MeshDiagnostic MapDiagnostic(
        CompactMeshDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Severity switch
            {
                CompactMeshDiagnosticSeverity.Information =>
                    Dl1MeshDiagnosticSeverity.Information,
                CompactMeshDiagnosticSeverity.Warning =>
                    Dl1MeshDiagnosticSeverity.Warning,
                CompactMeshDiagnosticSeverity.Error =>
                    Dl1MeshDiagnosticSeverity.Error,
                _ => Dl1MeshDiagnosticSeverity.Error,
            },
            diagnostic.Message,
            diagnostic.EntityIndex);
}
