using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1TrailingTriangleEvidenceTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public InstalledDl1TrailingTriangleEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("survivor_woman_a")]
    [InlineData("survivor_woman_b")]
    [InlineData("SURVIVOR_WOMAN_A")]
    public void ExactRetailRuntimeCorrectionExcludesOnlyThePaddingTriangle(
        string resourceName)
    {
        CompiledMeshTestFixture fixture =
            BuildSyntheticScHeadFixture();

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                fixture.Metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices,
                retailResourceName: resourceName);

        Assert.DoesNotContain(
            geometry.Diagnostics,
            static diagnostic =>
                diagnostic.Severity ==
                CompactMeshDiagnosticSeverity.Error);
        CompiledMeshSurface lod1 = Assert.Single(
            geometry.Surfaces,
            static surface => surface.LodIndex == 1);
        Assert.Equal("SC_head", lod1.Name);
        Assert.Equal(1_365, lod1.Indices.Count);
        Assert.Equal(1_365, Assert.Single(lod1.Submeshes).IndexCount);
        Assert.All(
            lod1.Indices,
            static value => Assert.InRange(value, (ushort)0, (ushort)2));
        CompactMeshDiagnostic correction = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG014");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Information,
            correction.Severity);
        Assert.Contains(
            "serialized 1368, effective 1365",
            correction.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("survivor_woman_c", "SC_head", 1_368)]
    [InlineData("survivor_woman_a", "SC_head_extra", 1_368)]
    [InlineData("survivor_woman_a", "SC_head", 1_367)]
    public void RuntimeCorrectionDoesNotGeneralizeBeyondTheEnginePredicate(
        string resourceName,
        string entityName,
        int serializedIndexCount)
    {
        CompiledMeshTestFixture fixture =
            BuildSyntheticScHeadFixture(
                entityName,
                serializedIndexCount);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                fixture.Metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices,
                retailResourceName: resourceName);

        Assert.DoesNotContain(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG014");
        Assert.DoesNotContain(
            geometry.Surfaces,
            static surface => surface.LodIndex == 1);
        Assert.Contains(
            geometry.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESHG004" &&
                diagnostic.Message.Contains(
                    "index 1365 references vertex",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task AssetDecoderPassesTheExactRetailResourceIdentity()
    {
        CompiledMeshTestFixture fixture =
            BuildSyntheticScHeadFixture();
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "survivor_woman_a",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(42, fixture.Metadata),
                    new RpackTestItem(42, fixture.Variants),
                    new RpackTestItem(42, [1]),
                    new RpackTestItem(42, fixture.Vertices),
                    new RpackTestItem(42, fixture.Indices),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 8 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 8 * 1024 * 1024,
                    MaximumDiskBytes = 32 * 1024 * 1024,
                });

            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    Assert.Single(archive.Resources),
                    cache);

            Dl1MeshSurface lod1 = Assert.Single(
                mesh.Surfaces,
                static surface => surface.LodIndex == 1);
            Assert.Equal(1_365, lod1.IndexCount);
            Assert.Contains(
                mesh.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "CMESHG014" &&
                    diagnostic.Severity ==
                    Dl1MeshDiagnosticSeverity.Information);
            Assert.DoesNotContain(
                mesh.Diagnostics,
                static diagnostic => diagnostic.Code == "CMESHG004");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact(Timeout = 600_000)]
    public async Task InstalledScHeadIndexTailRemainsAuditable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        if (!string.Equals(
                build.BuildFingerprint,
                ValidatedBuildFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(
                        temporaryDirectory,
                        "cache"),
                    MaximumMemoryBytes = 128L * 1024 * 1024,
                    MaximumMemoryEntryBytes = 64 * 1024 * 1024,
                    MaximumDiskBytes = 2L * 1024 * 1024 * 1024,
                });

            EvidenceControl[] controls =
            [
                new(
                    Path.Combine(
                        install.DataPath,
                        "common_meshes_PC.rpack"),
                    4_357,
                    "survivor_woman_a",
                    "2afc8b378edd2c62030f0a6c2b7325ca83cb8b232c1c13f66bf4b2b7cc23d086",
                    "f826fb80a543a173132d36d09400fd4751df93212fa80f5023a9b5357e89bc99",
                    "315f66725f44",
                    126),
                new(
                    Path.Combine(
                        install.InstallPath,
                        "DW_DLC17",
                        "Data",
                        "wasteland_PC.rpack"),
                    867,
                    "survivor_woman_b",
                    "6d534647a180168583007e1103e69ffdcbd4e10464199c4625081522d134b5e1",
                    "4d49841598af5dfca901fe51b1e4c35298395f2dcb001e241c3942b85242ad05",
                    "30355f66725f",
                    134),
            ];

            foreach (EvidenceControl control in controls)
            {
                Rp6lArchive archive =
                    await Rp6lArchive.OpenAsync(control.PackPath);
                Rp6lResourceDescriptor resource =
                    archive.Resources[control.ResourceIndex];
                Assert.Equal(control.ResourceName, resource.Name);
                Assert.Equal(5, resource.Items.Count);
                byte[][] items = new byte[resource.Items.Count][];
                for (int slot = 0; slot < resource.Items.Count; slot++)
                {
                    items[slot] = await archive.ReadItemBytesAsync(
                        resource.Items[slot],
                        cache,
                        maximumBytes: 512 * 1024 * 1024);
                    Rp6lItemDescriptor descriptor =
                        resource.Items[slot];
                    _output.WriteLine(
                        $"{control.ResourceName} item{slot}: descriptorIndex={descriptor.Index}, chunk={descriptor.ChunkIndex}, storageGroup={descriptor.StorageGroupId}, logicalOffset={descriptor.Offset}, declaredSize={descriptor.SizeOrHash}, bytes={items[slot].Length}, sha256={Convert.ToHexString(SHA256.HashData(items[slot])).ToLowerInvariant()}");
                }

                byte[] metadata = items[0];
                byte[] indexPayload = items[4];
                Assert.Equal(
                    control.MetadataSha256,
                    Convert.ToHexString(
                            SHA256.HashData(metadata))
                        .ToLowerInvariant());
                Assert.Equal(
                    control.IndexSha256,
                    Convert.ToHexString(
                            SHA256.HashData(indexPayload))
                        .ToLowerInvariant());
                List<RawLodRecord> lods =
                    ReadLods(metadata);
                RawLodRecord[] scHead = lods
                    .Where(static record =>
                        string.Equals(
                            record.Name,
                            "sc_head",
                            StringComparison.Ordinal))
                    .OrderBy(static record => record.LodIndex)
                    .ToArray();
                Assert.Collection(
                    scHead,
                    lod0 =>
                    {
                        Assert.Equal(270, lod0.EntityIndex);
                        Assert.Equal(0, lod0.LodIndex);
                        Assert.Equal(500, lod0.VertexCount);
                        Assert.Equal(0, lod0.IndexByteOffset);
                        Assert.Equal(2_295, lod0.IndexCount);
                        Assert.Equal(4_590, checked(
                            lod0.IndexByteOffset +
                            lod0.IndexCount * sizeof(ushort)));
                    },
                    lod1 =>
                    {
                        Assert.Equal(270, lod1.EntityIndex);
                        Assert.Equal(1, lod1.LodIndex);
                        Assert.Equal(20_000, lod1.VertexByteOffset);
                        Assert.Equal(355, lod1.VertexCount);
                        Assert.Equal(40, lod1.VertexStride);
                        Assert.Equal(4_592, lod1.IndexByteOffset);
                        Assert.Equal(1_368, lod1.IndexCount);
                        Assert.Equal("58050000", Hex(
                            lod1.FaceCountBytes));
                    });

                RawLodRecord affected = scHead[1];
                RawLodRecord next = lods
                    .Where(candidate =>
                        candidate.IndexByteOffset >
                        affected.IndexByteOffset)
                    .OrderBy(static candidate =>
                        candidate.IndexByteOffset)
                    .First();
                Assert.Equal("sc_legs_a", next.Name);
                Assert.Equal(0, next.LodIndex);
                Assert.Equal(7_328, next.IndexByteOffset);
                int validByteEnd = checked(
                    affected.IndexByteOffset +
                    1_365 * sizeof(ushort));
                int serializedByteEnd = checked(
                    affected.IndexByteOffset +
                    affected.IndexCount * sizeof(ushort));
                Assert.Equal(7_322, validByteEnd);
                Assert.Equal(next.IndexByteOffset, serializedByteEnd);
                Assert.Equal(
                    next.IndexByteOffset,
                    Align16(validByteEnd));

                ushort[] rawIndices = ReadIndices(
                    indexPayload,
                    affected.IndexByteOffset,
                    affected.IndexCount);
                Assert.All(
                    rawIndices.Take(1_365),
                    value => Assert.InRange(
                        value,
                        (ushort)0,
                        checked((ushort)(affected.VertexCount - 1))));
                Assert.All(
                    rawIndices.Skip(1_365),
                    value => Assert.True(
                        value >= affected.VertexCount,
                        $"Expected alignment padding, but decoded in-range index {value}."));
                byte[] padding = indexPayload
                    .AsSpan(validByteEnd, 6)
                    .ToArray();
                Assert.Equal(
                    control.PaddingHex,
                    Hex(padding));

                CompiledMeshGeometryDocument geometry =
                    CompiledMeshGeometryDecoder.Decode(
                        items[0],
                        items[1],
                        items[3],
                        items[4],
                        retailResourceName:
                            control.ResourceName);
                Assert.Equal(
                    control.ExpectedSurfaceCount,
                    geometry.Surfaces.Count);
                Assert.DoesNotContain(
                    geometry.Diagnostics,
                    static diagnostic =>
                        diagnostic.Severity ==
                        CompactMeshDiagnosticSeverity.Error);
                CompiledMeshSurface decoded = Assert.Single(
                    geometry.Surfaces,
                    static surface =>
                        surface.EntityIndex == 270 &&
                        surface.LodIndex == 1);
                Assert.Equal(1_365, decoded.Indices.Count);
                Assert.Equal(
                    1_365,
                    Assert.Single(decoded.Submeshes).IndexCount);
                Assert.Single(
                    geometry.Diagnostics,
                    static diagnostic =>
                        diagnostic.Code == "CMESHG014");

                _output.WriteLine(
                    $"{control.ResourceName}: raw sc_head LOD1 index span 4592+1368*2, valid prefix=1365, padding={Hex(padding)} ('{Ascii(padding)}'), next surface={next.Name}@{next.IndexByteOffset}, decoded effective count={decoded.Indices.Count}");
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    private static List<RawLodRecord> ReadLods(
        ReadOnlySpan<byte> metadata)
    {
        const int entityStride = 0xD0;
        int entityTable = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[0x08..]));
        int entityCount = BinaryPrimitives.ReadInt32LittleEndian(
            metadata[0x64..]);
        int declarationTable = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[0x50..]));
        int declarationCount =
            BinaryPrimitives.ReadInt32LittleEndian(
                metadata[0x7C..]);
        int[] strides = new int[declarationCount];
        for (int declarationIndex = 0;
             declarationIndex < declarationCount;
             declarationIndex++)
        {
            int row = checked(
                declarationTable + declarationIndex * 16);
            int elementTable = DecodePointer(
                BinaryPrimitives.ReadUInt64LittleEndian(
                    metadata[row..]));
            int elementCount =
                BinaryPrimitives.ReadInt32LittleEndian(
                    metadata[(row + 8)..]);
            for (int elementIndex = 0;
                 elementIndex < elementCount;
                 elementIndex++)
            {
                byte format =
                    metadata[elementTable + elementIndex * 4];
                strides[declarationIndex] = checked(
                    strides[declarationIndex] +
                    GetFormatSize(format));
            }
        }

        List<RawLodRecord> result = [];
        for (int entityIndex = 0;
             entityIndex < entityCount;
             entityIndex++)
        {
            int entity = checked(
                entityTable + entityIndex * entityStride);
            ulong namePointer =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    metadata[(entity + 0x78)..]);
            string name = namePointer == 0
                ? $"entity_{entityIndex}"
                : ReadString(
                    metadata,
                    DecodePointer(namePointer));
            ulong lodTablePointer =
                BinaryPrimitives.ReadUInt64LittleEndian(
                    metadata[(entity + 0x88)..]);
            int lodCount = metadata[entity + 0xCA];
            if (lodTablePointer == 0 || lodCount == 0)
            {
                continue;
            }

            int lodTable = DecodePointer(lodTablePointer);
            for (int lodIndex = 0;
                 lodIndex < lodCount;
                 lodIndex++)
            {
                int lod = checked(
                    lodTable + lodIndex * 0x30);
                ulong meshInfoPointer =
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        metadata[(lod + 8)..]);
                if (meshInfoPointer == 0)
                {
                    continue;
                }

                int meshInfo = DecodePointer(meshInfoPointer);
                int faceCounts = DecodePointer(
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        metadata[meshInfo..]));
                int submeshCount =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        metadata[(meshInfo + 48)..]);
                if (submeshCount == 0)
                {
                    submeshCount =
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            metadata[(lod + 34)..]);
                }

                int declarationGroup =
                    BinaryPrimitives.ReadInt16LittleEndian(
                        metadata[(meshInfo + 50)..]);
                int indexCount = 0;
                for (int submeshIndex = 0;
                     submeshIndex < submeshCount;
                     submeshIndex++)
                {
                    indexCount = checked(
                        indexCount +
                        BinaryPrimitives.ReadInt32LittleEndian(
                            metadata[
                                (faceCounts +
                                 submeshIndex * sizeof(int))..]));
                }

                result.Add(new RawLodRecord(
                    entityIndex,
                    name,
                    lodIndex,
                    BinaryPrimitives.ReadInt32LittleEndian(
                        metadata[(meshInfo + 24)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(
                        metadata[(meshInfo + 40)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(
                        metadata[(meshInfo + 44)..]),
                    indexCount,
                    submeshCount,
                    declarationGroup,
                    strides[declarationGroup],
                    metadata.Slice(lod, 0x30).ToArray(),
                    metadata.Slice(meshInfo, 56).ToArray(),
                    metadata
                        .Slice(
                            faceCounts,
                            submeshCount * sizeof(int))
                        .ToArray()));
            }
        }

        return result;
    }

    private static CompiledMeshTestFixture BuildSyntheticScHeadFixture(
        string entityName = "SC_head",
        int serializedIndexCount = 1_368)
    {
        const int entityTable = 0xB0;
        const int entityStride = 0xD0;
        const int meshEntity = entityTable + entityStride;
        const int geometryLodTable = 0x2B0;
        const int oldMeshInfo = 0x300;
        const int firstMeshInfo = 0x500;
        const int secondMeshInfo = 0x540;
        const int secondFaceCount = 0x580;
        const int materialIndexes = 0x340;
        const int paletteTable = 0x350;
        const int meshName = 0x490;
        const int secondIndexByteOffset = 16;
        const int validIndexCount = 1_365;

        CompiledMeshTestFixture source =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = new byte[0x600];
        source.Metadata.CopyTo(metadata, 0);
        source.Metadata
            .AsSpan(oldMeshInfo, 56)
            .CopyTo(metadata.AsSpan(firstMeshInfo));
        metadata.AsSpan(geometryLodTable + 0x30, 0x30).Clear();
        WritePointer(
            metadata,
            geometryLodTable + 8,
            firstMeshInfo);
        WritePointer(
            metadata,
            geometryLodTable + 0x30 + 8,
            secondMeshInfo);
        WritePointer(
            metadata,
            geometryLodTable + 0x30 + 16,
            materialIndexes);
        WritePointer(
            metadata,
            geometryLodTable + 0x30 + 24,
            paletteTable);
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata.AsSpan(
                geometryLodTable + 0x30 + 34),
            1);

        metadata
            .AsSpan(firstMeshInfo, 56)
            .CopyTo(metadata.AsSpan(secondMeshInfo));
        WritePointer(
            metadata,
            secondMeshInfo,
            secondFaceCount);
        BinaryPrimitives.WriteInt32LittleEndian(
            metadata.AsSpan(secondMeshInfo + 24),
            0);
        BinaryPrimitives.WriteInt32LittleEndian(
            metadata.AsSpan(secondMeshInfo + 40),
            3);
        BinaryPrimitives.WriteInt32LittleEndian(
            metadata.AsSpan(secondMeshInfo + 44),
            secondIndexByteOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            metadata.AsSpan(secondFaceCount),
            serializedIndexCount);

        BinaryPrimitives.WriteUInt64LittleEndian(
            metadata.AsSpan(meshEntity + 0x80),
            0);
        metadata[meshEntity + 0xCA] = 2;
        metadata.AsSpan(meshName, 16).Clear();
        Encoding.UTF8
            .GetBytes(entityName + '\0')
            .CopyTo(metadata.AsSpan(meshName));

        byte[] indices = new byte[
            checked(
                secondIndexByteOffset +
                serializedIndexCount * sizeof(ushort))];
        BinaryPrimitives.WriteUInt16LittleEndian(
            indices,
            0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            indices.AsSpan(2),
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            indices.AsSpan(4),
            2);
        for (int index = 0;
             index < Math.Min(
                 validIndexCount,
                 serializedIndexCount);
             index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                indices.AsSpan(
                    secondIndexByteOffset +
                    index * sizeof(ushort)),
                checked((ushort)(index % 3)));
        }

        if (serializedIndexCount > validIndexCount)
        {
            ReadOnlySpan<byte> padding =
                "05_fr_"u8;
            int paddingBytes = Math.Min(
                padding.Length,
                checked(
                    (serializedIndexCount - validIndexCount) *
                    sizeof(ushort)));
            padding[..paddingBytes].CopyTo(
                indices.AsSpan(
                    secondIndexByteOffset +
                    validIndexCount * sizeof(ushort)));
        }

        return new CompiledMeshTestFixture(
            metadata,
            source.Variants,
            source.Vertices,
            indices);
    }

    private static void WritePointer(
        Span<byte> payload,
        int fieldOffset,
        int targetOffset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload[fieldOffset..],
            checked((ulong)targetOffset + 1));
    }

    private static ushort[] ReadIndices(
        ReadOnlySpan<byte> payload,
        int offset,
        int count)
    {
        ushort[] result = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            result[index] =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    payload[
                        (offset +
                         index * sizeof(ushort))..]);
        }

        return result;
    }

    private static int DecodePointer(ulong pointer) =>
        checked((int)(pointer - 1));

    private static int Align16(int value) =>
        checked((value + 15) & ~15);

    private static string ReadString(
        ReadOnlySpan<byte> payload,
        int offset)
    {
        int length = payload[offset..].IndexOf((byte)0);
        return Encoding.UTF8.GetString(
            payload.Slice(offset, length));
    }

    private static int GetFormatSize(byte format) =>
        format switch
        {
            2 => 12,
            4 => 4,
            15 => 4,
            16 => 8,
            31 => 4,
            _ => throw new InvalidDataException(
                $"Unsupported evidence format {format}."),
        };

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Ascii(ReadOnlySpan<byte> bytes)
    {
        StringBuilder result = new(bytes.Length);
        foreach (byte value in bytes)
        {
            result.Append(
                value is >= 0x20 and <= 0x7E
                    ? (char)value
                    : '.');
        }

        return result.ToString();
    }

    private sealed record EvidenceControl(
        string PackPath,
        int ResourceIndex,
        string ResourceName,
        string MetadataSha256,
        string IndexSha256,
        string PaddingHex,
        int ExpectedSurfaceCount);

    private sealed record RawLodRecord(
        int EntityIndex,
        string Name,
        int LodIndex,
        int VertexByteOffset,
        int VertexCount,
        int IndexByteOffset,
        int IndexCount,
        int SubmeshCount,
        int DeclarationGroup,
        int VertexStride,
        byte[] LodBytes,
        byte[] MeshInfoBytes,
        byte[] FaceCountBytes);
}
