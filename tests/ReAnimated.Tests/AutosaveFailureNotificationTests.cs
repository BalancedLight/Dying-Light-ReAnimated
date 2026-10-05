using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class AutosaveFailureNotificationTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "Recovery")]
    public async Task RepeatedBackgroundFailuresStayNonmodalAndSuccessAllowsOneLaterDiagnostic()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var store = new JsonWorkspaceStateStore(Path.Combine(directory, "recovery", "workspace.json"));
            await using var assets = new Dl1AssetWorkspace(
                Path.Combine(directory, "index.sqlite3"), Path.Combine(directory, "cache"));
            var dialogs = new CountingDialogs();
            await using var workspace = new MainWindowViewModel(store, dialogs, assets, new NullFingerprintService());
            workspace.RestoreSnapshot(workspace.CreateSnapshot() with { IsProjectDirty = true });
            Assert.True(workspace.IsDirty);
            int initialDiagnostics = workspace.Diagnostics.Count;

            workspace.NotifyAutosave(new(false, "interval", DateTimeOffset.UnixEpoch, "disk full"));
            Assert.True(workspace.IsDirty);
            Assert.Equal(0, dialogs.FailureCount);
            Assert.Contains("disk full", workspace.StatusText, StringComparison.Ordinal);
            Assert.Contains("unsaved edits remain in memory", workspace.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(initialDiagnostics + 1, workspace.Diagnostics.Count);
            Assert.Equal("Autosave", workspace.Diagnostics[0].Area);
            Assert.Equal("disk full", workspace.Diagnostics[0].Detail);

            workspace.NotifyAutosave(new(false, "interval", DateTimeOffset.UnixEpoch.AddSeconds(30), "still full"));
            Assert.True(workspace.IsDirty);
            Assert.Equal(0, dialogs.FailureCount);
            Assert.Contains("still full", workspace.StatusText, StringComparison.Ordinal);
            Assert.Equal(initialDiagnostics + 1, workspace.Diagnostics.Count);

            workspace.NotifyAutosave(new(true, "interval", DateTimeOffset.UnixEpoch.AddMinutes(1), null));
            Assert.True(workspace.IsDirty);
            Assert.Contains("Recovery autosaved", workspace.StatusText, StringComparison.Ordinal);
            Assert.Equal(0, dialogs.FailureCount);

            workspace.NotifyAutosave(new(false, "interval", DateTimeOffset.UnixEpoch.AddMinutes(2), "disk full again"));
            Assert.True(workspace.IsDirty);
            Assert.Equal(0, dialogs.FailureCount);
            Assert.Contains("disk full again", workspace.StatusText, StringComparison.Ordinal);
            Assert.Equal(initialDiagnostics + 2, workspace.Diagnostics.Count);
            Assert.Equal(2, workspace.Diagnostics.Count(row => row.Area == "Autosave"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private sealed class CountingDialogs : IProjectFileDialogService
    {
        public int FailureCount { get; private set; }
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public void ShowOperationFailure(string title, string summary, string details) => FailureCount++;
    }

    private sealed class NullFingerprintService : IDl1InstalledBuildFingerprintService
    {
        public Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Dl1InstalledBuildFingerprint?>(null);

        public Task<Dl1InstalledBuildFingerprint> ReadAsync(string installPath,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Dl1InstalledBuildFingerprint>(new FileNotFoundException());
    }
}
