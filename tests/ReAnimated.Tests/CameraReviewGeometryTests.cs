using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class CameraReviewGeometryTests
{
    [Fact]
    public void IdentityFrameUsesPositiveZForwardAndInvertedNegativeYUp()
    {
        CameraReviewFrame review = CameraReviewGeometry.Create(TransformMatrix.Identity, new CameraLens(90, 1, 1, 3));
        Assert.Equal(Vector3D.Zero, review.Position);
        AssertVector(Vector3D.UnitZ, review.Forward);
        AssertVector(-Vector3D.UnitY, review.Up);
        AssertVector(Vector3D.UnitX, review.Right);
        Assert.Equal(180, review.RollDegrees!.Value, 10);
        Assert.Equal(4, review.NearCorners.Length);
        Assert.Equal(4, review.FarCorners.Length);
    }

    [Fact]
    public void FrustumCornersRemainAtRequestedDepthAndScaleWithLens()
    {
        CameraLens lens = new(60, 2, .5, 5);
        CameraReviewFrame review = CameraReviewGeometry.Create(TransformMatrix.Identity, lens);
        Assert.All(review.NearCorners, corner => Assert.Equal(.5, corner.Z, 10));
        Assert.All(review.FarCorners, corner => Assert.Equal(5, corner.Z, 10));
        Assert.Equal(4, review.GetCornersAtDepth(2.0).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => review.GetCornersAtDepth(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => review.GetCornersAtDepth(double.NaN));
    }

    [Fact]
    public void NonUniformScaledShearedFrameOrthogonalizesDirections()
    {
        TransformMatrix frame = new TransformTRS(new(2, -3, 4), QuaternionD.FromAxisAngle(Vector3D.UnitY, .6), new(2, 3, 4)).ToMatrix() with
        {
            M12 = .4,
            M23 = -.2,
        };
        CameraReviewFrame review = CameraReviewGeometry.Create(frame, CameraLens.Default, invertUp: false);
        Assert.Equal(new(2, -3, 4), review.Position);
        Assert.InRange(Math.Abs(review.Forward.Length - 1), 0, 1e-10);
        Assert.InRange(Math.Abs(review.Up.Length - 1), 0, 1e-10);
        Assert.InRange(Math.Abs(review.Right.Length - 1), 0, 1e-10);
        Assert.InRange(Math.Abs(Vector3D.Dot(review.Forward, review.Up)), 0, 1e-10);
        Assert.InRange(Math.Abs(Vector3D.Dot(review.Forward, review.Right)), 0, 1e-10);
        Assert.InRange(Math.Abs(Vector3D.Dot(review.Up, review.Right)), 0, 1e-10);
    }

    [Fact]
    public void UpInversionChangesUpAndRightHandedBasis()
    {
        CameraReviewFrame normal = CameraReviewGeometry.Create(TransformMatrix.Identity, CameraLens.Default, invertUp: false);
        CameraReviewFrame inverted = CameraReviewGeometry.Create(TransformMatrix.Identity, CameraLens.Default, invertUp: true);
        AssertVector(Vector3D.UnitY, normal.Up);
        AssertVector(-Vector3D.UnitY, inverted.Up);
        AssertVector(-Vector3D.UnitX, normal.Right);
        AssertVector(Vector3D.UnitX, inverted.Right);
    }

    [Fact]
    public void SingularProjectiveAndNonFiniteInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CameraReviewGeometry.Create(TransformMatrix.CreateScale(new(1, 0, 1)), CameraLens.Default));
        Assert.Throws<ArgumentException>(() => CameraReviewGeometry.Create(TransformMatrix.Identity with { M44 = 2 }, CameraLens.Default));
        Assert.Throws<ArgumentException>(() => CameraReviewGeometry.Create(TransformMatrix.Identity with { M11 = double.NaN }, CameraLens.Default));
        Assert.Throws<ArgumentException>(() => CameraReviewGeometry.Create(TransformMatrix.Identity, default));
        Assert.Throws<InvalidDataException>(() => CameraReviewGeometry.Create(TransformMatrix.Identity with { M22 = 1e-13, M32 = 1 }, CameraLens.Default));

        Assert.Throws<InvalidDataException>(() => CameraReviewGeometry.Create(TransformMatrix.Identity, new CameraLens(60, 1e308, .01, 1000)));
    }

    private static void AssertVector(Vector3D expected, Vector3D actual, double epsilon = 1e-10)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, epsilon);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, epsilon);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, epsilon);
    }
}
