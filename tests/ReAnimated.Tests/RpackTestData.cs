using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace ReAnimated.Tests;

public enum RpackTestCompression
{
    None,
    Zlib,
    Lzma,
}

internal sealed record RpackTestItem(
    short LogicalType,
    byte[] Payload);

internal sealed record CompiledMeshTestFixture(
    byte[] Metadata,
    byte[] Variants,
    byte[] Vertices,
    byte[] Indices);

internal static class RpackTestData
{
    internal const int CompiledMeshMaterialDatabaseHolderOffset = 0x3B0;

    internal const int CompiledMeshMaterialDatabaseEntriesOffset = 0x3C0;

    internal const int CompiledMeshMorphLodTableOffset = 0x2E0;

    internal const int CompiledMeshMorphPayloadOffset = 0x260;

    public static async Task<string> WriteArchiveAsync(
        string directory,
        string resourceName,
        short resourceType,
        IReadOnlyList<RpackTestItem> items,
        RpackTestCompression compression)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.rpack");
        byte[] archive = BuildArchive(
            resourceName,
            resourceType,
            items,
            compression);
        await File.WriteAllBytesAsync(path, archive);
        return path;
    }

    public static byte[] BuildArchive(
        string resourceName,
        short resourceType,
        IReadOnlyList<RpackTestItem> items,
        RpackTestCompression compression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(items);
        byte[] logical = items.SelectMany(static item => item.Payload).ToArray();
        byte[] stored = compression switch
        {
            RpackTestCompression.None => logical,
            RpackTestCompression.Zlib => CompressZlib(logical),
            RpackTestCompression.Lzma => CompressLzma(logical),
            _ => throw new ArgumentOutOfRangeException(nameof(compression)),
        };
        int compressionFlags = compression switch
        {
            RpackTestCompression.None => 0,
            RpackTestCompression.Zlib => 1,
            RpackTestCompression.Lzma => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(compression)),
        };
        byte[] name = Encoding.UTF8.GetBytes(resourceName + '\0');
        int tableSize =
            36 +
            20 +
            items.Count * 16 +
            12 +
            4 +
            name.Length;
        byte[] result = GC.AllocateUninitializedArray<byte>(
            tableSize + stored.Length);
        Span<byte> data = result;
        "RP6L"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], compressionFlags);
        BinaryPrimitives.WriteInt32LittleEndian(data[12..], items.Count);
        BinaryPrimitives.WriteInt32LittleEndian(data[16..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[24..], name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data[28..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[32..], 1);

        int cursor = 36;
        BinaryPrimitives.WriteUInt16LittleEndian(data[cursor..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data[(cursor + 2)..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(cursor + 4)..],
            checked((uint)tableSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(cursor + 8)..],
            checked((uint)logical.Length));
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(cursor + 12)..],
            compression == RpackTestCompression.None ? 0 : stored.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data[(cursor + 16)..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data[(cursor + 18)..], 2);
        cursor += 20;

        int logicalOffset = 0;
        foreach (RpackTestItem item in items)
        {
            data[cursor] = 0;
            data[cursor + 1] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(
                data[(cursor + 2)..],
                item.LogicalType);
            BinaryPrimitives.WriteUInt32LittleEndian(
                data[(cursor + 4)..],
                checked((uint)logicalOffset));
            BinaryPrimitives.WriteInt32LittleEndian(
                data[(cursor + 8)..],
                item.Payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(
                data[(cursor + 12)..],
                0);
            logicalOffset += item.Payload.Length;
            cursor += 16;
        }

        BinaryPrimitives.WriteInt16LittleEndian(
            data[cursor..],
            checked((short)items.Count));
        BinaryPrimitives.WriteInt16LittleEndian(
            data[(cursor + 2)..],
            resourceType);
        BinaryPrimitives.WriteInt32LittleEndian(data[(cursor + 4)..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[(cursor + 8)..], 0);
        cursor += 12;
        BinaryPrimitives.WriteInt32LittleEndian(data[cursor..], 0);
        cursor += 4;
        name.CopyTo(data[cursor..]);
        stored.CopyTo(data[tableSize..]);
        return result;
    }

    public static byte[] BuildCompactMeshPayload()
    {
        const int tableOffset = 0xB0;
        const int stride = 0xD0;
        (string Name, short Parent, byte Type, byte Children)[] entities =
        [
            ("bip01", -1, 8, 1),
            ("pelvis", 0, 8, 1),
            ("refcamera", 1, 4, 0),
            ("body", -1, 2, 0),
        ];
        int namesSize = entities.Sum(static entity =>
            Encoding.UTF8.GetByteCount(entity.Name) + 1);
        byte[] payload = new byte[
            tableOffset + stride * entities.Length + namesSize];
        Span<byte> data = payload;
        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x08..],
            tableOffset + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[0x64..],
            checked((uint)entities.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x68..], 2);
        int nameOffset = tableOffset + stride * entities.Length;
        for (int index = 0; index < entities.Length; index++)
        {
            int rowOffset = tableOffset + index * stride;
            Span<byte> row = data.Slice(rowOffset, stride);
            WriteIdentityMatrix(row);
            WriteIdentityMatrix(row[0x30..]);
            BinaryPrimitives.WriteInt32LittleEndian(
                row[0x0C..],
                BitConverter.SingleToInt32Bits(index));
            BinaryPrimitives.WriteUInt64LittleEndian(
                row[0x78..],
                checked((ulong)nameOffset + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(row[0xC0..], 0);
            BinaryPrimitives.WriteInt16LittleEndian(
                row[0xC6..],
                entities[index].Parent);
            row[0xC8] = entities[index].Type;
            row[0xC9] = entities[index].Children;
            row[0xCA] = entities[index].Type == 2 ? (byte)1 : (byte)0;
            byte[] encoded = Encoding.UTF8.GetBytes(
                entities[index].Name + '\0');
            encoded.CopyTo(data[nameOffset..]);
            nameOffset += encoded.Length;
        }

        return payload;
    }

    public static CompiledMeshTestFixture BuildCompiledMeshFixture(
        bool includeBlendStreams = true)
    {
        const int entityTable = 0xB0;
        const int entityStride = 0xD0;
        const int declarationTable = 0x280;
        const int declarationElements = 0x290;
        const int geometryLodTable = 0x2B0;
        const int morphLodTable = CompiledMeshMorphLodTableOffset;
        const int meshInfo = 0x300;
        const int materialIndexes = 0x340;
        const int paletteTable = 0x350;
        const int faceCounts = 0x360;
        const int paletteValues = 0x370;
        const int morphNames = 0x380;
        const int morphName = 0x390;
        const int morphIndexes = 0x3A0;
        const int morphPayload = CompiledMeshMorphPayloadOffset;
        const int materialName0 = 0x430;
        const int materialName1 = 0x440;
        const int materialName2 = 0x450;
        const int materialName3 = 0x460;
        const int rootName = 0x480;
        const int meshName = 0x490;
        byte[] metadata = new byte[0x4A0];
        Span<byte> data = metadata;
        WritePointer(data, 0x08, entityTable);
        WritePointer(
            data,
            0x18,
            CompiledMeshMaterialDatabaseHolderOffset);
        WritePointer(data, 0x20, morphNames);
        WritePointer(data, 0x50, declarationTable);
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x64..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x68..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x70..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x7C..], 1);

        Span<byte> root = data.Slice(entityTable, entityStride);
        WriteIdentityMatrix(root);
        WriteIdentityMatrix(root[0x30..]);
        WritePointer(root, 0x78, rootName);
        BinaryPrimitives.WriteInt16LittleEndian(root[0xC6..], -1);
        root[0xC8] = 8;

        Span<byte> mesh = data.Slice(
            entityTable + entityStride,
            entityStride);
        WriteIdentityMatrix(mesh);
        WriteIdentityMatrix(mesh[0x30..]);
        WritePointer(mesh, 0x78, meshName);
        WritePointer(mesh, 0x80, morphLodTable);
        WritePointer(mesh, 0x88, geometryLodTable);
        BinaryPrimitives.WriteInt16LittleEndian(mesh[0xC6..], -1);
        mesh[0xC8] = 2;
        mesh[0xCA] = 1;

        WritePointer(data, declarationTable, declarationElements);
        (byte Format, byte Semantic, byte Channel)[] elements =
            includeBlendStreams
                ?
                [
                    (2, 0, 0),
                    (4, 1, 0),
                    (4, 2, 0),
                    (31, 3, 0),
                    (15, 5, 0),
                ]
                :
                [
                    (2, 0, 0),
                    (31, 3, 0),
                    (15, 5, 0),
                ];
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(declarationTable + 8)..],
            elements.Length);
        for (int index = 0; index < elements.Length; index++)
        {
            int offset = declarationElements + index * 4;
            data[offset] = elements[index].Format;
            data[offset + 1] = elements[index].Semantic;
            data[offset + 2] = elements[index].Channel;
        }

        WritePointer(data, geometryLodTable + 8, meshInfo);
        WritePointer(data, geometryLodTable + 16, materialIndexes);
        WritePointer(data, geometryLodTable + 24, paletteTable);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[(geometryLodTable + 34)..],
            1);
        WritePointer(data, meshInfo, faceCounts);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(meshInfo + 24)..],
            0);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(meshInfo + 40)..],
            3);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(meshInfo + 44)..],
            0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[(meshInfo + 48)..],
            1);
        BinaryPrimitives.WriteInt16LittleEndian(
            data[(meshInfo + 50)..],
            0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[materialIndexes..],
            2);
        WritePointer(data, paletteTable, paletteValues);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(paletteTable + 8)..],
            1);
        BinaryPrimitives.WriteInt16LittleEndian(
            data[paletteValues..],
            0);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[faceCounts..],
            3);

        WritePointer(data, morphNames, morphName);
        Encoding.UTF8.GetBytes("smile\0").CopyTo(data[morphName..]);
        WritePointer(data, morphLodTable, morphPayload);
        WritePointer(data, morphLodTable + 8, morphIndexes);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(morphLodTable + 16)..],
            3);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(morphLodTable + 20)..],
            1);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[(morphLodTable + 24)..],
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[morphIndexes..],
            0);
        WriteMorphDelta(data, morphPayload, 16_384, 0, 0);
        WriteMorphDelta(data, morphPayload + 8, 0, -8_192, 4_096);
        WriteMorphDelta(data, morphPayload + 16, 0, 0, 0);
        WritePointer(
            data,
            CompiledMeshMaterialDatabaseHolderOffset,
            CompiledMeshMaterialDatabaseEntriesOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[(CompiledMeshMaterialDatabaseHolderOffset + 8)..],
            3);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[(CompiledMeshMaterialDatabaseHolderOffset + 10)..],
            4);
        (int NameOffset, string Name, uint RawLoadValue)[] materials =
        [
            (materialName0, "characters_body", 0x00000011),
            (materialName1, string.Empty, 0),
            (materialName2, "body_cloth", 0xAABBCCDD),
            (materialName3, "body_cloth_wet", 0x01020304),
        ];
        for (int index = 0; index < materials.Length; index++)
        {
            int entryOffset =
                CompiledMeshMaterialDatabaseEntriesOffset + index * 24;
            WritePointer(data, entryOffset, materials[index].NameOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(
                data[(entryOffset + 16)..],
                materials[index].RawLoadValue);
            Encoding.UTF8
                .GetBytes(materials[index].Name + '\0')
                .CopyTo(data[materials[index].NameOffset..]);
        }

        Encoding.UTF8.GetBytes("root\0").CopyTo(data[rootName..]);
        Encoding.UTF8.GetBytes("body\0").CopyTo(data[meshName..]);

        int vertexStride = includeBlendStreams ? 28 : 20;
        byte[] vertices = new byte[3 * vertexStride];
        WriteTestVertex(
            vertices.AsSpan(0, vertexStride),
            0,
            0,
            0,
            0,
            0,
            includeBlendStreams);
        WriteTestVertex(
            vertices.AsSpan(vertexStride, vertexStride),
            1,
            0,
            0,
            1,
            0,
            includeBlendStreams);
        WriteTestVertex(
            vertices.AsSpan(vertexStride * 2, vertexStride),
            0,
            1,
            0,
            0,
            1,
            includeBlendStreams);
        byte[] indices = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(indices, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(4), 2);
        return new CompiledMeshTestFixture(
            metadata,
            Encoding.ASCII.GetBytes("Default\0Cult\0"),
            vertices,
            indices);
    }

    public static byte[] BuildCompiledMeshSkinPayload(
        string name,
        IReadOnlyList<(ushort TargetSlot, ushort ReplacementEntry)>
            materialOverrides,
        IReadOnlyList<ushort> entityOverrides,
        ushort rawFeatures = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(materialOverrides);
        ArgumentNullException.ThrowIfNull(entityOverrides);
        if (materialOverrides.Count > byte.MaxValue ||
            entityOverrides.Count > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(materialOverrides),
                "Synthetic compact-mesh skin arrays must fit their byte counts.");
        }

        const int tableOffset = 8;
        const int rowOffset = tableOffset;
        const int rowSize = 48;
        int materialOffset = rowOffset + rowSize;
        int entityOffset = checked(
            materialOffset + materialOverrides.Count * 4);
        int nameOffset = checked(
            entityOffset + entityOverrides.Count * sizeof(ushort));
        byte[] encodedName = Encoding.UTF8.GetBytes(name + '\0');
        byte[] payload = new byte[checked(
            nameOffset + encodedName.Length)];
        Span<byte> data = payload;
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[4..],
            tableOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            data[rowOffset..],
            nameOffset - rowOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data[(rowOffset + 24)..],
            rawFeatures);
        data[rowOffset + 26] =
            checked((byte)materialOverrides.Count);
        data[rowOffset + 27] =
            checked((byte)entityOverrides.Count);
        if (materialOverrides.Count > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                data[(rowOffset + 32)..],
                materialOffset - rowOffset);
        }

        if (entityOverrides.Count > 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                data[(rowOffset + 36)..],
                entityOffset - rowOffset);
        }

        for (int index = 0;
             index < materialOverrides.Count;
             index++)
        {
            int offset = checked(materialOffset + index * 4);
            BinaryPrimitives.WriteUInt16LittleEndian(
                data[offset..],
                materialOverrides[index].TargetSlot);
            BinaryPrimitives.WriteUInt16LittleEndian(
                data[(offset + 2)..],
                materialOverrides[index].ReplacementEntry);
        }

        for (int index = 0;
             index < entityOverrides.Count;
             index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                data[(entityOffset + index * sizeof(ushort))..],
                entityOverrides[index]);
        }

        encodedName.CopyTo(data[nameOffset..]);
        return payload;
    }

    public static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dlr-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string allowedRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "dlr-tests"));
        if (!fullPath.StartsWith(
                allowedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to delete a directory outside the test root.");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static byte[] CompressZlib(byte[] value)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(
                   output,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            zlib.Write(value);
        }

        return output.ToArray();
    }

    private static void WriteMorphDelta(
        Span<byte> data,
        int offset,
        short x,
        short y,
        short z)
    {
        BinaryPrimitives.WriteInt16LittleEndian(data[offset..], x);
        BinaryPrimitives.WriteInt16LittleEndian(data[(offset + 2)..], y);
        BinaryPrimitives.WriteInt16LittleEndian(data[(offset + 4)..], z);
        BinaryPrimitives.WriteInt16LittleEndian(data[(offset + 6)..], 0);
    }

    private static byte[] CompressLzma(byte[] value)
    {
        using MemoryStream output = new();
        using (LzmaStream lzma = LzmaStream.Create(
                   new LzmaEncoderProperties(
                       eos: false,
                       dictionary: 1 << 16),
                   isLzma2: false,
                   output))
        {
            lzma.Write(value);
        }

        return output.ToArray();
    }

    private static void WriteIdentityMatrix(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(1));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[20..],
            BitConverter.SingleToInt32Bits(1));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[40..],
            BitConverter.SingleToInt32Bits(1));
    }

    private static void WritePointer(
        Span<byte> destination,
        int pointerFieldOffset,
        int targetOffset) =>
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[pointerFieldOffset..],
            checked((ulong)targetOffset + 1));

    private static void WriteTestVertex(
        Span<byte> destination,
        float x,
        float y,
        float z,
        float u,
        float v,
        bool includeBlendStreams)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[4..],
            BitConverter.SingleToInt32Bits(y));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[8..],
            BitConverter.SingleToInt32Bits(z));
        int normalOffset;
        int textureCoordinateOffset;
        if (includeBlendStreams)
        {
            destination[12] = byte.MaxValue;
            normalOffset = 20;
            textureCoordinateOffset = 24;
        }
        else
        {
            normalOffset = 12;
            textureCoordinateOffset = 16;
        }

        destination[normalOffset] = 0;
        destination[normalOffset + 1] = 0;
        destination[normalOffset + 2] = 127;
        destination[normalOffset + 3] = 127;
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[textureCoordinateOffset..],
            BitConverter.HalfToUInt16Bits((Half)u));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination[(textureCoordinateOffset + 2)..],
            BitConverter.HalfToUInt16Bits((Half)v));
    }
}
