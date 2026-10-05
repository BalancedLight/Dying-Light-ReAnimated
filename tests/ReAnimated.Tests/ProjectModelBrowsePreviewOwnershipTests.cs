using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectModelBrowsePreviewOwnershipTests
{
    [Theory]
    [InlineData("review")]
    [InlineData("authoring")]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public async Task NonBrowseOwnershipRefusesSchedulingAndCancelsPreviousRequest(string ownership)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            ProjectModelItemViewModel model = await PrepareSelectionAsync(viewModel);
            using var previous = new CancellationTokenSource();
            CancellationToken previousToken = previous.Token;
            SetField(viewModel, "_automaticAssetPreviewSource", previous);
            EnterOwnership(viewModel, ownership);
            Invoke(viewModel, "ScheduleProjectModelPreview", model);
            Assert.True(previousToken.IsCancellationRequested);
            Assert.Null(Field(viewModel, "_automaticAssetPreviewSource"));
            Assert.Null(Field(viewModel, "_automaticAssetPreviewTask"));
            Assert.Empty(viewModel.Jobs);
            SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), false);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("review")]
    [InlineData("authoring")]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public async Task DelayedBrowseRequestDoesNotStartAfterOwnershipChanges(string ownership)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = Assets(directory);
            await using var viewModel = ViewModel(directory, assets);
            ProjectModelItemViewModel model = await PrepareSelectionAsync(viewModel);
            using var source = new CancellationTokenSource();
            SetField(viewModel, "_automaticAssetPreviewSource", source);
            // Model decode would create a Preview job even though this deliberately
            // absent package cannot decode. No job proves the request never starts.
            Task preview = Assert.IsAssignableFrom<Task>(Invoke(viewModel,
                "PreviewProjectModelAfterDelayAsync", model, source));
            EnterOwnership(viewModel, ownership);
            await preview.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Empty(viewModel.Jobs);
            Assert.Null(Field(viewModel, "_automaticAssetPreviewSource"));
            Assert.Equal(model.ModelId, viewModel.SelectedProjectModel!.ModelId);
            if (ownership == "review") Assert.True(viewModel.HasGuidedPreviewReviewDraft);
            if (ownership == "authoring") Assert.True(viewModel.IsCustomModelAuthoringSurfaceVisible);
            SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), false);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static async Task<ProjectModelItemViewModel> PrepareSelectionAsync(MainWindowViewModel viewModel)
    {
        Guid modelId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Generic project") with
        {
            Assets = [new ProjectAssetReference
            {
                Id = assetId,
                Kind = ProjectAssetKind.CustomModelSource,
                RelativePath = "models/generic.dlrmodel",
                ContentSha256 = new string('a', 64),
            }],
            Models = [new ProjectModelEntry
            {
                Id = modelId,
                AssetId = assetId,
                Name = "Generic model",
                RigSignature = new string('b', 64),
            }],
            Workflow = new ProjectWorkflowState { SelectedModelId = modelId },
        };
        SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), true);
        SetField(viewModel, "_project", project);
        Invoke(viewModel, "RefreshProjectModelLibrary");
        Invoke(viewModel, "CancelAutomaticAssetPreview");
        if (Field(viewModel, "_automaticAssetPreviewTask") is Task prior)
            await prior.WaitAsync(TimeSpan.FromSeconds(30));
        SetField(viewModel, "_automaticAssetPreviewTask", null);
        SetProperty(viewModel, nameof(MainWindowViewModel.IsBusy), false);
        return Assert.IsType<ProjectModelItemViewModel>(viewModel.SelectedProjectModel);
    }

    private static void EnterOwnership(MainWindowViewModel viewModel, string ownership)
    {
        switch (ownership)
        {
            case "review": SetProperty(viewModel, nameof(MainWindowViewModel.HasGuidedPreviewReviewDraft), true); break;
            case "authoring": Invoke(viewModel, "OpenCustomModelAuthoring"); break;
            default: throw new ArgumentException("Unknown test ownership.", nameof(ownership));
        }
    }

    private static Dl1AssetWorkspace Assets(string directory) => new(
        Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache"));

    private static MainWindowViewModel ViewModel(string directory, Dl1AssetWorkspace assets) => new(
        new JsonWorkspaceStateStore(Path.Combine(directory, "state.json")), new NoDialogs(), assets);

    private static object? Field(object owner, string name) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void SetField(object owner, string name, object? value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void SetProperty(object owner, string name, object value) => owner.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object?[] arguments) => owner.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments);

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => throw new InvalidOperationException("No picker is used.");
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => throw new InvalidOperationException("No picker is used.");
    }
}
