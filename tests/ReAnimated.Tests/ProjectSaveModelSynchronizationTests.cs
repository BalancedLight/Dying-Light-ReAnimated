using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectSaveModelSynchronizationTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task SaveWaitsForPendingModelIntegrationThenPersistsLatestMetadataAndCoherentRows()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string projectPath = Path.Combine(directory, "project.dlraproj");
            FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-model.fbx");
            Guid modelId = imported.Package.Document.ModelId;
            await using var assets = Assets(directory, "author");
            await using (var viewModel = ViewModel(directory, "author", assets))
            {
                viewModel.Models.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
                    imported, Path.Combine(directory, "model.dlrmodel"),
                    new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid(),
                        PreviewMode = ProjectCustomModelPreviewMode.SourceFbx }));
                viewModel.Models.ModelName = "First model name";
                viewModel.Models.ResourceName = "generic_model";
                viewModel.ProjectPath = projectPath;
                Guid projectId = viewModel.CurrentProject.ProjectId;

                FieldInfo refreshField = typeof(MainWindowViewModel).GetField("_modelsTargetRefreshTask",
                    BindingFlags.Instance | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException("The pending model refresh task field is unavailable.");
                FieldInfo sourceField = typeof(MainWindowViewModel).GetField("_modelsIntegrationSource",
                    BindingFlags.Instance | BindingFlags.NonPublic) ??
                    throw new InvalidOperationException("The model integration cancellation field is unavailable.");
                (sourceField.GetValue(viewModel) as CancellationTokenSource)?.Cancel();
                Task prior = Assert.IsAssignableFrom<Task>(refreshField.GetValue(viewModel));
                await prior.WaitAsync(TimeSpan.FromSeconds(30));
                refreshField.SetValue(viewModel, pending.Task);

                Task save = viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
                try
                {
                    Assert.False(save.IsCompleted);
                    Assert.False(File.Exists(projectPath));
                    viewModel.Models.ModelName = "Final model name";
                    Assert.False(save.IsCompleted);
                    Assert.False(File.Exists(projectPath));
                }
                finally { pending.TrySetResult(true); }
                await save.WaitAsync(TimeSpan.FromSeconds(30));
                Task finalRefresh = Assert.IsAssignableFrom<Task>(refreshField.GetValue(viewModel));
                await finalRefresh.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.True(File.Exists(projectPath), viewModel.StatusText);
                Assert.Equal(projectId, viewModel.CurrentProject.ProjectId);
                Assert.Equal("Final model name", Assert.Single(viewModel.CurrentProject.Models).Name);
                Assert.Equal(modelId, viewModel.Models.CaptureProjectSession().Model!.Package.Document.ModelId);
                Assert.False(viewModel.IsDirty);
                Assert.NotEmpty(viewModel.AnimationLibrary);
                Assert.All(viewModel.AnimationLibrary, row =>
                {
                    Assert.NotNull(row);
                    Assert.False(string.IsNullOrWhiteSpace(row.Name));
                });
            }

            DlraProject durable = ProjectSerializer.Load(projectPath);
            Assert.Equal("Final model name", Assert.Single(durable.Models).Name);
            await using var reopenedAssets = Assets(directory, "reopen");
            await using var reopened = ViewModel(directory, "reopen", reopenedAssets);
            await reopened.OpenWorkspaceAsync(projectPath);
            Assert.Equal(durable.ProjectId, reopened.CurrentProject.ProjectId);
            Assert.Equal("Final model name", reopened.Models.ModelName);
            Assert.Equal("Final model name", Assert.Single(reopened.CurrentProject.Models).Name);
            Assert.True(reopened.Models.HasModel);
            Assert.Equal(modelId, reopened.Models.CaptureProjectSession().Model!.Package.Document.ModelId);
            Assert.False(reopened.IsDirty);
            Assert.Equal(durable.AnimationSources.Length, reopened.CurrentProject.AnimationSources.Length);
            Assert.NotEmpty(reopened.AnimationLibrary);
            Assert.All(reopened.AnimationLibrary, row =>
            {
                Assert.NotNull(row);
                Assert.False(string.IsNullOrWhiteSpace(row.Name));
            });
        }
        finally
        {
            pending.TrySetResult(true);
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Dl1AssetWorkspace Assets(string directory, string name) => new(
        Path.Combine(directory, name + ".sqlite3"), Path.Combine(directory, name + "-cache"));

    private static MainWindowViewModel ViewModel(string directory, string name, Dl1AssetWorkspace assets) => new(
        new JsonWorkspaceStateStore(Path.Combine(directory, name + "-state.json")),
        new NoProjectPicker(), assets);

    private sealed class NoProjectPicker : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            throw new InvalidOperationException("The test supplies the project path explicitly.");

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) =>
            throw new InvalidOperationException("The test supplies the project path explicitly.");
    }
}
