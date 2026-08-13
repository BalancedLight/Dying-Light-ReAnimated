using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ReAnimated.Tests;

public sealed class RepositoryHygieneTests
{
    private static readonly Regex AbsoluteWindowsPath = new(
        "(?<![A-Za-z])[A-Za-z]:\\\\",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex UncPath = new(
        "\\\\\\\\[A-Za-z0-9._-]+\\\\",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex HomeDirectoryPath = new(
        "(?:/" + "Users/|/" + "home/)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly string[] PrivateMarkers =
    [
        "Dying" + "LightTools",
        "Dying " + "Light Extraction",
        "Dying" + "LightDebug",
        "windows - " + "no names",
    ];

    [Fact]
    public void TrackedTextDoesNotContainWorkstationSpecificReferences()
    {
        string repository = FindRepositoryRoot();
        string[] tracked = GetTrackedFiles(repository);
        var violations = new List<string>();
        foreach (string relativePath in tracked)
        {
            if (!ShouldScan(relativePath))
            {
                continue;
            }

            string fullPath = Path.Combine(
                repository,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            string text = File.ReadAllText(fullPath);
            if (AbsoluteWindowsPath.IsMatch(text) ||
                UncPath.IsMatch(text) ||
                HomeDirectoryPath.IsMatch(text) ||
                PrivateMarkers.Any(marker => text.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(relativePath);
            }
        }

        Assert.True(
            violations.Count == 0,
            "Tracked text contains workstation-specific paths or private markers: " +
            string.Join(", ", violations));
    }

    private static bool ShouldScan(string relativePath)
    {
        if (relativePath.StartsWith("schemas/", StringComparison.Ordinal) ||
            !(relativePath.StartsWith("docs/", StringComparison.Ordinal) ||
              relativePath.StartsWith("src/", StringComparison.Ordinal) ||
              relativePath.StartsWith("tests/", StringComparison.Ordinal) ||
              relativePath.StartsWith("tools/", StringComparison.Ordinal) ||
              relativePath is "README.md" or ".gitignore"))
        {
            return false;
        }

        string extension = Path.GetExtension(relativePath);
        return extension is ".cs" or ".md" or ".json" or ".ps1" or
            ".props" or ".yml" or ".yaml" or ".xml" or ".txt";
    }

    private static string[] GetTrackedFiles(string repository)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"safe.directory={repository}");
        process.StartInfo.ArgumentList.Add("ls-files");
        process.StartInfo.ArgumentList.Add("-z");
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to list tracked files for repository hygiene: {error.Trim()}");
        }

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the DL ReAnimated repository root for hygiene validation.");
    }
}
