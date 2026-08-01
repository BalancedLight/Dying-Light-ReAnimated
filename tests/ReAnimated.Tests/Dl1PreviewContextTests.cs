using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class Dl1PreviewContextTests
{
    [Fact]
    public void FppUsesEyeCameraAndSeparateCapturedHandsProjection()
    {
        RigDefinition rig = CreatePlayerRig();
        AnimationClip clip = CreateRootAnimation();
        CameraLens sceneLens = new(68.0, 21.0 / 9.0, 0.03, 800.0);
        var handsProjection = new Dl1ProjectionParameters(
            52.0,
            Dl1ProjectionFovAxis.Horizontal,
            21.0 / 9.0,
            0.005,
            Dl1ProjectionFarPlane.Infinite);
        var inputs = new Dl1PreviewInputs(
            new Dl1FppProjectionSnapshot(sceneLens, handsProjection));

        // DyingLightDebug/libgamedll.dylib.NAMED.c:149511-149523 and
        // 6899768-6899859 identify EyeCamera/RefCamera in PlayerFppVis.
        // Lines 6926255-6926275 build a distinct infinite-far hands frustum.
        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                dl1PreviewInputs: inputs));

        EvaluatedCamera camera = Assert.IsType<EvaluatedCamera>(frame.Camera);
        Assert.Equal(EvaluatedCameraSource.Dl1FppEyeCamera, camera.Source);
        Assert.True(camera.IsFirstPerson);
        Assert.Equal(2.0, camera.WorldTransform.Translation.X, 10);
        Assert.Equal(1.6, camera.WorldTransform.Translation.Y, 10);
        Assert.Equal(0.2, camera.WorldTransform.Translation.Z, 10);
        Assert.Equal(sceneLens, camera.Lens);
        Assert.Equal(handsProjection, camera.HandsProjection);

        Assert.Equal(2, frame.CameraHelpers.Length);
        Assert.Equal(
            1.6,
            frame.CameraHelpers.Single(
                helper =>
                    helper.Role == Dl1PreviewContract.EyeCameraHelperRole)
                .WorldTransform.Translation.Y,
            10);
        Assert.Equal(
            1.7,
            frame.CameraHelpers.Single(
                helper =>
                    helper.Role == Dl1PreviewContract.ReferenceCameraHelperRole)
                .WorldTransform.Translation.Y,
            10);

        AssertStage(
            frame,
            Dl1PreviewStageIds.FppCameraHelpers,
            Dl1PreviewStageStatus.Applied);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppViewTransform,
            Dl1PreviewStageStatus.Fallback);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppSceneProjection,
            Dl1PreviewStageStatus.Applied);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandsProjection,
            Dl1PreviewStageStatus.Applied);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Unavailable);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Disabled);
        Assert.Contains(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code == "dl1_fpp_view_transform_fallback");
        Assert.True(
            frame.AuthoredPose.LocalTransforms.SequenceEqual(
            frame.DisplayPose.LocalTransforms));
    }

    [Fact]
    public void FppCorrectsHSpineBasisBeforeEyeCameraAndPreservesWorldScaleAndPosition()
    {
        RigDefinition rig = CreateBodyCorrectionRig();
        SkeletonPose original = rig.CreateBindPose();
        int hSpineIndex = rig.GetBoneIndex(
            Dl1PreviewContract.HSpineBoneName);
        int hSpine1Index = rig.GetBoneIndex(
            Dl1PreviewContract.HSpine1BoneName);
        int eyeCameraIndex = rig.GetBoneIndex(
            Dl1PreviewContract.EyeCameraBoneName);
        TransformMatrix expectedHSpine1BeforeCorrection =
            CreateCorrectedBasisWorld(
                original.GlobalMatrices[hSpineIndex],
                CreateBodyCorrectionSnapshot()) *
            original.LocalTransforms[hSpine1Index].ToMatrix();
        var inputs = new Dl1PreviewInputs(
            fppBodyCorrection: CreateBodyCorrectionSnapshot());

        // Windows 1.55 PlayerFppVis::ApplyAnimation at 0x180B959E0
        // invokes CorrectHSpine and CorrectHSpine1 before camera evaluation.
        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                new AnimationClip(
                    "fpp-body-correction",
                    new FrameRate(30, 1),
                    1),
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                dl1PreviewInputs: inputs));

        TransformMatrix correctedHSpine =
            frame.DisplayPose.GlobalMatrices[hSpineIndex];
        TransformMatrix correctedHSpine1 =
            frame.DisplayPose.GlobalMatrices[hSpine1Index];
        AssertVectorNearlyEqual(
            original.GlobalMatrices[hSpineIndex].Translation,
            correctedHSpine.Translation);
        AssertVectorNearlyEqual(
            GetColumnScale(original.GlobalMatrices[hSpineIndex]),
            GetColumnScale(correctedHSpine));
        AssertVectorNearlyEqual(
            expectedHSpine1BeforeCorrection.Translation,
            correctedHSpine1.Translation);
        AssertVectorNearlyEqual(
            GetColumnScale(expectedHSpine1BeforeCorrection),
            GetColumnScale(correctedHSpine1));

        TransformMatrix expectedEyeCamera =
            correctedHSpine1 *
            original.LocalTransforms[eyeCameraIndex].ToMatrix();
        EvaluatedCamera camera = Assert.IsType<EvaluatedCamera>(frame.Camera);
        Assert.True(
            expectedEyeCamera.NearlyEquals(camera.WorldTransform, 1e-8));
        Assert.True(
            expectedEyeCamera.NearlyEquals(
                frame.DisplayPose.GlobalMatrices[eyeCameraIndex],
                1e-8));
        Assert.False(
            original.GlobalMatrices[eyeCameraIndex].NearlyEquals(
                camera.WorldTransform,
                1e-8));
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Applied);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Disabled);
        Assert.True(
            original.LocalTransforms.SequenceEqual(
                frame.AuthoredPose.LocalTransforms));
        Assert.False(
            frame.AuthoredPose.LocalTransforms.SequenceEqual(
                frame.DisplayPose.LocalTransforms));
    }

    [Fact]
    public void FppConcreteHeadStagesReportIndependently()
    {
        RigDefinition rig = CreateBodyCorrectionRig();
        AnimationClip clip = new(
            "independent-fpp-stages",
            new FrameRate(30, 1),
            1);
        var inputs = new Dl1PreviewInputs(
            fppBodyCorrection: CreateBodyCorrectionSnapshot());
        var evaluator = new AnimationEvaluator();

        EvaluationFrame basisOnly = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                CreateFirstPersonProfile(
                    Dl1PreviewStageIds
                        .FppHSpineBasisCorrection),
                dl1PreviewInputs: inputs));
        AssertStage(
            basisOnly,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Applied);
        AssertStage(
            basisOnly,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            basisOnly,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Disabled);
        Assert.False(
            basisOnly.AuthoredPose.LocalTransforms.SequenceEqual(
                basisOnly.DisplayPose.LocalTransforms));

        EvaluationFrame headPositionOnly = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                CreateFirstPersonProfile(
                    Dl1PreviewStageIds
                        .FppHeadPositionCorrection),
                dl1PreviewInputs: inputs));
        AssertStage(
            headPositionOnly,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            headPositionOnly,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Unavailable);
        AssertStage(
            headPositionOnly,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Disabled);
        Assert.True(
            headPositionOnly.AuthoredPose.LocalTransforms.SequenceEqual(
                headPositionOnly.DisplayPose.LocalTransforms));

        EvaluationFrame handInertiaOnly = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                CreateFirstPersonProfile(
                    Dl1PreviewStageIds.FppHandInertia),
                dl1PreviewInputs: inputs));
        AssertStage(
            handInertiaOnly,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            handInertiaOnly,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            handInertiaOnly,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Unavailable);
        Assert.True(
            handInertiaOnly.AuthoredPose.LocalTransforms.SequenceEqual(
                handInertiaOnly.DisplayPose.LocalTransforms));
    }

    [Fact]
    public void FppHSpineCorrectionFailsClosedWhenRequiredElementIsMissing()
    {
        RigDefinition rig = CreateBodyCorrectionRig(
            includeHSpine1: false);
        var inputs = new Dl1PreviewInputs(
            fppBodyCorrection: CreateBodyCorrectionSnapshot());

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                new AnimationClip("missing-hspine1", new FrameRate(30, 1), 1),
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                dl1PreviewInputs: inputs));

        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Unavailable);
        Assert.Contains(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                "dl1_fpp_hspine_basis_bone_missing");
        Assert.True(
            frame.AuthoredPose.LocalTransforms.SequenceEqual(
                frame.DisplayPose.LocalTransforms));
    }

    [Fact]
    public void FppHSpineCorrectionFailsClosedWhenElementNameIsAmbiguous()
    {
        RigDefinition rig = CreateAmbiguousHSpineRig();
        var inputs = new Dl1PreviewInputs(
            fppBodyCorrection: CreateBodyCorrectionSnapshot());

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                new AnimationClip(
                    "ambiguous-hspine",
                    new FrameRate(30, 1),
                    1),
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                dl1PreviewInputs: inputs));

        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Unavailable);
        Assert.Contains(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                "dl1_fpp_hspine_basis_name_ambiguous");
        Assert.True(
            frame.AuthoredPose.LocalTransforms.SequenceEqual(
                frame.DisplayPose.LocalTransforms));
    }

    [Fact]
    public void FppHSpineCorrectionBypassesVehicleState()
    {
        RigDefinition rig = CreateBodyCorrectionRig();
        var inputs = new Dl1PreviewInputs(
            fppBodyCorrection:
                CreateBodyCorrectionSnapshot(
                    vehicleControllerActive: true));

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                new AnimationClip("vehicle-fpp", new FrameRate(30, 1), 1),
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                dl1PreviewInputs: inputs));

        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Bypassed);
        Assert.True(
            frame.AuthoredPose.LocalTransforms.SequenceEqual(
                frame.DisplayPose.LocalTransforms));
        Assert.DoesNotContain(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code.StartsWith(
                    "dl1_fpp_hspine_basis_",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void FppProfileCanExplicitlyDisableEveryOptionalProcedure()
    {
        PreviewProfile baseline = PreviewProfile.FirstPersonAuthoring;
        var profile = new PreviewProfile(
            baseline.Id,
            baseline.ViewMode,
            baseline.Fidelity,
            baseline.VisualStyle,
            baseline.CameraBoneName,
            baseline.CameraLens,
            baseline.CameraOffset,
            baseline.FidelityTier,
            baseline.Context,
            baseline.ProfileVersion,
            baseline.BuildFingerprint,
            [Dl1PreviewStageIds.NoProceduralStages],
            baseline.MorphActivationThreshold,
            baseline.MaximumActiveMorphTargets,
            baseline.ClampMorphWeightsToRigBounds);

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                CreatePlayerRig(),
                CreatePlayerRig(),
                CreateRootAnimation(),
                0.0,
                profile));

        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandsProjection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Disabled);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Disabled);
    }

    [Fact]
    public void FppLabelsMissingRuntimeStateWithoutGuessingProceduralPose()
    {
        RigDefinition rig = CreatePlayerRig(includeReferenceCamera: false);
        AnimationClip clip = CreateRootAnimation();
        var profile = new PreviewProfile(
            "fpp-explicit-unavailable",
            PreviewViewMode.FirstPerson,
            AuthoringPreviewFidelity.AuthoringAccurate |
                AuthoringPreviewFidelity.FirstPersonOcclusion,
            PreviewVisualStyle.MaterialApproximation,
            Dl1PreviewContract.EyeCameraBoneName,
            new CameraLens(61.0, 16.0 / 9.0, 0.02, 600.0),
            TransformTRS.Identity,
            PreviewFidelityTier.Dl1Profile,
            Dl1PreviewContext.Dl1Fpp,
            proceduralToggles:
            [
                Dl1PreviewStageIds.FppHandsProjection,
            ]);

        // PlayerFppVis::GetDefaultFOV/GetCameraClipNear at
        // libgamedll.dylib.NAMED.c:6903858-6904037 depend on runtime state.
        // UpdateHandInertia and its gates span 6905905-6907048; no static
        // offline formula is treated as game-equivalent.
        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(rig, rig, clip, 0.0, profile));

        EvaluatedCamera camera = Assert.IsType<EvaluatedCamera>(frame.Camera);
        Assert.Equal(profile.CameraLens, camera.Lens);
        Assert.Null(camera.HandsProjection);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppCameraHelpers,
            Dl1PreviewStageStatus.Fallback);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppViewTransform,
            Dl1PreviewStageStatus.Fallback);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppSceneProjection,
            Dl1PreviewStageStatus.Fallback);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandsProjection,
            Dl1PreviewStageStatus.Unavailable);
        Dl1PreviewStageReport headCorrection = AssertStage(
            frame,
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            Dl1PreviewStageStatus.Disabled);
        Dl1PreviewStageReport headPosition = AssertStage(
            frame,
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            Dl1PreviewStageStatus.Disabled);
        Dl1PreviewStageReport handInertia = AssertStage(
            frame,
            Dl1PreviewStageIds.FppHandInertia,
            Dl1PreviewStageStatus.Disabled);
        Assert.False(headCorrection.Requested);
        Assert.False(headPosition.Requested);
        Assert.False(handInertia.Requested);
        Assert.Contains(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code == "dl1_fpp_scene_projection_fallback");
        Assert.Contains(
            frame.Diagnostics,
            diagnostic =>
                diagnostic.Code == "dl1_fpp_hands_projection_unavailable");
        Assert.True(
            frame.AuthoredPose.LocalTransforms.SequenceEqual(
                frame.DisplayPose.LocalTransforms));
    }

    [Fact]
    public void MovieUsesExternalReferenceCameraAndNeverRigRefCamera()
    {
        RigDefinition rig = CreateMovieRigWithRefCamera();
        AnimationClip clip = new("movie", new FrameRate(30, 1), 1);
        TransformMatrix externalTransform =
            TransformMatrix.CreateTranslation(new Vector3D(7.0, 8.0, 9.0));
        CameraLens externalLens = new(44.0, 2.0, 0.1, 2000.0);
        var inputs = new Dl1PreviewInputs(
            movieReferenceCamera:
                new Dl1MovieReferenceCameraSnapshot(
                    externalTransform,
                    externalLens));

        // DyingLightDebug/libengine.dylib.NAMED.c:1302334-1302343 stores
        // CMovieManager's reference camera as an external IBaseCamera. It is
        // not the player-rig helper also named RefCamera.
        var evaluator = new AnimationEvaluator();
        EvaluationFrame captured = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.MovieAuthoring,
                dl1PreviewInputs: inputs));

        EvaluatedCamera camera =
            Assert.IsType<EvaluatedCamera>(captured.Camera);
        Assert.Equal(
            EvaluatedCameraSource.Dl1MovieReferenceCamera,
            camera.Source);
        Assert.Equal(new Vector3D(7.0, 8.0, 9.0), camera.WorldTransform.Translation);
        Assert.Equal(externalLens, camera.Lens);
        Assert.Empty(captured.CameraHelpers);
        AssertStage(
            captured,
            Dl1PreviewStageIds.MovieReferenceCamera,
            Dl1PreviewStageStatus.Applied);

        EvaluationFrame missing = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.MovieAuthoring));
        Assert.Null(missing.Camera);
        AssertStage(
            missing,
            Dl1PreviewStageIds.MovieReferenceCamera,
            Dl1PreviewStageStatus.Unavailable);
        Assert.Contains(
            missing.Diagnostics,
            diagnostic =>
                diagnostic.Code == "dl1_movie_reference_camera_unavailable");
    }

    [Fact]
    public void FppNeverSubstitutesRefCameraForMissingEyeCamera()
    {
        RigDefinition rig = CreateMovieRigWithRefCamera();
        AnimationClip clip = new("fpp", new FrameRate(30, 1), 1);
        var misleadingProfile = new PreviewProfile(
            "fpp-refcamera-is-not-eye",
            PreviewViewMode.FirstPerson,
            AuthoringPreviewFidelity.AuthoringAccurate,
            PreviewVisualStyle.MaterialApproximation,
            Dl1PreviewContract.ReferenceCameraBoneName,
            CameraLens.Default,
            TransformTRS.Identity,
            PreviewFidelityTier.Dl1Profile,
            Dl1PreviewContext.Dl1Fpp);

        EvaluationFrame frame = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                misleadingProfile));

        Assert.Null(frame.Camera);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppCameraHelpers,
            Dl1PreviewStageStatus.Unavailable);
        AssertStage(
            frame,
            Dl1PreviewStageIds.FppViewTransform,
            Dl1PreviewStageStatus.Unavailable);
        Assert.Contains(
            frame.Diagnostics,
            diagnostic => diagnostic.Code == "dl1_fpp_eye_camera_missing");
    }

    [Fact]
    public void FppCameraFollowsPreviewOnlyEyeBoneTweaksWithoutExportingThem()
    {
        RigDefinition rig = CreatePlayerRig();
        AnimationClip clip = CreateRootAnimation();
        var eyeTweak = new BoneEditLayer(
            Guid.NewGuid(),
            "FPP eye tweak",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.PreviewOnly,
            1.0,
            [
                new BoneEditTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                new Vector3D(0.0, 0.2, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
        var evaluator = new AnimationEvaluator();

        EvaluationFrame preview = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                editLayers: [eyeTweak],
                purpose: EvaluationPurpose.Preview));
        EvaluationFrame export = evaluator.Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                clip,
                0.0,
                PreviewProfile.FirstPersonAuthoring,
                editLayers: [eyeTweak],
                purpose: EvaluationPurpose.Export));

        EvaluatedCamera camera =
            Assert.IsType<EvaluatedCamera>(preview.Camera);
        Assert.Equal(1.8, camera.WorldTransform.Translation.Y, 10);
        Assert.Equal(
            1.6,
            preview.AuthoredPose.LocalTransforms[1].Translation.Y,
            10);
        Assert.Equal(
            1.8,
            preview.DisplayPose.LocalTransforms[1].Translation.Y,
            10);
        Assert.Equal(
            1.6,
            export.DisplayPose.LocalTransforms[1].Translation.Y,
            10);
        Assert.Null(export.Camera);
    }

    [Fact]
    public void ExportAndAnm2SamplingExcludeAllDl1PreviewContext()
    {
        RigDefinition rig = CreatePlayerRig();
        AnimationClip clip = CreateRootAnimation();
        var inputs = new Dl1PreviewInputs(
            fppProjection: new Dl1FppProjectionSnapshot(
                new CameraLens(70.0, 16.0 / 9.0, 0.02, 1000.0),
                new Dl1ProjectionParameters(
                    55.0,
                    Dl1ProjectionFovAxis.Horizontal,
                    16.0 / 9.0,
                    0.004,
                    Dl1ProjectionFarPlane.Infinite)),
            fppBodyCorrection: CreateBodyCorrectionSnapshot());
        var evaluator = new AnimationEvaluator();
        var exportRequest = new EvaluationRequest(
            rig,
            rig,
            clip,
            clip.FrameRate.SecondsForFrame(1),
            CreateFirstPersonProfile(
                Dl1PreviewStageIds.FppHSpineBasisCorrection,
                Dl1PreviewStageIds.FppHeadPositionCorrection,
                Dl1PreviewStageIds.FppHandInertia,
                Dl1PreviewStageIds.FppHandsProjection),
            purpose: EvaluationPurpose.Export,
            dl1PreviewInputs: inputs);

        EvaluationFrame export = evaluator.Evaluate(exportRequest);

        Assert.Null(export.Camera);
        Assert.Empty(export.CameraHelpers);
        Assert.Empty(export.Dl1PreviewStages);
        Assert.DoesNotContain(
            export.Diagnostics,
            diagnostic =>
                diagnostic.Code.StartsWith("dl1_", StringComparison.Ordinal));
        Assert.True(
            export.AuthoredPose.LocalTransforms.SequenceEqual(
                export.DisplayPose.LocalTransforms));

        Dl1Anm2AuthoringSequence sequence =
            new Anm2EvaluationAdapter(evaluator).SampleAuthoredFrames(
                exportRequest);
        Assert.Equal(2, sequence.Frames.Length);
        Assert.All(
            sequence.Frames,
            frame => Assert.Single(frame.Tracks));
        Assert.Equal(
            1.0,
            sequence.Frames[1].Tracks[0].LocalTransform.Translation.X,
            10);
    }

    [Fact]
    public void FppSnapshotRejectsFiniteHandsFarPlane()
    {
        var finiteHandsProjection = new Dl1ProjectionParameters(
            55.0,
            Dl1ProjectionFovAxis.Horizontal,
            16.0 / 9.0,
            0.004,
            Dl1ProjectionFarPlane.Finite,
            100.0);

        Assert.Throws<ArgumentException>(
            () => new Dl1FppProjectionSnapshot(
                CameraLens.Default,
                finiteHandsProjection));
    }

    [Fact]
    public void BuiltInProfilesKeepFppAndMovieCameraContractsDistinct()
    {
        Assert.Equal(
            Dl1PreviewContract.EyeCameraBoneName,
            PreviewProfile.FirstPersonAuthoring.CameraBoneName);
        Assert.Equal(
            Dl1PreviewContext.Dl1Fpp,
            PreviewProfile.FirstPersonAuthoring.Context);
        Assert.Contains(
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            PreviewProfile.FirstPersonAuthoring
                .ProceduralToggles);
        Assert.Contains(
            Dl1PreviewStageIds.FppHandsProjection,
            PreviewProfile.FirstPersonAuthoring
                .ProceduralToggles);
        Assert.DoesNotContain(
            Dl1PreviewStageIds.FppHeadSpineCorrection,
            PreviewProfile.FirstPersonAuthoring
                .ProceduralToggles);
        Assert.DoesNotContain(
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            PreviewProfile.FirstPersonAuthoring
                .ProceduralToggles);
        Assert.DoesNotContain(
            Dl1PreviewStageIds.FppHandInertia,
            PreviewProfile.FirstPersonAuthoring
                .ProceduralToggles);
        Assert.Null(PreviewProfile.MovieAuthoring.CameraBoneName);
        Assert.Equal(
            Dl1PreviewContext.Dl1Movie,
            PreviewProfile.MovieAuthoring.Context);
    }

    [Fact]
    public void BuiltInDl1StageIdentifiersCannotBeOverridden()
    {
        var guessedHandInertia = new ConstantBoneOffsetPreviewStage(
            Dl1PreviewStageIds.FppHandInertia,
            0,
            TransformTRS.Identity,
            AuthoringPreviewFidelity.Bones);

        Assert.Throws<ArgumentException>(
            () => new AnimationEvaluator([guessedHandInertia]));
    }

    private static Dl1PreviewStageReport AssertStage(
        EvaluationFrame frame,
        string stageId,
        Dl1PreviewStageStatus expectedStatus)
    {
        Dl1PreviewStageReport report = Assert.Single(
            frame.Dl1PreviewStages,
            stage => stage.StageId == stageId);
        Assert.Equal(expectedStatus, report.Status);
        return report;
    }

    private static PreviewProfile CreateFirstPersonProfile(
        params string[] proceduralToggles)
    {
        PreviewProfile baseline =
            PreviewProfile.FirstPersonAuthoring;
        return new PreviewProfile(
            baseline.Id,
            baseline.ViewMode,
            baseline.Fidelity,
            baseline.VisualStyle,
            baseline.CameraBoneName,
            baseline.CameraLens,
            baseline.CameraOffset,
            baseline.FidelityTier,
            baseline.Context,
            baseline.ProfileVersion,
            baseline.BuildFingerprint,
            [.. proceduralToggles],
            baseline.MorphActivationThreshold,
            baseline.MaximumActiveMorphTargets,
            baseline.ClampMorphWeightsToRigBounds,
            baseline.CaptureFingerprint);
    }

    private static Dl1FppBodyCorrectionSnapshot
        CreateBodyCorrectionSnapshot(
            bool vehicleControllerActive = false) =>
        new(
            Vector3D.UnitY,
            -Vector3D.UnitX,
            -Vector3D.UnitZ,
            vehicleControllerActive);

    private static RigDefinition CreateBodyCorrectionRig(
        bool includeHSpine1 = true)
    {
        var bones = new List<BoneDefinition>
        {
            new(
                0,
                "root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root,
                descriptorHash: 0xCCC3CDDF),
            new(
                1,
                Dl1PreviewContract.HSpineBoneName,
                0,
                new TransformTRS(
                    new Vector3D(1.0, 2.0, 3.0),
                    QuaternionD.FromAxisAngle(
                        Vector3D.UnitZ,
                        0.35),
                    new Vector3D(2.0, 3.0, 4.0)),
                semanticRole:
                    Dl1PreviewContract.HSpineSemanticRole),
        };
        int cameraParentIndex = 1;
        if (includeHSpine1)
        {
            bones.Add(
                new(
                    2,
                    Dl1PreviewContract.HSpine1BoneName,
                    1,
                    new TransformTRS(
                        new Vector3D(0.0, 5.0, 0.0),
                        QuaternionD.Identity,
                        new Vector3D(0.5, 2.0, 1.5)),
                    semanticRole:
                        Dl1PreviewContract.HSpine1SemanticRole));
            cameraParentIndex = 2;
        }

        int eyeCameraIndex = bones.Count;
        bones.Add(
            new(
                eyeCameraIndex,
                Dl1PreviewContract.EyeCameraBoneName,
                cameraParentIndex,
                new TransformTRS(
                    new Vector3D(0.2, 0.4, 0.8),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false,
                semanticRole:
                    Dl1PreviewContract.EyeCameraSemanticRole));
        bones.Add(
            new(
                bones.Count,
                Dl1PreviewContract.ReferenceCameraBoneName,
                cameraParentIndex,
                new TransformTRS(
                    new Vector3D(-0.2, 0.4, 0.8),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Helper,
                requiredForExport: false,
                semanticRole:
                    Dl1PreviewContract.ReferenceCameraSemanticRole));
        return new("dl1-fpp-body", "DL1 FPP Body", bones);
    }

    private static RigDefinition CreateAmbiguousHSpineRig() =>
        new(
            "ambiguous-hspine",
            "Ambiguous HSpine",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    Dl1PreviewContract.HSpineBoneName,
                    0,
                    TransformTRS.Identity),
                new BoneDefinition(
                    2,
                    Dl1PreviewContract.HSpineBoneName,
                    0,
                    TransformTRS.Identity),
                new BoneDefinition(
                    3,
                    Dl1PreviewContract.HSpine1BoneName,
                    1,
                    TransformTRS.Identity,
                    semanticRole:
                        Dl1PreviewContract.HSpine1SemanticRole),
                new BoneDefinition(
                    4,
                    Dl1PreviewContract.EyeCameraBoneName,
                    3,
                    TransformTRS.Identity,
                    BoneKind.Camera,
                    requiredForExport: false,
                    semanticRole:
                        Dl1PreviewContract.EyeCameraSemanticRole),
            ]);

    private static TransformMatrix CreateCorrectedBasisWorld(
        TransformMatrix original,
        Dl1FppBodyCorrectionSnapshot snapshot)
    {
        Vector3D scale = GetColumnScale(original);
        Vector3D columnX = snapshot.WorldUp * scale.X;
        Vector3D columnY = snapshot.ModelLeft * scale.Y;
        Vector3D columnZ = -snapshot.ModelForward * scale.Z;
        return new(
            columnX.X,
            columnY.X,
            columnZ.X,
            original.M14,
            columnX.Y,
            columnY.Y,
            columnZ.Y,
            original.M24,
            columnX.Z,
            columnY.Z,
            columnZ.Z,
            original.M34,
            0.0,
            0.0,
            0.0,
            1.0);
    }

    private static Vector3D GetColumnScale(TransformMatrix matrix) =>
        new(
            new Vector3D(matrix.M11, matrix.M21, matrix.M31).Length,
            new Vector3D(matrix.M12, matrix.M22, matrix.M32).Length,
            new Vector3D(matrix.M13, matrix.M23, matrix.M33).Length);

    private static void AssertVectorNearlyEqual(
        Vector3D expected,
        Vector3D actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
        Assert.Equal(expected.Z, actual.Z, 8);
    }

    private static RigDefinition CreatePlayerRig(
        bool includeReferenceCamera = true)
    {
        var bones = new List<BoneDefinition>
        {
            new(
                0,
                "root",
                -1,
                new TransformTRS(
                    new Vector3D(2.0, 0.0, 0.0),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Root,
                descriptorHash: 0xCCC3CDDF),
            new(
                1,
                Dl1PreviewContract.EyeCameraBoneName,
                0,
                new TransformTRS(
                    new Vector3D(0.0, 1.6, 0.2),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false,
                semanticRole: Dl1PreviewContract.EyeCameraSemanticRole),
        };
        if (includeReferenceCamera)
        {
            bones.Add(
                new(
                    2,
                    Dl1PreviewContract.ReferenceCameraBoneName,
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.7, -0.1),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Helper,
                    requiredForExport: false,
                    semanticRole:
                        Dl1PreviewContract.ReferenceCameraSemanticRole));
        }

        return new("dl1-player", "DL1 Player", bones);
    }

    private static RigDefinition CreateMovieRigWithRefCamera() =>
        new(
            "movie-rig",
            "Movie Rig",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0xCCC3CDDF),
                new BoneDefinition(
                    1,
                    Dl1PreviewContract.ReferenceCameraBoneName,
                    0,
                    new TransformTRS(
                        new Vector3D(100.0, 0.0, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Camera,
                    requiredForExport: false,
                    semanticRole:
                        Dl1PreviewContract.ReferenceCameraSemanticRole),
            ]);

    private static AnimationClip CreateRootAnimation() =>
        new(
            "root-motion",
            new FrameRate(30, 1),
            2,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                new Vector3D(2.0, 0.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                        new TransformKeyframe(
                            1.0,
                            new TransformTRS(
                                new Vector3D(1.0, 0.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
}
