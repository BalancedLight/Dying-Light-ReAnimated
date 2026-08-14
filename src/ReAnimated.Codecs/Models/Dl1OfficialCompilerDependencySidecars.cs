using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1OfficialCompilerDependencySidecar(
    string ObjectFileName,
    string SidecarFileName,
    string Path,
    string Sha256);

public static class Dl1OfficialCompilerDependencySidecarCodec
{
    public const long MaximumSidecarBytes = 16L * 1024L * 1024L;

    public static async Task<ImmutableArray<Dl1OfficialCompilerDependencySidecar>>
        PublishEmittedAsync(
            IEnumerable<string> compiledObjectPaths,
            string outputDirectory,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compiledObjectPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        RejectSidecarReparsePoint(output);
        var published = ImmutableArray.CreateBuilder<Dl1OfficialCompilerDependencySidecar>();
        foreach (string objectPathValue in compiledObjectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(objectPathValue);
            string objectPath = Path.GetFullPath(objectPathValue);
            if (!File.Exists(objectPath))
            {
                throw new FileNotFoundException(
                    "Compiled object is missing while checking its dependency sidecar.",
                    objectPath);
            }

            RejectSidecarReparsePoint(objectPath);
            string source = objectPath + "_dep";
            if (!File.Exists(source))
            {
                continue;
            }

            RejectSidecarReparsePoint(source);
            var info = new FileInfo(source);
            if (info.Length is <= 0 or > MaximumSidecarBytes)
            {
                throw new InvalidDataException(
                    $"Compiler dependency sidecar '{info.Name}' must contain between 1 and " +
                    $"{MaximumSidecarBytes:N0} bytes.");
            }

            string expectedName = Path.GetFileName(objectPath) + "_dep";
            if (!string.Equals(info.Name, expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Compiler dependency sidecar '{info.Name}' does not exactly match '{expectedName}'.");
            }

            string destination = Path.Combine(output, expectedName);
            await PublishSidecarAtomicallyAsync(source, destination, cancellationToken).ConfigureAwait(false);
            string sourceHash = await HashSidecarAsync(source, cancellationToken).ConfigureAwait(false);
            string destinationHash = await HashSidecarAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Published dependency sidecar '{expectedName}' failed SHA-256 validation.");
            }

            published.Add(new Dl1OfficialCompilerDependencySidecar(
                Path.GetFileName(objectPath),
                expectedName,
                destination,
                destinationHash));
        }

        return published.ToImmutable();
    }

    private static async Task<string> HashSidecarAsync(
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
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task PublishSidecarAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        string temporary = destination + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream input = new(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
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

    private static void RejectSidecarReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Compiler dependency sidecar handling refuses reparse point '{path}'.");
        }
    }
}
