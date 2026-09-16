using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SourceSkinInfluenceRegionsTests
{
    [Fact]
    public void AggregatesOriginalWeightsByMappedCurrentBoneAndRetainsDiscardedEvidence()
    {
        GeometrySourceComponent geometry = CreateGeometry(
            "body",
            [new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)],
            [
                Weights(("sourceA", 0.75, true, 0), ("sourceB", 0.25, false, 1)),
                Weights(("sourceA", 1.0, true, 2)),
                Weights(("sourceB", 1.0, true, 3)),
            ]);
        SourceGeometryAnalysis analysis = CreateAnalysis(
            "source-hash",
            geometry,
            [(0, 1, 2)]);

        SourceSkinInfluenceRegionsResult result = SourceSkinInfluenceRegions.Build(
            analysis,
            new Dictionary<string, int>
            {
                ["sourceA"] = 7,
                ["sourceB"] = 3,
            });

        Assert.Equal("source-hash", result.SourceSha256);
        Assert.True(result.HasUsedGeometry);
        Assert.Equal(new Vector3D(0, 0, 0), result.UsedGeometryMin);
        Assert.Equal(new Vector3D(2, 2, 0), result.UsedGeometryMax);
        SourceSkinInfluenceRegion regionA = result.Regions[7];
        Assert.Equal(2, regionA.SourceControlPointCount);
        Assert.Equal(1.75, regionA.OriginalNormalizedInfluenceMass, 10);
        Assert.Equal(7.0 / 6.0, regionA.SurfaceAreaWeightedInfluenceMass, 10);
        Assert.True(regionA.HasSurfaceSupport);
        Assert.True((regionA.Centroid - new Vector3D(8.0 / 7.0, 0, 0)).Length < 1e-12);

        SourceSkinDiscardedInfluence discarded = Assert.Single(result.DiscardedInfluences);
        Assert.Equal("body", discarded.ComponentId);
        Assert.Equal(0, discarded.SourceControlPointIndex);
        Assert.Equal("sourceB", discarded.JointId);
        Assert.Equal(3, discarded.BoneIndex);
        Assert.Equal(0.25, discarded.OriginalWeight, 10);
        Assert.Equal(0.25 / 1.0, discarded.NormalizedWeight, 10);
    }

    [Fact]
    public void SurfaceMassAndCentroidAreStableAcrossAUniformTessellation()
    {
        GeometrySourceComponent coarse = CreateGeometry(
            "coarse",
            [new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)],
            [Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)), Weights(("joint", 1.0, true, 2))]);
        GeometrySourceComponent tessellated = CreateGeometry(
            "tessellated",
            [
                new(0, 0, 0), new(2, 0, 0), new(0, 2, 0),
                new(1, 0, 0), new(0, 1, 0), new(1, 1, 0),
            ],
            [
                Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)),
                Weights(("joint", 1.0, true, 2)), Weights(("joint", 1.0, true, 3)),
                Weights(("joint", 1.0, true, 4)), Weights(("joint", 1.0, true, 5)),
            ]);

        SourceSkinInfluenceRegion coarseRegion = SourceSkinInfluenceRegions.Build(
            CreateAnalysis("hash", coarse, [(0, 1, 2)]),
            new Dictionary<string, int> { ["joint"] = 4 }).Regions[4];
        SourceSkinInfluenceRegion tessellatedRegion = SourceSkinInfluenceRegions.Build(
            CreateAnalysis("hash", tessellated, [(0, 3, 4), (3, 1, 5), (4, 5, 2), (3, 5, 4)]),
            new Dictionary<string, int> { ["joint"] = 4 }).Regions[4];

        Assert.Equal(2.0, coarseRegion.SurfaceAreaWeightedInfluenceMass, 10);
        Assert.Equal(
            coarseRegion.SurfaceAreaWeightedInfluenceMass,
            tessellatedRegion.SurfaceAreaWeightedInfluenceMass,
            10);
        Assert.InRange(Math.Abs(coarseRegion.Centroid.X - tessellatedRegion.Centroid.X), 0.0, 1e-12);
        Assert.InRange(Math.Abs(coarseRegion.Centroid.Y - tessellatedRegion.Centroid.Y), 0.0, 1e-12);
        Assert.InRange(Math.Abs(coarseRegion.Centroid.Z - tessellatedRegion.Centroid.Z), 0.0, 1e-12);
        Assert.Equal(new Vector3D(0, 0, 0), tessellatedRegion.Min);
        Assert.Equal(new Vector3D(2, 2, 0), tessellatedRegion.Max);
    }

    [Fact]
    public void UsesSourceControlPointIdentityAcrossCoincidentPositionsAndHandlesDegenerateSurface()
    {
        GeometrySourceComponent geometry = CreateGeometry(
            "coincident",
            [new(0, 0, 0), new(0, 0, 0), new(1, 0, 0)],
            [Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)), Weights(("joint", 1.0, true, 2))]);

        SourceSkinInfluenceRegion region = SourceSkinInfluenceRegions.Build(
            CreateAnalysis("hash", geometry, [(0, 1, 2)]),
            new Dictionary<string, int> { ["joint"] = 6 }).Regions[6];

        Assert.Equal(3, region.SourceControlPointCount);
        Assert.Equal(3.0, region.OriginalNormalizedInfluenceMass, 10);
        Assert.Equal(0.0, region.SurfaceAreaWeightedInfluenceMass);
        Assert.False(region.HasSurfaceSupport);
        Assert.Equal(Vector3D.Zero, region.Centroid);
        Assert.Equal(new Vector3D(0, 0, 0), region.Min);
        Assert.Equal(new Vector3D(1, 0, 0), region.Max);
    }

    [Fact]
    public void ExcludesComponentsWithoutSelectedTrianglesFromUsedBoundsAndRegions()
    {
        GeometrySourceComponent selected = CreateGeometry(
            "selected",
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)), Weights(("joint", 1.0, true, 2))]);
        GeometrySourceComponent excluded = CreateGeometry(
            "excluded",
            [new(100, 100, 100), new(101, 100, 100), new(100, 101, 100)],
            [Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)), Weights(("joint", 1.0, true, 2))]);

        SourceGeometryAnalysis analysis = new(
            "hash",
            [
                CreateComponentAnalysis(selected, [(0, 1, 2)]),
                CreateComponentAnalysis(excluded, []),
            ]);
        SourceSkinInfluenceRegionsResult result = SourceSkinInfluenceRegions.Build(
            analysis,
            new Dictionary<string, int> { ["joint"] = 2 });

        Assert.Equal(new Vector3D(0, 0, 0), result.UsedGeometryMin);
        Assert.Equal(new Vector3D(1, 1, 0), result.UsedGeometryMax);
        Assert.Equal(3, result.Regions[2].SourceControlPointCount);
    }

    [Fact]
    public void RequiresMappingsForDiscardedAndRetainedOriginalJoints()
    {
        GeometrySourceComponent geometry = CreateGeometry(
            "body",
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [
                Weights(("retained", 0.5, true, 0), ("discarded", 0.5, false, 1)),
                Weights(("retained", 1.0, true, 2)),
                Weights(("retained", 1.0, true, 3)),
            ]);

        Assert.Throws<KeyNotFoundException>(() =>
            SourceSkinInfluenceRegions.Build(
                CreateAnalysis("hash", geometry, [(0, 1, 2)]),
                new Dictionary<string, int> { ["retained"] = 1 }));
    }

    [Fact]
    public void RejectsInvalidFiniteAndIndexedSourceDataAndHonorsCancellation()
    {
        GeometrySourceComponent nonFinite = CreateGeometry(
            "bad",
            [new(double.NaN, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)), Weights(("joint", 1.0, true, 2))]);
        Assert.Throws<InvalidDataException>(() =>
            SourceSkinInfluenceRegions.Build(
                CreateAnalysis("hash", nonFinite, [(0, 1, 2)]),
                new Dictionary<string, int> { ["joint"] = 0 }));

        GeometrySourceComponent valid = CreateGeometry(
            "valid",
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            [Weights(("joint", 1.0, true, 0)), Weights(("joint", 1.0, true, 1)), Weights(("joint", 1.0, true, 2))]);
        SourceGeometryAnalysis validAnalysis = CreateAnalysis("hash", valid, [(0, 1, 2)]);
        SourceGeometryComponentAnalysis invalidComponent = validAnalysis.Components[0] with
        {
            Triangles = [new SourceGeometryAnalysisTriangle(new GeometrySourceTriangle(0, 0), 0, 1, 4)],
        };
        SourceGeometryAnalysis invalidAnalysis = validAnalysis with
        {
            Components = [invalidComponent],
        };
        Assert.Throws<InvalidDataException>(() =>
            SourceSkinInfluenceRegions.Build(
                invalidAnalysis,
                new Dictionary<string, int> { ["joint"] = 0 }));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SourceSkinInfluenceRegions.Build(
                CreateAnalysis("hash", valid, [(0, 1, 2)]),
                new Dictionary<string, int> { ["joint"] = 0 },
                cancellation.Token));
    }

    private static GeometrySourceComponent CreateGeometry(
        string id,
        ImmutableArray<Vector3D> positions,
        ImmutableArray<GeometrySourceControlPointWeights> weights) =>
        new(id, positions)
        {
            Skinning = new GeometrySourceSkinning(true, weights),
        };

    private static SourceGeometryAnalysis CreateAnalysis(
        string hash,
        GeometrySourceComponent geometry,
        ImmutableArray<(int A, int B, int C)> triangles) =>
        new(hash, [CreateComponentAnalysis(geometry, triangles)]);

    private static SourceGeometryComponentAnalysis CreateComponentAnalysis(
        GeometrySourceComponent geometry,
        ImmutableArray<(int A, int B, int C)> triangles)
    {
        ImmutableArray<SourceGeometryAnalysisTriangle> sourceTriangles =
            triangles
                .Select((triangle, index) => new SourceGeometryAnalysisTriangle(
                    new GeometrySourceTriangle(index, 0),
                    triangle.A,
                    triangle.B,
                    triangle.C))
                .ToImmutableArray();
        return new SourceGeometryComponentAnalysis(
            geometry,
            sourceTriangles,
            SourceMeshTopology.Build(geometry.ControlPoints.Length, triangles));
    }

    private static GeometrySourceControlPointWeights Weights(
        params (string JointId, double Weight, bool Retained, int Entry)[] influences)
    {
        double retained = influences.Where(static influence => influence.Retained).Sum(static influence => influence.Weight);
        double discarded = influences.Where(static influence => !influence.Retained).Sum(static influence => influence.Weight);
        return new GeometrySourceControlPointWeights(
            influences
                .Select(influence => new GeometrySourceInfluence(
                    "skin",
                    influence.JointId,
                    influence.JointId,
                    influence.Entry,
                    influence.JointId is "sourceB" or "discarded" ? 42 : 99,
                    influence.Weight,
                    influence.Retained))
                .ToImmutableArray(),
            retained,
            discarded);
    }
}
