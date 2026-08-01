using System.Runtime.CompilerServices;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Providers;

public sealed class LooseFileAssetProvider :
    IRetailAssetProvider,
    IRetailAssetSnapshotProvider
{
    private readonly string _root;
    private readonly HashSet<string> _extensions;
    private readonly int _priority;
    private readonly long _maximumFileBytes;
    private readonly string _installId;

    public LooseFileAssetProvider(
        string providerId,
        string root,
        IEnumerable<string> extensions,
        int priority,
        long maximumFileBytes = 256L * 1024 * 1024,
        string? installId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        ProviderId = providerId;
        _root = Path.GetFullPath(root);
        _extensions = extensions
            .Select(static extension =>
                extension.StartsWith('.')
                    ? extension
                    : string.Concat(".", extension))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _priority = priority;
        _maximumFileBytes = maximumFileBytes;
        _installId = string.IsNullOrWhiteSpace(installId)
            ? RetailAssetIdentity.CreateInstallId(_root)
            : installId;
    }

    public string ProviderId { get; }

    public async ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        bool rootExists = Directory.Exists(_root);
        RetailAssetRootSnapshot[] roots =
        [
            new(
                0,
                "loose-file-root",
                _root,
                rootExists),
        ];
        List<RetailAssetSourceSnapshot> sources = [];
        if (rootExists)
        {
            string[] paths = Directory.EnumerateFiles(
                    _root,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    _extensions.Contains(Path.GetExtension(path)))
                .OrderBy(
                    static path => path,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (int index = 0; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(paths[index]);
                if (file.Length < 0 || file.Length > _maximumFileBytes)
                {
                    continue;
                }

                sources.Add(
                    await RetailAssetSnapshotCapture.CaptureFileAsync(
                        sources.Count,
                        RetailAssetSourceKind.LooseFile,
                        _priority,
                        file.FullName,
                        cancellationToken).ConfigureAwait(false));
            }
        }

        string configurationFingerprint =
            RetailAssetSnapshotCapture.CreateConfigurationFingerprint(
                ProviderId,
                _installId,
                _root.ToUpperInvariant(),
                _priority,
                _maximumFileBytes,
                string.Join(
                    "\n",
                    _extensions.OrderBy(
                        static extension => extension,
                        StringComparer.OrdinalIgnoreCase)));
        return new RetailAssetProviderSnapshot(
            ProviderId,
            nameof(LooseFileAssetProvider),
            _installId,
            configurationFingerprint,
            roots,
            sources);
    }

    public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        long sourceIndex = 0;
        foreach (string path in Directory.EnumerateFiles(
                     _root,
                     "*",
                     SearchOption.AllDirectories)
                     .OrderBy(
                         static path => path,
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_extensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            FileInfo file = new(path);
            if (file.Length < 0 || file.Length > _maximumFileBytes)
            {
                continue;
            }

            long currentSourceIndex = sourceIndex++;
            string relative = Path.GetRelativePath(_root, file.FullName)
                .Replace('\\', '/');
            RetailAssetLogicalId logicalId;
            try
            {
                logicalId = RetailAssetLogicalId.VirtualFile(relative);
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
                    _priority,
                    RetailAssetIdentity.CreateSourceFingerprint(
                        file.FullName.ToUpperInvariant(),
                        file.Length,
                        file.LastWriteTimeUtc)),
                relative,
                new RetailAssetSource(
                    ProviderId,
                    RetailAssetSourceKind.LooseFile,
                    _priority,
                    file.FullName,
                    relative,
                    null,
                    file.Length,
                    file.Length,
                    file.LastWriteTimeUtc));
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
            asset.Source.Kind != RetailAssetSourceKind.LooseFile)
        {
            throw new ArgumentException(
                "The asset does not belong to this loose-file provider.",
                nameof(asset));
        }

        FileInfo file = new(asset.Source.ContainerPath);
        if (!file.Exists ||
            file.Length != asset.Source.SourceLength ||
            file.LastWriteTimeUtc != asset.Source.SourceLastWriteTimeUtc)
        {
            throw new IOException(
                $"Loose asset '{asset.Source.ContainerPath}' changed after cataloging.");
        }

        Stream stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }
}
