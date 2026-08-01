using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace ReAnimated.Codecs.CompactMesh;

/// <summary>
/// Decodes the split item-16/item-18/item-240/item-241 representation used by
/// retail Dying Light 1 compiled meshes. Palette entries remain compiled-entity
/// indexes because a shared vertex may be drawn by submeshes with different
/// local-to-entity palettes.
/// </summary>
public static class CompiledMeshGeometryDecoder
{
    private const int HeaderMinimumSize = 0xB0;
    private const int EntityStride = 0xD0;
    private const int EntityTablePointerOffset = 0x08;
    private const int MaterialSlotTablePointerOffset = 0x18;
    private const int MorphNameTablePointerOffset = 0x20;
    private const int DeclarationTablePointerOffset = 0x50;
    private const int EntityCountOffset = 0x64;
    private const int MorphCountOffset = 0x70;
    private const int DeclarationCountOffset = 0x7C;
    private const int EntityNamePointerOffset = 0x78;
    private const int EntityMorphLodPointerOffset = 0x80;
    private const int EntityGeometryLodPointerOffset = 0x88;
    private const int EntityLodCountOffset = 0xCA;
    private const int GeometryLodStride = 0x30;
    private const int MorphLodStride = 0x20;
    private const int MorphDeltaElementStride = sizeof(short) * 4;
    private const float MorphDeltaScale = 1.0f / 16_384.0f;
    private const int MaterialDatabaseHeaderSize = 0x0C;
    private const int MaterialDatabaseEntryStride = 0x18;
    private const int SkinHeaderSize = 0x08;
    private const int SkinDefinitionStride = 0x30;
    private const int MaximumTextBytes = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CompiledMeshGeometryDocument Decode(
        ReadOnlyMemory<byte> metadataPayload,
        ReadOnlyMemory<byte> variantPayload,
        ReadOnlyMemory<byte> vertexPayload,
        ReadOnlyMemory<byte> indexPayload,
        CompiledMeshDecodeLimits? limits = null,
        string? retailResourceName = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= CompiledMeshDecodeLimits.Default;
        limits.Validate();
        if (metadataPayload.Length < HeaderMinimumSize)
        {
            throw new InvalidDataException(
                $"Compiled mesh metadata is only 0x{metadataPayload.Length:X} bytes.");
        }

        ReadOnlySpan<byte> metadata = metadataPayload.Span;
        int entityCount = ReadBoundedCount(
            metadata,
            EntityCountOffset,
            "entity",
            1_000_000);
        int entityTableOffset = ReadRequiredPointer(
            metadata,
            EntityTablePointerOffset,
            "entity table");
        EnsureRange(
            metadata.Length,
            entityTableOffset,
            checked((long)entityCount * EntityStride),
            "entity table");

        List<CompactMeshDiagnostic> diagnostics = [];
        List<CompiledVertexLayout> layouts =
            ReadVertexLayouts(metadata, limits, diagnostics);
        List<CompiledMorphChannel> morphChannels =
            ReadMorphChannels(metadata, limits, diagnostics);
        CompiledMaterialDatabase materialDatabase =
            ReadMaterialDatabase(
                metadata,
                limits,
                diagnostics,
                cancellationToken);
        List<CompiledMeshSkinDefinition> skinDefinitions =
            ReadSkinDefinitions(
                variantPayload.Span,
                entityCount,
                materialDatabase,
                limits,
                diagnostics,
                cancellationToken,
                out bool exactSkinPayload);
        List<string> variants = exactSkinPayload
            ? skinDefinitions
                .Select(static skin => skin.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : ExtractVariantNames(variantPayload.Span, limits);
        List<CompiledMeshSurface> surfaces = ReadSurfaces(
            metadata,
            entityTableOffset,
            entityCount,
            vertexPayload.Span,
            indexPayload.Span,
            layouts,
            limits,
            retailResourceName,
            diagnostics,
            cancellationToken);
        List<CompiledNodeMorphBinding> morphBindings =
            ReadMorphBindings(
                metadata,
                entityTableOffset,
                entityCount,
                morphChannels.Count,
                surfaces,
                limits,
                diagnostics,
                cancellationToken);

        return new CompiledMeshGeometryDocument(
            layouts,
            surfaces,
            variants,
            materialDatabase,
            morphChannels,
            morphBindings,
            diagnostics)
        {
            SkinDefinitions = skinDefinitions,
        };
    }

    private static List<CompiledVertexLayout> ReadVertexLayouts(
        ReadOnlySpan<byte> metadata,
        CompiledMeshDecodeLimits limits,
        List<CompactMeshDiagnostic> diagnostics)
    {
        int groupCount = ReadBoundedCount(
            metadata,
            DeclarationCountOffset,
            "vertex declaration group",
            limits.MaximumDeclarationGroups);
        if (groupCount == 0)
        {
            return [];
        }

        int tableOffset = ReadRequiredPointer(
            metadata,
            DeclarationTablePointerOffset,
            "vertex declaration table");
        EnsureRange(
            metadata.Length,
            tableOffset,
            checked((long)groupCount * 16),
            "vertex declaration table");
        List<CompiledVertexLayout> layouts = new(groupCount);
        for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            int groupOffset = checked(tableOffset + groupIndex * 16);
            int semanticCount = ReadBoundedCount(
                metadata,
                groupOffset + 8,
                $"vertex declaration {groupIndex} element",
                limits.MaximumElementsPerDeclaration);
            int semanticsOffset = semanticCount == 0
                ? -1
                : ReadRequiredPointer(
                    metadata,
                    groupOffset,
                    $"vertex declaration {groupIndex} elements");
            if (semanticCount > 0)
            {
                EnsureRange(
                    metadata.Length,
                    semanticsOffset,
                    checked((long)semanticCount * 4),
                    $"vertex declaration {groupIndex} elements");
            }

            int stride = 0;
            List<CompiledVertexElement> elements = new(semanticCount);
            for (int semanticIndex = 0;
                 semanticIndex < semanticCount;
                 semanticIndex++)
            {
                int offset = checked(
                    semanticsOffset + semanticIndex * 4);
                byte format = metadata[offset];
                int size = GetFormatSize(format);
                if (size == 0)
                {
                    diagnostics.Add(new CompactMeshDiagnostic(
                        "CMESHG001",
                        CompactMeshDiagnosticSeverity.Error,
                        $"Vertex declaration {groupIndex} uses unsupported format {format}."));
                }

                elements.Add(new CompiledVertexElement(
                    format,
                    metadata[offset + 1],
                    metadata[offset + 2],
                    stride,
                    size));
                stride = checked(stride + size);
            }

            layouts.Add(new CompiledVertexLayout(
                groupIndex,
                stride,
                elements));
        }

        return layouts;
    }

