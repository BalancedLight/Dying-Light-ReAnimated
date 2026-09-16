using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxRigGeometryEvidenceTests
{
    [Fact]
    public void CurrentBindingEvidenceIsDistinctFromPreservedOriginalInfluences()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceWeightFixture(), "weights.fbx");
        var original = SourceSkinInfluenceRegions.Build(FbxSourceGeometryAnalysis.Build(model), model.Package.Document.Bones
            .ToDictionary(b => b.FbxObjectId.ToString(CultureInfo.InvariantCulture), static b => b.Index));
        var current = FbxRigGeometryEvidence.Build(model);
        Assert.Equal(6, original.Regions.Count);
        Assert.Equal(4, current.Supports.Length);
        Assert.Equal("current-render-binding", current.GeometryBasis);
        var surface = model.Surfaces[0];
        double expectedArea = Vector3D.Cross(surface.Vertices[1].Position - surface.Vertices[0].Position,
            surface.Vertices[2].Position - surface.Vertices[0].Position).Length / 2;
        Assert.Equal(expectedArea, current.Supports.Sum(static s => s.SurfaceMass), 12);
        var shift = new Vector3D(0.5, -0.2, 1.0);
        var edited = model with { Surfaces = model.Surfaces.Select(s => s with { Vertices = s.Vertices
            .Select(v => v with { Position = v.Position + shift }).ToImmutableArray() }).ToImmutableArray() };
        var after = FbxRigGeometryEvidence.Build(edited);
        foreach (var before in current.Supports)
        {
            var shifted = after.Supports.Single(s => s.BoneIndex == before.BoneIndex);
            Assert.True((before.Centroid + shift - shifted.Centroid).Length < 1e-12);
            Assert.Equal(before.SurfaceMass, shifted.SurfaceMass, 12);
        }
        Assert.Same(model.Surfaces[0].SourceGeometry, edited.Surfaces[0].SourceGeometry);
    }

    [Fact]
    public void CurrentGeometryHonorsAnatomyDecisionsAndRejectsStaleRigPairs()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, true), "parts.fbx");
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AdaptExistingRig);
        session = session with { Components = session.Components.Select(c => c with { UseForAnatomy = false }).ToImmutableArray() };
        var excluded = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
        Assert.Empty(FbxRigGeometryEvidence.Build(excluded).Supports);
        var bone = model.Package.Document.Bones[0];
        var changedDocument = model.Package.Document with { Bones = model.Package.Document.Bones.SetItem(0, bone with {
            LocalBindTransform = bone.LocalBindTransform with { Translation = Vector3D.UnitX },
            ExactLocalBindMatrix = TransformMatrix.CreateTranslation(Vector3D.UnitX) }) };
        changedDocument = changedDocument with { RigSignature = CustomModelContractSignatures.ComputeRig(changedDocument.Bones) };
        Assert.Throws<InvalidDataException>(() => FbxRigGeometryEvidence.Build(model with { Package = model.Package with { Document = changedDocument } }));
        Assert.ThrowsAny<OperationCanceledException>(() => FbxRigGeometryEvidence.Build(model, new(true)));
    }

    [Fact]
    public void MaterialSplitsDoNotDuplicateSurfaceMassOrSourcePointSupport()
    {
        var whole = FbxRigGeometryEvidence.Build(FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, false), "whole.fbx"));
        var split = FbxRigGeometryEvidence.Build(FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, true), "split.fbx"));
        Assert.Equal(whole.Supports.Length, split.Supports.Length);
        for (int i = 0; i < whole.Supports.Length; i++)
        {
            Assert.Equal(whole.Supports[i].BoneIndex, split.Supports[i].BoneIndex);
            Assert.Equal(whole.Supports[i].ControlPointCount, split.Supports[i].ControlPointCount);
            Assert.Equal(whole.Supports[i].SurfaceMass, split.Supports[i].SurfaceMass, 12);
            Assert.True((whole.Supports[i].Centroid - split.Supports[i].Centroid).Length < 1e-12);
        }
    }
}
