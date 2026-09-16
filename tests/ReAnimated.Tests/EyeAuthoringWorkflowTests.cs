using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class EyeAuthoringWorkflowTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();
    internal static FbxModelAuthoringImportResult Source()
    {
        var geometry = EyePivotDetectorTests.Sphere(new(.03, .2, .05), new(.025, .025, .025)).Components[0];
        var model = FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx(["blink"], firstShapeDeltaX: .001,
            meshVertices: geometry.Geometry.ControlPoints.SelectMany(p => new[] { p.X * 100, p.Y * 100, p.Z * 100 }).ToArray(),
            meshPolygons: geometry.Triangles.SelectMany(t => new[] { (long)t.A, (long)t.B, -t.C - 1L }).ToArray()), "generic-eye.fbx");
        return model with { Package = model.Package with { Document = model.Package.Document with
            { RiggingSession = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped) } } };
    }

    [Fact]
    public async Task GlobeReviewSurvivesNavigationSavesAndReopensWithoutSurfaceChanges()
    {
        var source = Source(); using var workspace = Workspace(source); var vm = workspace.Conformance;
        vm.EyeMode = vm.EyeModeChoices.Single(c => c.Mode == RigEyeSetupMode.GeometryPivot);
        await vm.DetectEyeCommand.ExecuteAsync(null);
        Assert.Equal(EyePivotDetectionStatus.GlobeCandidate, vm.EyeDetection?.Status);
        Assert.False(vm.CanSaveEyeSetup);
        Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);
        var detection = vm.EyeDetection;
        vm.StudioStage = RigStudioStage.HelpersAndHooks;
        Assert.Same(detection, vm.EyeDetection);
        vm.EyeReviewed = true;
        Assert.True(vm.CanSaveEyeSetup, vm.EyeStatus);
        vm.SaveEyeSetupCommand.Execute(null);
        var saved = workspace.CaptureProjectSession().Model!;
        var eye = Assert.Single(saved.Package.Document.RiggingSession!.Eyes);
        Assert.Equal(RigEyeGeometryKind.GlobeCandidate, eye.GeometryKind);
        Assert.Equal(detection!.Center, eye.GlobalFrame.Translation);
        Assert.Equal(source.Surfaces, saved.Surfaces);
        Assert.Equal(source.Package.SourceFbx, saved.Package.SourceFbx);
        Assert.Equal(source.Package.Document.MorphChannels, saved.Package.Document.MorphChannels);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Eyes);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Eyes);
        var path = Path.Combine(_directory, "eye-review.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(saved.Package, path);
        var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(eye.GlobalFrame, Assert.Single(reopened.Package.Document.RiggingSession!.Eyes).GlobalFrame);
        Assert.Equal<byte>(saved.Package.SourceFbx, reopened.Package.SourceFbx);
    }

    [Fact]
    public async Task PaintedGeometryCannotCreatePivotAndFacialSelectionsRemainIndependent()
    {
        using var workspace = Workspace(Source()); var vm = workspace.Conformance;
        vm.EyeMode = vm.EyeModeChoices.Single(c => c.Mode == RigEyeSetupMode.GeometryPivot); vm.EyePainted = true;
        await vm.DetectEyeCommand.ExecuteAsync(null);
        Assert.Equal(EyePivotDetectionStatus.UnsupportedGeometry, vm.EyeDetection?.Status);
        vm.EyeReviewed = true; Assert.False(vm.CanSaveEyeSetup); Assert.False(vm.CanApplyEyeHelper);
        vm.EyeMode = vm.EyeModeChoices.Single(c => c.Mode == RigEyeSetupMode.Mimic);
        Assert.Single(vm.EyeMorphs).Selected = true;
        vm.EyeReviewed = true; vm.SaveEyeSetupCommand.Execute(null);
        var eye = Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Eyes);
        Assert.Equal(RigEyeSetupMode.Mimic, eye.Mode); Assert.Single(eye.MorphDescriptors);
        Assert.Null(eye.ComponentId); Assert.Null(eye.HelperEntityId);
        vm.EyeSide = RigEyeSide.Right;
        Assert.False(Assert.Single(vm.EyeMorphs).Selected);
        Assert.False(vm.EyeReviewed);
    }

    [Fact]
    public void GazeHelperIsOneUndoAndKeepsSourceEyesAndCamerasSeparate()
    {
        var imported = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-eye-rig.fbx");
        var source = imported with { Package = imported.Package with { Document = imported.Package.Document with
            { RiggingSession = RiggingSessions.Create(imported.Package.Document, RigStudioEntryPath.RepairExistingRig) } } };
        using var workspace = Workspace(source); var vm = workspace.Conformance;
        vm.EyeSourceNode = vm.EyeNodes.Last(); vm.EyeReviewed = true; vm.SaveEyeSetupCommand.Execute(null);
        var sourceSetup = Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Eyes);
        Assert.Equal(vm.EyeSourceNode!.GlobalFrame, sourceSetup.GlobalFrame);
        vm.EyeMode = vm.EyeModeChoices.Single(c => c.Mode == RigEyeSetupMode.GazeReference);
        vm.EyeParent = vm.EyeNodes.First(); vm.EyePainted = true;
        vm.EyePosition.Set(new(.03, .2, .05)); vm.EyeForward.Set(Vector3D.UnitY); vm.EyeReviewed = true;
        Assert.False(vm.CanApplyEyeHelper); // Collinear axes cannot establish an orientation.
        vm.EyeForward.Set(Vector3D.UnitZ); Assert.False(vm.EyeReviewed);
        vm.EyeReviewed = true; Assert.True(vm.CanApplyEyeHelper, vm.EyeStatus);
        vm.ApplyEyeHelperCommand.Execute(null);
        var saved = workspace.CaptureProjectSession().Model!;
        Assert.Single(saved.Package.Document.AuthoredHelpers);
        Assert.Equal(2, saved.Package.Document.RiggingSession!.Eyes.Length);
        Assert.Equal(source.Surfaces, saved.Surfaces);
        Assert.Equal(source.Package.Document.Bones, saved.Package.Document.Bones);
        Assert.Equal(source.Package.Document.Camera, saved.Package.Document.Camera);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Eyes);
    }

    private static ModelsWorkspaceViewModel Workspace(FbxModelAuthoringImportResult model)
    {
        var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), _ => { }, _ => Task.CompletedTask, () => null);
        workspace.CommitProjectRestore(new(model, "eye.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true; return workspace;
    }

    [Fact]
    public async Task EyeBoneBindingAndMotionReviewAreSeparateUndoableEdits()
    {
        var source=FbxGeneratedBodyBinding.Generate(GeneratedBodyWorkflowTests.WithFixtureGuides(RigidEyeBindingTests.Source()));
        using var workspace=Workspace(source); var vm=workspace.Conformance;
        vm.EyeMode=vm.EyeModeChoices.Single(c=>c.Mode==RigEyeSetupMode.GeometryPivot);
        await vm.DetectEyeCommand.ExecuteAsync(null);
        vm.EyeParent=vm.EyeNodes.Single(n=>n.Name=="body_head"); vm.EyeReviewed=true;
        vm.SaveEyeSetupCommand.Execute(null);
        Assert.True(vm.CanBuildEyeRig,vm.EyeStatus);
        await vm.BuildEyeRigCommand.ExecuteAsync(null);
        Assert.True(vm.HasEyeDeformBone,vm.EyeRigStatus);
        Assert.True(vm.CanPreviewEyeBinding,vm.EyeRigStatus);
        await vm.PreviewEyeBindingCommand.ExecuteAsync(null);
        Assert.Equal(266,vm.EyeBinding?.ChangedPointCount);
        var preview=vm.EyeBinding; vm.StudioStage=RigStudioStage.HelpersAndHooks;
        Assert.NotNull(vm.EyeBinding); Assert.Equal(preview!.EyeEntityId,vm.EyeBinding!.EyeEntityId);
        await vm.ApplyEyeBindingCommand.ExecuteAsync(null);
        var bound=workspace.CaptureProjectSession().Model!;
        Assert.Contains(bound.Surfaces,s=>s.IsSkinned);
        var snapshot=bound.Package.AuthoredLayerPayload;
        vm.EyeMotionReviewEnabled=true; vm.EyeYawDegrees=35; vm.EyePitchDegrees=-20;
        Assert.True(vm.TryGetEyeMotion(out _));
        Assert.Equal(snapshot,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.True(vm.HasEyeDeformBone); Assert.All(workspace.CaptureProjectSession().Model!.Surfaces,s=>Assert.False(s.IsSkinned));
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.False(vm.HasEyeDeformBone);
        Assert.Equal(source.Package.Document.Bones.Length,workspace.CaptureProjectSession().Model!.Package.Document.Bones.Length);
    }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}