    private static List<CompiledMeshSurface> ReadSurfaces(
        ReadOnlySpan<byte> metadata,
        int entityTableOffset,
        int entityCount,
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<byte> indices,
        IReadOnlyList<CompiledVertexLayout> layouts,
        CompiledMeshDecodeLimits limits,
        string? retailResourceName,
        List<CompactMeshDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<CompiledMeshSurface> surfaces = [];
        for (int entityIndex = 0; entityIndex < entityCount; entityIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int entityOffset = checked(
                entityTableOffset + entityIndex * EntityStride);
            ulong geometryLodPointer = ReadUInt64(
                metadata,
                entityOffset + EntityGeometryLodPointerOffset);
            int lodCount = metadata[entityOffset + EntityLodCountOffset];
            if (geometryLodPointer == 0 || lodCount == 0)
            {
                continue;
            }

            string name;
            try
            {
                name = ReadStringAtPointer(
                    metadata,
                    entityOffset + EntityNamePointerOffset,
                    $"entity {entityIndex} name");
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESHG002",
                    CompactMeshDiagnosticSeverity.Error,
                    exception.Message,
                    entityIndex));
                continue;
            }

            int geometryLodOffset;
            try
            {
                geometryLodOffset = DecodePointer(
                    geometryLodPointer,
                    metadata.Length,
                    $"entity {entityIndex} geometry LOD table");
                EnsureRange(
                    metadata.Length,
                    geometryLodOffset,
                    checked((long)lodCount * GeometryLodStride),
                    $"entity {entityIndex} geometry LOD table");
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESHG003",
                    CompactMeshDiagnosticSeverity.Error,
                    exception.Message,
                    entityIndex));
                continue;
            }

            for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int lodOffset = checked(
                    geometryLodOffset + lodIndex * GeometryLodStride);
                try
                {
                    CompiledMeshSurface? surface = ReadSurface(
                        metadata,
                        vertices,
                        indices,
                        layouts,
                        entityIndex,
                        name,
                        lodIndex,
                        lodOffset,
                        limits,
                        retailResourceName,
                        diagnostics);
                    if (surface is not null)
                    {
                        surfaces.Add(surface);
                    }
                }
                catch (InvalidDataException exception)
                {
                    diagnostics.Add(new CompactMeshDiagnostic(
                        "CMESHG004",
                        CompactMeshDiagnosticSeverity.Error,
                        $"Entity '{name}' LOD {lodIndex}: {exception.Message}",
                        entityIndex));
                }
            }
        }

        return surfaces;
    }

    private static CompiledMeshSurface? ReadSurface(
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> vertexPayload,
        ReadOnlySpan<byte> indexPayload,
        IReadOnlyList<CompiledVertexLayout> layouts,
        int entityIndex,
        string name,
        int lodIndex,
        int lodOffset,
        CompiledMeshDecodeLimits limits,
        string? retailResourceName,
        List<CompactMeshDiagnostic> diagnostics)
    {
        ulong meshInfoPointer = ReadUInt64(metadata, lodOffset + 8);
        if (meshInfoPointer == 0)
        {
            return null;
        }

        int meshInfoOffset = DecodePointer(
            meshInfoPointer,
            metadata.Length,
            "mesh info");
        EnsureRange(metadata.Length, meshInfoOffset, 56, "mesh info");
        int faceCountsOffset = ReadRequiredPointer(
            metadata,
            meshInfoOffset,
            "submesh index-count table");
        int vertexByteOffset = ReadNonNegativeUInt32(
            metadata,
            meshInfoOffset + 24,
            "vertex byte offset");
        int vertexCount = ReadBoundedCount(
            metadata,
            meshInfoOffset + 40,
            "surface vertex",
            limits.MaximumVerticesPerSurface);
        int indexByteOffset = ReadNonNegativeUInt32(
            metadata,
            meshInfoOffset + 44,
            "index byte offset");
        int submeshCount = ReadUInt16(
            metadata,
            meshInfoOffset + 48);
        if (submeshCount == 0)
        {
            submeshCount = ReadUInt16(metadata, lodOffset + 34);
        }

        if (submeshCount <= 0 ||
            submeshCount > limits.MaximumSubmeshesPerSurface)
        {
            throw new InvalidDataException(
                $"Submesh count {submeshCount} is unsafe.");
        }

        short declarationGroup = ReadInt16(
            metadata,
            meshInfoOffset + 50);
        if (declarationGroup < 0 ||
            declarationGroup >= layouts.Count)
        {
            throw new InvalidDataException(
                $"Vertex declaration group {declarationGroup} is outside the {layouts.Count} decoded groups.");
        }

        CompiledVertexLayout layout = layouts[declarationGroup];
        if (layout.Stride <= 0 ||
            layout.Elements.Any(static element => element.ByteSize <= 0))
        {
            throw new InvalidDataException(
                $"Vertex declaration group {declarationGroup} is not decodable.");
        }

        EnsureRange(
            vertexPayload.Length,
            vertexByteOffset,
            checked((long)vertexCount * layout.Stride),
            "vertex stream");
        EnsureRange(
            metadata.Length,
            faceCountsOffset,
            checked((long)submeshCount * sizeof(int)),
            "submesh index-count table");
        int paletteTableOffset = ReadOptionalPointer(
            metadata,
            lodOffset + 24,
            "bone palette table");
        if (paletteTableOffset >= 0)
        {
            EnsureRange(
                metadata.Length,
                paletteTableOffset,
                checked((long)submeshCount * 16),
                "bone palette table");
        }

        int materialIndexesOffset = ReadOptionalPointer(
            metadata,
            lodOffset + 16,
            "submesh material-index table");
        if (materialIndexesOffset >= 0)
        {
            EnsureRange(
                metadata.Length,
                materialIndexesOffset,
                checked((long)submeshCount * sizeof(ushort)),
                "submesh material-index table");
        }

        List<CompiledVertex> decodedVertices =
            ReadVertices(
                vertexPayload,
                vertexByteOffset,
                vertexCount,
                layout);
        List<ushort> decodedIndices = [];
        List<CompiledMeshSubmesh> submeshes =
            new(submeshCount);
        int currentIndexByteOffset = indexByteOffset;
        for (int submeshIndex = 0;
             submeshIndex < submeshCount;
             submeshIndex++)
        {
            int serializedIndexCount = ReadBoundedCount(
                metadata,
                faceCountsOffset + submeshIndex * sizeof(int),
                $"submesh {submeshIndex} index",
                limits.MaximumIndicesPerSurface);
            int indexCount = ApplyRetailRuntimeIndexCountCorrection(
                retailResourceName,
                name,
                lodIndex,
                submeshCount,
                submeshIndex,
                serializedIndexCount);
            if (indexCount != serializedIndexCount)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESHG014",
                    CompactMeshDiagnosticSeverity.Information,
                    $"Applied the retail DL1 runtime index-count correction for entity '{name}' LOD {lodIndex}: serialized {serializedIndexCount}, effective {indexCount}. The final three serialized indexes are alignment padding and remain excluded from the decoded surface.",
                    entityIndex));
            }

            EnsureRange(
                indexPayload.Length,
                currentIndexByteOffset,
                checked((long)indexCount * sizeof(ushort)),
                $"submesh {submeshIndex} index stream");
            int firstIndex = decodedIndices.Count;
            for (int index = 0; index < indexCount; index++)
            {
                ushort vertexIndex = ReadUInt16(
                    indexPayload,
                    currentIndexByteOffset + index * sizeof(ushort));
                if (vertexIndex >= vertexCount)
                {
                    throw new InvalidDataException(
                        $"Submesh {submeshIndex} index {index} references vertex {vertexIndex}, outside {vertexCount} vertices.");
                }

                decodedIndices.Add(vertexIndex);
            }

            IReadOnlyList<short> palette =
                paletteTableOffset < 0
                    ? []
                    : ReadPalette(
                        metadata,
                        paletteTableOffset + submeshIndex * 16,
                        entityIndex,
                        submeshIndex);
            submeshes.Add(new CompiledMeshSubmesh(
                submeshIndex,
                firstIndex,
                indexCount,
                materialIndexesOffset < 0
                    ? null
                    : ReadUInt16(
                        metadata,
                        materialIndexesOffset +
                        submeshIndex * sizeof(ushort)),
                palette));
            currentIndexByteOffset = checked(
                currentIndexByteOffset +
                indexCount * sizeof(ushort));
        }

        return new CompiledMeshSurface(
            entityIndex,
            name,
            lodIndex,
            declarationGroup,
            vertexByteOffset,
            indexByteOffset,
            layout,
            decodedVertices,
            decodedIndices,
            submeshes);
    }

    /// <summary>
    /// Mirrors the exact retail engine workaround in
    /// <c>CCompactMesh::Create</c>. Both the Windows engine and the named
    /// engine build special-case these two mesh files, <c>SC_head</c> LOD 1,
    /// and a 1,368-index surface by reducing the effective count to 1,365.
    /// The affected retail payloads place six non-index padding bytes between
    /// the valid prefix and the next 16-byte-aligned surface.
    /// </summary>
    private static int ApplyRetailRuntimeIndexCountCorrection(
        string? retailResourceName,
        string entityName,
        int lodIndex,
        int submeshCount,
        int submeshIndex,
        int serializedIndexCount)
    {
        bool affectedResource =
            string.Equals(
                retailResourceName,
                "survivor_woman_a",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                retailResourceName,
                "survivor_woman_b",
                StringComparison.OrdinalIgnoreCase);
        return affectedResource &&
               string.Equals(
                   entityName,
                   "SC_head",
                   StringComparison.OrdinalIgnoreCase) &&
               lodIndex == 1 &&
               submeshCount == 1 &&
               submeshIndex == 0 &&
               serializedIndexCount == 1_368
            ? 1_365
            : serializedIndexCount;
    }

    private static List<CompiledVertex> ReadVertices(
        ReadOnlySpan<byte> payload,
        int streamOffset,
        int count,
        CompiledVertexLayout layout)
    {
        List<CompiledVertex> result = new(count);
        for (int vertexIndex = 0; vertexIndex < count; vertexIndex++)
        {
            int vertexOffset = checked(
                streamOffset + vertexIndex * layout.Stride);
            Vector3 position = Vector3.Zero;
            Vector3 normal = Vector3.UnitY;
            Vector4 tangent = new(1, 0, 0, 1);
            Vector2 uv0 = Vector2.Zero;
            Vector2 uv1 = Vector2.Zero;
            Vector4 color = Vector4.One;
            Vector4 weights = Vector4.Zero;
            CompiledBoneIndex4 boneIndexes = default;
            foreach (CompiledVertexElement element in layout.Elements)
            {
                int offset = checked(vertexOffset + element.ByteOffset);
                switch (element.RawSemantic)
                {
                    case (byte)CompiledVertexSemantic.Position:
                        position = element.RawFormat switch
                        {
                            (byte)CompiledVertexFormat.Float3 =>
                                new Vector3(
                                    ReadSingle(payload, offset),
                                    ReadSingle(payload, offset + 4),
                                    ReadSingle(payload, offset + 8)),
                            (byte)CompiledVertexFormat.Half4 =>
                                new Vector3(
                                    ReadHalf(payload, offset),
                                    ReadHalf(payload, offset + 2),
                                    ReadHalf(payload, offset + 4)),
                            _ => position,
                        };
                        break;

                    case (byte)CompiledVertexSemantic.BlendWeights
                        when element.RawFormat ==
                             (byte)CompiledVertexFormat.Byte4:
                        weights = ReadUnormByte4(payload, offset);
                        break;

                    case (byte)CompiledVertexSemantic.BlendIndices
                        when element.RawFormat ==
                             (byte)CompiledVertexFormat.Byte4:
                        boneIndexes = new CompiledBoneIndex4(
                            payload[offset],
                            payload[offset + 1],
                            payload[offset + 2],
                            payload[offset + 3]);
                        break;

                    case (byte)CompiledVertexSemantic.Normal
                        when element.RawFormat ==
                             (byte)CompiledVertexFormat.SignedNormalizedByte4:
                        normal = NormalizeOrDefault(
                            new Vector3(
                                ReadSNormByte(payload[offset]),
                                ReadSNormByte(payload[offset + 1]),
                                ReadSNormByte(payload[offset + 2])),
                            Vector3.UnitY);
                        break;

                    case (byte)CompiledVertexSemantic.TextureCoordinate
                        when element.RawFormat ==
                             (byte)CompiledVertexFormat.Half2:
                        Vector2 uv = new(
                            ReadHalf(payload, offset),
                            ReadHalf(payload, offset + 2));
                        if (element.Channel == 0)
                        {
                            uv0 = uv;
                        }
                        else if (element.Channel == 1)
                        {
                            uv1 = uv;
                        }

                        break;

                    case (byte)CompiledVertexSemantic.Tangent
                        when element.RawFormat ==
                             (byte)CompiledVertexFormat.SignedNormalizedByte4:
                        tangent = new Vector4(
                            ReadSNormByte(payload[offset]),
                            ReadSNormByte(payload[offset + 1]),
                            ReadSNormByte(payload[offset + 2]),
                            ReadSNormByte(payload[offset + 3]));
                        break;

                    case (byte)CompiledVertexSemantic.Color
                        when element.RawFormat ==
                             (byte)CompiledVertexFormat.Byte4:
                        color = ReadUnormByte4(payload, offset);
                        break;
                }
            }

            result.Add(new CompiledVertex(
                position,
                normal,
                tangent,
                uv0,
                uv1,
                color,
                weights,
                boneIndexes));
        }

        return result;
    }

    private static short[] ReadPalette(
        ReadOnlySpan<byte> metadata,
        int rowOffset,
        int entityIndex,
        int submeshIndex)
    {
        int count = ReadBoundedCount(
            metadata,
            rowOffset + 8,
            $"entity {entityIndex} submesh {submeshIndex} palette",
            ushort.MaxValue);
        if (count == 0)
        {
            return [];
        }

        int valuesOffset = ReadRequiredPointer(
            metadata,
            rowOffset,
            $"entity {entityIndex} submesh {submeshIndex} palette");
        EnsureRange(
            metadata.Length,
            valuesOffset,
            checked((long)count * sizeof(short)),
            $"entity {entityIndex} submesh {submeshIndex} palette");
        short[] result = new short[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = ReadInt16(
                metadata,
                valuesOffset + index * sizeof(short));
        }

        return result;
    }

    private static List<CompiledMorphChannel> ReadMorphChannels(
        ReadOnlySpan<byte> metadata,
        CompiledMeshDecodeLimits limits,
        List<CompactMeshDiagnostic> diagnostics)
    {
        int count = ReadBoundedCount(
            metadata,
            MorphCountOffset,
            "morph channel",
            limits.MaximumMorphChannels);
        if (count == 0)
        {
            return [];
        }

        int tableOffset;
        try
        {
            tableOffset = ReadRequiredPointer(
                metadata,
                MorphNameTablePointerOffset,
                "morph name table");
            EnsureRange(
                metadata.Length,
                tableOffset,
                checked((long)count * sizeof(ulong)),
                "morph name table");
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG005",
                CompactMeshDiagnosticSeverity.Error,
                exception.Message));
            return [];
        }

        List<CompiledMorphChannel> channels = new(count);
        for (int index = 0; index < count; index++)
        {
            try
            {
                ulong pointer = ReadUInt64(
                    metadata,
                    tableOffset + index * sizeof(ulong));
                int offset = DecodePointer(
                    pointer,
                    metadata.Length,
                    $"morph channel {index} name");
                channels.Add(new CompiledMorphChannel(
                    index,
                    ReadNullTerminatedUtf8(
                        metadata,
                        offset,
                        $"morph channel {index} name")));
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESHG006",
                    CompactMeshDiagnosticSeverity.Error,
                    exception.Message));
            }
        }

        return channels;
    }

    private static List<CompiledNodeMorphBinding> ReadMorphBindings(
        ReadOnlySpan<byte> metadata,
        int entityTableOffset,
        int entityCount,
        int morphChannelCount,
        IReadOnlyList<CompiledMeshSurface> surfaces,
        CompiledMeshDecodeLimits limits,
        List<CompactMeshDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<CompiledNodeMorphBinding> result = [];
        long decodedDeltaBytes = 0;
        for (int entityIndex = 0; entityIndex < entityCount; entityIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int entityOffset = checked(
                entityTableOffset + entityIndex * EntityStride);
            ulong pointer = ReadUInt64(
                metadata,
                entityOffset + EntityMorphLodPointerOffset);
            int lodCount = metadata[entityOffset + EntityLodCountOffset];
            if (pointer == 0 || lodCount == 0)
            {
                continue;
            }

            try
            {
                int tableOffset = DecodePointer(
                    pointer,
                    metadata.Length,
                    $"entity {entityIndex} morph LOD table");
                EnsureRange(
                    metadata.Length,
                    tableOffset,
                    checked((long)lodCount * MorphLodStride),
                    $"entity {entityIndex} morph LOD table");
                for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
                {
                    int rowOffset = checked(
                        tableOffset + lodIndex * MorphLodStride);
                    int vertexCount = ReadBoundedCount(
                        metadata,
                        rowOffset + 16,
                        $"entity {entityIndex} LOD {lodIndex} morph vertex",
                        limits.MaximumVerticesPerSurface);
                    int targetCount = ReadBoundedCount(
                        metadata,
                        rowOffset + 20,
                        $"entity {entityIndex} LOD {lodIndex} morph target",
                        limits.MaximumMorphChannels);
                    if (targetCount == 0)
                    {
                        if (vertexCount != 0)
                        {
                            diagnostics.Add(new CompactMeshDiagnostic(
                                "CMESHG013",
                                CompactMeshDiagnosticSeverity.Error,
                                $"Entity {entityIndex} LOD {lodIndex} declares {vertexCount} morph vertices but no targets.",
                                entityIndex));
                        }

                        continue;
                    }

                    if (vertexCount == 0)
                    {
                        throw new InvalidDataException(
                            $"Compiled mesh entity {entityIndex} LOD {lodIndex} declares morph targets but no vertices.");
                    }

                    CompiledMeshSurface? surface = surfaces.FirstOrDefault(
                        candidate =>
                            candidate.EntityIndex == entityIndex &&
                            candidate.LodIndex == lodIndex);
                    if (surface is null)
                    {
                        throw new InvalidDataException(
                            $"Compiled mesh entity {entityIndex} LOD {lodIndex} has morph deltas but no decoded geometry surface.");
                    }

                    if (surface.Vertices.Count != vertexCount)
                    {
                        throw new InvalidDataException(
                            $"Compiled mesh entity {entityIndex} LOD {lodIndex} has {vertexCount} morph vertices but {surface.Vertices.Count} geometry vertices.");
                    }

                    int indexesOffset = ReadRequiredPointer(
                        metadata,
                        rowOffset + 8,
                        $"entity {entityIndex} LOD {lodIndex} morph indexes");
                    EnsureRange(
                        metadata.Length,
                        indexesOffset,
                        checked((long)targetCount * sizeof(ushort)),
                        $"entity {entityIndex} LOD {lodIndex} morph indexes");
                    ushort[] indexes = new ushort[targetCount];
                    for (int index = 0; index < targetCount; index++)
                    {
                        indexes[index] = ReadUInt16(
                            metadata,
                            indexesOffset + index * sizeof(ushort));
                        if (indexes[index] >= morphChannelCount)
                        {
                            throw new InvalidDataException(
                                $"Compiled mesh entity {entityIndex} LOD {lodIndex} references morph channel {indexes[index]}, outside {morphChannelCount} decoded channels.");
                        }
                    }

                    int payloadOffset = ReadRequiredPointer(
                        metadata,
                        rowOffset,
                        $"entity {entityIndex} LOD {lodIndex} morph payload");
                    long elementCount = checked(
                        (long)vertexCount * targetCount);
                    long payloadBytes = checked(
                        elementCount * MorphDeltaElementStride);
                    EnsureRange(
                        metadata.Length,
                        payloadOffset,
                        payloadBytes,
                        $"entity {entityIndex} LOD {lodIndex} morph payload");
                    long decodedBindingBytes = checked(
                        elementCount *
                        (long)(sizeof(float) * 3));
                    decodedDeltaBytes = checked(
                        decodedDeltaBytes + decodedBindingBytes);
                    if (decodedDeltaBytes >
                        limits.MaximumDecodedMorphDeltaBytes)
                    {
                        throw new InvalidDataException(
                            $"Compiled mesh decoded morph deltas exceed the configured {limits.MaximumDecodedMorphDeltaBytes:N0}-byte limit.");
                    }

                    CompiledMorphTargetDeltas[] targets =
                        new CompiledMorphTargetDeltas[targetCount];
                    for (int localTargetIndex = 0;
                         localTargetIndex < targetCount;
                         localTargetIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Vector3[] deltas = new Vector3[vertexCount];
                        int targetOffset = checked(
                            payloadOffset +
                            localTargetIndex *
                            vertexCount *
                            MorphDeltaElementStride);
                        for (int vertexIndex = 0;
                             vertexIndex < vertexCount;
                             vertexIndex++)
                        {
                            if ((vertexIndex & 0xFFF) == 0)
                            {
                                cancellationToken
                                    .ThrowIfCancellationRequested();
                            }

                            int elementOffset = checked(
                                targetOffset +
                                vertexIndex *
                                MorphDeltaElementStride);
                            short w = ReadInt16(
                                metadata,
                                elementOffset + sizeof(short) * 3);
                            if (w != 0)
                            {
                                throw new InvalidDataException(
                                    $"Compiled mesh entity {entityIndex} LOD {lodIndex} morph target {localTargetIndex} vertex {vertexIndex} has unsupported nonzero SHORT4 W value {w}.");
                            }

                            deltas[vertexIndex] = new Vector3(
                                ReadInt16(metadata, elementOffset) *
                                    MorphDeltaScale,
                                ReadInt16(
                                    metadata,
                                    elementOffset + sizeof(short)) *
                                    MorphDeltaScale,
                                ReadInt16(
                                    metadata,
                                    elementOffset + sizeof(short) * 2) *
                                    MorphDeltaScale);
                        }

                        targets[localTargetIndex] =
                            new CompiledMorphTargetDeltas(
                                localTargetIndex,
                                indexes[localTargetIndex],
                                deltas);
                    }

                    result.Add(new CompiledNodeMorphBinding(
                        entityIndex,
                        lodIndex,
                        vertexCount,
                        MorphDeltaElementStride,
                        payloadOffset,
                        CompiledMorphDeltaFormat
                            .SignedShort4Scale16384,
                        indexes,
                        targets));
                }
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESHG008",
                    CompactMeshDiagnosticSeverity.Error,
                    exception.Message,
                    entityIndex));
            }
        }

        return result;
    }

    private static CompiledMaterialDatabase ReadMaterialDatabase(
        ReadOnlySpan<byte> metadata,
        CompiledMeshDecodeLimits limits,
        List<CompactMeshDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ulong pointer = ReadUInt64(
            metadata,
            MaterialSlotTablePointerOffset);
        if (pointer == 0)
        {
            return CompiledMaterialDatabase.Empty;
        }

        int holderOffset;
        try
        {
            holderOffset = DecodePointer(
                pointer,
                metadata.Length,
                "material database holder");
            EnsureRange(
                metadata.Length,
                holderOffset,
                MaterialDatabaseHeaderSize,
                "material database holder");
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG009",
                CompactMeshDiagnosticSeverity.Error,
                exception.Message));
            return CompiledMaterialDatabase.Empty;
        }

        int slotCount = ReadUInt16(metadata, holderOffset + 8);
        int entryCount = ReadUInt16(metadata, holderOffset + 10);
        if (slotCount > limits.MaximumMaterialDatabaseEntries ||
            entryCount > limits.MaximumMaterialDatabaseEntries)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG010",
                CompactMeshDiagnosticSeverity.Error,
                $"Material database declares {slotCount} slots and {entryCount} entries, exceeding the configured {limits.MaximumMaterialDatabaseEntries}-entry limit."));
            return new CompiledMaterialDatabase(
                slotCount,
                entryCount,
                Array.Empty<CompiledMaterialDatabaseEntry>());
        }

        if (entryCount < slotCount)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG010",
                CompactMeshDiagnosticSeverity.Error,
                $"Material database declares {slotCount} slots but only {entryCount} entries."));
        }

        if (entryCount == 0)
        {
            return new CompiledMaterialDatabase(
                slotCount,
                entryCount,
                Array.Empty<CompiledMaterialDatabaseEntry>());
        }

        int entriesOffset;
        try
        {
            entriesOffset = ReadRequiredPointer(
                metadata,
                holderOffset,
                "material database entries");
            EnsureRange(
                metadata.Length,
                entriesOffset,
                checked((long)entryCount * MaterialDatabaseEntryStride),
                "material database entries");
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG011",
                CompactMeshDiagnosticSeverity.Error,
                exception.Message));
            return new CompiledMaterialDatabase(
                slotCount,
                entryCount,
                Array.Empty<CompiledMaterialDatabaseEntry>());
        }

        List<CompiledMaterialDatabaseEntry> entries = new(entryCount);
        for (int index = 0; index < entryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int entryOffset = checked(
                entriesOffset + index * MaterialDatabaseEntryStride);
            try
            {
                string databaseName = ReadStringAtPointer(
                    metadata,
                    entryOffset,
                    $"material database entry {index} name");
                entries.Add(new CompiledMaterialDatabaseEntry(
                    index,
                    databaseName,
                    ReadUInt32(metadata, entryOffset + 16)));
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new CompactMeshDiagnostic(
                    "CMESHG012",
                    CompactMeshDiagnosticSeverity.Error,
                    exception.Message));
            }
        }

        return new CompiledMaterialDatabase(
            slotCount,
            entryCount,
            entries);
    }

    private static List<string> ExtractVariantNames(
        ReadOnlySpan<byte> data,
        CompiledMeshDecodeLimits limits)
    {
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int start = -1;
        for (int index = 0; index <= data.Length; index++)
        {
            bool printable =
                index < data.Length &&
                data[index] is >= 32 and <= 126;
            if (printable && start < 0)
            {
                start = index;
            }
            else if (!printable && start >= 0)
            {
                int length = index - start;
                if (length is >= 4 and <= MaximumTextBytes)
                {
                    string value = Encoding.ASCII.GetString(
                        data.Slice(start, length));
                    if (value.Any(char.IsLetter) &&
                        seen.Add(value))
                    {
                        if (result.Count >= limits.MaximumVariantNames)
                        {
                            throw new InvalidDataException(
                                "Compiled mesh variant-name count exceeds the configured limit.");
                        }

                        result.Add(value);
                    }
                }

                start = -1;
            }
        }

        return result;
    }

    private static List<CompiledMeshSkinDefinition> ReadSkinDefinitions(
        ReadOnlySpan<byte> data,
        int entityCount,
        CompiledMaterialDatabase materialDatabase,
        CompiledMeshDecodeLimits limits,
        List<CompactMeshDiagnostic> diagnostics,
        CancellationToken cancellationToken,
        out bool exactSkinPayload)
    {
        exactSkinPayload = false;
        if (data.Length < SkinHeaderSize)
        {
            return [];
        }

        int count = ReadUInt16(data, 0);
        int tableOffset = ReadInt32(data, 4);
        long tableLength = checked((long)count * SkinDefinitionStride);
        bool plausibleTable =
            tableOffset >= SkinHeaderSize &&
            tableOffset <= data.Length &&
            tableLength <= data.Length - (long)tableOffset;
        if (!plausibleTable)
        {
            return [];
        }

        exactSkinPayload = true;
        try
        {
            if (count > limits.MaximumSkinDefinitions ||
                count > limits.MaximumVariantNames)
            {
                throw new InvalidDataException(
                    $"Compiled mesh skin-definition count {count} exceeds the configured limit.");
            }

            List<CompiledMeshSkinDefinition> result = new(count);
            long aggregateOverrideCount = 0;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rowOffset = checked(
                    tableOffset + index * SkinDefinitionStride);
                string name = ReadStringAtRelativeInt32Pointer(
                    data,
                    rowOffset,
                    rowOffset,
                    $"skin definition {index} name");

                ushort rawFeatures = ReadUInt16(data, rowOffset + 24);
                int materialCount = data[rowOffset + 26];
                int entityOverrideCount = data[rowOffset + 27];
                int surfaceOverrideCount = data[rowOffset + 28];
                int randomizedChildCount = data[rowOffset + 29];
                aggregateOverrideCount = checked(
                    aggregateOverrideCount +
                    materialCount +
                    entityOverrideCount +
                    surfaceOverrideCount +
                    randomizedChildCount);
                if (aggregateOverrideCount >
                    limits.MaximumSkinOverrides)
                {
                    throw new InvalidDataException(
                        $"Compiled mesh skin overrides exceed the configured {limits.MaximumSkinOverrides} entry limit.");
                }

                List<CompiledMeshSkinMaterialOverride>
                    materialOverrides = ReadSkinMaterialOverrides(
                        data,
                        rowOffset,
                        materialCount,
                        materialDatabase);
                List<CompiledMeshSkinEntityOverride> entityOverrides =
                    ReadSkinEntityOverrides(
                        data,
                        rowOffset,
                        entityOverrideCount,
                        entityCount);
                ValidateOptionalSkinArrayPointer(
                    data,
                    rowOffset,
                    rowOffset + 40,
                    surfaceOverrideCount,
                    $"skin definition {index} surface overrides");
                ValidateOptionalSkinArrayPointer(
                    data,
                    rowOffset,
                    rowOffset + 44,
                    randomizedChildCount,
                    $"skin definition {index} randomized children");
                result.Add(new CompiledMeshSkinDefinition(
                    index,
                    name,
                    rawFeatures,
                    materialOverrides,
                    entityOverrides,
                    surfaceOverrideCount,
                    randomizedChildCount));
            }

            return result;
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG015",
                CompactMeshDiagnosticSeverity.Error,
                exception.Message));
            return [];
        }
        catch (OverflowException exception)
        {
            diagnostics.Add(new CompactMeshDiagnostic(
                "CMESHG015",
                CompactMeshDiagnosticSeverity.Error,
                $"Compiled mesh skin definitions overflow a bounded range: {exception.Message}"));
            return [];
        }
    }

    private static List<CompiledMeshSkinMaterialOverride>
        ReadSkinMaterialOverrides(
            ReadOnlySpan<byte> data,
            int rowOffset,
            int count,
            CompiledMaterialDatabase materialDatabase)
    {
        if (count == 0)
        {
            return [];
        }

        int arrayOffset = ReadRelativeInt32Pointer(
            data,
            rowOffset,
            rowOffset + 32,
            "skin material overrides");
        EnsureRange(
            data.Length,
            arrayOffset,
            checked((long)count * 4),
            "skin material overrides");
        List<CompiledMeshSkinMaterialOverride> result = new(count);
        for (int index = 0; index < count; index++)
        {
            int offset = checked(arrayOffset + index * 4);
            int targetSlot = ReadUInt16(data, offset);
            int replacementEntry = ReadUInt16(data, offset + 2);
            if (targetSlot >= materialDatabase.DeclaredSlotCount)
            {
                throw new InvalidDataException(
                    $"Compiled mesh skin material override {index} targets slot {targetSlot}, outside {materialDatabase.DeclaredSlotCount} declared slots.");
            }

            if (replacementEntry >=
                materialDatabase.DeclaredEntryCount)
            {
                throw new InvalidDataException(
                    $"Compiled mesh skin material override {index} selects database entry {replacementEntry}, outside {materialDatabase.DeclaredEntryCount} declared entries.");
            }

            result.Add(new CompiledMeshSkinMaterialOverride(
                targetSlot,
                replacementEntry));
        }

        return result;
    }

    private static List<CompiledMeshSkinEntityOverride>
        ReadSkinEntityOverrides(
            ReadOnlySpan<byte> data,
            int rowOffset,
            int count,
            int entityCount)
    {
        if (count == 0)
        {
            return [];
        }

        int arrayOffset = ReadRelativeInt32Pointer(
            data,
            rowOffset,
            rowOffset + 36,
            "skin entity overrides");
        EnsureRange(
            data.Length,
            arrayOffset,
            checked((long)count * sizeof(ushort)),
            "skin entity overrides");
        List<CompiledMeshSkinEntityOverride> result = new(count);
        for (int index = 0; index < count; index++)
        {
            ushort rawValue = ReadUInt16(
                data,
                checked(arrayOffset + index * sizeof(ushort)));
            int entityIndex = rawValue & 0x3FFF;
            if (entityIndex >= entityCount)
            {
                throw new InvalidDataException(
                    $"Compiled mesh skin entity override {index} selects entity {entityIndex}, outside {entityCount} hierarchy entities.");
            }

            result.Add(new CompiledMeshSkinEntityOverride(
                entityIndex,
                rawValue));
        }

        return result;
    }

    private static void ValidateOptionalSkinArrayPointer(
        ReadOnlySpan<byte> data,
        int rowOffset,
        int pointerOffset,
        int count,
        string label)
    {
        if (count == 0)
        {
            return;
        }

        int arrayOffset = ReadRelativeInt32Pointer(
            data,
            rowOffset,
            pointerOffset,
            label);
        EnsureRange(data.Length, arrayOffset, 1, label);
    }

    private static string ReadStringAtRelativeInt32Pointer(
        ReadOnlySpan<byte> data,
        int relativeBaseOffset,
        int pointerOffset,
        string label)
    {
        int offset = ReadRelativeInt32Pointer(
            data,
            relativeBaseOffset,
            pointerOffset,
            label);
        return ReadNullTerminatedUtf8(data, offset, label);
    }

    private static int ReadRelativeInt32Pointer(
        ReadOnlySpan<byte> data,
        int relativeBaseOffset,
        int pointerOffset,
        string label)
    {
        int relativeOffset = ReadInt32(data, pointerOffset);
        long target = (long)relativeBaseOffset + relativeOffset;
        if (target < 0 || target >= data.Length)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} relative pointer {relativeOffset} from 0x{relativeBaseOffset:X} is outside 0x{data.Length:X} bytes.");
        }

        return checked((int)target);
    }

    private static string ReadStringAtPointer(
        ReadOnlySpan<byte> data,
        int pointerOffset,
        string label)
    {
        int offset = ReadRequiredPointer(data, pointerOffset, label);
        return ReadNullTerminatedUtf8(data, offset, label);
    }

    private static string ReadNullTerminatedUtf8(
        ReadOnlySpan<byte> data,
        int offset,
        string label)
    {
        int available = Math.Min(MaximumTextBytes, data.Length - offset);
        int terminator = data.Slice(offset, available).IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} is not NUL terminated.");
        }

        try
        {
            return StrictUtf8.GetString(data.Slice(offset, terminator));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} is not valid UTF-8.",
                exception);
        }
    }

    private static int ReadRequiredPointer(
        ReadOnlySpan<byte> data,
        int offset,
        string label) =>
        DecodePointer(ReadUInt64(data, offset), data.Length, label);

    private static int ReadOptionalPointer(
        ReadOnlySpan<byte> data,
        int offset,
        string label)
    {
        ulong value = ReadUInt64(data, offset);
        return value == 0
            ? -1
            : DecodePointer(value, data.Length, label);
    }

    private static int DecodePointer(
        ulong value,
        int dataLength,
        string label)
    {
        if (value == 0 || value - 1 >= (ulong)dataLength)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} pointer 0x{value:X} is invalid.");
        }

        return checked((int)(value - 1));
    }

    private static int ReadBoundedCount(
        ReadOnlySpan<byte> data,
        int offset,
        string label,
        int maximum)
    {
        int value = ReadInt32(data, offset);
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} count {value} is unsafe.");
        }

        return value;
    }

    private static int ReadNonNegativeUInt32(
        ReadOnlySpan<byte> data,
        int offset,
        string label)
    {
        uint value = ReadUInt32(data, offset);
        if (value > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} 0x{value:X} is unsafe.");
        }

        return (int)value;
    }

    private static void EnsureRange(
        int totalLength,
        int offset,
        long length,
        string label)
    {
        if (offset < 0 ||
            length < 0 ||
            offset > totalLength ||
            length > totalLength - (long)offset)
        {
            throw new InvalidDataException(
                $"Compiled mesh {label} range 0x{offset:X}+0x{length:X} is outside 0x{totalLength:X} bytes.");
        }
    }

    private static int GetFormatSize(byte format) => format switch
    {
        (byte)CompiledVertexFormat.Float3 => 12,
        (byte)CompiledVertexFormat.Byte4 => 4,
        (byte)CompiledVertexFormat.Half2 => 4,
        (byte)CompiledVertexFormat.Half4 => 8,
        (byte)CompiledVertexFormat.SignedNormalizedByte4 => 4,
        _ => 0,
    };

    private static Vector4 ReadUnormByte4(
        ReadOnlySpan<byte> data,
        int offset) =>
        new(
            data[offset] / 255f,
            data[offset + 1] / 255f,
            data[offset + 2] / 255f,
            data[offset + 3] / 255f);

    private static float ReadSNormByte(byte value) =>
        Math.Max((sbyte)value / 127f, -1f);

    private static Vector3 NormalizeOrDefault(
        Vector3 value,
        Vector3 fallback) =>
        value.LengthSquared() > 0.0000001f
            ? Vector3.Normalize(value)
            : fallback;

    private static float ReadHalf(
        ReadOnlySpan<byte> data,
        int offset) =>
        (float)BitConverter.UInt16BitsToHalf(
            ReadUInt16(data, offset));

    private static float ReadSingle(
        ReadOnlySpan<byte> data,
        int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(data, offset));

    private static short ReadInt16(
        ReadOnlySpan<byte> data,
        int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);

    private static ushort ReadUInt16(
        ReadOnlySpan<byte> data,
        int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    private static int ReadInt32(
        ReadOnlySpan<byte> data,
        int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    private static uint ReadUInt32(
        ReadOnlySpan<byte> data,
        int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static ulong ReadUInt64(
        ReadOnlySpan<byte> data,
        int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
}
