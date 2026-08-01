using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace ReAnimated.Codecs.Rp6l;

public sealed record Rp6lChunkCacheOptions
{
    public static Rp6lChunkCacheOptions Default { get; } = new();

    public string CacheDirectory { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLReAnimated",
        "Cache",
        "Rp6l");

    public long MaximumMemoryBytes { get; init; } = 384L * 1024 * 1024;

    public int MaximumMemoryEntryBytes { get; init; } = 64 * 1024 * 1024;

    public long MaximumDiskBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public int CopyBufferBytes { get; init; } = 256 * 1024;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CacheDirectory);
        if (MaximumMemoryBytes < 0 ||
            MaximumMemoryEntryBytes < 0 ||
            MaximumDiskBytes <= 0 ||
            CopyBufferBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Rp6lChunkCacheOptions),
                "RP6L cache limits are invalid.");
        }

        if (MaximumMemoryEntryBytes > MaximumMemoryBytes)
        {
            throw new ArgumentException(
                "The maximum memory entry cannot exceed the total memory cache.",
                nameof(Rp6lChunkCacheOptions));
        }
    }
}

public sealed class Rp6lChunkCache : IDisposable, IAsyncDisposable
{
    // Chrome's runtime initializes the raw LZMA decoder with these fixed
    // properties: lc/lp/pb = 3/0/2 and a 64 KiB dictionary.
    private static readonly byte[] Dl1LzmaProperties = [0x5D, 0x00, 0x00, 0x01, 0x00];
    private readonly Rp6lChunkCacheOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = [];
    private readonly Dictionary<string, CachedChunk> _entries =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private long _memoryBytes;
    private bool _disposed;

    public Rp6lChunkCache(Rp6lChunkCacheOptions? options = null)
    {
        _options = options ?? Rp6lChunkCacheOptions.Default;
        _options.Validate();
        Directory.CreateDirectory(_options.CacheDirectory);
    }

    public long MemoryBytes
    {
        get
        {
            lock (_sync)
            {
                return _memoryBytes;
            }
        }
    }

    public async ValueTask<Stream> OpenChunkAsync(
        Rp6lArchive archive,
        Rp6lChunkDescriptor chunk,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(chunk);
        if (!chunk.IsCompressed)
        {
            return archive.OpenStoredChunkStream(chunk);
        }

        if (chunk.Index < 0 ||
            chunk.Index >= archive.Chunks.Count ||
            !Equals(archive.Chunks[chunk.Index], chunk))
        {
            throw new ArgumentException(
                "The chunk does not belong to this archive.",
                nameof(chunk));
        }

        string key = CreateChunkKey(archive, chunk);
        Stream? cached = await TryOpenCachedAsync(
            key,
            chunk.LogicalSize,
            cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        SemaphoreSlim gate = _gates.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cached = await TryOpenCachedAsync(
                key,
                chunk.LogicalSize,
                cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            CachedChunk created = await MaterializeAsync(
                archive,
                chunk,
                key,
                cancellationToken).ConfigureAwait(false);
            AddEntry(key, created);
            TrimMemory();
            TrimDisk(created.DiskPath);
            return created.OpenRead();
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _entries.Clear();
            _memoryBytes = 0;
        }

        foreach (SemaphoreSlim gate in _gates.Values)
        {
            gate.Dispose();
        }

        _gates.Clear();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<Stream?> TryOpenCachedAsync(
        string key,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out CachedChunk? entry) &&
                entry.Length == expectedLength &&
                entry.IsAvailable)
            {
                entry.LastAccessUtc = DateTime.UtcNow;
                return entry.OpenRead();
            }

            if (entry is not null)
            {
                RemoveEntry(key, entry);
            }
        }

        string diskPath = GetDiskPath(key);
        FileInfo file = new(diskPath);
        if (file.Exists &&
            file.Length == expectedLength &&
            await VerifyDiskEntryAsync(
                diskPath,
                expectedLength,
                cancellationToken).ConfigureAwait(false))
        {
            try
            {
                file.LastAccessTimeUtc = DateTime.UtcNow;
            }
            catch (IOException)
            {
                // Access-time updates are advisory and unsupported on some volumes.
            }
            catch (UnauthorizedAccessException)
            {
                // A concurrently running app, inherited ACL, or read-only
                // cache file may allow safe reads while denying metadata
                // writes. Cache recency is advisory; do not reject a
                // verified chunk solely because its timestamp cannot be
                // touched.
            }

            CachedChunk disk = CachedChunk.FromDisk(diskPath, expectedLength);
            AddEntry(key, disk);
            return disk.OpenRead();
        }

