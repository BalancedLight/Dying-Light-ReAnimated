using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Providers;

public sealed record RpackEffectOriginalBundle(
    Rp6lArchive Archive,
    Rp6lResourceDescriptor Resource,
    Rp6lItemDescriptor Item,
    byte[] Payload);

public sealed class RpackEffectAssetProvider : IRetailAssetProvider, IRetailAssetSnapshotProvider, IRetailEmbeddedEffectProvider, IRetailAssetCatalogCachePolicy
{
    private const int MaximumBundleBytes = 64 * 1024 * 1024;
    private readonly RpackAssetProvider _parent;
    private readonly Rp6lChunkCache _cache;
    private readonly string _installId;
    private readonly ConcurrentDictionary<string, RpackProviderError> _errors =
        new(StringComparer.OrdinalIgnoreCase);

    public RpackEffectAssetProvider(
        string providerId,
        RpackAssetProvider parent,
        Rp6lChunkCache cache,
        string? installId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(cache);
        if (providerId.Equals(parent.ProviderId, StringComparison.Ordinal))
            throw new ArgumentException("The effect provider needs its own provider ID.", nameof(providerId));
        ProviderId = providerId;
        _parent = parent;
        _cache = cache;
        _installId = string.IsNullOrWhiteSpace(installId)
            ? RetailAssetIdentity.CreateInstallId(
                Path.GetDirectoryName(parent.Sources[0].Path) ?? parent.Sources[0].Path)
            : installId;
    }

    public string ProviderId { get; }

    public bool CanPersistCatalog => _errors.IsEmpty;

    public IReadOnlyList<RpackProviderError> SourceErrors => _errors.Values
        .OrderBy(static error => error.Path, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static error => error.ResourceIndex).ToArray();

