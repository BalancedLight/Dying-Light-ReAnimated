using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class RpackResourceCustodyTests
{
    [Theory]
    [InlineData(RpackTestCompression.None)]
    [InlineData(RpackTestCompression.Zlib)]
    [InlineData(RpackTestCompression.Lzma)]
    public async Task AllItemsAndOpaqueMetadataRoundTripThroughAStandaloneEnvelope(RpackTestCompression compression)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[][] payloads = ["metadata"u8.ToArray(), "complete-level-data"u8.ToArray()];
            string path = await WritePack(directory, payloads, compression);
            byte[] original = await File.ReadAllBytesAsync(path);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var asset = Assert.Single(catalog.Assets);
            var custody = await catalog.ReadRpackResourceCustodyAsync(asset);
            Assert.Equal(asset, custody.Asset);
            Assert.Equal(77, custody.Header.Unknown);
            Assert.Equal(2, custody.Items.Length);
            Assert.Equal(payloads[0], custody.Items[0].Payload.ToArray());
            Assert.Equal(payloads[1], custody.Items[1].Payload.ToArray());
            var sourceChunk = Assert.Single(custody.Chunks);
            Assert.Equal(0xAB20, sourceChunk.Descriptor.Flags);
            Assert.Equal(0x234, sourceChunk.Descriptor.Category);
            Assert.Equal(11, custody.Items[0].Descriptor.Flags);
            Assert.Equal(42, custody.Items[0].Descriptor.StorageGroupId);
            Assert.Equal(123, custody.Items[0].Descriptor.Unknown);
            Assert.Equal(Hash(payloads.SelectMany(static bytes => bytes).ToArray()), custody.ContentSha256);
            Assert.Equal(Hash(original.AsSpan(checked((int)sourceChunk.Descriptor.Offset),
                checked((int)sourceChunk.Descriptor.StoredSize)).ToArray()), sourceChunk.StoredSha256);

            string output = Path.Combine(directory, "standalone.rpack");
            var result = await Rp6lResourceEnvelopeWriter.WriteNewAsync(output, custody.Header, custody.Resource,
                custody.Items.Select(static item => new Rp6lResourceItemPayload(item.Descriptor, item.Payload, item.ContentSha256)).ToArray(),
                custody.Chunks.Select(static chunk => chunk.Descriptor).ToArray(), cache);
            var readback = result.Readback;
            Assert.Equal(custody.Header.Unknown, readback.Header.Unknown);
            Assert.Equal(custody.Resource.Name, readback.Resource.Name);
            Assert.Equal(Rp6lResourceTypes.Texture, readback.Resource.ResourceType);
            Assert.Equal(2, readback.Chunks.Length);
            Assert.Equal(custody.ContentSha256, readback.ContentSha256);
            for (int index = 0; index < payloads.Length; index++)
            {
                Assert.Equal(payloads[index], readback.Items[index].Payload.ToArray());
                Assert.Equal(custody.Items[index].Descriptor.Flags, readback.Items[index].Descriptor.Flags);
                Assert.Equal(custody.Items[index].Descriptor.StorageGroupId, readback.Items[index].Descriptor.StorageGroupId);
                Assert.Equal(custody.Items[index].Descriptor.Unknown, readback.Items[index].Descriptor.Unknown);
                Assert.Equal(sourceChunk.Descriptor.Flags, readback.Chunks[index].Flags);
                Assert.Equal(sourceChunk.Descriptor.Category, readback.Chunks[index].Category);
                Assert.Equal(sourceChunk.Descriptor.Unknown0, readback.Chunks[index].Unknown0);
                Assert.Equal(sourceChunk.Descriptor.Unknown1, readback.Chunks[index].Unknown1);
                Assert.Equal(index, readback.Items[index].Descriptor.ChunkIndex);
                Assert.Equal(0, readback.Items[index].Descriptor.Offset);
                Assert.Equal(Rp6lCompression.None, readback.Chunks[index].Compression);
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Equal(Hash(await File.ReadAllBytesAsync(output)), result.ArchiveSha256);
            Assert.Equal(readback.ContentSha256, (await Rp6lResourceEnvelopeWriter.ReadBackAsync(output, cache)).ContentSha256);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task TimestampPreservingCompressedChangesReadCurrentContentInsteadOfCachedContent()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] first = Enumerable.Repeat((byte)17, 8192).ToArray();
            byte[] second = Enumerable.Repeat((byte)18, 8192).ToArray();
            string path = await WritePack(directory, [first], RpackTestCompression.Zlib);
            byte[] original = await File.ReadAllBytesAsync(path);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var asset = Assert.Single(catalog.Assets);
            await using (Stream stream = await catalog.OpenReadAsync(asset))
            {
                using MemoryStream copied = new();
                await stream.CopyToAsync(copied);
                Assert.Equal(first, copied.ToArray());
            }
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            await WritePack(directory, [second], RpackTestCompression.Zlib);
            Assert.Equal(original.Length, new FileInfo(path).Length);
            File.SetLastWriteTimeUtc(path, timestamp);
            var custody = await catalog.ReadRpackResourceCustodyAsync(asset);
            Assert.Equal(second, Assert.Single(custody.Items).Payload.ToArray());
            Assert.Equal(Hash(second), custody.ContentSha256);
            Assert.NotEqual(Hash(first), custody.ContentSha256);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task AChangedTableRowIsRejectedEvenWhenMetadataAndCacheIdentityStayTheSame()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, ["data"u8.ToArray()], RpackTestCompression.None);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var asset = Assert.Single(catalog.Assets);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68), 124);
            DateTime timestamp = File.GetLastWriteTimeUtc(path);
            await File.WriteAllBytesAsync(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);
            await Assert.ThrowsAsync<IOException>(() => catalog.ReadRpackResourceCustodyAsync(asset).AsTask());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task ForgedRowsUnreadableItemsAndCancellationAreRejected()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, ["data"u8.ToArray()], RpackTestCompression.None);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var asset = Assert.Single(catalog.Assets);
            var forged = asset with { Source = asset.Source with { EntryPath = "other#0" } };
            await Assert.ThrowsAsync<IOException>(() => provider.ReadRpackResourceCustodyAsync(forged).AsTask());
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.ReadRpackResourceCustodyAsync(asset, canceled.Token).AsTask());

            byte[] bytes = await File.ReadAllBytesAsync(path);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), -1);
            string unreadable = Path.Combine(directory, "unreadable.rpack");
            await File.WriteAllBytesAsync(unreadable, bytes);
            await using var hashProvider = new RpackAssetProvider("hash-packs", [new(unreadable, 10)], cache);
            var hashCatalog = await RetailAssetCatalog.BuildAsync([hashProvider]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                hashProvider.ReadRpackResourceCustodyAsync(Assert.Single(hashCatalog.Assets)).AsTask());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task IncompleteOrModifiedItemsCannotBeExportedAndExistingFilesArePreserved()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, ["metadata"u8.ToArray(), "all-levels"u8.ToArray()], RpackTestCompression.None);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var custody = await provider.ReadRpackResourceCustodyAsync(Assert.Single(catalog.Assets));
            var items = custody.Items.Select(static item =>
                new Rp6lResourceItemPayload(item.Descriptor, item.Payload, item.ContentSha256)).ToArray();
            var chunks = custody.Chunks.Select(static chunk => chunk.Descriptor).ToArray();
            Assert.Throws<InvalidDataException>(() => Rp6lResourceEnvelopeWriter.Build(
                custody.Header, custody.Resource, items[..1], chunks));
            Assert.Throws<InvalidDataException>(() => Rp6lResourceEnvelopeWriter.Build(
                custody.Header, custody.Resource,
                [items[0] with { ContentSha256 = new string('0', 64) }, items[1]], chunks));
            string output = Path.Combine(directory, "existing.rpack");
            byte[] retained = "retained"u8.ToArray();
            await File.WriteAllBytesAsync(output, retained);
            await Assert.ThrowsAsync<IOException>(() => Rp6lResourceEnvelopeWriter.WriteNewAsync(
                output, custody.Header, custody.Resource, items, chunks, cache));
            Assert.Equal(retained, await File.ReadAllBytesAsync(output));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            string canceledOutput = Path.Combine(directory, "canceled.rpack");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rp6lResourceEnvelopeWriter.WriteNewAsync(
                canceledOutput, custody.Header, custody.Resource, items, chunks, cache, canceled.Token));
            Assert.False(File.Exists(canceledOutput));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task ContentKeyedCacheRejectsInvalidPackedDigests(string digest)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, ["data"u8.ToArray()], RpackTestCompression.Zlib);
            var archive = await Rp6lArchive.OpenAsync(path);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await Assert.ThrowsAsync<ArgumentException>(() =>
                cache.OpenChunkAsync(archive, archive.Chunks[0], digest).AsTask());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(1, RpackTestCompression.None)]
    [InlineData(5, RpackTestCompression.None)]
    [InlineData(1, RpackTestCompression.Zlib)]
    [InlineData(5, RpackTestCompression.Zlib)]
    [InlineData(1, RpackTestCompression.Lzma)]
    [InlineData(5, RpackTestCompression.Lzma)]
    public async Task NormalMeshesPreserveEveryOpaqueItemAndMetadataInNewObjectUnits(
        int itemCount, RpackTestCompression compression)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[][] payloads = Enumerable.Range(0, itemCount)
                .Select(static index => new byte[9 + index]).ToArray();
            for (int index = 0; index < payloads.Length; index++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(payloads[index], 0.01f + index);
                payloads[index][^1] = checked((byte)(0x90 + index));
            }
            string path = await WritePack(directory, payloads, compression, Rp6lResourceTypes.Mesh);
            byte[] original = await File.ReadAllBytesAsync(path);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var custody = await provider.ReadRpackResourceCustodyAsync(Assert.Single(catalog.Assets));
            var items = custody.Items.Select(static item =>
                new Rp6lResourceItemPayload(item.Descriptor, item.Payload, item.ContentSha256)).ToArray();
            var chunks = custody.Chunks.Select(static chunk => chunk.Descriptor).ToArray();
            string output = Path.Combine(directory, "preserved.msh_obj");
            var exported = await Rp6lResourceEnvelopeWriter.WriteNewAsync(
                output, custody.Header, custody.Resource, items, chunks, cache);
            var readback = await Rp6lResourceEnvelopeWriter.ReadBackAsync(output, cache);
            Assert.Equal(Rp6lResourceTypes.Mesh, readback.Resource.ResourceType);
            Assert.Equal(custody.Resource.Name, readback.Resource.Name);
            Assert.Equal(custody.Header.Unknown, readback.Header.Unknown);
            Assert.Equal(itemCount, readback.Items.Length);
            Assert.Equal(itemCount, readback.Chunks.Length);
            Assert.Equal(custody.ContentSha256, readback.ContentSha256);
            for (int index = 0; index < itemCount; index++)
            {
                var item = readback.Items[index];
                var chunk = readback.Chunks[index];
                Assert.Equal(payloads[index], item.Payload.ToArray());
                Assert.Equal(custody.Items[index].ContentSha256, item.ContentSha256);
                Assert.Equal(custody.Items[index].Descriptor.Flags, item.Descriptor.Flags);
                Assert.Equal(custody.Items[index].Descriptor.StorageGroupId, item.Descriptor.StorageGroupId);
                Assert.Equal(custody.Items[index].Descriptor.Unknown, item.Descriptor.Unknown);
                Assert.Equal(chunks[0].Flags, chunk.Flags);
                Assert.Equal(chunks[0].Category, chunk.Category);
                Assert.Equal(chunks[0].Unknown0, chunk.Unknown0);
                Assert.Equal(chunks[0].Unknown1, chunk.Unknown1);
                Assert.Equal(Rp6lCompression.None, chunk.Compression);
                Assert.Equal(index, item.Descriptor.ChunkIndex);
                Assert.Equal(0, item.Descriptor.Offset);
            }
            byte[] retained = await File.ReadAllBytesAsync(output);
            Assert.Equal(Hash(retained), exported.ArchiveSha256);
            await Assert.ThrowsAsync<IOException>(() => Rp6lResourceEnvelopeWriter.WriteNewAsync(
                output, custody.Header, custody.Resource, items, chunks, cache));
            Assert.Equal(retained, await File.ReadAllBytesAsync(output));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task CompilerFlaggedMeshResourceTypesAreNotNormalized()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await WritePack(directory, ["opaque"u8.ToArray()],
                RpackTestCompression.None, Rp6lResourceTypes.Mesh);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await using var provider = new RpackAssetProvider("packs", [new(path, 10)], cache);
            var catalog = await RetailAssetCatalog.BuildAsync([provider]);
            var custody = await provider.ReadRpackResourceCustodyAsync(Assert.Single(catalog.Assets));
            var flagged = custody.Resource with { ResourceType = unchecked((short)(Rp6lResourceTypes.Mesh | 0x8000)) };
            Assert.Throws<InvalidDataException>(() => Rp6lResourceEnvelopeWriter.Build(
                custody.Header, flagged,
                custody.Items.Select(static item =>
                    new Rp6lResourceItemPayload(item.Descriptor, item.Payload, item.ContentSha256)).ToArray(),
                custody.Chunks.Select(static chunk => chunk.Descriptor).ToArray()));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<string> WritePack(
        string directory, byte[][] payloads, RpackTestCompression compression,
        short resourceType = Rp6lResourceTypes.Texture)
    {
        Directory.CreateDirectory(directory);
        string resourceName = resourceType == Rp6lResourceTypes.Mesh ? "generic_mesh" : "generic_texture";
        byte[] bytes = RpackTestData.BuildArchive(resourceName, resourceType,
            payloads.Select(static payload => new RpackTestItem(42, payload)).ToArray(), compression);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), 77);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(36),
            resourceType == Rp6lResourceTypes.Mesh ? (ushort)0xAB10 : (ushort)0xAB20);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38), 0x234);
        for (int index = 0; index < payloads.Length; index++)
        {
            int row = 56 + index * 16;
            bytes[row + 1] = checked((byte)(11 + index));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(row + 2), checked((short)(42 + index)));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(row + 12), 123 + index);
        }
        string path = Path.Combine(directory, "source.rpack");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}
