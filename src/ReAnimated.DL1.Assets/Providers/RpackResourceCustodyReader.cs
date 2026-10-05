using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Providers;

internal static class RpackResourceCustodyReader
{
    private const int MaximumResourceBytes = 64 * 1024 * 1024;

    public static async ValueTask<RetailRpackResourceCustody> ReadAsync(
        RetailAssetRecord asset,
        string providerId,
        string installId,
        RpackSource source,
        Rp6lArchive cached,
        Rp6lLimits limits,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream sourceGuard = new(source.Path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(
            source.Path, limits, cancellationToken).ConfigureAwait(false);
        if (sourceGuard.Length != archive.File.Length)
            throw new IOException("The resource archive changed during capture.");
        ValidateTables(cached, archive);
        int resourceIndex = asset.Source.ResourceIndex
            ?? throw new InvalidDataException("The resource index is missing.");
        if ((uint)resourceIndex >= (uint)archive.Resources.Count)
            throw new InvalidDataException("The resource index is outside the archive.");
        Rp6lResourceDescriptor resource = archive.Resources[resourceIndex];
        long total = 0;
        foreach (Rp6lItemDescriptor item in resource.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.HasReadableSize)
                throw new InvalidDataException("The resource contains an item without a readable length.");
            total = checked(total + item.SizeOrHash);
            if (total > MaximumResourceBytes)
                throw new InvalidDataException("The resource exceeds the 64 MiB custody limit.");
        }
        RetailAssetRecord current = new(
            RetailAssetId.Create(RetailAssetLogicalId.Rpack(resource.ResourceType, resource.Name),
                installId, providerId, resource.Index, source.Priority, archive.CacheIdentity),
            resource.Name,
            new(providerId, RetailAssetSourceKind.Rpack, source.Priority, archive.Path,
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{resource.Name}#{resource.Index}"),
                resource.Index, total, archive.File.Length, archive.File.LastWriteTimeUtc));
        if (current != asset)
            throw new IOException("The resource identity changed after cataloging.");
        ValidateFile(asset.Source, archive);

        Rp6lChunkDescriptor[] chunks = resource.Items.Select(item => archive.Chunks[item.ChunkIndex])
            .DistinctBy(static chunk => chunk.Index).OrderBy(static chunk => chunk.Index).ToArray();
        long tableLength = checked(36L + archive.Header.ChunkCount * 20L +
            archive.Header.ItemCount * 16L + archive.Header.ResourceCount * 12L +
            archive.Header.NameCount * 4L + archive.Header.NameBlobSize);
        string tableBefore = await HashRangeAsync(
            archive.Path, 0, tableLength, cancellationToken).ConfigureAwait(false);
        var chunkHashes = new Dictionary<int, string>();
        foreach (Rp6lChunkDescriptor chunk in chunks)
        {
            if (chunk.StoredSize > limits.MaximumStoredChunkBytes ||
                chunk.LogicalSize > limits.MaximumLogicalChunkBytes)
                throw new InvalidDataException("A resource chunk exceeds the configured read limits.");
            chunkHashes.Add(chunk.Index, await HashRangeAsync(
                archive.Path, chunk.Offset, chunk.StoredSize, cancellationToken).ConfigureAwait(false));
        }

        var items = ImmutableArray.CreateBuilder<RetailRpackItemCustody>(resource.Items.Count);
        using IncrementalHash content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (Rp6lItemDescriptor item in resource.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lChunkDescriptor chunk = archive.Chunks[item.ChunkIndex];
            await using Stream logical = await cache.OpenChunkAsync(
                archive, chunk, chunkHashes[chunk.Index], cancellationToken).ConfigureAwait(false);
            logical.Position = item.Offset;
            byte[] payload = GC.AllocateUninitializedArray<byte>(item.SizeOrHash);
            await logical.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            content.AppendData(payload);
            items.Add(new(item, ImmutableArray.CreateRange(payload),
                Convert.ToHexStringLower(SHA256.HashData(payload))));
        }
        foreach (Rp6lChunkDescriptor chunk in chunks)
        {
            string after = await HashRangeAsync(
                archive.Path, chunk.Offset, chunk.StoredSize, cancellationToken).ConfigureAwait(false);
            if (after != chunkHashes[chunk.Index])
                throw new IOException("A packed resource chunk changed during custody capture.");
        }
        if (tableBefore != await HashRangeAsync(
                archive.Path, 0, tableLength, cancellationToken).ConfigureAwait(false))
            throw new IOException("The resource tables changed during custody capture.");
        ValidateFile(asset.Source, archive);
        return new(asset, archive.Header, resource with { Items = resource.Items.ToImmutableArray() },
            items.MoveToImmutable(),
            chunks.Select(chunk => new RetailRpackChunkCustody(chunk, chunkHashes[chunk.Index])).ToImmutableArray(),
            Convert.ToHexStringLower(content.GetHashAndReset()));
    }

    private static void ValidateTables(Rp6lArchive cached, Rp6lArchive current)
    {
        if (cached.File != current.File || cached.Header != current.Header ||
            cached.CacheIdentity != current.CacheIdentity ||
            !cached.Names.SequenceEqual(current.Names, StringComparer.Ordinal) ||
            !cached.Chunks.SequenceEqual(current.Chunks) ||
            !cached.Items.SequenceEqual(current.Items) ||
            cached.Resources.Count != current.Resources.Count)
            throw new IOException("The resource archive tables changed after cataloging.");
        for (int index = 0; index < cached.Resources.Count; index++)
        {
            Rp6lResourceDescriptor left = cached.Resources[index];
            Rp6lResourceDescriptor right = current.Resources[index];
            if (left.Index != right.Index || left.Name != right.Name ||
                left.ResourceType != right.ResourceType || left.NameIndex != right.NameIndex ||
                left.FirstItemIndex != right.FirstItemIndex || left.ItemCount != right.ItemCount)
                throw new IOException("The resource archive tables changed after cataloging.");
        }
    }

    private static void ValidateFile(RetailAssetSource source, Rp6lArchive archive)
    {
        FileInfo current = new(archive.Path);
        if (!current.Exists || current.Length != source.SourceLength ||
            current.LastWriteTimeUtc != source.SourceLastWriteTimeUtc ||
            archive.File.Length != source.SourceLength ||
            archive.File.LastWriteTimeUtc != source.SourceLastWriteTimeUtc)
            throw new IOException("The resource archive changed after cataloging.");
    }

    private static async Task<string> HashRangeAsync(
        string path, long offset, long length, CancellationToken cancellationToken)
    {
        if (offset < 0 || length < 0)
            throw new InvalidDataException("A resource range is invalid.");
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = offset;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, length);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            length -= count;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
