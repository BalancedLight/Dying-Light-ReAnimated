using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class BoneEditInterpolationTests
{
    [Fact]
    public void DefaultLinearSamplingUsesShortestQuaternionHemisphere()
    {
        QuaternionD target =
            QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2.0);
        var track = new BoneEditTrack(
            0,
            [
                new TransformKeyframe(
                    0.0,
                    new TransformTRS(
                        Vector3D.Zero,
                        QuaternionD.Identity,
                        Vector3D.One)),
                new TransformKeyframe(
                    10.0,
                    new TransformTRS(
                        new Vector3D(10.0, 20.0, 30.0),
                        -target,
                        new Vector3D(3.0, 5.0, 7.0))),
            ]);

        TransformTRS sampled = track.Sample(5.0);
        TransformTRS repeated = track.Sample(5.0);
        QuaternionD expected =
            QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 4.0);

        Assert.Equal(BoneEditInterpolation.Linear, track.Interpolation);
        Assert.Equal(new Vector3D(5.0, 10.0, 15.0), sampled.Translation);
        Assert.Equal(new Vector3D(2.0, 3.0, 4.0), sampled.Scale);
        Assert.InRange(
            Math.Abs(QuaternionD.Dot(sampled.Rotation, expected)),
            1.0 - 1e-12,
            1.0);
        Assert.InRange(
            Math.Abs(sampled.Rotation.LengthSquared - 1.0),
            0.0,
            1e-12);
        Assert.Equal(sampled, repeated);
    }

    [Fact]
    public void StepSamplingHoldsCompleteLocalTrsUntilExactNextKey()
    {
        TransformTRS first = new(
            new Vector3D(1.0, 2.0, 3.0),
            QuaternionD.FromAxisAngle(Vector3D.UnitX, 0.25),
            new Vector3D(1.0, 2.0, 3.0));
        TransformTRS second = new(
            new Vector3D(9.0, 8.0, 7.0),
            QuaternionD.FromAxisAngle(Vector3D.UnitZ, 1.25),
            new Vector3D(4.0, 5.0, 6.0));
        var track = new BoneEditTrack(
            0,
            [
                new TransformKeyframe(2.0, first),
                new TransformKeyframe(6.0, second),
            ],
            BoneEditInterpolation.Step);

        Assert.Equal(first, track.Sample(-100.0));
        Assert.Equal(first, track.Sample(2.0));
        Assert.Equal(first, track.Sample(5.999999));
        Assert.Equal(second, track.Sample(6.0));
        Assert.Equal(second, track.Sample(100.0));
    }

    [Fact]
    public void TrackRejectsUndefinedInterpolationMode()
    {
        ImmutableArray<TransformKeyframe> keys =
        [
            new TransformKeyframe(0.0, TransformTRS.Identity),
        ];

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new BoneEditTrack(
                    0,
                    keys,
                    (BoneEditInterpolation)99));

        Assert.Equal("interpolation", exception.ParamName);
    }

    [Fact]
    public void TrackRejectsNonFiniteSamplesForEveryInterpolationMode()
    {
        TransformKeyframe[] keys =
        [
            new TransformKeyframe(0.0, TransformTRS.Identity),
        ];

        foreach (BoneEditInterpolation interpolation in
                 Enum.GetValues<BoneEditInterpolation>())
        {
            var track = new BoneEditTrack(0, keys, interpolation);
            Assert.Throws<ArgumentOutOfRangeException>(
                () => track.Sample(double.NaN));
        }
    }
}
