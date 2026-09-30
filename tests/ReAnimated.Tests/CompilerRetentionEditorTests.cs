using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class CompilerRetentionEditorTests
{
    [Fact]
    public async Task ProposalIsVisibleRequiresReviewAndAppliesAsOneUndo()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), _ => { }, _ => Task.CompletedTask, () => null);
        workspace.CommitProjectRestore(new(source, "retention.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var editor = workspace.Conformance;
        editor.StudioStage = RigStudioStage.HelpersAndHooks;
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.StructuralNode = editor.StructuralNodes.Single(n => n.Name == "Child");
        Assert.True(editor.CanPreviewStructuralRetention);
        await editor.PreviewStructuralRetentionCommand.ExecuteAsync(null);
        Assert.NotNull(editor.StructuralPreview?.AddedHelperId);
        Assert.False(editor.CanApplyStructural);
        Assert.Equal(source.Package.Document.AuthoredHelpers.Length, workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers.Length);
        Assert.True(workspace.Viewport.SceneSource.CaptureFrame().Gizmos.Count >= 19);
        Assert.Contains("Compile this candidate", editor.StructuralStatus, StringComparison.Ordinal);
        var id = editor.StructuralPreview!.AddedHelperId;
        editor.StudioStage = RigStudioStage.Animate;
        Assert.NotNull(editor.StructuralPreview);
        editor.StudioStage = RigStudioStage.HelpersAndHooks;
        Assert.NotNull(editor.StructuralPreview);
        Assert.Equal(id, editor.StructuralPreview.AddedHelperId);
        editor.StructuralReviewed = true;
        Assert.True(editor.CanApplyStructural);
        editor.ApplyStructuralCommand.Execute(null);
        var after = workspace.CaptureProjectSession().Model!;
        Assert.Equal(source.Package.Document.AuthoredHelpers.Length + 1, after.Package.Document.AuthoredHelpers.Length);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal<CustomModelAuthoredHelper>(source.Package.Document.AuthoredHelpers, workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal<CustomModelAuthoredHelper>(after.Package.Document.AuthoredHelpers, workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.StructuralNode = editor.StructuralNodes.Single(n => n.Name == "Child");
        Assert.False(editor.CanPreviewStructuralRetention);
    }

    [Fact]
    public async Task CancelDiscardsHelperWithoutTouchingSavedModel()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        var editor = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("control", "Offline control")), _ => { });
        editor.SetModel(source);
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.StructuralNode = editor.StructuralNodes.Single(n => n.Name == "Child");
        await editor.PreviewStructuralRetentionCommand.ExecuteAsync(null);
        editor.StructuralReviewed = true;
        editor.CancelStructuralCommand.Execute(null);
        Assert.Null(editor.StructuralPreview);
        Assert.False(editor.CanApplyStructural);
        Assert.Equal(2, source.Package.Document.AuthoredHelpers.Length);
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
