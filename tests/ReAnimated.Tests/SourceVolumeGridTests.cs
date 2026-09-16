using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SourceVolumeGridTests
{
    [Fact]
    public void CellCentresPreserveMetricSpacingAndKnownClosedField()
    {
        var volume = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(SourceGeometryVolumeTests.Box("body")));
        var grid = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = 8, PaddingCells = 1 });
        Assert.Equal(10, grid.SizeX); Assert.Equal(10, grid.SizeY); Assert.Equal(10, grid.SizeZ);
        Assert.Equal(.25, grid.CellSize); Assert.Equal(512, grid.InteriorCellCount); Assert.Equal(0, grid.UnknownCellCount);
        Assert.True(grid.HasUsableInterior);
        Assert.True(grid.HasCompleteField);
        Assert.Equal(new Vector3D(-1.125, -1.125, -1.125), grid.GetPosition(0, 0, 0));
        Assert.Equal(SourceVolumeLocation.Exterior, grid.GetCell(0, 0, 0).Location);
        for (int z = 0; z < grid.SizeZ; z++)
        for (int y = 0; y < grid.SizeY; y++)
        for (int x = 0; x < grid.SizeX; x++)
        {
            var point = grid.GetPosition(x, y, z);
            var cell = grid.GetCell(x, y, z);
            bool inside = Math.Abs(point.X) < 1 && Math.Abs(point.Y) < 1 && Math.Abs(point.Z) < 1;
            Assert.Equal(inside ? SourceVolumeLocation.Interior : SourceVolumeLocation.Exterior, cell.Location);
            Assert.True(double.IsFinite(cell.SignedField!.Value));
            Assert.Equal(volume.Sample(point).SignedField, cell.SignedField);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.GetCell(-1, 0, 0));
    }

    [Fact]
    public void UnknownTopologyStaysUnknownAndCellBudgetDoesNotSilentlyCoarsen()
    {
        var open = SourceGeometryVolumeTests.Box("open");
        open = open with { Triangles = open.Triangles.RemoveRange(0, 2) };
        var volume = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(open));
        Assert.Throws<InvalidDataException>(() => SourceVolumeGrid.Build(volume));
        var diagnostic = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = 4, AllowUnreliableTopology = true });
        Assert.Equal(diagnostic.CellCount, diagnostic.UnknownCellCount);
        Assert.Null(diagnostic.GetCell(2, 2, 2).SignedField);
        Assert.False(diagnostic.HasUsableInterior);
        Assert.False(diagnostic.HasCompleteField);
        var closed = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(SourceGeometryVolumeTests.Box("body")));
        Assert.Throws<ArgumentException>(() => SourceVolumeGrid.Build(closed, new() { LongestAxisCells = 32, MaximumCells = 100 }));
    }

    [Fact]
    public void CancellationDuringSamplingReturnsNoPartialGrid()
    {
        var volume = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(SourceGeometryVolumeTests.Box("body")));
        using var cancelled = new CancellationTokenSource();
        var progress = new CancellingProgress(cancelled);
        Assert.ThrowsAny<OperationCanceledException>(() => SourceVolumeGrid.Build(volume, new() { LongestAxisCells = 8 }, progress, cancelled.Token));
        Assert.Equal(1, progress.CompletedSlices);
    }

    [Fact]
    public void SamplingSettingsChangeFingerprintWithoutChangingOriginalSurface()
    {
        var component = SourceGeometryVolumeTests.Box("body");
        var volume = SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(component));
        var coarse = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = 4 });
        var fine = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = 8 });
        Assert.NotEqual(coarse.InputFingerprint, fine.InputFingerprint);
        Assert.Equal(coarse.SourceSha256, fine.SourceSha256);
        Assert.Equal(8, component.Geometry.ControlPoints.Length);
        Assert.Equal(12, component.Triangles.Length);
        Assert.Equal(volume.GeometryFingerprint, SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(component)).GeometryFingerprint);
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<SourceVolumeGridProgress>
    {
        public int CompletedSlices { get; private set; }
        public void Report(SourceVolumeGridProgress value) { CompletedSlices = value.CompletedSlices; cancellation.Cancel(); }
    }
}