    public async ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        RetailAssetProviderSnapshot parent = await _parent.CaptureSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        return new(
            ProviderId,
            nameof(RpackEffectAssetProvider),
            _installId,
            RetailAssetIdentity.CreateSourceFingerprint(parent.ConfigurationFingerprint, "embedded-effects-v2"),
            parent.Roots,
            parent.Sources.Select(static source => source with
            {
                Kind = RetailAssetSourceKind.RpackEmbeddedEffect,
            }).ToArray());
    }

    public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (RpackSource source in _parent.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lArchive archive;
            RetailAssetSourceSnapshot snapshot;
            try
            {
                snapshot = await CaptureSourceAsync(source, cancellationToken).ConfigureAwait(false);
                archive = await _parent.GetArchiveAsync(source.Path, cancellationToken).ConfigureAwait(false);
                ValidateFile(snapshot, archive);
                _errors.TryRemove(ErrorKey(source.Path), out _);
            }
            catch (Exception exception) when (IsSourceError(exception))
            {
                AddError(source.Path, null, null, exception);
                continue;
            }

            foreach (Rp6lResourceDescriptor resource in archive.Resources
                         .Where(static resource => resource.ResourceType == Rp6lResourceTypes.Effect))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ImmutableArray<RetailAssetRecord> records;
                try
                {
                    Rp6lItemDescriptor item = GetBundleItem(archive, resource);
                    string packedHash = await HashStoredChunkAsync(archive, item, cancellationToken)
                        .ConfigureAwait(false);
                    byte[] payload = await archive.ReadItemBytesAsync(
                        item, _cache, MaximumBundleBytes, cancellationToken).ConfigureAwait(false);
                    ImmutableArray<Rp6lEffectDefinition> definitions =
                        Rp6lEffectBundleDecoder.Decode(payload, cancellationToken);
                    records = definitions.Select(definition => CreateRecord(
                        source, snapshot, archive, resource, item, definition, packedHash)).ToImmutableArray();
                    ValidateFile(snapshot, archive);
                    _errors.TryRemove(ErrorKey(source.Path, resource.Index), out _);
                }
                catch (Exception exception) when (IsSourceError(exception))
                {
                    AddError(source.Path, resource.Index, resource.Name, exception);
                    continue;
                }

                foreach (RetailAssetRecord record in records)
                    yield return record;
            }
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        VerifiedBundle verified = await ReadVerifiedBundleAsync(asset, cancellationToken).ConfigureAwait(false);
        return new MemoryStream(
            verified.Bundle.Payload.AsSpan(
                verified.Definition.TextOffset, verified.Definition.TextByteLength).ToArray(),
            writable: false);
    }

    public async ValueTask<RpackEffectOriginalBundle> ReadOriginalBundleAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default) =>
        (await ReadVerifiedBundleAsync(asset, cancellationToken).ConfigureAwait(false)).Bundle;

    public async ValueTask<RetailEmbeddedEffectCustody> ReadEmbeddedCustodyAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        VerifiedBundle verified = await ReadVerifiedBundleAsync(asset, cancellationToken).ConfigureAwait(false);
        RpackEffectOriginalBundle bundle = verified.Bundle;
        Rp6lResourceDescriptor resource = bundle.Resource;
        RetailAssetLogicalId logical = RetailAssetLogicalId.Rpack(resource.ResourceType, resource.Name);
        RetailAssetRecord parent = new(
            RetailAssetId.Create(logical, _installId, _parent.ProviderId, resource.Index,
                asset.Source.Priority, bundle.Archive.CacheIdentity),
            resource.Name,
            new(_parent.ProviderId, RetailAssetSourceKind.Rpack, asset.Source.Priority,
                bundle.Archive.Path,
                string.Create(CultureInfo.InvariantCulture, $"{resource.Name}#{resource.Index}"),
                resource.Index, bundle.Item.SizeOrHash,
                bundle.Archive.File.Length, bundle.Archive.File.LastWriteTimeUtc));
        return new(parent, resource, bundle.Item, bundle.Archive.Chunks[bundle.Item.ChunkIndex],
            ImmutableArray.CreateRange(bundle.Payload), verified.Definition);
    }

    private async ValueTask<VerifiedBundle> ReadVerifiedBundleAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        RetailAssetRecordValidator.Validate(ProviderId, asset);
        if (asset.Source.Kind != RetailAssetSourceKind.RpackEmbeddedEffect ||
            !asset.Id.InstallId.Equals(_installId, StringComparison.Ordinal))
            throw new ArgumentException("The asset does not belong to this effect provider.", nameof(asset));
        RpackSource source = _parent.Sources.FirstOrDefault(source =>
            source.Path.Equals(asset.Source.ContainerPath, StringComparison.OrdinalIgnoreCase) &&
            source.Priority == asset.Source.Priority)
            ?? throw new ArgumentException("The effect container is not a configured source.", nameof(asset));
        RetailAssetSourceSnapshot snapshot = await CaptureSourceAsync(source, cancellationToken)
            .ConfigureAwait(false);
        Rp6lArchive archive = await _parent.GetArchiveAsync(source.Path, cancellationToken).ConfigureAwait(false);
        ValidateFile(snapshot, archive);
        if (snapshot.Length != asset.Source.SourceLength ||
            snapshot.LastWriteTimeUtcTicks != asset.Source.SourceLastWriteTimeUtc.Ticks)
            throw new IOException("The effect archive changed after cataloging.");
        int index = asset.Source.ResourceIndex
            ?? throw new InvalidDataException("The effect asset is missing its resource index.");
        if ((uint)index >= (uint)archive.Resources.Count ||
            archive.Resources[index].ResourceType != Rp6lResourceTypes.Effect)
            throw new InvalidDataException("The effect resource is no longer available.");
        Rp6lResourceDescriptor resource = archive.Resources[index];
        Rp6lItemDescriptor item = GetBundleItem(archive, resource);
        string packedHash = await HashStoredChunkAsync(archive, item, cancellationToken).ConfigureAwait(false);
        byte[] payload = await archive.ReadItemBytesAsync(
            item, _cache, MaximumBundleBytes, cancellationToken).ConfigureAwait(false);
        ImmutableArray<Rp6lEffectDefinition> definitions = Rp6lEffectBundleDecoder.Decode(payload, cancellationToken);
        Rp6lEffectDefinition? match = definitions.FirstOrDefault(definition =>
            string.Equals(definition.Name + ".fx", asset.Id.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(definition.ContentSha256, asset.Id.ContentFingerprint, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new IOException("The embedded effect identity or content changed after cataloging.");
        RetailAssetRecord current = CreateRecord(source, snapshot, archive, resource, item, match, packedHash);
        if (current.Id != asset.Id || current.Source != asset.Source)
            throw new IOException("The embedded effect identity or content changed after cataloging.");
        ValidateFile(snapshot, archive);
        return new(new(archive, resource, item, payload), match);
    }

    private RetailAssetRecord CreateRecord(
        RpackSource source,
        RetailAssetSourceSnapshot snapshot,
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource,
        Rp6lItemDescriptor item,
        Rp6lEffectDefinition definition,
        string packedHash)
    {
        string name = definition.Name + ".fx";
        RetailAssetLogicalId logical = RetailAssetLogicalId.VirtualFile(name);
        string entry = string.Create(CultureInfo.InvariantCulture,
            $"fx/{resource.Index}/{item.Index}/{definition.EntryOffset}/{definition.EntryByteLength}/{definition.TextOffset}/{definition.TextByteLength}/{definition.Kind}");
        string fingerprint = RetailAssetIdentity.CreateSourceFingerprint(
            _parent.ProviderId, archive.CacheIdentity, snapshot.BoundedFingerprint,
            Directory.Exists(Path.GetDirectoryName(source.Path)), resource.Index, resource.Name,
            item.Index, item.Offset, entry, packedHash, definition.ContentSha256);
        return new(
            RetailAssetId.Create(logical, _installId, ProviderId, resource.Index,
                source.Priority, fingerprint, definition.ContentSha256),
            name,
            new(ProviderId, RetailAssetSourceKind.RpackEmbeddedEffect, source.Priority,
                archive.Path, entry, resource.Index, definition.TextByteLength,
                snapshot.Length, new DateTime(snapshot.LastWriteTimeUtcTicks, DateTimeKind.Utc)));
    }

    private static async ValueTask<RetailAssetSourceSnapshot> CaptureSourceAsync(
        RpackSource source, CancellationToken cancellationToken) =>
        await RetailAssetSnapshotCapture.CaptureFileAsync(
            0, RetailAssetSourceKind.RpackEmbeddedEffect, source.Priority,
            source.Path, cancellationToken).ConfigureAwait(false);

    private static Rp6lItemDescriptor GetBundleItem(Rp6lArchive archive, Rp6lResourceDescriptor resource)
    {
        if (resource.Items.Count != 1 || !resource.Items[0].HasReadableSize)
            throw new InvalidDataException("An effect bundle must contain one readable item.");
        if ((archive.Chunks[resource.Items[0].ChunkIndex].Flags & 0xFF) != 80)
            throw new InvalidDataException("The effect item does not use the FX chunk type.");
        return resource.Items[0];
    }

    private static void ValidateFile(RetailAssetSourceSnapshot snapshot, Rp6lArchive archive)
    {
        FileInfo current = new(archive.Path);
        if (!current.Exists || current.Length != snapshot.Length ||
            current.LastWriteTimeUtc.Ticks != snapshot.LastWriteTimeUtcTicks ||
            archive.File.Length != snapshot.Length ||
            archive.File.LastWriteTimeUtc.Ticks != snapshot.LastWriteTimeUtcTicks)
            throw new IOException("The effect archive changed after cataloging.");
    }

    private static async Task<string> HashStoredChunkAsync(
        Rp6lArchive archive, Rp6lItemDescriptor item, CancellationToken cancellationToken)
    {
        Rp6lChunkDescriptor chunk = archive.Chunks[item.ChunkIndex];
        if (chunk.StoredSize < 0 || chunk.StoredSize > archive.Limits.MaximumStoredChunkBytes)
            throw new InvalidDataException("The packed effect chunk exceeds the configured stored-chunk limit.");
        await using FileStream stream = new(
            archive.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.Position = chunk.Offset;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = chunk.StoredSize;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, remaining);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsSourceError(Exception exception) =>
        exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or OverflowException;

    private void AddError(string path, int? index, string? name, Exception exception) =>
        _errors[ErrorKey(path, index)] = new(path, index, name, exception.GetType().Name, exception.Message);

    private static string ErrorKey(string path, int? index = null) =>
        index is { } value ? string.Create(CultureInfo.InvariantCulture, $"{path}\0{value}") : path;

    private sealed record VerifiedBundle(RpackEffectOriginalBundle Bundle, Rp6lEffectDefinition Definition);
}
