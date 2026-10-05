using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReAnimated.Core.Automation;

public static class AppControlTransport
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 16 * 1024;
    public static TimeSpan RequestTimeout => TimeSpan.FromMinutes(2);
    public static string DiscoveryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DLReAnimated", "AppControl");

    public static AppControlInstance CreateInstance()
    {
        using Process process = Process.GetCurrentProcess();
        string instanceId = Guid.NewGuid().ToString("N");
        return new(ProtocolVersion, instanceId, process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            Environment.ProcessPath ?? throw new InvalidOperationException("App executable unavailable."),
            "DLReAnimated.App." + instanceId, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public static bool MatchesProcessGeneration(AppControlInstance instance, int processId,
        long startUtcTicks, string executablePath) =>
        instance.ProtocolVersion == ProtocolVersion && instance.ProcessId == processId &&
        instance.ProcessStartUtcTicks == startUtcTicks &&
        string.Equals(instance.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase);

    public static bool IsLive(AppControlInstance instance)
    {
        if (!IsValidInstance(instance)) return false;
        try
        {
            using Process process = Process.GetProcessById(instance.ProcessId);
            return !process.HasExited && MatchesProcessGeneration(instance, process.Id,
                process.StartTime.ToUniversalTime().Ticks, process.MainModule?.FileName ?? string.Empty);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsValidInstance(AppControlInstance instance) =>
        instance.ProtocolVersion == ProtocolVersion && instance.ProcessId > 0 &&
        instance.ProcessStartUtcTicks > 0 && Guid.TryParseExact(instance.InstanceId, "N", out _) &&
        instance.PipeName == "DLReAnimated.App." + instance.InstanceId &&
        instance.AccessToken is { Length: 64 } && instance.AccessToken.All(char.IsAsciiHexDigit) &&
        !string.IsNullOrWhiteSpace(instance.ExecutablePath) && Path.IsPathFullyQualified(instance.ExecutablePath);

    public static string Publish(AppControlInstance instance)
    {
        Directory.CreateDirectory(DiscoveryDirectory);
        string path = Path.Combine(DiscoveryDirectory, instance.InstanceId + ".json");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, instance);
        return path;
    }

    public static IReadOnlyList<AppControlInstance> Discover()
    {
        if (!Directory.Exists(DiscoveryDirectory)) return [];
        var instances = new List<AppControlInstance>();
        foreach (string path in Directory.EnumerateFiles(DiscoveryDirectory, "*.json").Take(256))
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > MaximumMessageBytes) continue;
                AppControlInstance? instance = JsonSerializer.Deserialize<AppControlInstance>(file);
                if (instance is not null && IsLive(instance) &&
                    Path.GetFileName(path) == instance.InstanceId + ".json") instances.Add(instance);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException)
            {
                // Entries can disappear while another app exits.
            }
        }
        return instances;
    }

    public static Task<AppControlResponse> SendAsync(AppControlInstance instance,
        string command, string? outputPath, CancellationToken cancellationToken) =>
        SendAsync(instance, command, outputPath, null, cancellationToken);

    public static async Task<AppControlResponse> SendAsync(AppControlInstance instance,
        string command, string? outputPath, string? projectPath, CancellationToken cancellationToken)
    {
        if (!IsLive(instance)) throw new IOException("The app instance is no longer available.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        await using var pipe = new NamedPipeClientStream(".", instance.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
            if (!IsLive(instance)) throw new IOException("The app instance changed.");
            await WriteAsync(pipe, new AppControlRequest(ProtocolVersion, instance.InstanceId,
                instance.ProcessStartUtcTicks, instance.AccessToken, command, outputPath, projectPath), timeout.Token)
                .ConfigureAwait(false);
            return await ReadAsync<AppControlResponse>(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new IOException("The app is disconnected.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The app request timed out. Check app status before retrying.", exception);
        }
    }

    public static bool Authenticate(AppControlInstance instance, AppControlRequest request)
    {
        if (request.ProtocolVersion != ProtocolVersion || request.InstanceId != instance.InstanceId ||
            request.ProcessStartUtcTicks != instance.ProcessStartUtcTicks ||
            request.AccessToken is null || request.AccessToken.Length != instance.AccessToken.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(request.AccessToken),
            System.Text.Encoding.ASCII.GetBytes(instance.AccessToken));
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > MaximumMessageBytes) throw new InvalidDataException("App message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new InvalidDataException("Invalid app message size.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidDataException("Empty app message.");
    }
}
