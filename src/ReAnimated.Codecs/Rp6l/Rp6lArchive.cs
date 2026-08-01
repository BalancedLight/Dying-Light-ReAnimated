using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Rp6l;

public sealed class Rp6lArchive
{
    private const int HeaderSize = 36;
    private const int ChunkRowSize = 20;
    private const int ItemRowSize = 16;
    private const int ResourceRowSize = 12;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private Rp6lArchive(
        string path,
        FileInfoSnapshot file,
        Rp6lLimits limits,
        Rp6lHeader header,
        IReadOnlyList<Rp6lChunkDescriptor> chunks,
        IReadOnlyList<Rp6lItemDescriptor> items,
        IReadOnlyList<Rp6lResourceDescriptor> resources,
        IReadOnlyList<string> names)
    {
        Path = path;
        File = file;
        Limits = limits;
        Header = header;
        Chunks = chunks;
        Items = items;
        Resources = resources;
        Names = names;
        CacheIdentity = CreateCacheIdentity(path, file, header, chunks);
    }

    public string Path { get; }

    public FileInfoSnapshot File { get; }

    public Rp6lLimits Limits { get; }

    public Rp6lHeader Header { get; }

    public IReadOnlyList<Rp6lChunkDescriptor> Chunks { get; }

    public IReadOnlyList<Rp6lItemDescriptor> Items { get; }

    public IReadOnlyList<Rp6lResourceDescriptor> Resources { get; }

    public IReadOnlyList<string> Names { get; }

    public string CacheIdentity { get; }

