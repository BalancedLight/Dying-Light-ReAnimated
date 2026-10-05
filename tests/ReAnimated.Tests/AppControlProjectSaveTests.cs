using System.Reflection;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AppControlProjectSaveTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ExplicitSaveWaitsForModelIntegrationAndPersistsLatestPackageWithoutPicker()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-model.fbx");
            viewModel.Models.CommitProjectRestore(new PreparedModelsWorkspaceRestore(imported,
                Path.Combine(directory, "model.dlrmodel"), new ProjectModelsWorkspaceState
                {
                    PackageAssetId = Guid.NewGuid(),
                    PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
                }));
            viewModel.Models.ModelName = "Initial model";
            viewModel.Models.ResourceName = "generic_model";
            await InstallPendingRefreshAsync(viewModel, pending.Task);
            string path = Path.Combine(directory, "project.dlraproj");
            Task save = viewModel.SaveWorkspaceToNewPathAsync(path, CancellationToken.None);
            try
            {
                Assert.False(save.IsCompleted);
                Assert.False(File.Exists(path));
                Assert.True(viewModel.IsBusy);
                viewModel.Models.ModelName = "Final model";
            }
            finally { pending.TrySetResult(); }
            await save.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(path, viewModel.ProjectPath);
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsDirty);
            Assert.Equal("Final model", Assert.Single(ProjectSerializer.Load(path).Models).Name);
            Assert.Equal(imported.Package.Document.ModelId,
                viewModel.Models.CaptureProjectSession().Model!.Package.Document.ModelId);
        }
        finally
        {
            pending.TrySetResult();
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ExplicitSaveCancellationWhileWaitingRestoresBusyStateWithoutWriting()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            await InstallPendingRefreshAsync(viewModel, pending.Task);
            string path = Path.Combine(directory, "project.dlraproj");
            Task save = viewModel.SaveWorkspaceToNewPathAsync(path, cancellation.Token);
            Assert.False(save.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
            Assert.False(viewModel.IsBusy);
            Assert.False(File.Exists(path));
            Assert.Null(viewModel.ProjectPath);
            pending.TrySetResult();
        }
        finally
        {
            pending.TrySetResult();
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task CrossDirectorySaveCopiesLocalAssetsAndReopensWithTheirContentIdentity()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            string original = Path.Combine(directory, "project.dlraproj");
            byte[] bytes = [1, 2, 3, 4, 5];
            ProjectAssetReference source = await CreateLocalAssetProjectAsync(original, bytes);
            await viewModel.OpenWorkspaceAsync(original);
            string relocated = Path.Combine(directory, "other", "project.dlraproj");
            await viewModel.SaveWorkspaceToNewPathAsync(relocated, CancellationToken.None);
            Assert.Equal(relocated, viewModel.ProjectPath);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, "Sources", "source.bin")));
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, "other", "Sources", "source.bin")));
            DlraProject saved = ProjectSerializer.Load(relocated);
            Assert.Equal(source.ContentSha256, saved.Assets.Single(asset => asset.Id == source.Id).ContentSha256);
            Assert.Contains(saved.Assets, asset => asset.Kind == ProjectAssetKind.RetailGameResource);
            await viewModel.OpenWorkspaceAsync(relocated);
            Assert.Equal(saved.ProjectId, viewModel.CurrentProject.ProjectId);
            Assert.False(viewModel.IsDirty);
            Assert.False(viewModel.IsBusy);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task DestinationConflictKeepsOriginalProjectAndBothAssetFiles()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string original = Path.Combine(directory, "project.dlraproj");
            byte[] originalBytes = [1, 2, 3];
            await CreateLocalAssetProjectAsync(original, originalBytes);
            byte[] conflict = [4, 5, 6];
            string destinationSource = Path.Combine(directory, "other", "Sources", "source.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationSource)!);
            await File.WriteAllBytesAsync(destinationSource, conflict);
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            await viewModel.OpenWorkspaceAsync(original);
            string output = Path.Combine(directory, "other", "project.dlraproj");
            await Assert.ThrowsAsync<IOException>(() => viewModel.SaveWorkspaceToNewPathAsync(output, CancellationToken.None));
            Assert.Equal(original, viewModel.ProjectPath);
            Assert.False(File.Exists(output));
            Assert.Equal(conflict, File.ReadAllBytes(destinationSource));
            Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(directory, "Sources", "source.bin")));
            Assert.False(viewModel.IsBusy);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ProjectPublicationFailureRollsBackCopiedAssetsAndRetainsCurrentPath()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string original = Path.Combine(directory, "project.dlraproj");
            byte[] sourceBytes = [1, 2, 3];
            await CreateLocalAssetProjectAsync(original, sourceBytes);
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            await viewModel.OpenWorkspaceAsync(original);
            await InstallPendingRefreshAsync(viewModel, pending.Task);
            string output = Path.Combine(directory, "other", "project.dlraproj");
            Task save = viewModel.SaveWorkspaceToNewPathAsync(output, CancellationToken.None);
            Directory.CreateDirectory(output);
            pending.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => save);
            Assert.Equal(original, viewModel.ProjectPath);
            Assert.True(Directory.Exists(output));
            Assert.False(File.Exists(Path.Combine(directory, "other", "Sources", "source.bin")));
            Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(directory, "Sources", "source.bin")));
            Assert.False(viewModel.IsBusy);
        }
        finally
        {
            pending.TrySetResult();
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task<ProjectAssetReference> CreateLocalAssetProjectAsync(string projectPath, byte[] bytes)
    {
        string root = Path.GetDirectoryName(projectPath)!;
        string sourcePath = Path.Combine(root, "Sources", "source.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllBytesAsync(sourcePath, bytes);
        var source = new ProjectAssetReference
        {
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/source.bin",
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        ProjectSerializer.SaveAtomic(DlraProject.Create("Generic project") with
        {
            Assets = [source, new ProjectAssetReference
            {
                Kind = ProjectAssetKind.RetailGameResource,
                RelativePath = "virtual/resource.bin",
            }],
        }, projectPath);
        return source;
    }

    private static async Task InstallPendingRefreshAsync(MainWindowViewModel viewModel, Task pending)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo refresh = typeof(MainWindowViewModel).GetField("_modelsTargetRefreshTask", flags)!;
        FieldInfo source = typeof(MainWindowViewModel).GetField("_modelsIntegrationSource", flags)!;
        (source.GetValue(viewModel) as CancellationTokenSource)?.Cancel();
        await ((Task)refresh.GetValue(viewModel)!).WaitAsync(TimeSpan.FromSeconds(30));
        refresh.SetValue(viewModel, pending);
    }

    private static Dl1AssetWorkspace Assets(string directory) => new(
        Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache"));

    private static MainWindowViewModel ViewModel(string directory, Dl1AssetWorkspace assets) => new(
        new JsonWorkspaceStateStore(Path.Combine(directory, "state.json")), new NoProjectPicker(), assets);

    private sealed class NoProjectPicker : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            throw new InvalidOperationException("The test supplies the project path.");
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) =>
            throw new InvalidOperationException("The test supplies the project path.");
    }
}
