using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class RecoveryCloseTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public async Task EmptyWindowClosesWhenRecoveryIsPendingOrUnavailable(bool pendingRecovery, bool changeWorkspace)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        ReAnimated.App.ViewModels.MainWindowViewModel? owner = null;
        try
        {
            WpfTestDispatcher.Run(() =>
            {
                string recoveryDirectory = directory;
                if (!pendingRecovery)
                {
                    recoveryDirectory = Path.Combine(directory, "blocked");
                    File.WriteAllText(recoveryDirectory, "Existing file");
                }
                var store = new JsonWorkspaceStateStore(Path.Combine(recoveryDirectory, "recovery.json"));
                if (pendingRecovery) store.Save(new ControlledSnapshots().CreateSnapshot());
                byte[]? previousRecovery = pendingRecovery ? File.ReadAllBytes(store.FilePath) : null;
                owner = new ReAnimated.App.ViewModels.MainWindowViewModel(store,
                    new WindowsProjectFileDialogService(),
                    new Dl1AssetWorkspace(
                        Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache")));
                Assert.Equal(!pendingRecovery, owner.CanSaveWorkspaceSnapshot);
                Assert.False(owner.HasAppControlUnsavedChanges);
                if (changeWorkspace)
                {
                    owner.ActiveWorkspaceMode = "Retarget/Edit";
                    Assert.True(owner.HasAppControlUnsavedChanges);
                }
                Assert.False(owner.RequiresRecoverySaveOnClose);
                using var autosave = new WorkspaceAutosaveService(owner, store);
                var window = new ReAnimated.App.MainWindow(owner, autosave,
                    new EditorDockLayoutSettingsStore(Path.Combine(directory, "layout")))
                { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var frame = new System.Windows.Threading.DispatcherFrame();
                Exception? failure = null;
                bool closed = false;
                System.Windows.Threading.DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
                { failure = args.Exception; args.Handled = true; frame.Continue = false; };
                dispatcher.UnhandledException += handler;
                window.Closed += (_, _) => { closed = true; frame.Continue = false; };
                var timeout = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) => frame.Continue = false;
                try
                {
                    window.Show();
                    window.Close();
                    timeout.Start();
                    if (!closed) System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                finally
                {
                    timeout.Stop();
                    dispatcher.UnhandledException -= handler;
                    if (!closed) window.Close();
                }
                Assert.Null(failure);
                Assert.True(closed);
                if (pendingRecovery) Assert.Equal(previousRecovery, File.ReadAllBytes(store.FilePath));
                else Assert.Equal("Existing file", File.ReadAllText(recoveryDirectory));
            });
        }
        finally
        {
            if (owner is not null) await WpfTestDispatcher.Run(() => owner.DisposeAsync().AsTask());
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task UnsavedAnimationLibraryStillRequiresRecoveryBeforeClosing()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = new Dl1AssetWorkspace(
                Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache"));
            await using var owner = new ReAnimated.App.ViewModels.MainWindowViewModel(
                new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json")),
                new WindowsProjectFileDialogService(), assets);
            owner.RestoreSnapshot(owner.CreateSnapshot() with
            {
                Project = owner.CurrentProject with
                {
                    AnimationLibraries = [new ReAnimated.Core.Project.ProjectAnimationLibrary
                    { ResourceName = "motion", DisplayName = "Motion" }],
                },
                IsProjectDirty = true,
            });

            Assert.True(owner.HasAppControlUnsavedChanges);
            Assert.True(owner.RequiresRecoverySaveOnClose);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task AsynchronousWriteFailurePreservesRecoveryAndAllowsRetry()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string path = Path.Combine(directory, "recovery.json");
        try
        {
            var provider = new ControlledSnapshots();
            var store = new JsonWorkspaceStateStore(path);
            store.Save(provider.CreateSnapshot());
            byte[] previous = File.ReadAllBytes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            using var autosave = new WorkspaceAutosaveService(provider, store);
            int failures = 0;
            autosave.AutosaveCompleted += (_, result) => { if (!result.Succeeded) failures++; };
            provider.Frame = 50;
            Assert.False(await autosave.SaveNowAsync("closing"));
            Assert.Equal(1, failures);
            Assert.Equal(previous, File.ReadAllBytes(path));
            File.SetAttributes(path, FileAttributes.Normal);
            Assert.True(await autosave.SaveNowAsync("retry"));
            Assert.Equal(50, store.Load()!.CurrentFrame);
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public async Task DispatcherEmergencySaveWaitsForTheLatestSnapshotDuringIntervalSave()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var provider = new ControlledSnapshots { HoldFirst = true };
        Task<bool>? intervalSave = null;
        try
        {
            var store = new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json"));
            using var autosave = new WorkspaceAutosaveService(provider, store);
            intervalSave = autosave.SaveNowAsync("interval");
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Frame = 88;

            Task emergencySave = WpfTestDispatcher.Run(() =>
            {
                var app = (ReAnimated.App.App)System.Windows.Application.Current;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var field = typeof(ReAnimated.App.App).GetField("_autosave", flags)!;
                object? previous = field.GetValue(app);
                field.SetValue(app, autosave);
                try
                {
                    return app.TryEmergencySaveAsync();
                }
                finally { field.SetValue(app, previous); }
            });

            Assert.False(emergencySave.IsCompleted);
            provider.Release.TrySetResult();
            await emergencySave.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await intervalSave.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(88, store.Load()!.CurrentFrame);
        }
        finally
        {
            provider.Release.TrySetResult();
            if (intervalSave is not null)
                _ = await intervalSave.WaitAsync(TimeSpan.FromSeconds(10));
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentAutosavesSerializePreparationAndWriteTheLatestRevisionLast()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var provider = new ControlledSnapshots { HoldFirst = true };
        try
        {
            var store = new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json"));
            using var autosave = new WorkspaceAutosaveService(provider, store);
            Task<bool> first = autosave.SaveNowAsync("interval");
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.Frame = 88;
            Task<bool> second = autosave.SaveNowAsync("closing");
            Assert.Equal(1, provider.PreparedCount);
            Assert.False(second.IsCompleted);
            provider.Release.TrySetResult();
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(2, provider.PreparedCount);
            Assert.Equal(88, store.Load()!.CurrentFrame);
        }
        finally
        {
            provider.Release.TrySetResult();
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private sealed class ControlledSnapshots : IWorkspaceSnapshotProvider
    {
        public int Frame { get; set; } = 12;
        public bool HoldFirst { get; init; }
        public int PreparedCount { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WorkspaceSnapshot CreateSnapshot() => new(WorkspaceSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow, null, string.Empty, null, null, Frame, false, 60, 0.1f, "Models");
        public async Task<WorkspaceSnapshot> PrepareSnapshotAsync(CancellationToken cancellationToken = default)
        {
            WorkspaceSnapshot snapshot = CreateSnapshot();
            PreparedCount++;
            if (HoldFirst && PreparedCount == 1)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }
        public void RestoreSnapshot(WorkspaceSnapshot snapshot) => Frame = snapshot.CurrentFrame;
    }
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ClosingWaitsForADurableRecoveryWrite()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> closing = RequestClose(() => saved.Task, "Cancel", () => Task.FromResult(false));
        Assert.False(closing.IsCompleted);
        saved.SetResult(true);
        Assert.True(await closing);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CancelKeepsTheWindowOpenWhenRecoveryFails()
    {
        Assert.False(await RequestClose(() => Task.FromResult(false), "Cancel", () => Task.FromResult(false)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RetryClosesOnlyAfterAWriteSucceeds()
    {
        int attempt = 0;
        Assert.True(await RequestClose(() => Task.FromResult(++attempt == 2), "Retry",
            () => Task.FromResult(false)));
        Assert.Equal(2, attempt);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SavingElsewhereMustSucceedBeforeClosing()
    {
        Assert.True(await RequestClose(() => Task.FromResult(false), "SaveElsewhere", () => Task.FromResult(true)));
    }

    [Fact]
    public async Task FailedAlternateSaveReturnsToTheChoiceAndCanCancel()
    {
        int choices = 0;
        bool closed = await RecoveryCloseCoordinator.TryCloseAsync(() => Task.FromResult(false),
            () => ++choices == 1 ? RecoveryCloseDecision.SaveElsewhere : RecoveryCloseDecision.Cancel,
            () => Task.FromResult(false));
        Assert.False(closed);
        Assert.Equal(2, choices);
    }

    private static Task<bool> RequestClose(Func<Task<bool>> save, string decision, Func<Task<bool>> saveElsewhere)
    {
        return RecoveryCloseCoordinator.TryCloseAsync(save,
            () => Enum.Parse<RecoveryCloseDecision>(decision), saveElsewhere);
    }
}
