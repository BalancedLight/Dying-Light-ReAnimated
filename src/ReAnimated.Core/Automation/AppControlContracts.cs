namespace ReAnimated.Core.Automation;

public sealed record AppControlInstance(int ProtocolVersion, string InstanceId, int ProcessId,
    long ProcessStartUtcTicks, string ExecutablePath, string PipeName, string AccessToken);

public sealed record AppControlRequest(int ProtocolVersion, string InstanceId,
    long ProcessStartUtcTicks, string AccessToken, string Command, string? OutputPath = null,
    string? ProjectPath = null);

public sealed record AppControlStatus(string InstanceId, int ProcessId, string ProjectName,
    string? ProjectPath, bool IsDirty, bool IsBusy)
{
    public string? ActiveWorkflow { get; init; }
    public string? ActivePage { get; init; }
    public string? GuidedStep { get; init; }
    public Guid? ProjectId { get; init; }
    public Guid? SelectedModelId { get; init; }
    public Guid? SelectedAnimationId { get; init; }
    public Guid? ActiveAnimationId { get; init; }
}

public sealed record AppControlResponse(bool Success, string? Error, AppControlStatus? Status);

public interface IAppControlHost
{
    Task<AppControlStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task SaveAsync(string outputPath, CancellationToken cancellationToken);
    Task OpenAsync(string projectPath, CancellationToken cancellationToken);
    void ScheduleClose();
}

public static class AppControlHandler
{
    public static Task<AppControlResponse> ExecuteAsync(IAppControlHost host,
        string command, string? outputPath, CancellationToken cancellationToken) =>
        ExecuteAsync(host, command, outputPath, null, cancellationToken);

    public static async Task<AppControlResponse> ExecuteAsync(IAppControlHost host,
        string command, string? outputPath, string? projectPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        cancellationToken.ThrowIfCancellationRequested();
        if (command is not ("status" or "save" or "close" or "open"))
            return new(false, "Unknown app command.", null);
        if (command != "open" && projectPath is not null)
            return new(false, "Only open accepts a project path.", null);
        if (command == "open" && outputPath is not null)
            return new(false, "Open does not accept an output path.", null);
        if (command == "status" && outputPath is not null)
            return new(false, "Status does not accept an output path.", null);
        AppControlStatus status = await host.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (command == "status") return new(true, null, status);
        if (status.IsBusy) return new(false, "The app is busy.", status);
        if (command == "open")
        {
            if (status.IsDirty) return new(false, "Save the current project before opening another.", status);
            if (string.IsNullOrWhiteSpace(projectPath))
                return new(false, "Open requires a project path.", status);
            await host.OpenAsync(ValidateExistingProjectPath(projectPath), cancellationToken).ConfigureAwait(false);
            return new(true, null, await host.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }
        if (command == "save" && string.IsNullOrWhiteSpace(outputPath))
            return new(false, "Save requires a new output path.", status);
        if (outputPath is not null)
        {
            string path = ValidateNewOutputPath(outputPath);
            await host.SaveAsync(path, cancellationToken).ConfigureAwait(false);
            status = await host.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (command == "close")
        {
            if (status.IsBusy) return new(false, "The app is busy.", status);
            if (status.IsDirty) return new(false, "Save the project before closing.", status);
            host.ScheduleClose();
        }
        return new(true, null, status);
    }

    public static string ValidateNewOutputPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute output path.");
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".dlraproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The output must be a .dlraproj file.");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("The output already exists. Choose a new path.");
        return fullPath;
    }

    public static string ValidateExistingProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Use an absolute project path.");
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".dlraproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The project must be a .dlraproj file.");
        var file = new FileInfo(fullPath);
        if (!file.Exists) throw new FileNotFoundException("The project file is missing.");
        if (file.Length <= 0 || file.Length > ReAnimated.Core.Project.ProjectSerializer.MaximumProjectBytes)
            throw new InvalidDataException("The project file has an unsupported size.");
        return fullPath;
    }
}
