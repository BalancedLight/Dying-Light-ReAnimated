using System.IO;
using System.Text.Json;

namespace ReAnimated.App.Infrastructure;

public sealed class BlenderExecutableResolver
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public BlenderExecutableResolver(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public string? Resolve()
    {
        foreach (string? candidate in EnumerateCandidates())
        {
            if (TryValidate(candidate, out string? resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    public void SaveConfiguredPath(string blenderExecutablePath)
    {
        if (!TryValidate(
                blenderExecutablePath,
                out string? resolved))
        {
            throw new FileNotFoundException(
                "The selected Blender executable does not exist.",
                blenderExecutablePath);
        }

        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The Blender settings path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(
            new BlenderSettings(resolved!),
            SerializerOptions);
        AtomicFileWriter.WriteAllText(_settingsPath, json);
    }

    public string? LoadConfiguredPath()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(_settingsPath);
            if (info.Length > 64 * 1024)
            {
                return null;
            }

            BlenderSettings? settings =
                JsonSerializer.Deserialize<BlenderSettings>(
                    File.ReadAllText(_settingsPath),
                    SerializerOptions);
            return TryValidate(
                settings?.ExecutablePath,
                out string? resolved)
                    ? resolved
                    : null;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException)
        {
            return null;
        }
    }

    public static bool TryValidate(
        string? candidate,
        out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            string path = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    candidate.Trim().Trim('"')));
            if (!File.Exists(path) ||
                !string.Equals(
                    Path.GetFileName(path),
                    "blender.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolved = path;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static BlenderExecutableResolver CreateDefault()
    {
        string root = AppPaths.CreateDefault().RootDirectory;
        return new BlenderExecutableResolver(
            Path.Combine(
                root,
                "Settings",
                "blender.json"));
    }

    private IEnumerable<string?> EnumerateCandidates()
    {
        yield return LoadConfiguredPath();
        yield return Environment.GetEnvironmentVariable(
            "BLENDER_EXECUTABLE");

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (string directory in path.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                yield return Path.Combine(directory, "blender.exe");
            }
        }

        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            yield break;
        }

        string foundation = Path.Combine(
            programFiles,
            "Blender Foundation");
        if (!Directory.Exists(foundation))
        {
            yield break;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(
                    foundation,
                    "Blender *",
                    SearchOption.TopDirectoryOnly)
                .OrderByDescending(
                    static value => value,
                    StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string directory in directories)
        {
            yield return Path.Combine(directory, "blender.exe");
        }
    }

    private sealed record BlenderSettings(string ExecutablePath);
}
