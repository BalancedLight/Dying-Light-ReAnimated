using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RetentionRemovalWorkflowTests
{
    [Fact]
    public async Task RemovalIsReviewedUndoableAndRechecksCurrentProjectDependencies()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        var parent = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child");
        source = FbxCompilerRetentionAuthoring.Preview(source, parent.EntityId).Candidate;
        var helper = source.Package.Document.AuthoredHelpers[^1];
        bool blocked = false, failCheck = false; int observedThreshold = -1;
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), _ => { }, _ => Task.CompletedTask, () => null,
            getRetentionRemovalBlockers: (_, index, _) => { observedThreshold = index; if (failCheck) throw new InvalidDataException("ambiguous project ownership"); return blocked ? ["active attachment reference"] : []; });
        workspace.CommitProjectRestore(new(source, "retention-removal.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var editor = workspace.Conformance; editor.StudioStage = RigStudioStage.HelpersAndHooks;
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.StructuralNode = editor.StructuralNodes.Single(n => n.EntityId == helper.Id);
        Assert.True(editor.CanPreviewStructuralRetentionRemoval);
        await editor.PreviewStructuralRetentionRemovalCommand.ExecuteAsync(null);
        Assert.Equal(helper.Id, editor.StructuralPreview!.RemovedHelperId);
        Assert.False(editor.CanApplyStructural);
        Assert.True(workspace.Viewport.SceneSource.CaptureFrame().Gizmos.Count >= 3);
        Assert.True(observedThreshold < source.Package.Document.CreateEffectiveBones().Length - 1);
        editor.StructuralReviewed = true; blocked = true;
        editor.ApplyStructuralCommand.Execute(null);
        Assert.Contains("active attachment reference", editor.StructuralStatus, StringComparison.Ordinal);
        Assert.False(editor.StructuralReviewed);
        Assert.Contains(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers, h => h.Id == helper.Id);
        blocked = false; failCheck = true; editor.StructuralReviewed = true;
        editor.ApplyStructuralCommand.Execute(null);
        Assert.Contains("ambiguous project ownership", editor.StructuralStatus, StringComparison.Ordinal);
        Assert.Contains(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers, h => h.Id == helper.Id);
        failCheck = false; editor.StructuralReviewed = true;
        editor.ApplyStructuralCommand.Execute(null);
        Assert.DoesNotContain(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers, h => h.Id == helper.Id);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Contains(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers, h => h.Id == helper.Id);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.DoesNotContain(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers, h => h.Id == helper.Id);
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
