using System.Buffers.Binary;
using System.Text;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class RpackEffectAssetProviderTests
{
    private static readonly int[] ExpectedPriorities = [30, 20, 10];
    [Theory]
    [InlineData(RpackTestCompression.None)]
    [InlineData(RpackTestCompression.Zlib)]
    [InlineData(RpackTestCompression.Lzma)]
    public async Task EffectsExposeExactTextAndPreserveTheirParentBundle(RpackTestCompression compression)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] bundle = Bundle(("spark", -1, "Emitter(\"spark\");\r\n"), ("effects/glow", 7, "Glow();"));
            string path = await WritePack(directory, bundle, compression);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            var catalog = await RetailAssetCatalog.BuildAsync([parent, effects]);
            var spark = Assert.IsType<RetailAssetRecord>(catalog.Resolve(RetailAssetLogicalId.VirtualFile("spark.fx")));
            Assert.Equal(RetailAssetSourceKind.RpackEmbeddedEffect, spark.Source.Kind);
            Assert.Equal(10, spark.Id.Precedence);
            Assert.Equal(0, spark.Source.ResourceIndex);
            await using Stream source = await catalog.OpenReadAsync(spark);
            Assert.False(source.CanWrite);
            Assert.Equal("Emitter(\"spark\");\r\n", await new StreamReader(source).ReadToEndAsync());
            Assert.NotNull(catalog.Resolve(RetailAssetLogicalId.VirtualFile("effects/glow.fx")));
            Assert.Null(catalog.Resolve(RetailAssetLogicalId.VirtualFile("glow.fx")));
            var original = await effects.ReadOriginalBundleAsync(spark);
            Assert.Equal(bundle, original.Payload);
            var custody = await effects.ReadEmbeddedCustodyAsync(spark);
            var parentAsset = Assert.IsType<RetailAssetRecord>(catalog.Resolve(
                RetailAssetLogicalId.Rpack(Rp6lResourceTypes.Effect, "effect_bundle")));
            Assert.Equal(parentAsset, custody.Parent);
            Assert.Equal(bundle, custody.BundlePayload.ToArray());
            Assert.Equal(-1, custody.Definition.Kind);
            Assert.Equal(42, custody.Item.StorageGroupId);
            Assert.Equal(80, custody.Chunk.Flags & 0xFF);
            Assert.Empty(effects.SourceErrors);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task QualifiedNamesAndPackPrioritiesKeepEveryConflictCandidate()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string first = await WritePack(Path.Combine(directory, "base"), Bundle(("shared", 0, "Base();")));
            string second = await WritePack(Path.Combine(directory, "patch"), Bundle(("shared", 0, "Patch();")));
            string loose = Path.Combine(directory, "loose");
            Directory.CreateDirectory(loose);
            await File.WriteAllTextAsync(Path.Combine(loose, "shared.fx"), "Loose();");
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(first, 10), new(second, 20)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            var files = new LooseFileAssetProvider("loose", loose, [".fx"], 30);
            var catalog = await RetailAssetCatalog.BuildAsync([parent, effects, files]);
            var logical = RetailAssetLogicalId.VirtualFile("shared.fx");
            var candidates = catalog.GetCandidates(logical);
            Assert.Equal(3, candidates.Count);
            Assert.Equal(ExpectedPriorities, candidates.Select(asset => asset.Source.Priority));
            Assert.Equal("loose", catalog.Resolve(logical)!.Source.ProviderId);
            await using Stream packed = await catalog.OpenReadAsync(candidates[1]);
            Assert.Equal("Patch();", await new StreamReader(packed).ReadToEndAsync());
            Assert.Single(catalog.Conflicts, conflict => conflict.Id == logical);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task AChangedBundleWithPreservedMetadataIsRejected()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, Bundle(("spark", 0, "First();")));
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            var catalog = await RetailAssetCatalog.BuildAsync([effects]);
            var asset = Assert.Single(catalog.Assets);
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            await WritePack(directory, Bundle(("spark", 0, "Other();")));
            File.SetLastWriteTimeUtc(path, timestamp);
            await Assert.ThrowsAsync<IOException>(() => effects.OpenReadAsync(asset).AsTask());
            await Assert.ThrowsAsync<IOException>(() => effects.ReadOriginalBundleAsync(asset).AsTask());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task PackedChunkMutationOutsideSnapshotSamplesCannotReuseCachedText()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var random = new Random(1234);
            string text = new(Enumerable.Range(0, 1_000_000).Select(_ => (char)('a' + random.Next(26))).ToArray());
            string path = await WritePack(directory, Bundle(("large", 0, text)), RpackTestCompression.Zlib);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            var catalog = await RetailAssetCatalog.BuildAsync([effects]);
            var asset = Assert.Single(catalog.Assets);
            var before = await effects.CaptureSnapshotAsync();
            var archive = await parent.GetArchiveAsync(path);
            var chunk = Assert.Single(archive.Chunks);
            Assert.True(chunk.StoredSize > 256 * 1024);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            bytes[checked((int)(chunk.Offset + chunk.StoredSize / 8))] ^= 1;
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            await File.WriteAllBytesAsync(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);
            Assert.Equal(before.StableFingerprint, (await effects.CaptureSnapshotAsync()).StableFingerprint);
            await Assert.ThrowsAsync<IOException>(() => effects.OpenReadAsync(asset).AsTask());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedBundlesProduceDiagnosticsAndNoPartialRows(bool wrongChunkType)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] valid = Bundle(("spark", 0, "Spark();"));
            byte[] payload = wrongChunkType ? valid : valid[..^1].Concat("broken"u8.ToArray()).ToArray();
            string path = await WritePack(directory, payload, chunkType: wrongChunkType ? (ushort)16 : (ushort)80);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            var catalog = await RetailAssetCatalog.BuildAsync([effects]);
            Assert.Empty(catalog.Assets);
            var error = Assert.Single(effects.SourceErrors);
            Assert.Equal(0, error.ResourceIndex);
            Assert.Equal(nameof(InvalidDataException), error.ErrorType);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task PersistentCatalogRestoresEmbeddedSourceKindAndCustody()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, Bundle(("spark", 2, "Spark();")));
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            await using var index = new RetailAssetSqliteIndex(Path.Combine(directory, "catalog.sqlite"));
            var first = await RetailAssetCatalog.BuildAsync([parent, effects], index);
            Assert.False(first.WasRestoredFromPersistentIndex);
            var restored = await RetailAssetCatalog.BuildAsync([parent, effects], index);
            Assert.True(restored.WasRestoredFromPersistentIndex);
            var asset = Assert.IsType<RetailAssetRecord>(restored.Resolve(RetailAssetLogicalId.VirtualFile("spark.fx")));
            Assert.Equal(RetailAssetSourceKind.RpackEmbeddedEffect, asset.Source.Kind);
            await using Stream source = await restored.OpenReadAsync(asset);
            Assert.Equal("Spark();", await new StreamReader(source).ReadToEndAsync());
            Assert.Equal(2, (await effects.ReadEmbeddedCustodyAsync(asset)).Definition.Kind);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task PhysicalIdentityMustMatchEvenWhenNameAndTextMatch()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, Bundle(("spark", 0, "Spark();")));
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            var catalog = await RetailAssetCatalog.BuildAsync([effects]);
            var asset = Assert.Single(catalog.Assets);
            var changedEntry = asset with { Source = asset.Source with { EntryPath = asset.Source.EntryPath + "/7" } };
            await Assert.ThrowsAsync<IOException>(() => effects.OpenReadAsync(changedEntry).AsTask());
            var changedFingerprint = asset with
            {
                Id = RetailAssetId.Create(asset.Id.LogicalId, asset.Id.InstallId, asset.Id.ProviderId,
                    asset.Id.SourceIndex, asset.Id.Precedence, "different", asset.Id.ContentFingerprint),
            };
            await Assert.ThrowsAsync<IOException>(() => effects.OpenReadAsync(changedFingerprint).AsTask());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshProvidersKeepMalformedBundleDiagnosticsWhenUsingPersistentCatalogs(bool legacyPartialSnapshot)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string healthy = await WritePack(Path.Combine(directory, "healthy"), Bundle(("glow", 0, "Glow();")));
            byte[] valid = Bundle(("spark", 0, "Spark();"));
            string malformed = await WritePack(Path.Combine(directory, "malformed"),
                valid[..^1].Concat("broken"u8.ToArray()).ToArray());
            RpackSource[] sources = [new(healthy, 20), new(malformed, 10)];
            string database = Path.Combine(directory, "catalog.sqlite");
            await using (var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "first-cache") }))
            await using (var parent = new RpackAssetProvider("packs", sources, cache))
            await using (var index = new RetailAssetSqliteIndex(database))
            {
                var effects = new RpackEffectAssetProvider("effects", parent, cache);
                var first = await RetailAssetCatalog.BuildAsync([effects], index);
                Assert.False(first.WasRestoredFromPersistentIndex);
                Assert.Equal("glow.fx", Assert.Single(first.Assets).Id.Name);
                Assert.Single(effects.SourceErrors);
                Assert.False(effects.CanPersistCatalog);
                var snapshot = await effects.CaptureSnapshotAsync();
                Assert.Null(await index.TryLoadValidatedAsync([snapshot]));
                if (legacyPartialSnapshot)
                {
                    var parentSnapshot = await parent.CaptureSnapshotAsync();
                    var legacy = snapshot with
                    {
                        ConfigurationFingerprint = RetailAssetIdentity.CreateSourceFingerprint(
                            parentSnapshot.ConfigurationFingerprint, "embedded-effects-v1"),
                    };
                    await index.ReplaceSnapshotAsync([legacy], first.Assets);
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                await using var cache = new Rp6lChunkCache(new()
                {
                    CacheDirectory = Path.Combine(directory, $"fresh-cache-{attempt}"),
                });
                await using var parent = new RpackAssetProvider("packs", sources, cache);
                await using var index = new RetailAssetSqliteIndex(database);
                var effects = new RpackEffectAssetProvider("effects", parent, cache);
                var rebuilt = await RetailAssetCatalog.BuildAsync([effects], index);
                Assert.False(rebuilt.WasRestoredFromPersistentIndex);
                Assert.Equal("glow.fx", Assert.Single(rebuilt.Assets).Id.Name);
                var error = Assert.Single(effects.SourceErrors);
                Assert.Equal(malformed, error.Path);
                Assert.Equal(nameof(InvalidDataException), error.ErrorType);
                Assert.False(effects.CanPersistCatalog);
                Assert.Null(await index.TryLoadValidatedAsync([await effects.CaptureSnapshotAsync()]));
            }
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task CancellationEscapesEnumerationWithoutCreatingSourceDiagnostics()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, Bundle(("spark", 0, "Spark();")));
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var parent = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var effects = new RpackEffectAssetProvider("effects", parent, cache);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var asset in effects.EnumerateAsync(cancellation.Token))
                    Assert.NotNull(asset);
            });
            Assert.Empty(effects.SourceErrors);
            Assert.True(effects.CanPersistCatalog);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static byte[] Bundle(params (string Name, sbyte Kind, string Text)[] definitions)
    {
        using var stream = new MemoryStream();
        foreach (var definition in definitions)
        {
            stream.Write(Encoding.UTF8.GetBytes(definition.Name));
            stream.WriteByte(0);
            stream.WriteByte(unchecked((byte)definition.Kind));
            stream.Write(Encoding.UTF8.GetBytes(definition.Text));
            stream.WriteByte(0);
        }
        stream.WriteByte(0);
        return stream.ToArray();
    }

    private static async Task<string> WritePack(
        string directory, byte[] bundle,
        RpackTestCompression compression = RpackTestCompression.None, ushort chunkType = 80)
    {
        Directory.CreateDirectory(directory);
        byte[] archive = RpackTestData.BuildArchive("effect_bundle", Rp6lResourceTypes.Effect,
            [new(42, bundle)], compression);
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(36), chunkType);
        string path = Path.Combine(directory, "effects.rpack");
        await File.WriteAllBytesAsync(path, archive);
        return path;
    }
}
