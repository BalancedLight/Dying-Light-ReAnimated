using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class WeightBrushWorkflowTests
{
    [Fact]
    public async Task CancelledStrokeKeepsSourceAndCommittedStrokeHasOneUndo()
    {
        using var workspace = Workspace();
        var wizard = workspace.Conformance;
        await wizard.InspectWeightsCommand.ExecuteAsync(null);
        wizard.SelectedWeightInfluence = wizard.WeightInfluences.Single(i => i.Name == "joint_a");
        wizard.WeightTargetValue = .5; wizard.WeightBrushRadius = .2;
        wizard.WeightBrushEnabled = true;
        Assert.Same(wizard.WeightBrushTarget, workspace.Viewport.SceneSource.BrushTarget);
        Assert.All(workspace.Viewport.SceneSource.CaptureFrame().Meshes, mesh => Assert.False(mesh.IsSkinned));
        var before = workspace.CaptureProjectSession().Model!;
        var ray = Ray(before);
        Assert.True(wizard.WeightBrushTarget.TryBeginBrush(ray), wizard.WeightEditingStatus);
        wizard.WeightBrushTarget.CompleteBrush(false);
        Assert.Equal(before.Package.AuthoredLayerPayload, workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        Assert.Null(wizard.WeightBrushCenter);
        Assert.True(wizard.WeightBrushTarget.TryBeginBrush(ray));
        Assert.True(wizard.WeightBrushTarget.UpdateBrush(ray));
        wizard.WeightBrushTarget.CompleteBrush(true);
        await wizard.WeightBrushCommitTask;
        var applied = workspace.CaptureProjectSession().Model!;
        Assert.NotEqual(before.Package.AuthoredLayerPayload, applied.Package.AuthoredLayerPayload);
        Assert.All(FbxSkinWeightAuthoring.Inspect(applied).Points, p => Assert.InRange(p.Weights.Single(w => w.HandleId == wizard.SelectedWeightInfluence!.EntityId).Weight, .5, .75));
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(before.Package.AuthoredLayerPayload, workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(applied.Package.AuthoredLayerPayload, workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
    }

    [Fact]
    public async Task ModelChangeDiscardsActiveStrokeAndMirrorSettingsRequireExplicitSave()
    {
        using var workspace = Workspace();
        var wizard = workspace.Conformance;
        await wizard.InspectWeightsCommand.ExecuteAsync(null);
        wizard.SelectedWeightInfluence = wizard.WeightInfluences.Single(i => i.Name == "joint_a");
        wizard.WeightMirrorInfluence = wizard.WeightInfluences.Single(i => i.Name == "joint_b");
        wizard.MirrorWeightEdits = true;
        Assert.True(wizard.HasUnsavedWeightMirroring);
        Assert.False(wizard.PreviewWeightCorrectionCommand.CanExecute(null));
        await wizard.SaveWeightMirroringCommand.ExecuteAsync(null);
        var settings = workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!;
        Assert.True(settings.MirrorWeightEdits);
        Assert.Single(settings.WeightMirrorPairs);
        Assert.False(wizard.HasUnsavedWeightMirroring);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "mirroring.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(workspace.CaptureProjectSession().Model!.Package, path);
            var restored = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal<RigWeightMirrorPair>(settings.WeightMirrorPairs, restored.Package.Document.RiggingSession!.WeightMirrorPairs);
            Assert.Equal(settings.SymmetryNormal, restored.Package.Document.RiggingSession.SymmetryNormal);
            Assert.True(restored.Package.Document.RiggingSession.MirrorWeightEdits);
        }
        finally { Directory.Delete(directory, true); }
        wizard.MirrorWeightEdits = false;
        await wizard.SaveWeightMirroringCommand.ExecuteAsync(null);
        wizard.WeightBrushEnabled = true; wizard.WeightBrushRadius = .2;
        Assert.True(wizard.WeightBrushTarget.TryBeginBrush(Ray(workspace.CaptureProjectSession().Model!)));
        workspace.CommitProjectRestore(new(FbxSkinWeightAuthoringTests.Model(), "replacement.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        Assert.False(wizard.WeightBrushTarget.UpdateBrush(default));
        wizard.WeightBrushTarget.CompleteBrush(true);
        await wizard.WeightBrushCommitTask;
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession);
    }

    private static RenderBrushPointerRay Ray(FbxModelAuthoringImportResult model)
    {
        var snapshot = FbxSkinWeightAuthoring.Inspect(model);
        var (a, b, c) = snapshot.Triangles[0];
        var center = (snapshot.Positions[a] + snapshot.Positions[b] + snapshot.Positions[c]) / 3;
        var normal = Vector3D.Cross(snapshot.Positions[b] - snapshot.Positions[a], snapshot.Positions[c] - snapshot.Positions[a]).Normalized();
        var origin = center + normal;
        return new(new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z), new Vector3((float)-normal.X, (float)-normal.Y, (float)-normal.Z));
    }
    private static ModelsWorkspaceViewModel Workspace()
    {
        var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(FbxSkinWeightAuthoringTests.Model(), "brush.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        return workspace;
    }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
