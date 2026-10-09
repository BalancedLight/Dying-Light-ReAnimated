using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RecoveryModelSynchronizationTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SynchronousRecoveryRefusesToWriteAnOutdatedModelRevision()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "assets.sqlite3"),
                Path.Combine(directory, "cache"));
            var store = new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json"));
            await using var model = new MainWindowViewModel(store, new NoPicker(), assets);
            FieldInfo refresh = PreparePendingModel(model, directory, pending.Task);
            using var autosave = new WorkspaceAutosaveService(model, store);

            try
            {
                Assert.False(autosave.SaveNow("window-closing"));
                Assert.False(store.Exists);
            }
            finally { pending.TrySetResult(true); }
            await ((Task)refresh.GetValue(model)!).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            pending.TrySetResult(true);
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PreparedRecoveryWaitsForPendingIntegrationAndIncludesTheLatestModel()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "assets.sqlite3"),
                Path.Combine(directory, "cache"));
            var store = new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json"));
            await using var model = new MainWindowViewModel(store, new NoPicker(), assets);
            _ = PreparePendingModel(model, directory, pending.Task);
            try
            {
                Task<WorkspaceSnapshot> snapshotTask = model.PrepareSnapshotAsync();
                Assert.False(snapshotTask.IsCompleted);
                model.Models.ModelName = "Latest model edit";
                Assert.False(snapshotTask.IsCompleted);
                pending.TrySetResult(true);
                WorkspaceSnapshot snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal("Latest model edit", Assert.Single(snapshot.Project!.Models).Name);
                Assert.Equal(model.Models.ModelName, Assert.Single(snapshot.Project.Models).Name);
                Assert.NotEmpty(snapshot.PendingAssets);
                Assert.True(snapshot.IsProjectDirty);
                store.Save(snapshot);
                Assert.Equal("Latest model edit", Assert.Single(store.Load()!.Project!.Models).Name);
            }
            finally { pending.TrySetResult(true); }
        }
        finally
        {
            pending.TrySetResult(true);
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static FieldInfo PreparePendingModel(MainWindowViewModel model, string directory, Task pending)
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-model.fbx");
        model.Models.CommitProjectRestore(new PreparedModelsWorkspaceRestore(imported,
            Path.Combine(directory, "model.dlrmodel"), new ProjectModelsWorkspaceState
            {
                PackageAssetId = Guid.NewGuid(),
                PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
            }));
        FieldInfo refresh = typeof(MainWindowViewModel).GetField("_modelsTargetRefreshTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        refresh.SetValue(model, pending);
        model.Models.ModelName = "Pending model edit";
        return refresh;
    }

    private sealed class NoPicker : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