        if (file.Exists)
        {
            TryDeleteCacheEntry(diskPath);
        }

        return null;
    }

    private async Task<CachedChunk> MaterializeAsync(
        Rp6lArchive archive,
        Rp6lChunkDescriptor chunk,
        string key,
        CancellationToken cancellationToken)
    {
        if (chunk.Compression == Rp6lCompression.Unknown)
        {
            throw new InvalidDataException(
                $"RP6L chunk {chunk.Index} is packed but the archive has no known compression flag.");
        }

        if (chunk.LogicalSize <= _options.MaximumMemoryEntryBytes)
        {
            byte[] bytes = GC.AllocateUninitializedArray<byte>(
                checked((int)chunk.LogicalSize));
            await using Stream destination = new MemoryStream(bytes, writable: true);
            await DecompressExactlyAsync(
                archive,
                chunk,
                destination,
                cancellationToken).ConfigureAwait(false);
            return CachedChunk.FromMemory(bytes);
        }

        Directory.CreateDirectory(_options.CacheDirectory);
        string finalPath = GetDiskPath(key);
        string temporaryPath = string.Concat(
            finalPath,
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        string finalHashPath = GetHashPath(finalPath);
        string temporaryHashPath = string.Concat(
            temporaryPath,
            ".sha256");
        try
        {
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                _options.CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await DecompressExactlyAsync(
                    archive,
                    chunk,
                    destination,
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] contentHash;
            await using (FileStream content = new(
                             temporaryPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             _options.CopyBufferBytes,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                contentHash = await SHA256.HashDataAsync(
                    content,
                    cancellationToken).ConfigureAwait(false);
            }

            await File.WriteAllTextAsync(
                temporaryHashPath,
                Convert.ToHexString(contentHash),
                Encoding.ASCII,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: true);
            File.Move(
                temporaryHashPath,
                finalHashPath,
                overwrite: true);
            return CachedChunk.FromDisk(finalPath, chunk.LogicalSize);
        }
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(temporaryHashPath);
            throw;
        }
    }

    private async Task DecompressExactlyAsync(
        Rp6lArchive archive,
        Rp6lChunkDescriptor chunk,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using Stream stored = archive.OpenStoredChunkStream(chunk);
        using Stream decoder = CreateDecoder(stored, chunk);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(_options.CopyBufferBytes);
        long remaining = chunk.LogicalSize;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = checked((int)Math.Min(buffer.Length, remaining));
            int read = await decoder.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"RP6L chunk {chunk.Index} ended before its declared logical size.");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        int extra = await decoder.ReadAsync(
            buffer.AsMemory(0, 1),
            cancellationToken).ConfigureAwait(false);
        if (extra != 0)
        {
            throw new InvalidDataException(
                $"RP6L chunk {chunk.Index} expands beyond its declared logical size.");
        }
    }

    private static Stream CreateDecoder(
        Stream stored,
        Rp6lChunkDescriptor chunk) =>
        chunk.Compression switch
        {
            Rp6lCompression.Zlib => new ZLibStream(
                stored,
                CompressionMode.Decompress,
                leaveOpen: true),
            Rp6lCompression.Lzma => LzmaStream.Create(
                Dl1LzmaProperties,
                stored,
                chunk.PackedSize,
                chunk.LogicalSize,
                leaveOpen: true),
            _ => throw new InvalidDataException(
                $"Unsupported RP6L compression {chunk.Compression}."),
        };

    private static string CreateChunkKey(
        Rp6lArchive archive,
        Rp6lChunkDescriptor chunk)
    {
        string value = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{archive.CacheIdentity}|{chunk.Index}|{chunk.Offset}|{chunk.PackedSize}|{chunk.LogicalSize}|{(int)chunk.Compression}");
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private string GetDiskPath(string key) =>
        System.IO.Path.Combine(
            _options.CacheDirectory,
            string.Concat(key, ".chunk"));

    private static string GetHashPath(string diskPath) =>
        string.Concat(diskPath, ".sha256");

    private async Task<bool> VerifyDiskEntryAsync(
        string diskPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        string hashPath = GetHashPath(diskPath);
        FileInfo hashFile = new(hashPath);
        if (!hashFile.Exists || hashFile.Length is < 64 or > 66)
        {
            return false;
        }

        try
        {
            string expectedHash = (
                await File.ReadAllTextAsync(
                    hashPath,
                    Encoding.ASCII,
                    cancellationToken).ConfigureAwait(false))
                .Trim();
            if (expectedHash.Length != 64)
            {
                return false;
            }

            byte[] actualHash;
            await using (FileStream content = new(
                             diskPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read | FileShare.Delete,
                             _options.CopyBufferBytes,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                if (content.Length != expectedLength)
                {
                    return false;
                }

                actualHash = await SHA256.HashDataAsync(
                    content,
                    cancellationToken).ConfigureAwait(false);
            }

            return string.Equals(
                expectedHash,
                Convert.ToHexString(actualHash),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void AddEntry(string key, CachedChunk entry)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out CachedChunk? existing))
            {
                RemoveEntry(key, existing);
            }

            _entries.Add(key, entry);
            if (entry.Memory is not null)
            {
                _memoryBytes += entry.Memory.LongLength;
            }
        }
    }

    private void TrimMemory()
    {
        lock (_sync)
        {
            foreach ((string key, CachedChunk entry) in _entries
                         .Where(static pair => pair.Value.Memory is not null)
                         .OrderBy(static pair => pair.Value.LastAccessUtc)
                         .ToArray())
            {
                if (_memoryBytes <= _options.MaximumMemoryBytes)
                {
                    break;
                }

                RemoveEntry(key, entry);
            }
        }
    }

    private void TrimDisk(string? protectedPath)
    {
        try
        {
            DirectoryInfo directory = new(_options.CacheDirectory);
            FileInfo[] files = directory
                .EnumerateFiles("*.chunk", SearchOption.TopDirectoryOnly)
                .OrderBy(static file => file.LastAccessTimeUtc)
                .ThenBy(static file => file.Name, StringComparer.Ordinal)
                .ToArray();
            long total = files.Sum(static file => file.Length);
            foreach (FileInfo file in files)
            {
                if (total <= _options.MaximumDiskBytes)
                {
                    break;
                }

                if (string.Equals(
                    file.FullName,
                    protectedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long length = file.Length;
                if (TryDelete(file.FullName))
                {
                    TryDelete(GetHashPath(file.FullName));
                    total -= length;
                    lock (_sync)
                    {
                        string? key = _entries
                            .FirstOrDefault(pair =>
                                string.Equals(
                                    pair.Value.DiskPath,
                                    file.FullName,
                                    StringComparison.OrdinalIgnoreCase))
                            .Key;
                        if (key is not null &&
                            _entries.TryGetValue(key, out CachedChunk? entry))
                        {
                            RemoveEntry(key, entry);
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // Cache cleanup is best-effort; materialized data remains valid.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache cleanup is best-effort; materialized data remains valid.
        }
    }

    private void RemoveEntry(string key, CachedChunk entry)
    {
        _entries.Remove(key);
        if (entry.Memory is not null)
        {
            _memoryBytes -= entry.Memory.LongLength;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteCacheEntry(string diskPath)
    {
        if (TryDelete(diskPath))
        {
            TryDelete(GetHashPath(diskPath));
        }
    }

    private sealed class CachedChunk
    {
        private CachedChunk(
            byte[]? memory,
            string? diskPath,
            long length,
            DateTime diskLastWriteTimeUtc)
        {
            Memory = memory;
            DiskPath = diskPath;
            Length = length;
            DiskLastWriteTimeUtc = diskLastWriteTimeUtc;
        }

        public byte[]? Memory { get; }

        public string? DiskPath { get; }

        public long Length { get; }

        public DateTime DiskLastWriteTimeUtc { get; }

        public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;

        public bool IsAvailable =>
            Memory is not null ||
            (DiskPath is not null &&
             new FileInfo(DiskPath) is { Exists: true } file &&
             file.Length == Length &&
             file.LastWriteTimeUtc == DiskLastWriteTimeUtc);

        public static CachedChunk FromMemory(byte[] memory) =>
            new(memory, null, memory.Length, default);

        public static CachedChunk FromDisk(string path, long length) =>
            new(
                null,
                path,
                length,
                new FileInfo(path).LastWriteTimeUtc);

        public Stream OpenRead()
        {
            LastAccessUtc = DateTime.UtcNow;
            if (Memory is not null)
            {
                return new MemoryStream(Memory, writable: false);
            }

            return new FileStream(
                DiskPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }
    }
}
