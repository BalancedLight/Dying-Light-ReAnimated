using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed class ProjectAssetSaveTransaction : IAsyncDisposable
{
    private sealed record AssetCopy(string Source, string SourceRoot, string Destination, string Sha256);
    private readonly string _destinationRoot;
    private readonly List<AssetCopy> _copies;
    private readonly List<AssetCopy> _created = [];
    private readonly List<FileStream> _destinationLocks = [];
    private bool _committed;

    private ProjectAssetSaveTransaction(DlraProject project, string destinationRoot, List<AssetCopy> copies)
    {
        Project = project;
        _destinationRoot = destinationRoot;
        _copies = copies;
    }

    public DlraProject Project { get; }

    public static async Task<ProjectAssetSaveTransaction> PrepareAsync(DlraProject project,
        string? sourceProjectPath, string destinationProjectPath, PendingProjectAssetStore store,
        IReadOnlyDictionary<Guid, PendingProjectAssetReceipt> pendingAssets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pendingAssets);
        string destinationRoot = Path.GetDirectoryName(Path.GetFullPath(destinationProjectPath))!;
        string? sourceRoot = sourceProjectPath is null ? null
            : Path.GetDirectoryName(Path.GetFullPath(sourceProjectPath));
        var copies = new List<AssetCopy>();
        var assets = ImmutableArray.CreateBuilder<ProjectAssetReference>();
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectAssetReference asset in project.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.Kind == ProjectAssetKind.RetailGameResource)
            {
                assets.Add(asset);
                continue;
            }
            string destination = ResolveUnderRoot(destinationRoot, asset.RelativePath);
            string source;
            string assetSourceRoot;
            if (pendingAssets.TryGetValue(asset.Id, out PendingProjectAssetReceipt? receipt))
            {
                if (asset.RelativePath != receipt.RelativePath ||
                    !string.Equals(asset.ContentSha256, receipt.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The pending asset disagrees with the project identity.");
                source = await store.ResolveAsync(receipt, cancellationToken).ConfigureAwait(false);
                assetSourceRoot = Path.GetDirectoryName(source)!;
            }
            else
            {
                if (sourceRoot is null) throw new FileNotFoundException("A project source asset is unavailable.");
                source = ResolveUnderRoot(sourceRoot, asset.RelativePath);
                assetSourceRoot = sourceRoot;
            }
            ProjectSourceImporter.ValidateAssetFileSize(source);
            string hash = await ProjectSourceImporter.ComputeSha256Async(source, cancellationToken).ConfigureAwait(false);
            if (asset.ContentSha256 is not null &&
                !string.Equals(hash, asset.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A project source asset failed its content check.");
            if (destinations.TryGetValue(destination, out string? priorHash))
            {
                if (!string.Equals(hash, priorHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Project assets conflict at one destination path.");
            }
            else
            {
                if (Directory.Exists(destination)) throw new IOException("A destination asset path is a directory.");
                if (File.Exists(destination) && !string.Equals(
                        await ProjectSourceImporter.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false),
                        hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A destination asset contains different bytes.");
                destinations.Add(destination, hash);
                copies.Add(new(source, assetSourceRoot, destination, hash));
            }
            assets.Add(asset with { ContentSha256 = hash });
        }
        return new(project with { Assets = assets.ToImmutable() }, destinationRoot, copies);
    }

    public async Task MaterializeAsync(CancellationToken cancellationToken)
    {
        foreach (AssetCopy copy in _copies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(copy.SourceRoot, copy.Source);
            EnsureNoReparsePoints(_destinationRoot, copy.Destination);
            if (await ProjectSourceImporter.CopyVerifiedAssetAsync(copy.Source, copy.Destination,
                    copy.Sha256, cancellationToken).ConfigureAwait(false)) _created.Add(copy);
            EnsureNoReparsePoints(_destinationRoot, copy.Destination);
            var destinationLock = new FileStream(copy.Destination, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _destinationLocks.Add(destinationLock);
            string destinationHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(destinationLock,
                cancellationToken).ConfigureAwait(false));
            if (!string.Equals(destinationHash, copy.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A destination asset changed during the save.");
        }
    }

    public void Commit() => _committed = true;

    public async ValueTask DisposeAsync()
    {
        foreach (FileStream stream in _destinationLocks) await stream.DisposeAsync().ConfigureAwait(false);
        _destinationLocks.Clear();
        GC.SuppressFinalize(this);
        if (_committed) return;
        foreach (AssetCopy copy in _created.AsEnumerable().Reverse())
        {
            try
            {
                EnsureNoReparsePoints(_destinationRoot, copy.Destination);
                if (File.Exists(copy.Destination) && string.Equals(
                        await ProjectSourceImporter.ComputeSha256Async(copy.Destination, CancellationToken.None)
                            .ConfigureAwait(false), copy.Sha256, StringComparison.OrdinalIgnoreCase))
                    File.Delete(copy.Destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') ||
            relativePath.Split(['/', '\\']).Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException("An asset path must stay inside its project directory.");
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An asset path escapes its project directory.");
        EnsureNoReparsePoints(root, path);
        return path;
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        string current = root;
        foreach (string part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Prepend(string.Empty))
        {
            if (part.Length > 0) current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Project asset paths cannot traverse a link or junction.");
        }
    }
}
