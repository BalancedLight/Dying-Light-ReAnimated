using System.Security.Cryptography;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledCorpusInspectorTests
{
    [Fact]
    public async Task MeshInventoryKeepsPhysicalIdentityItemHashesAndUnknownCompanions()
    {
        var fixture = RpackTestData.BuildCompiledMeshFixture();
        byte[][] payloads = [fixture.Metadata, fixture.Variants, [1, 2, 3], fixture.Vertices, fixture.Indices];
        await WithResource(Rp6lResourceTypes.Mesh, payloads, async (asset, archive, cache, digest) =>
        {
            var evidence = await Dl1CompiledCorpusInspector.InspectAsync(asset, archive, cache, digest);
            Assert.Equal(asset.Id, evidence.AssetId);
            Assert.Equal(5, evidence.Items.Length);
            Assert.NotNull(evidence.Hierarchy);
            Assert.True(evidence.Hierarchy.IsStructurallyValid);
            Assert.Equal(digest, evidence.ContentSha256);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fixture.Metadata)), evidence.Items[0].Sha256);
            Assert.Contains(evidence.Unknowns, u => u.Contains("CHR", StringComparison.Ordinal));
            Assert.False(evidence.RuntimeBindingVerified);
            await Assert.ThrowsAsync<InvalidDataException>(() => Dl1CompiledCorpusInspector.InspectAsync(asset, archive, cache, new string('a', 64)));
        });
    }

    [Fact]
    public async Task CompiledSequenceTableDoesNotInventClipBindings()
    {
        AnimationScrSections sections = AnimationScrCodec.Build([new("idle", "idle.anm2", 0, 30, 30)]);
        await WithResource(Rp6lResourceTypes.AnimationScript, [sections.RecordsAndNames, sections.IndexAndNames], async (asset, archive, cache, digest) =>
        {
            var evidence = await Dl1CompiledCorpusInspector.InspectAsync(asset, archive, cache, digest);
            Assert.Equal("idle", Assert.Single(evidence.AnimationScript!.Sequences).Name);
            Assert.Null(evidence.Hierarchy);
            Assert.Contains(evidence.Unknowns, u => u.Contains("do not prove clip references", StringComparison.Ordinal));
            Assert.False(evidence.RuntimeBindingVerified);
            var wrongIdentity = asset with { Id = RetailAssetId.Create(asset.Id.LogicalId, asset.Id.InstallId, asset.Id.ProviderId,
                asset.Id.SourceIndex, asset.Id.Precedence, "different-source") };
            await Assert.ThrowsAsync<InvalidDataException>(() => Dl1CompiledCorpusInspector.InspectAsync(wrongIdentity, archive, cache, digest));
        });
    }

    [Fact]
    public void AliasFieldReaderRejectsTruncatedHeaders() =>
        Assert.Throws<InvalidDataException>(() => Dl1RuntimeMeshObjectValidator.ReadAnimationScriptAlias(new byte[8]));

    private static async Task WithResource(short resourceType, byte[][] payloads,
        Func<RetailAssetRecord, Rp6lArchive, Rp6lChunkCache, string, Task> inspect)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await RpackTestData.WriteArchiveAsync(directory, "synthetic", resourceType,
                payloads.Select(static bytes => new RpackTestItem(42, bytes)).ToArray(), RpackTestCompression.None);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("synthetic", [new(path, 1)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var asset = Assert.Single(catalog.Assets);
            Rp6lArchive archive = await provider.GetArchiveAsync(path);
            await using Stream stream = await catalog.OpenReadAsync(asset);
            string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            await inspect(asset, archive, cache, digest);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
}
