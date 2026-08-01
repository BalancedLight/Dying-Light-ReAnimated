using System.Text;
using System.Buffers.Binary;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class RpackArchiveTests
{
    [Theory]
    [InlineData(RpackTestCompression.None)]
    [InlineData(RpackTestCompression.Zlib)]
    [InlineData(RpackTestCompression.Lzma)]
    public async Task OpensAndExtractsBoundedResource(
        RpackTestCompression compression)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] expected = Encoding.UTF8.GetBytes(
                "retail-codec-roundtrip");
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "player_1_fpp",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, expected)],
                compression);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            Assert.Single(archive.Resources);
            Assert.Equal(compression switch
            {
                RpackTestCompression.None => Rp6lCompression.None,
                RpackTestCompression.Zlib => Rp6lCompression.Zlib,
                RpackTestCompression.Lzma => Rp6lCompression.Lzma,
                _ => throw new InvalidOperationException(),
            }, archive.Chunks[0].Compression);

            await using Rp6lChunkCache cache = new(new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 32 * 1024 * 1024,
            });
            byte[] actual = await archive.ReadItemBytesAsync(
                archive.Items[0],
                cache);
            Assert.Equal(expected, actual);
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(directory, "cache"),
                "*.tmp",
                SearchOption.TopDirectoryOnly));
            if (compression != RpackTestCompression.None)
            {
                FileInfo cached = Assert.Single(
                    new DirectoryInfo(Path.Combine(directory, "cache"))
                        .EnumerateFiles("*.chunk"));
                Assert.Equal(expected.Length, cached.Length);
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationStopsChunkMaterialization()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "cancel",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, new byte[4 * 1024 * 1024])],
                RpackTestCompression.Zlib);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            await using Rp6lChunkCache cache = new(new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 32 * 1024 * 1024,
            });
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await archive.ReadItemBytesAsync(
                    archive.Items[0],
                    cache,
                    cancellationToken: cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(
                Path.Combine(directory, "cache"),
                "*.tmp",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task RejectsUnsafeTableBeforeAllocation()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] invalid = new byte[36];
            "RP6L"u8.CopyTo(invalid);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                invalid.AsSpan(4),
                1);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                invalid.AsSpan(12),
                int.MaxValue);
            string path = Path.Combine(directory, "unsafe.rpack");
            await File.WriteAllBytesAsync(path, invalid);
            await Assert.ThrowsAsync<InvalidDataException>(
                () => Rp6lArchive.OpenAsync(path));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task OpensLargeLogicalChunkWithoutAllocatingItsPayload()
    {
        const uint logicalSize = 3_200_000_000;
        const int headerSize = 36;
        const int chunkRowSize = 20;
        const int itemRowSize = 16;
        int payloadOffset = headerSize + chunkRowSize + itemRowSize;
        byte[] archiveBytes = new byte[payloadOffset + 1];
        Span<byte> data = archiveBytes;
        "RP6L"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[12..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[16..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[24..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[28..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[32..], 0);

        int cursor = headerSize;
        BinaryPrimitives.WriteUInt16LittleEndian(data[cursor..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data[(cursor + 2)..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(cursor + 4)..],
            checked((uint)payloadOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(cursor + 8)..],
            logicalSize);
        BinaryPrimitives.WriteInt32LittleEndian(data[(cursor + 12)..], 1);
        cursor += chunkRowSize;

        data[cursor] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(cursor + 4)..],
            logicalSize - 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[(cursor + 8)..], 1);

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "large-logical.rpack");
            await File.WriteAllBytesAsync(path, archiveBytes);

            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);

            Rp6lChunkDescriptor chunk = Assert.Single(archive.Chunks);
            Assert.Equal(3_200_000_000L, chunk.LogicalSize);
            Assert.Equal(1L, chunk.StoredSize);
            Assert.Equal(3_199_999_999L, Assert.Single(archive.Items).Offset);
            Assert.Equal(archiveBytes.Length, archive.File.Length);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ResolvesZeroOffsetChunkAsBoundedTailPayload()
    {
        const int headerSize = 36;
        const int chunkRowSize = 20;
        const int itemRowSize = 16;
        const int chunkCount = 3;
        int tableEnd =
            headerSize + chunkCount * chunkRowSize + itemRowSize;
        byte[] archiveBytes = new byte[tableEnd + 8];
        Span<byte> data = archiveBytes;
        "RP6L"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[12..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[16..], chunkCount);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[24..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[28..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[32..], 0);

        int cursor = headerSize;
        WriteChunkRow(data[cursor..], 34, 8450, 0, 4);
        cursor += chunkRowSize;
        WriteChunkRow(
            data[cursor..],
            32,
            258,
            checked((uint)tableEnd),
            2);
        cursor += chunkRowSize;
        WriteChunkRow(
            data[cursor..],
            255,
            4,
            checked((uint)(tableEnd + 2)),
            2);
        cursor += chunkRowSize;

        data[cursor] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(
            data[(cursor + 4)..],
            checked((uint)(archiveBytes.Length - 4)));
        BinaryPrimitives.WriteInt32LittleEndian(data[(cursor + 8)..], 4);
        archiveBytes[^4] = 1;
        archiveBytes[^3] = 2;
        archiveBytes[^2] = 3;
        archiveBytes[^1] = 4;

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "implicit-tail.rpack");
            await File.WriteAllBytesAsync(path, archiveBytes);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            Assert.Equal(
                archiveBytes.Length - 4,
                archive.Chunks[0].Offset);

            await using Rp6lChunkCache cache = new(new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 1024 * 1024,
            });
            byte[] payload = await archive.ReadItemBytesAsync(
                Assert.Single(archive.Items),
                cache);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static void WriteChunkRow(
        Span<byte> row,
        ushort flags,
        ushort category,
        uint offset,
        uint logicalSize)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(row, flags);
        BinaryPrimitives.WriteUInt16LittleEndian(row[2..], category);
        BinaryPrimitives.WriteUInt32LittleEndian(row[4..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(row[8..], logicalSize);
        BinaryPrimitives.WriteInt32LittleEndian(row[12..], 0);
    }
}
