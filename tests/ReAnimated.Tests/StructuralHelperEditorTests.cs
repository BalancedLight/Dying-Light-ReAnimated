using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class StructuralHelperEditorTests
{
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
    private static RigConformanceWizardViewModel Editor() => new(
        (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic structural review")), static _ => { });

    [Fact]
    public async Task ScanAndFrameDraftRequireExplicitReviewAndRejectChangedInputs()
    {
        var source = StructuralHelperAuthoringTests.Source(); var editor = Editor();
        editor.SetModel(source);
        Assert.Empty(editor.StructuralNodes);
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        Assert.Equal(source.Rig!.BoneCount, editor.StructuralNodes.Count);
        editor.StructuralNode = editor.StructuralNodes.Single(n => n.Name == "normal_marker_2");
        editor.StructuralOffset.X = .02;
        await editor.PreviewStructuralFrameCommand.ExecuteAsync(null);
        Assert.NotNull(editor.StructuralPreview);
        Assert.False(editor.CanApplyStructural);
        editor.StructuralReviewed = true;
        Assert.True(editor.CanApplyStructural);
        editor.StructuralOffset.X = .03;
        Assert.Null(editor.StructuralPreview); Assert.False(editor.CanApplyStructural);
        await editor.PreviewStructuralFrameCommand.ExecuteAsync(null);
        editor.StructuralReviewed = true;
        FbxModelAuthoringImportResult? result = null;
        editor.StructuralApplyRequested += (_, e) => { Assert.Same(source, e.Source); result = e.Result; };
        editor.ApplyStructuralCommand.Execute(null);
        Assert.NotNull(result); Assert.False(editor.CanApplyStructural);
        Assert.Equal<FbxModelSurface>(source.Surfaces, result.Surfaces);
    }

    [Fact]
    public async Task ProtectionDraftAndJointRoutingPreserveReviewIntent()
    {
        var editor = Editor(); editor.SetModel(StructuralHelperAuthoringTests.Source());
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.StructuralNode = editor.StructuralNodes.First(n => n.CanProtect && n.WeightedCorners > 0);
        Guid id = editor.StructuralNode.EntityId;
        Assert.False(editor.CanPreviewStructuralFrame);
        editor.StructuralLockPosition = true; editor.StructuralLockOrientation = true;
        await editor.PreviewStructuralProtectionCommand.ExecuteAsync(null);
        Assert.NotNull(editor.StructuralPreview); Assert.False(editor.CanApplyStructural);
        var saved = editor.StructuralPreview.Candidate.Package.Document.RiggingSession!.Recipe.Helpers.Single(h => h.EntityId == id);
        Assert.Equal(RigHelperEditFields.Position | RigHelperEditFields.Orientation, saved.LockedFields);
        editor.StructuralLockChannels = true;
        Assert.Null(editor.StructuralPreview);
        editor.OpenStructuralRestCommand.Execute(null);
        Assert.Equal(RigStudioStage.Fit, editor.StudioStage);
        Assert.Equal(id, editor.RestPoseNode!.EntityId);
        Assert.Equal(RigRestSurfaceMode.PreserveSurface, editor.RestSurfaceMode!.Mode);
        Assert.Equal(RigRestDescendantMode.KeepGlobal, editor.RestDescendantMode!.Mode);
    }

    [Fact]
    public async Task NamedRetentionBatchRequiresPreviewReviewAndOneApply()
    {
        var source = CompilerRetentionAuthoringTests.Source();
        var editor = Editor();
        editor.SetModel(source);
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        StructuralNodeReview eligible = editor.StructuralNodes.Single(node => node.CanAddRetentionHelper);
        Assert.False(editor.PreviewStructuralRetentionBatchCommand.CanExecute(null));

        editor.StructuralRetentionBatchNames = eligible.Name;
        Assert.True(editor.PreviewStructuralRetentionBatchCommand.CanExecute(null));
        await editor.PreviewStructuralRetentionBatchCommand.ExecuteAsync(null);
        Assert.NotNull(editor.StructuralPreview);
        Assert.Contains("propose 1 compiler-retention helpers", editor.StructuralStatus, StringComparison.Ordinal);
        Assert.False(editor.CanApplyStructural);
        editor.StructuralReviewed = true;

        FbxModelAuthoringImportResult? applied = null;
        editor.StructuralApplyRequested += (_, change) => applied = change.Result;
        editor.ApplyStructuralCommand.Execute(null);
        Assert.NotNull(applied);
        Assert.Single(applied.Package.Document.RiggingSession!.Recipe.Helpers,
            helper => helper.RoleId == FbxCompilerRetentionAuthoring.RoleId);
        Assert.False(editor.CanApplyStructural);
    }

    [Fact]
    public async Task SourceReplacementDuringScanCannotPublishStaleRows()
    {
        var editor = Editor(); editor.SetModel(StructuralHelperAuthoringTests.Source());
        Task scan = editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.SetModel(null);
        await scan;
        Assert.Empty(editor.StructuralNodes); Assert.False(editor.IsBusy); Assert.Null(editor.StructuralPreview);
    }

    [Fact]
    public async Task WorkspaceAppliesOneUndoAndKeepsStageOnlyPreview()
    {
        var source = StructuralHelperAuthoringTests.Source();
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic structural review")));
        workspace.CommitProjectRestore(new(source, "structural-target.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var editor = workspace.Conformance; editor.StudioStage = RigStudioStage.HelpersAndHooks;
        await editor.ScanStructuralCommand.ExecuteAsync(null);
        editor.StructuralNode = editor.StructuralNodes.Single(n => n.Name == "normal_marker_2");
        editor.StructuralOffset.X = .05;
        await editor.PreviewStructuralFrameCommand.ExecuteAsync(null);
        Assert.NotNull(editor.StructuralPreview);
        editor.StructuralPreviewEnabled = false; editor.StructuralPreviewEnabled = true;
        editor.StructuralReviewed = true;
        editor.StudioStage = RigStudioStage.Animate; editor.StudioStage = RigStudioStage.HelpersAndHooks;
        Assert.True(editor.CanApplyStructural);
        editor.ApplyStructuralCommand.Execute(null);
        var after = workspace.CaptureProjectSession().Model!;
        Assert.NotEqual(source.Package.Document, after.Package.Document);
        Assert.Equal<FbxModelSurface>(source.Surfaces, after.Surfaces);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(source.Package.Document.AuthoredHelpers[0].LocalTransform,
            workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers[0].LocalTransform);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(after.Package.Document.AuthoredHelpers[0].LocalTransform,
            workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers[0].LocalTransform);
    }
}
