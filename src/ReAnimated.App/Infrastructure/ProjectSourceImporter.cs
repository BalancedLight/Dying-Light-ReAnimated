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
    private const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024;

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
}
