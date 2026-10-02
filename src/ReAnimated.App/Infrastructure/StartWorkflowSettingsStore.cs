using System.IO;
using System.Text.Json;
using ReAnimated.Core.Storage;

namespace ReAnimated.App.Infrastructure;

public enum StartWorkflowMode
{
    AnimationsOnly,
    ModelsOnly,
    ModelsAndAnimations,
}

/// <summary>Stores the user's preferred editor entry workflow independently of project files.</summary>
public sealed class StartWorkflowSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public StartWorkflowSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        SettingsPath = Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public StartWorkflowMode? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            StartWorkflowSettings? settings = JsonSerializer.Deserialize<StartWorkflowSettings>(File.ReadAllText(SettingsPath));
            return settings is not null && Enum.IsDefined(settings.Mode) ? settings.Mode : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(StartWorkflowMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (directory is null) throw new InvalidOperationException("The workflow settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new StartWorkflowSettings(mode), JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static StartWorkflowSettingsStore CreateDefault() => new(Path.Combine(
        Path.GetDirectoryName(LocalApplicationPaths.CreateDefault().RootDirectory)
            ?? throw new InvalidOperationException("The local application-data directory has no parent."),
        "ReAnimated",
        "start-workflow.json"));

    private sealed record StartWorkflowSettings(StartWorkflowMode Mode);
}
