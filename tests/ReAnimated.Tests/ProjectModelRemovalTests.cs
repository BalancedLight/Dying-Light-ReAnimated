using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

/// <summary>
/// Guards removing a model from a project.
/// </summary>
/// <remarks>
/// An animation target variant is defined by the model it retargets onto, so
/// it cannot outlive that model. Everything else the model touches - the
/// immutable source, a shared script, a shared asset - has to survive.
/// </remarks>
public sealed class ProjectModelRemovalTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-ModelRemoval-{Guid.NewGuid():N}");

    private static DlraProject CreateTwoModelProject(
        out Guid removedModelId,
        out Guid keptModelId,
        out Guid removedVariantId,
        out Guid keptVariantId,
        out Guid sourceId)
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid removedAssetId = Guid.NewGuid();
        Guid keptAssetId = Guid.NewGuid();
        Guid removedLibraryId = Guid.NewGuid();
        Guid keptLibraryId = Guid.NewGuid();
        removedModelId = Guid.NewGuid();
        keptModelId = Guid.NewGuid();
        removedVariantId = Guid.NewGuid();
        keptVariantId = Guid.NewGuid();
        sourceId = Guid.NewGuid();
        string removedSignature = new('a', 64);
        string keptSignature = new('b', 64);

        return DlraProject.Create("Removal") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/take.fbx",
                    ContentSha256 = new string('1', 64),
                },
                new ProjectAssetReference
                {
                    Id = removedAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "models/removed.dlrmodel",
                    ContentSha256 = new string('2', 64),
                },
                new ProjectAssetReference
                {
                    Id = keptAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "models/kept.dlrmodel",
                    ContentSha256 = new string('3', 64),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = removedModelId,
                    AssetId = removedAssetId,
                    Name = "Removed",
                    RigSignature = removedSignature,
                    RootAnimationLibraryId = removedLibraryId,
                },
                new ProjectModelEntry
                {
                    Id = keptModelId,
                    AssetId = keptAssetId,
                    Name = "Kept",
                    RigSignature = keptSignature,
                    RootAnimationLibraryId = keptLibraryId,
                },
            ],
            AnimationLibraries =
            [
                new ProjectAnimationLibrary
                {
                    Id = removedLibraryId,
                    ResourceName = "anims_removed",
                    DisplayName = "Removed animations",
                },
                new ProjectAnimationLibrary
                {
                    Id = keptLibraryId,
                    ResourceName = "anims_kept",
                    DisplayName = "Kept animations",
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Take",
                    SourceAssetId = sourceAssetId,
                    RequiresSourceRebind = true,
                    MigrationNote = "Unavailable.",
                    FrameCount = 2,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    Id = removedVariantId,
                    SourceId = sourceId,
                    Name = "Take on removed",
                    TargetModelId = removedModelId,
                    TargetRigId = "removed",
                    TargetRigSignature = removedSignature,
                    BindingMode = ProjectAnimationBindingMode.Retarget,
                    OwningAnimationLibraryId = removedLibraryId,
                    OutputAnm2Name = "take_removed.anm2",
                },
                new ProjectAnimationVariant
                {
                    Id = keptVariantId,
                    SourceId = sourceId,
                    Name = "Take on kept",
                    TargetModelId = keptModelId,
                    TargetRigId = "kept",
                    TargetRigSignature = keptSignature,
                    BindingMode = ProjectAnimationBindingMode.Retarget,
                    OwningAnimationLibraryId = keptLibraryId,
                    OutputAnm2Name = "take_kept.anm2",
                },
            ],
        };
    }

    private async Task<MainWindowViewModel> OpenAsync(
        DlraProject project,
        Dl1AssetWorkspace assets,
        bool confirmRemoval)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            $"removal-{Guid.NewGuid():N}.dlraproj");
        ProjectSerializer.SaveAtomic(project, projectPath);

        var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(
                    _temporaryDirectory,
                    $"state-{Guid.NewGuid():N}.json")),
            new Dialogs(projectPath, confirmRemoval),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        return viewModel;
    }

    [Fact]
    public async Task RemovingAModelTakesItsAnimationTargetsAndScriptWithIt()
    {
        DlraProject project = CreateTwoModelProject(
            out Guid removedModelId,
            out Guid keptModelId,
            out Guid removedVariantId,
            out Guid keptVariantId,
            out Guid sourceId);
        Guid removedAssetId = project.Models.Single(model =>
            model.Id == removedModelId).AssetId;

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "a.sqlite3"),
            Path.Combine(_temporaryDirectory, "a-cache"));
        await using MainWindowViewModel viewModel =
            await OpenAsync(project, assets, confirmRemoval: true);

        viewModel.SelectedProjectModel = viewModel.ProjectModelLibrary
            .Single(model => model.ModelId == removedModelId);
        Assert.True(
            viewModel.RemoveSelectedProjectModelCommand.CanExecute(null));

        viewModel.RemoveSelectedProjectModelCommand.Execute(null);

        Assert.True(
            viewModel.Diagnostics.All(d => d.Severity != "Error"),
            "status=" + viewModel.StatusText + " | " + string.Join(
                " ;; ",
                viewModel.Diagnostics
                    .Where(d => d.Severity == "Error")
                    .Select(d => d.Message + ": " + d.Detail)));

        DlraProject after = viewModel.CurrentProject;
        Assert.DoesNotContain(
            after.Models,
            model => model.Id == removedModelId);
        Assert.DoesNotContain(
            after.AnimationVariants,
            variant => variant.Id == removedVariantId);
        Assert.DoesNotContain(
            after.AnimationLibraries,
            library => library.ResourceName == "anims_removed");
        Assert.DoesNotContain(
            after.Assets,
            asset => asset.Id == removedAssetId);

        // Everything belonging to the other model survives, and so does the
        // immutable source the removed variant was made from.
        Assert.Contains(after.Models, model => model.Id == keptModelId);
        Assert.Contains(
            after.AnimationVariants,
            variant => variant.Id == keptVariantId);
        Assert.Contains(
            after.AnimationLibraries,
            library => library.ResourceName == "anims_kept");
        Assert.Contains(
            after.AnimationSources,
            source => source.Id == sourceId);

        after.Validate();
    }

    [Fact]
    public async Task DecliningTheConfirmationChangesNothing()
    {
        DlraProject project = CreateTwoModelProject(
            out Guid removedModelId,
            out _,
            out Guid removedVariantId,
            out _,
            out _);

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "b.sqlite3"),
            Path.Combine(_temporaryDirectory, "b-cache"));
        await using MainWindowViewModel viewModel =
            await OpenAsync(project, assets, confirmRemoval: false);

        viewModel.SelectedProjectModel = viewModel.ProjectModelLibrary
            .Single(model => model.ModelId == removedModelId);

        viewModel.RemoveSelectedProjectModelCommand.Execute(null);

        Assert.Contains(
            viewModel.CurrentProject.Models,
            model => model.Id == removedModelId);
        Assert.Contains(
            viewModel.CurrentProject.AnimationVariants,
            variant => variant.Id == removedVariantId);
    }

    [Fact]
    public async Task RemovalDropsEveryUnsharedScriptUsedOnlyByTheModel()
    {
        DlraProject project = CreateTwoModelProject(
            out Guid removedModelId,
            out _,
            out Guid removedVariantId,
            out _,
            out _);
        Guid rootLibraryId = project.Models.Single(model =>
            model.Id == removedModelId).RootAnimationLibraryId!.Value;
        Guid secondaryLibraryId = Guid.NewGuid();
        project = project with
        {
            AnimationLibraries = project.AnimationLibraries.Add(
                new ProjectAnimationLibrary
                {
                    Id = secondaryLibraryId,
                    ResourceName = "anims_removed_secondary",
                    DisplayName = "Removed secondary animations",
                }),
            AnimationVariants =
            [
                .. project.AnimationVariants.Select(variant =>
                    variant.Id == removedVariantId
                        ? variant with
                        {
                            OwningAnimationLibraryId = secondaryLibraryId,
                        }
                        : variant),
            ],
        };

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "secondary.sqlite3"),
            Path.Combine(_temporaryDirectory, "secondary-cache"));
        await using MainWindowViewModel viewModel =
            await OpenAsync(project, assets, confirmRemoval: true);

        viewModel.SelectedProjectModel = viewModel.ProjectModelLibrary
            .Single(model => model.ModelId == removedModelId);
        viewModel.RemoveSelectedProjectModelCommand.Execute(null);

        DlraProject after = viewModel.CurrentProject;
        Assert.DoesNotContain(
            after.AnimationLibraries,
            library => library.Id == rootLibraryId);
        Assert.DoesNotContain(
            after.AnimationLibraries,
            library => library.Id == secondaryLibraryId);
        after.Validate();
    }

    [Fact]
    public async Task RemovalKeepsAScriptImportedByASurvivingModel()
    {
        DlraProject project = CreateTwoModelProject(
            out Guid removedModelId,
            out Guid keptModelId,
            out _,
            out _,
            out _);
        Guid sharedLibraryId = project.Models.Single(model =>
            model.Id == removedModelId).RootAnimationLibraryId!.Value;
        Guid keptLibraryId = project.Models.Single(model =>
            model.Id == keptModelId).RootAnimationLibraryId!.Value;
        project = project with
        {
            AnimationLibraries =
            [
                .. project.AnimationLibraries.Select(library =>
                    library.Id == keptLibraryId
                        ? library with
                        {
                            Imports =
                            [
                                new ProjectAnimationLibraryImport
                                {
                                    Kind =
                                        ProjectAnimationLibraryImportKind
                                            .ProjectLibrary,
                                    ProjectLibraryId = sharedLibraryId,
                                },
                            ],
                        }
                        : library),
            ],
        };

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "imports.sqlite3"),
            Path.Combine(_temporaryDirectory, "imports-cache"));
        await using MainWindowViewModel viewModel =
            await OpenAsync(project, assets, confirmRemoval: true);

        viewModel.SelectedProjectModel = viewModel.ProjectModelLibrary
            .Single(model => model.ModelId == removedModelId);
        viewModel.RemoveSelectedProjectModelCommand.Execute(null);

        DlraProject after = viewModel.CurrentProject;
        Assert.Contains(
            after.AnimationLibraries,
            library => library.Id == sharedLibraryId);
        after.Validate();
    }

    [Fact]
    public async Task RemovalAlsoClearsAStaleExportSelection()
    {
        DlraProject project = CreateTwoModelProject(
            out Guid removedModelId,
            out _,
            out Guid removedVariantId,
            out Guid keptVariantId,
            out _);
        project = project with
        {
            ExportSelection = new ProjectExportSelection
            {
                ModelIds = [removedModelId],
                AnimationVariantIds = [removedVariantId, keptVariantId],
            },
        };

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "c.sqlite3"),
            Path.Combine(_temporaryDirectory, "c-cache"));
        await using MainWindowViewModel viewModel =
            await OpenAsync(project, assets, confirmRemoval: true);

        viewModel.SelectedProjectModel = viewModel.ProjectModelLibrary
            .Single(model => model.ModelId == removedModelId);
        viewModel.RemoveSelectedProjectModelCommand.Execute(null);

        // A dangling export selection would fail validation on the next save.
        DlraProject after = viewModel.CurrentProject;
        Assert.Empty(after.ExportSelection.ModelIds);
        Guid selectedVariantId = Assert.Single(
            after.ExportSelection.AnimationVariantIds);
        Assert.Equal(keptVariantId, selectedVariantId);
        after.Validate();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class Dialogs(string projectPath, bool confirmRemoval)
        : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            projectPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => projectPath;

        public bool ConfirmProjectModelRemoval(
            string modelName,
            int animationTargetCount,
            int orphanedAnimationScriptCount) => confirmRemoval;
    }
}
