using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class FppModelBasisTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    public void CorrectionBasisFollowsTheModelsDeclaredLeftSide(double sign)
    {
        var rig = new RigDefinition("synthetic", "synthetic", [
            new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root),
            new BoneDefinition(1, "left_marker", 0, new(new(sign, 1, 0), QuaternionD.Identity, Vector3D.One),
                BoneKind.Deform, semanticRole: "arm.left.clavicle"),
            new BoneDefinition(2, "right_marker", 0, new(new(-sign, 1, 0), QuaternionD.Identity, Vector3D.One),
                BoneKind.Deform, semanticRole: "arm.right.clavicle"),
        ]);
        var axes = MainWindowViewModel.ResolveFppModelAxes(rig);
        Assert.Equal(new Vector3D(sign, 0, 0), axes.Left);
        Assert.Equal(new Vector3D(0, 0, sign), axes.Forward);
        Assert.Equal(0, Vector3D.Dot(axes.Left, axes.Forward), precision: 12);
    }
}
