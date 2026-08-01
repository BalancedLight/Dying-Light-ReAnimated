using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class AnimationDocumentTests
{
    [Fact]
    public void SynchronizesBodyAndMimicAndBindsMappingToRigFingerprints()
    {
        RigDefinition source = CreateRig("source", "Root", 0x11111111);
        RigDefinition target = CreateRig("target", "Bip01", 0x22222222);
        var body = new AnimationClip(
            "walk",
            new FrameRate(30000, 1001),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(1, TransformTRS.Identity),
                    ]),
            ]);
        var mimic = new AnimationClip(
            "face",
            new FrameRate(30000, 1001),
            2,
            scalarTracks:
            [
                new ScalarTrack(
                    "Smile",
                    [
                        new ScalarKeyframe(0, 0),
                        new ScalarKeyframe(1, 1),
                    ]),
            ]);
        var map = new RetargetMap(
            source.Id,
            target.Id,
            [new BoneMapEntry(0, 0, BoneMappingMethod.Manual, 1)]);

        var document = new AnimationDocument(
            Guid.NewGuid(),
            "Walk and smile",
            source,
            target,
            body,
            mimic,
            map,
            AnimationRootMode.Bip01,
            PreviewProfile.ThirdPersonAuthoring);

        Assert.Single(document.SynchronizedAnimation.TransformTracks);
        Assert.Single(document.SynchronizedAnimation.ScalarTracks);
        Assert.Equal(RigSignature.Compute(source), document.MappingBinding.SourceRigSignature);
        Assert.Equal(RigSignature.Compute(target), document.MappingBinding.TargetRigSignature);
        Assert.Equal(64, document.MappingBinding.MappingFingerprint.Length);
        Assert.Equal(
            EvaluationPurpose.Export,
            document.CreateEvaluationRequest(0, EvaluationPurpose.Export).Purpose);
    }

    [Fact]
    public void RefusesUnsynchronizedMimicTiming()
    {
        RigDefinition rig = CreateRig("rig", "Root", 0x11111111);
        var body = new AnimationClip(
            "body",
            new FrameRate(30, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(1, TransformTRS.Identity),
                    ]),
            ]);
        var mimic = new AnimationClip(
            "face",
            new FrameRate(25, 1),
            2,
            scalarTracks:
            [
                new ScalarTrack(
                    "Smile",
                    [
                        new ScalarKeyframe(0, 0),
                        new ScalarKeyframe(1, 1),
                    ]),
            ]);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new AnimationDocument(
                Guid.NewGuid(),
                "invalid",
                rig,
                rig,
                body,
                mimic,
                null,
                AnimationRootMode.InPlace,
                PreviewProfile.RawAuthoring));

        Assert.Contains("same rational frame rate", exception.Message);
    }

    [Fact]
    public void RigSignatureChangesWhenBindPoseChanges()
    {
        RigDefinition first = CreateRig("rig", "Root", 0x11111111);
        RigDefinition second = new(
            "rig",
            "Rig",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity with
                    {
                        Translation = new Vector3D(1, 0, 0),
                    },
                    descriptorHash: 0x11111111),
            ],
            [
                new MorphChannelDefinition(0, "Smile", 0x33333333),
            ]);

        Assert.NotEqual(RigSignature.Compute(first), RigSignature.Compute(second));
    }

    private static RigDefinition CreateRig(
        string id,
        string boneName,
        uint descriptor) =>
        new(
            id,
            "Rig",
            [
                new BoneDefinition(
                    0,
                    boneName,
                    -1,
                    TransformTRS.Identity,
                    descriptorHash: descriptor),
            ],
            [
                new MorphChannelDefinition(0, "Smile", 0x33333333),
            ]);
}
