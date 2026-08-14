using System.IO;
using System.Text.Json;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Machine-local retarget review preferences. The toggle is intentionally
/// excluded from portable projects: enabling it authorizes only an explicit
/// Apply assisted review action and never edits a project on load.
/// </summary>
public sealed class AssistedReviewSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public AssistedReviewSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public bool LoadEnabled()
    {
        if (!File.Exists(_settingsPath))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(_settingsPath);
            if (info.Length is <= 0 or > 16 * 1024)
            {
                return false;
            }

            SettingsPayload? payload = JsonSerializer.Deserialize<SettingsPayload>(
                File.ReadAllText(_settingsPath),
                SerializerOptions);
            return payload?.Enabled ?? false;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
            return false;
        }
    }

    public void SaveEnabled(bool enabled)
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The assisted-review settings path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(
            new SettingsPayload(enabled),
            SerializerOptions);
        AtomicFileWriter.WriteAllText(_settingsPath, json);
    }

    public static AssistedReviewSettingsStore CreateDefault() =>
        new(Path.Combine(
            AppPaths.CreateDefault().RootDirectory,
            "Settings",
            "retarget-review.json"));

    private sealed record SettingsPayload(bool Enabled);
}
