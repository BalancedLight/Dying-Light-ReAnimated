using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class Dl1AuthoringPolicyTests
{
    // Python oracle:
    // tests/test_auto_root_cached_sparse.py::
    // test_two_turn_root_policies_remove_or_transfer_heading_and_preserve_tilt
    [Theory]
    [InlineData(AnimationRootMode.InPlace)]
    [InlineData(AnimationRootMode.Bip01)]
    [InlineData(AnimationRootMode.MotionAccumulator)]
    public void MatchesDl1LegacyRootTranslationAndHeadingOwnership(
        AnimationRootMode mode)
    {
        RootPolicyFixture fixture = CreateRootPolicyFixture();
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            fixture.Rig,
            fixture.Rig,
            null,
            mode);
        var request = new EvaluationRequest(
            fixture.Rig,
            fixture.Rig,
            fixture.Clip,
            1.0,
            PreviewProfile.RawAuthoring,
            purpose: EvaluationPurpose.Export,
            dl1AuthoringPolicy: policy);

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(request);
        TransformTRS root = frame.AuthoredPose.LocalTransforms[0];
        TransformTRS accumulator = frame.AuthoredPose.LocalTransforms[1];

        switch (mode)
        {
            case AnimationRootMode.InPlace:
                AssertVectorNear(new Vector3D(0.0, 1.0, 0.0), root.Translation);
                AssertRotationNear(fixture.Tilt, root.Rotation);
                Assert.Equal(TransformTRS.Identity, accumulator);
                break;
            case AnimationRootMode.Bip01:
                AssertVectorNear(new Vector3D(2.0, 2.0, -3.0), root.Translation);
                AssertRotationNear(
                    (fixture.Heading * fixture.Tilt).Normalized(),
                    root.Rotation);
                Assert.Equal(fixture.Rig.Bones[1].LocalBindPose, accumulator);
                break;
            case AnimationRootMode.MotionAccumulator:
                AssertVectorNear(new Vector3D(0.0, 2.0, 0.0), root.Translation);
                AssertRotationNear(fixture.Tilt, root.Rotation);
                AssertVectorNear(
                    new Vector3D(2.0, 0.0, -3.0),
                    accumulator.Translation);
                AssertRotationNear(fixture.Heading, accumulator.Rotation);
                AssertVectorNear(Vector3D.One, accumulator.Scale);
                break;
            default:
                throw new InvalidOperationException();
        }
    }

    // Python oracle:
    // tests/test_auto_root_cached_sparse.py::
    // test_heading_policy_reconstructs_local_when_selected_root_has_parent
    [Fact]
    public void InPlaceHeadingCorrectionReconstructsParentedRootLocalTransform()
    {
        QuaternionD parentTilt = QuaternionD.FromAxisAngle(
            Vector3D.UnitZ,
            10.0 * Math.PI / 180.0);
        QuaternionD childHeading = QuaternionD.FromAxisAngle(
            Vector3D.UnitY,
            40.0 * Math.PI / 180.0);
        var rig = new RigDefinition(
            "dl1-parented-root-policy",
            "DL1 parented root policy",
            [
                new BoneDefinition(
                    0,
                    "armature_parent",
                    -1,
                    new TransformTRS(
                        new Vector3D(0.5, 0.0, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Helper,
                    descriptorHash: 0x10101010),
                new BoneDefinition(
                    1,
                    "pelvis",
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.0, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Root,
                    descriptorHash: 0x20202020,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    2,
                    "DLR_OffsetHelper_CCC3CDDF",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    descriptorHash:
                        Dl1RootMotionPolicy
                            .MotionAccumulatorDescriptor),
            ]);
        var clip = new AnimationClip(
            "parented-root",
            new FrameRate(1, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            rig.Bones[0].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            rig.Bones[0].LocalBindPose with
                            {
                                Rotation = parentTilt,
                            }),
                    ]),
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            rig.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            rig.Bones[1].LocalBindPose with
                            {
                                Rotation = childHeading,
                            }),
                    ]),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            null,
            AnimationRootMode.InPlace,
            targetRootBoneName: "pelvis");
        var request = new EvaluationRequest(
            rig,
            rig,
            clip,
            1.0,
            PreviewProfile.RawAuthoring,
            purpose: EvaluationPurpose.Export,
            dl1AuthoringPolicy: policy);

        EvaluationFrame frame =
            new AnimationEvaluator().Evaluate(request);
        TransformTRS correctedLocal =
            frame.AuthoredPose.LocalTransforms[1];
        TransformTRS correctedGlobal =
            frame.AuthoredPose.GlobalMatrices[1].Decompose();
        TransformTRS bindGlobal =
            rig.CreateBindPose().GlobalMatrices[1].Decompose();
        QuaternionD headingTwist = ExtractTwist(
            correctedGlobal.Rotation,
            Vector3D.UnitY);

        Assert.True(correctedLocal.IsFinite);
        Assert.True(correctedGlobal.IsFinite);
        AssertRotationNear(
            parentTilt,
            frame.AuthoredPose.LocalTransforms[0].Rotation);
        AssertVectorNear(
            bindGlobal.Translation,
            correctedGlobal.Translation);
        AssertRotationNear(
            QuaternionD.Identity,
            headingTwist);
        Assert.Equal(
            TransformTRS.Identity,
            frame.AuthoredPose.LocalTransforms[2]);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void CrossRigBip01TransfersSourcePelvisTravelToSelectedDl1Root()
    {
        TransformTRS sourceBind = new(
            new Vector3D(0.0, 1.0, 0.0),
            QuaternionD.Identity,
            Vector3D.One);
        var source = new RigDefinition(
            "mixamo-root-motion",
            "Mixamo root motion",
            [
                new BoneDefinition(
                    0,
                    "mixamorig:Hips",
                    -1,
                    sourceBind,
                    BoneKind.Root,
                    descriptorHash: 0x01010101,
                    semanticRole: "body.pelvis"),
            ]);
        var target = new RigDefinition(
            "dl1-bip01-root-motion",
            "DL1 Bip01 root motion",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x10101010,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "pelvis",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: 0x20202020,
                    semanticRole: "body.pelvis"),
            ]);
        QuaternionD heading = QuaternionD.FromAxisAngle(
            Vector3D.UnitY,
            35.0 * Math.PI / 180.0);
        var clip = new AnimationClip(
            "travelling-mixamo",
            new FrameRate(1, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, sourceBind),
                        new TransformKeyframe(
                            1,
                            sourceBind with
                            {
                                Translation = new Vector3D(3.0, 1.0, 2.0),
                                Rotation = heading,
                            }),
                    ]),
            ]);
        var map = new RetargetMap(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    1,
                    BoneMappingMethod.Manual,
                    1.0,
                    isReviewed: true,
                    transferPolicy: RetargetTransferPolicy.CopyLocal,
                    componentPolicy: RetargetComponentPolicy.Rotation),
            ],
            reviewedTargetBindBoneIndices: [0]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            source,
            target,
            map,
            AnimationRootMode.Bip01,
            targetRootBoneName: "Bip01");

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                source,
                target,
                clip,
                1.0,
                PreviewProfile.RawAuthoring,
                map,
                purpose: EvaluationPurpose.Export,
                dl1AuthoringPolicy: policy));

        Assert.Equal(0, policy.RootMotion.SourceMotionBoneIndex);
        Assert.Equal(1, policy.RootMotion.TargetPoseMotionBoneIndex);
        Assert.False(policy.RootMotion.TargetPoseOwnsTranslation);
        AssertVectorNear(
            new Vector3D(3.0, 0.0, 2.0),
            frame.AuthoredPose.LocalTransforms[0].Translation);
        AssertRotationNear(
            heading,
            frame.AuthoredPose.LocalTransforms[0].Rotation);
        AssertVectorNear(
            Vector3D.UnitY,
            frame.AuthoredPose.LocalTransforms[1].Translation);
        AssertVectorNear(
            new Vector3D(3.0, 1.0, 2.0),
            frame.AuthoredPose.GlobalMatrices[1].Translation);
    }

    [Fact]
    public void BatchAuthoredPoseSamplingMatchesIndividualEvaluation()
    {
        RootPolicyFixture fixture = CreateRootPolicyFixture();
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            fixture.Rig,
            fixture.Rig,
            null,
            AnimationRootMode.MotionAccumulator);
        double[] sampleTimes = [0.0, 0.25, 0.5, 0.75, 1.0];
        var batchRequest = new EvaluationRequest(
            fixture.Rig,
            fixture.Rig,
            fixture.Clip,
            0.0,
            PreviewProfile.RawAuthoring,
            purpose: EvaluationPurpose.Export,
            dl1AuthoringPolicy: policy);
        var batched = new SkeletonPose[sampleTimes.Length];

        AnimationEvaluator.EvaluateAuthoredPoseBatch(
            batchRequest,
            sampleTimes,
            (index, pose) => batched[index] = pose);

        var evaluator = new AnimationEvaluator();
        for (int index = 0; index < sampleTimes.Length; index++)
        {
            EvaluationFrame individual = evaluator.Evaluate(
                new EvaluationRequest(
                    fixture.Rig,
                    fixture.Rig,
                    fixture.Clip,
                    sampleTimes[index],
                    PreviewProfile.RawAuthoring,
                    purpose: EvaluationPurpose.Export,
                    dl1AuthoringPolicy: policy));
            for (int boneIndex = 0;
                 boneIndex < fixture.Rig.BoneCount;
                 boneIndex++)
            {
                TransformTRS expected =
                    individual.AuthoredPose.LocalTransforms[boneIndex];
                TransformTRS actual =
                    batched[index].LocalTransforms[boneIndex];
                AssertVectorNear(
                    expected.Translation,
                    actual.Translation);
                AssertRotationNear(
                    expected.Rotation,
                    actual.Rotation);
                AssertVectorNear(
                    expected.Scale,
                    actual.Scale);
            }
        }
    }

    // Python oracles:
    // tests/test_bone_map_v2_row_policies.py::
    // test_base_rows_honor_transfer_and_component_policies_and_bind_unmapped
    // tests/test_helper_retarget.py::
    // test_apply_is_deterministic_and_leaves_unmapped_helper_unchanged
    [Fact]
    public void MapsRequiredHelperAndPreservesUnmappedRequiredTracksAtTargetBind()
    {
        HelperPolicyFixture fixture = CreateHelperPolicyFixture();
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            fixture.SourceRig,
            fixture.TargetRig,
            fixture.Mapping,
            AnimationRootMode.InPlace);
        var request = new EvaluationRequest(
            fixture.SourceRig,
            fixture.TargetRig,
            fixture.Clip,
            1.0,
            PreviewProfile.RawAuthoring,
            fixture.Mapping,
            purpose: EvaluationPurpose.Export,
            dl1AuthoringPolicy: policy);
        var evaluator = new AnimationEvaluator();

        EvaluationFrame frame = evaluator.Evaluate(request);

        Assert.True(frame.Compatibility!.CanEvaluate);
        Assert.Contains(
            frame.Compatibility.Diagnostics,
            diagnostic =>
                diagnostic.Code == "reviewed_required_target_bind" &&
                diagnostic.TargetBoneName == "unmapped_helper");
        Assert.Equal(
            Dl1TargetTrackSource.Evaluated,
            policy.TargetTracks[1].Source);
        Assert.True(policy.TargetTracks[1].IsHelper);
        Assert.Equal(
            Dl1TargetTrackSource.TargetBind,
            policy.TargetTracks[2].Source);
        Assert.True(policy.TargetTracks[2].IsHelper);
        AssertVectorNear(
            new Vector3D(0.35, 1.2, 0.3),
            frame.AuthoredPose.LocalTransforms[1].Translation);
        Assert.Equal(
            fixture.TargetRig.Bones[2].LocalBindPose,
            frame.AuthoredPose.LocalTransforms[2]);
        Assert.Equal(
            fixture.TargetRig.Bones[3].LocalBindPose,
            frame.AuthoredPose.LocalTransforms[3]);

        Dl1Anm2AuthoringSequence sequence =
            new Anm2EvaluationAdapter(evaluator).SampleAuthoredFrames(request);
        Dl1Anm2AuthoringFrame exported = sequence.Frames[1];
        Assert.Equal(
            fixture.TargetRig.Bones[2].LocalBindPose,
            exported.Tracks.Single(
                track => track.BoneIndex == 2).LocalTransform);
        Assert.Equal(
            fixture.TargetRig.Bones[3].LocalBindPose,
            exported.Tracks.Single(
                track => track.BoneIndex == 3).LocalTransform);
        Assert.Equal(
            TransformTRS.Identity,
            exported.Tracks.Single(
                track => track.BoneIndex == 4).LocalTransform);
    }

    // Pipeline-order oracle:
    // dlanm2_gui/helper_retarget.py documents helper/root policy after the body
    // solver and before packed ANM2 output.
    [Fact]
    public void RunsAfterAuthoredIkAndBeforePreviewOnlyProceduralOutput()
    {
        RigDefinition rig = CreateIkRig();
        var clip = new AnimationClip(
            "ik",
            new FrameRate(30, 1),
            1);
        var constraint = new TwoBoneIkConstraint(
            0,
            1,
            2,
            new Vector3D(1.0, 1.0, 0.0),
            Vector3D.UnitZ);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            null,
            AnimationRootMode.InPlace);
        var previewStage = new ConstantBoneOffsetPreviewStage(
            "preview-root-offset",
            0,
            new TransformTRS(
                new Vector3D(100.0, 0.0, 0.0),
                QuaternionD.Identity,
                Vector3D.One),
            AuthoringPreviewFidelity.Bones);
        var evaluator = new AnimationEvaluator([previewStage]);
        var request = new EvaluationRequest(
            rig,
            rig,
            clip,
            0.0,
            PreviewProfile.ThirdPersonAuthoring,
            ikConstraints: [constraint],
            purpose: EvaluationPurpose.Preview,
            dl1AuthoringPolicy: policy);

        EvaluationFrame frame = evaluator.Evaluate(request);

        AssertVectorNear(
            new Vector3D(1.0, 1.0, 0.0),
            frame.AuthoredPose.GlobalMatrices[2].Translation,
            1e-7);
        AssertVectorNear(
            new Vector3D(101.0, 1.0, 0.0),
            frame.DisplayPose.GlobalMatrices[2].Translation,
            1e-7);

        Dl1Anm2AuthoringSequence sequence =
            new Anm2EvaluationAdapter(evaluator).SampleAuthoredFrames(request);
        TransformTRS[] exportedLocals = sequence.Frames[0].Tracks
            .OrderBy(static track => track.BoneIndex)
            .Select(static track => track.LocalTransform)
            .ToArray();
        var exportedPose = new SkeletonPose(rig, exportedLocals);
        AssertVectorNear(
            new Vector3D(1.0, 1.0, 0.0),
            exportedPose.GlobalMatrices[2].Translation,
            1e-7);
    }

    [Fact]
    public void GlobalBindBasisHelperResolvesAfterAuthoredParentEdit()
    {
        TransformTRS sourceRootBind = new(
            new Vector3D(0.4, 0.1, -0.2),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitY,
                15.0 * Math.PI / 180.0),
            Vector3D.One);
        TransformTRS sourceHeadBind = new(
            new Vector3D(0.0, 1.0, 0.0),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitX,
                10.0 * Math.PI / 180.0),
            Vector3D.One);
        TransformTRS sourceHeadAnimated = new(
            new Vector3D(0.2, 1.1, -0.3),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitZ,
                35.0 * Math.PI / 180.0),
            Vector3D.One);
        var source = new RigDefinition(
            "post-edit-helper-source",
            "Post-edit helper source",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    sourceRootBind,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "Head",
                    0,
                    sourceHeadBind),
            ]);

        TransformTRS targetRootBind = new(
            new Vector3D(-0.8, 0.2, 0.5),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitY,
                -20.0 * Math.PI / 180.0),
            Vector3D.One);
        TransformTRS targetHeadBind = new(
            new Vector3D(0.0, 1.6, 0.1),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitX,
                -5.0 * Math.PI / 180.0),
            Vector3D.One);
        TransformTRS eyeCameraBind = new(
            new Vector3D(0.1, 0.25, 0.35),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitY,
                8.0 * Math.PI / 180.0),
            Vector3D.One);
        var target = new RigDefinition(
            "post-edit-helper-target",
            "Post-edit helper target",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    targetRootBind,
                    BoneKind.Root,
                    descriptorHash: 0xA1000001),
                new BoneDefinition(
                    1,
                    "Head",
                    0,
                    targetHeadBind,
                    descriptorHash: 0xA1000002),
                new BoneDefinition(
                    2,
                    "EyeCamera",
                    1,
                    eyeCameraBind,
                    BoneKind.Camera,
                    requiredForExport: false,
                    descriptorHash: 0xA1000003),
            ]);
        var mapping = new RetargetMap(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Manual,
                    1.0,
                    isReviewed: true),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.Manual,
                    1.0,
                    isReviewed: true),
                new BoneMapEntry(
                    1,
                    2,
                    BoneMappingMethod.Manual,
                    1.0,
                    isReviewed: true,
                    mappingKind:
                        RetargetMappingKind.HelperOverride,
                    transferPolicy:
                        RetargetTransferPolicy.GlobalBindBasis,
                    componentPolicy:
                        RetargetComponentPolicy.FullTransform),
            ]);
        var clip = new AnimationClip(
            "post-edit-helper",
            new FrameRate(30, 1),
            1,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            sourceRootBind with
                            {
                                Translation =
                                    sourceRootBind.Translation +
                                    new Vector3D(2.0, 0.5, -1.0),
                                Rotation =
                                    QuaternionD.FromAxisAngle(
                                        Vector3D.UnitY,
                                        55.0 * Math.PI / 180.0),
                            }),
                    ]),
                new TransformTrack(
                    1,
                    [new TransformKeyframe(0, sourceHeadAnimated)]),
            ]);
        TransformTRS editedHead = targetHeadBind with
        {
            Rotation = QuaternionD.FromAxisAngle(
                Vector3D.UnitY,
                70.0 * Math.PI / 180.0),
        };
        var editLayer = new BoneEditLayer(
            Guid.NewGuid(),
            "Authored head correction",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    1,
                    [new TransformKeyframe(0, editedHead)]),
            ]);
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            source,
            target,
            mapping,
            AnimationRootMode.InPlace);
        var request = new EvaluationRequest(
            source,
            target,
            clip,
            0.0,
            PreviewProfile.RawAuthoring,
            mapping,
            editLayers: [editLayer],
            purpose: EvaluationPurpose.Export,
            dl1AuthoringPolicy: policy);

        var evaluator = new AnimationEvaluator();
        EvaluationFrame frame = evaluator.Evaluate(request);

        SkeletonPose sourcePose =
            clip.SamplePose(source, 0.0, PlaybackMode.Clamp);
        TransformMatrix expectedEyeCameraGlobal =
            sourcePose.GlobalMatrices[1] *
            source.CreateBindPose()
                .GlobalMatrices[1]
                .InvertedAffine() *
            target.CreateBindPose().GlobalMatrices[2];
        AssertRotationNear(
            editedHead.Rotation,
            frame.AuthoredPose.LocalTransforms[1].Rotation);
        AssertMatrixNear(
            expectedEyeCameraGlobal,
            frame.AuthoredPose.GlobalMatrices[2],
            1e-8);

        SkeletonPose? batched = null;
        AnimationEvaluator.EvaluateAuthoredPoseBatch(
            request,
            [0.0],
            (_, pose) => batched = pose);
        SkeletonPose batchPose =
            Assert.IsType<SkeletonPose>(batched);
        AssertMatrixNear(
            expectedEyeCameraGlobal,
            batchPose.GlobalMatrices[2],
            1e-8);

        Dl1Anm2AuthoringFrame exported =
            Assert.Single(
                new Anm2EvaluationAdapter(evaluator)
                    .SampleAuthoredFrames(request)
                    .Frames);
        TransformTRS[] exportedLocals = exported.Tracks
            .OrderBy(static track => track.BoneIndex)
            .Select(static track => track.LocalTransform)
            .ToArray();
        var exportedPose = new SkeletonPose(
            target,
            exportedLocals);
        AssertMatrixNear(
            expectedEyeCameraGlobal,
            exportedPose.GlobalMatrices[2],
            1e-8);
    }

    [Fact]
    public void MotionAccumulatorIsAuxiliaryWhileRequiredHelpersFailClosed()
    {
        var noAccumulator = new RigDefinition(
            "no-accumulator",
            "No accumulator",
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
        Dl1AuthoringPolicy noAccumulatorPolicy =
            Dl1AuthoringPolicy.Create(
                noAccumulator,
                noAccumulator,
                null,
                AnimationRootMode.MotionAccumulator);
        Assert.Null(
            noAccumulatorPolicy.RootMotion.MotionAccumulatorBoneIndex);

        var missingHelperDescriptor = new RigDefinition(
            "missing-helper",
            "Missing helper descriptor",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x11111111,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "required_helper",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Helper),
            ]);
        InvalidOperationException missingDescriptor =
            Assert.Throws<InvalidOperationException>(
                () => Dl1AuthoringPolicy.Create(
                    missingHelperDescriptor,
                    missingHelperDescriptor,
                    null,
                    AnimationRootMode.InPlace));
        Assert.Contains("no authoritative descriptor", missingDescriptor.Message);
    }

    private static RootPolicyFixture CreateRootPolicyFixture()
    {
        QuaternionD tilt = QuaternionD.FromAxisAngle(
            Vector3D.UnitX,
            20.0 * Math.PI / 180.0);
        QuaternionD heading = QuaternionD.FromAxisAngle(
            Vector3D.UnitY,
            90.0 * Math.PI / 180.0);
        TransformTRS rootBind = new(
            new Vector3D(0.0, 1.0, 0.0),
            tilt,
            Vector3D.One);
        TransformTRS accumulatorBind = new(
            new Vector3D(7.0, 8.0, 9.0),
            QuaternionD.FromAxisAngle(
                Vector3D.UnitZ,
                15.0 * Math.PI / 180.0),
            new Vector3D(2.0, 2.0, 2.0));
        var rig = new RigDefinition(
            "dl1-root-policy",
            "DL1 root policy",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    rootBind,
                    BoneKind.Root,
                    descriptorHash: 0x11111111,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "DLR_OffsetHelper_CCC3CDDF",
                    -1,
                    accumulatorBind,
                    BoneKind.Helper,
                    descriptorHash:
                        Dl1RootMotionPolicy.MotionAccumulatorDescriptor),
            ]);
        var clip = new AnimationClip(
            "root-motion",
            new FrameRate(1, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, rootBind),
                        new TransformKeyframe(
                            1,
                            new TransformTRS(
                                new Vector3D(2.0, 2.0, -3.0),
                                (heading * tilt).Normalized(),
                                Vector3D.One)),
                    ]),
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(0, accumulatorBind),
                        new TransformKeyframe(
                            1,
                            new TransformTRS(
                                new Vector3D(99.0, 98.0, 97.0),
                                heading,
                                new Vector3D(3.0, 3.0, 3.0))),
                    ]),
            ]);
        return new(rig, clip, tilt, heading);
    }

    private static HelperPolicyFixture CreateHelperPolicyFixture()
    {
        var source = new RigDefinition(
            "source-helper-policy",
            "Source helper policy",
            [
                new BoneDefinition(
                    0,
                    "source_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x01010101),
                new BoneDefinition(
                    1,
                    "source_helper",
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.0, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Helper,
                    descriptorHash: 0x02020202),
            ]);
        var target = new RigDefinition(
            "target-helper-policy",
            "Target helper policy",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x10101010,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "mapped_helper",
                    0,
                    new TransformTRS(
                        new Vector3D(0.1, 1.2, 0.3),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Helper,
                    descriptorHash: 0x20202020),
                new BoneDefinition(
                    2,
                    "unmapped_helper",
                    0,
                    new TransformTRS(
                        new Vector3D(0.4, 0.5, 0.6),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Helper,
                    descriptorHash: 0x30303030),
                new BoneDefinition(
                    3,
                    "target_only_deform",
                    0,
                    new TransformTRS(
                        new Vector3D(0.7, 0.8, 0.9),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: 0x40404040),
                new BoneDefinition(
                    4,
                    "DLR_OffsetHelper_CCC3CDDF",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    descriptorHash:
                        Dl1RootMotionPolicy.MotionAccumulatorDescriptor),
            ]);
        var mapping = new RetargetMap(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Manual,
                    1.0),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.Manual,
                    1.0),
            ]);
        var clip = new AnimationClip(
            "helper-motion",
            new FrameRate(1, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(1, TransformTRS.Identity),
                    ]),
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            source.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            new TransformTRS(
                                new Vector3D(0.25, 1.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
        return new(source, target, mapping, clip);
    }

    private static RigDefinition CreateIkRig() =>
        new(
            "dl1-ik-policy",
            "DL1 IK policy",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x11111111,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "elbow",
                    0,
                    new TransformTRS(
                        Vector3D.UnitX,
                        QuaternionD.Identity,
                        Vector3D.One),
                    descriptorHash: 0x22222222),
                new BoneDefinition(
                    2,
                    "hand",
                    1,
                    new TransformTRS(
                        Vector3D.UnitX,
                        QuaternionD.Identity,
                        Vector3D.One),
                    descriptorHash: 0x33333333),
                new BoneDefinition(
                    3,
                    "DLR_OffsetHelper_CCC3CDDF",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    descriptorHash:
                        Dl1RootMotionPolicy.MotionAccumulatorDescriptor),
            ]);

    private static void AssertRotationNear(
        QuaternionD expected,
        QuaternionD actual,
        double tolerance = 1e-9)
    {
        double agreement = Math.Clamp(
            Math.Abs(
                QuaternionD.Dot(
                    expected.Normalized(),
                    actual.Normalized())),
            0.0,
            1.0);
        Assert.InRange(1.0 - agreement, 0.0, tolerance);
    }

    private static QuaternionD ExtractTwist(
        QuaternionD rotation,
        Vector3D axis)
    {
        QuaternionD unit = rotation.Normalized();
        Vector3D vector = new(unit.X, unit.Y, unit.Z);
        Vector3D projected =
            axis * Vector3D.Dot(vector, axis);
        var twist = new QuaternionD(
            projected.X,
            projected.Y,
            projected.Z,
            unit.W);
        return twist.LengthSquared <= 1e-20
            ? QuaternionD.Identity
            : twist.Normalized();
    }

    private static void AssertVectorNear(
        Vector3D expected,
        Vector3D actual,
        double tolerance = 1e-9)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0.0, tolerance);
    }

    private static void AssertMatrixNear(
        TransformMatrix expected,
        TransformMatrix actual,
        double tolerance)
    {
        double[] expectedValues =
        [
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44,
        ];
        double[] actualValues =
        [
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44,
        ];
        for (int index = 0; index < expectedValues.Length; index++)
        {
            Assert.InRange(
                Math.Abs(expectedValues[index] - actualValues[index]),
                0.0,
                tolerance);
        }
    }

    private sealed record RootPolicyFixture(
        RigDefinition Rig,
        AnimationClip Clip,
        QuaternionD Tilt,
        QuaternionD Heading);

    private sealed record HelperPolicyFixture(
        RigDefinition SourceRig,
        RigDefinition TargetRig,
        RetargetMap Mapping,
        AnimationClip Clip);
}
