using Microsoft.Win32;
using System.Text;

namespace ReAnimated.DL1.Assets.Discovery;

public sealed record Dl1InstallLocation(
    string InstallPath,
    string SteamLibraryPath,
    string Source,
    bool IsValid,
    string? Diagnostic)
{
    public string DataPath => Path.Combine(InstallPath, "DW", "Data");
}

public static class SteamInstallDiscovery
{
    public const int DyingLightAppId = 239140;

    public static IReadOnlyList<Dl1InstallLocation> Discover(
        IEnumerable<string>? additionalSteamRoots = null,
        IEnumerable<string>? explicitInstallPaths = null)
    {
        HashSet<string> steamRoots =
            new(StringComparer.OrdinalIgnoreCase);
        if (additionalSteamRoots is not null)
        {
            foreach (string root in additionalSteamRoots)
            {
                AddPath(steamRoots, root);
            }
        }

        AddDefaultSteamRoots(steamRoots);
        HashSet<string> libraries =
            new(steamRoots, StringComparer.OrdinalIgnoreCase);
        foreach (string steamRoot in steamRoots.ToArray())
        {
            string libraryFile = Path.Combine(
                steamRoot,
                "steamapps",
                "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            try
            {
                string content = File.ReadAllText(libraryFile);
                foreach ((string key, string value) in ParseQuotedPairs(content))
                {
                    if (key.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                        IsLegacyLibraryPair(key, value))
                    {
                        AddPath(libraries, value);
                    }
                }
            }
            catch (IOException)
            {
                // A locked or partially updated VDF should not suppress other roots.
            }
            catch (UnauthorizedAccessException)
            {
                // A protected library should not suppress other roots.
            }
        }

        List<Dl1InstallLocation> result = [];
        HashSet<string> seenInstalls =
            new(StringComparer.OrdinalIgnoreCase);
        if (explicitInstallPaths is not null)
        {
            foreach (string explicitPath in explicitInstallPaths)
            {
                AddInstall(
                    result,
                    seenInstalls,
                    explicitPath,
                    string.Empty,
                    "explicit");
            }
        }

        foreach (string library in libraries.OrderBy(
                     static path => path,
                     StringComparer.OrdinalIgnoreCase))
        {
            string steamApps = Path.Combine(library, "steamapps");
            string manifest = Path.Combine(
                steamApps,
                $"appmanifest_{DyingLightAppId}.acf");
            string? installDirectory = null;
            if (File.Exists(manifest))
            {
                try
                {
                    string content = File.ReadAllText(manifest);
                    installDirectory = ParseQuotedPairs(content)
                        .FirstOrDefault(static pair =>
                            pair.Key.Equals(
                                "installdir",
                                StringComparison.OrdinalIgnoreCase))
                        .Value;
                }
                catch (IOException)
                {
                    // The conventional fallback remains useful.
                }
                catch (UnauthorizedAccessException)
                {
                    // The conventional fallback remains useful.
                }
            }

            installDirectory = string.IsNullOrWhiteSpace(installDirectory)
                ? "Dying Light"
                : installDirectory;
            AddInstall(
                result,
                seenInstalls,
                Path.Combine(steamApps, "common", installDirectory),
                library,
                File.Exists(manifest)
                    ? "steam-manifest"
                    : "steam-conventional");
        }

        return result
            .OrderByDescending(static location => location.IsValid)
            .ThenBy(static location =>
                location.InstallPath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLegacyLibraryPair(
        string key,
        string value)
    {
        // Old libraryfolders.vdf files represented roots as
        // "1" "D:\\SteamLibrary". Modern files use nested "path" fields and
        // also contain numeric app IDs and byte counts. The lightweight token
        // reader cannot infer brace nesting, so only fully qualified numeric
        // values can be legacy library roots.
        return int.TryParse(
                   key,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out _) &&
               Path.IsPathFullyQualified(value);
    }

    public static bool IsDyingLightInstall(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            string fullPath = Path.GetFullPath(path);
            return File.Exists(Path.Combine(
                       fullPath,
                       "DyingLightGame.exe")) &&
                   File.Exists(Path.Combine(
                       fullPath,
                       "DW",
                       "Data0.pak")) &&
                   Directory.Exists(Path.Combine(
                       fullPath,
                       "DW",
                       "Data"));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static void AddDefaultSteamRoots(HashSet<string> roots)
    {
        string? programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            AddPath(roots, Path.Combine(programFilesX86, "Steam"));
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ReadRegistryPath(
            roots,
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamPath");
        ReadRegistryPath(
            roots,
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            @"Software\Valve\Steam",
            "InstallPath");
        ReadRegistryPath(
            roots,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"Software\WOW6432Node\Valve\Steam",
            "InstallPath");
    }

    private static void ReadRegistryPath(
        HashSet<string> roots,
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using RegistryKey baseKey =
                RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(subKey);
            if (key?.GetValue(valueName) is string value)
            {
                AddPath(roots, value);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            // Registry discovery is one of several independent probes.
        }
    }

    private static void AddInstall(
        List<Dl1InstallLocation> result,
        HashSet<string> seen,
        string path,
        string library,
        string source)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                path.Replace(
                    "\\\\",
                    "\\",
                    StringComparison.Ordinal));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return;
        }

        if (!seen.Add(fullPath))
        {
            return;
        }

        bool valid = IsDyingLightInstall(fullPath);
        result.Add(new Dl1InstallLocation(
            fullPath,
            library,
            source,
            valid,
            valid
                ? null
                : "Expected DyingLightGame.exe, DW\\Data0.pak, and DW\\Data."));
    }

    private static void AddPath(HashSet<string> paths, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(
                value
                    .Replace("\\\\", "\\", StringComparison.Ordinal)
                    .Replace('/', Path.DirectorySeparatorChar));
            paths.Add(fullPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            // Ignore malformed discovery hints.
        }
    }

    private static List<(string Key, string Value)> ParseQuotedPairs(
        string text)
    {
        List<(string Key, string Value)> pairs = [];
        int cursor = 0;
        while (cursor < text.Length)
        {
            int quote = text.IndexOf('"', cursor);
            if (quote < 0)
            {
                break;
            }

            cursor = quote;
            if (!TryReadQuotedToken(text, ref cursor, out string key))
            {
                break;
            }

            while (cursor < text.Length &&
                   char.IsWhiteSpace(text[cursor]))
            {
                cursor++;
            }

            // Object keys are followed by '{'; scalar properties are followed
            // directly by another quoted token. Keeping that distinction
            // prevents nested VDF object names from shifting all later pairs.
            if (cursor >= text.Length || text[cursor] != '"')
            {
                continue;
            }

            if (!TryReadQuotedToken(text, ref cursor, out string value))
            {
                break;
            }

            pairs.Add((key, value));
        }

        return pairs;
    }

    private static bool TryReadQuotedToken(
        string text,
        ref int cursor,
        out string token)
    {
        if (cursor >= text.Length || text[cursor] != '"')
        {
            token = string.Empty;
            return false;
        }

        StringBuilder builder = new();
        cursor++;
        while (cursor < text.Length)
        {
            char value = text[cursor++];
            if (value == '"')
            {
                token = builder.ToString();
                return true;
            }

            if (value == '\\' &&
                cursor < text.Length &&
                text[cursor] is '\\' or '"')
            {
                value = text[cursor++];
            }

            builder.Append(value);
        }

        token = string.Empty;
        return false;
    }
}
