using System.IO;
using System.Text.Json;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Machine-local convenience settings for custom-model deployment. Project
/// roots are deliberately kept out of portable .dlrmodel documents.
/// </summary>
public sealed class CustomModelDeveloperToolsSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public CustomModelDeveloperToolsSettings(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public string? LoadProjectRoot()
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(_settingsPath);
            if (info.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }

            SettingsPayload? payload = JsonSerializer.Deserialize<SettingsPayload>(
                File.ReadAllText(_settingsPath),
                SerializerOptions);
            return TryNormalizeDirectory(payload?.DeveloperToolsProjectRoot, out string? root)
                ? root
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void SaveProjectRoot(string projectRoot)
    {
        if (!TryNormalizeDirectory(projectRoot, out string? root))
        {
            throw new DirectoryNotFoundException(
                "The selected Developer Tools project directory does not exist.");
        }

        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The deployment settings path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(new SettingsPayload(root!), SerializerOptions);
        AtomicFileWriter.WriteAllText(_settingsPath, json);
    }

    public static CustomModelDeveloperToolsSettings CreateDefault()
    {
        string root = AppPaths.CreateDefault().RootDirectory;
        return new CustomModelDeveloperToolsSettings(
            Path.Combine(root, "Settings", "custom-model-deployment.json"));
    }

    private static bool TryNormalizeDirectory(string? candidate, out string? root)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
            if (!Directory.Exists(fullPath))
            {
                return false;
            }

            root = fullPath;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private sealed record SettingsPayload(string DeveloperToolsProjectRoot);
}
