using System.Runtime.CompilerServices;

namespace ReAnimated.Tests;

/// <summary>
/// Locates the source checkout for tests whose output directory is separate
/// from the repository. This class is test-only and has no machine-specific
/// defaults.
/// </summary>
internal static class TestRepositoryPaths
{
    internal const string RepositoryRootEnvironmentVariable =
        "DLR_TEST_REPOSITORY_ROOT";

    private const string RepositoryMarker = "DLReAnimated.slnx";

    internal static string FindRepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        string? configuredRoot = Environment.GetEnvironmentVariable(
            RepositoryRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            string normalized = Path.GetFullPath(
                configuredRoot.Trim().Trim('"'));
            if (!IsRepositoryRoot(normalized))
            {
                throw new DirectoryNotFoundException(
                    $"{RepositoryRootEnvironmentVariable} must name a directory containing {RepositoryMarker}.");
            }

            return normalized;
        }

        var visited = new HashSet<string>(PathComparer);
        foreach (string start in new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(sourceFile) ?? string.Empty,
        })
        {
            if (string.IsNullOrWhiteSpace(start))
            {
                continue;
            }

            string fullStart = Path.GetFullPath(start);
            for (DirectoryInfo? directory = new DirectoryInfo(fullStart);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (!visited.Add(directory.FullName))
                {
                    continue;
                }

                if (IsRepositoryRoot(directory.FullName))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the DL ReAnimated repository root. Set {RepositoryRootEnvironmentVariable} to a directory containing {RepositoryMarker}.");
    }

    internal static string FindRepositoryFile(
        string[] relativeSegments,
        [CallerFilePath] string sourceFile = "")
    {
        ArgumentNullException.ThrowIfNull(relativeSegments);
        if (relativeSegments.Length == 0 ||
            relativeSegments.Any(string.IsNullOrWhiteSpace) ||
            relativeSegments.Any(Path.IsPathRooted))
        {
            throw new ArgumentException(
                "Repository file paths must contain non-empty relative segments.",
                nameof(relativeSegments));
        }

        string root = FindRepositoryRoot(sourceFile);
        string candidate = Path.GetFullPath(Path.Combine(
            [root, .. relativeSegments]));
        string rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, PathComparison) ||
            !File.Exists(candidate))
        {
            throw new FileNotFoundException(
                $"Could not locate repository file '{Path.Combine(relativeSegments)}' under '{root}'.",
                candidate);
        }

        return candidate;
    }

    private static bool IsRepositoryRoot(string path) =>
        File.Exists(Path.Combine(path, RepositoryMarker));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}