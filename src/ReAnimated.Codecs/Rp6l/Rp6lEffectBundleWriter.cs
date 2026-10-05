using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Rp6l;

public sealed record Rp6lEffectBundleReadback(
    Rp6lHeader Header,
    Rp6lResourceDescriptor Resource,
    Rp6lItemDescriptor Item,
    Rp6lChunkDescriptor Chunk,
    ImmutableArray<Rp6lEffectDefinition> Definitions,
    string PayloadSha256,
    long ArchiveByteLength);

public sealed record Rp6lEffectBundleExportResult(
    string Path,
    string ArchiveSha256,
    Rp6lEffectBundleReadback Readback);

public static class Rp6lEffectBundleWriter
{
    private const int TableEnd = 91;
    private const int MaximumStoredBytes = 128 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Rp6lLimits ReadbackLimits = new()
    {
        MaximumTableCount = 1,
        MaximumNameBlobBytes = 3,
        MaximumTableBytes = TableEnd - 36,
        MaximumLogicalChunkBytes = Rp6lEffectBundleDecoder.MaximumBytes,
        MaximumStoredChunkBytes = MaximumStoredBytes,
        MaximumItemBytes = Rp6lEffectBundleDecoder.MaximumBytes,
    };

    public static byte[] BuildPayload(
        IReadOnlyList<Rp6lEffectDefinition> definitions,
        CancellationToken cancellationToken = default) =>
        BuildValidatedPayload(Snapshot(definitions, cancellationToken), cancellationToken);

    public static byte[] Build(
        IReadOnlyList<Rp6lEffectDefinition> definitions,
        Rp6lCompression compression,
        CancellationToken cancellationToken = default) =>
        BuildContainer(BuildPayload(definitions, cancellationToken), compression, cancellationToken);

