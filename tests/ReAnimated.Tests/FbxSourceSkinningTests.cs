using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;

namespace ReAnimated.Tests;

public sealed class FbxSourceSkinningTests
{
    [Fact]
    public void OriginalEntriesSurviveJointAggregationThresholdAndTopFourReduction()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceWeightFixture(), "weights.fbx");
        var geometry = Assert.Single(FbxSourceGeometryAnalysis.Build(model).Components).Geometry;
        var skin = Assert.IsType<GeometrySourceSkinning>(geometry.Skinning);
        Assert.True(skin.HasSkinDeformer);
        Assert.Equal(geometry.ControlPoints.Length, skin.ControlPoints.Length);
        var point = skin.ControlPoints[0];
        Assert.Equal(7, point.Influences.Length);
        Assert.Equal(0, Assert.Single(point.Influences.Where(w => w.DeformerId == "31")).SourceEntryIndex);
        Assert.Equal(1, Assert.Single(skin.ControlPoints[1].Influences.Where(w => w.DeformerId == "31")).SourceEntryIndex);
        Assert.Equal<double>([0.5, 0.1], point.Influences.Where(w => w.JointId == "2").Select(w => w.Weight));
        var secondSkin = Assert.Single(point.Influences.Where(w => w.SkinId == "120"));
        Assert.Equal("2", secondSkin.JointId);
        Assert.Equal("121", secondSkin.DeformerId);
        Assert.True(secondSkin.Retained);
        Assert.Equal(1.5, point.RetainedWeight, 12);
        Assert.Equal(0.1 + 1e-15, point.DiscardedWeight, 15);
        Assert.False(Assert.Single(point.Influences.Where(w => w.Weight == 1e-15)).Retained);
        Assert.Equal(4, point.Influences.Where(w => w.Retained).Select(w => w.ImportedBoneIndex).Distinct().Count());
        foreach (var surface in model.Surfaces)
        {
            Assert.Same(geometry, surface.SourceGeometry);
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                int cp = surface.SourceCorners[i].ControlPointIndex;
                var source = skin.ControlPoints[cp];
                var expected = source.Influences.Where(w => w.Retained).GroupBy(w => w.ImportedBoneIndex)
                    .ToDictionary(g => g.Key, g => g.Sum(w => w.Weight) / source.RetainedWeight);
                var vertex = surface.Vertices[i];
                Assert.Equal(expected.Count, vertex.BoneIndices.Length);
                for (int j = 0; j < vertex.BoneIndices.Length; j++)
                    Assert.Equal(expected[surface.PaletteBoneIndices[vertex.BoneIndices[j]]], vertex.BoneWeights[j], 12);
            }
        }
    }

    [Fact]
    public void MaterialPartitionsShareOriginalWeights()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, true), "partitioned.fbx");
        Assert.Equal(2, model.Surfaces.Length);
        Assert.NotNull(model.Surfaces[0].SourceGeometry!.Skinning);
        Assert.Same(model.Surfaces[0].SourceGeometry!.Skinning, model.Surfaces[1].SourceGeometry!.Skinning);
    }

    [Fact]
    public void MissingOrConflictingSourceWeightsCannotBecomeAnalysisEvidence()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(false, true), "partitioned.fbx");
        var surface = model.Surfaces[0];
        var geometry = surface.SourceGeometry!;
        var skin = geometry.Skinning!;
        var missing = model with { Surfaces = model.Surfaces.SetItem(0, surface with { SourceGeometry = geometry with { Skinning = null } }) };
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(missing));
        var firstPoint = skin.ControlPoints[0];
        var changedSkin = skin with { ControlPoints = skin.ControlPoints.SetItem(0, firstPoint with {
            RetainedWeight = firstPoint.RetainedWeight / 2,
            Influences = firstPoint.Influences.SetItem(0, firstPoint.Influences[0] with { Weight = firstPoint.Influences[0].Weight / 2 }) }) };
        changedSkin.Validate(geometry.ControlPoints.Length);
        var conflicting = model with { Surfaces = model.Surfaces.SetItem(0, surface with { SourceGeometry = geometry with { Skinning = changedSkin } }) };
        Assert.Throws<InvalidDataException>(() => FbxSourceGeometryAnalysis.Build(conflicting));
        Assert.Throws<InvalidDataException>(() => (skin with { ControlPoints = skin.ControlPoints.SetItem(0, firstPoint with { RetainedWeight = 2 }) }).Validate(geometry.ControlPoints.Length));
        Assert.ThrowsAny<OperationCanceledException>(() => skin.Validate(geometry.ControlPoints.Length, new(true)));
    }

    [Fact]
    public void SourceEntryIdentityCannotBeReusedForAnotherControlPoint()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateSourceWeightFixture(), "weights.fbx");
        var skin = model.Surfaces[0].SourceGeometry!.Skinning!;
        var second = skin.ControlPoints[1];
        var repeated = skin with { ControlPoints = skin.ControlPoints.SetItem(1, second with {
            Influences = second.Influences.SetItem(0, second.Influences[0] with { SourceEntryIndex = 0 }) }) };
        Assert.Throws<InvalidDataException>(() => repeated.Validate(skin.ControlPoints.Length));
    }

    [Fact]
    public void UnskinnedEvidenceMustStillCoverEveryControlPoint()
    {
        var skin = new GeometrySourceSkinning(false, [new([], 0, 0), new([], 0, 0)]);
        skin.Validate(2);
        Assert.Throws<InvalidDataException>(() => skin.Validate(3));
        Assert.Throws<InvalidDataException>(() => (skin with { HasSkinDeformer = true }).Validate(2));
    }

}
