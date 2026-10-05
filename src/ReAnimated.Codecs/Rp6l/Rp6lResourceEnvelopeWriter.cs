using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Rp6l;

public sealed record Rp6lResourceItemPayload(
    Rp6lItemDescriptor Descriptor,
    ImmutableArray<byte> Payload,
    string ContentSha256);

public sealed record Rp6lResourceEnvelopeReadback(
    Rp6lHeader Header,
    Rp6lResourceDescriptor Resource,
    ImmutableArray<Rp6lResourceItemPayload> Items,
    ImmutableArray<Rp6lChunkDescriptor> Chunks,
    string ContentSha256,
    long ArchiveByteLength);

public sealed record Rp6lResourceEnvelopeExportResult(
    string Path,
    string ArchiveSha256,
    Rp6lResourceEnvelopeReadback Readback);

public static class Rp6lResourceEnvelopeWriter
{
    private const int MaximumResourceBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Rp6lLimits ReadbackLimits = new()
    {
        MaximumTableCount = 256,
        MaximumNameBlobBytes = 4097,
        MaximumTableBytes = 1024 * 1024,
        MaximumLogicalChunkBytes = MaximumResourceBytes,
        MaximumStoredChunkBytes = MaximumResourceBytes,
        MaximumItemBytes = MaximumResourceBytes,
    };

    public static byte[] Build(
        Rp6lHeader header,
        Rp6lResourceDescriptor resource,
        IReadOnlyList<Rp6lResourceItemPayload> items,
        IReadOnlyList<Rp6lChunkDescriptor> chunks,
        CancellationToken cancellationToken = default)
    {
        ImmutableArray<Rp6lResourceItemPayload> snapshot = ValidateInputs(
            header, resource, items, chunks, cancellationToken);
        return BuildContainer(header, resource, snapshot, chunks, cancellationToken);
    }

