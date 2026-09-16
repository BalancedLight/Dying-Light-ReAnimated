using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class AnatomicalGuideEditingWorkflowTests
{
    [Fact]
    public void DragPreviewCancelCommitUndoAndRedoPreserveSourceAndStableSelection()
    {
        using var workspace = Workspace();
        var wizard = workspace.Conformance;
        var original = workspace.CaptureProjectSession().Model!;
        Assert.Same(workspace.UndoHelperEditCommand, wizard.UndoBodyGuideEditCommand);
        Assert.Same(workspace.RedoHelperEditCommand, wizard.RedoBodyGuideEditCommand);
        Assert.Contains("Unrigged model loaded", wizard.SolveStatus, StringComparison.Ordinal);
        var session = original.Package.Document.RiggingSession!;
        Guid selected = session.Landmarks[1].Id;
        wizard.SelectBodyGuide(selected);
        var binding = TranslationGizmoBinding.ForTarget(selected, TranslationGizmoAxis.Y, RenderGizmoSpace.Global);
        var target = workspace.Viewport.SceneSource;
        var scene = target.CaptureFrame();
        Assert.Null(scene.Skeleton);
        var handles = scene.Gizmos.Where(g => g.TranslationBinding is not null).ToArray();
        Assert.Equal(3, handles.Length);
        Assert.All(handles, h => { Assert.Equal(selected, h.TranslationBinding!.Value.TargetId); Assert.Equal(-1, h.TranslationBinding.Value.BoneIndex); });
        Assert.True(target.TryBeginTranslationGizmoDrag(new(binding, Vector3.UnitY)));
        Assert.True(target.UpdateTranslationGizmoDrag(new(binding, Vector3.UnitY * .25f, .25f)));
        Assert.Same(original, workspace.CaptureProjectSession().Model);
        Assert.Equal(session.Landmarks[1].Position.Y + .25, wizard.BodyGuideY);
        Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        Assert.Equal(scene.Meshes, target.CaptureFrame().Meshes);
        target.CompleteTranslationGizmoDrag(false);
        Assert.Empty(wizard.BodyGuidePreview);
        Assert.Equal(session.Landmarks[1].Position.Y, wizard.BodyGuideY);
        Assert.Same(original, workspace.CaptureProjectSession().Model);

        Assert.True(target.TryBeginTranslationGizmoDrag(new(binding, Vector3.UnitY)));
        Assert.True(target.UpdateTranslationGizmoDrag(new(binding, Vector3.UnitY * .5f, .5f)));
        target.CompleteTranslationGizmoDrag(true);
        var committed = workspace.CaptureProjectSession().Model!;
        Assert.Equal(session.Landmarks[1].Position + Vector3D.UnitY * .5, committed.Package.Document.RiggingSession!.Landmarks[1].Position);
        Assert.Equal(selected, wizard.SelectedBodyGuideId);
        Assert.Same(original.Surfaces[0], committed.Surfaces[0]);
        Assert.Equal(original.Package.Document.Bones, committed.Package.Document.Bones);
        Assert.Equal(original.Package.SourceFbx, committed.Package.SourceFbx);
        var stale = committed.Package.Document.RiggingSession.CreateJobToken();
        Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        var undone = workspace.CaptureProjectSession().Model!;
        Assert.Equal(session.Landmarks[1].Position, undone.Package.Document.RiggingSession!.Landmarks[1].Position);
        Assert.Equal(selected, wizard.SelectedBodyGuideId);
        Assert.False(undone.Package.Document.RiggingSession.Matches(stale));
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(committed.Package.Document.RiggingSession.Landmarks, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Landmarks);
        Assert.Same(original.Surfaces[0], workspace.CaptureProjectSession().Model!.Surfaces[0]);
    }

    [Fact]
    public void SelectionTabModelAndAxisChangesCancelPendingDrags()
    {
        using var workspace = Workspace();
        var wizard = workspace.Conformance;
        var model = workspace.CaptureProjectSession().Model!;
        var session = model.Package.Document.RiggingSession!;
        Guid selected = session.Landmarks[0].Id;
        wizard.SelectBodyGuide(selected);
        var binding = TranslationGizmoBinding.ForTarget(selected, TranslationGizmoAxis.X, RenderGizmoSpace.Global);
        var target = workspace.Viewport.SceneSource;
        void Begin()
        {
            Assert.True(target.TryBeginTranslationGizmoDrag(new(binding, Vector3.UnitX)));
            Assert.True(target.UpdateTranslationGizmoDrag(new(binding, Vector3.UnitX * .25f, .25f)));
        }
        Begin();
        wizard.SelectBodyGuide(session.Landmarks[1].Id);
        target.CompleteTranslationGizmoDrag(true);
        Assert.Same(model, workspace.CaptureProjectSession().Model);
        wizard.SelectBodyGuide(selected);
        Begin();
        workspace.IsConformTabSelected = false;
        target.CompleteTranslationGizmoDrag(true);
        Assert.Empty(wizard.BodyGuidePreview);
        Assert.Same(model, workspace.CaptureProjectSession().Model);
        workspace.IsConformTabSelected = true;
        Begin();
        Assert.False(target.UpdateTranslationGizmoDrag(new(binding with { Axis = TranslationGizmoAxis.Z }, Vector3.UnitZ, 1)));
        target.CompleteTranslationGizmoDrag(true);
        Assert.Same(model, workspace.CaptureProjectSession().Model);
        Begin();
        var replacement = WithGuides();
        workspace.CommitProjectRestore(new(replacement, "generic-other.dlrmodel", new() { PackageAssetId = Guid.NewGuid() }));
        target.CompleteTranslationGizmoDrag(true);
        Assert.Empty(wizard.BodyGuidePreview);
        Assert.Equal(replacement.Package.Document.RiggingSession!.Landmarks, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Landmarks);
        Assert.False(target.TryBeginTranslationGizmoDrag(new(binding, Vector3.UnitX)));
    }

    [Fact]
    public void NumericMirroringUsesSavedPlanePinsBlockMovementAndNonFiniteValuesAreRefused()
    {
        using var workspace = Workspace();
        var wizard = workspace.Conformance;
        var session = workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!;
        wizard.SelectBodyGuide(session.Landmarks[0].Id);
        Assert.True(wizard.CanMirrorBodyGuide);
        wizard.MirrorBodyGuide = true;
        wizard.BodyGuideX = 3; wizard.BodyGuideY = 4; wizard.BodyGuideZ = 5;
        wizard.ApplyBodyGuidePositionCommand.Execute(null);
        var moved = workspace.CaptureProjectSession().Model!;
        Assert.Equal(new Vector3D(3, 4, 5), moved.Package.Document.RiggingSession!.Landmarks[0].Position);
        Assert.Equal(new Vector3D(-1, 4, 5), moved.Package.Document.RiggingSession.Landmarks[1].Position);
        Assert.True(wizard.MirrorBodyGuide);
        wizard.BodyGuideX = double.NaN;
        wizard.ApplyBodyGuidePositionCommand.Execute(null);
        Assert.Same(moved, workspace.CaptureProjectSession().Model);
        wizard.ToggleBodyGuidePinCommand.Execute(null);
        Assert.False(wizard.CanEditBodyGuide);
        Assert.False(wizard.ApplyBodyGuidePositionCommand.CanExecute(null));
        Assert.DoesNotContain(workspace.Viewport.SceneSource.CaptureFrame().Gizmos, g => g.TranslationBinding is not null);
        wizard.SelectBodyGuide(session.Landmarks[1].Id);
        Assert.False(wizard.CanMirrorBodyGuide);
        Assert.True(wizard.CanEditBodyGuide);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.True(wizard.CanEditBodyGuide);
        Assert.Equal(session.Landmarks[0].Id, wizard.SelectedBodyGuideId);
        Assert.False(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Landmarks[0].Locked);
    }

    [Fact]
    public void MetadataSaveCancelsPendingMoveWithoutStrandingTheNextEdit()
    {
        using var workspace = Workspace();
        var wizard = workspace.Conformance;
        var model = workspace.CaptureProjectSession().Model!;
        var guide = model.Package.Document.RiggingSession!.Landmarks[0];
        wizard.SelectBodyGuide(guide.Id);
        var binding = TranslationGizmoBinding.ForTarget(guide.Id, TranslationGizmoAxis.X, RenderGizmoSpace.Global);
        var target = workspace.Viewport.SceneSource;
        Assert.True(target.TryBeginTranslationGizmoDrag(new(binding, Vector3.UnitX)));
        Assert.True(target.UpdateTranslationGizmoDrag(new(binding, Vector3.UnitX, 1)));
        workspace.ModelName = "Renamed guide model";
        var saved = workspace.CaptureProjectSession().Model!;
        target.CompleteTranslationGizmoDrag(true);
        Assert.Empty(wizard.BodyGuidePreview);
        Assert.Equal(guide.Position, saved.Package.Document.RiggingSession!.Landmarks[0].Position);
        Assert.Same(saved, workspace.CaptureProjectSession().Model);
        Assert.Equal(guide.Id, wizard.SelectedBodyGuideId);
        wizard.BodyGuideY += .5;
        wizard.ApplyBodyGuidePositionCommand.Execute(null);
        var edited = workspace.CaptureProjectSession().Model!;
        Assert.Equal(guide.Position + Vector3D.UnitY * .5, edited.Package.Document.RiggingSession!.Landmarks[0].Position);
        Assert.Equal("Renamed guide model", edited.Package.Document.Name);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(guide.Position, workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Landmarks[0].Position);
        Assert.Equal("Renamed guide model", workspace.ModelName);
    }

    private static ModelsWorkspaceViewModel Workspace()
    {
        var workspace = new ModelsWorkspaceViewModel(new NullProjectFileDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(WithGuides(), "generic-guide.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        return workspace;
    }

    private static FbxModelAuthoringImportResult WithGuides()
    {
        var model = AnatomicalDetectionWorkflowTests.CreateUnriggedModel();
        Guid left = Guid.NewGuid(), right = Guid.NewGuid();
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped) with {
            Stage = RigStudioStage.Fit,
            SymmetryOrigin = Vector3D.UnitX, Landmarks = [new() { Id = left, RoleId = "hand.left", Position = new(2, 3, 4), MirrorPartnerId = right, UserApproved = true },
                new() { Id = right, RoleId = "hand.right", Position = new(0, 3, 4), MirrorPartnerId = left }],
        };
        return model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
    }

    private sealed class NullProjectFileDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
