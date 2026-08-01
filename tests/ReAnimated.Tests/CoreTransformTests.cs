using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class CoreTransformTests
{
    [Fact]
    public void ColumnVectorHierarchyUsesParentTimesLocal()
    {
        TransformTRS parent = new(
            new Vector3D(10.0, 0.0, 0.0),
            QuaternionD.FromAxisAngle(Vector3D.UnitZ, Math.PI / 2.0),
            Vector3D.One);
        TransformTRS child = new(
            Vector3D.UnitX,
            QuaternionD.Identity,
            Vector3D.One);

        TransformMatrix global = parent.ToMatrix() * child.ToMatrix();

        AssertNear(new Vector3D(10.0, 1.0, 0.0), global.Translation);
        AssertNear(new Vector3D(10.0, 2.0, 0.0), global.TransformPoint(Vector3D.UnitX));
        Assert.Equal(10.0, global.M14, 10);
        Assert.Equal(1.0, global.M24, 10);
    }

    [Fact]
    public void AffineInverseAndDecompositionRoundTrip()
    {
        TransformTRS source = new(
            new Vector3D(3.0, -2.0, 5.0),
            QuaternionD.FromAxisAngle(new Vector3D(1.0, 2.0, 3.0), 0.7),
            new Vector3D(2.0, 3.0, 4.0));
        TransformMatrix matrix = source.ToMatrix();

        TransformMatrix identity = matrix.InvertedAffine() * matrix;
        TransformTRS decomposed = matrix.Decompose();

        Assert.True(identity.NearlyEquals(TransformMatrix.Identity, 1e-9));
        Assert.True(decomposed.ToMatrix().NearlyEquals(matrix, 1e-9));
    }

    [Fact]
    public void QuaternionContractUsesXyzwAndShortestHemisphereSlerp()
    {
        QuaternionD quarterTurn = QuaternionD.FromAxisAngle(
            Vector3D.UnitZ,
            Math.PI / 2.0);
        Vector3D rotated = quarterTurn.Rotate(Vector3D.UnitX);
        QuaternionD oppositeRepresentation = -quarterTurn;

        AssertNear(Vector3D.UnitY, rotated);
        QuaternionD midpoint = QuaternionD.Slerp(
            quarterTurn,
            oppositeRepresentation,
            0.5);
        Assert.True(Math.Abs(QuaternionD.Dot(midpoint, quarterTurn)) > 0.999999);
    }

    [Fact]
    public void RigCarriesRetailIdentityDescriptorsMorphsAndNamedIkChains()
    {
        BoneDefinition[] bones =
        [
            new(
                0,
                "bip01",
                -1,
                TransformTRS.Identity,
                BoneKind.Root,
                descriptorHash: 0xCCC3CDDF,
                semanticRole: "root.motion"),
            new(
                1,
                "arm",
                0,
                new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One)),
            new(
                2,
                "forearm",
                1,
                new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One)),
        ];
        var fingerprint = new SourceAssetFingerprint(
            "Data/packs/Data0.pak::models/player.msh",
            new string('A', 64),
            "_MESH_");
        var rig = new RigDefinition(
            "dl1-player",
            "DL1 Player",
            bones,
            [new MorphChannelDefinition(0, "jaw_open", 0x12345678, "mimic.jaw_open")],
            fingerprint,
            [new TwoBoneIkChainDefinition("right_arm", 0, 1, 2)]);

        Assert.Equal(0xCCC3CDDFu, rig.Bones[0].DescriptorHash);
        Assert.Equal("root.motion", rig.Bones[0].SemanticRole);
        Assert.Equal("jaw_open", Assert.Single(rig.MorphChannels).Name);
        Assert.Equal(fingerprint, rig.SourceAssetFingerprint);
        Assert.Equal("right_arm", Assert.Single(rig.IkChains).Name);
    }

    private static void AssertNear(
        Vector3D expected,
        Vector3D actual,
        double tolerance = 1e-9)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0.0, tolerance);
    }
}
