using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class SkinWeightMirroringTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid C = new("00000000-0000-0000-0000-000000000003");

    [Fact]
    public void ExplicitPlaneFindsReciprocalPairsAndCenterlineWithoutCrossingComponents()
    {
        SkinWeightCorrectionPoint[] points = [Point(0), Point(1), Point(2), Point(3) with { ComponentId = "accessory" }];
        Vector3D[] positions = [new(-.5, 0, 0), new(1.5, 0, 0), new(.5, 0, 0), new(-.5, 0, 0)];
        var map = SkinWeightMirroring.Build(points, positions, new(.5, 0, 0), new(4, 0, 0), .001);
        Assert.Equal<int>([1, 0, 2, -1], map.Counterparts);
        Assert.Equal(0, map.Distances[0]);
        Assert.Contains(map.Diagnostics, d => d.Code == "mirror-unmatched" && d.ComponentId == "accessory");
    }

    [Fact]
    public void DuplicatePositionsAndNonreciprocalNearMatchesRemainUnresolved()
    {
        SkinWeightCorrectionPoint[] points = [Point(0), Point(1), Point(2)];
        var ambiguous = SkinWeightMirroring.Build(points, [new(-1, 0, 0), new(1, 0, 0), new(1, 0, 0)], Vector3D.Zero, Vector3D.UnitX, .01);
        Assert.All(ambiguous.Counterparts, p => Assert.Equal(-1, p));
        Assert.Contains(ambiguous.Diagnostics, d => d.Code == "mirror-ambiguous");
        var asymmetric = SkinWeightMirroring.Build(points, [new(-1, 0, 0), new(1, 0, 0), new(1.001, 0, 0)], Vector3D.Zero, Vector3D.UnitX, .01);
        Assert.Equal<int>([1, 0, -1], asymmetric.Counterparts);
        Assert.Contains(asymmetric.Diagnostics, d => d.Code == "mirror-nonreciprocal");
    }

    [Fact]
    public void PairedSetUsesOriginalFractionsAndPreservesExactCenterlineTargets()
    {
        SkinWeightCorrectionPoint[] points = [Point(0), Point(1), Point(2) with { Weights = [new(A, .5), new(B, .2), new(C, .3)] }];
        var map = SkinWeightMirroring.Build(points, [new(-1, 0, 0), new(1, 0, 0), Vector3D.Zero], Vector3D.Zero, Vector3D.UnitX, .001);
        var edited = SkinWeightMirroring.SetMirrored(points, [new(0, 1)], map, A, B, .4);
        Assert.True(edited.CanApply);
        Assert.Equal(2, edited.Changes.Length);
        Assert.Equal(.4, edited.Changes.Single(c => c.PointIndex == 0).After.Single(w => w.HandleId == A).Weight);
        Assert.Equal(.4, edited.Changes.Single(c => c.PointIndex == 1).After.Single(w => w.HandleId == B).Weight);
        var center = SkinWeightMirroring.SetMirrored(points, [new(2, 1)], map, A, B, .5);
        Assert.True(center.CanApply);
        var weights = Assert.Single(center.Changes).After;
        Assert.Equal(.5, weights.Single(w => w.HandleId == A).Weight);
        Assert.Equal(.5, weights.Single(w => w.HandleId == B).Weight);
        Assert.DoesNotContain(weights, w => w.HandleId == C);
        Assert.Equal(.3, points[2].Weights.Single(w => w.HandleId == C).Weight);
    }

    [Fact]
    public void UnmatchedPointsAndLockedOrImpossibleCounterpartsRefuseWholeEdit()
    {
        SkinWeightCorrectionPoint[] points = [Point(0), Point(1) with { LockedInfluences = [B] }];
        var map = SkinWeightMirroring.Build(points, [new(-1, 0, 0), new(1, 0, 0)], Vector3D.Zero, Vector3D.UnitX, .001);
        var refused = SkinWeightMirroring.SetMirrored(points, [new(0, 1)], map, A, B, .5);
        Assert.False(refused.CanApply); Assert.Empty(refused.Changes);
        var unmatched = map with { Counterparts = [-1, -1] };
        Assert.False(SkinWeightMirroring.SetMirrored(points, [new(0, 1)], unmatched, A, B, .5).CanApply);
        var centerMap = new SkinWeightMirrorMap([0], [0], []);
        Assert.False(SkinWeightMirroring.SetMirrored([Point(0)], [new(0, 1)], centerMap, A, B, .8).CanApply);
    }

    private static SkinWeightCorrectionPoint Point(int index) => new("body", index, [new(A, .1), new(B, .2), new(C, .7)], [], []);
}
