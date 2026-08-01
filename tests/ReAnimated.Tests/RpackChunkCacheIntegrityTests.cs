using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class RpackChunkCacheIntegrityTests
{
    [Fact]
    public async Task SameLengthDiskCorruptionIsRejectedAndRegenerated()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] expected = Enumerable.Range(0, 8 * 1024)
                .Select(static index => unchecked((byte)(index * 31)))
                .ToArray();
            string archivePath = await RpackTestData.WriteArchiveAsync(
                Path.Combine(directory, "archive"),
                "cache_integrity",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, expected)],
                RpackTestCompression.Zlib);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(archivePath);
            Rp6lChunkCacheOptions options = CreateDiskOnlyOptions(directory);

            await using (Rp6lChunkCache cache = new(options))
            {
                Assert.Equal(
                    expected,
                    await archive.ReadItemBytesAsync(
                        Assert.Single(archive.Items),
                        cache));
            }

            string chunkPath = Assert.Single(
                Directory.EnumerateFiles(
                    options.CacheDirectory,
                    "*.chunk"));
            string hashPath = string.Concat(chunkPath, ".sha256");
            Assert.True(File.Exists(hashPath));
            File.WriteAllBytes(
                chunkPath,
                Enumerable.Repeat((byte)0xA5, expected.Length).ToArray());

            await using (Rp6lChunkCache cache = new(options))
            {
                Assert.Equal(
                    expected,
                    await archive.ReadItemBytesAsync(
                        Assert.Single(archive.Items),
                        cache));
            }

            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(expected)),
                (await File.ReadAllTextAsync(hashPath)).Trim());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task DiskLruEvictionRemovesDataAndIntegritySidecarTogether()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] firstPayload = Enumerable.Repeat(
                    (byte)0x31,
                    8 * 1024)
                .ToArray();
            byte[] secondPayload = Enumerable.Repeat(
                    (byte)0x72,
                    8 * 1024)
                .ToArray();
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
            Rp6lChunkCacheOptions options = CreateDiskOnlyOptions(
                directory,
                maximumDiskBytes: 12 * 1024);

            await using Rp6lChunkCache cache = new(options);
            Assert.Equal(
                firstPayload,
                await first.ReadItemBytesAsync(
                    Assert.Single(first.Items),
                    cache));
            Assert.Equal(
                secondPayload,
                await second.ReadItemBytesAsync(
                    Assert.Single(second.Items),
                    cache));

            string[] chunks = Directory.GetFiles(
                options.CacheDirectory,
                "*.chunk");
            string[] hashes = Directory.GetFiles(
                options.CacheDirectory,
                "*.chunk.sha256");
            Assert.Single(chunks);
            Assert.Single(hashes);
            Assert.Equal(
                string.Concat(chunks[0], ".sha256"),
                hashes[0],
                ignoreCase: true);
            Assert.Equal(
                secondPayload,
                await second.ReadItemBytesAsync(
                    Assert.Single(second.Items),
                    cache));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task FailedInflationLeavesNoPartialDataOrIntegrityFiles()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string archivePath = await RpackTestData.WriteArchiveAsync(
                Path.Combine(directory, "archive"),
                "corrupt",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, new byte[8 * 1024])],
                RpackTestCompression.Zlib);
            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath);
            archiveBytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(archivePath, archiveBytes);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(archivePath);
            Rp6lChunkCacheOptions options = CreateDiskOnlyOptions(directory);

            await using Rp6lChunkCache cache = new(options);
            await Assert.ThrowsAnyAsync<InvalidDataException>(
                async () => await archive.ReadItemBytesAsync(
                    Assert.Single(archive.Items),
                    cache));
            Assert.Empty(
                Directory.EnumerateFiles(
                    options.CacheDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Rp6lChunkCacheOptions CreateDiskOnlyOptions(
        string directory,
        long maximumDiskBytes = 64 * 1024 * 1024) =>
        new()
        {
            CacheDirectory = Path.Combine(directory, "cache"),
            MaximumMemoryBytes = 0,
            MaximumMemoryEntryBytes = 0,
            MaximumDiskBytes = maximumDiskBytes,
            CopyBufferBytes = 4096,
        };
}