    public static async Task<Rp6lArchive> OpenAsync(
        string path,
        Rp6lLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        limits ??= Rp6lLimits.Default;
        limits.Validate();

        string fullPath = System.IO.Path.GetFullPath(path);
        FileInfo fileInfo = new(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("RP6L archive was not found.", fullPath);
        }

        FileInfoSnapshot snapshot = new(
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc);
        await using FileStream stream = OpenArchiveFile(fullPath);
        byte[] headerBytes = GC.AllocateUninitializedArray<byte>(HeaderSize);
        await stream.ReadExactlyAsync(
            headerBytes,
            cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> headerData = headerBytes;
        if (!headerData[..4].SequenceEqual("RP6L"u8))
        {
            throw new InvalidDataException(
                $"'{fullPath}' is not an RP6L archive.");
        }

        Rp6lHeader header = new(
            BinaryPrimitives.ReadInt32LittleEndian(headerData[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(headerData[8..]),
            ReadBoundedCount(headerData[12..], "item", limits.MaximumTableCount),
            ReadBoundedCount(headerData[16..], "chunk", limits.MaximumTableCount),
            ReadBoundedCount(headerData[20..], "resource", limits.MaximumTableCount),
            ReadBoundedCount(
                headerData[24..],
                "name blob byte",
                limits.MaximumNameBlobBytes),
            ReadBoundedCount(headerData[28..], "name", limits.MaximumTableCount),
            BinaryPrimitives.ReadInt32LittleEndian(headerData[32..]));
        if (header.Version != 1)
        {
            throw new InvalidDataException(
                $"RP6L version {header.Version} is not supported.");
        }

        if (header.ChunkCount > byte.MaxValue + 1)
        {
            throw new InvalidDataException(
                $"RP6L chunk count {header.ChunkCount} exceeds the byte-sized item index.");
        }

        long tableSize = checked(
            (long)header.ChunkCount * ChunkRowSize +
            (long)header.ItemCount * ItemRowSize +
            (long)header.ResourceCount * ResourceRowSize +
            (long)header.NameCount * sizeof(int) +
            header.NameBlobSize);
        if (tableSize > limits.MaximumTableBytes)
        {
            throw new InvalidDataException(
                $"RP6L tables require {tableSize:N0} bytes, above the configured limit.");
        }

        if (HeaderSize + tableSize > snapshot.Length)
        {
            throw new InvalidDataException(
                "RP6L tables extend beyond the archive.");
        }

        byte[] table = GC.AllocateUninitializedArray<byte>((int)tableSize);
        await stream.ReadExactlyAsync(
            table,
            cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> data = table;
        int cursor = 0;

        List<Rp6lChunkDescriptor> chunks = new(header.ChunkCount);
        for (int index = 0; index < header.ChunkCount; index++)
        {
            ReadOnlySpan<byte> row = data.Slice(cursor, ChunkRowSize);
            cursor += ChunkRowSize;
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            long logicalSize = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            int packedSize = BinaryPrimitives.ReadInt32LittleEndian(row[12..]);
            if (logicalSize > limits.MaximumLogicalChunkBytes ||
                packedSize < 0 ||
                packedSize > limits.MaximumStoredChunkBytes)
            {
                throw new InvalidDataException(
                    $"RP6L chunk {index} has unsafe declared sizes.");
            }

            long storedSize = packedSize > 0
                ? packedSize
                : logicalSize;
            if (offset > snapshot.Length ||
                storedSize > snapshot.Length - offset)
            {
                throw new InvalidDataException(
                    $"RP6L chunk {index} extends beyond the archive.");
            }

            Rp6lCompression compression = packedSize == 0
                ? Rp6lCompression.None
                : DecodeCompression(header.CompressionFlags);
            chunks.Add(new Rp6lChunkDescriptor(
                index,
                BinaryPrimitives.ReadUInt16LittleEndian(row),
                BinaryPrimitives.ReadUInt16LittleEndian(row[2..]),
                offset,
                logicalSize,
                packedSize,
                BinaryPrimitives.ReadUInt16LittleEndian(row[16..]),
                BinaryPrimitives.ReadUInt16LittleEndian(row[18..]),
                compression));
        }

        ResolveImplicitTailChunk(
            chunks,
            snapshot.Length,
            checked(HeaderSize + tableSize));
        ValidateChunkRanges(chunks);

        List<Rp6lItemDescriptor> items = new(header.ItemCount);
        for (int index = 0; index < header.ItemCount; index++)
        {
            ReadOnlySpan<byte> row = data.Slice(cursor, ItemRowSize);
            cursor += ItemRowSize;
            int chunkIndex = row[0];
            long serializedOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            int sizeOrHash = BinaryPrimitives.ReadInt32LittleEndian(row[8..]);
            if (chunkIndex >= chunks.Count)
            {
                throw new InvalidDataException(
                    $"RP6L item {index} references missing chunk {chunkIndex}.");
            }

            Rp6lChunkDescriptor chunk = chunks[chunkIndex];
            long offset = serializedOffset - chunk.ItemOffsetBias;
            if (offset < 0)
            {
                throw new InvalidDataException(
                    $"RP6L item {index} precedes logical chunk {chunkIndex}.");
            }

            if (sizeOrHash >= 0)
            {
                if (sizeOrHash > limits.MaximumItemBytes)
                {
                    throw new InvalidDataException(
                        $"RP6L item {index} exceeds the configured item limit.");
                }

                if (offset > chunk.LogicalSize ||
                    sizeOrHash > chunk.LogicalSize - offset)
                {
                    throw new InvalidDataException(
                        $"RP6L item {index} extends beyond logical chunk {chunkIndex}.");
                }
            }

            items.Add(new Rp6lItemDescriptor(
                index,
                chunkIndex,
                row[1],
                BinaryPrimitives.ReadInt16LittleEndian(row[2..]),
                offset,
                sizeOrHash,
                BinaryPrimitives.ReadInt32LittleEndian(row[12..])));
        }

        List<RawResource> rawResources = new(header.ResourceCount);
        for (int index = 0; index < header.ResourceCount; index++)
        {
            ReadOnlySpan<byte> row = data.Slice(cursor, ResourceRowSize);
            cursor += ResourceRowSize;
            short itemCount = BinaryPrimitives.ReadInt16LittleEndian(row);
            short resourceType = BinaryPrimitives.ReadInt16LittleEndian(row[2..]);
            int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(row[4..]);
            int firstItemIndex = BinaryPrimitives.ReadInt32LittleEndian(row[8..]);
            if (itemCount < 0 ||
                nameIndex < 0 ||
                nameIndex >= header.NameCount ||
                firstItemIndex < 0 ||
                firstItemIndex > header.ItemCount - itemCount)
            {
                throw new InvalidDataException(
                    $"RP6L resource {index} has invalid table indexes.");
            }

            rawResources.Add(new RawResource(
                itemCount,
                resourceType,
                nameIndex,
                firstItemIndex));
        }

        int[] nameOffsets = new int[header.NameCount];
        for (int index = 0; index < nameOffsets.Length; index++)
        {
            nameOffsets[index] =
                BinaryPrimitives.ReadInt32LittleEndian(data[cursor..]);
            cursor += sizeof(int);
        }

        ReadOnlySpan<byte> nameBlob = data.Slice(cursor, header.NameBlobSize);
        string[] names = new string[header.NameCount];
        for (int index = 0; index < names.Length; index++)
        {
            int offset = nameOffsets[index];
            if (offset < 0 || offset >= nameBlob.Length)
            {
                throw new InvalidDataException(
                    $"RP6L name {index} has an invalid offset.");
            }

            int terminator = nameBlob[offset..].IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException(
                    $"RP6L name {index} is not NUL terminated.");
            }

            try
            {
                names[index] = StrictUtf8.GetString(
                    nameBlob.Slice(offset, terminator));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"RP6L name {index} is not valid UTF-8.",
                    exception);
            }
        }

        long firstChunkOffset = chunks.Count == 0
            ? snapshot.Length
            : chunks.Min(static chunk => chunk.Offset);
        if (firstChunkOffset < HeaderSize + tableSize)
        {
            throw new InvalidDataException(
                "An RP6L chunk overlaps the archive tables.");
        }

        List<Rp6lResourceDescriptor> resources = new(rawResources.Count);
        for (int index = 0; index < rawResources.Count; index++)
        {
            RawResource raw = rawResources[index];
            Rp6lItemDescriptor[] ownedItems = items
                .Skip(raw.FirstItemIndex)
                .Take(raw.ItemCount)
                .ToArray();
            resources.Add(new Rp6lResourceDescriptor(
                index,
                names[raw.NameIndex],
                raw.ResourceType,
                raw.NameIndex,
                raw.FirstItemIndex,
                raw.ItemCount,
                ownedItems));
        }

        return new Rp6lArchive(
            fullPath,
            snapshot,
            limits,
            header,
            chunks,
            items,
            resources,
            names);
    }

    public Rp6lResourceDescriptor? FindResource(
        short resourceType,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Resources.FirstOrDefault(resource =>
            resource.ResourceType == resourceType &&
            string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask<Stream> OpenItemStreamAsync(
        Rp6lItemDescriptor item,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(cache);
        ValidateItemOwnership(item);
        if (!item.HasReadableSize)
        {
            throw new InvalidDataException(
                $"RP6L item {item.Index} stores a hash instead of a readable size.");
        }

        Rp6lChunkDescriptor chunk = Chunks[item.ChunkIndex];
        Stream logical = chunk.IsCompressed
            ? await cache.OpenChunkAsync(
                this,
                chunk,
                cancellationToken).ConfigureAwait(false)
            : OpenStoredChunkStream(chunk);
        try
        {
            return new BoundedReadStream(
                logical,
                item.Offset,
                item.SizeOrHash);
        }
        catch
        {
            await logical.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<Stream> OpenResourceStreamAsync(
        Rp6lResourceDescriptor resource,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(cache);
        ValidateResourceOwnership(resource);
        List<Stream> streams = new(resource.ItemCount);
        try
        {
            foreach (Rp6lItemDescriptor item in resource.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                streams.Add(await OpenItemStreamAsync(
                    item,
                    cache,
                    cancellationToken).ConfigureAwait(false));
            }

            return new ConcatenatedReadStream(streams);
        }
        catch
        {
            foreach (Stream stream in streams)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask<byte[]> ReadItemBytesAsync(
        Rp6lItemDescriptor item,
        Rp6lChunkCache cache,
        int? maximumBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        int limit = maximumBytes ?? Limits.MaximumItemBytes;
        if (!item.HasReadableSize || item.SizeOrHash > limit)
        {
            throw new InvalidDataException(
                $"RP6L item {item.Index} cannot be read within the {limit:N0}-byte limit.");
        }

        byte[] result = GC.AllocateUninitializedArray<byte>(item.SizeOrHash);
        await using Stream stream = await OpenItemStreamAsync(
            item,
            cache,
            cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(
            result,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal FileStream OpenStoredFile() => OpenArchiveFile(Path);

    internal Stream OpenStoredChunkStream(Rp6lChunkDescriptor chunk)
    {
        FileStream stream = OpenStoredFile();
        try
        {
            return new BoundedReadStream(
                stream,
                chunk.Offset,
                chunk.StoredSize);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenArchiveFile(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static int ReadBoundedCount(
        ReadOnlySpan<byte> source,
        string label,
        int maximum)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(source);
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException(
                $"RP6L {label} count {value} is unsafe.");
        }

        return value;
    }

    private static Rp6lCompression DecodeCompression(int flags)
    {
        if ((flags & 2) != 0)
        {
            return Rp6lCompression.Lzma;
        }

        if ((flags & 1) != 0)
        {
            return Rp6lCompression.Zlib;
        }

        return Rp6lCompression.Unknown;
    }

    private static void ValidateChunkRanges(
        IReadOnlyList<Rp6lChunkDescriptor> chunks)
    {
        Rp6lChunkDescriptor[] physical = chunks
            .OrderBy(static chunk => chunk.Offset)
            .ToArray();
        for (int index = 1; index < physical.Length; index++)
        {
            Rp6lChunkDescriptor previous = physical[index - 1];
            Rp6lChunkDescriptor current = physical[index];
            if (current.Offset < previous.Offset + previous.StoredSize)
            {
                throw new InvalidDataException(
                    $"RP6L chunks {previous.Index} and {current.Index} overlap.");
            }
        }
    }

    private static void ResolveImplicitTailChunk(
        List<Rp6lChunkDescriptor> chunks,
        long archiveLength,
        long tableEnd)
    {
        Rp6lChunkDescriptor[] implicitChunks = chunks
            .Where(static chunk => chunk.Offset == 0)
            .ToArray();
        if (implicitChunks.Length == 0)
        {
            return;
        }

        if (implicitChunks.Length != 1)
        {
            throw new InvalidDataException(
                "RP6L contains multiple zero-offset chunks whose physical ranges are ambiguous.");
        }

        Rp6lChunkDescriptor implicitChunk = implicitChunks[0];
        long inferredOffset = checked(
            archiveLength - implicitChunk.StoredSize);
        long explicitEnd = chunks
            .Where(chunk => chunk.Index != implicitChunk.Index)
            .Select(static chunk => checked(chunk.Offset + chunk.StoredSize))
            .DefaultIfEmpty(tableEnd)
            .Max();
        if (inferredOffset < tableEnd || inferredOffset < explicitEnd)
        {
            throw new InvalidDataException(
                $"RP6L chunk {implicitChunk.Index} has an unresolved zero stored offset.");
        }

        chunks[implicitChunk.Index] = implicitChunk with
        {
            Offset = inferredOffset,
            ItemOffsetBias = inferredOffset,
        };
    }

    private static string CreateCacheIdentity(
        string path,
        FileInfoSnapshot file,
        Rp6lHeader header,
        IReadOnlyList<Rp6lChunkDescriptor> chunks)
    {
        StringBuilder value = new();
        value.Append(path.ToUpperInvariant())
            .Append('|')
            .Append(file.Length)
            .Append('|')
            .Append(file.LastWriteTimeUtc.Ticks)
            .Append('|')
            .Append(header.CompressionFlags);
        foreach (Rp6lChunkDescriptor chunk in chunks)
        {
            value.Append('|')
                .Append(chunk.Offset)
                .Append(':')
                .Append(chunk.LogicalSize)
                .Append(':')
                .Append(chunk.PackedSize);
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private void ValidateItemOwnership(Rp6lItemDescriptor item)
    {
        if (item.Index < 0 ||
            item.Index >= Items.Count ||
            !Equals(Items[item.Index], item))
        {
            throw new ArgumentException(
                "The item does not belong to this archive.",
                nameof(item));
        }
    }

    private void ValidateResourceOwnership(Rp6lResourceDescriptor resource)
    {
        if (resource.Index < 0 ||
            resource.Index >= Resources.Count ||
            !Equals(Resources[resource.Index], resource))
        {
            throw new ArgumentException(
                "The resource does not belong to this archive.",
                nameof(resource));
        }
    }

    private sealed record RawResource(
        short ItemCount,
        short ResourceType,
        int NameIndex,
        int FirstItemIndex);
}

public sealed record FileInfoSnapshot(long Length, DateTime LastWriteTimeUtc);
