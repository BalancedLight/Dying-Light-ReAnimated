using System.Buffers.Binary;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1SkinningLayoutEvidenceTests
{
    private const string RunCorpusEvidenceEnvironmentVariable =
        "DLR_RUN_ZERO_WEIGHT_SKINNING_EVIDENCE";

    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public InstalledDl1SkinningLayoutEvidenceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 600_000)]
    public async Task InstalledZeroWeightAndHeadLodLayoutsRemainAuditable()
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
            InstalledMeshControl[] controls =
            [
                new(
                    Path.Combine(
                        install.DataPath,
                        "common_cod_1_PC.rpack"),
                    6,
                    "player_1_tpp"),
                new(
                    Path.Combine(
                        install.DataPath,
                        "common_meshes_PC.rpack"),
                    428,
                    "brecken_cin"),
                new(
                    Path.Combine(
                        install.DataPath,
                        "common_meshes_PC.rpack"),
                    1_331,
                    "furniture_int_a_anm"),
                new(
                    Path.Combine(
                        install.DataPath,
                        "common_meshes_PC.rpack"),
                    4_357,
                    "survivor_woman_a"),
                new(
                    Path.Combine(
                        install.InstallPath,
                        "DW_DLC17",
                        "Data",
                        "wasteland_PC.rpack"),
                    867,
                    "survivor_woman_b"),
                new(
                    Path.Combine(
                        install.DataPath,
                        "weapons_PC.rpack"),
                    441,
                    "wn_shotgun_b_hq"),
            ];

            foreach (InstalledMeshControl control in controls)
            {
                Rp6lArchive archive =
                    await Rp6lArchive.OpenAsync(control.PackPath);
                Rp6lResourceDescriptor resource =
                    archive.Resources[control.ResourceIndex];
                Assert.Equal(control.ResourceName, resource.Name);
                byte[] metadata = await archive.ReadItemBytesAsync(
                    resource.Items[0],
                    cache,
                    maximumBytes: 64 * 1024 * 1024);
                byte[] variants = await archive.ReadItemBytesAsync(
                    resource.Items[1],
                    cache,
                    maximumBytes: 16 * 1024 * 1024);
                byte[] vertices = await archive.ReadItemBytesAsync(
                    resource.Items[3],
                    cache,
                    maximumBytes: 512 * 1024 * 1024);
                byte[] indices = await archive.ReadItemBytesAsync(
                    resource.Items[4],
                    cache,
                    maximumBytes: 512 * 1024 * 1024);
                CompiledMeshGeometryDocument geometry =
                    CompiledMeshGeometryDecoder.Decode(
                        metadata,
                        variants,
                        vertices,
                        indices);

                _output.WriteLine(
                    $"{control.ResourceName}: layouts={geometry.VertexLayouts.Count}, surfaces={geometry.Surfaces.Count}, diagnostics={string.Join(" | ", geometry.Diagnostics.Select(static item => $"{item.Code}:{item.Message}"))}");
                foreach (RawDeclaration declaration in
                         ReadDeclarations(metadata))
                {
                    _output.WriteLine(
                        $"  decl {declaration.Index}: tail={declaration.Tail}, raw=[{string.Join(", ", declaration.Elements.Select(static element => $"{element.Format}/{element.Semantic}/{element.Channel}/{element.SerializedOffset}"))}], sequentialStride={declaration.SequentialStride}, explicitStride={declaration.ExplicitStride}");
                }

                foreach (CompiledMeshSurface surface in
                         geometry.Surfaces)
                {
                    bool hasZeroReferencedWeight =
                        surface.Submeshes.Any(submesh =>
                            EnumerateReferencedVertices(
                                    surface,
                                    submesh)
                                .Any(index =>
                                    surface.Vertices[index]
                                        .BlendWeights ==
                                    System.Numerics.Vector4.Zero));
                    if (!hasZeroReferencedWeight)
                    {
                        continue;
                    }

                    RawDeclaration declaration =
                        ReadDeclarations(metadata)[
                            surface.DeclarationGroupIndex];
                    RawElement? rawWeights =
                        declaration.Elements.SingleOrDefault(
                            static element =>
                                element.Semantic ==
                                (byte)CompiledVertexSemantic
                                    .BlendWeights);
                    if (rawWeights is null)
                    {
                        continue;
                    }

                    CompiledVertexElement sequentialWeights =
                        Assert.Single(
                            surface.VertexLayout.Elements,
                            static element =>
                                element.RawSemantic ==
                                (byte)CompiledVertexSemantic
                                    .BlendWeights);
                    foreach (CompiledMeshSubmesh submesh in
                             surface.Submeshes)
                    {
                        int[] referenced =
                            EnumerateReferencedVertices(
                                    surface,
                                    submesh)
                                .Distinct()
                                .ToArray();
                        int zeroCount = referenced.Count(index =>
                            surface.Vertices[index].BlendWeights ==
                            System.Numerics.Vector4.Zero);
                        if (zeroCount == 0)
                        {
                            continue;
                        }

                        int first = referenced.First(index =>
                            surface.Vertices[index].BlendWeights ==
                            System.Numerics.Vector4.Zero);
                        string localIndexCounts = string.Join(
                            ",",
                            referenced
                                .GroupBy(index =>
                                    surface.Vertices[index]
                                        .LocalBlendIndices.X)
                                .OrderBy(static group => group.Key)
                                .Select(static group =>
                                    $"{group.Key}:{group.Count()}"));
                        _output.WriteLine(
                            $"  zero {surface.Name}/lod{surface.LodIndex}/part{submesh.Index}: referenced={referenced.Length}, zero={zeroCount}, palette=[{string.Join(",", submesh.BonePaletteEntityIndexes)}], localX=[{localIndexCounts}], first={first}, localIndexes={surface.Vertices[first].LocalBlendIndices}, sequentialWeightBytes={ReadBytes(vertices, surface.VertexByteOffset, first, declaration.SequentialStride, sequentialWeights.ByteOffset)}");
                    }
                }

                foreach (CompactMeshDiagnostic diagnostic in
                         geometry.Diagnostics.Where(static item =>
                             item.Code == "CMESHG004"))
                {
                    Assert.NotNull(diagnostic.EntityIndex);
                    RawLodRecord record = ReadLodRecord(
                        metadata,
                        geometry.VertexLayouts,
                        diagnostic.EntityIndex.Value,
                        lodIndex: 1);
                    ushort[] rawIndices = ReadIndices(
                        indices,
                        record.IndexByteOffset,
                        record.IndexCount);
                    string invalidIndexes = string.Join(
                        ",",
                        rawIndices
                            .Select(static (value, index) =>
                                (Value: value, Index: index))
                            .Where(row =>
                                row.Value >= record.VertexCount)
                            .Select(static row =>
                                $"{row.Index}:{row.Value}"));
                    _output.WriteLine(
                        $"  failed entity={diagnostic.EntityIndex} lod1 vertexOffset={record.VertexByteOffset}, stride={record.Stride}, baseVertex={record.VertexByteOffset / record.Stride}, vertexCount={record.VertexCount}, indexOffset={record.IndexByteOffset}, indexCount={record.IndexCount}, indexRange={rawIndices.Min()}..{rawIndices.Max()}, invalid=[{invalidIndexes}], tail=[{string.Join(",", rawIndices.TakeLast(12))}]");
                }
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    [Fact(Timeout = 7_200_000)]
    public async Task ConfiguredCorpusProvesZeroWeightRigidIndexEncoding()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    RunCorpusEvidenceEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .First(static location => location.IsValid);
        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        Assert.Equal(
            ValidatedBuildFingerprint,
            build.BuildFingerprint,
            ignoreCase: true);

        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData),
            "DLReAnimated",
            "Cache",
            "Rp6lCorpus");
        Directory.CreateDirectory(cacheDirectory);
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = cacheDirectory,
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 16L * 1024 * 1024 * 1024,
            });
        await using Dl1RetailProviderSet providers =
            Dl1RetailProviderSet.Create(
                install.InstallPath,
                cache);

        int resourceCount = 0;
        int zeroWeightResourceCount = 0;
        int zeroWeightSubmeshCount = 0;
        Dictionary<int, int> paletteSizeCounts = [];
        List<string> mixedSubmeshes = [];
        List<string> invalidPrimaryIndexes = [];
        List<string> nonZeroSecondaryIndexes = [];
        List<string> otherInvalidSums = [];
        foreach (RpackSource source in
                 providers.RpackProvider.Sources)
        {
            Rp6lArchive archive =
                await Rp6lArchive.OpenAsync(source.Path);
            foreach (Rp6lResourceDescriptor resource in
                     archive.Resources.Where(static resource =>
                         resource.ResourceType ==
                         Rp6lResourceTypes.Mesh))
            {
                resourceCount++;
                Dl1MeshData mesh =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        cache);
                bool resourceHasZeroWeights = false;
                foreach (Dl1MeshSurface surface in
                         mesh.Surfaces)
                {
                    if (surface.EntityIndex < 0 ||
                        surface.EntityIndex >=
                            mesh.Hierarchy.Entities.Count ||
                        !mesh.Hierarchy.Entities[
                                surface.EntityIndex]
                            .EntityType.HasFlag(
                                CompactMeshEntityType
                                    .SkinnedMesh) ||
                        !HasBlendStreams(surface))
                    {
                        continue;
                    }

                    foreach (Dl1MeshSubmesh submesh in
                             surface.Submeshes)
                    {
                        int[] referenced = surface.Indices
                            .Skip(submesh.FirstIndex)
                            .Take(submesh.IndexCount)
                            .Select(static index => (int)index)
                            .Distinct()
                            .ToArray();
                        if (referenced.Length == 0)
                        {
                            continue;
                        }

                        int[] zero = referenced
                            .Where(index =>
                                surface.Vertices[index]
                                    .BlendWeights ==
                                System.Numerics.Vector4.Zero)
                            .ToArray();
                        foreach (int vertexIndex in
                                 referenced.Except(zero))
                        {
                            float sum =
                                SumWeights(
                                    surface.Vertices[vertexIndex]
                                        .BlendWeights);
                            if (!float.IsFinite(sum) ||
                                MathF.Abs(sum - 1.0f) >
                                0.02f)
                            {
                                otherInvalidSums.Add(
                                    Describe(
                                        source,
                                        resource,
                                        surface,
                                        submesh,
                                        $"vertex {vertexIndex} sum {sum}"));
                            }
                        }

                        if (zero.Length == 0)
                        {
                            continue;
                        }

                        resourceHasZeroWeights = true;
                        zeroWeightSubmeshCount++;
                        paletteSizeCounts[
                            submesh
                                .BonePaletteEntityIndexes
                                .Count] =
                            paletteSizeCounts.GetValueOrDefault(
                                submesh
                                    .BonePaletteEntityIndexes
                                    .Count) + 1;
                        if (zero.Length != referenced.Length)
                        {
                            mixedSubmeshes.Add(
                                Describe(
                                    source,
                                    resource,
                                    surface,
                                    submesh,
                                    $"{zero.Length}/{referenced.Length} zero"));
                        }

                        foreach (int vertexIndex in zero)
                        {
                            Dl1BoneIndex4 local =
                                surface.Vertices[vertexIndex]
                                    .LocalBlendIndices;
                            if (local.X >= submesh
                                    .BonePaletteEntityIndexes
                                    .Count)
                            {
                                invalidPrimaryIndexes.Add(
                                    Describe(
                                        source,
                                        resource,
                                        surface,
                                        submesh,
                                        $"vertex {vertexIndex} local X {local.X}, palette {submesh.BonePaletteEntityIndexes.Count}"));
                            }

                            if (local.Y != 0 ||
                                local.Z != 0 ||
                                local.W != 0)
                            {
                                nonZeroSecondaryIndexes.Add(
                                    Describe(
                                        source,
                                        resource,
                                        surface,
                                        submesh,
                                        $"vertex {vertexIndex} local indexes ({local.X},{local.Y},{local.Z},{local.W})"));
                            }
                        }
                    }
                }

                if (resourceHasZeroWeights)
                {
                    zeroWeightResourceCount++;
                }
            }
        }

        _output.WriteLine(
            $"resources={resourceCount:N0}, zeroResources={zeroWeightResourceCount:N0}, zeroSubmeshes={zeroWeightSubmeshCount:N0}, palettes=[{string.Join(",", paletteSizeCounts.OrderBy(static row => row.Key).Select(static row => $"{row.Key}:{row.Value}"))}], mixed={mixedSubmeshes.Count}, invalidX={invalidPrimaryIndexes.Count}, secondary={nonZeroSecondaryIndexes.Count}, otherInvalid={otherInvalidSums.Count}");
        Assert.Equal(8_738, resourceCount);
        Assert.Equal(106, zeroWeightResourceCount);
        Assert.Equal(583, zeroWeightSubmeshCount);
        Assert.Empty(mixedSubmeshes);
        Assert.Empty(invalidPrimaryIndexes);
        Assert.Empty(nonZeroSecondaryIndexes);
        Assert.Empty(otherInvalidSums);
        Assert.Contains(
            paletteSizeCounts,
            static row => row.Key > 1);
    }

    private static bool HasBlendStreams(
        Dl1MeshSurface surface) =>
        surface.VertexLayout.Elements.Any(static element =>
            element.Semantic ==
                Dl1VertexSemantic.BlendWeights &&
            element.Format ==
                Dl1VertexElementFormat.Byte4Normalized) &&
        surface.VertexLayout.Elements.Any(static element =>
            element.Semantic ==
                Dl1VertexSemantic.BlendIndices &&
            element.Format == Dl1VertexElementFormat.Byte4);

    private static float SumWeights(
        System.Numerics.Vector4 weights) =>
        weights.X + weights.Y + weights.Z + weights.W;

    private static string Describe(
        RpackSource source,
        Rp6lResourceDescriptor resource,
        Dl1MeshSurface surface,
        Dl1MeshSubmesh submesh,
        string detail) =>
        $"{Path.GetFileName(source.Path)}#{resource.Index} '{resource.Name}'/{surface.Name}/lod{surface.LodIndex}/part{submesh.Index}: {detail}";

    private static IEnumerable<int> EnumerateReferencedVertices(
        CompiledMeshSurface surface,
        CompiledMeshSubmesh submesh)
    {
        int end = checked(
            submesh.FirstIndex + submesh.IndexCount);
        for (int offset = submesh.FirstIndex;
             offset < end;
             offset++)
        {
            yield return surface.Indices[offset];
        }
    }

    private static string ReadBytes(
        byte[] vertices,
        int streamOffset,
        int vertexIndex,
        int stride,
        int elementOffset)
    {
        int offset = checked(
            streamOffset + vertexIndex * stride + elementOffset);
        return Convert.ToHexString(
            vertices.AsSpan(offset, sizeof(uint)));
    }

    private static List<RawDeclaration> ReadDeclarations(
        ReadOnlySpan<byte> metadata)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(
            metadata[0x7C..]);
        int table = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[0x50..]));
        List<RawDeclaration> result = new(count);
        for (int index = 0; index < count; index++)
        {
            int row = checked(table + index * 16);
            int elementCount =
                BinaryPrimitives.ReadInt32LittleEndian(
                    metadata[(row + 8)..]);
            int elements = DecodePointer(
                BinaryPrimitives.ReadUInt64LittleEndian(
                    metadata[row..]));
            List<RawElement> values = new(elementCount);
            int sequentialStride = 0;
            int explicitStride = 0;
            for (int elementIndex = 0;
                 elementIndex < elementCount;
                 elementIndex++)
            {
                int offset = checked(elements + elementIndex * 4);
                int size = GetFormatSize(metadata[offset]);
                values.Add(new RawElement(
                    metadata[offset],
                    metadata[offset + 1],
                    metadata[offset + 2],
                    metadata[offset + 3],
                    size));
                sequentialStride = checked(
                    sequentialStride + size);
                explicitStride = Math.Max(
                    explicitStride,
                    checked(metadata[offset + 3] + size));
            }

            result.Add(new RawDeclaration(
                index,
                BinaryPrimitives.ReadInt32LittleEndian(
                    metadata[(row + 12)..]),
                sequentialStride,
                explicitStride,
                values));
        }

        return result;
    }

    private static RawLodRecord ReadLodRecord(
        ReadOnlySpan<byte> metadata,
        IReadOnlyList<CompiledVertexLayout> layouts,
        int entityIndex,
        int lodIndex)
    {
        int entityTable = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[0x08..]));
        int entity = checked(entityTable + entityIndex * 0xD0);
        int lodTable = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[(entity + 0x88)..]));
        int lod = checked(lodTable + lodIndex * 0x30);
        int meshInfo = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[(lod + 8)..]));
        int faceCounts = DecodePointer(
            BinaryPrimitives.ReadUInt64LittleEndian(
                metadata[meshInfo..]));
        int declarationGroup =
            BinaryPrimitives.ReadInt16LittleEndian(
                metadata[(meshInfo + 50)..]);
        return new RawLodRecord(
            BinaryPrimitives.ReadInt32LittleEndian(
                metadata[(meshInfo + 24)..]),
            BinaryPrimitives.ReadInt32LittleEndian(
                metadata[(meshInfo + 40)..]),
            BinaryPrimitives.ReadInt32LittleEndian(
                metadata[(meshInfo + 44)..]),
            BinaryPrimitives.ReadInt32LittleEndian(
                metadata[faceCounts..]),
            layouts[declarationGroup].Stride);
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
                    payload[(offset + index * sizeof(ushort))..]);
        }

        return result;
    }

    private static int DecodePointer(ulong value) =>
        checked((int)(value - 1));

    private static int GetFormatSize(byte format) => format switch
    {
        (byte)CompiledVertexFormat.Float3 => 12,
        (byte)CompiledVertexFormat.Byte4 => 4,
        (byte)CompiledVertexFormat.Half2 => 4,
        (byte)CompiledVertexFormat.Half4 => 8,
        (byte)CompiledVertexFormat.SignedNormalizedByte4 => 4,
        _ => throw new InvalidDataException(
            $"Unsupported evidence-test vertex format {format}."),
    };

    private sealed record InstalledMeshControl(
        string PackPath,
        int ResourceIndex,
        string ResourceName);

    private sealed record RawDeclaration(
        int Index,
        int Tail,
        int SequentialStride,
        int ExplicitStride,
        IReadOnlyList<RawElement> Elements);

    private sealed record RawElement(
        byte Format,
        byte Semantic,
        byte Channel,
        byte SerializedOffset,
        int Size);

    private sealed record RawLodRecord(
        int VertexByteOffset,
        int VertexCount,
        int IndexByteOffset,
        int IndexCount,
        int Stride);
}