    public static async Task<Rp6lEffectBundleExportResult> WriteNewAsync(
        string path,
        IReadOnlyList<Rp6lEffectDefinition> definitions,
        Rp6lCompression compression,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cache);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = System.IO.Path.GetFullPath(path);
        string directory = System.IO.Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The effect output has no parent directory.");
        if (File.Exists(destination))
            throw new IOException("The effect output already exists.");
        ImmutableArray<Rp6lEffectDefinition> expected = Snapshot(definitions, cancellationToken);
        byte[] payload = BuildValidatedPayload(expected, cancellationToken);
        byte[] archiveBytes = BuildContainer(payload, compression, cancellationToken);
        string archiveHash = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        Directory.CreateDirectory(directory);
        string temporary = System.IO.Path.Combine(directory,
            $".{System.IO.Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream output = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(archiveBytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Rp6lEffectBundleReadback readback = await ReadBackAsync(
                temporary, cache, cancellationToken).ConfigureAwait(false);
            VerifySemantics(expected, readback.Definitions);
            if (readback.PayloadSha256 != Convert.ToHexStringLower(SHA256.HashData(payload)) ||
                readback.ArchiveByteLength != archiveBytes.Length ||
                readback.Chunk.Compression != compression)
                throw new InvalidDataException("The effect envelope did not round-trip.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            return new(destination, archiveHash, readback);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async Task<Rp6lEffectBundleReadback> ReadBackAsync(
        string path,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cache);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(path, ReadbackLimits, cancellationToken)
            .ConfigureAwait(false);
        ValidateRouting(archive);
        Rp6lResourceDescriptor resource = archive.Resources[0];
        Rp6lItemDescriptor item = archive.Items[0];
        byte[] payload = await archive.ReadItemBytesAsync(
            item, cache, Rp6lEffectBundleDecoder.MaximumBytes, cancellationToken).ConfigureAwait(false);
        ImmutableArray<Rp6lEffectDefinition> definitions =
            Rp6lEffectBundleDecoder.Decode(payload, cancellationToken);
        return new(archive.Header, resource, item, archive.Chunks[0], definitions,
            Convert.ToHexStringLower(SHA256.HashData(payload)), archive.File.Length);
    }

    private static ImmutableArray<Rp6lEffectDefinition> Snapshot(
        IReadOnlyList<Rp6lEffectDefinition> definitions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        cancellationToken.ThrowIfCancellationRequested();
        int count = definitions.Count;
        if (count < 0 || count > Rp6lEffectBundleDecoder.MaximumDefinitions)
            throw new InvalidDataException("Too many effect definitions.");
        var snapshot = ImmutableArray.CreateBuilder<Rp6lEffectDefinition>(count);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Add(definitions[index]
                ?? throw new InvalidDataException("An effect definition is missing."));
        }
        return snapshot.MoveToImmutable();
    }

    private static byte[] BuildValidatedPayload(
        ImmutableArray<Rp6lEffectDefinition> definitions,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Rp6lEffectDefinition definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.Name is null || definition.SourceText is null ||
                definition.Name.Length == 0 ||
                definition.Name.Length > Rp6lEffectBundleDecoder.MaximumNameBytes ||
                definition.SourceText.Length > Rp6lEffectBundleDecoder.MaximumSourceBytes ||
                definition.Name.Contains('\0') || definition.SourceText.Contains('\0') ||
                !names.Add(definition.Name))
                throw new InvalidDataException("An effect name or source is unsafe, oversized, or duplicated.");
            byte[] name = EncodeStrict(definition.Name);
            byte[] source = EncodeStrict(definition.SourceText);
            int entryLength = checked(name.Length + source.Length + 3);
            if (name.Length > Rp6lEffectBundleDecoder.MaximumNameBytes ||
                source.Length > Rp6lEffectBundleDecoder.MaximumSourceBytes ||
                definition.EntryOffset < 0 ||
                (long)definition.EntryOffset + entryLength >= Rp6lEffectBundleDecoder.MaximumBytes ||
                definition.TextOffset != (long)definition.EntryOffset + name.Length + 2 ||
                definition.TextByteLength != source.Length ||
                definition.EntryByteLength != entryLength ||
                !string.Equals(definition.ContentSha256,
                    Convert.ToHexStringLower(SHA256.HashData(source)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The effect descriptor does not match its exact UTF-8 source.");
            if (output.Length + entryLength + 1 > Rp6lEffectBundleDecoder.MaximumBytes)
                throw new InvalidDataException("The effect bundle exceeds its byte limit.");
            output.Write(name);
            output.WriteByte(0);
            output.WriteByte(unchecked((byte)definition.Kind));
            output.Write(source);
            output.WriteByte(0);
        }
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteByte(0);
        byte[] payload = output.ToArray();
        VerifySemantics(definitions, Rp6lEffectBundleDecoder.Decode(payload, cancellationToken));
        return payload;
    }

    private static byte[] EncodeStrict(string value)
    {
        try { return StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("The effect text is not valid UTF-8.", exception);
        }
    }

    private static void VerifySemantics(
        ImmutableArray<Rp6lEffectDefinition> expected,
        ImmutableArray<Rp6lEffectDefinition> actual)
    {
        if (actual.Length != expected.Length)
            throw new InvalidDataException("The effect definition count changed during readback.");
        for (int index = 0; index < actual.Length; index++)
        {
            Rp6lEffectDefinition left = expected[index];
            Rp6lEffectDefinition right = actual[index];
            if (left.Name != right.Name || left.Kind != right.Kind ||
                left.SourceText != right.SourceText || left.TextByteLength != right.TextByteLength ||
                left.EntryByteLength != right.EntryByteLength ||
                !string.Equals(left.ContentSha256, right.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An effect definition changed during readback.");
        }
    }

    private static byte[] BuildContainer(
        byte[] payload,
        Rp6lCompression compression,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] stored;
        switch (compression)
        {
            case Rp6lCompression.None:
                stored = payload;
                break;
            case Rp6lCompression.Zlib:
                using (MemoryStream compressed = new())
                {
                    using (ZLibStream encoder = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        for (int offset = 0; offset < payload.Length; offset += 64 * 1024)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            encoder.Write(payload.AsSpan(offset, Math.Min(64 * 1024, payload.Length - offset)));
                        }
                    }
                    stored = compressed.ToArray();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(compression), "Only None and Zlib compression are supported.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (stored.Length > MaximumStoredBytes)
            throw new InvalidDataException("The packed effect bundle exceeds its byte limit.");
        byte[] output = new byte[checked(TableEnd + stored.Length)];
        Span<byte> bytes = output;
        "RP6L"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], compression == Rp6lCompression.Zlib ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[12..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[28..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[32..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[36..], 80);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[38..], 514);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[40..], TableEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[44..], checked((uint)payload.Length));
        BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], compression == Rp6lCompression.Zlib ? stored.Length : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[52..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[54..], 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[64..], payload.Length);
        BinaryPrimitives.WriteInt16LittleEndian(bytes[72..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes[74..], Rp6lResourceTypes.Effect);
        "FX\0"u8.CopyTo(bytes[88..]);
        stored.CopyTo(bytes[TableEnd..]);
        return output;
    }

    private static void ValidateRouting(Rp6lArchive archive)
    {
        Rp6lHeader header = archive.Header;
        if (header.Version != 1 || header.CompressionFlags is not (0 or 1) || header.Unknown != 1 ||
            header.ItemCount != 1 || header.ChunkCount != 1 || header.ResourceCount != 1 ||
            header.NameCount != 1 || header.NameBlobSize != 3 || archive.Names[0] != "FX")
            throw new InvalidDataException("The effect envelope has an unexpected header.");
        Rp6lResourceDescriptor resource = archive.Resources[0];
        Rp6lItemDescriptor item = archive.Items[0];
        Rp6lChunkDescriptor chunk = archive.Chunks[0];
        if (resource.Index != 0 || resource.Name != "FX" || resource.ResourceType != Rp6lResourceTypes.Effect ||
            resource.NameIndex != 0 || resource.FirstItemIndex != 0 || resource.ItemCount != 1 ||
            item.Index != 0 || item.ChunkIndex != 0 || item.Flags != 0 || item.StorageGroupId != 0 ||
            item.Offset != 0 || item.Unknown != 0 || !item.HasReadableSize ||
            chunk.Index != 0 || chunk.Flags != 80 || chunk.Category != 514 ||
            chunk.Unknown0 != 1 || chunk.Unknown1 != 2 || chunk.ItemOffsetBias != 0 ||
            chunk.Offset != TableEnd || chunk.LogicalSize != item.SizeOrHash ||
            chunk.Compression != (header.CompressionFlags == 1 ? Rp6lCompression.Zlib : Rp6lCompression.None) ||
            archive.File.Length != TableEnd + chunk.StoredSize)
            throw new InvalidDataException("The effect envelope has unexpected physical routing.");
    }
}
