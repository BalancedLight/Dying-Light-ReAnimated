using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed record PendingProjectAssetReceipt
{
    public Guid AssetId { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public string ContentSha256 { get; init; } = string.Empty;

    public long Length { get; init; }

    public string StagedFileName { get; init; } = string.Empty;
}

/// <summary>
/// Project assets need portable project-relative identities before an untitled
/// project has a directory. This store owns only recovery copies; a successful
/// project Save materializes the exact bytes at their already-recorded paths.
/// </summary>
public sealed class PendingProjectAssetStore
{
    public const int MaximumReceipts = 64;

    private const int CopyBufferSize = 128 * 1024;
    private readonly string _root;

    public PendingProjectAssetStore(string recoveryFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryFilePath);
        string recoveryDirectory = Path.GetDirectoryName(
                Path.GetFullPath(recoveryFilePath)) ??
            throw new ArgumentException(
                "The recovery file must have a parent directory.",
                nameof(recoveryFilePath));
        _root = Path.Combine(recoveryDirectory, "StagedAssets");
    }

    /// <summary>
    /// Sweeps abandoned atomic-write scratch files from the recovery staging
    /// directory. Best-effort; never throws.
    /// </summary>
    public int SweepStaleTemporaryFiles() =>
        ProjectSourceImporter.SweepStaleTemporaryFiles(_root);

    public async Task<PendingProjectAssetReceipt> StageAsync(
        Guid assetId,
        string relativePath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException(
                "A staged project asset must have a stable identity.",
                nameof(assetId));
        }

        string portablePath = ValidatePortableRelativePath(relativePath);
        if (bytes.Length <= 0 ||
            bytes.Length > CustomModelPackageSerializer.MaximumPackageBytes)
        {
            throw new InvalidDataException(
                $"A staged custom-model asset must be between 1 byte and {CustomModelPackageSerializer.MaximumPackageBytes:N0} bytes.");
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(bytes.Span))
            .ToLowerInvariant();
        string stagedFileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{assetId:N}-{sha256[..16]}.dlrstage");
        Directory.CreateDirectory(_root);
        string destination = GetStagedPath(stagedFileName);
        if (!File.Exists(destination) ||
            !string.Equals(
                await ProjectSourceImporter.ComputeSha256Async(
                    destination,
                    cancellationToken).ConfigureAwait(false),
                sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            await WriteAtomicAsync(
                destination,
                bytes,
                cancellationToken).ConfigureAwait(false);
        }

        return new PendingProjectAssetReceipt
        {
            AssetId = assetId,
            RelativePath = portablePath,
            ContentSha256 = sha256,
            Length = bytes.Length,
            StagedFileName = stagedFileName,
        };
    }

    public async Task<string> ResolveAsync(
        PendingProjectAssetReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ValidateReceipt(receipt);
        string path = GetStagedPath(receipt.StagedFileName);
        FileInfo info = new(path);
        if (!info.Exists || info.Length != receipt.Length)
        {
            throw new FileNotFoundException(
                "The recovery-staged project asset is missing or has a different length.",
                path);
        }

        string hash = await ProjectSourceImporter.ComputeSha256Async(
            path,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                hash,
                receipt.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The recovery-staged project asset failed SHA-256 verification.");
        }

        return path;
    }

    /// <summary>
    /// Copies a staged asset to its project-relative path. Set
    /// <paramref name="replaceExisting"/> when the caller already owns that
    /// path and is deliberately replacing its bytes; otherwise foreign bytes
    /// at the destination are refused so an unrelated file is never clobbered.
    /// </summary>
    public async Task<string> MaterializeAsync(
        PendingProjectAssetReceipt receipt,
        string projectPath,
        bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string source = await ResolveAsync(receipt, cancellationToken)
            .ConfigureAwait(false);
        string projectDirectory = Path.GetDirectoryName(
                Path.GetFullPath(projectPath)) ??
            throw new InvalidOperationException(
                "The project path has no parent directory.");
        string relativePath = ValidatePortableRelativePath(
            receipt.RelativePath);
        string destination = Path.GetFullPath(Path.Combine(
            projectDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureUnderDirectory(projectDirectory, destination);
        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The staged asset destination has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        bool destinationExists = File.Exists(destination);
        if (destinationExists)
        {
            string existing = await ProjectSourceImporter.ComputeSha256Async(
                destination,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(
                    existing,
                    receipt.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                // The exact bytes are already published. Rewriting them would
                // only risk a torn file for no observable change.
                return destination;
            }

            if (!replaceExisting)
            {
                throw new IOException(
                    $"The portable asset path '{relativePath}' already contains different bytes. " +
                    $"Destination '{destination}' holds {existing}; the staged asset is {receipt.ContentSha256}.");
            }
        }

        await CopyAtomicAsync(
                source,
                destination,
                overwrite: destinationExists,
                cancellationToken)
            .ConfigureAwait(false);
        return destination;
    }

    public static ImmutableArray<PendingProjectAssetReceipt> ValidateSnapshotReceipts(
        IEnumerable<PendingProjectAssetReceipt>? receipts)
    {
        PendingProjectAssetReceipt[] bounded = receipts?.ToArray() ?? [];
        if (bounded.Length > MaximumReceipts)
        {
            throw new InvalidDataException(
                $"A recovery snapshot cannot reference more than {MaximumReceipts:N0} staged assets.");
        }

        var seenIds = new HashSet<Guid>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PendingProjectAssetReceipt receipt in bounded)
        {
            ValidateReceipt(receipt);
            if (!seenIds.Add(receipt.AssetId) ||
                !seenPaths.Add(receipt.RelativePath))
            {
                throw new InvalidDataException(
                    "A recovery snapshot contains duplicate staged asset identities or paths.");
            }
        }

        return bounded.ToImmutableArray();
    }

    public void Delete(PendingProjectAssetReceipt receipt)
    {
        ValidateReceipt(receipt);
        string path = GetStagedPath(receipt.StagedFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void ValidateReceipt(PendingProjectAssetReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.AssetId == Guid.Empty ||
            receipt.Length <= 0 ||
            receipt.Length > CustomModelPackageSerializer.MaximumPackageBytes ||
            !IsCanonicalSha256(receipt.ContentSha256))
        {
            throw new InvalidDataException(
                "A recovery-staged asset receipt has invalid bounds or identity fields.");
        }

        _ = ValidatePortableRelativePath(receipt.RelativePath);
        string expectedPrefix = $"{receipt.AssetId:N}-";
        if (string.IsNullOrWhiteSpace(receipt.StagedFileName) ||
            Path.GetFileName(receipt.StagedFileName) != receipt.StagedFileName ||
            !receipt.StagedFileName.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !receipt.StagedFileName.EndsWith(
                ".dlrstage",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A recovery-staged asset receipt has an invalid store filename.");
        }
    }

    private string GetStagedPath(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(_root, fileName));
        EnsureUnderDirectory(_root, path);
        return path;
    }

    private static string ValidatePortableRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.Length > 512 ||
            normalized.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or "..") ||
            !normalized.StartsWith("Sources/", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A staged project asset path must be a portable path under Sources/.");
        }

        return normalized;
    }

    private static bool IsCanonicalSha256(string value) =>
        value?.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void EnsureUnderDirectory(
        string directory,
        string path)
    {
        string root = Path.GetFullPath(directory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A staged project asset escaped its owned directory.");
        }
    }

    private static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task CopyAtomicAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream input = new(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(
                    output,
                    CopyBufferSize,
                    cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
