using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class AuthoritativeRootMotionTrailSamplerTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void DirectSameRigRecordedTrailCanApplyPreviewOnlyAuxiliaryMotion()
    {
        var rig = new RigDefinition(
            "direct-root-trail",
            "Direct root trail",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x11111111,
                    semanticRole: "root.skeletal"),
            ]);
        var clip = new AnimationClip(
            "recorded-with-auxiliary",
            new FrameRate(1, 1),
            3,
            transformTracks:
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            2,
                            new TransformTRS(
                                new Vector3D(2, 0, 0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ],
            auxiliaryTransformTracks:
            [
                new AuxiliaryTransformTrack(
                    Dl1RootMotionPolicy.MotionAccumulatorDescriptor,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            2,
                            new TransformTRS(
                                new Vector3D(10, 0, 0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            retargetMap: null,
            AnimationRootMode.Recorded);

        Vector3[] result =
            AuthoritativeRootMotionTrailSampler.Evaluate(
                new AuthoritativeRootMotionTrailRequest(
                    rig,
                    rig,
                    clip,
                    Mapping: null,
                    [],
                    [],
                    policy,
                    [],
                    [],
                    [],
                    SampleCount: 3,
                    PreviewMotionAccumulationEnabled: true));

        Assert.Equal(
            [
                Vector3.Zero,
                new Vector3(6, 0, 0),
                new Vector3(12, 0, 0),
            ],
            result);
    }

    [Theory]
    [InlineData(AnimationRootMode.Recorded, true)]
    [InlineData(AnimationRootMode.Bip01, true)]
    [InlineData(AnimationRootMode.InPlace, false)]
    [InlineData(AnimationRootMode.MotionAccumulator, true)]
    public void SamplesEffectiveExportableRootForEveryDl1Mode(
        AnimationRootMode rootMode,
        bool preservesMotion)
    {
        var rig = new RigDefinition(
            "root-trail",
            "Root trail",
            [
                new BoneDefinition(
                    0,
                    "DLR_OffsetHelper_CCC3CDDF",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    descriptorHash:
                        Dl1RootMotionPolicy
                            .MotionAccumulatorDescriptor),
                new BoneDefinition(
                    1,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: 0x11111111,
                    semanticRole: "root.skeletal"),
            ]);
        var clip = new AnimationClip(
            "moving-root",
            new FrameRate(1, 1),
            3,
            [
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            TransformTRS.Identity),
                        new TransformKeyframe(
                            2,
                            new TransformTRS(
                                new Vector3D(4.0, 2.0, -6.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
        var mapping = new RetargetMap(
            rig.Id,
            rig.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.ExactName,
                    1.0),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            mapping,
            rootMode);
        var progress = new InlineProgress();

        Vector3[] result =
            AuthoritativeRootMotionTrailSampler.Evaluate(
                new AuthoritativeRootMotionTrailRequest(
                    rig,
                    rig,
                    clip,
                    mapping,
                    [],
                    [],
                    policy,
                    [],
                    [],
                    [],
                    SampleCount: 3),
                progress: progress);

        Vector3[] moving =
        [
            Vector3.Zero,
            new Vector3(2.0f, 1.0f, -3.0f),
            new Vector3(4.0f, 2.0f, -6.0f),
        ];
        Assert.Equal(
            preservesMotion
                ? moving
                : [Vector3.Zero, Vector3.Zero, Vector3.Zero],
            result);
        Assert.Equal(100.0, progress.LastValue, 10);
    }

    [Fact]
    public void CancellationAndSampleBoundsFailClosed()
    {
        var rig = new RigDefinition(
            "root",
            "Root",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 1,
                    semanticRole: "root.skeletal"),
            ]);
        var clip = new AnimationClip(
            "bind",
            new FrameRate(30, 1),
            1);
        var mapping = new RetargetMap(
            rig.Id,
            rig.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            mapping,
            AnimationRootMode.Bip01);
        AuthoritativeRootMotionTrailRequest request = new(
            rig,
            rig,
            clip,
            mapping,
            [],
            [],
            policy,
            [],
            [],
            [],
            SampleCount: 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => AuthoritativeRootMotionTrailSampler.Evaluate(
                request,
                cancellationToken: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuthoritativeRootMotionTrailSampler.Evaluate(
                request with
                {
                    SampleCount =
                        AuthoritativeRootMotionTrailSampler
                            .MaximumSampleCount + 1,
                }));
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public double LastValue { get; private set; }

        public void Report(double value)
        {
            LastValue = value;
        }
    }
}
