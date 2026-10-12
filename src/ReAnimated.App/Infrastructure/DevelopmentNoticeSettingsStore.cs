using System.IO;
using System.Text.Json;

namespace ReAnimated.App.Infrastructure;

public sealed class DevelopmentNoticeSettingsStore
{
    public DevelopmentNoticeSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        SettingsPath = Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public bool IsDismissed()
    {
        try
        {
            return File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<NoticeSettings>(File.ReadAllText(SettingsPath))?.Dismissed == true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public void Dismiss()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new NoticeSettings(true)));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static DevelopmentNoticeSettingsStore CreateDefault() => new(Path.Combine(
        Path.GetDirectoryName(StartWorkflowSettingsStore.CreateDefault().SettingsPath)!,
        "development-notice.json"));

    private sealed record NoticeSettings(bool Dismissed);
}
