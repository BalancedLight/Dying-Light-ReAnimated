using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Tests;

public sealed class EvaluationPipelineTests
{
    [Fact]
    public void PreviewOnlyLayerNeverChangesAuthoredExportPose()
    {
        RigDefinition rig = CreateSingleBoneRig();
        AnimationClip clip = CreateTranslationClip();
        BoneEditLayer authored = CreateLayer(
            "Authored",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            new Vector3D(2.0, 0.0, 0.0));
        BoneEditLayer previewOnly = CreateLayer(
            "Preview",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.PreviewOnly,
            new Vector3D(100.0, 0.0, 0.0));
        var evaluator = new AnimationEvaluator();

        EvaluationFrame preview = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.5,
                PreviewProfile.ThirdPersonAuthoring,
                editLayers: [authored, previewOnly],
                purpose: EvaluationPurpose.Preview));
        EvaluationFrame export = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.5,
                PreviewProfile.ThirdPersonAuthoring,
                editLayers: [authored, previewOnly],
                purpose: EvaluationPurpose.Export));

        Assert.Equal(7.0, preview.AuthoredPose.LocalTransforms[0].Translation.X, 10);
        Assert.Equal(100.0, preview.DisplayPose.LocalTransforms[0].Translation.X, 10);
        Assert.Equal(7.0, export.AuthoredPose.LocalTransforms[0].Translation.X, 10);
        Assert.Equal(7.0, export.DisplayPose.LocalTransforms[0].Translation.X, 10);
    }

    [Fact]
    public void AuthoritativeEvaluatorHonorsBoneEditTrackInterpolation()
    {
        RigDefinition rig = CreateSingleBoneRig();
        AnimationClip clip = CreateTranslationClip();
        TransformKeyframe[] keys =
        [
            new TransformKeyframe(0.0, TransformTRS.Identity),
            new TransformKeyframe(
                10.0,
                new TransformTRS(
                    new Vector3D(10.0, 0.0, 0.0),
                    QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2.0),
                    new Vector3D(3.0, 3.0, 3.0))),
        ];
        BoneEditLayer linear = new(
            Guid.NewGuid(),
            "Linear",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [new BoneEditTrack(0, keys, BoneEditInterpolation.Linear)]);
        BoneEditLayer step = new(
            Guid.NewGuid(),
            "Step",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [new BoneEditTrack(0, keys, BoneEditInterpolation.Step)]);
        var evaluator = new AnimationEvaluator();

        EvaluationFrame linearExport = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.5,
                PreviewProfile.ThirdPersonAuthoring,
                editLayers: [linear],
                purpose: EvaluationPurpose.Export));
        EvaluationFrame stepPreview = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.5,
                PreviewProfile.ThirdPersonAuthoring,
                editLayers: [step],
                purpose: EvaluationPurpose.Preview));
        EvaluationFrame stepExport = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.5,
                PreviewProfile.ThirdPersonAuthoring,
                editLayers: [step],
                purpose: EvaluationPurpose.Export));

        Assert.Equal(
            5.0,
            linearExport.AuthoredPose.LocalTransforms[0].Translation.X,
            10);
        Assert.Equal(
            TransformTRS.Identity,
            stepPreview.AuthoredPose.LocalTransforms[0]);
        Assert.Equal(
            stepPreview.AuthoredPose.LocalTransforms[0],
            stepExport.AuthoredPose.LocalTransforms[0]);
        Assert.Equal(
            stepExport.AuthoredPose.LocalTransforms[0],
            stepExport.DisplayPose.LocalTransforms[0]);
    }

    [Fact]
    public void EvaluatorAppliesTwoBoneIkIntoAuthoredPose()
    {
        var rig = new RigDefinition(
            "arm",
            "Arm",
            [
                new BoneDefinition(0, "shoulder", -1, TransformTRS.Identity, BoneKind.Root),
                new BoneDefinition(
                    1,
                    "elbow",
                    0,
                    new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One)),
                new BoneDefinition(
                    2,
                    "hand",
                    1,
                    new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One)),
            ]);
        var clip = new AnimationClip("idle", new FrameRate(30, 1), 1);
        var constraint = new TwoBoneIkConstraint(
            0,
            1,
            2,
            new Vector3D(1.0, 1.0, 0.0),
            Vector3D.UnitZ);

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.ThirdPersonAuthoring,
                ikConstraints: [constraint],
                purpose: EvaluationPurpose.Export));

        Vector3D endPosition = frame.AuthoredPose.GlobalMatrices[2].Translation;
        Assert.InRange(Math.Abs(endPosition.X - 1.0), 0.0, 1e-7);
        Assert.InRange(Math.Abs(endPosition.Y - 1.0), 0.0, 1e-7);
        Assert.InRange(Math.Abs(endPosition.Z), 0.0, 1e-7);
    }

    [Fact]
    public void FirstPersonCameraUsesDisplayPoseAndReportsMissingBindings()
    {
        var rig = new RigDefinition(
            "fpp",
            "FPP",
            [
                new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root),
                new BoneDefinition(
                    1,
                    "refcamera",
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.7, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Camera,
                    requiredForExport: false,
                    semanticRole: "camera.reference"),
            ]);
        var clip = new AnimationClip("idle", new FrameRate(30, 1), 1);
        var fpp = new PreviewProfile(
            "fpp",
            PreviewViewMode.FirstPerson,
            AuthoringPreviewFidelity.AuthoringAccurate |
                AuthoringPreviewFidelity.FirstPersonOcclusion,
            PreviewVisualStyle.MaterialApproximation,
            "refcamera",
            new CameraLens(75.0, 16.0 / 9.0, 0.005, 500.0),
            new TransformTRS(
                new Vector3D(0.0, 0.1, 0.0),
                QuaternionD.Identity,
                Vector3D.One));

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(rig, rig, clip, 0.0, fpp));

        EvaluatedCamera camera = Assert.IsType<EvaluatedCamera>(frame.Camera);
        Assert.Equal(1.8, camera.WorldTransform.Translation.Y, 10);
        Assert.True(camera.IsFirstPerson);
        Assert.Empty(frame.Diagnostics);

        var missing = new PreviewProfile(
            "fpp-missing",
            PreviewViewMode.FirstPerson,
            fpp.Fidelity,
            fpp.VisualStyle,
            "eyecamera",
            fpp.CameraLens,
            fpp.CameraOffset);
        EvaluationFrame missingFrame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(rig, rig, clip, 0.0, missing));
        Assert.Null(missingFrame.Camera);
        Assert.Contains(
            missingFrame.Diagnostics,
            diagnostic => diagnostic.Code == "preview_camera_bone_not_found");
    }

    [Fact]
    public void AttachmentsAndProceduralStageKeepAuthoredAndDisplayOutputsDistinct()
    {
        RigDefinition rig = CreateSingleBoneRig();
        AnimationClip clip = CreateTranslationClip();
        Guid authoredAsset = Guid.NewGuid();
        Guid previewAsset = Guid.NewGuid();
        AttachmentBinding[] attachments =
        [
            new(
                Guid.NewGuid(),
                authoredAsset,
                "authored prop",
                0,
                new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One),
                AttachmentScope.AuthoredExportable),
            new(
                Guid.NewGuid(),
                previewAsset,
                "preview weapon",
                0,
                new TransformTRS(
                    new Vector3D(2.0, 0.0, 0.0),
                    QuaternionD.Identity,
                    Vector3D.One),
                AttachmentScope.PreviewOnly),
        ];
        var stage = new ConstantBoneOffsetPreviewStage(
            "dl1-profile-root-offset",
            0,
            new TransformTRS(
                new Vector3D(3.0, 0.0, 0.0),
                QuaternionD.Identity,
                Vector3D.One),
            AuthoringPreviewFidelity.Bones);
        var evaluator = new AnimationEvaluator([stage]);

        EvaluationFrame frame = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.5,
                PreviewProfile.ThirdPersonAuthoring,
                attachments: attachments));

        Assert.Equal(5.0, frame.AuthoredPose.GlobalMatrices[0].Translation.X, 10);
        Assert.Equal(8.0, frame.DisplayPose.GlobalMatrices[0].Translation.X, 10);
        EvaluatedAttachment authored = Assert.Single(frame.AuthoredAttachments);
        Assert.Equal(6.0, authored.WorldTransform.Translation.X, 10);
        Assert.Equal(2, frame.DisplayAttachments.Length);
        Assert.Equal(
            9.0,
            frame.DisplayAttachments.Single(
                attachment => attachment.AssetId == authoredAsset).WorldTransform.Translation.X,
            10);
        Assert.Equal(
            10.0,
            frame.DisplayAttachments.Single(
                attachment => attachment.AssetId == previewAsset).WorldTransform.Translation.X,
            10);
    }

    [Fact]
    public void Anm2AdapterSamplesOnlyDescriptorOrderedAuthoredPose()
    {
        var rig = new RigDefinition(
            "anm2",
            "ANM2",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0xCCC3CDDF),
            ],
            [new MorphChannelDefinition(0, "jaw_open", 0x10203040)]);
        var clip = new AnimationClip(
            "anm2-ready",
            new FrameRate(10, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0.0, TransformTRS.Identity),
                        new TransformKeyframe(
                            1.0,
                            new TransformTRS(
                                Vector3D.UnitX,
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ],
            [
                new ScalarTrack(
                    "jaw_open",
                    [new ScalarKeyframe(0.0, 0.0), new ScalarKeyframe(1.0, 1.0)]),
            ]);
        BoneEditLayer previewOnly = CreateLayer(
            "Never export",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.PreviewOnly,
            new Vector3D(100.0, 0.0, 0.0));
        var evaluator = new AnimationEvaluator(
            [
                new ConstantBoneOffsetPreviewStage(
                    "also-never-export",
                    0,
                    new TransformTRS(
                        new Vector3D(50.0, 0.0, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    AuthoringPreviewFidelity.Bones),
            ]);
        var adapter = new Anm2EvaluationAdapter(evaluator);

        Dl1Anm2AuthoringSequence sequence = adapter.SampleAuthoredFrames(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.ThirdPersonAuthoring,
                editLayers: [previewOnly],
                purpose: EvaluationPurpose.Preview));

        Assert.Equal(2, sequence.Frames.Length);
        Assert.Equal(0xCCC3CDDFu, Assert.Single(sequence.Frames[1].Tracks).DescriptorHash);
        Assert.Equal(1.0, sequence.Frames[1].Tracks[0].LocalTransform.Translation.X, 10);
        Assert.Equal(0x10203040u, Assert.Single(sequence.Frames[1].Morphs).DescriptorHash);
        Assert.Equal(1.0, sequence.Frames[1].Morphs[0].Value, 10);
    }

    private static RigDefinition CreateSingleBoneRig() =>
        new(
            "single",
            "Single",
            [new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root)]);

    private static AnimationClip CreateTranslationClip() =>
        new(
            "move",
            new FrameRate(10, 1),
            11,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0.0, TransformTRS.Identity),
                        new TransformKeyframe(
                            10.0,
                            new TransformTRS(
                                new Vector3D(10.0, 0.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);

    private static BoneEditLayer CreateLayer(
        string name,
        BoneEditBlendMode blendMode,
        BoneEditLayerScope scope,
        Vector3D translation) =>
        new(
            Guid.NewGuid(),
            name,
            blendMode,
            scope,
            1.0,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                translation,
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
}
