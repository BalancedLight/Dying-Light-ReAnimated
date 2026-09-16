using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class AutomaticSkinBinderTests
{
    private static readonly Guid LeftId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid RightId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void ConcaveGapUsesInteriorPathInsteadOfNearestBoneAcrossEmptySpace()
    {
        var left = Box("left", new(-.55, 0, 0), new(.25, 2, .3));
        var right = Box("right", new(.55, 0, 0), new(.25, 2, .3));
        var baseBar = Box("base", new(0, -1.75, 0), new(.8, .25, .3));
        var grid = Grid(40, left, right, baseBar);
        var handles = new SkinBindingHandle[] { new(LeftId, new(-.55, .1, 0), new(-.55, .1, 0)), new(RightId, new(.55, 1.7, 0), new(.55, 1.7, 0)) };
        var point = new SkinBindingPoint("left", 0, new(-.55, 1.7, .3));
        Assert.True((point.Position - handles[1].Start).Length < (point.Position - handles[0].Start).Length);
        var result = AutomaticSkinBinder.Bind(grid, handles, [point]);
        Assert.True(result.AllPointsAssigned);
        var weights = Assert.Single(result.Points).Influences;
        Assert.True(weights.Single(w => w.HandleId == LeftId).Weight > .85);
        Assert.Equal(1, weights.Sum(static w => w.Weight), 12);
        Assert.True(result.ExpandedCells > 0);
        var reversed = AutomaticSkinBinder.Bind(grid, handles.Reverse().ToArray(), [point]);
        Assert.Equal(result.InputFingerprint, reversed.InputFingerprint);
        Assert.Equal<GeneratedSkinInfluence>(weights, reversed.Points[0].Influences);
    }

    [Fact]
    public void ExplicitRegionBarriersAndEligibilityAreHardConstraints()
    {
        var grid = Grid(10, Box("body", Vector3D.Zero, Vector3D.One));
        var labels = ImmutableArray.CreateBuilder<int>(grid.CellCount);
        for (int z = 0; z < grid.SizeZ; z++)
        for (int y = 0; y < grid.SizeY; y++)
        for (int x = 0; x < grid.SizeX; x++) labels.Add(grid.GetPosition(x, y, z).X < 0 ? 1 : 2);
        var handles = new SkinBindingHandle[] {
            new(LeftId, new(-.5, -.5, 0), new(-.5, .5, 0)) { AllowedRegions = [1] },
            new(RightId, new(.5, -.5, 0), new(.5, .5, 0)) { AllowedRegions = [2] },
        };
        var points = new SkinBindingPoint[] { new("body", 0, new(-.5, 1, 0)), new("body", 1, new(.5, 1, 0)),
            new("body", 2, new(-.5, 1, 0)) { AllowedHandles = [RightId] },
            new("body", 3, new(-.01, .3, 0)) { AllowedHandles = [RightId] } };
        var result = AutomaticSkinBinder.Bind(grid, handles, points, new() { CellRegions = labels.MoveToImmutable() });
        Assert.Equal(new GeneratedSkinInfluence(LeftId, 1), Assert.Single(result.Points[0].Influences));
        Assert.Equal(new GeneratedSkinInfluence(RightId, 1), Assert.Single(result.Points[1].Influences));
        Assert.Empty(result.Points[2].Influences);
        Assert.Equal(1, result.Points[2].UnboundFraction);
        Assert.False(result.AllPointsAssigned);
        Assert.Contains(result.Diagnostics, d => d.Code == "unbound-control-point" && d.ControlPointIndex == 2);
        Assert.Equal(new GeneratedSkinInfluence(RightId, 1), Assert.Single(result.Points[3].Influences));
        var selected = result.Points[3].SampleCell!.Value;
        Assert.True(grid.GetPosition(selected.X, selected.Y, selected.Z).X > 0);
    }

    [Fact]
    public void FixedFractionsAndZeroLocksSurviveAndDetachedPartsRequireExplicitAssignment()
    {
        var grid = Grid(8, Box("body", Vector3D.Zero, Vector3D.One));
        SkinBindingHandle[] handles = [new(LeftId, new(-.5, 0, 0), new(-.5, 0, 0)), new(RightId, new(.5, 0, 0), new(.5, 0, 0))];
        SkinBindingPoint[] points = [
            new("body", 0, new(-1, 0, 0)) { FixedInfluences = [new(LeftId, .3)] },
            new("body", 1, new(-1, 0, 0)) { FixedInfluences = [new(LeftId, 0)] },
            new("badge", 0, new(9, 3, 0)),
            new("badge", 1, new(9, 4, 0)) { FixedInfluences = [new(LeftId, 1)] },
        ];
        var result = AutomaticSkinBinder.Bind(grid, handles, points);
        Assert.Equal(.3, result.Points[0].Influences.Single(w => w.HandleId == LeftId).Weight);
        Assert.Equal(.7, result.Points[0].Influences.Single(w => w.HandleId == RightId).Weight, 12);
        Assert.Equal(new GeneratedSkinInfluence(RightId, 1), Assert.Single(result.Points[1].Influences));
        Assert.Equal(1, result.Points[2].UnboundFraction);
        Assert.Null(result.Points[2].SampleCell);
        Assert.Equal(new GeneratedSkinInfluence(LeftId, 1), Assert.Single(result.Points[3].Influences));
        Assert.Equal(0, result.Points[3].UnboundFraction);
        Assert.Equal(.3, points[0].FixedInfluences[0].Weight);
        Assert.Equal(new Vector3D(-1, 0, 0), points[0].Position);
    }

    [Fact]
    public void DisconnectedUnseededIslandDoesNotReceiveWeightsFromNearbyGeometry()
    {
        var grid = Grid(30, Box("left", new(-1.1, 0, 0), new(.5, 1, .5)), Box("right", new(1.1, 0, 0), new(.5, 1, .5)));
        var result = AutomaticSkinBinder.Bind(grid, [new(LeftId, new(-1.1, -.5, 0), new(-1.1, .5, 0))], [new("right", 0, new(1.1, 1, 0))]);
        Assert.False(result.AllPointsAssigned);
        Assert.Empty(result.Points[0].Influences);
        Assert.Equal(1, result.Points[0].UnboundFraction);
    }

    [Fact]
    public void InfluenceLimitReportsRemovedMassWithoutRenormalizingFixedFractions()
    {
        var grid = Grid(8, Box("body", Vector3D.Zero, Vector3D.One));
        var handles = Enumerable.Range(1, 6).Select(i => new SkinBindingHandle(Guid.Parse($"00000000-0000-0000-0000-{i:D12}"), Vector3D.Zero, Vector3D.Zero)).ToArray();
        var point = new SkinBindingPoint("body", 0, Vector3D.UnitY) { FixedInfluences = [new(LeftId, .25)] };
        var result = AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumInfluences = 4 });
        var row = Assert.Single(result.Points);
        Assert.Equal(4, row.Influences.Length);
        Assert.Equal(.25, row.Influences.Single(w => w.HandleId == LeftId).Weight);
        Assert.Equal(1, row.Influences.Sum(static w => w.Weight), 12);
        Assert.Equal(.3, row.RemovedWeightBeforeRenormalization, 10);
        Assert.Contains(result.Diagnostics, d => d.Code == "influence-truncation");
        Assert.NotEqual(result.InputFingerprint, AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumInfluences = 3 }).InputFingerprint);
        Assert.NotEqual(result.InputFingerprint, AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumExpandedCells = 9000 }).InputFingerprint);
    }

    [Fact]
    public void InvalidLocksAndWorkBudgetsFailBeforePublishingAPartialResult()
    {
        var grid = Grid(8, Box("body", Vector3D.Zero, Vector3D.One));
        SkinBindingHandle[] handles = [new(LeftId, Vector3D.Zero, Vector3D.Zero), new(RightId, Vector3D.UnitY * .5, Vector3D.UnitY * .5)];
        SkinBindingPoint point = new("body", 0, Vector3D.UnitY);
        Assert.Throws<ArgumentException>(() => AutomaticSkinBinder.Bind(grid, handles, [point with { FixedInfluences = [new(LeftId, .7), new(RightId, .5)] }]));
        Assert.Throws<ArgumentException>(() => AutomaticSkinBinder.Bind(grid, handles, [point, point]));
        Assert.Throws<ArgumentException>(() => AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumGridCells = 1 }));
        Assert.Throws<ArgumentException>(() => AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumPointHandlePairs = 1 }));
        Assert.Throws<ArgumentException>(() => AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumGridHandlePairs = 1 }));
        Assert.Throws<InvalidOperationException>(() => AutomaticSkinBinder.Bind(grid, handles, [point], new() { MaximumExpandedCells = 1 }));
        Assert.ThrowsAny<OperationCanceledException>(() => AutomaticSkinBinder.Bind(grid, handles, [point], cancellationToken: new(true)));
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => AutomaticSkinBinder.Bind(grid, handles, [point], completedHandles: new CancelAfterFirst(cancellation), cancellationToken: cancellation.Token));
        Assert.Empty(point.FixedInfluences);
    }

    [Fact]
    public void AssignmentCoverageDoesNotClaimDeformationApprovalAndRejectsMalformedRows()
    {
        var grid = Grid(8, Box("body", Vector3D.Zero, Vector3D.One));
        var result = AutomaticSkinBinder.Bind(grid, [new(LeftId, Vector3D.Zero, Vector3D.Zero)], [new("body", 0, Vector3D.UnitY)]);
        Assert.True(result.AllPointsAssigned);
        Assert.True(result.RequiresDeformationReview);
        Assert.False((result with { Points = [result.Points[0] with { Influences = [new(LeftId, double.NaN)] }] }).AllPointsAssigned);
        Assert.False((result with { Points = [result.Points[0] with { Influences = [new(LeftId, .5), new(LeftId, .5)] }] }).AllPointsAssigned);
        Assert.False((result with { Points = [result.Points[0] with { Influences = [] }] }).AllPointsAssigned);
    }

    private sealed class CancelAfterFirst(CancellationTokenSource source) : IProgress<int>
    { public void Report(int value) { if (value == 1) source.Cancel(); } }

    private static SourceVolumeGrid Grid(int resolution, params SourceGeometryComponentAnalysis[] boxes) =>
        SourceVolumeGrid.Build(SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(boxes)), new() { LongestAxisCells = resolution });

    private static SourceGeometryComponentAnalysis Box(string id, Vector3D centre, Vector3D halfExtents) =>
        SourceGeometryVolumeTests.Box(id, TransformMatrix.CreateTranslation(centre) * TransformMatrix.CreateScale(halfExtents));
}
