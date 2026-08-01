using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Tests;

public sealed class EvaluationAuthoringLayerIntegrationTests
{
    [Fact]
    public void BoneMaskScalesTheNonDestructiveLayerPerBone()
    {
        RigDefinition rig = new(
            "masked",
            "Masked",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity),
            ]);
        var clip = new AnimationClip(
            "pose",
            new FrameRate(30, 1),
            1);
        var layer = new BoneEditLayer(
            Guid.NewGuid(),
            "Quarter influence",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable,
            1,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            TransformTRS.Identity with
                            {
                                Translation = new Vector3D(10, 0, 0),
                            }),
                    ]),
            ],
            boneMask: new Dictionary<int, double>
            {
                [0] = 0.25,
            });

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0,
                PreviewProfile.RawAuthoring,
                editLayers: [layer],
                purpose: EvaluationPurpose.Export));

        Assert.Equal(
            new Vector3D(2.5, 0, 0),
            frame.AuthoredPose.LocalTransforms[0].Translation);
    }

    [Fact]
    public void PreviewFacialLayerAndRuntimeThresholdNeverChangeExportWeights()
    {
        RigDefinition rig = new(
            "face",
            "Face",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    descriptorHash: 1),
            ],
            [
                new MorphChannelDefinition(0, "Blink", 2),
            ]);
        var clip = new AnimationClip(
            "blink",
            new FrameRate(30, 1),
            1,
            scalarTracks:
            [
                new ScalarTrack(
                    "Blink",
                    [new ScalarKeyframe(0, 0.8)]),
            ]);
        var previewLayer = new MorphEditLayer(
            Guid.NewGuid(),
            "Optional preview blink",
            MorphEditBlendMode.Override,
            MorphEditLayerScope.PreviewOnly,
            1,
            [
                new MorphEditTrack(
                    "Blink",
                    [new ScalarKeyframe(0, 0.0005)]),
            ]);
        var evaluator = new AnimationEvaluator();

        EvaluationFrame preview = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0,
                PreviewProfile.ThirdPersonAuthoring,
                purpose: EvaluationPurpose.Preview,
                morphEditLayers: [previewLayer]));
        EvaluationFrame export = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0,
                PreviewProfile.ThirdPersonAuthoring,
                purpose: EvaluationPurpose.Export,
                morphEditLayers: [previewLayer]));

        Assert.Equal(0.8, preview.AuthoredMorphWeights["Blink"], 8);
        Assert.Empty(preview.DisplayMorphWeights);
        Assert.Equal(0.8, export.AuthoredMorphWeights["Blink"], 8);
        Assert.Equal(0.8, export.DisplayMorphWeights["Blink"], 8);
    }

    [Fact]
    public void KeyedIkLayerAppliesRequestedWorldEndOrientation()
    {
        RigDefinition rig = new(
            "arm",
            "Arm",
            [
                new BoneDefinition(
                    0,
                    "Shoulder",
                    -1,
                    TransformTRS.Identity),
                new BoneDefinition(
                    1,
                    "Elbow",
                    0,
                    TransformTRS.Identity with
                    {
                        Translation = Vector3D.UnitX,
                    }),
                new BoneDefinition(
                    2,
                    "Hand",
                    1,
                    TransformTRS.Identity with
                    {
                        Translation = Vector3D.UnitX,
                    }),
            ]);
        var clip = new AnimationClip(
            "reach",
            new FrameRate(30, 1),
            1);
        QuaternionD requested = QuaternionD.FromAxisAngle(
            Vector3D.UnitZ,
            Math.PI / 2);
        var layer = new IkConstraintLayer(
            Guid.NewGuid(),
            "Hand placement",
            0,
            1,
            2,
            1,
            [
                new IkConstraintKeyframe(
                    0,
                    new Vector3D(2, 0, 0),
                    Vector3D.UnitY,
                    requested),
            ]);

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0,
                PreviewProfile.RawAuthoring,
                purpose: EvaluationPurpose.Export,
                ikLayers: [layer]));

        QuaternionD actual =
            frame.AuthoredPose.GlobalMatrices[2].Decompose().Rotation;
        Assert.True(
            Math.Abs(QuaternionD.Dot(actual, requested)) > 0.999999);
    }

    [Fact]
    public void KeyedIkLayerBakesToDeterministicOverrideTracks()
    {
        RigDefinition rig = new(
            "bake-arm",
            "Bake arm",
            [
                new BoneDefinition(
                    0,
                    "Shoulder",
                    -1,
                    TransformTRS.Identity),
                new BoneDefinition(
                    1,
                    "Elbow",
                    0,
                    TransformTRS.Identity with
                    {
                        Translation = Vector3D.UnitX,
                    }),
                new BoneDefinition(
                    2,
                    "Hand",
                    1,
                    TransformTRS.Identity with
                    {
                        Translation = Vector3D.UnitX,
                    }),
            ]);
        var clip = new AnimationClip(
            "reach",
            new FrameRate(30, 1),
            3);
        Guid ikLayerId = Guid.NewGuid();
        var ikLayer = new IkConstraintLayer(
            ikLayerId,
            "Hand placement",
            0,
            1,
            2,
            1,
            [
                new IkConstraintKeyframe(
                    0,
                    new Vector3D(2, 0, 0),
                    Vector3D.UnitZ),
                new IkConstraintKeyframe(
                    2,
                    new Vector3D(1, 1, 0),
                    Vector3D.UnitZ),
            ],
            bakeToEditLayer: true);
        var evaluator = new AnimationEvaluator();
        var template = new EvaluationRequest(
            rig,
            rig,
            clip,
            0,
            PreviewProfile.RawAuthoring,
            purpose: EvaluationPurpose.Export,
            ikLayers: [ikLayer]);

        BoneEditLayer baked =
            IkConstraintLayerBaker.BakeToOverrideLayer(
                evaluator,
                template,
                ikLayerId,
                Guid.NewGuid(),
                "Baked hand placement");

        Assert.Equal(BoneEditBlendMode.Override, baked.BlendMode);
        Assert.Equal(
            BoneEditLayerScope.AuthoredExportable,
            baked.Scope);
        Assert.Equal(3, baked.Tracks.Length);
        Assert.All(
            baked.Tracks,
            track => Assert.Equal(3, track.Keyframes.Length));
        for (var frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            double seconds =
                clip.FrameRate.SecondsForFrame(frameIndex);
            EvaluationFrame constrained = evaluator.Evaluate(
                new EvaluationRequest(
                    rig,
                    rig,
                    clip,
                    seconds,
                    PreviewProfile.RawAuthoring,
                    purpose: EvaluationPurpose.Export,
                    ikLayers: [ikLayer]));
            EvaluationFrame replayed = evaluator.Evaluate(
                new EvaluationRequest(
                    rig,
                    rig,
                    clip,
                    seconds,
                    PreviewProfile.RawAuthoring,
                    editLayers: [baked],
                    purpose: EvaluationPurpose.Export));

            Assert.True(
                constrained.AuthoredPose.LocalTransforms.SequenceEqual(
                    replayed.AuthoredPose.LocalTransforms));
        }
    }

    [Fact]
    public void KeyedIkBakeHonorsCancellationBeforePublishingAnyLayer()
    {
        RigDefinition rig = new(
            "cancel-bake-arm",
            "Cancel bake arm",
            [
                new BoneDefinition(
                    0,
                    "Shoulder",
                    -1,
                    TransformTRS.Identity),
                new BoneDefinition(
                    1,
                    "Elbow",
                    0,
                    TransformTRS.Identity with
                    {
                        Translation = Vector3D.UnitX,
                    }),
                new BoneDefinition(
                    2,
                    "Hand",
                    1,
                    TransformTRS.Identity with
                    {
                        Translation = Vector3D.UnitX,
                    }),
            ]);
        Guid ikLayerId = Guid.NewGuid();
        var ikLayer = new IkConstraintLayer(
            ikLayerId,
            "Cancelable hand placement",
            0,
            1,
            2,
            1,
            [
                new IkConstraintKeyframe(
                    0,
                    new Vector3D(2, 0, 0),
                    Vector3D.UnitZ),
            ],
            bakeToEditLayer: true);
        var template = new EvaluationRequest(
            rig,
            rig,
            new AnimationClip(
                "long reach",
                new FrameRate(30, 1),
                1_000),
            0,
            PreviewProfile.RawAuthoring,
            purpose: EvaluationPurpose.Export,
            ikLayers: [ikLayer]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            IkConstraintLayerBaker.BakeToOverrideLayer(
                new AnimationEvaluator(),
                template,
                ikLayerId,
                Guid.NewGuid(),
                "Canceled bake",
                cancellation.Token));
    }
}
