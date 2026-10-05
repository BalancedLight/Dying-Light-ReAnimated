using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class Dl1CharacterMaterialDependencyResolverTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MemoryReceiptPreservesProviderAndExactPayloadWithStoredPadding()
    {
        byte[] provider = Pack("generic.mat", "generic_color.dds", storedPadding: 7);
        byte[] original = provider.ToArray();
        await using Dl1MaterialPackReader reader = await Dl1MaterialPackReader.OpenMemoryAsync(
            provider.ToImmutableArray());
        Dl1MaterialPayloadReceipt receipt = Assert.IsType<Dl1MaterialPayloadReceipt>(
            await reader.ReadMaterialReceiptAsync("Materials/GENERIC.MAT"));
        Assert.Equal("generic.mat", receipt.NormalizedName);
        Assert.Equal(Dl1ResourceNameHash.Compute("generic.mat"), receipt.NameHash);
        Assert.Equal(0, receipt.TableIndex);
        Assert.Equal(80, receipt.Offset);
        Assert.Equal(36, receipt.LogicalByteLength);
        Assert.Equal(43, receipt.StoredSize);
        Assert.Equal(provider.AsSpan(80, 36).ToArray(), receipt.PayloadBytes.ToArray());
        Assert.Equal(Sha(provider.AsSpan(80, 36)), receipt.PayloadSha256);
        Assert.Equal(Sha(provider), receipt.ProviderSha256);
        Assert.Equal((ushort)3, receipt.TechniqueCount);
        Dl1MaterialPackTextureRecord texture = Assert.Single(receipt.Textures);
        Assert.Equal(0x12345678U, texture.SamplerState);
        Assert.Equal(Dl1ResourceNameHash.ComputeTextureResource("generic_color.dds"), texture.TextureNameHash);
        Assert.Equal(5U, texture.LoadFlags);
        Assert.Equal(original, provider);
        Assert.Equal(string.Empty, reader.Path);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task FileAndMemoryReadersUseTheSameInventoryAndPayloadParser()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] provider = Pack("generic.mat", "generic_color.dds", storedPadding: 3);
            string path = Path.Combine(directory, "generic.mp");
            await File.WriteAllBytesAsync(path, provider);
            await using Dl1MaterialPackReader file = await Dl1MaterialPackReader.OpenAsync(path);
            await using Dl1MaterialPackReader memory = await Dl1MaterialPackReader.OpenMemoryAsync(provider.ToImmutableArray());
            Dl1MaterialPayloadReceipt disk = Assert.IsType<Dl1MaterialPayloadReceipt>(
                await file.ReadMaterialReceiptAsync("generic.mat"));
            Dl1MaterialPayloadReceipt bytes = Assert.IsType<Dl1MaterialPayloadReceipt>(
                await memory.ReadMaterialReceiptAsync("generic.mat"));
            Assert.Equal(disk.ProviderSha256, bytes.ProviderSha256);
            Assert.Equal(disk.PayloadSha256, bytes.PayloadSha256);
            Assert.Equal(disk.TableIndex, bytes.TableIndex);
            Assert.Equal(disk.Offset, bytes.Offset);
            Assert.Equal(disk.StoredSize, bytes.StoredSize);
            Assert.Equal(disk.PayloadBytes.ToArray(), bytes.PayloadBytes.ToArray());
            Assert.Equal(disk.Textures.ToArray(), bytes.Textures.ToArray());
            Assert.DoesNotContain(path, System.Text.Json.JsonSerializer.Serialize(disk));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ResolvesExactMaterialAndEffectiveTextureIdentityWithoutReadingTextureBytes()
    {
        RetailAssetRecord shadowed = Texture("generic_color", priority: 10, source: 1);
        RetailAssetRecord selected = Texture("generic_color", priority: 20, source: 2);
        var catalog = new FakeCatalog(shadowed, selected) { ExposePhysicalAlternatives = true };
        byte[] provider = Pack("generic.mat", "generic_color.dds");
        Dl1CharacterMaterialDependencyResolution result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            provider.ToImmutableArray(), ["generic.mat"], catalog);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Findings);
        Assert.Equal(provider.Length, result.ProviderByteLength);
        Assert.Equal(Sha(provider), result.ProviderSha256);
        Dl1CharacterMaterialDependency material = Assert.Single(result.Materials);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.Resolved, material.Status);
        Assert.NotNull(material.Material);
        Dl1CharacterMaterialTextureDependency texture = Assert.Single(material.Textures);
        Assert.Equal(selected.Id, texture.SelectedTextureId);
        Assert.Equal(selected.DisplayName, texture.TextureName);
        Assert.Equal(0, texture.TextureIndex);
        Assert.Equal(0x12345678U, texture.SamplerState);
        Assert.Equal(5U, texture.LoadFlags);
        Assert.Equal(selected.Id, Assert.Single(texture.CandidateTextureIds));
        Assert.Equal(2, catalog.Assets.Count);
        Assert.Equal(0, catalog.OpenCalls);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MissingMaterialDoesNotTrySuffixFallback()
    {
        var result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            Pack("generic.mat", "generic_color.dds").ToImmutableArray(), ["generic"], new FakeCatalog());
        Assert.False(result.IsComplete);
        Dl1CharacterMaterialDependency material = Assert.Single(result.Materials);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.MissingMaterial, material.Status);
        Assert.Null(material.Material);
        Assert.Equal("generic", material.NormalizedName);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MissingTextureAndWrongResourceTypeRemainUnresolved()
    {
        RetailAssetRecord texture = Texture("generic_color.dds");
        var wrongType = texture with
        {
            Id = RetailAssetId.Create(RetailAssetLogicalId.Rpack(Rp6lResourceTypes.Mesh, texture.DisplayName),
                "generic-install", "generic-provider", 1, 0, "generic-snapshot"),
        };
        var result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            Pack("generic.mat", "generic_color.dds").ToImmutableArray(), ["generic.mat"], new FakeCatalog(wrongType));
        Assert.False(result.IsComplete);
        Dl1CharacterMaterialDependency material = Assert.Single(result.Materials);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.MissingTexture, material.Status);
        Assert.NotNull(material.Material);
        Assert.Null(Assert.Single(material.Textures).SelectedTextureId);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.MissingTexture, Assert.Single(result.Findings).Status);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task DistinctCatalogNamesSharingTextureHashAreReportedWithoutSelection()
    {
        var catalog = new FakeCatalog(Texture("set-a/shared.dds", source: 1),
            Texture("set-b/shared.dds", source: 2));
        var result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            Pack("generic.mat", "shared.dds").ToImmutableArray(), ["generic.mat"], catalog);
        Assert.False(result.IsComplete);
        Dl1CharacterMaterialDependency material = Assert.Single(result.Materials);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.TextureHashCollision, material.Status);
        Dl1CharacterMaterialTextureDependency texture = Assert.Single(material.Textures);
        Assert.Null(texture.SelectedTextureId);
        Assert.Equal(2, texture.CandidateTextureIds.Length);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.TextureHashCollision, Assert.Single(result.Findings).Status);
        Assert.Equal(0, catalog.OpenCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MalformedMaterialPayloadsReturnExplicitFailure(int corruption)
    {
        byte[] provider = Pack("generic.mat", "generic_color.dds");
        switch (corruption)
        {
            case 0: BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(80), 0); break;
            case 1: BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(80 + 18), 257); break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(80 + 22), ushort.MaxValue); break;
        }
        var result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(provider.ToImmutableArray(),
            ["generic.mat"], new FakeCatalog(Texture("generic_color.dds")));
        Assert.False(result.IsComplete);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.InvalidMaterial, Assert.Single(result.Materials).Status);
        Assert.Null(Assert.Single(result.Materials).Material);
        Assert.Equal(Dl1CharacterMaterialDependencyStatus.InvalidMaterial, Assert.Single(result.Findings).Status);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task DistinctRequestedMaterialNamesWithEqualSeededHashAreNotAssignedOnePayload()
    {
        const string first = "material_zw5ljet8k6a0.mat";
        const string second = "material_015nyjhe54zo.mat";
        Assert.Equal(Dl1ResourceNameHash.Compute(first), Dl1ResourceNameHash.Compute(second));
        var result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            Pack(first, "generic_color.dds").ToImmutableArray(), [first, second],
            new FakeCatalog(Texture("generic_color.dds")));
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.Materials.Length);
        Assert.All(result.Materials, material =>
        {
            Assert.Equal(Dl1CharacterMaterialDependencyStatus.MaterialHashCollision, material.Status);
            Assert.Null(material.Material);
        });
        Assert.Equal(2, result.Findings.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task DuplicateRequestsKeepExactSpellingAndStablePayloadIdentity()
    {
        var result = await Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            Pack("generic.mat", "generic_color.dds").ToImmutableArray(),
            ["generic.mat", "generic.mat", "Materials/GENERIC.MAT"], new FakeCatalog(Texture("generic_color.dds")));
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Materials.Length);
        Assert.Equal("generic.mat", result.Materials[0].RequestedName);
        Assert.Equal("Materials/GENERIC.MAT", result.Materials[1].RequestedName);
        Assert.Equal(result.Materials[0].Material!.PayloadSha256, result.Materials[1].Material!.PayloadSha256);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MemoryReaderRetainsStrictContainerLayoutAndBounds()
    {
        byte[] inconsistent = Pack("generic.mat", "generic_color.dds");
        BinaryPrimitives.WriteUInt32LittleEndian(inconsistent.AsSpan(16 + 36), 2);
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl1MaterialPackReader.OpenMemoryAsync(inconsistent.ToImmutableArray()));
        byte[] escaped = Pack("generic.mat", "generic_color.dds");
        BinaryPrimitives.WriteUInt32LittleEndian(escaped.AsSpan(64 + 4), uint.MaxValue);
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl1MaterialPackReader.OpenMemoryAsync(escaped.ToImmutableArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl1MaterialPackReader.OpenMemoryAsync(default));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RequestCountAndCancellationAreBoundedBeforeCatalogAccess()
    {
        var catalog = new FakeCatalog();
        ImmutableArray<byte> provider = Pack("generic.mat", "generic_color.dds").ToImmutableArray();
        await Assert.ThrowsAsync<ArgumentException>(() => Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            provider, Enumerable.Repeat("generic.mat", 257), catalog));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dl1CharacterMaterialDependencyResolver.ResolveAsync(
            provider, ["generic.mat"], catalog, cancellation.Token));
        Assert.Equal(0, catalog.OpenCalls);
    }

    private static byte[] Pack(string material, string texture, int storedPadding = 0)
    {
        byte[] provider = new byte[80 + 36 + storedPadding + 11];
        "ABDM"u8.CopyTo(provider);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(8), 16);
        "materials"u8.CopyTo(provider.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(16 + 32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(16 + 36), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(16 + 40), 64);
        uint hash = Dl1ResourceNameHash.Compute(material);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(64), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(68), 80);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(72), 36);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(76), checked((uint)(36 + storedPadding)));
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(80), hash);
        BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(96), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(98), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(provider.AsSpan(102), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(104), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(108), Dl1ResourceNameHash.ComputeTextureResource(texture));
        BinaryPrimitives.WriteUInt32LittleEndian(provider.AsSpan(112), 5);
        provider.AsSpan(116).Fill(0xA5);
        return provider;
    }

    private static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static RetailAssetRecord Texture(string name, int priority = 0, int source = 1) => new(
        RetailAssetId.Create(RetailAssetLogicalId.Rpack(Rp6lResourceTypes.Texture, name), "generic-install",
            "generic-provider", source, priority, "generic-snapshot"), name,
        new RetailAssetSource("generic-provider", RetailAssetSourceKind.Rpack, priority,
            "generic.rpack", name, source, 32, 128, DateTime.UnixEpoch));

    private sealed class FakeCatalog : IRetailAssetCatalog
    {
        private readonly Dictionary<RetailAssetLogicalId, RetailAssetRecord[]> _records;
        private readonly RetailAssetRecord[] _allRecords;
        public FakeCatalog(params RetailAssetRecord[] records)
        {
            _records = records.GroupBy(record => record.Id.LogicalId).ToDictionary(group => group.Key,
                group => group.OrderByDescending(record => record.Source.Priority).ToArray());
            _allRecords = records;
        }
        public int OpenCalls { get; private set; }
        public bool ExposePhysicalAlternatives { get; init; }
        public IReadOnlyList<RetailAssetRecord> Assets => ExposePhysicalAlternatives
            ? _allRecords : _records.Values.Select(records => records[0]).ToArray();
        public IReadOnlyList<RetailAssetConflict> Conflicts => [];
        public RetailAssetRecord? Resolve(RetailAssetLogicalId id) => _records.GetValueOrDefault(id)?.FirstOrDefault();
        public IReadOnlyList<RetailAssetRecord> GetCandidates(RetailAssetLogicalId id) => _records.GetValueOrDefault(id) ?? [];
        public IReadOnlyList<RetailAssetRecord> Search(string text, int maximumResults = 500) =>
            Assets.Where(asset => asset.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(maximumResults).ToArray();
        public ValueTask<Stream> OpenReadAsync(RetailAssetLogicalId id, CancellationToken cancellationToken = default) => FailRead();
        public ValueTask<Stream> OpenReadAsync(RetailAssetId id, CancellationToken cancellationToken = default) => FailRead();
        public ValueTask<Stream> OpenReadAsync(RetailAssetRecord asset, CancellationToken cancellationToken = default) => FailRead();
        private ValueTask<Stream> FailRead()
        {
            OpenCalls++;
            throw new InvalidOperationException("Material resolution must not read texture payloads.");
        }
    }
}
