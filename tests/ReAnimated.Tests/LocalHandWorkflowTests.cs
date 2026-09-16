using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class LocalHandWorkflowTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    internal static FbxModelAuthoringImportResult Source()
    {
        var analysis = LocalHandDetectorTests.Geometry();
        var points = new List<Vector3D>(); var polygons = new List<long>();
        foreach (var component in analysis.Components)
        {
            int offset = points.Count; points.AddRange(component.Geometry.ControlPoints);
            foreach (var triangle in component.Triangles) polygons.AddRange([offset + triangle.A, offset + triangle.B, -(offset + triangle.C) - 1]);
        }
        var model = FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx([], meshVertices: points.SelectMany(p => new[] { p.X * 100, p.Y * 100, p.Z * 100 }).ToArray(),
            meshPolygons: polygons.ToArray()), "generic-local-hand.fbx");
        return model with { Package = model.Package with { Document = model.Package.Document with
            { RiggingSession = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped) } } };
    }

    [Fact]
    public async Task DetectAdoptEditAndReopenKeepsSourceAndHandDeclarations()
    {
        var model = Source();
        using var workspace = Workspace(model);
        var wizard = workspace.Conformance;
        wizard.HandWrist.Set(Vector3D.Zero);
        await wizard.DetectHandCommand.ExecuteAsync(null);
        Assert.NotNull(wizard.HandDetection);
        Assert.Equal(5, wizard.HandDetection.Fingers.Length);
        Assert.True(wizard.CanUseHandProposals, wizard.HandStatus);
        Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);
        var detection = wizard.HandDetection;
        wizard.StudioStage = RigStudioStage.Fit;
        Assert.Same(detection, wizard.HandDetection);
        // Explicitly assign anonymous branches if geometry cannot establish digit names.
        var available = wizard.HandBranches.Where(b => b.Id is not null).ToArray();
        for (int i = 0; i < wizard.HandDigits.Count; i++)
        { wizard.HandDigits[i].Presence = RigFingerPresence.Present; wizard.HandDigits[i].Branch = available[i]; }
        wizard.UseHandProposalsCommand.Execute(null);
        Assert.True(wizard.HasHandSetup, wizard.HandStatus);
        var adopted = workspace.CaptureProjectSession().Model!;
        var hand = Assert.Single(adopted.Package.Document.RiggingSession!.Hands);
        Assert.Equal(5, hand.Fingers.Length);
        Assert.Equal(21, adopted.Package.Document.RiggingSession.Landmarks.Length);
        Assert.False(hand.UserApproved);
        Assert.Equal(model.Surfaces, adopted.Surfaces);
        Assert.Equal(model.Package.SourceFbx, adopted.Package.SourceFbx);
        wizard.SelectedBodyProposal = wizard.HandGuideChoices.First(p => p.Role.StartsWith("finger.", StringComparison.Ordinal));
        Assert.True(wizard.CanEditBodyGuide);
        Assert.Contains(workspace.Viewport.SceneSource.CaptureFrame().Gizmos, g => g.Kind == ReAnimated.Renderer.D3D11.GizmoKind.TranslationHandle);
        double original = wizard.BodyGuideX;
        wizard.BodyGuideX += .001;
        wizard.ApplyBodyGuidePositionCommand.Execute(null);
        Assert.Equal(original + .001, wizard.BodyGuideX, 8);
        foreach (var row in wizard.HandDigits) row.Reviewed = true;
        wizard.ReviewHandSetupCommand.Execute(null);
        var reviewed = workspace.CaptureProjectSession().Model!;
        Assert.True(reviewed.Package.Document.RiggingSession!.Hands[0].UserApproved, wizard.HandStatus);
        Assert.False(wizard.CanBuildHandRig); // This fixture has no generated body parent.
        var path = Path.Combine(_directory, "hand-guides.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(reviewed.Package, path);
        var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(5, reopened.Package.Document.RiggingSession!.Hands[0].Fingers.Length);
        Assert.Equal<byte>(reviewed.Package.SourceFbx, reopened.Package.SourceFbx);
    }

    [Fact]
    public void AdoptionPreservesLocksRejectsDoubleAssignedBranchesAndStaleWork()
    {
        var model = Source();
        var component = model.Surfaces[0].SourceGeometry!.Id;
        var work = FbxLocalHandAuthoring.Detect(model, component, new(-.11, -.03, -.015), new(.06, .03, .19), new(), new() { LongestAxisCells = 64 });
        var branch = work.Detection.Fingers[0];
        Assert.Throws<ArgumentException>(() => HandDetectionAdoption.Adopt(work.Session, work.Token, work.Detection, work.Wrist,
            [new("index", RigFingerPresence.Present, branch.BranchId), new("middle", RigFingerPresence.Present, branch.BranchId)]));
        var guide = new RigLandmark { RoleId = "finger.left.index.1", Position = new(-.04, 0, .09), Locked = true };
        var changed = RiggingSessions.Change(work.Session, work.Session with { Landmarks = [guide] }, RiggingEditKind.Anatomy);
        Assert.Throws<InvalidOperationException>(() => HandDetectionAdoption.Adopt(changed, work.Token, work.Detection, work.Wrist, [new("index", RigFingerPresence.Present, branch.BranchId)]));
        var adopted = HandDetectionAdoption.Adopt(changed, changed.CreateJobToken(), work.Detection, work.Wrist,
            [new("index", RigFingerPresence.Present, branch.BranchId), new("ring", RigFingerPresence.Absent, null), new("little", RigFingerPresence.Fused, null)]);
        Assert.Same(guide, adopted.Landmarks.Single(g => g.Id == guide.Id));
        Assert.Empty(adopted.Hands[0].Fingers.Single(f => f.Id == "ring").JointGuideIds);
        Assert.Empty(adopted.Hands[0].Fingers.Single(f => f.Id == "little").JointGuideIds);
        Assert.Equal(RigFingerPresence.Fused, adopted.Hands[0].Fingers.Single(f => f.Id == "little").Presence);
    }

    private static ModelsWorkspaceViewModel Workspace(FbxModelAuthoringImportResult model)
    {
        var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), _ => { }, _ => Task.CompletedTask, () => null);
        workspace.CommitProjectRestore(new(model, "local-hand.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true; return workspace;
    }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}
