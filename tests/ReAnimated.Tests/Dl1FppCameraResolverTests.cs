using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class Dl1FppCameraResolverTests
{
    [Fact]
    public void ExactEyeCameraNameIsTheContractBinding()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "Head", 0, "body.head"),
            Bone(2, Dl1PreviewContract.EyeCameraBoneName, 1, "camera.eye"));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.Equal(2, resolution.BoneIndex);
        Assert.Equal(
            Dl1FppCameraBinding.ExactEyeCamera,
            resolution.Binding);
        Assert.True(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void RequestingEyeCameraByNameIsTreatedAsAutoDetect()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, Dl1PreviewContract.EyeCameraBoneName, 0));

        Dl1FppCameraResolution resolution = Dl1FppCameraResolver.Resolve(
            rig,
            Dl1PreviewContract.EyeCameraBoneName);

        Assert.Equal(
            Dl1FppCameraBinding.ExactEyeCamera,
            resolution.Binding);
        Assert.True(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void ExplicitPreviewBoneWinsOverAnExistingEyeCamera()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "Head", 0, "body.head"),
            Bone(2, Dl1PreviewContract.EyeCameraBoneName, 1, "camera.eye"));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, "Head");

        Assert.Equal(1, resolution.BoneIndex);
        Assert.Equal(
            Dl1FppCameraBinding.ExplicitPreviewBone,
            resolution.Binding);
        Assert.False(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void ExplicitRequestForTheEyeCameraBoneStillReportsTheContract()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "eyecamera", 0));

        // A saved selection may differ in case from the canonical spelling.
        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, "eyecamera");

        Assert.Equal(1, resolution.BoneIndex);
        Assert.True(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void MissingExplicitPreviewBoneFallsThroughAndRecordsEvidence()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, Dl1PreviewContract.EyeCameraBoneName, 0));

        // A rig can change under a saved project. That must degrade to
        // auto-detection rather than blanking the preview.
        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, "cam_that_no_longer_exists");

        Assert.Equal(1, resolution.BoneIndex);
        Assert.Equal(
            Dl1FppCameraBinding.ExactEyeCamera,
            resolution.Binding);
        Assert.Contains(
            "cam_that_no_longer_exists",
            resolution.Evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousCameraEyeRoleStillResolvesTheUniqueEyeCameraName()
    {
        // Checking the role first used to return -1 here, so a rig with one
        // perfectly good EyeCamera bone previewed nothing.
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "cam_left", 0, "camera.eye"),
            Bone(2, "cam_right", 0, "camera.eye"),
            Bone(3, Dl1PreviewContract.EyeCameraBoneName, 0));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.Equal(3, resolution.BoneIndex);
        Assert.Equal(
            Dl1FppCameraBinding.ExactEyeCamera,
            resolution.Binding);
        Assert.True(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void UniqueCameraEyeRoleBindsWhenNoBoneCarriesTheName()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "cam_eye_helper", 0, "camera.eye"));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.Equal(1, resolution.BoneIndex);
        Assert.Equal(
            Dl1FppCameraBinding.SemanticEyeCamera,
            resolution.Binding);
        Assert.True(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void DuplicateEyeCameraNamesAreNeverGuessed()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, Dl1PreviewContract.EyeCameraBoneName, 0),
            Bone(2, Dl1PreviewContract.EyeCameraBoneName, 0));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.False(resolution.IsResolved);
        Assert.Equal(Dl1FppCameraBinding.None, resolution.Binding);
        Assert.Contains(
            "ambiguous",
            resolution.Evidence,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeclaredHeadRoleAnchorsThePreviewWithoutClaimingTheContract()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "neck", 0, "body.neck.0"),
            Bone(2, "head", 1, "body.head"));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.Equal(2, resolution.BoneIndex);
        Assert.Equal(Dl1FppCameraBinding.HeadFallback, resolution.Binding);
        Assert.False(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void HeadIsIdentifiedByNameWhenNoRoleIsDeclared()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "mixamorig:Hips", -1),
            Bone(1, "mixamorig:Neck", 0),
            Bone(2, "mixamorig:Head", 1));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.Equal(2, resolution.BoneIndex);
        Assert.Equal(Dl1FppCameraBinding.HeadFallback, resolution.Binding);
        Assert.False(resolution.IsEyeCameraContract);
    }

    [Fact]
    public void UpperNeckIsPreferredOverLowerNeckWhenNoHeadExists()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(1, "neck", 0, "body.neck.0"),
            Bone(2, "neck1", 1, "body.neck.1"));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.Equal(2, resolution.BoneIndex);
        Assert.Equal(Dl1FppCameraBinding.NeckFallback, resolution.Binding);
    }

    [Fact]
    public void RigWithNoCameraOrAnatomicalEvidenceResolvesNothing()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "root", -1),
            Bone(1, "prop_a", 0),
            Bone(2, "prop_b", 0));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.False(resolution.IsResolved);
        Assert.Equal(Dl1FppCameraBinding.None, resolution.Binding);
        Assert.Contains(
            Dl1PreviewContract.EyeCameraBoneName,
            resolution.Evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceCameraIsNeverSubstitutedForAMissingEyeCamera()
    {
        RigDefinition rig = CreateRig(
            Bone(0, "bip01", -1),
            Bone(
                1,
                Dl1PreviewContract.ReferenceCameraBoneName,
                0,
                "camera.reference_helper"));

        Dl1FppCameraResolution resolution =
            Dl1FppCameraResolver.Resolve(rig, requestedBoneName: null);

        Assert.False(resolution.IsResolved);
    }

    private static RigDefinition CreateRig(params BoneDefinition[] bones) =>
        new("resolver-rig", "Resolver Rig", bones);

    private static BoneDefinition Bone(
        int index,
        string name,
        int parentIndex,
        string? semanticRole = null) =>
        new(
            index,
            name,
            parentIndex,
            TransformTRS.Identity,
            BoneKind.Deform,
            requiredForExport: true,
            descriptorHash: null,
            semanticRole: semanticRole);
}
