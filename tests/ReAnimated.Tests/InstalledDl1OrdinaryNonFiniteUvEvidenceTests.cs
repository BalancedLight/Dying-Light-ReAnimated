using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1OrdinaryNonFiniteUvEvidenceTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private static readonly Control[] Controls =
    [
        new(
            @"DW_DLC49\Data\hellraid_PC.rpack",
            219,
            "furniture_weapon_rack_a",
            "furniture_bookshelf_a.mat",
            "9704fc19b87038046287a11dde300a48c40d565e38df2644accdd573502c6456",
            56,
            47,
            23,
            51,
            32),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            2_717,
            "ot_glass_a",
            "ot_glass_a.mat",
            "dc630a7f9425b5a7680682bbb8f49826eec3f2a51c94e4f33ede92100d9fb38d",
            16,
            8,
            8,
            16,
            16),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            3_900,
            "slums_cs_terrain_horizon_a",
            "horizon_town_constructions.mat",
            "030cccd52ecf3f59c54a696c6c0aa84aaf374ef48589656e40af7bc733b9d8fb",
            8,
            8,
            0,
            0,
            8),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            4_095,
            "slums_noise_barrier_destro_b",
            "slums_noise_barrier_a.mat",
            "bc728cfecfea850aa630fe14b5c8ad4e06cef0810f0d6b269ce17ffd8bd3ee55",
            6,
            7,
            0,
            6,
            0),
    ];

    private static readonly SiblingControl[] SiblingControls =
    [
        new(
            @"DW_DLC49\Data\hellraid_PC.rpack",
            220,
            "furniture_weapon_rack_c"),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            4_094,
            "slums_noise_barrier_destro_a"),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            4_165,
            "slums_terrain_horizon_a"),
    ];

    private readonly ITestOutputHelper _output;

    public InstalledDl1OrdinaryNonFiniteUvEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 300_000)]
    public async Task InstalledOrdinaryMaterialNonFiniteUvEvidenceIsLocked()
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
            _output.WriteLine(
                $"Installed ordinary-UV controls skipped for build {build.BuildFingerprint}.");
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 64L * 1024 * 1024,
                    MaximumMemoryEntryBytes = 32 * 1024 * 1024,
                    MaximumDiskBytes = 1024L * 1024 * 1024,
                });
            await using Dl1RetailProviderSet providers =
                Dl1RetailProviderSet.Create(
                    install.InstallPath,
                    cache);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    providers.Providers);
            Dictionary<uint, string[]> texturesByHash =
                catalog.Assets
                    .Where(static asset =>
                        asset.Id.ResourceType ==
                        Rp6lResourceTypes.Texture)
                    .GroupBy(static asset =>
                        Dl1ResourceNameHash
                            .ComputeTextureResource(
                                asset.DisplayName))
                    .ToDictionary(
                        static group => group.Key,
                        static group => group
                            .Select(static asset =>
                                asset.DisplayName)
                            .OrderBy(
                                static name => name,
                                StringComparer.Ordinal)
                            .ToArray());

            foreach (Control control in Controls)
            {
                string packPath = Path.Combine(
                    install.InstallPath,
                    control.RelativePackPath);
                Rp6lArchive archive =
                    await Rp6lArchive.OpenAsync(packPath);
                Rp6lResourceDescriptor resource =
                    archive.Resources[control.ResourceIndex];
                Assert.Equal(control.ResourceName, resource.Name);
                Dl1MeshData mesh =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        cache);
                byte[] metadataBytes =
                    await archive.ReadItemBytesAsync(
                        resource.Items[0],
                        cache,
                        maximumBytes: 64 * 1024 * 1024);
                byte[] variantBytes =
                    await archive.ReadItemBytesAsync(
                        resource.Items[1],
                        cache,
                        maximumBytes: 16 * 1024 * 1024);
                byte[] vertexBytes =
                    await archive.ReadItemBytesAsync(
                        resource.Items[3],
                        cache,
                        maximumBytes: 512 * 1024 * 1024);
                byte[] indexBytes =
                    await archive.ReadItemBytesAsync(
                        resource.Items[4],
                        cache,
                        maximumBytes: 512 * 1024 * 1024);
                string geometryFingerprint =
                    ComputeLengthDelimitedSha256(
                        metadataBytes,
                        variantBytes,
                        vertexBytes,
                        indexBytes);
                Dl1MeshGeometryProvenance provenance =
                    Assert.IsType<Dl1MeshGeometryProvenance>(
                        mesh.GeometryProvenance);

                Evidence evidence = Analyze(
                    mesh,
                    vertexBytes,
                    control.MaterialName);
                _output.WriteLine(
                    $"{control.ResourceName}: bad={evidence.BadVertexCount}, " +
                    $"triangles={evidence.AffectedTriangleCount}, " +
                    $"nondegenerate={evidence.NonDegenerateTriangleCount}, " +
                    $"allBad={evidence.AllBadTriangleCount}, " +
                    $"materials=[{string.Join(", ", evidence.Materials)}], " +
                    $"geometrySha256={geometryFingerprint}, " +
                    $"badHalfPatterns=[{string.Join(", ", evidence.NonFiniteHalfPatternCounts.Select(static pair => $"0x{pair.Key:X4}:{pair.Value}"))}]");
                foreach (Dl1MeshSurface surface in mesh.Surfaces)
                {
                    _output.WriteLine(
                        $"  {surface.Name}/lod{surface.LodIndex}: " +
                        DescribeLayout(surface.VertexLayout));
                }

                foreach (string line in evidence.SampleLines)
                {
                    _output.WriteLine($"  {line}");
                }

                Assert.Equal(
                    control.GeometryFingerprint,
                    geometryFingerprint);
                Assert.Equal(
                    control.GeometryFingerprint,
                    provenance.LengthDelimitedSha256);
                Assert.Equal(
                    metadataBytes.LongLength,
                    provenance.MetadataLength);
                Assert.Equal(
                    variantBytes.LongLength,
                    provenance.VariantLength);
                Assert.Equal(
                    vertexBytes.LongLength,
                    provenance.VertexLength);
                Assert.Equal(
                    indexBytes.LongLength,
                    provenance.IndexLength);
                Assert.Equal(
                    control.ExpectedBadVertexCount,
                    evidence.BadVertexCount);
                Assert.Equal(
                    control.ExpectedAffectedTriangleCount,
                    evidence.AffectedTriangleCount);
                Assert.Equal(
                    evidence.AffectedTriangleCount,
                    evidence.NonDegenerateTriangleCount);
                Assert.Equal(
                    control.ExpectedAllBadTriangleCount,
                    evidence.AllBadTriangleCount);
                Assert.Equal(
                    control.ExpectedPositiveInfinityComponentCount,
                    evidence.NonFiniteHalfPatternCounts
                        .GetValueOrDefault((ushort)0x7C00));
                Assert.Equal(
                    control.ExpectedNegativeInfinityComponentCount,
                    evidence.NonFiniteHalfPatternCounts
                        .GetValueOrDefault((ushort)0xFC00));
                Assert.Equal(
                    control.ExpectedPositiveInfinityComponentCount +
                    control.ExpectedNegativeInfinityComponentCount,
                    evidence.NonFiniteHalfPatternCounts.Values.Sum());
                Assert.DoesNotContain(
                    evidence.NonFiniteHalfPatternCounts.Keys,
                    static pattern =>
                        pattern is not 0x7C00 and not 0xFC00);
                Assert.Contains(
                    control.MaterialName,
                    evidence.Materials,
                    StringComparer.OrdinalIgnoreCase);

                var validator =
                    new Dl1MeshCorpusValidator(cache);
                Dl1MeshCorpusResourceResult result =
                    validator.ValidateDecodedMesh(
                        resource,
                        mesh);
                Assert.True(
                    result.Passed,
                    string.Join(
                        Environment.NewLine,
                        result.Issues.Select(static issue =>
                            $"{issue.Code}: {issue.Message}")));
                Assert.Contains(
                    result.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS035" &&
                        issue.Severity ==
                            Dl1MeshCorpusIssueSeverity.Warning &&
                        issue.Message.Contains(
                            "published unchanged",
                            StringComparison.Ordinal) &&
                        issue.Message.Contains(
                            "fidelity-limited",
                            StringComparison.Ordinal));
                Assert.DoesNotContain(
                    result.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS028");

                Assert.Contains(
                    mesh.Diagnostics,
                    static diagnostic =>
                        diagnostic.Code == "DL1MESH017" &&
                        diagnostic.Severity ==
                            Dl1MeshDiagnosticSeverity.Warning &&
                        diagnostic.Message.Contains(
                            "fidelity-limited",
                            StringComparison.Ordinal));
                Dl1MeshPreviewPayload preview =
                    Dl1MeshPreviewAdapter.Convert(mesh);
                Vector2[] publishedUvs = preview.Meshes
                    .SelectMany(static previewMesh =>
                        previewMesh.Vertices.ToArray())
                    .Select(static vertex =>
                        vertex.TextureCoordinate)
                    .ToArray();
                Assert.NotEmpty(publishedUvs);
                Assert.Equal(
                    control.ExpectedPositiveInfinityComponentCount > 0,
                    publishedUvs.Any(static uv =>
                        float.IsPositiveInfinity(uv.X) ||
                        float.IsPositiveInfinity(uv.Y)));
                Assert.Equal(
                    control.ExpectedNegativeInfinityComponentCount > 0,
                    publishedUvs.Any(static uv =>
                        float.IsNegativeInfinity(uv.X) ||
                        float.IsNegativeInfinity(uv.Y)));
                Assert.Contains(
                    preview.Diagnostics,
                    static diagnostic =>
                        diagnostic.Contains(
                            "DL1MESH017",
                            StringComparison.Ordinal) &&
                        diagnostic.Contains(
                            "fidelity-limited",
                            StringComparison.Ordinal));

                Dl1MeshData fingerprintMismatch = mesh with
                {
                    GeometryProvenance = provenance with
                    {
                        LengthDelimitedSha256 =
                            (provenance.LengthDelimitedSha256[0] ==
                                '0'
                                ? "1"
                                : "0") +
                            provenance.LengthDelimitedSha256[1..],
                    },
                };
                Dl1MeshCorpusResourceResult mismatchResult =
                    validator.ValidateDecodedMesh(
                        resource,
                        fingerprintMismatch);
                Assert.False(mismatchResult.Passed);
                Assert.Contains(
                    mismatchResult.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS028" &&
                        issue.Severity ==
                            Dl1MeshCorpusIssueSeverity.Error);
                Assert.DoesNotContain(
                    mismatchResult.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS035");

                Dl1MeshData nanMesh =
                    ReplaceFirstNonFiniteUvWithNaN(mesh);
                Dl1MeshCorpusResourceResult nanResult =
                    validator.ValidateDecodedMesh(
                        resource,
                        nanMesh);
                Assert.False(nanResult.Passed);
                Assert.Contains(
                    nanResult.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS028" &&
                        issue.Severity ==
                            Dl1MeshCorpusIssueSeverity.Error);
                Assert.DoesNotContain(
                    nanResult.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS035");
            }

            foreach (SiblingControl control in SiblingControls)
            {
                string packPath = Path.Combine(
                    install.InstallPath,
                    control.RelativePackPath);
                Rp6lArchive archive =
                    await Rp6lArchive.OpenAsync(packPath);
                Rp6lResourceDescriptor resource =
                    archive.Resources[control.ResourceIndex];
                Assert.Equal(control.ResourceName, resource.Name);
                Dl1MeshData mesh =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        cache);
                int referencedNonFinite = mesh.Surfaces.Sum(surface =>
                {
                    HashSet<ushort> referenced =
                        surface.Indices.ToHashSet();
                    return surface.Vertices
                        .Select((vertex, index) =>
                            (vertex, index))
                        .Count(row =>
                            row.index <= ushort.MaxValue &&
                            referenced.Contains(
                                checked((ushort)row.index)) &&
                            !IsFinite(
                                row.vertex.TextureCoordinate0));
                });
                _output.WriteLine(
                    $"sibling {control.ResourceName}: " +
                    $"surfaces={mesh.Surfaces.Count}, " +
                    $"referencedBadUv0={referencedNonFinite}, " +
                    $"materials=[{string.Join(", ", mesh.MaterialSlots.Select(static slot => slot.DatabaseName))}]");
                foreach (Dl1MeshSurface surface in mesh.Surfaces)
                {
                    _output.WriteLine(
                        $"  {surface.Name}/lod{surface.LodIndex}: " +
                        DescribeLayout(surface.VertexLayout));
                }

                Assert.Equal(0, referencedNonFinite);
            }

            foreach (string materialPack in EnumerateMaterialPacks(install))
            {
                await using Dl1MaterialPackReader reader =
                    await Dl1MaterialPackReader.OpenAsync(materialPack);
                foreach (Control control in Controls)
                {
                    Dl1MaterialPackMaterialRecord? material =
                        await reader.ReadMaterialAsync(
                            control.MaterialName);
                    if (material is null)
                    {
                        continue;
                    }

                    byte[] raw = await ReadMaterialPayloadAsync(
                        materialPack,
                        control.MaterialName);
                    _output.WriteLine(
                        $"{Path.GetFileName(materialPack)}:{control.MaterialName}: " +
                        $"techniques={material.TechniqueCount}, " +
                        $"textures={material.Textures.Count}, " +
                        $"raw={Convert.ToHexString(raw.AsSpan(0, Math.Min(raw.Length, 160)))}");
                    for (int index = 0;
                         index < material.Textures.Count;
                         index++)
                    {
                        Dl1MaterialPackTextureRecord texture =
                            material.Textures[index];
                        _output.WriteLine(
                            $"  texture[{index}] sampler=0x{texture.SamplerState:X8}, " +
                            $"hash=0x{texture.TextureNameHash:X8}, " +
                            $"load=0x{texture.LoadFlags:X8}, " +
                            $"names=[{string.Join(", ", texturesByHash.GetValueOrDefault(texture.TextureNameHash) ?? [])}]");
                    }
                }
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Evidence Analyze(
        Dl1MeshData mesh,
        byte[] vertexBytes,
        string expectedMaterial)
    {
        HashSet<int> badVertices = [];
        HashSet<string> materials =
            new(StringComparer.OrdinalIgnoreCase);
        Dictionary<ushort, int> nonFiniteHalfPatterns = [];
        List<string> samples = [];
        int affectedTriangles = 0;
        int nonDegenerateTriangles = 0;
        int allBadTriangles = 0;

        foreach (Dl1MeshSurface surface in mesh.Surfaces)
        {
            HashSet<int> referenced =
                surface.Indices.Select(static index => (int)index)
                    .ToHashSet();
            int[] surfaceBad = Enumerable.Range(
                    0,
                    surface.Vertices.Count)
                .Where(index =>
                    referenced.Contains(index) &&
                    !IsFinite(
                        surface.Vertices[index]
                            .TextureCoordinate0))
                .ToArray();
            foreach (int vertexIndex in surfaceBad)
            {
                badVertices.Add(vertexIndex);
            }

            Dl1VertexElement uvElement =
                Assert.Single(
                    surface.VertexLayout.Elements,
                    static element =>
                        element.Semantic ==
                            Dl1VertexSemantic.TextureCoordinate &&
                        element.SemanticIndex == 0);
            foreach (int vertexIndex in surfaceBad)
            {
                int offset = checked(
                    surface.VertexBuffer.ByteOffset +
                    vertexIndex * surface.VertexLayout.Stride +
                    uvElement.ByteOffset);
                ReadOnlySpan<byte> raw =
                    vertexBytes.AsSpan(offset, 4);
                for (int component = 0;
                     component < 2;
                     component++)
                {
                    ushort bits =
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            raw[(component * sizeof(ushort))..]);
                    if ((bits & 0x7C00) == 0x7C00)
                    {
                        nonFiniteHalfPatterns[bits] =
                            nonFiniteHalfPatterns.GetValueOrDefault(bits) +
                            1;
                    }
                }
            }

            foreach (int vertexIndex in surfaceBad.Take(12))
            {
                int offset = checked(
                    surface.VertexBuffer.ByteOffset +
                    vertexIndex * surface.VertexLayout.Stride +
                    uvElement.ByteOffset);
                ReadOnlySpan<byte> raw =
                    vertexBytes.AsSpan(offset, 4);
                Dl1MeshVertex vertex = surface.Vertices[vertexIndex];
                samples.Add(
                    $"v{vertexIndex}: uv0=({vertex.TextureCoordinate0.X:R}," +
                    $"{vertex.TextureCoordinate0.Y:R}), " +
                    $"uv1=({vertex.TextureCoordinate1.X:R}," +
                    $"{vertex.TextureCoordinate1.Y:R}), " +
                    $"raw={Convert.ToHexString(raw)}, " +
                    $"half=0x{BinaryPrimitives.ReadUInt16LittleEndian(raw):X4}/" +
                    $"0x{BinaryPrimitives.ReadUInt16LittleEndian(raw[2..]):X4}, " +
                    $"pos=({vertex.Position.X:R},{vertex.Position.Y:R},{vertex.Position.Z:R})");
            }

            HashSet<int> badSet = surfaceBad.ToHashSet();
            foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
            {
                string material = mesh.MaterialSlots
                    .FirstOrDefault(slot =>
                        slot.Index == submesh.MaterialSlotIndex)
                    ?.DatabaseName ?? $"slot-{submesh.MaterialSlotIndex}";
                for (int index = submesh.FirstIndex;
                     index < submesh.FirstIndex + submesh.IndexCount;
                     index += 3)
                {
                    int a = surface.Indices[index];
                    int b = surface.Indices[index + 1];
                    int c = surface.Indices[index + 2];
                    int badCount =
                        (badSet.Contains(a) ? 1 : 0) +
                        (badSet.Contains(b) ? 1 : 0) +
                        (badSet.Contains(c) ? 1 : 0);
                    if (badCount == 0)
                    {
                        continue;
                    }

                    affectedTriangles++;
                    materials.Add(material);
                    if (badCount == 3)
                    {
                        allBadTriangles++;
                    }

                    Vector3 p0 = surface.Vertices[a].Position;
                    Vector3 p1 = surface.Vertices[b].Position;
                    Vector3 p2 = surface.Vertices[c].Position;
                    float crossLength =
                        Vector3.Cross(p1 - p0, p2 - p0).Length();
                    if (float.IsFinite(crossLength) &&
                        crossLength > 1e-8f)
                    {
                        nonDegenerateTriangles++;
                    }

                    if (samples.Count < 32)
                    {
                        samples.Add(
                            $"tri@{index / 3} part{submesh.Index} {material}: " +
                            $"[{a},{b},{c}] bad={badCount}, cross={crossLength:R}");
                    }
                }
            }
        }

        Assert.Contains(
            expectedMaterial,
            materials,
            StringComparer.OrdinalIgnoreCase);
        return new Evidence(
            badVertices.Count,
            affectedTriangles,
            nonDegenerateTriangles,
            allBadTriangles,
            materials.OrderBy(
                    static name => name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            nonFiniteHalfPatterns
                .OrderBy(static pair => pair.Key)
                .ToDictionary(),
            samples);
    }

    private static IEnumerable<string> EnumerateMaterialPacks(
        Dl1InstallLocation install)
    {
        string optimized = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        if (File.Exists(optimized))
        {
            yield return optimized;
        }

        string global = Path.Combine(
            install.InstallPath,
            "DevTools",
            "DW",
            "Data",
            "global_dx11.mp");
        if (File.Exists(global))
        {
            yield return global;
        }
    }

    private static async Task<byte[]> ReadMaterialPayloadAsync(
        string path,
        string resourceName)
    {
        const int containerRowSize = 48;
        const int materialEntryRowSize = 16;
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        byte[] header = new byte[16];
        await stream.ReadExactlyAsync(header);
        int containerCount = checked(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        long containerOffset =
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        byte[] containers = new byte[
            checked(containerCount * containerRowSize)];
        stream.Position = containerOffset;
        await stream.ReadExactlyAsync(containers);
        int materialCount = 0;
        long materialTableOffset = 0;
        for (int index = 0; index < containerCount; index++)
        {
            ReadOnlySpan<byte> row = containers.AsSpan(
                index * containerRowSize,
                containerRowSize);
            int terminator = row[..32].IndexOf((byte)0);
            string name = System.Text.Encoding.ASCII.GetString(
                row[..terminator]);
            if (!name.Equals(
                    "materials",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            materialCount = checked(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(row[32..]));
            materialTableOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(row[40..]);
            break;
        }

        byte[] table = new byte[
            checked(materialCount * materialEntryRowSize)];
        stream.Position = materialTableOffset;
        await stream.ReadExactlyAsync(table);
        uint wantedHash = Dl1ResourceNameHash.Compute(resourceName);
        for (int index = 0; index < materialCount; index++)
        {
            ReadOnlySpan<byte> row = table.AsSpan(
                index * materialEntryRowSize,
                materialEntryRowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row) !=
                wantedHash)
            {
                continue;
            }

            long offset =
                BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            int size = checked(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            byte[] payload = new byte[size];
            stream.Position = offset;
            await stream.ReadExactlyAsync(payload);
            return payload;
        }

        throw new InvalidDataException(
            $"Material '{resourceName}' was not found in '{path}'.");
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y);

    private static string DescribeLayout(
        Dl1VertexLayout layout) =>
        $"stride={layout.Stride}, elements=[" +
        string.Join(
            ", ",
            layout.Elements.Select(static element =>
                $"{element.Semantic}{element.SemanticIndex}/" +
                $"{element.Format}@{element.ByteOffset}")) +
        "]";

    private static string ComputeLengthDelimitedSha256(
        params byte[][] payloads)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(long)];
        foreach (byte[] payload in payloads)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                length,
                payload.LongLength);
            hash.AppendData(length);
            hash.AppendData(payload);
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static Dl1MeshData ReplaceFirstNonFiniteUvWithNaN(
        Dl1MeshData mesh)
    {
        Dl1MeshSurface[] surfaces = mesh.Surfaces.ToArray();
        for (int surfaceIndex = 0;
             surfaceIndex < surfaces.Length;
             surfaceIndex++)
        {
            Dl1MeshSurface surface = surfaces[surfaceIndex];
            Dl1MeshVertex[] vertices =
                surface.Vertices.ToArray();
            HashSet<ushort> referenced =
                surface.Indices.ToHashSet();
            int vertexIndex = -1;
            for (int index = 0;
                 index < vertices.Length &&
                 index <= ushort.MaxValue;
                 index++)
            {
                if (referenced.Contains(
                        checked((ushort)index)) &&
                    !IsFinite(
                        vertices[index].TextureCoordinate0))
                {
                    vertexIndex = index;
                    break;
                }
            }
            if (vertexIndex < 0)
            {
                continue;
            }

            Dl1MeshVertex vertex = vertices[vertexIndex];
            vertices[vertexIndex] = vertex with
            {
                TextureCoordinate0 = new Vector2(
                    float.NaN,
                    vertex.TextureCoordinate0.Y),
            };
            surfaces[surfaceIndex] = surface with
            {
                Vertices = vertices,
            };
            return mesh with
            {
                Surfaces = surfaces,
            };
        }

        throw new InvalidOperationException(
            "The installed control has no referenced non-finite UV0 vertex.");
    }

    private sealed record Control(
        string RelativePackPath,
        int ResourceIndex,
        string ResourceName,
        string MaterialName,
        string GeometryFingerprint,
        int ExpectedBadVertexCount,
        int ExpectedAffectedTriangleCount,
        int ExpectedAllBadTriangleCount,
        int ExpectedPositiveInfinityComponentCount,
        int ExpectedNegativeInfinityComponentCount);

    private sealed record SiblingControl(
        string RelativePackPath,
        int ResourceIndex,
        string ResourceName);

    private sealed record Evidence(
        int BadVertexCount,
        int AffectedTriangleCount,
        int NonDegenerateTriangleCount,
        int AllBadTriangleCount,
        IReadOnlyList<string> Materials,
        IReadOnlyDictionary<ushort, int> NonFiniteHalfPatternCounts,
        IReadOnlyList<string> SampleLines);
}
