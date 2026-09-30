using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class SkeletonSegmentLengthDriftEvaluatorTests
{
    [Fact]
    public void RotationAndWorldTranslationDoNotCountAsStretch()
    {
        SkeletonRenderData bind = Skeleton(
            Bone("root", -1, 0, 0),
            Bone("upper", 0, 0, 1),
            Bone("lower", 1, 0, 2));
        SkeletonRenderData rotated = Skeleton(
            Bone("root", -1, 5, 6),
            Bone("upper", 0, 6, 6),
            Bone("lower", 1, 7, 6));

        SkeletonSegmentLengthDriftReport report =
            SkeletonSegmentLengthDriftEvaluator.Compare(bind, rotated);

        Assert.Equal(2, report.ComparableSegments);
        Assert.Equal(0.0, report.MaximumAbsoluteDriftPercent, 6);
        Assert.Null(report.WorstBoneName);
    }

    [Fact]
    public void ChangedChildOffsetReportsActualSegmentDriftAndSkipsHelpers()
    {
        SkeletonRenderData bind = Skeleton(
            Bone("root", -1, 0, 0),
            Bone("upper", 0, 0, 1),
            Bone("lower", 1, 0, 2),
            Bone("locator", 0, 0, 3, BoneRenderRole.Helper));
        SkeletonRenderData stretched = Skeleton(
            Bone("root", -1, 0, 0),
            Bone("upper", 0, 0, 1),
            Bone("lower", 1, 0, 3),
            Bone("locator", 0, 0, 30, BoneRenderRole.Helper));

        SkeletonSegmentLengthDriftReport report =
            SkeletonSegmentLengthDriftEvaluator.Compare(bind, stretched);

        Assert.Equal(2, report.ComparableSegments);
        Assert.Equal(100.0, report.MaximumAbsoluteDriftPercent, 6);
        Assert.Equal("lower", report.WorstBoneName);
        Assert.DoesNotContain(report.Segments, row => row.BoneName == "locator");
    }

    [Fact]
    public void DifferentBoneIdentityCannotProduceAFalseMetric()
    {
        SkeletonRenderData bind = Skeleton(
            Bone("root", -1, 0, 0),
            Bone("lower", 0, 0, 1));
        SkeletonRenderData unrelated = Skeleton(
            Bone("root", -1, 0, 0),
            Bone("different", 0, 0, 1));

        Assert.Throws<ArgumentException>(() =>
            SkeletonSegmentLengthDriftEvaluator.Compare(bind, unrelated));
    }

    private static SkeletonRenderData Skeleton(params BoneRenderData[] bones) =>
        new(bones, Matrix4x4.Identity);

    private static BoneRenderData Bone(
        string name,
        int parent,
        float x,
        float y,
        BoneRenderRole role = BoneRenderRole.Deform) =>
        new(name, parent, Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(x, y, 0), false)
        {
            Role = role,
        };
}
