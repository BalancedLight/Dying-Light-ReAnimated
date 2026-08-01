using System.Runtime.CompilerServices;
using System.Text;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.Tests;

public sealed class AssetCatalogTests
{
    [Fact]
    public async Task ResolvesPriorityAndPersistsAllCandidates()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            RetailAssetLogicalId id =
                RetailAssetLogicalId.Rpack(272, "Survivor_A");
            FakeProvider stock = new(
                "stock",
                CreateRecord(id, "stock", 10),
                "stock-bytes");
            FakeProvider dlc = new(
                "dlc",
                CreateRecord(id, "dlc", 20),
                "dlc-bytes");
            await using RetailAssetSqliteIndex index = new(
                Path.Combine(directory, "assets.sqlite"));
            RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(
                [stock, dlc],
                index);

            RetailAssetRecord winner = Assert.IsType<RetailAssetRecord>(
                catalog.Resolve(id));
            Assert.Equal("dlc", winner.Source.ProviderId);
            Assert.Collection(
                catalog.GetCandidates(id),
                static _ => { },
                static _ => { });
            RetailAssetConflict conflict = Assert.Single(catalog.Conflicts);
            Assert.Equal("stock", Assert.Single(conflict.Shadowed).Source.ProviderId);

            await using Stream stream = await catalog.OpenReadAsync(id);
            using StreamReader reader = new(
                stream,
                Encoding.UTF8);
            Assert.Equal("dlc-bytes", await reader.ReadToEndAsync());

            IReadOnlyList<RetailAssetRecord> stored =
                await index.LoadAsync();
            Assert.Equal(2, stored.Count);
            Assert.Contains(
                stored,
                static row => row.Source.ProviderId == "stock");
            Assert.Contains(
                stored,
                static row => row.Source.ProviderId == "dlc");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void AssetIdsNormalizeCaseAndRejectTraversal()
    {
        Assert.Equal(
            RetailAssetLogicalId.Rpack(272, "Player_1_FPP"),
            RetailAssetLogicalId.Rpack(272, "player_1_fpp"));
        Assert.Equal(
            "data/characters/player.fed",
            RetailAssetLogicalId.VirtualFile(
                @"Data\Characters\Player.fed").Name);
        Assert.Throws<InvalidDataException>(
            () => RetailAssetLogicalId.VirtualFile("../outside.fed"));

        RetailAssetLogicalId logical =
            RetailAssetLogicalId.Rpack(272, "player");
        RetailAssetId stock = RetailAssetId.Create(
            logical,
            "install",
            "stock",
            1,
            10,
            "snapshot");
        RetailAssetId dlc = RetailAssetId.Create(
            logical,
            "install",
            "dlc",
            1,
            20,
            "snapshot");
        Assert.NotEqual(stock, dlc);
        Assert.Equal(logical, stock.LogicalId);
    }

    private static RetailAssetRecord CreateRecord(
        RetailAssetLogicalId logicalId,
        string provider,
        int priority) =>
        new(
            RetailAssetId.Create(
                logicalId,
                "test-install",
                provider,
                0,
                priority,
                string.Concat(provider, "-snapshot"),
                string.Concat(provider, "-content")),
            logicalId.Name,
            new RetailAssetSource(
                provider,
                RetailAssetSourceKind.Rpack,
                priority,
                string.Concat(provider, ".rpack"),
                logicalId.Name,
                0,
                10,
                10,
                DateTime.UnixEpoch));

    private sealed class FakeProvider : IRetailAssetProvider
    {
        private readonly RetailAssetRecord _asset;
        private readonly byte[] _payload;

        public FakeProvider(
            string providerId,
            RetailAssetRecord asset,
            string payload)
        {
            ProviderId = providerId;
            _asset = asset;
            _payload = Encoding.UTF8.GetBytes(payload);
        }

        public string ProviderId { get; }

        public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return _asset;
        }

        public ValueTask<Stream> OpenReadAsync(
            RetailAssetRecord asset,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(_asset, asset);
            return ValueTask.FromResult<Stream>(
                new MemoryStream(_payload, writable: false));
        }
    }
}
