using System.IO.Compression;
using System.Runtime.CompilerServices;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Providers;

public sealed record ZipPakSource(string Path, int Priority);

public sealed class ZipPakAssetProvider :
    IRetailAssetProvider,
    IRetailAssetSnapshotProvider
{
    private readonly ZipPakSource[] _sources;
    private readonly HashSet<string> _extensions;
    private readonly long _maximumEntryBytes;
    private readonly string _installId;

    public ZipPakAssetProvider(
        string providerId,
        IEnumerable<ZipPakSource> sources,
        IEnumerable<string> extensions,
        long maximumEntryBytes = 256L * 1024 * 1024,
        string? installId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryBytes);
        ProviderId = providerId;
        _sources = sources
            .Select(static source => source with
            {
                Path = System.IO.Path.GetFullPath(source.Path),
            })
            .OrderByDescending(static source => source.Priority)
            .ThenBy(static source => source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _extensions = extensions
            .Select(static extension =>
                extension.StartsWith('.')
                    ? extension
                    : string.Concat(".", extension))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_sources.Length == 0 || _extensions.Count == 0)
        {
            throw new ArgumentException(
                "ZIP PAK sources and extensions must be non-empty.");
        }

        _maximumEntryBytes = maximumEntryBytes;
        _installId = string.IsNullOrWhiteSpace(installId)
            ? RetailAssetIdentity.CreateInstallId(
                Path.GetDirectoryName(_sources[0].Path)
                ?? _sources[0].Path)
            : installId;
    }

    public string ProviderId { get; }

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
                    "zip-pak-directory",
                    path,
                    Directory.Exists(path)))
            .ToArray();
        List<RetailAssetSourceSnapshot> sources =
            new(_sources.Length);
        for (int index = 0; index < _sources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipPakSource source = _sources[index];
            sources.Add(
                await RetailAssetSnapshotCapture.CaptureFileAsync(
                    index,
                    RetailAssetSourceKind.ZipPak,
                    source.Priority,
                    source.Path,
                    cancellationToken).ConfigureAwait(false));
        }

        string configurationFingerprint =
            RetailAssetSnapshotCapture.CreateConfigurationFingerprint(
                ProviderId,
                _installId,
                _maximumEntryBytes,
                string.Join(
                    "\n",
                    _extensions.OrderBy(
                        static extension => extension,
                        StringComparer.OrdinalIgnoreCase)),
                string.Join(
                    "\n",
                    _sources.Select(static source =>
                        string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"{source.Priority}|{source.Path.ToUpperInvariant()}"))));
        return new RetailAssetProviderSnapshot(
            ProviderId,
            nameof(ZipPakAssetProvider),
            _installId,
            configurationFingerprint,
            roots,
            sources);
    }

    public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (ZipPakSource source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo sourceFile = new(source.Path);
            using ZipArchive archive = ZipFile.OpenRead(sourceFile.FullName);
            long sourceIndex = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long currentSourceIndex = sourceIndex++;
                if (string.IsNullOrEmpty(entry.Name) ||
                    !_extensions.Contains(
                        System.IO.Path.GetExtension(entry.FullName)) ||
                    entry.Length < 0 ||
                    entry.Length > _maximumEntryBytes)
                {
                    continue;
                }

                RetailAssetLogicalId logicalId;
                try
                {
                    logicalId =
                        RetailAssetLogicalId.VirtualFile(entry.FullName);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                yield return new RetailAssetRecord(
                    RetailAssetId.Create(
                        logicalId,
                        _installId,
                        ProviderId,
                        currentSourceIndex,
                        source.Priority,
                        RetailAssetIdentity.CreateSourceFingerprint(
                            sourceFile.FullName.ToUpperInvariant(),
                            sourceFile.Length,
                            sourceFile.LastWriteTimeUtc,
                            entry.FullName,
                            entry.Length,
                            entry.CompressedLength)),
                    entry.FullName,
                    new RetailAssetSource(
                        ProviderId,
                        RetailAssetSourceKind.ZipPak,
                        source.Priority,
                        sourceFile.FullName,
                        entry.FullName,
                        null,
                        entry.Length,
                        sourceFile.Length,
                        sourceFile.LastWriteTimeUtc));
            }
        }
    }

    public ValueTask<Stream> OpenReadAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                asset.Source.ProviderId,
                ProviderId,
                StringComparison.Ordinal) ||
            asset.Source.Kind != RetailAssetSourceKind.ZipPak)
        {
            throw new ArgumentException(
                "The asset does not belong to this ZIP PAK provider.",
                nameof(asset));
        }

        FileInfo file = new(asset.Source.ContainerPath);
        if (!file.Exists ||
            file.Length != asset.Source.SourceLength ||
            file.LastWriteTimeUtc != asset.Source.SourceLastWriteTimeUtc)
        {
            throw new IOException(
                $"PAK '{asset.Source.ContainerPath}' changed after cataloging.");
        }

        FileStream source = new(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            ZipArchive archive = new(
                source,
                ZipArchiveMode.Read,
                leaveOpen: false);
            try
            {
                ZipArchiveEntry entry = archive.GetEntry(asset.Source.EntryPath)
                    ?? throw new IOException(
                        $"PAK entry '{asset.Source.EntryPath}' no longer exists.");
                if (entry.Length != asset.Source.Length ||
                    entry.Length > _maximumEntryBytes)
                {
                    throw new IOException(
                        $"PAK entry '{asset.Source.EntryPath}' changed after cataloging.");
                }

                return ValueTask.FromResult<Stream>(
                    new OwnedZipEntryStream(archive, entry.Open(), entry.Length));
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private sealed class OwnedZipEntryStream : Stream
    {
        private readonly ZipArchive _archive;
        private readonly Stream _entry;
        private bool _disposed;

        public OwnedZipEntryStream(
            ZipArchive archive,
            Stream entry,
            long length)
        {
            _archive = archive;
            _entry = entry;
            Length = length;
        }

        public override bool CanRead => !_disposed && _entry.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int read = _entry.Read(buffer, offset, count);
            Position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int read = await _entry.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            Position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _entry.Dispose();
                _archive.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await _entry.DisposeAsync().ConfigureAwait(false);
                _archive.Dispose();
            }

            _disposed = true;
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
