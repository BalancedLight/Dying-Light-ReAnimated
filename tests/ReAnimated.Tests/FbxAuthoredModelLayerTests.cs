using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class FbxAuthoredModelLayerTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void ActualConformanceBakeAndWeightTransferSurvivePackageReplay()
    {
        var source = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "rigged-source.fbx");
        var sourceBones = source.Package.Document.Bones;
        var sourceGlobals = new TransformMatrix[sourceBones.Length];
        foreach (var bone in sourceBones) sourceGlobals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : sourceGlobals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var offsets = sourceBones.Select(b => TransformMatrix.CreateTranslation(new(0, .05 + .01 * b.Index, 0))).ToImmutableArray();
        var targetGlobals = sourceBones.Select(b => offsets[b.Index] * sourceGlobals[b.Index]).ToArray();
        var entities = sourceBones.Select(b => new Dl1RigTemplateEntity { Index = b.Index, Name = "authored_" + b.Name, ParentIndex = b.ParentIndex,
            Kind = b.Kind, IsDeform = true, GlobalRestMatrix = targetGlobals[b.Index],
            LocalRestMatrix = b.ParentIndex < 0 ? targetGlobals[b.Index] : targetGlobals[b.ParentIndex].InvertedAffine() * targetGlobals[b.Index] }).ToImmutableArray();
        var template = new Dl1RigTemplate("generic", "generic_reference", new string('b', 64), entities);
        var landmark = new RigLandmarkSolution { UniformScale = 1, PelvisAnchor = Vector3D.Zero, Samples = [], RegionFits = [], ProportionResidual = 0, WorstSampleDeviation = 0,
            Evidence = "Explicit synthetic translation used to test conformance persistence." };
        var fit = new RigConformanceResult(template, landmark, 0, sourceBones.Select(b => new RigConformedBone {
            Index = b.Index, Name = entities[b.Index].Name, ParentIndex = b.ParentIndex, Kind = b.Kind, IsDeform = true,
            Disposition = RigBoneDisposition.Mapped, SourceBoneIndex = b.Index, TemplateIndex = b.Index, Position = targetGlobals[b.Index].Translation,
            Orientation = TransformMatrix.Identity, OffsetFromSourceJoint = offsets[b.Index].Translation.Length, SegmentRatio = 1,
        }), [], new RigRestPoseTransferResult { SkinningTransforms = offsets, PosedGlobals = targetGlobals.ToImmutableArray(), MaximumJointResidual = 0 });
        var settings = new CustomModelRigConformance { TemplateId = template.TemplateId, TemplateProfileName = "generic", TemplateSourceResourceName = "generic_reference",
            TemplateFingerprint = new string('b', 64), SourceFbxSha256 = source.Package.Document.Source.ContentSha256 };
        var conformed = Dl1RigConformanceApplier.Apply(source, fit, settings);
        Assert.NotEqual(source.Package.Document.RigSignature, conformed.Package.Document.RigSignature);
        Assert.NotEqual(source.Surfaces[0].Vertices[0].Position, conformed.Surfaces[0].Vertices[0].Position);
        AssertEquivalent(conformed, SaveReopen(FbxAuthoredModelLayer.Capture(conformed)));
    }

    [Fact]
    public void WorkspaceMetadataAndProjectHandoffRetainAuthoredPayload()
    {
        var model = FbxAuthoredModelLayer.Capture(Author(Source()));
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(model, "authored.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.ModelName = "Renamed authored model";
        workspace.FlipTextureCoordinateV = !workspace.FlipTextureCoordinateV;
        var changed = workspace.CaptureProjectSession().Model!;
        Assert.True(model.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(changed.Package.AuthoredLayerPayload.AsSpan()));
        var persistence = workspace.CreatePersistencePayload()!;
        string path = Path.Combine(_directory, "handoff.dlrmodel");
        File.WriteAllBytes(path, persistence.PackageBytes.ToArray());
        var restored = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal("Renamed authored model", restored.Package.Document.Name);
        AssertEquivalent(changed, restored);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.True(model.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload.AsSpan()));
    }

    [Fact]
    public void GeneratedRigWeightsRestEditsSplitNormalsAndMorphsSurviveSaveReopen()
    {
        var source = Source();
        var authored = Author(source);
        var captured = FbxAuthoredModelLayer.Capture(authored);
        Assert.NotNull(captured.Package.Document.AuthoredLayer);
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(captured.Package)!;
        Assert.NotEmpty(layer.Components);
        Assert.All(layer.Components.SelectMany(c => c.Points), p => Assert.True(p.ReplaceWeights));
        Assert.True(source.Package.SourceFbx.AsSpan().SequenceEqual(captured.Package.SourceFbx.AsSpan()));
        var roundTrip = SaveReopen(captured);
        Assert.Equal<CustomModelBone>(captured.Package.Document.Bones, roundTrip.Package.Document.Bones);
        Assert.Equal(captured.Package.Document.RigSignature, roundTrip.Package.Document.RigSignature);
        Assert.Equal(captured.Package.Document.RigConformance, roundTrip.Package.Document.RigConformance);
        AssertEquivalent(authored, roundTrip);
        Assert.Null(source.Rig);
        Assert.All(source.Surfaces.SelectMany(s => s.Vertices), v => Assert.Empty(v.BoneWeights));
        var recaptured = FbxAuthoredModelLayer.Capture(roundTrip);
        Assert.True(captured.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(recaptured.Package.AuthoredLayerPayload.AsSpan()));
        var prepared = Dl1CustomModelRigPreparer.Prepare(roundTrip);
        Assert.NotEmpty(prepared.Contract.Nodes);
    }

    [Fact]
    public void AllZeroMorphNormalArrayPresenceSurvives()
    {
        var source = Source();
        var authored = Author(source);
        authored = authored with { Surfaces = authored.Surfaces.Select(s => s with {
            MorphTargets = s.MorphTargets.Select(m => m with { NormalDeltas = Enumerable.Repeat(Vector3D.Zero, s.Vertices.Length).ToImmutableArray() }).ToImmutableArray(),
        }).ToImmutableArray() };
        var captured = FbxAuthoredModelLayer.Capture(authored);
        Assert.Contains(CustomModelPackageSerializer.ValidateAuthoredLayer(captured.Package)!.Components.SelectMany(c => c.Morphs), m => m.HasNormalDeltas == true);
        var restored = SaveReopen(captured);
        Assert.All(restored.Surfaces.SelectMany(s => s.MorphTargets), m => {
            Assert.NotEmpty(m.NormalDeltas); Assert.All(m.NormalDeltas, d => Assert.Equal(Vector3D.Zero, d));
        });
    }

    [Fact]
    public void StableBoneReferencesSurviveReorderingAndRecapture()
    {
        var first = FbxAuthoredModelLayer.Capture(Author(Source()));
        var oldLayer = CustomModelPackageSerializer.ValidateAuthoredLayer(first.Package)!;
        var bones = first.Package.Document.Bones;
        var reordered = ImmutableArray.Create(bones[0], bones[2] with { Index = 1 }, bones[1] with { Index = 2 });
        var document = first.Package.Document with { Bones = reordered, RigSignature = CustomModelContractSignatures.ComputeRig(reordered) };
        var changed = first with { Package = first.Package with { Document = document }, Rig = document.CreateRigDefinition(),
            Surfaces = first.Surfaces.Select(s => s with { PaletteBoneIndices = s.PaletteBoneIndices.Select(i => i == 1 ? 2 : i == 2 ? 1 : i).ToImmutableArray() }).ToImmutableArray() };
        var recaptured = FbxAuthoredModelLayer.Capture(changed);
        var newLayer = CustomModelPackageSerializer.ValidateAuthoredLayer(recaptured.Package)!;
        Assert.Equal(oldLayer.Bones.OrderBy(b => b.Name), newLayer.Bones.OrderBy(b => b.Name));
        AssertEquivalent(changed, SaveReopen(recaptured));
    }

    [Fact]
    public void PackageRefusesMissingCorruptAndWrongRigPayloads()
    {
        var model = FbxAuthoredModelLayer.Capture(Author(Source()));
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Serialize(model.Package with { AuthoredLayerPayload = [] }));
        var corrupted = model.Package.AuthoredLayerPayload.SetItem(0, 0);
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Serialize(model.Package with { AuthoredLayerPayload = corrupted }));
        var bones = model.Package.Document.Bones.SetItem(1, model.Package.Document.Bones[1] with { Name = "renamed_without_rebinding" });
        var wrongRig = model.Package.Document with { Bones = bones, RigSignature = CustomModelContractSignatures.ComputeRig(bones) };
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Serialize(model.Package with { Document = wrongRig }));
        Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.PreviewReimport(model.Package,
            FbxCustomModelMorphImportTests.CreateMorphFbx(["expression"], firstShapeDeltaX: .6, splitCorners: true), "changed.fbx"));
        var unchanged = FbxModelAuthoringImporter.PreviewReimport(model.Package, model.Package.SourceFbx.AsSpan(), "same.fbx");
        Assert.Equal(CustomModelReimportContractChange.None, unchanged.Changes);
        AssertEquivalent(model, unchanged.Replacement);
    }

    [Fact]
    public void CompilerFingerprintPreservesAndIncludesTheAuthoredLayerPayload()
    {
        var model = FbxAuthoredModelLayer.Capture(Author(Source()));
        string before = Dl1OfficialModelCompiler.CalculateInputFingerprint(model, "generic", "default", null);
        Assert.Equal(64, before.Length);
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(model.Package)!;
        var component = layer.Components[0];
        var changed = layer with { Components = layer.Components.SetItem(0, component with {
            Points = component.Points.SetItem(0, component.Points[0] with { PositionDelta = component.Points[0].PositionDelta + Vector3D.UnitX * .001 }),
        }) };
        var replacement = ReplaceLayer(model, changed);
        Assert.NotEqual(before, Dl1OfficialModelCompiler.CalculateInputFingerprint(replacement, "generic", "default", null));
        Assert.Equal(before, Dl1OfficialModelCompiler.CalculateInputFingerprint(SaveReopen(model), "generic", "default", null));
    }

    [Fact]
    public void SourceFingerprintAndOldUnreplayableConformanceCannotSilentlyFallBack()
    {
        var model = FbxAuthoredModelLayer.Capture(Author(Source()));
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(model.Package)!;
        var wrong = ReplaceLayer(model, layer with { SourceGeometryFingerprint = new string('f', 64) });
        Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.ImportPackage(wrong.Package));
        var old = model.Package with { Document = model.Package.Document with { AuthoredLayer = null }, AuthoredLayerPayload = [] };
        Assert.Throws<CustomModelFormatException>(() => FbxModelAuthoringImporter.ImportPackage(old));
    }

    [Fact]
    public void ReplayRejectsOverflowingMorphSums()
    {
        var source = FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx(["expression"], firstShapeDeltaX: double.MaxValue), "large-source.fbx");
        var captured = FbxAuthoredModelLayer.Capture(source);
        var surface = source.Surfaces[0];
        var layer = CustomModelPackageSerializer.ValidateAuthoredLayer(captured.Package)!;
        var edits = new AuthoredComponentEdits(surface.SourceGeometry!.Id, surface.SourceGeometry.ControlPoints.Length,
            surface.SourceCorners.Max(c => c.PolygonVertexIndex) + 1, [], [],
            [new(surface.MorphTargets[0].DescriptorHash, [new(0, new(double.MaxValue, 0, 0))], [])]);
        var overflowing = ReplaceLayer(captured, layer with { Components = [edits] });
        Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.ImportPackage(overflowing.Package));
    }

    internal static FbxModelAuthoringImportResult Source() => FbxModelAuthoringImporter.Import(
        FbxCustomModelMorphImportTests.CreateMorphFbx(["expression"], splitCorners: true), "source-with-morph.fbx");

    internal static FbxModelAuthoringImportResult Author(FbxModelAuthoringImportResult source)
    {
        CustomModelBone[] bones = [Bone(0, "root", -1, Vector3D.Zero), Bone(1, "joint_a", 0, new(0, .02, 0)), Bone(2, "joint_b", 0, new(.02, 0, 0))];
        var document = source.Package.Document with { Bones = bones.ToImmutableArray(), RigMode = CustomModelRigMode.ExactFbxRig,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones.ToImmutableArray()), RigConformance = new() {
                TemplateId = "synthetic-reference", TemplateProfileName = "generic", TemplateSourceResourceName = "generic_rig",
                TemplateFingerprint = new string('b', 64), SourceFbxSha256 = source.Package.Document.Source.ContentSha256,
            } };
        var surfaces = source.Surfaces.Select(s => {
            var vertices = s.Vertices.Select((v, i) => {
                var corner = s.SourceCorners[i];
                return v with { Position = v.Position + new Vector3D(.001 * (corner.ControlPointIndex + 1), .003, 0),
                    Normal = (v.Normal + new Vector3D(.01 * (corner.PolygonVertexIndex + 1), .02, 0)).Normalized(),
                    BoneIndices = [0, 1], BoneWeights = [.25, .75] };
            }).ToImmutableArray();
            return s with { Vertices = vertices, IsSkinned = true, PaletteBoneIndices = [2, 1], InverseBindMatrices = [
                new(1, .1, 0, -.02, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1), TransformMatrix.CreateTranslation(new(0, -.02, 0)) ],
                MorphTargets = s.MorphTargets.Select(m => m with {
                    PositionDeltas = m.PositionDeltas.Select((d, i) => d + Vector3D.UnitY * (.001 * (s.SourceCorners[i].ControlPointIndex + 1))).ToImmutableArray(),
                    NormalDeltas = s.SourceCorners.Select(c => new Vector3D(.001 * (c.PolygonVertexIndex + 1), 0, 0)).ToImmutableArray(),
                }).ToImmutableArray() };
        }).ToImmutableArray();
        return source with { Package = source.Package with { Document = document }, Rig = document.CreateRigDefinition(), Surfaces = surfaces };
    }

    private static CustomModelBone Bone(int index, string name, int parent, Vector3D position) => new() {
        Index = index, Name = name, ParentIndex = parent, Kind = index == 0 ? BoneKind.Root : BoneKind.Deform, IsWeighted = index != 0,
        LocalBindTransform = new(position, QuaternionD.Identity, Vector3D.One), ExactLocalBindMatrix = TransformMatrix.CreateTranslation(position),
    };

    private FbxModelAuthoringImportResult SaveReopen(FbxModelAuthoringImportResult model)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(model.Package, path);
        return FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
    }

    private static FbxModelAuthoringImportResult ReplaceLayer(FbxModelAuthoringImportResult model, AuthoredModelLayer layer)
    {
        var payload = AuthoredModelLayerCodec.Serialize(layer);
        return model with { Package = model.Package with { AuthoredLayerPayload = payload,
            Document = model.Package.Document with { AuthoredLayer = model.Package.Document.AuthoredLayer! with {
                PayloadLength = payload.Length, ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())),
            } } } };
    }

    private static void AssertEquivalent(FbxModelAuthoringImportResult expected, FbxModelAuthoringImportResult actual)
    {
        Assert.True(expected.Package.SourceFbx.AsSpan().SequenceEqual(actual.Package.SourceFbx.AsSpan()));
        Assert.Equal(expected.Surfaces.Sum(s => s.Indices.Length), actual.Surfaces.Sum(s => s.Indices.Length));
        var expectedVertices = Vertices(expected); var actualVertices = Vertices(actual);
        Assert.Equal(expectedVertices.Keys.Order(), actualVertices.Keys.Order());
        foreach (var (id, value) in expectedVertices)
        {
            var other = actualVertices[id];
            Assert.InRange((value.Vertex.Position - other.Vertex.Position).Length, 0, 1e-12);
            Assert.InRange((value.Vertex.Normal - other.Vertex.Normal).Length, 0, 1e-12);
            Assert.Equal(value.Weights.OrderBy(w => w.Key), other.Weights.OrderBy(w => w.Key));
            Assert.Equal(value.Binds.OrderBy(w => w.Key), other.Binds.OrderBy(w => w.Key));
            foreach (var morph in value.Morphs)
            {
                var match = other.Morphs.Single(m => m.Descriptor == morph.Descriptor);
                Assert.InRange((morph.Position - match.Position).Length, 0, 1e-12);
                Assert.InRange((morph.Normal - match.Normal).Length, 0, 1e-12);
            }
        }
    }

    private sealed record VertexState(FbxModelVertex Vertex, Dictionary<string, double> Weights, Dictionary<string, TransformMatrix> Binds,
        (uint Descriptor, Vector3D Position, Vector3D Normal)[] Morphs);
    private static Dictionary<(string, int), VertexState> Vertices(FbxModelAuthoringImportResult model) => model.Surfaces.SelectMany(s => s.Vertices.Select((v, i) => (
        Id: (s.SourceGeometry!.Id, s.SourceCorners[i].PolygonVertexIndex), Value: new VertexState(v,
            v.BoneIndices.Select((slot, w) => (Name: model.Package.Document.Bones[s.PaletteBoneIndices[slot]].Name, Weight: v.BoneWeights[w])).ToDictionary(p => p.Name, p => p.Weight),
            v.BoneIndices.ToDictionary(slot => model.Package.Document.Bones[s.PaletteBoneIndices[slot]].Name, slot => s.InverseBindMatrices[slot]),
            s.MorphTargets.Select(m => (m.DescriptorHash, m.PositionDeltas[i], m.NormalDeltas.IsDefaultOrEmpty ? Vector3D.Zero : m.NormalDeltas[i])).ToArray()))))
        .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Value);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
