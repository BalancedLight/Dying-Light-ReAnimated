using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ReAnimated.Codecs.Fed;

namespace ReAnimated.App.Infrastructure;

/// <summary>Opt-in executable discovery for the external, headless DyingAudio module.</summary>
public sealed record SpeechInspectionToolSettings
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    public string PythonExecutable { get; init; } = string.Empty;
    public string? ModuleSearchPath { get; init; }

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReAnimated", "speech-preview.json");

    public static SpeechInspectionToolSettings? Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<SpeechInspectionToolSettings>(File.ReadAllText(SettingsPath)) : null;

    public void Save()
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, WriteOptions));
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PythonExecutable) || !Path.IsPathFullyQualified(PythonExecutable) || !File.Exists(PythonExecutable))
            throw new InvalidOperationException("Choose the Python executable that has DyingAudio installed.");
        if (ModuleSearchPath is not null && (!Path.IsPathFullyQualified(ModuleSearchPath) || !Directory.Exists(ModuleSearchPath)))
            throw new InvalidOperationException("DyingAudio module search directory does not exist.");
    }
}

public static class SpeechInspectionTool
{
    public static async Task<SpeechCurveExchange> InspectAsync(string spbPath, SpeechInspectionToolSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings.Validate();
        string output = Path.Combine(Path.GetTempPath(), $"speech-exchange-{Guid.NewGuid():N}.json");
        ProcessStartInfo start = new(settings.PythonExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (string argument in new[] { "-m", "dyingaudio.speech_cli", "inspect", Path.GetFullPath(spbPath), "--output", output })
            start.ArgumentList.Add(argument);
        if (settings.ModuleSearchPath is not null) start.Environment["PYTHONPATH"] = settings.ModuleSearchPath;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Speech inspector could not start.");
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            await stdout;
            string message = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Speech inspector failed: {message}");
            return SpeechCurveExchangeReader.Read(output);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
    }
}
