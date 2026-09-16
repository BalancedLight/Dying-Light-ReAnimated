using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class EyePivotDetectorTests
{
    internal static SourceGeometryAnalysis Sphere(Vector3D center, Vector3D radii, bool open = false)
    {
        const int latitude = 12, longitude = 24;
        var points = new List<Vector3D> { center + new Vector3D(0, radii.Y, 0) };
        for (int i = 1; i < latitude; i++) for (int j = 0; j < longitude; j++)
        {
            double theta = Math.PI * i / latitude, phi = 2 * Math.PI * j / longitude;
            points.Add(center + new Vector3D(radii.X * Math.Sin(theta) * Math.Cos(phi), radii.Y * Math.Cos(theta), radii.Z * Math.Sin(theta) * Math.Sin(phi)));
        }
        int south = points.Count; points.Add(center - new Vector3D(0, radii.Y, 0));
        var triangles = new List<(int A, int B, int C)>();
        for (int j = 0; j < longitude; j++) triangles.Add((0, 1 + (j + 1) % longitude, 1 + j));
        for (int i = 0; i < latitude - 2; i++) for (int j = 0; j < longitude; j++)
        {
            int a = 1 + i * longitude + j, b = 1 + i * longitude + (j + 1) % longitude, c = a + longitude, d = b + longitude;
            triangles.Add((a, b, c)); triangles.Add((b, d, c));
        }
        if (!open) for (int j = 0; j < longitude; j++) triangles.Add((south, 1 + (latitude - 2) * longitude + j, 1 + (latitude - 2) * longitude + (j + 1) % longitude));
        return Analysis(points, triangles);
    }
    private static SourceGeometryAnalysis Analysis(IReadOnlyList<Vector3D> points, IReadOnlyList<(int A, int B, int C)> triangles) =>
        new(new string('e', 64), [new(new GeometrySourceComponent("eye-surface", points.ToImmutableArray()),
            triangles.Select((t, i) => new SourceGeometryAnalysisTriangle(new(i, 0), t.A, t.B, t.C)).ToImmutableArray(), SourceMeshTopology.Build(points.Count, triangles))]);

    [Fact]
    public void ClosedGlobeHasAStableGeometryPivotAndMeasuredResiduals()
    {
        var center = new Vector3D(.1, 1.6, -.3);
        var result = EyePivotDetector.Detect(Sphere(center, new(.025, .025, .025)), "eye-surface", 0);
        Assert.Equal(EyePivotDetectionStatus.GlobeCandidate, result.Status);
        Assert.True(result.ClosedSurface);
        Assert.True((result.Center!.Value - center).Length < 1e-10);
        Assert.Equal(.025, result.Radius!.Value, 10);
        Assert.InRange(result.NormalizedVertexError!.Value, 0, 1e-10);
        Assert.InRange(result.NormalizedSurfaceError!.Value, 0, .03);
        Assert.True(result.RequiresReview);
        Assert.Contains(result.Diagnostics, d => d.Code == "eye_semantic_review");
    }

    [Fact]
    public void PaintedAndFlatSurfacesDoNotReceiveAnInferredGlobe()
    {
        var globe = Sphere(Vector3D.Zero, new(.02, .02, .02));
        var painted = EyePivotDetector.Detect(globe, "eye-surface", 0, new() { PaintedSurface = true });
        Assert.Equal(EyePivotDetectionStatus.UnsupportedGeometry, painted.Status); Assert.Null(painted.Center);
        var flat = EyePivotDetector.Detect(Sphere(Vector3D.Zero, new(.02, .0001, .02)), "eye-surface", 0);
        Assert.Equal(EyePivotDetectionStatus.UnsupportedGeometry, flat.Status); Assert.Null(flat.Center);
        Assert.Contains(flat.Diagnostics, d => d.Code == "eye_insufficient_depth");
    }

    [Fact]
    public void OpenCapAndCubeAreNotCertifiedByVertexSphereFit()
    {
        var open = EyePivotDetector.Detect(Sphere(Vector3D.Zero, new(.02, .02, .02), open: true), "eye-surface", 0);
        Assert.Equal(EyePivotDetectionStatus.NeedsReview, open.Status);
        Assert.False(open.ClosedSurface);
        Vector3D[] p = [new(-1,-1,-1),new(1,-1,-1),new(1,1,-1),new(-1,1,-1),new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1)];
        (int,int,int)[] t = [(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),(3,7,6),(3,6,2),(0,4,7),(0,7,3),(1,2,6),(1,6,5)];
        var cube = EyePivotDetector.Detect(Analysis(p,t), "eye-surface", 0);
        Assert.Equal(EyePivotDetectionStatus.NeedsReview, cube.Status);
        Assert.InRange(cube.NormalizedVertexError!.Value, 0, 1e-10);
        Assert.True(cube.NormalizedSurfaceError > .08);
    }

    [Fact]
    public void TranslationScaleAndInputOptionsAreExplicit()
    {
        var a = EyePivotDetector.Detect(Sphere(Vector3D.Zero, new(.01,.01,.01)), "eye-surface", 0);
        var b = EyePivotDetector.Detect(Sphere(new(100,200,-300), new(.1,.1,.1)), "eye-surface", 0);
        Assert.Equal(a.Status,b.Status); Assert.Equal(a.Radius!.Value * 10,b.Radius!.Value,8);
        Assert.InRange(Math.Abs(a.NormalizedSurfaceError!.Value - b.NormalizedSurfaceError!.Value),0,1e-9);
        Assert.NotEqual(a.InputFingerprint,b.InputFingerprint);
        Assert.Throws<OperationCanceledException>(() => EyePivotDetector.Detect(Sphere(Vector3D.Zero,Vector3D.One),"eye-surface",0,cancellationToken:new(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => EyePivotDetector.Detect(Sphere(Vector3D.Zero,Vector3D.One),"eye-surface",9));
        var invalidIdentity = Sphere(Vector3D.Zero, Vector3D.One) with { SourceSha256 = "missing" };
        Assert.Throws<ArgumentException>(() => EyePivotDetector.Detect(invalidIdentity, "eye-surface", 0, new() { PaintedSurface = true }));
    }
}
