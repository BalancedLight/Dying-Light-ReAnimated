using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ModelAuthoringHistoryRoutingTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task WindowUndoRedoRoutesNumericBoundsEditsToModelWithoutReplacingProject()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = CreateAssets(directory);
            await using var viewModel = CreateViewModel(directory, assets);
            FbxModelAuthoringImportResult imported = RigConformanceWizardTests.CreateModel();
            viewModel.Models.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
                imported, "model.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
            viewModel.OpenCustomModelAuthoringCommand.Execute(null);
            viewModel.Models.SelectedBone = Assert.Single(viewModel.Models.Bones, b => b.Index == 0);
            Guid projectId = viewModel.CurrentProject.ProjectId;
            Guid modelId = imported.Package.Document.ModelId;

            viewModel.Models.BoneBoundsText = "1 2 3 4 5 6";
            viewModel.Models.ApplyBoneBoundsCommand.Execute(null);
            Dl1AuthoredBoneBounds first = Assert.IsType<Dl1AuthoredBoneBounds>(
                CurrentBounds(viewModel));
            viewModel.Models.BoneBoundsText = "7 8 9 10 11 12";
            viewModel.Models.ApplyBoneBoundsCommand.Execute(null);
            Dl1AuthoredBoneBounds second = Assert.IsType<Dl1AuthoredBoneBounds>(
                CurrentBounds(viewModel));
            Assert.NotEqual(first, second);
            Assert.True(viewModel.UndoCommand.CanExecute(null));

            viewModel.UndoCommand.Execute(null);
            Assert.Equal(first, CurrentBounds(viewModel));
            Assert.Equal(projectId, viewModel.CurrentProject.ProjectId);
            Assert.Equal(modelId, CurrentDocument(viewModel).ModelId);
            Assert.True(viewModel.Models.HasModel);
            Assert.True(viewModel.RedoCommand.CanExecute(null));

            viewModel.RedoCommand.Execute(null);
            Assert.Equal(second, CurrentBounds(viewModel));
            Assert.Equal(projectId, viewModel.CurrentProject.ProjectId);
            Assert.Equal(modelId, CurrentDocument(viewModel).ModelId);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task EmptyModelHistoryNeverConsumesProjectUndoAndProjectUndoWorksOutsideAuthoring()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "project.dlraproj");
            string packRoot = Path.Combine(directory, "packs", "shared");
            Directory.CreateDirectory(packRoot);
            ProjectSerializer.SaveAtomic(DlraProject.Create("History routing"), projectPath);
            await using var assets = CreateAssets(directory);
            await using var viewModel = CreateViewModel(directory, assets, projectPath, packRoot);
            await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
            viewModel.AddAdditionalRpackRootCommand.Execute(null);
            Assert.Single(viewModel.CurrentProject.Dl1Settings.AdditionalRpackRoots);
            Assert.True(viewModel.UndoCommand.CanExecute(null));

            viewModel.OpenCustomModelAuthoringCommand.Execute(null);
            Assert.False(viewModel.Models.UndoHelperEditCommand.CanExecute(null));
            Assert.False(viewModel.UndoCommand.CanExecute(null));
            viewModel.UndoCommand.Execute(null);
            Assert.Single(viewModel.CurrentProject.Dl1Settings.AdditionalRpackRoots);

            viewModel.OpenRetailModelBrowserCommand.Execute(null);
            Assert.True(viewModel.UndoCommand.CanExecute(null));
            viewModel.UndoCommand.Execute(null);
            Assert.Empty(viewModel.CurrentProject.Dl1Settings.AdditionalRpackRoots);
            Assert.True(viewModel.RedoCommand.CanExecute(null));
            viewModel.RedoCommand.Execute(null);
            Assert.Single(viewModel.CurrentProject.Dl1Settings.AdditionalRpackRoots);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Dl1AuthoredBoneBounds? CurrentBounds(MainWindowViewModel viewModel) =>
        CurrentDocument(viewModel).Bones[0].LocalBounds;

    private static CustomModelDocument CurrentDocument(MainWindowViewModel viewModel) =>
        viewModel.Models.CaptureProjectSession().Model!.Package.Document;

    private static Dl1AssetWorkspace CreateAssets(string directory) => new(
        Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache"));

    private static MainWindowViewModel CreateViewModel(string directory, Dl1AssetWorkspace assets,
        string? projectPath = null, string? packRoot = null) => new(
        new JsonWorkspaceStateStore(Path.Combine(directory, "workspace.json")),
        new HistoryDialogs(projectPath, packRoot), assets);

    private sealed class HistoryDialogs(string? projectPath, string? packRoot) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => projectPath;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => projectPath;

        public string? ShowSelectAdditionalRpackRootDialog(string? initialPath) => packRoot;
    }
}
