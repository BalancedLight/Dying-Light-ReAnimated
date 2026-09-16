using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class VolumeInteriorGraphTests
{
    [Fact]
    public void FindsExactDeterministicPathAndRetainsGridFingerprint()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        VolumeInteriorGraph graph = VolumeInteriorGraph.Build(grid);
        VolumeGridCoordinate start = new(2, 2, 2);
        VolumeGridCoordinate end = new(7, 7, 7);

        SourceVolumeGridPath path = Assert.IsType<SourceVolumeGridPath>(graph.FindPath(start, end));
        SourceVolumeGridPath repeat = Assert.IsType<SourceVolumeGridPath>(graph.FindPath(start, end));

        Assert.Equal(grid.InputFingerprint, graph.InputFingerprint);
        Assert.Equal(grid.InputFingerprint, path.InputFingerprint);
        Assert.Equal<VolumeGridCoordinate>(path.Cells, repeat.Cells);
        Assert.Equal(start, path.Cells[0]);
        Assert.Equal(end, path.Cells[^1]);
        Assert.Equal(grid.GetPosition(start.X, start.Y, start.Z), path.Positions[0]);
        Assert.Equal(grid.GetPosition(end.X, end.Y, end.Z), path.Positions[^1]);
        Assert.Equal(0.0, path.ClearanceWeight);
    }

    [Fact]
    public void ReturnsNoRouteForDisconnectedInteriorComponents()
    {
        SourceVolumeGrid grid = Grid(
            [
                SourceGeometryVolumeTests.Box("left", TransformMatrix.CreateTranslation(new(-4, 0, 0))),
                SourceGeometryVolumeTests.Box("right", TransformMatrix.CreateTranslation(new(4, 0, 0))),
            ],
            16);
        VolumeInteriorGraph graph = VolumeInteriorGraph.Build(grid);
        VolumeGridCoordinate left = FindCell(grid, position => position.X < -3.0);
        VolumeGridCoordinate right = FindCell(grid, position => position.X > 3.0);

        Assert.Null(graph.FindPath(left, right));
    }

    [Fact]
    public void RoutesAroundClosedRingHoleWithoutCrossingExteriorCells()
    {
        SourceVolumeGrid grid = Grid(CreateRing(), 32);
        VolumeInteriorGraph graph = VolumeInteriorGraph.Build(grid);
        VolumeGridCoordinate start = FindNearestInterior(grid, new(2, 0, 0));
        VolumeGridCoordinate end = FindNearestInterior(grid, new(-2, 0, 0));

        SourceVolumeGridPath path = Assert.IsType<SourceVolumeGridPath>(graph.FindPath(start, end));
        Assert.True(path.Cells.Length > 2);
        Assert.All(path.Positions, position =>
            Assert.True(Math.Sqrt((position.X * position.X) + (position.Z * position.Z)) > 1.0));
    }

    [Fact]
    public void ClearanceWeightIsDimensionallyConsistentAndZeroIsPhysicalLength()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        VolumeInteriorGraph graph = VolumeInteriorGraph.Build(grid);
        VolumeGridCoordinate start = new(2, 2, 2);
        VolumeGridCoordinate end = new(7, 7, 7);

        SourceVolumeGridPath physical = Assert.IsType<SourceVolumeGridPath>(graph.FindPath(start, end, 0.0));
        SourceVolumeGridPath clearance = Assert.IsType<SourceVolumeGridPath>(graph.FindPath(start, end, 4.0));

        Assert.Equal(physical.PhysicalLength, physical.TotalCost, 12);
        Assert.True(clearance.TotalCost >= clearance.PhysicalLength);
        Assert.Equal(4.0, clearance.ClearanceWeight);
        double physicalMargin = physical.Cells.Average(c => -grid.GetCell(c.X, c.Y, c.Z).SignedField!.Value);
        double biasedMargin = clearance.Cells.Average(c => -grid.GetCell(c.X, c.Y, c.Z).SignedField!.Value);
        Assert.True(biasedMargin > physicalMargin);
        var scaledGrid = Grid(SourceGeometryVolumeTests.Box("body", TransformMatrix.CreateScale(Vector3D.One * 2)), 8);
        var scaled = Assert.IsType<SourceVolumeGridPath>(VolumeInteriorGraph.Build(scaledGrid).FindPath(start, end, 4));
        Assert.Equal<VolumeGridCoordinate>(clearance.Cells, scaled.Cells);
        Assert.Equal(clearance.TotalCost * 2, scaled.TotalCost, 10);
    }

    [Fact]
    public void RejectsInvalidEndpointsUnknownFieldsAndVisitBudgets()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        VolumeInteriorGraph graph = VolumeInteriorGraph.Build(grid);
        VolumeGridCoordinate start = new(2, 2, 2);
        VolumeGridCoordinate end = new(7, 7, 7);

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.FindPath(new(-1, 2, 2), end));
        Assert.Throws<InvalidDataException>(() => graph.FindPath(new(0, 0, 0), end));
        Assert.Throws<InvalidOperationException>(() => graph.FindPath(start, end, maximumVisitedNodes: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.FindPath(start, end, maximumVisitedNodes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.FindPath(start, end, clearanceWeight: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            graph.FindPath(start, end, clearanceWeight: VolumeInteriorGraph.MaximumClearanceWeight + 1));

        SourceGeometryComponentAnalysis open = SourceGeometryVolumeTests.Box("open");
        open = open with { Triangles = open.Triangles.RemoveRange(0, 2) };
        SourceGeometryVolume volume = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(open));
        SourceVolumeGrid incomplete = SourceVolumeGrid.Build(
            volume,
            new SourceVolumeGridOptions { LongestAxisCells = 4, AllowUnreliableTopology = true });
        Assert.Throws<InvalidDataException>(() => VolumeInteriorGraph.Build(incomplete));
    }

    [Fact]
    public void HonorsCancellationBeforeGraphAndPathWork()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => VolumeInteriorGraph.Build(grid, cancellation.Token));

        VolumeInteriorGraph graph = VolumeInteriorGraph.Build(grid);
        Assert.Throws<OperationCanceledException>(() =>
            graph.FindPath(
                new VolumeGridCoordinate(2, 2, 2),
                new VolumeGridCoordinate(7, 7, 7),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void RegionMaskCanBlockAnInteriorRouteAndPreservesExactFingerprintIdentity()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        HashSet<VolumeGridCoordinate> allInterior = InteriorCells(grid);
        VolumeGridCoordinate start = new(2, 2, 2);
        VolumeGridCoordinate end = new(7, 7, 7);
        HashSet<VolumeGridCoordinate> blocked = new(allInterior);
        blocked.RemoveWhere(cell => cell.X == 4);

        VolumeInteriorGraph graph = VolumeInteriorGraph.BuildForRegion(grid, blocked);
        Assert.Equal(grid.InputFingerprint, graph.GridInputFingerprint);
        Assert.NotEqual(grid.InputFingerprint, graph.InputFingerprint);
        Assert.Null(graph.FindPath(start, end));
        Assert.Throws<InvalidDataException>(() => graph.FindPath(new(4, 2, 2), end));
    }

    [Fact]
    public void RegionFingerprintIsOrderIndependentAndMasksDiffer()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        VolumeGridCoordinate[] cells = InteriorCells(grid).ToArray();
        var first = new HashSet<VolumeGridCoordinate>(cells);
        var second = new HashSet<VolumeGridCoordinate>(cells.Reverse());
        second.Remove(cells[^1]);

        VolumeInteriorGraph firstGraph = VolumeInteriorGraph.BuildForRegion(grid, first);
        VolumeInteriorGraph repeatGraph = VolumeInteriorGraph.BuildForRegion(grid, new HashSet<VolumeGridCoordinate>(cells.Reverse()));
        VolumeInteriorGraph secondGraph = VolumeInteriorGraph.BuildForRegion(grid, second);

        Assert.Equal(firstGraph.InputFingerprint, repeatGraph.InputFingerprint);
        Assert.NotEqual(firstGraph.InputFingerprint, secondGraph.InputFingerprint);
        Assert.Equal(grid.InputFingerprint, firstGraph.GridInputFingerprint);
    }

    [Fact]
    public void RegionMaskValidatesCoordinatesAndCancellation()
    {
        SourceVolumeGrid grid = Grid(SourceGeometryVolumeTests.Box("body"), 8);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VolumeInteriorGraph.BuildForRegion(
                grid,
                new HashSet<VolumeGridCoordinate> { new(-1, 0, 0) }));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            VolumeInteriorGraph.BuildForRegion(
                grid,
                InteriorCells(grid),
                cancellation.Token));
    }

    private static SourceVolumeGrid Grid(
        params SourceGeometryComponentAnalysis[] components) =>
        Grid(components, 16);

    private static SourceVolumeGrid Grid(SourceGeometryComponentAnalysis component, int cells) =>
        Grid([component], cells);

    private static SourceVolumeGrid Grid(
        SourceGeometryComponentAnalysis[] components,
        int cells)
    {
        SourceGeometryVolume volume = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(components));
        return SourceVolumeGrid.Build(
            volume,
            new SourceVolumeGridOptions { LongestAxisCells = cells, PaddingCells = 1 });
    }

    private static VolumeGridCoordinate FindCell(
        SourceVolumeGrid grid,
        Func<Vector3D, bool> predicate)
    {
        for (int z = 0; z < grid.SizeZ; z++)
        for (int y = 0; y < grid.SizeY; y++)
        for (int x = 0; x < grid.SizeX; x++)
        {
            SourceVolumeGridCell cell = grid.GetCell(x, y, z);
            if (cell.Location == SourceVolumeLocation.Interior &&
                cell.SignedField is < 0 &&
                predicate(grid.GetPosition(x, y, z)))
            {
                return new VolumeGridCoordinate(x, y, z);
            }
        }

        throw new Xunit.Sdk.XunitException("No matching interior cell was found.");
    }

    private static VolumeGridCoordinate FindNearestInterior(SourceVolumeGrid grid, Vector3D target)
    {
        VolumeGridCoordinate? result = null;
        double bestDistance = double.PositiveInfinity;
        for (int z = 0; z < grid.SizeZ; z++)
        for (int y = 0; y < grid.SizeY; y++)
        for (int x = 0; x < grid.SizeX; x++)
        {
            SourceVolumeGridCell cell = grid.GetCell(x, y, z);
            if (cell.Location != SourceVolumeLocation.Interior || cell.SignedField is not < 0)
            {
                continue;
            }

            double distance = (grid.GetPosition(x, y, z) - target).LengthSquared;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                result = new VolumeGridCoordinate(x, y, z);
            }
        }

        return result ?? throw new Xunit.Sdk.XunitException("No interior cell was found.");
    }

    private static HashSet<VolumeGridCoordinate> InteriorCells(SourceVolumeGrid grid)
    {
        var cells = new HashSet<VolumeGridCoordinate>();
        for (int z = 0; z < grid.SizeZ; z++)
        for (int y = 0; y < grid.SizeY; y++)
        for (int x = 0; x < grid.SizeX; x++)
        {
            SourceVolumeGridCell cell = grid.GetCell(x, y, z);
            if (cell.Location == SourceVolumeLocation.Interior && cell.SignedField is < 0)
            {
                cells.Add(new VolumeGridCoordinate(x, y, z));
            }
        }

        return cells;
    }

    private static SourceGeometryComponentAnalysis CreateRing()
    {
        const int around = 16;
        const int tube = 12;
        var points = ImmutableArray.CreateBuilder<Vector3D>();
        for (int u = 0; u < around; u++)
        for (int v = 0; v < tube; v++)
        {
            double major = 2 * Math.PI * u / around;
            double minor = 2 * Math.PI * v / tube;
            points.Add(new(
                (2 + 0.5 * Math.Cos(minor)) * Math.Cos(major),
                0.5 * Math.Sin(minor),
                (2 + 0.5 * Math.Cos(minor)) * Math.Sin(major)));
        }

        var triangles = new List<(int A, int B, int C)>();
        for (int u = 0; u < around; u++)
        for (int v = 0; v < tube; v++)
        {
            int a = (u * tube) + v;
            int b = ((((u + 1) % around) * tube) + v);
            int c = (u * tube) + ((v + 1) % tube);
            int d = ((((u + 1) % around) * tube) + ((v + 1) % tube));
            triangles.Add((a, b, d));
            triangles.Add((a, d, c));
        }

        return new(
            new GeometrySourceComponent("ring", points.ToImmutable()),
            triangles.Select((triangle, index) => new SourceGeometryAnalysisTriangle(
                new GeometrySourceTriangle(index, 0), triangle.A, triangle.B, triangle.C)).ToImmutableArray(),
            SourceMeshTopology.Build(points.Count, triangles));
    }
}
