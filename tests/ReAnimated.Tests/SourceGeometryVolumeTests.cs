using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SourceGeometryVolumeTests
{
    [Fact]
    public void ClosedBoxHasInteriorExteriorSurfaceAndSourceLinkedDistances()
    {
        var component = Box("body");
        var volume = SourceGeometryVolume.Build(Analysis(component));
        Assert.True(Assert.Single(volume.Shells).IsClosedCandidate);
        var inside = volume.Sample(Vector3D.Zero);
        Assert.Equal(SourceVolumeLocation.Interior, inside.Location);
        Assert.Equal(-1, inside.SignedField!.Value, 12);
        Assert.Equal(SourceVolumeFieldKind.SignedDistanceToInputShell, inside.FieldKind);
        var outside = volume.Sample(new(2, 2, 2));
        Assert.Equal(SourceVolumeLocation.Exterior, outside.Location);
        Assert.Equal(Math.Sqrt(3), outside.SignedField!.Value, 12);
        var surface = volume.Sample(new(1, 0, 0));
        Assert.Equal(SourceVolumeLocation.Surface, surface.Location);
        Assert.Equal(0, surface.SignedField);
        var hit = Assert.IsType<SourceGeometrySurfaceHit>(outside.NearestInputSurface);
        Assert.Equal("body", hit.ComponentId);
        Assert.Contains(hit.Triangle, component.Triangles);
        var points = component.Geometry.ControlPoints;
        var reconstructed = points[hit.Triangle.A] * hit.BarycentricWeights.X + points[hit.Triangle.B] * hit.BarycentricWeights.Y + points[hit.Triangle.C] * hit.BarycentricWeights.Z;
        Assert.True((reconstructed - hit.Point).Length < 1e-12);
    }

    [Fact]
    public void TranslatedRotatedScaledAndReversedShellRetainsItsVolume()
    {
        var transform = TransformMatrix.CreateTranslation(new(3, -4, 2)) *
            TransformMatrix.CreateRotation(new QuaternionD(0, 0, Math.Sin(.3), Math.Cos(.3))) * TransformMatrix.CreateScale(Vector3D.One * 2);
        var component = Box("body", transform);
        component = component with { Triangles = component.Triangles.Select(t => t with { B = t.C, C = t.B }).ToImmutableArray() };
        var volume = SourceGeometryVolume.Build(Analysis(component));
        Assert.False(volume.HasUnreliableTopology);
        Assert.Equal(-2, volume.Sample(transform.TransformPoint(Vector3D.Zero)).SignedField!.Value, 10);
        Assert.Equal(2, volume.Sample(transform.TransformPoint(new(2, 0, 0))).SignedField!.Value, 10);
    }

    [Fact]
    public void NestedAndOverlappingComponentsUseExplicitUnionFieldSemantics()
    {
        var outer = Box("outer", TransformMatrix.CreateScale(Vector3D.One * 2));
        var inner = Box("inner", TransformMatrix.CreateScale(Vector3D.One * .5));
        var nested = SourceGeometryVolume.Build(Analysis(inner, outer));
        var boundaryInsideOuter = nested.Sample(new(.5, 0, 0));
        Assert.Equal(SourceVolumeLocation.Interior, boundaryInsideOuter.Location);
        Assert.Equal(-1.5, boundaryInsideOuter.SignedField!.Value, 12);
        Assert.Equal(0, boundaryInsideOuter.NearestInputSurface!.Distance);
        Assert.Equal(SourceVolumeFieldKind.UnionOfSignedShellFields, boundaryInsideOuter.FieldKind);
        Assert.Equal(-2, nested.Sample(Vector3D.Zero).SignedField!.Value, 12);
        Assert.Equal(-.5, nested.Sample(Vector3D.Zero, "inner").SignedField!.Value, 12);
        Assert.Throws<ArgumentException>(() => nested.Sample(Vector3D.Zero, "missing"));
        var overlapping = SourceGeometryVolume.Build(Analysis(Box("left", TransformMatrix.CreateTranslation(new(-.5, 0, 0))),
            Box("right", TransformMatrix.CreateTranslation(new(.5, 0, 0)))));
        Assert.Equal(-.5, overlapping.Sample(Vector3D.Zero).SignedField!.Value, 12);
        // Actual union-boundary distance here is 1; the explicit minimum-field semantics must not claim it.
        Assert.Equal(SourceVolumeFieldKind.UnionOfSignedShellFields, overlapping.Sample(Vector3D.Zero).FieldKind);
    }

    [Fact]
    public void OpenInconsistentAndNonmanifoldInputsDoNotGainInventedInteriors()
    {
        var closed = Box("body");
        var open = closed with { Triangles = closed.Triangles.RemoveRange(0, 2) };
        var reversedOne = closed with { Triangles = closed.Triangles.SetItem(0, closed.Triangles[0] with { B = closed.Triangles[0].C, C = closed.Triangles[0].B }) };
        var duplicateFace = closed with { Triangles = closed.Triangles.Add(closed.Triangles[0] with { Source = new(12, 0) }) };
        foreach (var component in new[] { open, reversedOne, duplicateFace })
        {
            var volume = SourceGeometryVolume.Build(Analysis(component));
            Assert.True(volume.HasUnreliableTopology);
            Assert.Equal(SourceVolumeLocation.Unknown, volume.Sample(Vector3D.Zero).Location);
            Assert.Null(volume.Sample(Vector3D.Zero).SignedField);
        }
        Assert.True(SourceGeometryVolume.Build(Analysis(open)).Shells[0].Issues.HasFlag(SourceVolumeTopologyIssue.OpenBoundary));
        Assert.True(SourceGeometryVolume.Build(Analysis(reversedOne)).Shells[0].Issues.HasFlag(SourceVolumeTopologyIssue.InconsistentWinding));
        Assert.True(SourceGeometryVolume.Build(Analysis(duplicateFace)).Shells[0].Issues.HasFlag(SourceVolumeTopologyIssue.NonManifoldEdge));
        Assert.Equal(SourceVolumeTopologyIssue.None, closed.Topology.BoundaryEdges.IsEmpty ? SourceVolumeTopologyIssue.None : SourceVolumeTopologyIssue.OpenBoundary);
    }

    [Fact]
    public void ThinDoubleSidedSheetIsNotAClosedVolumeAndBudgetsFailBeforePartialResults()
    {
        var geometry = new GeometrySourceComponent("sheet", [Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitY]);
        var triangles = ImmutableArray.Create(new SourceGeometryAnalysisTriangle(new(0, 0), 0, 1, 2), new SourceGeometryAnalysisTriangle(new(1, 0), 0, 2, 1));
        var component = new SourceGeometryComponentAnalysis(geometry, triangles, SourceMeshTopology.Build(3, [(0, 1, 2), (0, 2, 1)]));
        var volume = SourceGeometryVolume.Build(Analysis(component));
        Assert.True(volume.Shells[0].Issues.HasFlag(SourceVolumeTopologyIssue.ZeroVolume));
        Assert.Null(volume.Sample(new(.2, .2, 1)).SignedField);
        Assert.Throws<ArgumentException>(() => SourceGeometryVolume.Build(Analysis(Box("box")), new() { MaximumTriangles = 3 }));
        Assert.Throws<ArgumentException>(() => SourceGeometryVolume.Build(Analysis(Box("same"), Box("same"))));
        Assert.ThrowsAny<OperationCanceledException>(() => SourceGeometryVolume.Build(Analysis(Box("box")), cancellationToken: new(true)));
        Assert.ThrowsAny<OperationCanceledException>(() => volume.Sample(Vector3D.Zero, cancellationToken: new(true)));
        Assert.Throws<ArgumentException>(() => volume.Sample(new(double.NaN, 0, 0)));
    }

    [Fact]
    public void ClosedRingHoleRemainsExteriorAndDisconnectedIslandsRemainSeparateSolids()
    {
        const int around = 16, tube = 12;
        var points = ImmutableArray.CreateBuilder<Vector3D>();
        for (int u = 0; u < around; u++)
        for (int v = 0; v < tube; v++)
        {
            double a = 2 * Math.PI * u / around, b = 2 * Math.PI * v / tube;
            points.Add(new((2 + .5 * Math.Cos(b)) * Math.Cos(a), .5 * Math.Sin(b), (2 + .5 * Math.Cos(b)) * Math.Sin(a)));
        }
        var triangles = new List<(int A, int B, int C)>();
        for (int u = 0; u < around; u++)
        for (int v = 0; v < tube; v++)
        {
            int a = u * tube + v, b = (u + 1) % around * tube + v, c = u * tube + (v + 1) % tube, d = (u + 1) % around * tube + (v + 1) % tube;
            triangles.Add((a, b, d)); triangles.Add((a, d, c));
        }
        var ring = new SourceGeometryComponentAnalysis(new("ring", points.ToImmutable()), triangles.Select((t, i) => new SourceGeometryAnalysisTriangle(new(i, 0), t.A, t.B, t.C)).ToImmutableArray(),
            SourceMeshTopology.Build(points.Count, triangles));
        var volume = SourceGeometryVolume.Build(Analysis(ring));
        Assert.False(volume.HasUnreliableTopology);
        Assert.Equal(SourceVolumeLocation.Exterior, volume.Sample(Vector3D.Zero).Location);
        Assert.InRange(volume.Sample(Vector3D.Zero).SignedField!.Value, 1.4, 1.51);
        Assert.Equal(SourceVolumeLocation.Interior, volume.Sample(new(2, 0, 0)).Location);

        var outer = Box("body", TransformMatrix.CreateScale(Vector3D.One * 2));
        var inner = Box("body", TransformMatrix.CreateScale(Vector3D.One * .5));
        var joinedPoints = outer.Geometry.ControlPoints.AddRange(inner.Geometry.ControlPoints);
        var joinedTriangles = outer.Triangles.AddRange(inner.Triangles.Select(t => new SourceGeometryAnalysisTriangle(
            new(t.Source.PolygonIndex + outer.Triangles.Length, 0), t.A + 8, t.B + 8, t.C + 8)));
        var sameComponent = new SourceGeometryComponentAnalysis(new("body", joinedPoints), joinedTriangles,
            SourceMeshTopology.Build(joinedPoints.Length, joinedTriangles.Select(static t => (t.A, t.B, t.C)).ToArray()));
        var nestedIslands = SourceGeometryVolume.Build(Analysis(sameComponent));
        Assert.Equal(2, nestedIslands.Shells.Length);
        Assert.Equal(-2, nestedIslands.Sample(Vector3D.Zero).SignedField!.Value, 12);
    }

    [Fact]
    public void GeometryFingerprintIncludesSelectionAndActualCoordinatesButNotComponentOrder()
    {
        var body = Box("body");
        var accessory = Box("accessory", TransformMatrix.CreateTranslation(new(5, 0, 0)));
        var selected = SourceGeometryVolume.Build(Analysis(body));
        var both = SourceGeometryVolume.Build(Analysis(body, accessory));
        var reordered = SourceGeometryVolume.Build(Analysis(accessory, body));
        Assert.Equal(selected.SourceSha256, both.SourceSha256);
        Assert.NotEqual(selected.GeometryFingerprint, both.GeometryFingerprint);
        Assert.Equal(both.GeometryFingerprint, reordered.GeometryFingerprint);
        Assert.NotEqual(selected.GeometryFingerprint, SourceGeometryVolume.Build(Analysis(Box("body", TransformMatrix.CreateTranslation(Vector3D.UnitX)))).GeometryFingerprint);
        Assert.Equal(SourceVolumeLocation.Exterior, selected.Sample(new(5, 0, 0)).Location);
        Assert.Equal(SourceVolumeLocation.Interior, both.Sample(new(5, 0, 0)).Location);
    }

    internal static SourceGeometryAnalysis Analysis(params SourceGeometryComponentAnalysis[] components) => new(new string('a', 64), components.ToImmutableArray());

    internal static SourceGeometryComponentAnalysis Box(string id, TransformMatrix? transform = null)
    {
        Vector3D[] points = [new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
        (int A, int B, int C)[] triangles = [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7), (0, 1, 5), (0, 5, 4),
            (1, 2, 6), (1, 6, 5), (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7)];
        var geometry = new GeometrySourceComponent(id, points.Select(p => (transform ?? TransformMatrix.Identity).TransformPoint(p)).ToImmutableArray());
        return new(geometry, triangles.Select((t, i) => new SourceGeometryAnalysisTriangle(new(i, 0), t.A, t.B, t.C)).ToImmutableArray(),
            SourceMeshTopology.Build(points.Length, triangles));
    }
}
