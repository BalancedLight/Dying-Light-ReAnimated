using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ReAnimated.App.Infrastructure;

public sealed class CrashReporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _crashDirectory;

    public CrashReporter(string crashDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(crashDirectory);
        _crashDirectory = Path.GetFullPath(crashDirectory);
    }

    public string WriteReport(
        Exception exception,
        string source,
        string? recoveryFile)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Directory.CreateDirectory(_crashDirectory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string processId = Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string fileName =
            $"ReAnimated-{now:yyyyMMdd-HHmmss-fff}-p{processId}-{Guid.NewGuid():N}.json";
        string path = Path.Combine(_crashDirectory, fileName);
        CrashEnvelope envelope = new(
            now,
            source,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            Environment.OSVersion.VersionString,
            Environment.Version.ToString(),
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            recoveryFile,
            exception.ToString());
        AtomicFileWriter.WriteAllText(
            path,
            JsonSerializer.Serialize(envelope, SerializerOptions));
        return path;
    }

    private sealed record CrashEnvelope(
        DateTimeOffset Timestamp,
        string Source,
        string ApplicationVersion,
        string OperatingSystem,
        string Runtime,
        int ProcessId,
        int ManagedThreadId,
        string? RecoveryFile,
        string Exception);
}
