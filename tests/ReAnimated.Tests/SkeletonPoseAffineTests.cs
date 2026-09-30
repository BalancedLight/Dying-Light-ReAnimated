using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class SkeletonPoseAffineTests
{
    [Fact]
    public void MachineEpsilonHomogeneousResidueDoesNotDiscardExactAuthoredFrame()
    {
        var rig = new RigDefinition(
            "generic",
            "Generic",
            [new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root)]);
        TransformMatrix authored = TransformMatrix.Identity with
        {
            M14 = 1.25,
            M41 = 2e-16,
            M42 = -1e-16,
            M43 = 3e-18,
            M44 = 1 + 2e-16,
        };

        var pose = new SkeletonPose(rig, [TransformTRS.Identity], [authored]);

        Assert.True(pose.HasAffineLocalMatrices);
        Assert.Equal(1.25, pose.LocalMatrices[0].M14);
        Assert.Equal(0, pose.LocalMatrices[0].M41);
        Assert.Equal(0, pose.LocalMatrices[0].M42);
        Assert.Equal(0, pose.LocalMatrices[0].M43);
        Assert.Equal(1, pose.LocalMatrices[0].M44);
        Assert.Equal(1.25, pose.GlobalMatrices[0].M14);
    }

    [Fact]
    public void ProjectiveAndNonfiniteBottomRowsStillFail()
    {
        var rig = new RigDefinition(
            "generic",
            "Generic",
            [new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root)]);
        Assert.Throws<ArgumentException>(() => new SkeletonPose(
            rig, [TransformTRS.Identity],
            [TransformMatrix.Identity with { M41 = 1e-6 }]));
        Assert.Throws<ArgumentException>(() => new SkeletonPose(
            rig, [TransformTRS.Identity],
            [TransformMatrix.Identity with { M44 = double.NaN }]));
    }
}
