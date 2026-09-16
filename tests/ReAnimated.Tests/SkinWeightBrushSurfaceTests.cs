using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class SkinWeightBrushSurfaceTests
{
    [Fact]
    public void NearestSurfaceBrushDoesNotReachDisconnectedGeometryBehindIt()
    {
        var points = Enumerable.Range(0, 6).Select(i => new SkinWeightCorrectionPoint("body", i, [], [],
            Enumerable.Range(i < 3 ? 0 : 3, 3).Where(n => n != i).ToImmutableArray())).ToArray();
        Vector3D[] positions = [new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0), new(-1, -1, -.001), new(1, -1, -.001), new(0, 1, -.001)];
        var surface = SkinWeightBrushSurface.Build(points, positions, [(0, 1, 2), (3, 4, 5)]);
        var hit = Assert.IsType<SkinWeightBrushHit>(surface.Sample(new(0, 0, 1), new(0, 0, -2), "body", 2, .8));
        Assert.Equal(Vector3D.Zero, hit.Position);
        Assert.Equal(3, hit.Selection.Length);
        Assert.All(hit.Selection, p => Assert.InRange(p.PointIndex, 0, 2));
        Assert.Equal(.8 * .25, hit.Selection.Single(p => p.PointIndex == 2).Strength, 12);
        Assert.Null(surface.Sample(new(0, 0, 1), new(0, 0, -1), "hidden-component", 2, 1));
        Assert.Null(surface.Sample(new(0, 0, 1), new(0, 0, 1), "body", 2, 1));
    }
}
