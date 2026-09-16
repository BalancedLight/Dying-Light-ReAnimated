using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class FbxSkinWeightAuthoringTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void CorrectionFollowsSourcePointAcrossSeamsAndRetainsMorphNormalsAndExpertBinds()
    {
        var source = Model();
        var snapshot = FbxSkinWeightAuthoring.Inspect(source);
        Assert.True(source.Surfaces.Sum(s => s.Vertices.Length) > snapshot.Points.Length);
        var chosen = snapshot.Influences.Single(b => b.Name == "joint_a");
        var preview = FbxSkinWeightAuthoring.Preview(snapshot, [new(0, 1)], new() { TargetInfluence = chosen.EntityId, TargetWeight = .5 });
        Assert.True(preview.Correction.CanApply);
        Assert.Single(preview.Correction.Changes);
        Assert.True(FbxSkinWeightAuthoring.TryApply(source, preview, out var edited));
        var after = FbxSkinWeightAuthoring.Inspect(edited);
        Assert.Equal(.5, after.Points[0].Weights.Single(w => w.HandleId == chosen.EntityId).Weight);
        Assert.Equal(snapshot.Points.Skip(1).Select(p => p.Weights), after.Points.Skip(1).Select(p => p.Weights), ImmutableArrayWeightComparer.Instance);
        Assert.True(source.Package.SourceFbx.AsSpan().SequenceEqual(edited.Package.SourceFbx.AsSpan()));
        AssertSurfacePayload(source, edited);
        AssertSurfacePayload(edited, Reopen(edited));
        Assert.NotNull(edited.Package.Document.AuthoredLayer);
        Assert.Empty(edited.Package.Document.RiggingSession!.ValidationHistory);
    }

    [Fact]
    public void StalePreviewRefusesChangedModelButMetadataCanBeReboundExplicitly()
    {
        var model = Model();
        var snapshot = FbxSkinWeightAuthoring.Inspect(model);
        var preview = FbxSkinWeightAuthoring.Preview(snapshot, [new(0, 1)], new() { TargetInfluence = snapshot.Influences[1].EntityId, TargetWeight = .5 });
        var renamed = model with { Package = model.Package with { Document = model.Package.Document with { Name = "Renamed" } } };
        Assert.False(FbxSkinWeightAuthoring.TryApply(renamed, preview, out _));
        Assert.True(FbxSkinWeightAuthoring.TryRefreshMetadata(snapshot, preview, renamed, out _, out var refreshedPreview));
        Assert.True(FbxSkinWeightAuthoring.TryApply(renamed, refreshedPreview!, out _));
        var changed = model with { Surfaces = model.Surfaces.SetItem(0, model.Surfaces[0] with { Id = "different" }) };
        Assert.False(FbxSkinWeightAuthoring.TryRefreshMetadata(snapshot, preview, changed, out _, out _));
    }

    [Fact]
    public void LocksIncludingZerosPersistAndRejectConflictingCorrections()
    {
        var model = Model();
        var snapshot = FbxSkinWeightAuthoring.Inspect(model);
        var influence = snapshot.Influences[1];
        Assert.True(FbxSkinWeightAuthoring.TrySetLocks(model, snapshot, [0], influence.EntityId, true, out var locked));
        Assert.Equal(model.Package.AuthoredLayerPayload, locked.Package.AuthoredLayerPayload);
        locked = Reopen(locked);
        Assert.Single(locked.Package.Document.RiggingSession!.WeightLocks);
        var observed = FbxSkinWeightAuthoring.Inspect(locked);
        var preview = FbxSkinWeightAuthoring.Preview(observed, [new(0, 1)], new() { TargetInfluence = influence.EntityId, TargetWeight = .5 });
        Assert.False(preview.Correction.CanApply);
        Assert.Throws<InvalidDataException>(() => FbxSkinWeightAuthoring.TryApply(locked, preview, out _));
        Assert.True(FbxSkinWeightAuthoring.TrySetLocks(locked, observed, [0], influence.EntityId, false, out var unlocked));
        Assert.Empty(unlocked.Package.Document.RiggingSession!.WeightLocks);
        var zero = FbxSkinWeightAuthoring.Inspect(unlocked);
        Assert.True(FbxSkinWeightAuthoring.TrySetLocks(unlocked, zero, [0], zero.Influences[0].EntityId, true, out var zeroLocked));
        var zeroSnapshot = FbxSkinWeightAuthoring.Inspect(zeroLocked);
        Assert.Contains(zero.Influences[0].EntityId, zeroSnapshot.Points[0].LockedInfluences);
        Assert.DoesNotContain(zeroSnapshot.Points[0].Weights, w => w.HandleId == zero.Influences[0].EntityId);
    }

    [Fact]
    public void ConflictingSeamWeightsAndInvalidSourceCornersAreRefused()
    {
        var model = Model();
        var surface = model.Surfaces[0];
        var duplicate = surface.SourceCorners.Select((c, i) => (c.ControlPointIndex, i)).GroupBy(p => p.ControlPointIndex).First(g => g.Count() > 1).Last().i;
        var conflict = model with { Surfaces = model.Surfaces.SetItem(0, surface with {
            Vertices = surface.Vertices.SetItem(duplicate, surface.Vertices[duplicate] with { BoneWeights = [.5, .5] }),
        }) };
        Assert.Throws<InvalidDataException>(() => FbxSkinWeightAuthoring.Inspect(conflict));
        Assert.Throws<InvalidDataException>(() => FbxSkinWeightAuthoring.Inspect(model with { Surfaces = model.Surfaces.SetItem(0, surface with { SourceCorners = [] }) }));
    }

    [Fact]
    public void HeatmapUsesPreviewOnlyUvsAndPreservesSkinningAndMorphs()
    {
        var model = Model();
        var prepared = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.SourceFbx);
        var snapshot = FbxSkinWeightAuthoring.Inspect(model);
        var selected = snapshot.Influences[1];
        var preview = FbxSkinWeightAuthoring.Preview(snapshot, [new(0, 1)], new() { TargetInfluence = selected.EntityId, TargetWeight = .5 });
        var heatmap = SkinWeightHeatmapBuilder.Build(model, prepared.Meshes, selected.BoneIndex, preview);
        var mesh = heatmap[0];
        Assert.NotEqual(prepared.Meshes[0].Vertices.Span[0].TextureCoordinate, mesh.Vertices.Span[0].TextureCoordinate);
        Assert.Equal(prepared.Meshes[0].Vertices.Span[0].BoneWeights, mesh.Vertices.Span[0].BoneWeights);
        Assert.Equal(prepared.Meshes[0].Vertices.Span[0].Position, mesh.Vertices.Span[0].Position);
        Assert.Same(prepared.Meshes[0].MorphTargets, mesh.MorphTargets);
        Assert.Equal(255, mesh.BaseColorTexture!.BaseMipBytes.Span[0]); // Blue at zero in BGRA.
        Assert.Equal(255, mesh.BaseColorTexture.BaseMipBytes.Span[255 * 4 + 2]); // Red at one.
        Assert.Equal(.5f, mesh.Vertices.Span[0].TextureCoordinate.X);
        Assert.Null(model.Package.Document.RiggingSession);
    }

    [Fact]
    public async Task WorkspacePreviewApplyAndLockAreSeparateUndoableTransactions()
    {
        var model = Model();
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(model, "weights.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        var wizard = workspace.Conformance;
        await wizard.InspectWeightsCommand.ExecuteAsync(null);
        wizard.SelectedWeightInfluence = wizard.WeightInfluences.Single(i => i.Name == "joint_a");
        wizard.WeightSourcePoints = "0";
        wizard.WeightTargetValue = .5;
        await wizard.PreviewWeightCorrectionCommand.ExecuteAsync(null);
        Assert.Single(wizard.WeightPointReviews);
        Assert.True(wizard.ApplyWeightCorrectionCommand.CanExecute(null), wizard.WeightEditingStatus);
        Assert.Equal(model.Surfaces, workspace.CaptureProjectSession().Model!.Surfaces);
        Assert.Equal(.75, Weight(workspace, "joint_a"));
        workspace.ModelName = "Renamed while reviewing";
        Assert.True(wizard.ApplyWeightCorrectionCommand.CanExecute(null));
        await wizard.ApplyWeightCorrectionCommand.ExecuteAsync(null);
        Assert.Equal(.5, Weight(workspace, "joint_a"));
        await wizard.LockWeightInfluenceCommand.ExecuteAsync(null);
        Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.WeightLocks);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.WeightLocks);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(.75, Weight(workspace, "joint_a"));
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(.5, Weight(workspace, "joint_a"));
    }

    private static double Weight(ModelsWorkspaceViewModel workspace, string name)
    {
        var snapshot = FbxSkinWeightAuthoring.Inspect(workspace.CaptureProjectSession().Model!);
        var influence = snapshot.Influences.Single(i => i.Name == name);
        return snapshot.Points[0].Weights.Single(w => w.HandleId == influence.EntityId).Weight;
    }

    [Fact]
    public void GeneratedRebindingCarriesSavedFractionsAndRefusesConflictingRigidAssignment()
    {
        var model = FbxGeneratedBodyBinding.Generate(GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source()));
        var initial = model.Package.Document.RiggingSession!;
        var rigid = initial with { Components = initial.Components.Select(c => c with { BindingMode = RigComponentBindingMode.Rigid, RigidBoneRoleId = "body.head" }).ToImmutableArray() };
        model = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = rigid } } };
        var work = FbxGeneratedBodyBinding.ForFixed(FbxGeneratedBodyBinding.Prepare(model));
        Assert.True(FbxGeneratedBodyBinding.TryApply(model, work, AutomaticSkinBinder.BindFixed(work.SourceSha256, work.Handles, work.Points), out model));
        var observed = FbxSkinWeightAuthoring.Inspect(model);
        var head = observed.Influences.Single(b => b.Name == "body_head");
        Assert.True(FbxSkinWeightAuthoring.TrySetLocks(model, observed, [0], head.EntityId, true, out model));
        var session = model.Package.Document.RiggingSession!;
        var automatic = session with { Components = session.Components.Select(c => c with { BindingMode = RigComponentBindingMode.Automatic, RigidBoneRoleId = null }).ToImmutableArray() };
        model = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = automatic } } };
        var automaticWork = FbxGeneratedBodyBinding.Prepare(model);
        var locked = automaticWork.Points.Single(p => p.ComponentId == observed.Points[0].ComponentId && p.ControlPointIndex == observed.Points[0].ControlPointIndex);
        Assert.Equal(new FixedSkinInfluence(head.EntityId, 1), Assert.Single(locked.FixedInfluences));
        var conflicting = automatic with { Components = automatic.Components.Select(c => c with { BindingMode = RigComponentBindingMode.Rigid, RigidBoneRoleId = "body.pelvis" }).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => FbxGeneratedBodyBinding.Prepare(model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = conflicting } } }));
    }
    internal static FbxModelAuthoringImportResult Model() => FbxAuthoredModelLayer.Capture(FbxAuthoredModelLayerTests.Author(FbxAuthoredModelLayerTests.Source()));
    private FbxModelAuthoringImportResult Reopen(FbxModelAuthoringImportResult model)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(model.Package, path);
        return FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
    }
    private static void AssertSurfacePayload(FbxModelAuthoringImportResult before, FbxModelAuthoringImportResult after)
    {
        var original = before.Surfaces.SelectMany(s => s.Vertices.Select((v, i) => (Key: (s.SourceGeometry!.Id, s.SourceCorners[i].PolygonVertexIndex), s, v, i))).ToDictionary(p => p.Key);
        foreach (var s in after.Surfaces)
            for (int i = 0; i < s.Vertices.Length; i++)
            {
                var prior = original[(s.SourceGeometry!.Id, s.SourceCorners[i].PolygonVertexIndex)];
                var v = s.Vertices[i];
                Assert.Equal(prior.v.Position, v.Position); Assert.Equal(prior.v.Normal, v.Normal);
                Assert.Equal(prior.v.TextureCoordinateU, v.TextureCoordinateU); Assert.Equal(prior.v.TextureCoordinateV, v.TextureCoordinateV);
                foreach (var morph in s.MorphTargets)
                {
                    var old = prior.s.MorphTargets.Single(m => m.DescriptorHash == morph.DescriptorHash);
                    Assert.Equal(old.PositionDeltas[prior.i], morph.PositionDeltas[i]);
                    Assert.Equal(old.NormalDeltas[prior.i], morph.NormalDeltas[i]);
                }
                foreach (int slot in v.BoneIndices)
                {
                    int global = s.PaletteBoneIndices[slot];
                    int oldSlot = prior.s.PaletteBoneIndices.IndexOf(global);
                    if (oldSlot >= 0) Assert.Equal(prior.s.InverseBindMatrices[oldSlot], s.InverseBindMatrices[slot]);
                }
            }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class ImmutableArrayWeightComparer : IEqualityComparer<ImmutableArray<GeneratedSkinInfluence>>
    {
        public static readonly ImmutableArrayWeightComparer Instance = new();
        public bool Equals(ImmutableArray<GeneratedSkinInfluence> x, ImmutableArray<GeneratedSkinInfluence> y) => x.SequenceEqual(y);
        public int GetHashCode(ImmutableArray<GeneratedSkinInfluence> obj) => obj.Length;
    }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
