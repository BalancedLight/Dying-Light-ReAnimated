using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace ReAnimated.App.Infrastructure;

public sealed record ImportedProjectSource(
    string AbsolutePath,
    string ProjectRelativePath,
    string Sha256);

public static class ProjectSourceImporter
{
    /// <summary>
    /// Atomic writes here and in <see cref="PendingProjectAssetStore"/> stage
    /// through a sibling <c>*.tmp</c> file that is removed in their own
    /// <c>finally</c>. A hard kill skips that, so leftovers accumulate beside
    /// real project sources until something sweeps them.
    /// </summary>
    public static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(24);

    private const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumSweptTemporaryFiles = 4096;

    /// <summary>
    /// Best-effort removal of abandoned atomic-write scratch files. Never
    /// throws: a leftover that cannot be deleted right now (still locked by a
    /// concurrent write, or read-only) must not fail an open or a save.
    /// </summary>
    public static int SweepStaleTemporaryFiles(
        string directory,
        TimeSpan? olderThan = null)
    {
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
        {
            return 0;
        }

        TimeSpan threshold = olderThan ?? StaleTemporaryFileAge;
        DateTime cutoffUtc = DateTime.UtcNow - threshold;
        int removed = 0;
        try
        {
            int examined = 0;
            foreach (string path in Directory.EnumerateFiles(
                         directory,
                         "*.tmp",
                         SearchOption.TopDirectoryOnly))
            {
                if (++examined > MaximumSweptTemporaryFiles)
                {
                    break;
                }

                try
                {
                    FileInfo info = new(path);
                    if (!info.Exists ||
                        info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        info.LastWriteTimeUtc > cutoffUtc)
                    {
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
                {
                    // A live write owns this scratch file, or the filesystem
                    // refused the delete. Leave it and keep sweeping.
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
            return removed;
        }

        return removed;
    }

    /// <summary>
    /// Sweeps abandoned scratch files from a project's <c>Sources</c> folder.
    /// </summary>
    public static int SweepStaleProjectSourceTemporaryFiles(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return 0;
        }

        try
        {
            string? projectDirectory = Path.GetDirectoryName(
                Path.GetFullPath(projectPath));
            return string.IsNullOrWhiteSpace(projectDirectory)
                ? 0
                : SweepStaleTemporaryFiles(
                    Path.Combine(projectDirectory, "Sources"));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return 0;
        }
    }

    public static async Task<ImportedProjectSource> ImportAsync(
        string sourcePath,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string fullSource = Path.GetFullPath(sourcePath);
        string fullProject = Path.GetFullPath(projectPath);
        if (!File.Exists(fullSource))
        {
            throw new FileNotFoundException(
                "The animation source was not found.",
                fullSource);
        }

        FileInfo sourceInfo = new(fullSource);
        if (sourceInfo.Length <= 0 || sourceInfo.Length > MaximumSourceBytes)
        {
            throw new InvalidDataException(
                $"Animation sources must be between 1 byte and {MaximumSourceBytes:N0} bytes.");
        }

        string projectDirectory = Path.GetDirectoryName(fullProject)
            ?? throw new InvalidOperationException(
                "The project path has no parent directory.");
        string sourcesDirectory = Path.Combine(
            projectDirectory,
            "Sources");
        Directory.CreateDirectory(sourcesDirectory);

        string sha256 = await ComputeSha256Async(
            fullSource,
            cancellationToken).ConfigureAwait(false);
        string safeName = Path.GetFileName(fullSource);
        string destination = Path.Combine(sourcesDirectory, safeName);
        if (File.Exists(destination))
        {
            string existingHash = await ComputeSha256Async(
                destination,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    existingHash,
                    sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                destination = Path.Combine(
                    sourcesDirectory,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Path.GetFileNameWithoutExtension(safeName)}-{sha256[..12].ToLowerInvariant()}{Path.GetExtension(safeName)}"));
            }
        }

        if (!File.Exists(destination))
        {
            await CopyAtomicAsync(
                fullSource,
                destination,
                cancellationToken).ConfigureAwait(false);
        }

        string relative = Path.GetRelativePath(
                projectDirectory,
                destination)
            .Replace('\\', '/');
        if (Path.IsPathRooted(relative) ||
            relative.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment == ".."))
        {
            throw new InvalidOperationException(
                "The imported animation did not remain inside the project directory.");
        }

        return new ImportedProjectSource(
            destination,
            relative,
            sha256);
    }

    public static async Task<ImportedProjectSource> ImportBytesAsync(
        ReadOnlyMemory<byte> source,
        string sourceFileName,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        // ReadOnlyMemory<T>.Length is Int32-bounded, so it cannot exceed this
        // importer's 2 GiB file limit. Keep the non-empty guard here and let
        // the streaming path enforce the upper bound for larger disk files.
        if (source.Length <= 0)
        {
            throw new InvalidDataException(
                $"Animation and custom-model sources must be between 1 byte and {MaximumSourceBytes:N0} bytes.");
        }

        string safeName = Path.GetFileName(sourceFileName);
        if (string.IsNullOrWhiteSpace(safeName) ||
            safeName is "." or "..")
        {
            throw new InvalidDataException("The source file name is not portable.");
        }

        string fullProject = Path.GetFullPath(projectPath);
        string projectDirectory = Path.GetDirectoryName(fullProject)
            ?? throw new InvalidOperationException(
                "The project path has no parent directory.");
        string sourcesDirectory = Path.Combine(projectDirectory, "Sources");
        Directory.CreateDirectory(sourcesDirectory);

        string sha256 = Convert.ToHexString(SHA256.HashData(source.Span))
            .ToLowerInvariant();
        string destination = Path.Combine(sourcesDirectory, safeName);
        if (File.Exists(destination))
        {
            string existingHash = await ComputeSha256Async(
                destination,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingHash, sha256, StringComparison.OrdinalIgnoreCase))
            {
                destination = Path.Combine(
                    sourcesDirectory,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Path.GetFileNameWithoutExtension(safeName)}-{sha256[..12]}{Path.GetExtension(safeName)}"));
            }
        }

        if (!File.Exists(destination))
        {
            await WriteAtomicAsync(source, destination, cancellationToken)
                .ConfigureAwait(false);
        }

        string relative = Path.GetRelativePath(projectDirectory, destination)
            .Replace('\\', '/');
        if (Path.IsPathRooted(relative) ||
            relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment == ".."))
        {
            throw new InvalidOperationException(
                "The imported source did not remain inside the project directory.");
        }

        return new ImportedProjectSource(destination, relative, sha256);
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task CopyAtomicAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string destinationDirectory =
            Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                "The destination has no parent directory.");
        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream source = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough))
            {
                await source.CopyToAsync(
                    destination,
                    128 * 1024,
                    cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(
                    cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteAtomicAsync(
        ReadOnlyMemory<byte> source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                "The destination has no parent directory.");
        string temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.WriteThrough))
            {
                await destination.WriteAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
