using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Tests;

public sealed class IkConstraintLayerTests
{
    [Fact]
    public void SamplesEffectorPoleAndEndOrientationDeterministically()
    {
        var layer = new IkConstraintLayer(
            Guid.NewGuid(),
            "Left hand",
            0,
            1,
            2,
            0.75,
            [
                new IkConstraintKeyframe(
                    0,
                    Vector3D.Zero,
                    Vector3D.UnitY,
                    QuaternionD.Identity),
                new IkConstraintKeyframe(
                    10,
                    new Vector3D(10, 0, 0),
                    new Vector3D(0, 3, 0),
                    QuaternionD.FromAxisAngle(
                        Vector3D.UnitZ,
                        Math.PI)),
            ],
            bakeToEditLayer: true);

        TwoBoneIkConstraint sampled = layer.Sample(5);

        Assert.Equal(new Vector3D(5, 0, 0), sampled.Target);
        Assert.Equal(new Vector3D(0, 2, 0), sampled.Pole);
        Assert.Equal(0.75, sampled.Weight);
        Assert.True(sampled.EndOrientation.HasValue);
        Assert.True(layer.BakeToEditLayer);
    }

    [Fact]
    public void DisabledLayerSamplesWithZeroWeight()
    {
        var layer = new IkConstraintLayer(
            Guid.NewGuid(),
            "Foot",
            0,
            1,
            2,
            1,
            [
                new IkConstraintKeyframe(
                    0,
                    Vector3D.Zero,
                    Vector3D.UnitY),
            ],
            enabled: false);

        Assert.Equal(0, layer.Sample(0).Weight);
    }
}
