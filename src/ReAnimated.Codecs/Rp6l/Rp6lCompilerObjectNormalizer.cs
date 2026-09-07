using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace ReAnimated.Codecs.Rp6l;

public sealed record Rp6lCompilerObjectNormalizationResult(
    string OutputPath,
    int ConvertedResourceCount,
    ImmutableArray<string> ResourceNames);

/// <summary>
/// Links Techland compiler <c>*_obj</c> RP6L units into a normal standalone
/// RP6L container. Compiled chunk payloads remain opaque and byte-identical;
/// only compiler-object addressing, table indexes, compiler-only resource
/// types, and compiler-owned item load-suppression flags are normalized.
/// </summary>
public static class Rp6lCompilerObjectNormalizer
{
    private const int HeaderSize = 36;
    private const int ChunkRowSize = 20;
    private const int ItemRowSize = 16;
    private const int ResourceRowSize = 12;
    private const ushort CompilerObjectTypeBit = 0x8000;
    private const byte SuppressItemLoadFlag = 0x01;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Task<Rp6lCompilerObjectNormalizationResult> NormalizeAtomicAsync(
        string compilerObjectPath,
        string outputPath,
        Rp6lLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerObjectPath);
        return LinkCoreAtomicAsync(
            [compilerObjectPath],
            outputPath,
            requireSingleCompilerObject: true,
            limits,
            cancellationToken);
    }

    /// <summary>
    /// Links one compiler-addressed mesh object and any number of ordinary
    /// single-resource RP6L objects (for example the Developer Tools DDS
    /// outputs) without decoding or rewriting their payloads.
    /// </summary>
    public static Task<Rp6lCompilerObjectNormalizationResult> LinkAtomicAsync(
        IEnumerable<string> objectPaths,
        string outputPath,
        Rp6lLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectPaths);
        return LinkCoreAtomicAsync(
            objectPaths,
            outputPath,
            requireSingleCompilerObject: false,
            limits,
            cancellationToken);
    }

    private static async Task<Rp6lCompilerObjectNormalizationResult> LinkCoreAtomicAsync(
        IEnumerable<string> objectPaths,
        string outputPath,
        bool requireSingleCompilerObject,
        Rp6lLimits? limits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        limits ??= Rp6lLimits.Default;
        limits.Validate();

        string[] inputs = objectPaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                return Path.GetFullPath(path);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputs.Length == 0)
        {
            throw new ArgumentException("At least one RP6L object is required.", nameof(objectPaths));
        }

        if (requireSingleCompilerObject && inputs.Length != 1)
        {
            throw new ArgumentException(
                "Compiler-object normalization accepts exactly one input.",
                nameof(objectPaths));
        }

        foreach (string input in inputs)
        {
            if (!File.Exists(input))
            {
                throw new FileNotFoundException("A Techland compiler object was not found.", input);
            }
        }

        string destinationPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new InvalidOperationException("The linked RP6L output has no parent directory.");
        }

        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = destinationPath + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            var units = new List<ParsedCompilerObject>(inputs.Length);
            foreach (string input in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                units.Add(await ParseAsync(input, limits, cancellationToken).ConfigureAwait(false));
            }

            if (requireSingleCompilerObject &&
                (!units[0].HasCompilerAddressing || units[0].ConvertedResourceCount == 0))
            {
                throw new InvalidDataException(
                    "The RP6L input is not a Techland compiler-addressed resource object.");
            }

            if (!requireSingleCompilerObject && units.Sum(static unit => unit.ConvertedResourceCount) == 0)
            {
                throw new InvalidDataException(
                    "The RP6L inputs contain no compiler resource type to link.");
            }

            ParsedCompilerObject linked = Merge(units, limits);
            await WriteNormalizedAsync(
                temporaryPath,
                linked,
                cancellationToken).ConfigureAwait(false);

            // Re-open the complete temporary file through the ordinary bounded
            // reader before it can replace an existing output.
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(
                temporaryPath,
                limits,
                cancellationToken).ConfigureAwait(false);
            if (archive.Resources.Count != linked.Resources.Length)
            {
                throw new InvalidDataException(
                    "The linked RP6L resource table did not round-trip.");
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new Rp6lCompilerObjectNormalizationResult(
                destinationPath,
                linked.ConvertedResourceCount,
                archive.Resources.Select(static resource => resource.Name).ToImmutableArray());
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<ParsedCompilerObject> ParseAsync(
        string inputPath,
        Rp6lLimits limits,
        CancellationToken cancellationToken)
    {
        await using FileStream input = new(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long inputLength = input.Length;
        if (inputLength < HeaderSize)
        {
            throw new InvalidDataException("The RP6L object is smaller than its header.");
        }

        byte[] header = GC.AllocateUninitializedArray<byte>(HeaderSize);
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> headerData = header;
        if (!headerData[..4].SequenceEqual("RP6L"u8))
        {
            throw new InvalidDataException("The compiler output does not contain RP6L magic.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(headerData[4..]);
        int compressionFlags = BinaryPrimitives.ReadInt32LittleEndian(headerData[8..]);
        int itemCount = ReadBoundedCount(headerData[12..], "item", limits.MaximumTableCount);
        int chunkCount = ReadBoundedCount(headerData[16..], "chunk", limits.MaximumTableCount);
        int resourceCount = ReadBoundedCount(headerData[20..], "resource", limits.MaximumTableCount);
        int nameBlobSize = ReadBoundedCount(
            headerData[24..],
            "name blob byte",
            limits.MaximumNameBlobBytes);
        int nameCount = ReadBoundedCount(headerData[28..], "name", limits.MaximumTableCount);
        int headerUnknown = BinaryPrimitives.ReadInt32LittleEndian(headerData[32..]);
        if (version != 1)
        {
            throw new InvalidDataException($"Compiler-output RP6L version {version} is not supported.");
        }

        if (chunkCount > byte.MaxValue + 1)
        {
            throw new InvalidDataException(
                $"Compiler output has {chunkCount} chunks, above the byte-sized item index limit.");
        }

        long tableBytes = checked(
            (long)chunkCount * ChunkRowSize +
            (long)itemCount * ItemRowSize +
            (long)resourceCount * ResourceRowSize +
            (long)nameCount * sizeof(int) +
            nameBlobSize);
        if (tableBytes > limits.MaximumTableBytes)
        {
            throw new InvalidDataException(
                $"Compiler-output tables require {tableBytes:N0} bytes, above the configured limit.");
        }

        long tableEnd = checked(HeaderSize + tableBytes);
        if (tableEnd > inputLength)
        {
            throw new InvalidDataException("Compiler-output tables extend beyond the file.");
        }

        byte[] table = GC.AllocateUninitializedArray<byte>(checked((int)tableBytes));
        await input.ReadExactlyAsync(table, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> data = table;
        int cursor = 0;

        var chunks = new CompilerChunk[chunkCount];
        bool hasCompilerAddressing = false;
        for (int index = 0; index < chunks.Length; index++)
        {
            ReadOnlySpan<byte> row = data.Slice(cursor, ChunkRowSize);
            cursor += ChunkRowSize;
            long declaredOffset = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            long logicalSize = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            int packedSize = BinaryPrimitives.ReadInt32LittleEndian(row[12..]);
            if (logicalSize > limits.MaximumLogicalChunkBytes ||
                packedSize < 0 ||
                packedSize > limits.MaximumStoredChunkBytes)
            {
                throw new InvalidDataException(
                    $"Compiler-output chunk {index} has unsafe declared sizes.");
            }

            hasCompilerAddressing |= declaredOffset == 0;
            chunks[index] = new CompilerChunk(
                BinaryPrimitives.ReadUInt16LittleEndian(row),
                BinaryPrimitives.ReadUInt16LittleEndian(row[2..]),
                declaredOffset,
                logicalSize,
                packedSize,
                BinaryPrimitives.ReadUInt16LittleEndian(row[16..]),
                BinaryPrimitives.ReadUInt16LittleEndian(row[18..]),
                inputPath,
                0);
        }

        var items = new CompilerItem[itemCount];
        for (int index = 0; index < items.Length; index++)
        {
            ReadOnlySpan<byte> row = data.Slice(cursor, ItemRowSize);
            cursor += ItemRowSize;
            int chunkIndex = row[0];
            if (chunkIndex >= chunkCount)
            {
                throw new InvalidDataException(
                    $"Compiler-output item {index} references missing chunk {chunkIndex}.");
            }

            int sizeOrHash = BinaryPrimitives.ReadInt32LittleEndian(row[8..]);
            if (sizeOrHash > limits.MaximumItemBytes)
            {
                throw new InvalidDataException(
                    $"Compiler-output item {index} exceeds the configured item limit.");
            }

            items[index] = new CompilerItem(
                chunkIndex,
                row[1],
                BinaryPrimitives.ReadInt16LittleEndian(row[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[4..]),
                sizeOrHash,
                BinaryPrimitives.ReadInt32LittleEndian(row[12..]));
        }

        var resources = new CompilerResource[resourceCount];
        for (int index = 0; index < resources.Length; index++)
        {
            ReadOnlySpan<byte> row = data.Slice(cursor, ResourceRowSize);
            cursor += ResourceRowSize;
            short itemCountForResource = BinaryPrimitives.ReadInt16LittleEndian(row);
            short resourceType = BinaryPrimitives.ReadInt16LittleEndian(row[2..]);
            int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(row[4..]);
            int firstItemIndex = BinaryPrimitives.ReadInt32LittleEndian(row[8..]);
            if (itemCountForResource < 0 ||
                nameIndex < 0 ||
                nameIndex >= nameCount ||
                firstItemIndex < 0 ||
                firstItemIndex > itemCount - itemCountForResource)
            {
                throw new InvalidDataException(
                    $"Compiler-output resource {index} has invalid table indexes.");
            }

            resources[index] = new CompilerResource(
                itemCountForResource,
                resourceType,
                nameIndex,
                firstItemIndex);
        }

        var nameOffsets = new int[nameCount];
        for (int index = 0; index < nameOffsets.Length; index++)
        {
            nameOffsets[index] = BinaryPrimitives.ReadInt32LittleEndian(data[cursor..]);
            cursor += sizeof(int);
        }

        byte[] nameBlob = data.Slice(cursor, nameBlobSize).ToArray();
        var names = new string[nameCount];
        for (int index = 0; index < names.Length; index++)
        {
            int offset = nameOffsets[index];
            if (offset < 0 || offset >= nameBlob.Length)
            {
                throw new InvalidDataException(
                    $"Compiler-output name {index} has invalid offset {offset}.");
            }

            int terminator = nameBlob.AsSpan(offset).IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException(
                    $"Compiler-output name {index} is not NUL terminated.");
            }

            try
            {
                names[index] = StrictUtf8.GetString(nameBlob.AsSpan(offset, terminator));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    $"Compiler-output name {index} is not valid UTF-8.",
                    exception);
            }
        }

        for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
        {
            CompilerChunk chunk = chunks[chunkIndex];
            long storedSize = chunk.StoredSize;
            int[] references = items
                .Select((item, index) => (item, index))
                .Where(pair => pair.item.ChunkIndex == chunkIndex)
                .Select(pair => pair.index)
                .ToArray();
            long sourceOffset = chunk.DeclaredOffset;
            if (sourceOffset == 0)
            {
                if (references.Length == 0)
                {
                    throw new InvalidDataException(
                        $"Zero-offset compiler-output chunk {chunkIndex} has no referencing item.");
                }

                sourceOffset = references.Min(index => items[index].Offset);
                foreach (int itemIndex in references)
                {
                    CompilerItem item = items[itemIndex];
                    long relativeOffset = item.Offset - sourceOffset;
                    ValidateItemRange(itemIndex, chunkIndex, relativeOffset, item.SizeOrHash, chunk.LogicalSize);
                    items[itemIndex] = item with { Offset = relativeOffset };
                }
            }
            else
            {
                foreach (int itemIndex in references)
                {
                    CompilerItem item = items[itemIndex];
                    long normalizedOffset = item.Offset;
                    if (item.SizeOrHash >= 0 &&
                        item.Offset >= sourceOffset &&
                        item.Offset + item.SizeOrHash <= sourceOffset + chunk.LogicalSize)
                    {
                        normalizedOffset -= sourceOffset;
                    }

                    ValidateItemRange(
                        itemIndex,
                        chunkIndex,
                        normalizedOffset,
                        item.SizeOrHash,
                        chunk.LogicalSize);
                    items[itemIndex] = item with { Offset = normalizedOffset };
                }
            }

            if (sourceOffset < tableEnd ||
                sourceOffset > inputLength ||
                storedSize > inputLength - sourceOffset)
            {
                throw new InvalidDataException(
                    $"Compiler-output chunk {chunkIndex} payload extends outside the file.");
            }

            chunks[chunkIndex] = chunk with { SourceOffset = sourceOffset };
        }

        int convertedResourceCount = 0;
        for (int index = 0; index < resources.Length; index++)
        {
            CompilerResource resource = resources[index];
            ushort rawType = unchecked((ushort)resource.ResourceType);
            if (resource.ResourceType != Rp6lResourceTypes.BuilderInformation &&
                (rawType & CompilerObjectTypeBit) != 0)
            {
                resources[index] = resource with
                {
                    ResourceType = unchecked((short)(rawType & ~CompilerObjectTypeBit)),
                };
                // The runtime per-resource task builder skips item flag bit 0.
                // Compiler work units set it because the final linker chooses
                // which resources to publish. Clear it only for items owned by
                // a converted compiler resource; ordinary linked units retain
                // their scheduling flags and every chunk keeps its load mode,
                // allocation, alignment, and callback settings.
                for (int itemIndex = resource.FirstItemIndex;
                     itemIndex < resource.FirstItemIndex + resource.ItemCount;
                     itemIndex++)
                {
                    CompilerItem item = items[itemIndex];
                    items[itemIndex] = item with { Flags = (byte)(item.Flags & ~SuppressItemLoadFlag) };
                }
                convertedResourceCount++;
            }
        }

        long outputLength = tableEnd;
        foreach (CompilerChunk chunk in chunks)
        {
            outputLength = checked(outputLength + chunk.StoredSize);
        }

        if (outputLength > uint.MaxValue)
        {
            throw new InvalidDataException("The normalized RP6L exceeds its 32-bit container limit.");
        }

        return new ParsedCompilerObject(
            version,
            compressionFlags,
            headerUnknown,
            chunks,
            items,
            resources,
            nameOffsets,
            nameBlob,
            names,
            convertedResourceCount,
            hasCompilerAddressing,
            tableEnd,
            outputLength);
    }

    private static ParsedCompilerObject Merge(
        IReadOnlyList<ParsedCompilerObject> units,
        Rp6lLimits limits)
    {
        ParsedCompilerObject first = units[0];
        if (units.Any(unit =>
                unit.Version != first.Version ||
                unit.CompressionFlags != first.CompressionFlags ||
                unit.HeaderUnknown != first.HeaderUnknown))
        {
            throw new InvalidDataException(
                "The compiler outputs use incompatible RP6L header profiles and cannot be linked.");
        }

        int totalChunks = checked(units.Sum(static unit => unit.Chunks.Length));
        int totalItems = checked(units.Sum(static unit => unit.Items.Length));
        int totalNames = checked(units.Sum(static unit => unit.NameOffsets.Length));
        int totalNameBytes = checked(units.Sum(static unit => unit.NameBlob.Length));
        if (totalChunks > byte.MaxValue + 1 ||
            totalChunks > limits.MaximumTableCount ||
            totalItems > limits.MaximumTableCount ||
            totalNames > limits.MaximumTableCount ||
            totalNameBytes > limits.MaximumNameBlobBytes)
        {
            throw new InvalidDataException(
                "The linked RP6L tables exceed their configured or format limits.");
        }

        var chunks = new List<CompilerChunk>(totalChunks);
        var items = new List<CompilerItem>(totalItems);
        var resources = new List<CompilerResource>();
        var nameOffsets = new List<int>(totalNames);
        var names = new List<string>(totalNames);
        using var nameBlob = new MemoryStream(totalNameBytes);
        var builderResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int convertedResourceCount = 0;
        bool hasCompilerAddressing = false;

        foreach (ParsedCompilerObject unit in units)
        {
            int chunkBase = chunks.Count;
            int itemBase = items.Count;
            int nameBase = names.Count;
            int nameByteBase = checked((int)nameBlob.Length);

            chunks.AddRange(unit.Chunks);
            items.AddRange(unit.Items.Select(item => item with
            {
                ChunkIndex = checked(item.ChunkIndex + chunkBase),
            }));
            nameOffsets.AddRange(unit.NameOffsets.Select(offset => checked(offset + nameByteBase)));
            names.AddRange(unit.Names);
            nameBlob.Write(unit.NameBlob);

            foreach (CompilerResource resource in unit.Resources)
            {
                string name = unit.Names[resource.NameIndex];
                if (resource.ResourceType == Rp6lResourceTypes.BuilderInformation &&
                    !builderResources.Add(name))
                {
                    continue;
                }

                resources.Add(resource with
                {
                    NameIndex = checked(resource.NameIndex + nameBase),
                    FirstItemIndex = checked(resource.FirstItemIndex + itemBase),
                });
            }

            convertedResourceCount = checked(convertedResourceCount + unit.ConvertedResourceCount);
            hasCompilerAddressing |= unit.HasCompilerAddressing;
        }

        if (resources.Count > limits.MaximumTableCount)
        {
            throw new InvalidDataException("The linked RP6L resource table exceeds the configured limit.");
        }

        long tableBytes = checked(
            (long)chunks.Count * ChunkRowSize +
            (long)items.Count * ItemRowSize +
            (long)resources.Count * ResourceRowSize +
            (long)nameOffsets.Count * sizeof(int) +
            nameBlob.Length);
        if (tableBytes > limits.MaximumTableBytes)
        {
            throw new InvalidDataException(
                $"Linked RP6L tables require {tableBytes:N0} bytes, above the configured limit.");
        }

        long tableEnd = checked(HeaderSize + tableBytes);
        long outputLength = tableEnd;
        foreach (CompilerChunk chunk in chunks)
        {
            outputLength = checked(outputLength + chunk.StoredSize);
        }

        if (outputLength > uint.MaxValue)
        {
            throw new InvalidDataException("The linked RP6L exceeds its 32-bit container limit.");
        }

        return new ParsedCompilerObject(
            first.Version,
            first.CompressionFlags,
            first.HeaderUnknown,
            chunks.ToArray(),
            items.ToArray(),
            resources.ToArray(),
            nameOffsets.ToArray(),
            nameBlob.ToArray(),
            names.ToArray(),
            convertedResourceCount,
            hasCompilerAddressing,
            tableEnd,
            outputLength);
    }

    private static async Task WriteNormalizedAsync(
        string outputPath,
        ParsedCompilerObject parsed,
        CancellationToken cancellationToken)
    {
        var outputOffsets = new long[parsed.Chunks.Length];
        long cursor = parsed.TableEnd;
        for (int index = 0; index < parsed.Chunks.Length; index++)
        {
            outputOffsets[index] = cursor;
            cursor = checked(cursor + parsed.Chunks[index].StoredSize);
        }

        using var tables = new MemoryStream(checked((int)parsed.TableEnd));
        tables.Write("RP6L"u8);
        WriteInt32(tables, parsed.Version);
        WriteInt32(tables, parsed.CompressionFlags);
        WriteInt32(tables, parsed.Items.Length);
        WriteInt32(tables, parsed.Chunks.Length);
        WriteInt32(tables, parsed.Resources.Length);
        WriteInt32(tables, parsed.NameBlob.Length);
        WriteInt32(tables, parsed.NameOffsets.Length);
        WriteInt32(tables, parsed.HeaderUnknown);
        for (int index = 0; index < parsed.Chunks.Length; index++)
        {
            CompilerChunk chunk = parsed.Chunks[index];
            WriteUInt16(tables, chunk.Flags);
            WriteUInt16(tables, chunk.Category);
            WriteUInt32(tables, checked((uint)outputOffsets[index]));
            WriteUInt32(tables, checked((uint)chunk.LogicalSize));
            WriteInt32(tables, chunk.PackedSize);
            WriteUInt16(tables, chunk.Unknown0);
            WriteUInt16(tables, chunk.Unknown1);
        }

        foreach (CompilerItem item in parsed.Items)
        {
            tables.WriteByte(checked((byte)item.ChunkIndex));
            tables.WriteByte(item.Flags);
            WriteInt16(tables, item.StorageGroupId);
            WriteUInt32(tables, checked((uint)item.Offset));
            WriteInt32(tables, item.SizeOrHash);
            WriteInt32(tables, item.Unknown);
        }

        foreach (CompilerResource resource in parsed.Resources)
        {
            WriteInt16(tables, resource.ItemCount);
            WriteInt16(tables, resource.ResourceType);
            WriteInt32(tables, resource.NameIndex);
            WriteInt32(tables, resource.FirstItemIndex);
        }

        foreach (int offset in parsed.NameOffsets)
        {
            WriteInt32(tables, offset);
        }

        tables.Write(parsed.NameBlob);
        if (tables.Length != parsed.TableEnd)
        {
            throw new InvalidDataException("The linked RP6L table size changed unexpectedly.");
        }

        tables.Position = 0;
        await using FileStream output = new(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await tables.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        foreach (CompilerChunk chunk in parsed.Chunks)
        {
            await using FileStream input = new(
                chunk.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            await CopyRangeAsync(
                input,
                output,
                chunk.SourceOffset,
                chunk.StoredSize,
                cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (output.Length != parsed.OutputLength)
        {
            throw new InvalidDataException(
                "The linked RP6L output length is inconsistent with its descriptors.");
        }
    }

    private static async Task CopyRangeAsync(
        FileStream source,
        FileStream destination,
        long offset,
        long length,
        CancellationToken cancellationToken)
    {
        source.Position = offset;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "A compiler-output chunk ended before its declared size.");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateItemRange(
        int itemIndex,
        int chunkIndex,
        long offset,
        int sizeOrHash,
        long logicalSize)
    {
        if (offset < 0 || offset > uint.MaxValue || offset > logicalSize)
        {
            throw new InvalidDataException(
                $"Compiler-output item {itemIndex} has an invalid offset in chunk {chunkIndex}.");
        }

        if (sizeOrHash >= 0 && sizeOrHash > logicalSize - offset)
        {
            throw new InvalidDataException(
                $"Compiler-output item {itemIndex} extends beyond chunk {chunkIndex}.");
        }
    }

    private static int ReadBoundedCount(ReadOnlySpan<byte> data, string label, int maximum)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException(
                $"Compiler-output {label} count {value:N0} is outside the configured limit.");
        }

        return value;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record CompilerChunk(
        ushort Flags,
        ushort Category,
        long DeclaredOffset,
        long LogicalSize,
        int PackedSize,
        ushort Unknown0,
        ushort Unknown1,
        string SourcePath,
        long SourceOffset)
    {
        public long StoredSize => PackedSize > 0 ? PackedSize : LogicalSize;
    }

    private sealed record CompilerItem(
        int ChunkIndex,
        byte Flags,
        short StorageGroupId,
        long Offset,
        int SizeOrHash,
        int Unknown);

    private sealed record CompilerResource(
        short ItemCount,
        short ResourceType,
        int NameIndex,
        int FirstItemIndex);

    private sealed record ParsedCompilerObject(
        int Version,
        int CompressionFlags,
        int HeaderUnknown,
        CompilerChunk[] Chunks,
        CompilerItem[] Items,
        CompilerResource[] Resources,
        int[] NameOffsets,
        byte[] NameBlob,
        string[] Names,
        int ConvertedResourceCount,
        bool HasCompilerAddressing,
        long TableEnd,
        long OutputLength);
}
