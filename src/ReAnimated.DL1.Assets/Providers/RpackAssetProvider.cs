using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Providers;

public sealed record RpackSource(string Path, int Priority);

public sealed record RpackProviderError(
    string Path,
    int? ResourceIndex,
    string? ResourceName,
    string ErrorType,
    string Message);

public sealed class RpackAssetProvider :
    IRetailAssetProvider,
    IRetailAssetSnapshotProvider,
    IAsyncDisposable
{
    private readonly RpackSource[] _sources;
    private readonly Rp6lChunkCache _chunkCache;
    private readonly bool _ownsChunkCache;
    private readonly Rp6lLimits _limits;
    private readonly string _installId;
    private readonly ConcurrentDictionary<string, Rp6lArchive> _archives =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RpackProviderError> _sourceErrors =
        new(StringComparer.OrdinalIgnoreCase);

    public RpackAssetProvider(
        string providerId,
        IEnumerable<RpackSource> sources,
        Rp6lChunkCache? chunkCache = null,
        Rp6lLimits? limits = null,
        string? installId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(sources);
        ProviderId = providerId;
        _sources = sources
            .Select(static source => source with
            {
                Path = System.IO.Path.GetFullPath(source.Path),
            })
            .OrderByDescending(static source => source.Priority)
            .ThenBy(static source => source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_sources.Length == 0 ||
            _sources
                .GroupBy(static source => source.Path, StringComparer.OrdinalIgnoreCase)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "RPACK sources must be non-empty and have unique paths.",
                nameof(sources));
        }

        _chunkCache = chunkCache ?? new Rp6lChunkCache();
        _ownsChunkCache = chunkCache is null;
        _limits = limits ?? Rp6lLimits.Default;
        _installId = string.IsNullOrWhiteSpace(installId)
            ? RetailAssetIdentity.CreateInstallId(
                Path.GetDirectoryName(_sources[0].Path)
                ?? _sources[0].Path)
            : installId;
    }

    public string ProviderId { get; }

    public IReadOnlyList<RpackSource> Sources => _sources;

    public IReadOnlyList<RpackProviderError> SourceErrors =>
        _sourceErrors.Values
            .OrderBy(static error => error.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static error => error.ResourceIndex)
            .ToArray();

    public async ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        RetailAssetRootSnapshot[] roots = _sources
            .Select(static source =>
                Path.GetDirectoryName(source.Path) ?? source.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(static (path, ordinal) =>
                new RetailAssetRootSnapshot(
                    ordinal,
                    "rpack-directory",
                    path,
                    Directory.Exists(path)))
            .ToArray();
        List<RetailAssetSourceSnapshot> sources =
            new(_sources.Length);
        for (int index = 0; index < _sources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RpackSource source = _sources[index];
            sources.Add(
                await RetailAssetSnapshotCapture.CaptureFileAsync(
                    index,
                    RetailAssetSourceKind.Rpack,
                    source.Priority,
                    source.Path,
                    cancellationToken).ConfigureAwait(false));
        }

        string configurationFingerprint =
            RetailAssetSnapshotCapture.CreateConfigurationFingerprint(
                ProviderId,
                _installId,
                _limits.MaximumTableCount,
                _limits.MaximumNameBlobBytes,
                _limits.MaximumTableBytes,
                _limits.MaximumLogicalChunkBytes,
                _limits.MaximumStoredChunkBytes,
                _limits.MaximumItemBytes,
                string.Join(
                    "\n",
                    _sources.Select(static source =>
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"{source.Priority}|{source.Path.ToUpperInvariant()}"))));
        return new RetailAssetProviderSnapshot(
            ProviderId,
            nameof(RpackAssetProvider),
            _installId,
            configurationFingerprint,
            roots,
            sources);
    }

    public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (RpackSource source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lArchive archive;
            try
            {
                archive = await GetArchiveAsync(
                    source.Path,
                    cancellationToken).ConfigureAwait(false);
                _sourceErrors.TryRemove(
                    CreateErrorKey(source.Path),
                    out _);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _sourceErrors[CreateErrorKey(source.Path)] =
                    CreateError(source.Path, null, null, exception);
                continue;
            }

            foreach (Rp6lResourceDescriptor resource in archive.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RetailAssetRecord record;
                try
                {
                    long length = 0;
                    foreach (Rp6lItemDescriptor item in resource.Items)
                    {
                        if (item.HasReadableSize)
                        {
                            length = checked(length + item.SizeOrHash);
                        }
                    }

                    RetailAssetLogicalId logicalId =
                        RetailAssetLogicalId.Rpack(
                        resource.ResourceType,
                        resource.Name);
                    record = new RetailAssetRecord(
                        RetailAssetId.Create(
                            logicalId,
                            _installId,
                            ProviderId,
                            resource.Index,
                            source.Priority,
                            archive.CacheIdentity),
                        resource.Name,
                        new RetailAssetSource(
                            ProviderId,
                            RetailAssetSourceKind.Rpack,
                            source.Priority,
                            archive.Path,
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"{resource.Name}#{resource.Index}"),
                            resource.Index,
                            length,
                            archive.File.Length,
                            archive.File.LastWriteTimeUtc));
                    _sourceErrors.TryRemove(
                        CreateErrorKey(source.Path, resource.Index),
                        out _);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException
                    or ArgumentException
                    or OverflowException)
                {
                    _sourceErrors[
                        CreateErrorKey(source.Path, resource.Index)] =
                        CreateError(
                            source.Path,
                            resource.Index,
                            resource.Name,
                            exception);
                    continue;
                }

                yield return record;
            }
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ValidateOwnership(asset);
        Rp6lArchive archive = await GetArchiveAsync(
            asset.Source.ContainerPath,
            cancellationToken).ConfigureAwait(false);
        ValidateSourceSnapshot(asset.Source, archive);
        int index = asset.Source.ResourceIndex
            ?? throw new InvalidDataException(
                "RPACK asset does not identify a resource index.");
        if (index < 0 || index >= archive.Resources.Count)
        {
            throw new InvalidDataException(
                $"RPACK resource index {index} is outside the current archive.");
        }

        Rp6lResourceDescriptor resource = archive.Resources[index];
        RetailAssetLogicalId currentId = RetailAssetLogicalId.Rpack(
            resource.ResourceType,
            resource.Name);
        if (currentId != asset.Id.LogicalId ||
            asset.Id.SourceIndex != index ||
            !string.Equals(
                asset.Id.SourceFingerprint,
                archive.CacheIdentity,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "RPACK resource identity changed after cataloging.");
        }

        return await archive.OpenResourceStreamAsync(
            resource,
            _chunkCache,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _archives.Clear();
        if (_ownsChunkCache)
        {
            await _chunkCache.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<Rp6lArchive> GetArchiveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (_archives.TryGetValue(fullPath, out Rp6lArchive? archive))
        {
            return archive;
        }

        archive = await Rp6lArchive.OpenAsync(
            fullPath,
            _limits,
            cancellationToken).ConfigureAwait(false);
        return _archives.GetOrAdd(fullPath, archive);
    }

    private void ValidateOwnership(RetailAssetRecord asset)
    {
        if (!string.Equals(
                asset.Source.ProviderId,
                ProviderId,
                StringComparison.Ordinal) ||
            asset.Source.Kind != RetailAssetSourceKind.Rpack)
        {
            throw new ArgumentException(
                "The asset does not belong to this RPACK provider.",
                nameof(asset));
        }
    }

    private static void ValidateSourceSnapshot(
        RetailAssetSource source,
        Rp6lArchive archive)
    {
        FileInfo current = new(archive.Path);
        if (!current.Exists ||
            current.Length != source.SourceLength ||
            current.LastWriteTimeUtc != source.SourceLastWriteTimeUtc)
        {
            throw new IOException(
                $"RPACK '{archive.Path}' changed after the catalog was built.");
        }
    }

    private static string CreateErrorKey(
        string path,
        int? resourceIndex = null) =>
        resourceIndex is { } index
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{path}\0{index}")
            : path;

    private static RpackProviderError CreateError(
        string path,
        int? resourceIndex,
        string? resourceName,
        Exception exception) =>
        new(
            path,
            resourceIndex,
            resourceName,
            exception.GetType().Name,
            exception.Message);
}
