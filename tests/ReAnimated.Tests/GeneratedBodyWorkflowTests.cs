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

public sealed class GeneratedBodyWorkflowTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void DetectedGuidesGenerateAndBindARealUnriggedFbxThenSurviveReopen()
    {
        var source = Source();
        var analysis = FbxSourceGeometryAnalysis.Build(source);
        var grid = SourceVolumeGrid.Build(SourceGeometryVolume.Build(analysis), new() { LongestAxisCells = 48 });
        var detection = AnatomicalRigDetector.Detect(grid, new() { Frame = new() { Left = -Vector3D.UnitX } });
        var session = RiggingSessions.Create(source.Package.Document, RigStudioEntryPath.AutoRigBiped);
        Assert.Equal(18, detection.Joints.Length);
        Assert.True(AnatomicalDetectionAdoption.TryApply(session, session.CreateJobToken(), grid.InputFingerprint, detection, out var guided));
        var model = source with { Package = source.Package with { Document = source.Package.Document with { RiggingSession = guided } } };
        var generated = FbxGeneratedBodyBinding.Generate(model);
        Assert.True(GeneratedBodyRig.IsGenerated(generated.Package.Document));
        Assert.Equal(19, generated.Rig!.BoneCount);
        Assert.All(generated.Rig.Bones, b => Assert.NotNull(b.SemanticRole));
        var observed = RiggingSessions.ObserveSourceHierarchy(generated.Package.Document);
        Assert.Equal(19, observed.Length);
        Assert.All(observed, row => Assert.Contains(generated.Package.Document.RiggingSession!.Recipe.Entities, e => e.EntityId == row.EntityId));
        Assert.All(generated.Surfaces, s => Assert.False(s.IsSkinned));
        Assert.Contains(generated.Package.Document.Diagnostics, d => d.Code == FbxGeneratedBodyBinding.UnboundDiagnostic && d.Severity == CustomModelImportSeverity.Blocker);
        var work = FbxGeneratedBodyBinding.Prepare(generated);
        work = FbxGeneratedBodyBinding.ForVolume(work, grid);
        var binding = AutomaticSkinBinder.Bind(grid, work.Handles, work.Points);
        Assert.True(binding.AllPointsAssigned);
        Assert.False(FbxGeneratedBodyBinding.TryApply(generated, work, binding with { GridFingerprint = new string('f', 64) }, out _));
        Assert.True(FbxGeneratedBodyBinding.TryApply(generated, work, binding, out var bound));
        Assert.All(bound.Surfaces, s => Assert.True(s.IsSkinned));
        Assert.DoesNotContain(bound.Package.Document.Diagnostics, d => d.Code == FbxGeneratedBodyBinding.UnboundDiagnostic);
        Assert.NotNull(bound.Package.Document.RiggingSession!.BindingBackend);
        var before = SurfaceWeights(bound);
        string path = Path.Combine(_directory, "bound.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(bound.Package, path);
        var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.True(GeneratedBodyRig.IsGenerated(reopened.Package.Document));
        Assert.Equal(before, SurfaceWeights(reopened));
        Assert.True(source.Package.SourceFbx.AsSpan().SequenceEqual(reopened.Package.SourceFbx.AsSpan()));
        Assert.All(source.Surfaces.SelectMany(s => s.Vertices), v => Assert.Empty(v.BoneWeights));
        Assert.Equal(source.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position), bound.Surfaces.SelectMany(s => s.Vertices).Select(v => v.Position));
        Assert.True(FbxGeneratedBodyBinding.Prepare(reopened).Points.Length > 0);
    }

    [Fact]
    public void BindingRefusesStaleWorkAndForgedSourceOrFixedAssignments()
    {
        var model = GeneratedWithFixtureGuides();
        var session = model.Package.Document.RiggingSession!;
        var rigid = RiggingSessions.Change(session, session with { Components = session.Components.Select(c => c with {
            BindingMode = RigComponentBindingMode.Rigid, RigidBoneRoleId = "body.head",
        }).ToImmutableArray() }, RiggingEditKind.Components);
        model = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = rigid } } };
        var work = FbxGeneratedBodyBinding.ForFixed(FbxGeneratedBodyBinding.Prepare(model));
        var result = AutomaticSkinBinder.BindFixed(work.SourceSha256, work.Handles, work.Points);
        Assert.True(result.AllPointsAssigned);
        Assert.Null(result.GridFingerprint);
        var changed = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = RiggingSessions.RestoreForUndo(rigid, rigid) } } };
        Assert.False(FbxGeneratedBodyBinding.TryApply(changed, work, result, out _));
        Assert.False(FbxGeneratedBodyBinding.TryApply(model, work, result with { InputFingerprint = new string('f', 64) }, out _));
        Assert.False(FbxGeneratedBodyBinding.TryApply(model, work, result with { GridFingerprint = new string('a', 64) }, out _));
        var forged = work with { Points = work.Points.SetItem(0, work.Points[0] with { Position = Vector3D.Zero }) };
        Assert.Throws<InvalidDataException>(() => FbxGeneratedBodyBinding.TryApply(model, forged, result, out _));
        var wrongFixed = work with { Points = work.Points.SetItem(0, work.Points[0] with { FixedInfluences = [] }) };
        Assert.Throws<InvalidDataException>(() => FbxGeneratedBodyBinding.TryApply(model, wrongFixed, result, out _));
        Assert.True(FbxGeneratedBodyBinding.TryApply(model, work, result, out var rigidModel));
        Assert.All(rigidModel.Surfaces.SelectMany(s => s.Vertices), v => Assert.Equal(1, Assert.Single(v.BoneWeights)));
    }

    [Fact]
    public async Task WorkspaceBuildBindUndoAndReloadAreSeparateModelTransactions()
    {
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        var source = WithFixtureGuides(Source());
        workspace.CommitProjectRestore(new(source, "source.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var wizard = workspace.Conformance;
        Assert.True(wizard.BuildBodyRigCommand.CanExecute(null));
        await wizard.BuildBodyRigCommand.ExecuteAsync(null);
        Assert.True(wizard.HasGeneratedBodyRig, wizard.BodyAuthoringStatus);
        Assert.False(wizard.CanEditBodyGuide);
        Assert.True(wizard.BindBodyGeometryCommand.CanExecute(null));
        wizard.BodyDetectionResolution = 32;
        await wizard.BindBodyGeometryCommand.ExecuteAsync(null);
        Assert.NotNull(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.BindingBackend);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.True(wizard.HasGeneratedBodyRig);
        Assert.Null(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.BindingBackend);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.True(wizard.HasUnriggedSource);
        Assert.Null(workspace.CaptureProjectSession().Model!.Rig);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.True(wizard.HasGeneratedBodyRig);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.NotNull(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.BindingBackend);
    }

    [Fact]
    public void ExistingRigCannotEnterGeneratedBodyReplacement()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "existing.fbx");
        Assert.False(GeneratedBodyRig.IsGenerated(model.Package.Document));
        Assert.Throws<InvalidOperationException>(() => FbxGeneratedBodyBinding.Generate(model));
        Assert.Throws<InvalidDataException>(() => FbxGeneratedBodyBinding.Prepare(model));
    }

    [Fact]
    public async Task ComponentChoicesMustBeSavedAndRigidOnlyBindingNeedsNoAnatomyVolume()
    {
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(WithFixtureGuides(Source()), "components.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        var wizard = workspace.Conformance;
        var component = Assert.Single(wizard.BodyComponents);
        component.UseForAnatomy = false;
        component.BindingMode = RigComponentBindingMode.Rigid;
        component.RigidBoneRoleId = "body.head";
        Assert.True(wizard.HasUnsavedBodyComponents);
        Assert.False(wizard.BuildBodyRigCommand.CanExecute(null));
        Assert.False(wizard.DetectBodyCommand.CanExecute(null));
        wizard.SaveBodyComponentsCommand.Execute(null);
        Assert.False(wizard.HasUnsavedBodyComponents);
        await wizard.BuildBodyRigCommand.ExecuteAsync(null);
        Assert.True(wizard.HasGeneratedBodyRig, wizard.BodyAuthoringStatus);
        await wizard.BindBodyGeometryCommand.ExecuteAsync(null);
        var model = workspace.CaptureProjectSession().Model!;
        Assert.Equal("explicit-fixed", model.Package.Document.RiggingSession!.BindingBackend!.Id);
        Assert.All(model.Surfaces, surface => Assert.All(surface.Vertices, vertex => {
            int slot = Assert.Single(vertex.BoneIndices);
            Assert.Equal("body_head", model.Package.Document.Bones[surface.PaletteBoneIndices[slot]].Name);
            Assert.Equal(1, Assert.Single(vertex.BoneWeights));
        }));
    }

    internal static FbxModelAuthoringImportResult Source()
    {
        var geometry = AnatomicalVolumeFixtures.Create().Geometry.Components[0];
        var vertices = geometry.Geometry.ControlPoints.SelectMany(p => new[] { p.X * 100, p.Y * 100, p.Z * 100 }).ToArray();
        var polygons = geometry.Triangles.SelectMany(t => new long[] { t.A, t.B, -t.C - 1 }).ToArray();
        return FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx([], meshVertices: vertices, meshPolygons: polygons), "generic-body.fbx");
    }

    internal static FbxModelAuthoringImportResult WithFixtureGuides(FbxModelAuthoringImportResult source)
    {
        var p = AnatomicalVolumeFixtures.Create().ExpectedJointCoordinates;
        var positions = new Dictionary<string, Vector3D> {
            ["body.pelvis"] = p["pelvis"], ["body.spine.0"] = Vector3D.Lerp(p["pelvis"], p["chest"], .3),
            ["body.spine.1"] = Vector3D.Lerp(p["pelvis"], p["chest"], .65), ["body.spine.2"] = p["chest"],
            ["body.neck.0"] = p["neck"], ["body.head"] = p["head"],
            ["arm.left.upper"] = p["left_shoulder"], ["arm.left.lower"] = p["left_elbow"], ["hand.left"] = p["left_wrist"],
            ["arm.right.upper"] = p["right_shoulder"], ["arm.right.lower"] = p["right_elbow"], ["hand.right"] = p["right_wrist"],
            ["leg.left.upper"] = p["left_hip"], ["leg.left.lower"] = p["left_knee"], ["foot.left"] = p["left_ankle"],
            ["leg.right.upper"] = p["right_hip"], ["leg.right.lower"] = p["right_knee"], ["foot.right"] = p["right_ankle"],
        };
        var session = RiggingSessions.Create(source.Package.Document, RigStudioEntryPath.AutoRigBiped) with {
            Landmarks = positions.Select(pair => new RigLandmark { RoleId = pair.Key, Position = pair.Value, Provenance = RigEvidenceKind.UserOverride }).ToImmutableArray(),
        };
        return source with { Package = source.Package with { Document = source.Package.Document with { RiggingSession = session } } };
    }

    private static FbxModelAuthoringImportResult GeneratedWithFixtureGuides() => FbxGeneratedBodyBinding.Generate(WithFixtureGuides(Source()));

    private static string[] SurfaceWeights(FbxModelAuthoringImportResult model) => model.Surfaces.SelectMany(s => s.Vertices.Select((v, i) =>
        s.SourceGeometry!.Id + ":" + s.SourceCorners[i].PolygonVertexIndex + "=" + string.Join(";", v.BoneIndices.Select((slot, w) =>
            model.Package.Document.Bones[s.PaletteBoneIndices[slot]].Name + ":" + v.BoneWeights[w].ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)))).ToArray();

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
