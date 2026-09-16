using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SourceGeometrySeamProxyTests
{
    [Fact]
    public void ExactBoundaryPairsCloseExplodedCubeOnlyInAnalysisAndMapHitsToOriginalCorners()
    {
        var source = SourceGeometryVolumeTests.Analysis(ExplodedBox());
        var original = source.Components[0];
        Assert.Equal(12, original.Topology.Islands.Length);
        var proxy = SourceGeometrySeamProxy.Build(source);
        var report = Assert.Single(proxy.Reports);
        Assert.Equal(36, report.PairedBoundaryEdges);
        Assert.Equal(0, report.AmbiguousBoundaryGroups);
        Assert.Equal(0, report.UnpairedBoundaryEdges);
        Assert.Same(source, proxy.Source);
        Assert.Equal(36, proxy.Analysis.Components[0].Geometry.ControlPoints.Length);
        Assert.Equal(8, report.SourceToRepresentative.Distinct().Count());
        Assert.Same(original.Geometry.Skinning, proxy.Source.Components[0].Geometry.Skinning);
        Assert.NotNull(original.Geometry.Skinning);
        Assert.Null(proxy.Analysis.Components[0].Geometry.Skinning);
        Assert.Equal<Vector3D>(original.Geometry.ControlPoints, proxy.Analysis.Components[0].Geometry.ControlPoints);
        var volume = SourceGeometryVolume.Build(proxy.Analysis);
        Assert.False(volume.HasUnreliableTopology);
        Assert.Equal(SourceVolumeLocation.Interior, volume.Sample(Vector3D.Zero).Location);
        var hit = volume.Sample(new(2, .23, .37)).NearestInputSurface!;
        var remapped = proxy.RemapHit(hit);
        Assert.Equal(original.Triangles.Single(t => t.Source == hit.Triangle.Source), remapped.Triangle);
        var points = original.Geometry.ControlPoints;
        var position = points[remapped.Triangle.A] * remapped.BarycentricWeights.X + points[remapped.Triangle.B] * remapped.BarycentricWeights.Y + points[remapped.Triangle.C] * remapped.BarycentricWeights.Z;
        Assert.True((position - hit.Point).Length < 1e-12);
        Assert.Equal(12, original.Topology.Islands.Length);
        Assert.Equal(volume.GeometryFingerprint, SourceGeometryVolume.Build(SourceGeometrySeamProxy.Build(source).Analysis).GeometryFingerprint);
    }

    [Fact]
    public void AmbiguousCoincidentCopiesAndNearButUnequalPositionsAreNotWeldedByGuess()
    {
        var first = ExplodedBox();
        var points = first.Geometry.ControlPoints.AddRange(first.Geometry.ControlPoints);
        var triangles = first.Triangles.AddRange(first.Triangles.Select(t => new SourceGeometryAnalysisTriangle(new(t.Source.PolygonIndex + 12, 0), t.A + 36, t.B + 36, t.C + 36)));
        var ambiguous = new SourceGeometryComponentAnalysis(new("body", points), triangles,
            SourceMeshTopology.Build(points.Length, triangles.Select(static t => (t.A, t.B, t.C)).ToArray()));
        var proxy = SourceGeometrySeamProxy.Build(SourceGeometryVolumeTests.Analysis(ambiguous));
        Assert.Equal(0, proxy.Reports[0].PairedBoundaryEdges);
        Assert.True(proxy.Reports[0].AmbiguousBoundaryGroups > 0);
        Assert.True(SourceGeometryVolume.Build(proxy.Analysis).HasUnreliableTopology);
        var shifted = first with { Geometry = first.Geometry with { ControlPoints = first.Geometry.ControlPoints.SetItem(0, first.Geometry.ControlPoints[0] + new Vector3D(1e-9, 0, 0)) } };
        var nearProxy = SourceGeometrySeamProxy.Build(SourceGeometryVolumeTests.Analysis(shifted));
        Assert.True(nearProxy.Reports[0].UnpairedBoundaryEdges > 0);
        Assert.True(SourceGeometryVolume.Build(nearProxy.Analysis).HasUnreliableTopology);
    }

    [Fact]
    public void ComponentsStaySeparateAndCancellationDoesNotReturnAPartialProxy()
    {
        var source = SourceGeometryVolumeTests.Analysis(ExplodedBox(), ExplodedBox("other"));
        var proxy = SourceGeometrySeamProxy.Build(source);
        Assert.Equal(2, proxy.Reports.Length);
        Assert.All(proxy.Reports, r => Assert.Equal(36, r.PairedBoundaryEdges));
        Assert.Equal(2, SourceGeometryVolume.Build(proxy.Analysis).Shells.Length);
        Assert.ThrowsAny<OperationCanceledException>(() => SourceGeometrySeamProxy.Build(source, new(true)));
        var hit = SourceGeometryVolume.Build(proxy.Analysis).Sample(Vector3D.Zero).NearestInputSurface!;
        Assert.Throws<ArgumentException>(() => proxy.RemapHit(hit with { ComponentId = "missing" }));
    }

    private static SourceGeometryComponentAnalysis ExplodedBox(string id = "body")
    {
        var shared = SourceGeometryVolumeTests.Box(id);
        var points = shared.Triangles.SelectMany(t => new[] { shared.Geometry.ControlPoints[t.A], shared.Geometry.ControlPoints[t.B], shared.Geometry.ControlPoints[t.C] }).ToImmutableArray();
        var weights = Enumerable.Range(0, points.Length).Select(i => new GeometrySourceControlPointWeights([
            new("skin", "joint", "joint", i, 0, 1, true)], 1, 0)).ToImmutableArray();
        var triangles = shared.Triangles.Select((t, i) => new SourceGeometryAnalysisTriangle(t.Source, i * 3, i * 3 + 1, i * 3 + 2)).ToImmutableArray();
        return new(new(id, points) { Skinning = new(true, weights) }, triangles,
            SourceMeshTopology.Build(points.Length, triangles.Select(static t => (t.A, t.B, t.C)).ToArray()));
    }
}
