using System.IO;
using System.Collections.Immutable;
using System.Security.Cryptography;
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
    bool? SkeletonOverlayVisible = null,
    ImmutableArray<PendingProjectAssetReceipt> PendingAssets = default,
    bool? KeepFramed = null,
    ImmutableArray<PendingSecondaryMotionEdit> PendingSecondaryMotionEdits = default,
    double? PositionFrame = null)
{
    public const int CurrentSchemaVersion = 4;

    public const int LegacySchemaVersion = 1;
}

public sealed record WorkspaceSnapshotRead(
    WorkspaceSnapshot Snapshot,
    string ContentSha256);

public interface IWorkspaceSnapshotProvider
{
    /// <summary>Defers autosave while an existing recovery snapshot awaits an explicit decision.</summary>
    bool CanSaveWorkspaceSnapshot => true;

    WorkspaceSnapshot CreateSnapshot();

    Task<WorkspaceSnapshot> PrepareSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CreateSnapshot());
    }

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
        WorkspaceSnapshot normalized = snapshot with
        {
            SchemaVersion = WorkspaceSnapshot.CurrentSchemaVersion,
            PendingAssets = PendingProjectAssetStore.ValidateSnapshotReceipts(
                snapshot.PendingAssets.IsDefault
                    ? null
                    : snapshot.PendingAssets),
            PendingSecondaryMotionEdits = PendingSecondaryMotionEdit.ValidateSnapshotEdits(
                snapshot.PendingSecondaryMotionEdits.IsDefault
                    ? null
                    : snapshot.PendingSecondaryMotionEdits),
        };
        string json = JsonSerializer.Serialize(normalized, SerializerOptions);
        AtomicFileWriter.WriteAllText(FilePath, json);
    }

    public WorkspaceSnapshot? Load() => LoadWithFingerprint()?.Snapshot;

    public WorkspaceSnapshotRead? LoadWithFingerprint()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(FilePath);
        WorkspaceSnapshot? snapshot =
            JsonSerializer.Deserialize<WorkspaceSnapshot>(bytes, SerializerOptions);
        // Every schema from the legacy one up to the current is readable:
        // each field added since has been optional. Rejecting the immediately
        // previous version silently discarded a recoverable session and then
        // let the next autosave overwrite it.
        if (snapshot is null ||
            snapshot.SchemaVersion < WorkspaceSnapshot.LegacySchemaVersion ||
            snapshot.SchemaVersion > WorkspaceSnapshot.CurrentSchemaVersion)
        {
            return null;
        }

        return new WorkspaceSnapshotRead(
            snapshot with
            {
                SchemaVersion = WorkspaceSnapshot.CurrentSchemaVersion,
                PendingAssets = snapshot.PendingAssets.IsDefault
                    ? []
                    : snapshot.PendingAssets,
                PendingSecondaryMotionEdits = PendingSecondaryMotionEdit.ValidateSnapshotEdits(
                    snapshot.PendingSecondaryMotionEdits.IsDefault
                        ? null
                        : snapshot.PendingSecondaryMotionEdits),
            },
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public void RequireContentHash(string expectedSha256)
    {
        if (expectedSha256 is null ||
            expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Expected recovery content hash must be a SHA-256 hex digest.",
                nameof(expectedSha256));
        }
        if (!File.Exists(FilePath))
        {
            throw new IOException(
                "The recovery snapshot was removed in another window; reopen before choosing Restore or Dismiss.");
        }

        string currentSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(FilePath)));
        if (!string.Equals(currentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The recovery snapshot changed in another window; reopen before choosing Restore or Dismiss. No snapshot was deleted.");
        }
    }

    public void DeleteIfUnchanged(string expectedSha256)
    {
        RequireContentHash(expectedSha256);
        File.Delete(FilePath);
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
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
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
        if (!_snapshotProvider.CanSaveWorkspaceSnapshot) return false;
        if (!_saveGate.Wait(0)) return false;
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
        finally { _saveGate.Release(); }
    }

    public async Task<bool> SaveNowAsync(string reason, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_snapshotProvider.CanSaveWorkspaceSnapshot) return false;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        bool acquired = false;
        try
        {
            await _saveGate.WaitAsync(linked.Token);
            acquired = true;
            if (!_snapshotProvider.CanSaveWorkspaceSnapshot) return false;
            WorkspaceSnapshot snapshot = await _snapshotProvider.PrepareSnapshotAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (!_snapshotProvider.CanSaveWorkspaceSnapshot) return false;
            await Task.Run(() => _store.Save(snapshot), linked.Token);
            AutosaveCompleted?.Invoke(this, new AutosaveCompletedEventArgs(true, reason, snapshot.SavedAt, null));
            return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            AutosaveCompleted?.Invoke(this, new AutosaveCompletedEventArgs(false, reason,
                DateTimeOffset.UtcNow, exception.Message));
            return false;
        }
        finally
        {
            if (acquired) _saveGate.Release();
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
        _shutdown.Cancel();
        _ = DisposeSaveResourcesAsync();
    }

    private async Task DisposeSaveResourcesAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        _saveGate.Dispose();
        _shutdown.Dispose();
    }

    private async void OnAutosaveTick(object? sender, EventArgs args)
    {
        if (!_disposed) _ = await SaveNowAsync("interval");
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
