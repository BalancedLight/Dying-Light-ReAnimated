using System.Globalization;
using System.Text.Json;
using ReAnimated.Core.Automation;

namespace ReAnimated.Cli;

internal static class AppControlCommand
{
    public static async Task<int> RunAsync(string[] args, JsonSerializerOptions options,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (args.Length == 0) throw new ArgumentException("Usage: app <list|status|open|save|close>");
        string command = args[0].ToLowerInvariant();
        if (command == "list")
        {
            if (args.Length != 1) throw new ArgumentException("Usage: app list");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                format = "dl-reanimated-app-list-v1",
                instances = AppControlTransport.Discover().Select(instance => new
                {
                    instance.InstanceId, instance.ProcessId, instance.ProcessStartUtcTicks,
                    instance.ExecutablePath,
                }),
            }, options));
            return 0;
        }
        if (command is not ("status" or "save" or "close" or "open"))
            throw new ArgumentException("Unknown app command.");
        int? processId = null;
        string? instanceId = null;
        string? outputPath = null;
        string? projectPath = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            string option = args[index];
            if (!seen.Add(option)) throw new ArgumentException("Duplicate app option.");
            if (index + 1 >= args.Length) throw new ArgumentException("An app option is missing its value.");
            string value = args[index + 1];
            switch (option)
            {
                case "--pid":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                            out int parsed) || parsed <= 0)
                        throw new ArgumentException("Invalid app process ID.");
                    processId = parsed;
                    break;
                case "--instance":
                    if (!Guid.TryParseExact(value, "N", out _))
                        throw new ArgumentException("Invalid app instance ID.");
                    instanceId = value;
                    break;
                case "--output":
                    outputPath = AppControlHandler.ValidateNewOutputPath(value);
                    break;
                case "--project":
                    projectPath = AppControlHandler.ValidateExistingProjectPath(value);
                    break;
                default:
                    throw new ArgumentException("Unknown app option.");
            }
        }
        if (processId.HasValue == (instanceId is not null))
            throw new ArgumentException("Select one app with --pid or --instance.");
        if (command == "status" && outputPath is not null)
            throw new ArgumentException("Status does not accept --output.");
        if (command == "save" && outputPath is null)
            throw new ArgumentException("Save requires --output with a new .dlraproj path.");
        if (command == "open" && projectPath is null)
            throw new ArgumentException("Open requires --project with an existing .dlraproj path.");
        if (command == "open" && outputPath is not null)
            throw new ArgumentException("Open does not accept --output.");
        if (command != "open" && projectPath is not null)
            throw new ArgumentException("Only open accepts --project.");
        AppControlInstance[] matching = AppControlTransport.Discover()
            .Where(instance => processId.HasValue ? instance.ProcessId == processId.Value
                : string.Equals(instance.InstanceId, instanceId, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1) throw new IOException("The app instance is unavailable.");
        AppControlResponse response = await AppControlTransport.SendAsync(matching[0], command,
            outputPath, projectPath, token).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(AppControlResponseReport.Create(command, response), options));
        return response.Success ? 0 : 2;
    }
}

public sealed record AppControlResponseReport(
    string Format,
    string Command,
    bool Success,
    string? Error,
    AppControlStatus? Status,
    bool CloseScheduled)
{
    public static AppControlResponseReport Create(string command, AppControlResponse response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(response);
        return new("dl-reanimated-app-response-v1", command, response.Success, response.Error,
            response.Status, command == "close" && response.Success);
    }
}
