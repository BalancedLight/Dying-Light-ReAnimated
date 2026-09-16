using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SourceVolumeGridRegionTests
{
    [Fact]
    public void RegionResolutionUsesOnlyTheRequestedLocalBox()
    {
        var volume = SourceGeometryVolume.Build(
            SourceGeometryVolumeTests.Analysis(
                SourceGeometryVolumeTests.Box("body")));
        Vector3D minimum = new(-.5, -.5, -.5);
        Vector3D maximum = new(.5, .5, .5);
        SourceVolumeGrid grid = SourceVolumeGrid.BuildRegion(
            volume,
            minimum,
            maximum,
            new SourceVolumeGridOptions
            {
                LongestAxisCells = 8,
                PaddingCells = 8,
            });

        Assert.Equal(.125, grid.CellSize, 12);
        Assert.Equal(8, grid.SizeX);
        Assert.Equal(8, grid.SizeY);
        Assert.Equal(8, grid.SizeZ);
        int sampleX = 3, sampleY = 4, sampleZ = 5;
        Vector3D samplePosition = grid.GetPosition(sampleX, sampleY, sampleZ);
        Vector3D recoveredCell = ((samplePosition - grid.Min) / grid.CellSize) - new Vector3D(.5, .5, .5);
        Assert.Equal(sampleX, recoveredCell.X, 12);
        Assert.Equal(sampleY, recoveredCell.Y, 12);
        Assert.Equal(sampleZ, recoveredCell.Z, 12);
        Assert.All(
            Enumerable.Range(0, grid.SizeX)
                .SelectMany(x => Enumerable.Range(0, grid.SizeY)
                    .SelectMany(y => Enumerable.Range(0, grid.SizeZ)
                        .Select(z => grid.GetPosition(x, y, z)))),
            point =>
            {
                Assert.True(point.X >= minimum.X && point.X <= maximum.X);
                Assert.True(point.Y >= minimum.Y && point.Y <= maximum.Y);
                Assert.True(point.Z >= minimum.Z && point.Z <= maximum.Z);
            });
        Assert.Equal(SourceVolumeLocation.Interior, grid.GetCell(4, 4, 4).Location);
        Assert.True(grid.HasCompleteField);
    }

    [Fact]
    public void RegionRetainsPositiveThinDimensionsAndSamplesTheirMidpoint()
    {
        var volume = SourceGeometryVolume.Build(
            SourceGeometryVolumeTests.Analysis(
                SourceGeometryVolumeTests.Box("body")));
        Vector3D minimum = new(.2, -.15, -.15);
        Vector3D maximum = new(.21, .14, .15);
        SourceVolumeGrid grid = SourceVolumeGrid.BuildRegion(
            volume,
            minimum,
            maximum,
            new() { LongestAxisCells = 8 });

        Assert.Equal(1, grid.SizeX);
        Vector3D point = grid.GetPosition(0, grid.SizeY / 2, grid.SizeZ / 2);
        Assert.InRange(point.X, minimum.X, maximum.X);
        Assert.Equal((minimum.X + maximum.X) / 2, point.X, 12);
        Assert.InRange(point.Y, minimum.Y, maximum.Y);
        Assert.InRange(point.Z, minimum.Z, maximum.Z);
    }

    [Fact]
    public void DifferentRequestedRegionsHaveDistinctFingerprintsWithSameSourceIdentity()
    {
        var volume = SourceGeometryVolume.Build(
            SourceGeometryVolumeTests.Analysis(
                SourceGeometryVolumeTests.Box("body")));
        SourceVolumeGrid first = SourceVolumeGrid.BuildRegion(
            volume,
            new(-.5, -.5, -.5),
            new(.5, .5, .5),
            new() { LongestAxisCells = 8 });
        SourceVolumeGrid second = SourceVolumeGrid.BuildRegion(
            volume,
            new(2, 2, 2),
            new(3, 3, 3),
            new() { LongestAxisCells = 8 });

        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.NotEqual(first.InputFingerprint, second.InputFingerprint);
        Assert.False(second.HasUsableInterior);
        Assert.False(second.HasCompleteField);
        Assert.Equal(SourceVolumeLocation.Exterior, second.GetCell(4, 4, 4).Location);
        Assert.NotNull(second.GetCell(4, 4, 4).SignedField);
    }

    [Fact]
    public void RegionOutsideSourceIsNotClampedOrInvented()
    {
        var volume = SourceGeometryVolume.Build(
            SourceGeometryVolumeTests.Analysis(
                SourceGeometryVolumeTests.Box("body")));
        SourceVolumeGrid grid = SourceVolumeGrid.BuildRegion(
            volume,
            new(10, 10, 10),
            new(11, 11, 11),
            new() { LongestAxisCells = 8 });

        Assert.Equal(new Vector3D(10, 10, 10), grid.Min);
        Assert.Equal(SourceVolumeFieldKind.SignedDistanceToInputShell, grid.FieldKind);
        Assert.Equal(0, grid.InteriorCellCount);
        Assert.Equal(0, grid.UnknownCellCount);
        Assert.All(
            Enumerable.Range(0, grid.SizeX)
                .SelectMany(x => Enumerable.Range(0, grid.SizeY)
                    .SelectMany(y => Enumerable.Range(0, grid.SizeZ)
                        .Select(z => grid.GetCell(x, y, z)))),
            cell => Assert.Equal(SourceVolumeLocation.Exterior, cell.Location));
    }

    [Fact]
    public void RegionRejectsInvalidBoundsAndCellBudgetBeforeSampling()
    {
        var volume = SourceGeometryVolume.Build(
            SourceGeometryVolumeTests.Analysis(
                SourceGeometryVolumeTests.Box("body")));
        Assert.Throws<ArgumentException>(() => SourceVolumeGrid.BuildRegion(
            volume,
            new(0, 0, 0),
            new(0, 1, 1)));
        Assert.Throws<ArgumentException>(() => SourceVolumeGrid.BuildRegion(
            volume,
            new(double.NaN, 0, 0),
            new(1, 1, 1)));
        Assert.Throws<ArgumentException>(() => SourceVolumeGrid.BuildRegion(
            volume,
            new(-1, -1, -1),
            new(1, 1, 1),
            new() { LongestAxisCells = 16, MaximumCells = 10 }));
    }

    [Fact]
    public void RegionPreservesUnreliableTopologyAndCancellation()
    {
        var open = SourceGeometryVolumeTests.Box("open") with
        {
            Triangles = SourceGeometryVolumeTests.Box("open").Triangles.RemoveRange(0, 2),
        };
        var volume = SourceGeometryVolume.Build(
            SourceGeometryVolumeTests.Analysis(open));
        Assert.Throws<InvalidDataException>(() => SourceVolumeGrid.BuildRegion(
            volume,
            new(-1, -1, -1),
            new(1, 1, 1)));
        SourceVolumeGrid diagnostic = SourceVolumeGrid.BuildRegion(
            volume,
            new(-1, -1, -1),
            new(1, 1, 1),
            new()
            {
                LongestAxisCells = 8,
                AllowUnreliableTopology = true,
            });
        Assert.Equal(diagnostic.CellCount, diagnostic.UnknownCellCount);
        Assert.False(diagnostic.HasCompleteField);

        using var cancellation = new CancellationTokenSource();
        var progress = new CancellingProgress(cancellation);
        Assert.ThrowsAny<OperationCanceledException>(() => SourceVolumeGrid.BuildRegion(
            volume: SourceGeometryVolume.Build(
                SourceGeometryVolumeTests.Analysis(
                    SourceGeometryVolumeTests.Box("body"))),
            minimum: new(-1, -1, -1),
            maximum: new(1, 1, 1),
            options: new() { LongestAxisCells = 8 },
            progress: progress,
            cancellationToken: cancellation.Token));
        Assert.Equal(1, progress.CompletedSlices);
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) :
        IProgress<SourceVolumeGridProgress>
    {
        public int CompletedSlices { get; private set; }

        public void Report(SourceVolumeGridProgress value)
        {
            CompletedSlices = value.CompletedSlices;
            cancellation.Cancel();
        }
    }
}
