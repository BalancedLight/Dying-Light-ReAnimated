using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class ContactFootprintSolverTests
{
    internal static Vector3D[] Shoe(double width = .12, double height = .09, double length = .3) =>
        (from x in new[] { -width / 2, width / 2 }
         from y in new[] { 0.0, height }
         from z in new[] { -length / 2, length / 2 }
         select new Vector3D(x, y, z)).ToArray();

    [Theory]
    [InlineData(.12, .09, .3)]
    [InlineData(.2, .16, .4)]
    public void FitsTheSelectedShoeWithoutUniversalBounds(double width, double height, double length)
    {
        var fit = ContactFootprintSolver.Fit(Shoe(width, height, length), TransformMatrix.Identity, new());
        Assert.Equal(width / 2, fit.BoundsHalfExtents.X, 10);
        Assert.Equal(height / 2, fit.BoundsHalfExtents.Y, 10);
        Assert.Equal(length / 2, fit.BoundsHalfExtents.Z, 10);
        Assert.Equal(0, fit.ContactPlaneLocalY, 10);
        Assert.Equal(4, fit.Footprint.Length);
        Assert.True(fit.HasVolume);
        Assert.False(fit.OrientationAmbiguous);
    }

    [Fact]
    public void GeometryFindsTheLongAxisAndRetainsExactParentComposition()
    {
        var rotation = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, .6));
        var points = Shoe().Select(rotation.TransformPoint).ToArray();
        var parent = new TransformMatrix(2, .2, 0, 4, 0, 3, .1, 2, 0, 0, .5, -1, 0, 0, 0, 1);
        var fit = ContactFootprintSolver.Fit(points, parent, new());
        Assert.True((parent * fit.LocalFrame).NearlyEquals(fit.GlobalFrame));
        Assert.True((fit.GlobalFrame.TransformDirection(Vector3D.UnitZ) - rotation.TransformDirection(Vector3D.UnitZ)).Length < 1e-10);
        Assert.Equal(parent.Translation, fit.GlobalFrame.Translation);
        Assert.Equal(1, fit.GlobalFrame.LinearDeterminant, 10);
        Assert.Equal(.06, fit.BoundsHalfExtents.X, 10);
        Assert.Equal(.15, fit.BoundsHalfExtents.Z, 10);
    }

    [Fact]
    public void SquareFootprintRequiresDirectionReviewUnlessAxesAreExplicit()
    {
        var automatic = ContactFootprintSolver.Fit(Shoe(.2, .1, .2), TransformMatrix.Identity, new());
        Assert.True(automatic.OrientationAmbiguous);
        var manual = ContactFootprintSolver.Fit(Shoe(.2, .1, .2), TransformMatrix.Identity,
            new() { Axes = ContactAxisMode.ExplicitDirections });
        Assert.False(manual.OrientationAmbiguous);
    }

    [Fact]
    public void VertexDensityDoesNotChangeTheFootprintAxis()
    {
        var points = Shoe();
        var extra = Enumerable.Range(0, 100).Select(i => new Vector3D(.03, 0, i * .001)).ToArray();
        var first = ContactFootprintSolver.Fit(points, TransformMatrix.Identity, new());
        var second = ContactFootprintSolver.Fit(points.Concat(extra).ToArray(), TransformMatrix.Identity, new());
        Assert.True(first.GlobalFrame.NearlyEquals(second.GlobalFrame));
        Assert.Equal(first.BoundsHalfExtents, second.BoundsHalfExtents);
        Assert.Equal(first.Directionality, second.Directionality);
    }

    [Fact]
    public void ContactCenterIsOnTheBottomPlaneEvenWithTranslatedGeometry()
    {
        var offset = new Vector3D(12, 7, -4);
        var fit = ContactFootprintSolver.Fit(Shoe().Select(p => p + offset).ToArray(), TransformMatrix.Identity,
            new() { Origin = ContactOriginMode.FootprintCenter });
        Assert.True((fit.GlobalFrame.Translation - offset).Length < 1e-10);
        Assert.True(fit.Footprint.All(p => Math.Abs(p.Y - offset.Y) < 1e-10));
    }

    [Fact]
    public void FlatGeometryNeedsAnExplicitVolumeOverride()
    {
        var fit = ContactFootprintSolver.Fit(Shoe(height: 0), TransformMatrix.Identity, new());
        Assert.False(fit.HasVolume);
        Assert.Equal(0, fit.BoundsHalfExtents.Y);
    }

    [Fact]
    public void RejectsMissingFootprintBadDirectionsAndUnsupportedParent()
    {
        Assert.Throws<ArgumentException>(() => ContactFootprintSolver.Fit([Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitX * 2], TransformMatrix.Identity, new()));
        Assert.Throws<InvalidOperationException>(() => ContactFootprintSolver.Fit(Shoe(), TransformMatrix.Identity, new() { Forward = Vector3D.UnitY }));
        Assert.Throws<ArgumentException>(() => ContactFootprintSolver.Fit(Shoe(), TransformMatrix.CreateScale(new(-1, 1, 1)), new()));
        Assert.Throws<ArgumentException>(() => ContactFootprintSolver.Fit(Shoe(), TransformMatrix.Identity, new() { BottomBandFraction = double.NaN }));
        Assert.Throws<OperationCanceledException>(() => ContactFootprintSolver.Fit(Shoe(), TransformMatrix.Identity, new(), new(true)));
    }
}
