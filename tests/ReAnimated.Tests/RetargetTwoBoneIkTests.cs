using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Tests;

public sealed class RetargetTwoBoneIkTests
{
    [Fact]
    public void ReachableTargetPreservesSegmentLengthsAndUsesPoleSide()
    {
        TwoBoneIkSolution solution = TwoBoneIkSolver.Solve(
            Vector3D.Zero,
            Vector3D.UnitX,
            new Vector3D(2.0, 0.0, 0.0),
            new Vector3D(1.0, 1.0, 0.0),
            Vector3D.UnitZ);

        Assert.False(solution.WasClamped);
        AssertNear(1.0, Vector3D.Distance(solution.RootPosition, solution.JointPosition));
        AssertNear(1.0, Vector3D.Distance(solution.JointPosition, solution.EndPosition));
        AssertVectorNear(new Vector3D(1.0, 1.0, 0.0), solution.EndPosition);
        Assert.True(solution.JointPosition.Z > 0.0);
    }

    [Fact]
    public void UnreachableTargetClampsWithoutChangingChainLengths()
    {
        TwoBoneIkSolution solution = TwoBoneIkSolver.Solve(
            Vector3D.Zero,
            Vector3D.UnitX,
            new Vector3D(2.0, 0.0, 0.0),
            new Vector3D(10.0, 0.0, 0.0),
            Vector3D.UnitY);

        Assert.True(solution.WasClamped);
        Assert.InRange(solution.EndPosition.X, 1.999999, 2.0);
        AssertNear(1.0, Vector3D.Distance(solution.RootPosition, solution.JointPosition));
        AssertNear(1.0, Vector3D.Distance(solution.JointPosition, solution.EndPosition));
    }

    [Fact]
    public void ZeroLengthSegmentFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(
            () => TwoBoneIkSolver.Solve(
                Vector3D.Zero,
                Vector3D.Zero,
                Vector3D.UnitX,
                Vector3D.UnitX,
                Vector3D.UnitY));
    }

    private static void AssertNear(
        double expected,
        double actual,
        double tolerance = 1e-8)
    {
        Assert.InRange(Math.Abs(expected - actual), 0.0, tolerance);
    }

    private static void AssertVectorNear(
        Vector3D expected,
        Vector3D actual,
        double tolerance = 1e-8)
    {
        AssertNear(expected.X, actual.X, tolerance);
        AssertNear(expected.Y, actual.Y, tolerance);
        AssertNear(expected.Z, actual.Z, tolerance);
    }
}
