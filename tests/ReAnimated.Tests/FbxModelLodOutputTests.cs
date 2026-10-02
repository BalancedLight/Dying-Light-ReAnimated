using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using Xunit;

namespace ReAnimated.Tests;

public sealed class FbxModelLodOutputTests
{
    private static readonly int[] TwoLevelIndexes = [0, 1];
    private static readonly int[] BaseSurfaceIndexes = [0];
    private static readonly int[] LowerSurfaceIndexes = [1];
    [Fact]
    public async Task SourceWriterEmitsBothLodRowsAndPreviewUsesBaseLevelOnly()
    {
        FbxModelAuthoringImportResult model = CreateTwoLevelModel();
        string output = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1SourceModelBuildResult result = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = model,
                    OutputDirectory = output,
                    ResourceName = "generic_lod_output",
                });

            Assert.Equal(CustomModelBuildState.CompilerReady, result.State);
            Assert.Equal(TwoLevelIndexes, result.PreparedSkinningExpectations.Select(row => row.LodIndex));
            Assert.Equal(2, result.PreparedSkinningExpectations.Length);
            Assert.All(result.PreparedSkinningExpectations, row => Assert.Single(row.Subsets));
            Assert.Equal(2, result.PreparedMorphExpectations.Length);

            CustomModelPreviewSession preview = CustomModelPreviewAdapter.CreateSession(
                model,
                CustomModelPreviewMode.Dl1Output);
            Assert.Single(preview.Meshes);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(output); }
    }
    [Fact]
    public void LayoutKeepsTwoOrderedLevelsAndBasePreviewSelectsOnlyLevelZero()
    {
        FbxModelAuthoringImportResult model = CreateTwoLevelModel();

        ImmutableArray<FbxModelLodNodeLayout> layout = FbxModelLodLayout.Create(model);
        FbxModelLodNodeLayout node = Assert.Single(layout);
        Assert.Equal(TwoLevelIndexes, node.Levels.Select(level => level.LodIndex));
        Assert.Equal(BaseSurfaceIndexes, node.Levels[0].SurfaceIndexes);
        Assert.Equal(LowerSurfaceIndexes, node.Levels[1].SurfaceIndexes);
        Assert.Equal(BaseSurfaceIndexes, FbxModelLodLayout.GetBaseSurfaceIndexes(model).Order());

        Assert.NotEqual(
            model.Surfaces[0].Vertices[0].Position,
            model.Surfaces[1].Vertices[0].Position);
        Assert.NotEqual(model.Surfaces[0].PaletteBoneIndices, model.Surfaces[1].PaletteBoneIndices);
    }

    [Fact]
    public void LayoutRejectsADeclaredLevelWithoutAnImportedDraw()
    {
        FbxModelAuthoringImportResult model = CreateTwoLevelModel() with
        {
            SourceLodGroups = [new(
                900,
                901,
                "generic_lod_group",
                [],
                [
                    new(0, 902, "level_high", [new(700, "high", 902)]),
                    new(1, 903, "level_low", [new(999, "missing", 903)]),
                ])],
        };

        Assert.Throws<InvalidDataException>(() => FbxModelLodLayout.Create(model));
    }

    [Fact]
    public void LayoutRejectsARepeatedSurfaceAcrossLevels()
    {
        FbxModelAuthoringImportResult model = CreateTwoLevelModel() with
        {
            SourceLodGroups = [new(
                900,
                901,
                "generic_lod_group",
                [],
                [
                    new(0, 902, "level_high", [new(700, "high", 902)]),
                    new(1, 903, "level_low", [new(700, "high", 902)]),
                ])],
        };

        Assert.Throws<InvalidDataException>(() => FbxModelLodLayout.Create(model));
    }

    private static FbxModelAuthoringImportResult CreateTwoLevelModel()
    {
        FbxModelAuthoringImportResult source = Dl1OfficialCompilerSkinningReadBackAcceptanceTests.CreateTwoBoneTriangle();
        FbxModelSurface original = Assert.Single(source.Surfaces);
        FbxModelSurface high = original with
        {
            SourceGeometry = new GeometrySourceComponent("fbx:902:700", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]),
        };
        FbxModelSurface low = original with
        {
            Vertices = original.Vertices.Select(vertex => vertex with
            {
                Position = vertex.Position + new Vector3D(0.25, 0.0, 0.0),
                BoneIndices = vertex.BoneIndices.Select(index => 1 - index).ToImmutableArray(),
            }).ToImmutableArray(),
            PaletteBoneIndices = original.PaletteBoneIndices.Reverse().ToImmutableArray(),
            InverseBindMatrices = original.InverseBindMatrices.Reverse().ToImmutableArray(),
            SourceGeometry = new GeometrySourceComponent("fbx:903:701", [new(0.25, 0, 0), new(1.25, 0, 0), new(0.25, 1, 0)]),
            MorphTargets = [],
        };
        return source with
        {
            Surfaces = [high, low],
            SourceLodGroups = [new(
                900,
                901,
                "generic_lod_group",
                [],
                [
                    new(0, 902, "level_high", [new(700, "high", 902)]),
                    new(1, 903, "level_low", [new(701, "low", 903)]),
                ])],
        };
    }
}
