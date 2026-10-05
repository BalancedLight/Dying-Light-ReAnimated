using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Dl1RagdollShapeInputsTests
{
    [Theory]
    [InlineData(1, 2, 4, Dl1RagdollShapeAxis.Z, 1.005, 3.085)]
    [InlineData(4, 2, 1, Dl1RagdollShapeAxis.X, 1.005, 3.085)]
    [InlineData(1, 4, 2, Dl1RagdollShapeAxis.Y, 1.005, 3.085)]
    [InlineData(4, 4, 1, Dl1RagdollShapeAxis.X, 2.005, 2.085)]
    [InlineData(1, 4, 4, Dl1RagdollShapeAxis.Y, 2.005, 2.085)]
    [InlineData(4, 4, 4, Dl1RagdollShapeAxis.X, 2.005, 2.085)]
    [Trait("ValidationTier", "Hermetic")]
    public void CapsuleAxisAndDimensionsFollowTheNativeScalarBranches(
        double x, double y, double z, Dl1RagdollShapeAxis axis, double radius, double length)
    {
        var result = Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Capsule, new(x, y, z));
        Assert.Equal(axis, result.CapsuleAxis);
        Assert.Equal(radius, result.Radius!.Value, 5);
        Assert.Equal(length, result.CylinderLength!.Value, 5);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DeclaredScaleChangesRadiusWhileCapsuleLengthUsesItsSeparateAdjustment()
    {
        var result = Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Capsule, new(1, 2, 4), 2);
        Assert.Equal(new Vector3D((float)2.01, (float)4.01, (float)8.01), result.PaddedSpans);
        Assert.Equal(2.005, result.Radius!.Value, 5);
        Assert.Equal(3.0825, result.CylinderLength!.Value, 5);
        var zero = Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Capsule, Vector3D.Zero, 0);
        Assert.Equal(Dl1RagdollShapeAxis.X, zero.CapsuleAxis);
        Assert.Equal(.005, zero.Radius!.Value, 5);
        Assert.Equal(.085, zero.CylinderLength!.Value, 5);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SphereUsesLongestSpanAndBoxKeepsAllThreePaddedSpans()
    {
        var sphere = Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Sphere, new(1, 3, 2), .5);
        Assert.Equal(.755, sphere.Radius!.Value, 5);
        Assert.Null(sphere.CylinderLength);
        Assert.Null(sphere.CapsuleAxis);
        var box = Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Box, new(1, 3, 2), .5);
        Assert.Equal(sphere.PaddedSpans, box.PaddedSpans);
        Assert.Null(box.Radius);
        Assert.Null(box.CylinderLength);
        Assert.Null(box.CapsuleAxis);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RelativeIntegerWeightsAreClampedBeforeTotalMassDistribution()
    {
        var weights = new[] { -4, 0, 3, 5 };
        Assert.Equal(new double[] { 10, 10, 30, 50 },
            Dl1RagdollShapeInputCalculator.NormalizeMasses(weights, 100));
        Assert.Equal(new[] { -4, 0, 3, 5 }, weights);
        Assert.Empty(Dl1RagdollShapeInputCalculator.NormalizeMasses([], 100));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void InvalidOrOverflowingInputsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Dl1RagdollShapeInputCalculator.Calculate((Dl1RagdollShapeKind)99, Vector3D.One));
        foreach (double scale in new[] { double.NaN, double.PositiveInfinity, -1, double.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Capsule, Vector3D.One, scale));
        foreach (var spans in new[] { new Vector3D(-1, 1, 1), new(double.NaN, 1, 1), new(double.MaxValue, 1, 1) })
            Assert.ThrowsAny<ArgumentException>(() =>
                Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Box, spans));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Dl1RagdollShapeInputCalculator.Calculate(Dl1RagdollShapeKind.Capsule, new(1, 1, 2), float.Epsilon));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Dl1RagdollShapeInputCalculator.NormalizeMasses([1], double.MaxValue));
    }
}
