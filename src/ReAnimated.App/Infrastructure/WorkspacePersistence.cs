using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed record WorkspaceSnapshot(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    string? ProjectPath,
    string AssetSearch,
    string? SelectedAssetId,
    string? SelectedBonePath,
    int CurrentFrame,
    bool ViewportsLinked,
    float FppFieldOfView,
    float FppNearPlane,
    string ActiveWorkspaceMode,
    DlraProject? Project = null,
    bool IsProjectDirty = false,
    bool? MeshesVisible = null,
    bool? SkeletonOverlayVisible = null)
{
    public const int CurrentSchemaVersion = 1;
}

public interface IWorkspaceSnapshotProvider
{
    WorkspaceSnapshot CreateSnapshot();

    void RestoreSnapshot(WorkspaceSnapshot snapshot);
}

public sealed class JsonWorkspaceStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public JsonWorkspaceStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    public void Save(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The workspace state path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        AtomicFileWriter.WriteAllText(FilePath, json);
    }

    public WorkspaceSnapshot? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        string json = File.ReadAllText(FilePath);
        WorkspaceSnapshot? snapshot =
            JsonSerializer.Deserialize<WorkspaceSnapshot>(json, SerializerOptions);
        if (snapshot is null
            || snapshot.SchemaVersion != WorkspaceSnapshot.CurrentSchemaVersion)
        {
            return null;
        }

        return snapshot;
    }

    public string BackupCurrent()
    {
        if (!File.Exists(FilePath))
        {
            throw new FileNotFoundException(
                "The recovery snapshot no longer exists.",
                FilePath);
        }

        string directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException(
                "The workspace state path has no parent directory.");
        string backupDirectory = Path.Combine(
            directory,
            "Backups");
        string backupPath = Path.Combine(
            backupDirectory,
            $"{Path.GetFileNameWithoutExtension(FilePath)}.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}.json");
        AtomicFileWriter.WriteAllText(
            backupPath,
            File.ReadAllText(FilePath));
        return backupPath;
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }

    }
}

public sealed class WorkspaceAutosaveService : IDisposable
{
    private readonly IWorkspaceSnapshotProvider _snapshotProvider;
    private readonly JsonWorkspaceStateStore _store;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public WorkspaceAutosaveService(
        IWorkspaceSnapshotProvider snapshotProvider,
        JsonWorkspaceStateStore store,
        TimeSpan? interval = null)
    {
        _snapshotProvider = snapshotProvider
            ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timer = new DispatcherTimer(
            interval ?? TimeSpan.FromSeconds(30),
            DispatcherPriority.Background,
            OnAutosaveTick,
            Dispatcher.CurrentDispatcher);
    }

    public string AutosavePath => _store.FilePath;

    public event EventHandler<AutosaveCompletedEventArgs>? AutosaveCompleted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _timer.Start();
    }

    public void Stop()
    {
        if (!_disposed)
        {
            _timer.Stop();
        }
    }

    public bool SaveNow(string reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            WorkspaceSnapshot snapshot = _snapshotProvider.CreateSnapshot();
            _store.Save(snapshot);
            AutosaveCompleted?.Invoke(
                this,
                new AutosaveCompletedEventArgs(true, reason, snapshot.SavedAt, null));
            return true;
        }
        catch (Exception exception)
        {
            AutosaveCompleted?.Invoke(
                this,
                new AutosaveCompletedEventArgs(
                    false,
                    reason,
                    DateTimeOffset.UtcNow,
                    exception.Message));
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnAutosaveTick;
    }

    private void OnAutosaveTick(object? sender, EventArgs args)
    {
        _ = SaveNow("interval");
    }
}

public sealed class AutosaveCompletedEventArgs : EventArgs
{
    public AutosaveCompletedEventArgs(
        bool succeeded,
        string reason,
        DateTimeOffset timestamp,
        string? error)
    {
        Succeeded = succeeded;
        Reason = reason;
        Timestamp = timestamp;
        Error = error;
    }

    public bool Succeeded { get; }

    public string Reason { get; }

    public DateTimeOffset Timestamp { get; }

    public string? Error { get; }
}