    public static async Task<Rp6lResourceEnvelopeExportResult> WriteNewAsync(
        string path,
        Rp6lHeader header,
        Rp6lResourceDescriptor resource,
        IReadOnlyList<Rp6lResourceItemPayload> items,
        IReadOnlyList<Rp6lChunkDescriptor> chunks,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = System.IO.Path.GetFullPath(path);
        string directory = System.IO.Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The resource output has no parent directory.");
        if (File.Exists(destination))
            throw new IOException("The resource output already exists.");
        ImmutableArray<Rp6lResourceItemPayload> expected = ValidateInputs(
            header, resource, items, chunks, cancellationToken);
        byte[] bytes = BuildContainer(header, resource, expected, chunks, cancellationToken);
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory,
            $".{System.IO.Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream output = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            Rp6lResourceEnvelopeReadback readback = await ReadBackAsync(
                temporary, cache, cancellationToken).ConfigureAwait(false);
            VerifyReadback(header, resource, expected, chunks, readback);
            if (readback.ArchiveByteLength != bytes.Length)
                throw new InvalidDataException("The resource envelope length changed during readback.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return new(destination, digest, readback);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async Task<Rp6lResourceEnvelopeReadback> ReadBackAsync(
        string path, Rp6lChunkCache cache, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream guard = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(path, ReadbackLimits, cancellationToken)
            .ConfigureAwait(false);
        Rp6lHeader header = archive.Header;
        if (header.Version != 1 || header.CompressionFlags != 0 ||
            header.ResourceCount != 1 || header.NameCount != 1 ||
            header.ItemCount == 0 || header.ChunkCount != header.ItemCount)
            throw new InvalidDataException("The resource envelope has unexpected table counts.");
        Rp6lResourceDescriptor resource = archive.Resources[0];
        if (!IsSupportedResourceType(resource.ResourceType) || resource.Index != 0 ||
            resource.NameIndex != 0 || resource.FirstItemIndex != 0 ||
            resource.ItemCount != archive.Items.Count ||
            header.NameBlobSize != EncodeName(resource.Name).Length + 1)
            throw new InvalidDataException("The resource envelope has unexpected resource routing.");
        long cursor = checked(36L + header.ChunkCount * 20L +
            header.ItemCount * 16L + 12 + 4 + header.NameBlobSize);
        long total = 0;
        var items = ImmutableArray.CreateBuilder<Rp6lResourceItemPayload>(archive.Items.Count);
        using IncrementalHash content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int index = 0; index < archive.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lItemDescriptor item = archive.Items[index];
            Rp6lChunkDescriptor chunk = archive.Chunks[index];
            if (item.Index != index || item.ChunkIndex != index || item.Offset != 0 ||
                !item.HasReadableSize || chunk.Index != index || chunk.Offset != cursor ||
                chunk.LogicalSize != item.SizeOrHash || chunk.PackedSize != 0 ||
                chunk.Compression != Rp6lCompression.None || chunk.ItemOffsetBias != 0)
                throw new InvalidDataException("The resource envelope has unexpected item routing.");
            total = checked(total + item.SizeOrHash);
            if (total > MaximumResourceBytes)
                throw new InvalidDataException("The resource envelope exceeds its byte limit.");
            byte[] payload = await archive.ReadItemBytesAsync(
                item, cache, MaximumResourceBytes, cancellationToken).ConfigureAwait(false);
            content.AppendData(payload);
            items.Add(new(item, ImmutableArray.CreateRange(payload),
                Convert.ToHexStringLower(SHA256.HashData(payload))));
            cursor = checked(cursor + payload.Length);
        }
        if (cursor != archive.File.Length || guard.Length != archive.File.Length)
            throw new InvalidDataException("The resource envelope has unexpected trailing bytes.");
        return new(header, resource with { Items = resource.Items.ToImmutableArray() },
            items.MoveToImmutable(), archive.Chunks.ToImmutableArray(),
            Convert.ToHexStringLower(content.GetHashAndReset()), archive.File.Length);
    }

    private static ImmutableArray<Rp6lResourceItemPayload> ValidateInputs(
        Rp6lHeader header,
        Rp6lResourceDescriptor resource,
        IReadOnlyList<Rp6lResourceItemPayload> items,
        IReadOnlyList<Rp6lChunkDescriptor> chunks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(resource.Items);
        cancellationToken.ThrowIfCancellationRequested();
        if (header.Version != 1 || !IsSupportedResourceType(resource.ResourceType))
            throw new InvalidDataException("Only normal version-1 mesh and texture resources are supported.");
        _ = EncodeName(resource.Name);
        if (items.Count == 0 || items.Count > 256 || items.Count != resource.ItemCount ||
            resource.Items.Count != items.Count)
            throw new InvalidDataException("The complete resource item list is required.");
        if (chunks.Count == 0 || chunks.Count > 256)
            throw new InvalidDataException("The source chunk descriptor count is invalid.");
        Dictionary<int, Rp6lChunkDescriptor> lookup = [];
        foreach (Rp6lChunkDescriptor chunk in chunks)
        {
            if (chunk is null || chunk.Index < 0 || !lookup.TryAdd(chunk.Index, chunk))
                throw new InvalidDataException("The source chunk descriptors are missing or duplicated.");
        }
        var snapshot = ImmutableArray.CreateBuilder<Rp6lResourceItemPayload>(items.Count);
        long total = 0;
        for (int index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lResourceItemPayload item = items[index]
                ?? throw new InvalidDataException("A resource item is missing.");
            Rp6lItemDescriptor descriptor = item.Descriptor
                ?? throw new InvalidDataException("A resource item descriptor is missing.");
            if (descriptor != resource.Items[index] ||
                descriptor.Index != (long)resource.FirstItemIndex + index ||
                !descriptor.HasReadableSize || item.Payload.IsDefault ||
                item.Payload.Length != descriptor.SizeOrHash ||
                !lookup.TryGetValue(descriptor.ChunkIndex, out Rp6lChunkDescriptor? chunk) ||
                descriptor.Offset < 0 || descriptor.Offset > chunk.LogicalSize ||
                descriptor.SizeOrHash > chunk.LogicalSize - descriptor.Offset ||
                !string.Equals(item.ContentSha256,
                    Convert.ToHexStringLower(SHA256.HashData(item.Payload.AsSpan())), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A resource item does not match its source descriptor or hash.");
            total = checked(total + item.Payload.Length);
            if (total > MaximumResourceBytes)
                throw new InvalidDataException("The resource exceeds the 64 MiB envelope limit.");
            snapshot.Add(item);
        }
        return snapshot.MoveToImmutable();
    }

    private static byte[] BuildContainer(
        Rp6lHeader header,
        Rp6lResourceDescriptor resource,
        ImmutableArray<Rp6lResourceItemPayload> items,
        IReadOnlyList<Rp6lChunkDescriptor> chunks,
        CancellationToken cancellationToken)
    {
        Dictionary<int, Rp6lChunkDescriptor> lookup = chunks.ToDictionary(static chunk => chunk.Index);
        byte[] name = EncodeName(resource.Name);
        int tableEnd = checked(36 + items.Length * 36 + 12 + 4 + name.Length + 1);
        int size = checked(tableEnd + items.Sum(static item => item.Payload.Length));
        byte[] output = new byte[size];
        Span<byte> bytes = output;
        "RP6L"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], header.Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], items.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], items.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], name.Length + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[28..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], header.Unknown);
        int itemTable = 36 + items.Length * 20;
        int resourceTable = itemTable + items.Length * 16;
        int nameTable = resourceTable + 12;
        int payloadOffset = tableEnd;
        for (int index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lResourceItemPayload item = items[index];
            Rp6lChunkDescriptor original = lookup[item.Descriptor.ChunkIndex];
            int row = 36 + index * 20;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[row..], original.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(row + 2)..], original.Category);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(row + 4)..], checked((uint)payloadOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(row + 8)..], checked((uint)item.Payload.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(row + 16)..], original.Unknown0);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[(row + 18)..], original.Unknown1);
            row = itemTable + index * 16;
            bytes[row] = checked((byte)index);
            bytes[row + 1] = item.Descriptor.Flags;
            BinaryPrimitives.WriteInt16LittleEndian(bytes[(row + 2)..], item.Descriptor.StorageGroupId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(row + 8)..], item.Payload.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[(row + 12)..], item.Descriptor.Unknown);
            item.Payload.AsSpan().CopyTo(bytes[payloadOffset..]);
            payloadOffset += item.Payload.Length;
        }
        BinaryPrimitives.WriteInt16LittleEndian(bytes[resourceTable..], checked((short)items.Length));
        BinaryPrimitives.WriteInt16LittleEndian(bytes[(resourceTable + 2)..], resource.ResourceType);
        name.CopyTo(bytes[(nameTable + 4)..]);
        return output;
    }

    private static bool IsSupportedResourceType(short resourceType) =>
        resourceType is Rp6lResourceTypes.Mesh or Rp6lResourceTypes.Texture;
    private static byte[] EncodeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || name.Length > 4096)
            throw new InvalidDataException("The resource name is empty, oversized, or contains NUL.");
        try
        {
            byte[] encoded = StrictUtf8.GetBytes(name);
            if (encoded.Length > 4096)
                throw new InvalidDataException("The resource name exceeds its UTF-8 byte limit.");
            return encoded;
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("The resource name is not valid UTF-8.", exception);
        }
    }

    private static void VerifyReadback(
        Rp6lHeader header,
        Rp6lResourceDescriptor resource,
        ImmutableArray<Rp6lResourceItemPayload> expected,
        IReadOnlyList<Rp6lChunkDescriptor> chunks,
        Rp6lResourceEnvelopeReadback actual)
    {
        if (actual.Header.Version != header.Version || actual.Header.Unknown != header.Unknown ||
            actual.Resource.Name != resource.Name || actual.Resource.ResourceType != resource.ResourceType ||
            actual.Items.Length != expected.Length)
            throw new InvalidDataException("The resource metadata changed during readback.");
        Dictionary<int, Rp6lChunkDescriptor> lookup = chunks.ToDictionary(static chunk => chunk.Index);
        for (int index = 0; index < expected.Length; index++)
        {
            Rp6lResourceItemPayload original = expected[index];
            Rp6lResourceItemPayload emitted = actual.Items[index];
            Rp6lChunkDescriptor sourceChunk = lookup[original.Descriptor.ChunkIndex];
            Rp6lChunkDescriptor emittedChunk = actual.Chunks[index];
            if (emitted.Descriptor.Flags != original.Descriptor.Flags ||
                emitted.Descriptor.StorageGroupId != original.Descriptor.StorageGroupId ||
                emitted.Descriptor.Unknown != original.Descriptor.Unknown ||
                !string.Equals(emitted.ContentSha256,original.ContentSha256,StringComparison.OrdinalIgnoreCase) ||
                !emitted.Payload.AsSpan().SequenceEqual(original.Payload.AsSpan()) ||
                emittedChunk.Flags != sourceChunk.Flags || emittedChunk.Category != sourceChunk.Category ||
                emittedChunk.Unknown0 != sourceChunk.Unknown0 || emittedChunk.Unknown1 != sourceChunk.Unknown1)
                throw new InvalidDataException("A complete resource item changed during readback.");
        }
    }
}
