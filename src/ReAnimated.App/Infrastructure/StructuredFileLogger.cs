using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ReAnimated.App.Infrastructure;

public enum AppLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

public sealed class StructuredFileLogger : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly FileStream _stream;
    private bool _disposed;

    public StructuredFileLogger(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        string fullDirectory = Path.GetFullPath(logDirectory);
        Directory.CreateDirectory(fullDirectory);
        string fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-p{Environment.ProcessId}.ndjson");
        FilePath = Path.Combine(fullDirectory, fileName);
        _stream = new FileStream(
            FilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            16 * 1024,
            FileOptions.WriteThrough);
    }

    public string FilePath { get; }

    public void Write(
        AppLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, string>? properties = null,
        Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = new LogEntry(
            DateTimeOffset.UtcNow,
            level,
            eventName,
            message,
            Environment.ProcessId,
            Environment.CurrentManagedThreadId,
            properties,
            exception?.ToString());
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            entry,
            SerializerOptions);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Write(json);
            _stream.WriteByte((byte)'\n');
            _stream.Flush(flushToDisk: level >= AppLogLevel.Error);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
        }
    }

    private sealed record LogEntry(
        DateTimeOffset Timestamp,
        AppLogLevel Level,
        string Event,
        string Message,
        int ProcessId,
        int ManagedThreadId,
        IReadOnlyDictionary<string, string>? Properties,
        string? Exception);
}
