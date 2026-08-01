using System.Diagnostics;
using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class RpackStabilityAcceptanceTests
{
    [Fact]
    public async Task RepeatedCatalogOpenCloseAndAssetSwitchingRetainsExactPayloads()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[][] payloads =
            [
                CreatePayload(96 * 1024, 0x19),
                CreatePayload(112 * 1024, 0x53),
                CreatePayload(128 * 1024, 0xA7),
            ];
            string[] paths = new string[payloads.Length];
            for (var index = 0; index < payloads.Length; index++)
            {
                paths[index] = await RpackTestData.WriteArchiveAsync(
                    Path.Combine(directory, $"pack-{index}"),
                    $"switch_mesh_{index}",
                    Rp6lResourceTypes.Mesh,
                    [new RpackTestItem(16, payloads[index])],
                    RpackTestCompression.Zlib);
            }

            string cacheDirectory = Path.Combine(directory, "cache");
            string indexPath = Path.Combine(directory, "catalog", "retail.sqlite");
            RpackSource[] sources = paths
                .Select((path, index) => new RpackSource(path, 100 + index))
                .ToArray();

            const int sessionCount = 6;
            const int switchesPerSession = 18;
            for (var session = 0; session < sessionCount; session++)
            {
                await using Rp6lChunkCache cache = new(
                    CreateDiskOnlyOptions(cacheDirectory));
                await using RpackAssetProvider provider = new(
                    "stability-rpacks",
                    sources,
                    cache,
                    installId: "stability-install");
                await using RetailAssetSqliteIndex index = new(indexPath);
                RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(
                    [provider],
                    index);

                Assert.Equal(payloads.Length, catalog.Assets.Count);
                Assert.Equal(
                    payloads.Length,
                    (await index.LoadAsync()).Count);

                for (var switchIndex = 0;
                     switchIndex < switchesPerSession;
                     switchIndex++)
                {
                    int selected = (session + switchIndex * 2) % payloads.Length;
                    RetailAssetLogicalId logicalId = RetailAssetLogicalId.Rpack(
                        Rp6lResourceTypes.Mesh,
                        $"switch_mesh_{selected}");
                    RetailAssetRecord asset = Assert.IsType<RetailAssetRecord>(
                        catalog.Resolve(logicalId));
                    await using Stream stream = switchIndex % 2 == 0
                        ? await catalog.OpenReadAsync(logicalId)
                        : await catalog.OpenReadAsync(asset.Id);
                    using MemoryStream actual = new();
                    await stream.CopyToAsync(actual);
                    Assert.Equal(payloads[selected], actual.ToArray());
                }
            }

            Assert.Equal(
                payloads.Length,
                Directory.EnumerateFiles(cacheDirectory, "*.chunk").Count());
            Assert.Equal(
                payloads.Length,
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.chunk.sha256").Count());
            Assert.Empty(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentSameChunkRequestsPublishOneVerifiedCachePair()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] expected = CreatePayload(512 * 1024, 0x6D);
            string archivePath = await RpackTestData.WriteArchiveAsync(
                Path.Combine(directory, "archive"),
                "concurrent_mesh",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, expected)],
                RpackTestCompression.Zlib);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(archivePath);
            string cacheDirectory = Path.Combine(directory, "cache");
            await using Rp6lChunkCache cache = new(
                CreateDiskOnlyOptions(cacheDirectory));
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            Task<byte[]>[] reads = Enumerable.Range(0, 16)
                .Select(async _ =>
                {
                    await start.Task;
                    return await archive.ReadItemBytesAsync(
                        Assert.Single(archive.Items),
                        cache);
                })
                .ToArray();
            start.SetResult();

            byte[][] results = await Task.WhenAll(reads);

            Assert.All(results, result => Assert.Equal(expected, result));
            string chunkPath = Assert.Single(
                Directory.EnumerateFiles(cacheDirectory, "*.chunk"));
            string hashPath = Assert.Single(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.chunk.sha256"));
            Assert.Equal(
                string.Concat(chunkPath, ".sha256"),
                hashPath,
                ignoreCase: true);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(expected)),
                (await File.ReadAllTextAsync(hashPath)).Trim());
            Assert.Empty(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationAfterInflationBeginsLeavesNoPartialEntryAndRetrySucceeds()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] expected = new byte[24 * 1024 * 1024];
            string archivePath = await RpackTestData.WriteArchiveAsync(
                Path.Combine(directory, "archive"),
                "cancel_during_inflation",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, expected)],
                RpackTestCompression.Zlib);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(archivePath);
            string cacheDirectory = Path.Combine(directory, "cache");
            await using Rp6lChunkCache cache = new(
                CreateDiskOnlyOptions(cacheDirectory) with
                {
                    CopyBufferBytes = 4096,
                });
            using CancellationTokenSource cancellation = new();

            Task<byte[]> read = archive.ReadItemBytesAsync(
                Assert.Single(archive.Items),
                cache,
                cancellationToken: cancellation.Token).AsTask();
            await WaitForPartialInflationAsync(
                cacheDirectory,
                expected.LongLength,
                read);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await read);
            Assert.Empty(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly));

            byte[] retried = await archive.ReadItemBytesAsync(
                Assert.Single(archive.Items),
                cache);
            Assert.Equal(expected.Length, retried.Length);
            Assert.Equal(
                SHA256.HashData(expected),
                SHA256.HashData(retried));
            Assert.Single(
                Directory.EnumerateFiles(cacheDirectory, "*.chunk"));
            Assert.Single(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.chunk.sha256"));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task EvictionThenSidecarCorruptionRegeneratesAndReentersLru()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] firstPayload = CreatePayload(8 * 1024, 0x21);
            byte[] secondPayload = CreatePayload(8 * 1024, 0xB4);
            string firstPath = await RpackTestData.WriteArchiveAsync(
                Path.Combine(directory, "first"),
                "first",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, firstPayload)],
                RpackTestCompression.Zlib);
            string secondPath = await RpackTestData.WriteArchiveAsync(
                Path.Combine(directory, "second"),
                "second",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, secondPayload)],
                RpackTestCompression.Zlib);
            Rp6lArchive first = await Rp6lArchive.OpenAsync(firstPath);
            Rp6lArchive second = await Rp6lArchive.OpenAsync(secondPath);
            string cacheDirectory = Path.Combine(directory, "cache");
            Rp6lChunkCacheOptions options = CreateDiskOnlyOptions(
                cacheDirectory) with
            {
                MaximumDiskBytes = 12 * 1024,
            };

            await using (Rp6lChunkCache cache = new(options))
            {
                _ = await first.ReadItemBytesAsync(
                    Assert.Single(first.Items),
                    cache);
                _ = await second.ReadItemBytesAsync(
                    Assert.Single(second.Items),
                    cache);
            }

            string retainedChunk = Assert.Single(
                Directory.EnumerateFiles(cacheDirectory, "*.chunk"));
            string retainedHash = Assert.Single(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.chunk.sha256"));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(secondPayload)),
                (await File.ReadAllTextAsync(retainedHash)).Trim());
            await File.WriteAllTextAsync(
                retainedHash,
                new string('0', 64));

            await using (Rp6lChunkCache cache = new(options))
            {
                Assert.Equal(
                    secondPayload,
                    await second.ReadItemBytesAsync(
                        Assert.Single(second.Items),
                        cache));
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(secondPayload)),
                    (await File.ReadAllTextAsync(
                        string.Concat(retainedChunk, ".sha256"))).Trim());

                Assert.Equal(
                    firstPayload,
                    await first.ReadItemBytesAsync(
                        Assert.Single(first.Items),
                        cache));
            }

            string finalChunk = Assert.Single(
                Directory.EnumerateFiles(cacheDirectory, "*.chunk"));
            string finalHash = Assert.Single(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.chunk.sha256"));
            Assert.Equal(
                string.Concat(finalChunk, ".sha256"),
                finalHash,
                ignoreCase: true);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(firstPayload)),
                (await File.ReadAllTextAsync(finalHash)).Trim());
            Assert.Empty(
                Directory.EnumerateFiles(
                    cacheDirectory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Rp6lChunkCacheOptions CreateDiskOnlyOptions(
        string cacheDirectory) =>
        new()
        {
            CacheDirectory = cacheDirectory,
            MaximumMemoryBytes = 0,
            MaximumMemoryEntryBytes = 0,
            MaximumDiskBytes = 64 * 1024 * 1024,
            CopyBufferBytes = 4096,
        };

    private static byte[] CreatePayload(int length, byte seed)
    {
        byte[] result = new byte[length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = unchecked((byte)(seed + index * 37));
        }

        return result;
    }

    private static async Task WaitForPartialInflationAsync(
        string cacheDirectory,
        long expectedLength,
        Task operation)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (operation.IsCompleted)
            {
                throw new Xunit.Sdk.XunitException(
                    "Chunk inflation completed before the regression could cancel an observed partial cache write.");
            }

            string? temporaryPath = Directory
                .EnumerateFiles(
                    cacheDirectory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            if (temporaryPath is not null)
            {
                long length = new FileInfo(temporaryPath).Length;
                if (length > 0 && length < expectedLength)
                {
                    return;
                }
            }

            await Task.Yield();
        }

        throw new Xunit.Sdk.XunitException(
            "Timed out waiting for an in-progress RP6L inflation write.");
    }
}
