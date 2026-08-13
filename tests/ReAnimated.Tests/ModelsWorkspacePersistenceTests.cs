using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspacePersistenceTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ProjectSaveAndOpenRestoresOwnedCustomModelWithoutOriginalFbx()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string projectPath = Path.Combine(directory, "portable-model.dlraproj");
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "portable_character.fbx");
        var restoredState = new ProjectModelsWorkspaceState
        {
            PackageAssetId = Guid.NewGuid(),
            PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
            ShowMeshes = false,
            ShowBones = true,
            ShowHelpers = false,
            ShowCameraHelpers = true,
            ShowPropHelpers = false,
        };

        await using (var firstAssets = new Dl1AssetWorkspace(
                         Path.Combine(directory, "first-assets.sqlite3"),
                         Path.Combine(directory, "first-cache")))
        await using (var first = new MainWindowViewModel(
                         new JsonWorkspaceStateStore(
                             Path.Combine(directory, "first-recovery.json")),
                         new ProjectPathDialogs(projectPath),
                         firstAssets))
        {
            first.Models.CommitProjectRestore(
                new PreparedModelsWorkspaceRestore(
                    imported,
                    Path.Combine(directory, "not-an-original-fbx.dlrmodel"),
                    restoredState));
            first.Models.ModelName = "Portable character";
            first.Models.ResourceName = "portable_character";
            first.Models.CharacterId = "portable_character_id";
            first.Models.AnimationScriptAlias = "PortableLibrary";

            await first.SaveWorkspaceCommand.ExecuteAsync(null);

            Assert.True(File.Exists(projectPath), first.StatusText);
            Assert.NotNull(first.CurrentProject.ModelsWorkspace);
        }

        DlraProject saved = ProjectSerializer.Load(projectPath);
        ProjectModelsWorkspaceState savedState =
            Assert.IsType<ProjectModelsWorkspaceState>(
                saved.ModelsWorkspace);
        ProjectAssetReference packageAsset = Assert.Single(
            saved.Assets,
            asset => asset.Id == savedState.PackageAssetId);
        Assert.Equal(
            ProjectAssetKind.CustomModelSource,
            packageAsset.Kind);
        Assert.False(Path.IsPathRooted(packageAsset.RelativePath));
        string packagePath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            packageAsset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(packagePath));
        Assert.EndsWith(
            ".dlrmodel",
            packagePath,
            StringComparison.OrdinalIgnoreCase);

        await using var secondAssets = new Dl1AssetWorkspace(
            Path.Combine(directory, "second-assets.sqlite3"),
            Path.Combine(directory, "second-cache"));
        await using var second = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(directory, "second-recovery.json")),
            new ProjectPathDialogs(projectPath),
            secondAssets);

        await second.OpenWorkspaceCommand.ExecuteAsync(null);

        Assert.True(second.Models.HasModel, second.StatusText);
        Assert.Equal("Portable character", second.Models.ModelName);
        Assert.Equal("portable_character", second.Models.ResourceName);
        Assert.Equal("portable_character_id", second.Models.CharacterId);
        Assert.Equal("PortableLibrary", second.Models.AnimationScriptAlias);
        Assert.False(second.Models.ShowMeshes);
        Assert.True(second.Models.ShowBones);
        Assert.False(second.Models.ShowHelpers);
        Assert.True(second.Models.ShowCameraHelpers);
        Assert.False(second.Models.ShowPropHelpers);
        Assert.Equal(
            CustomModelPreviewMode.SourceFbx,
            second.Models.SelectedPreviewMode.Mode);
        Assert.Equal("Models", second.ActiveWorkspaceMode);
        Assert.Equal(
            imported.Package.Document.ModelId,
            second.Models.CaptureProjectSession()
                .Model!
                .Package
                .Document
                .ModelId);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task FailedPackagePreparationRetainsActiveModelsWorkspaceSession()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string corruptPackagePath = Path.Combine(directory, "broken.dlrmodel");
        await File.WriteAllTextAsync(corruptPackagePath, "not a custom-model package");

        using var viewModel = new ModelsWorkspaceViewModel(
            new NullProjectFileDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null);
        var state = new ProjectModelsWorkspaceState
        {
            PackageAssetId = Guid.NewGuid(),
            PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
            ShowMeshes = true,
            ShowBones = false,
            ShowHelpers = false,
            ShowCameraHelpers = true,
            ShowPropHelpers = false,
        };
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: true),
            Path.Combine(directory, "active.dlrmodel"),
            state));
        ModelsWorkspaceSessionSnapshot before = viewModel.CaptureProjectSession();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            viewModel.PrepareProjectRestoreAsync(
                corruptPackagePath,
                state,
                CancellationToken.None));

        ModelsWorkspaceSessionSnapshot after = viewModel.CaptureProjectSession();
        Assert.NotNull(before.Model);
        Assert.NotNull(after.Model);
        Assert.Equal(
            before.Model.Package.Document.ModelId,
            after.Model.Package.Document.ModelId);
        Assert.Equal(
            before.Model.Package.Document.Source.ContentSha256,
            after.Model.Package.Document.Source.ContentSha256);
        Assert.Equal(
            before.Model.Package.Document.RigSignature,
            after.Model.Package.Document.RigSignature);
        Assert.Equal(before.Model.Surfaces.Length, after.Model.Surfaces.Length);
        Assert.True(before.Model.Package.SourceFbx.SequenceEqual(after.Model.Package.SourceFbx));
        Assert.Equal(before.PackagePath, after.PackagePath);
        Assert.Equal(before.AuthoringRevision, after.AuthoringRevision);
        Assert.Equal(before.PreviewMode, after.PreviewMode);
        Assert.Equal(before.ShowMeshes, after.ShowMeshes);
        Assert.Equal(before.ShowBones, after.ShowBones);
        Assert.Equal(before.ShowHelpers, after.ShowHelpers);
        Assert.Equal(before.ShowCameraHelpers, after.ShowCameraHelpers);
        Assert.Equal(before.ShowPropHelpers, after.ShowPropHelpers);
        Assert.True(viewModel.HasModel);
        Assert.Equal("Synthetic preview model", viewModel.ModelName);
    }

    private sealed class NullProjectFileDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    private sealed class ProjectPathDialogs(string projectPath) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            projectPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;
    }
}
