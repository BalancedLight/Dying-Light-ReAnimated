using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

internal sealed class Dl1PreviewEvaluationResult
{
    public Dl1PreviewEvaluationResult(
        SkeletonPose displayPose,
        EvaluatedCamera? camera,
        IEnumerable<EvaluatedCameraHelper>? cameraHelpers = null,
        IEnumerable<Dl1PreviewStageReport>? stages = null,
        IEnumerable<EvaluationDiagnostic>? diagnostics = null)
    {
        DisplayPose = displayPose ??
            throw new ArgumentNullException(nameof(displayPose));
        Camera = camera;
        CameraHelpers = cameraHelpers?.ToImmutableArray() ?? [];
        Stages = stages?.ToImmutableArray() ?? [];
        Diagnostics = diagnostics?.ToImmutableArray() ?? [];
    }

    public SkeletonPose DisplayPose { get; }

    public EvaluatedCamera? Camera { get; }

    public ImmutableArray<EvaluatedCameraHelper> CameraHelpers { get; }

    public ImmutableArray<Dl1PreviewStageReport> Stages { get; }

    public ImmutableArray<EvaluationDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Evaluates DL1 camera context and explicitly bounded procedural stages.
/// This evaluator is called only after the exportable pose has been finalized.
/// </summary>
internal static class Dl1PreviewContextEvaluator
{
    public static Dl1PreviewEvaluationResult Evaluate(
        SkeletonPose displayPose,
        PreviewProfile profile,
        Dl1PreviewInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(displayPose);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);

        return profile.Context switch
        {
            Dl1PreviewContext.Dl1Fpp =>
                EvaluateFpp(displayPose, profile, inputs),
            Dl1PreviewContext.Dl1Movie =>
                EvaluateMovie(displayPose, profile, inputs),
            _ => EvaluateProfileCamera(displayPose, profile),
        };
    }

    private static Dl1PreviewEvaluationResult EvaluateFpp(
        SkeletonPose displayPose,
        PreviewProfile profile,
        Dl1PreviewInputs inputs)
    {
        var diagnostics = ImmutableArray.CreateBuilder<EvaluationDiagnostic>();
        var stages = ImmutableArray.CreateBuilder<Dl1PreviewStageReport>();
        var helpers = ImmutableArray.CreateBuilder<EvaluatedCameraHelper>();
        SkeletonPose previewPose = ApplyHSpineBasisCorrection(
            displayPose,
            profile,
            inputs,
            stages,
            diagnostics);
        bool cameraRequested =
            profile.Fidelity.HasFlag(AuthoringPreviewFidelity.Camera);

        int eyeCameraIndex = -1;
        int referenceCameraIndex = -1;
        TransformMatrix eyeCameraWorld = TransformMatrix.Identity;
        if (!cameraRequested)
        {
            stages.Add(
                Disabled(
                    Dl1PreviewStageIds.FppCameraHelpers,
                    "Camera fidelity is disabled for this profile."));
        }
        else
        {
            eyeCameraIndex = ResolveCameraHelper(
                previewPose.Rig,
                Dl1PreviewContract.EyeCameraSemanticRole,
                Dl1PreviewContract.EyeCameraBoneName,
                diagnostics);
            referenceCameraIndex = ResolveCameraHelper(
                previewPose.Rig,
                Dl1PreviewContract.ReferenceCameraSemanticRole,
                Dl1PreviewContract.ReferenceCameraBoneName,
                diagnostics);

            if (eyeCameraIndex < 0)
            {
                diagnostics.Add(
                    new(
                        "dl1_fpp_eye_camera_missing",
                        EvaluationDiagnosticSeverity.Error,
                        $"Rig '{previewPose.Rig.Id}' has no unambiguous EyeCamera helper."));
                stages.Add(
                    Unavailable(
                        Dl1PreviewStageIds.FppCameraHelpers,
                        "EyeCamera is required for the DL1 FPP camera context."));
            }
            else
            {
                eyeCameraWorld =
                    previewPose.GlobalMatrices[eyeCameraIndex] *
                    profile.CameraOffset.ToMatrix();
                helpers.Add(
                    new(
                        Dl1PreviewContract.EyeCameraHelperRole,
                        previewPose.Rig.Bones[eyeCameraIndex].Name,
                        eyeCameraIndex,
                        previewPose.GlobalMatrices[eyeCameraIndex]));

                if (referenceCameraIndex >= 0)
                {
                    helpers.Add(
                        new(
                            Dl1PreviewContract.ReferenceCameraHelperRole,
                            previewPose.Rig.Bones[referenceCameraIndex].Name,
                            referenceCameraIndex,
                            previewPose.GlobalMatrices[referenceCameraIndex]));
                    stages.Add(
                        Applied(
                            Dl1PreviewStageIds.FppCameraHelpers,
                            "EyeCamera and RefCamera helpers are bound from the evaluated player rig."));
                }
                else
                {
                    diagnostics.Add(
                        new(
                            "dl1_fpp_reference_helper_missing",
                            EvaluationDiagnosticSeverity.Warning,
                            $"Rig '{previewPose.Rig.Id}' has no unambiguous RefCamera helper; EyeCamera preview remains available."));
                    stages.Add(
                        Fallback(
                            Dl1PreviewStageIds.FppCameraHelpers,
                            "EyeCamera is bound, but RefCamera helper diagnostics are unavailable."));
                }
            }
        }

        if (!cameraRequested)
        {
            stages.Add(
                Disabled(
                    Dl1PreviewStageIds.FppViewTransform,
                    "Camera fidelity is disabled for this profile."));
        }
        else if (eyeCameraIndex < 0)
        {
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.FppViewTransform,
                    "EyeCamera is required for an editor FPP view."));
        }
        else
        {
            // GetCameraPos/GetCameraDir branch through live selfie, cinematic,
            // model, look, shaker, vehicle, and eye-tracking state. The
            // evaluated EyeCamera helper is useful authoring context, but is
            // not claimed as the complete runtime camera transform.
            diagnostics.Add(
                new(
                    "dl1_fpp_view_transform_fallback",
                    EvaluationDiagnosticSeverity.Warning,
                    "The editor view is anchored to the evaluated EyeCamera helper; live DL1 camera offsets and controller state are not available."));
            stages.Add(
                Fallback(
                    Dl1PreviewStageIds.FppViewTransform,
                    "Using the evaluated EyeCamera helper as an editor fallback, not a game-validated runtime camera transform."));
        }

        CameraLens sceneLens = profile.CameraLens;
        Dl1FppProjectionSnapshot? projection = inputs.FppProjection;
        if (!cameraRequested)
        {
            stages.Add(
                Disabled(
                    Dl1PreviewStageIds.FppSceneProjection,
                    "Camera fidelity is disabled for this profile."));
        }
        else if (projection is null)
        {
            // PlayerFppVis::GetDefaultFOV/GetCameraClipNear read live runtime
            // variables and state. A fixed number here would falsely claim
            // parity, so the profile lens is visibly labeled as fallback.
            diagnostics.Add(
                new(
                    "dl1_fpp_scene_projection_fallback",
                    EvaluationDiagnosticSeverity.Warning,
                    "No captured DL1 FPP projection was supplied; the editor profile lens is a non-game-validated fallback."));
            stages.Add(
                Fallback(
                    Dl1PreviewStageIds.FppSceneProjection,
                    "Using the editor profile lens because runtime FOV/near-plane state was not supplied."));
        }
        else
        {
            sceneLens = projection.SceneCameraLens;
            stages.Add(
                Applied(
                    Dl1PreviewStageIds.FppSceneProjection,
                    "Using explicit user/runtime-capture DL1 scene-camera projection inputs; this does not itself claim game validation."));
        }

        bool handsRequested = IsToggleRequested(
            profile,
            Dl1PreviewStageIds.FppHandsProjection);
        Dl1ProjectionParameters? handsProjection = null;
        if (!handsRequested || !cameraRequested)
        {
            stages.Add(
                Disabled(
                    Dl1PreviewStageIds.FppHandsProjection,
                    handsRequested
                        ? "Camera fidelity is disabled for this profile."
                        : "The separate hands projection toggle is disabled."));
        }
        else if (eyeCameraIndex < 0)
        {
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.FppHandsProjection,
                    "A separate hands projection cannot be used without EyeCamera."));
        }
        else if (projection is null)
        {
            // GetHandsProjection constructs a distinct infinite-far frustum
            // from camera angle/aspect and near-plane state. The decompile does
            // not establish safe static values for those runtime inputs.
            diagnostics.Add(
                new(
                    "dl1_fpp_hands_projection_unavailable",
                    EvaluationDiagnosticSeverity.Warning,
                    "DL1 uses a separate infinite-far hands projection, but no captured FOV/aspect/near-plane state was supplied."));
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.FppHandsProjection,
                    "Captured hands FOV, axis, aspect, and near-plane data are required."));
        }
        else
        {
            handsProjection = projection.HandsProjection;
            stages.Add(
                Applied(
                    Dl1PreviewStageIds.FppHandsProjection,
                    "Using explicit user/runtime-capture values for the separate DL1 hands projection; this does not itself claim game validation."));
        }

        AddUnavailableRuntimeProceduralStage(
            IsGroupedToggleRequested(
                profile,
                Dl1PreviewStageIds.FppHeadSpineCorrection,
                Dl1PreviewStageIds.FppHeadPositionCorrection),
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            "dl1_fpp_head_position_correction_unavailable",
            "The full head-position solver depends on movement, look, landing, sprint, edge-grab, interaction, and spring history that are not present in an offline pose request.",
            stages,
            diagnostics);
        AddUnavailableRuntimeProceduralStage(
            IsToggleRequested(
                profile,
                Dl1PreviewStageIds.FppHandInertia),
            Dl1PreviewStageIds.FppHandInertia,
            "dl1_fpp_hand_inertia_unavailable",
            "The game stage is stateful and depends on camera/player velocities, springs, aiming, weapons, interactions, movement controllers, and frame history.",
            stages,
            diagnostics);

        EvaluatedCamera? camera = cameraRequested && eyeCameraIndex >= 0
            ? new(
                eyeCameraWorld,
                sceneLens,
                true,
                EvaluatedCameraSource.Dl1FppEyeCamera,
                handsProjection)
            : null;
        return new(
            previewPose,
            camera,
            helpers,
            stages,
            diagnostics);
    }

    private static Dl1PreviewEvaluationResult EvaluateMovie(
        SkeletonPose displayPose,
        PreviewProfile profile,
        Dl1PreviewInputs inputs)
    {
        var diagnostics = ImmutableArray.CreateBuilder<EvaluationDiagnostic>();
        var stages = ImmutableArray.CreateBuilder<Dl1PreviewStageReport>();
        bool requested =
            profile.Fidelity.HasFlag(AuthoringPreviewFidelity.Camera) &&
            IsToggleRequested(
                profile,
                Dl1PreviewStageIds.MovieReferenceCamera);
        if (!requested)
        {
            stages.Add(
                Disabled(
                    Dl1PreviewStageIds.MovieReferenceCamera,
                    "The movie reference-camera stage is disabled."));
            return new(
                displayPose,
                null,
                stages: stages,
                diagnostics: diagnostics);
        }

        Dl1MovieReferenceCameraSnapshot? referenceCamera =
            inputs.MovieReferenceCamera;
        if (referenceCamera is null)
        {
            // CMovieManager stores an external IBaseCamera. A skeleton
            // RefCamera element is a different player-rig helper and is not a
            // valid substitute.
            diagnostics.Add(
                new(
                    "dl1_movie_reference_camera_unavailable",
                    EvaluationDiagnosticSeverity.Warning,
                    "No external DL1 movie reference-camera snapshot was supplied; a rig RefCamera bone is not substituted."));
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.MovieReferenceCamera,
                    "Movie preview requires an external reference-camera transform and lens."));
            return new(
                displayPose,
                null,
                stages: stages,
                diagnostics: diagnostics);
        }

        stages.Add(
            Applied(
                Dl1PreviewStageIds.MovieReferenceCamera,
                "Using the supplied external DL1 movie reference camera."));
        return new(
            displayPose,
            new(
                referenceCamera.WorldTransform,
                referenceCamera.Lens,
                false,
                EvaluatedCameraSource.Dl1MovieReferenceCamera),
            stages: stages,
            diagnostics: diagnostics);
    }

    private static Dl1PreviewEvaluationResult EvaluateProfileCamera(
        SkeletonPose displayPose,
        PreviewProfile profile)
    {
        var diagnostics = ImmutableArray.CreateBuilder<EvaluationDiagnostic>();
        if (!profile.Fidelity.HasFlag(AuthoringPreviewFidelity.Camera))
        {
            return new(displayPose, null);
        }

        if (string.IsNullOrWhiteSpace(profile.CameraBoneName))
        {
            if (profile.ViewMode is PreviewViewMode.FirstPerson or PreviewViewMode.Split)
            {
                diagnostics.Add(
                    new(
                        "preview_camera_binding_missing",
                        EvaluationDiagnosticSeverity.Error,
                        "The first-person preview profile has no camera-bone binding."));
            }

            return new(displayPose, null, diagnostics: diagnostics);
        }

        int cameraBoneIndex =
            displayPose.Rig.GetBoneIndex(profile.CameraBoneName);
        if (cameraBoneIndex < 0)
        {
            diagnostics.Add(
                new(
                    "preview_camera_bone_not_found",
                    EvaluationDiagnosticSeverity.Error,
                    $"Camera bone '{profile.CameraBoneName}' is not present in rig '{displayPose.Rig.Id}'."));
            return new(displayPose, null, diagnostics: diagnostics);
        }

        TransformMatrix cameraWorld =
            displayPose.GlobalMatrices[cameraBoneIndex] *
            profile.CameraOffset.ToMatrix();
        return new(
            displayPose,
            new(
                cameraWorld,
                profile.CameraLens,
                profile.ViewMode is
                    PreviewViewMode.FirstPerson or PreviewViewMode.Split));
    }

    private static int ResolveCameraHelper(
        RigDefinition rig,
        string semanticRole,
        string canonicalName,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        int[] semanticMatches = rig.Bones
            .Where(
                bone => string.Equals(
                    bone.SemanticRole,
                    semanticRole,
                    StringComparison.OrdinalIgnoreCase))
            .Select(static bone => bone.Index)
            .ToArray();
        if (semanticMatches.Length > 1)
        {
            diagnostics.Add(
                new(
                    "dl1_camera_helper_role_ambiguous",
                    EvaluationDiagnosticSeverity.Error,
                    $"Rig '{rig.Id}' has multiple bones with semantic role '{semanticRole}'."));
            return -1;
        }

        if (semanticMatches.Length == 1)
        {
            return semanticMatches[0];
        }

        int canonicalIndex = rig.GetBoneIndex(canonicalName);
        if (canonicalIndex >= 0)
        {
            return canonicalIndex;
        }

        return -1;
    }

    private static SkeletonPose ApplyHSpineBasisCorrection(
        SkeletonPose displayPose,
        PreviewProfile profile,
        Dl1PreviewInputs inputs,
        ImmutableArray<Dl1PreviewStageReport>.Builder stages,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        bool requested = IsGroupedToggleRequested(
            profile,
            Dl1PreviewStageIds.FppHeadSpineCorrection,
            Dl1PreviewStageIds.FppHSpineBasisCorrection);
        if (!requested)
        {
            stages.Add(
                Disabled(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    "The HSpine basis-correction toggle is disabled."));
            return displayPose;
        }

        Dl1FppBodyCorrectionSnapshot? snapshot = inputs.FppBodyCorrection;
        if (snapshot is null)
        {
            const string reason =
                "An explicit world-up/model-left/model-forward and vehicle-state snapshot is required.";
            diagnostics.Add(
                new(
                    "dl1_fpp_hspine_basis_snapshot_missing",
                    EvaluationDiagnosticSeverity.Information,
                    reason));
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    reason));
            return displayPose;
        }

        if (snapshot.VehicleControllerActive)
        {
            stages.Add(
                Bypassed(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    "DL1 bypasses HSpine/HSpine1 and head-position correction while the vehicle controller is active."));
            return displayPose;
        }

        int hSpineIndex = ResolveRequiredBodyElement(
            displayPose.Rig,
            Dl1PreviewContract.HSpineSemanticRole,
            Dl1PreviewContract.HSpineBoneName,
            diagnostics);
        int hSpine1Index = ResolveRequiredBodyElement(
            displayPose.Rig,
            Dl1PreviewContract.HSpine1SemanticRole,
            Dl1PreviewContract.HSpine1BoneName,
            diagnostics);
        if (hSpineIndex < 0 || hSpine1Index < 0)
        {
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    "The target rig must provide unambiguous HSpine and HSpine1 body elements."));
            return displayPose;
        }

        try
        {
            // PlayerFppVis::ApplyAnimation invokes CorrectHSpine and then
            // CorrectHSpine1 before CorrectHeadPosition and camera evaluation.
            SkeletonPose corrected = CorrectBodyElementBasis(
                displayPose,
                hSpineIndex,
                snapshot,
                orthonormalize: false);
            corrected = CorrectBodyElementBasis(
                corrected,
                hSpine1Index,
                snapshot,
                orthonormalize: true);
            stages.Add(
                Applied(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    "Applied the DL1 1.55 decompile-matched HSpine/HSpine1 basis correction from the explicitly supplied authoring-world/model basis before EyeCamera extraction; this is not game validated."));
            return corrected;
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(
                new(
                    "dl1_fpp_hspine_basis_unrepresentable",
                    EvaluationDiagnosticSeverity.Warning,
                    $"The HSpine correction was not applied atomically: {exception.Message}"));
            stages.Add(
                Unavailable(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    "The corrected world transforms cannot be represented safely by the local-TRS preview pose."));
            return displayPose;
        }
    }

    private static int ResolveRequiredBodyElement(
        RigDefinition rig,
        string semanticRole,
        string canonicalName,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        int[] semanticMatches = rig.Bones
            .Where(
                bone => string.Equals(
                    bone.SemanticRole,
                    semanticRole,
                    StringComparison.OrdinalIgnoreCase))
            .Select(static bone => bone.Index)
            .ToArray();
        if (semanticMatches.Length > 1)
        {
            diagnostics.Add(
                new(
                    "dl1_fpp_hspine_basis_role_ambiguous",
                    EvaluationDiagnosticSeverity.Error,
                    $"Rig '{rig.Id}' has multiple body elements with semantic role '{semanticRole}'."));
            return -1;
        }

        if (semanticMatches.Length == 1)
        {
            return semanticMatches[0];
        }

        ImmutableArray<int> nameMatches = rig.GetBoneIndices(canonicalName);
        if (nameMatches.Length > 1)
        {
            diagnostics.Add(
                new(
                    "dl1_fpp_hspine_basis_name_ambiguous",
                    EvaluationDiagnosticSeverity.Error,
                    $"Rig '{rig.Id}' has multiple body elements named '{canonicalName}'."));
            return -1;
        }

        if (nameMatches.Length == 1)
        {
            return nameMatches[0];
        }

        diagnostics.Add(
            new(
                "dl1_fpp_hspine_basis_bone_missing",
                EvaluationDiagnosticSeverity.Error,
                $"Rig '{rig.Id}' has no body element for '{canonicalName}' ({semanticRole})."));
        return -1;
    }

    private static SkeletonPose CorrectBodyElementBasis(
        SkeletonPose pose,
        int boneIndex,
        Dl1FppBodyCorrectionSnapshot snapshot,
        bool orthonormalize)
    {
        TransformMatrix originalWorld = pose.GlobalMatrices[boneIndex];
        Vector3D scale = GetColumnScale(originalWorld);
        Vector3D axisX = snapshot.WorldUp;
        Vector3D axisY = snapshot.ModelLeft;
        Vector3D axisZ = -snapshot.ModelForward;
        if (orthonormalize)
        {
            (axisX, axisY, axisZ) =
                OrthonormalizeBasis(axisX, axisY, axisZ);
        }

        TransformMatrix correctedWorld = CreateWorldBasis(
            axisX * scale.X,
            axisY * scale.Y,
            axisZ * scale.Z,
            originalWorld.Translation);
        int parentIndex = pose.Rig.Bones[boneIndex].ParentIndex;
        TransformMatrix parentWorld = parentIndex < 0
            ? TransformMatrix.Identity
            : pose.GlobalMatrices[parentIndex];
        TransformMatrix correctedLocal =
            parentWorld.InvertedAffine() * correctedWorld;
        SkeletonPose correctedPose = pose.WithLocalTransform(
            boneIndex,
            correctedLocal.Decompose(1e-7));
        if (!correctedPose.GlobalMatrices[boneIndex].NearlyEquals(
                correctedWorld,
                1e-7))
        {
            throw new InvalidOperationException(
                $"Corrected body element '{pose.Rig.Bones[boneIndex].Name}' did not round-trip through local TRS.");
        }

        return correctedPose;
    }

    private static Vector3D GetColumnScale(TransformMatrix matrix)
    {
        var scale = new Vector3D(
            new Vector3D(matrix.M11, matrix.M21, matrix.M31).Length,
            new Vector3D(matrix.M12, matrix.M22, matrix.M32).Length,
            new Vector3D(matrix.M13, matrix.M23, matrix.M33).Length);
        if (!scale.IsFinite ||
            scale.X <= 1e-10 ||
            scale.Y <= 1e-10 ||
            scale.Z <= 1e-10)
        {
            throw new InvalidOperationException(
                "A corrected body element has a zero or non-finite world-scale axis.");
        }

        return scale;
    }

    private static (
        Vector3D AxisX,
        Vector3D AxisY,
        Vector3D AxisZ) OrthonormalizeBasis(
            Vector3D axisX,
            Vector3D axisY,
            Vector3D axisZ)
    {
        Vector3D x = axisX.Normalized();
        Vector3D y =
            (axisY - (x * Vector3D.Dot(axisY, x))).Normalized();
        Vector3D z = Vector3D.Cross(x, y).Normalized();
        if (Vector3D.Dot(z, axisZ) < 0.0)
        {
            z = -z;
        }

        y = Vector3D.Cross(z, x).Normalized();
        return (x, y, z);
    }

    private static TransformMatrix CreateWorldBasis(
        Vector3D columnX,
        Vector3D columnY,
        Vector3D columnZ,
        Vector3D translation) =>
        new(
            columnX.X,
            columnY.X,
            columnZ.X,
            translation.X,
            columnX.Y,
            columnY.Y,
            columnZ.Y,
            translation.Y,
            columnX.Z,
            columnY.Z,
            columnZ.Z,
            translation.Z,
            0.0,
            0.0,
            0.0,
            1.0);

    private static void AddUnavailableRuntimeProceduralStage(
        bool requested,
        string stageId,
        string diagnosticCode,
        string unavailableReason,
        ImmutableArray<Dl1PreviewStageReport>.Builder stages,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        if (!requested)
        {
            stages.Add(
                Disabled(
                    stageId,
                    "The procedural preview toggle is disabled."));
            return;
        }

        diagnostics.Add(
            new(
                diagnosticCode,
                EvaluationDiagnosticSeverity.Information,
                unavailableReason));
        stages.Add(Unavailable(stageId, unavailableReason));
    }

    private static bool IsToggleRequested(
        PreviewProfile profile,
        string stageId) =>
        profile.ProceduralToggles.IsEmpty ||
        profile.ProceduralToggles.Contains(
            stageId,
            StringComparer.Ordinal);

    private static bool IsGroupedToggleRequested(
        PreviewProfile profile,
        string groupStageId,
        string concreteStageId) =>
        profile.ProceduralToggles.IsEmpty ||
        profile.ProceduralToggles.Contains(
            groupStageId,
            StringComparer.Ordinal) ||
        profile.ProceduralToggles.Contains(
            concreteStageId,
            StringComparer.Ordinal);

    private static Dl1PreviewStageReport Applied(
        string stageId,
        string message) =>
        new(stageId, true, Dl1PreviewStageStatus.Applied, message);

    private static Dl1PreviewStageReport Fallback(
        string stageId,
        string message) =>
        new(stageId, true, Dl1PreviewStageStatus.Fallback, message);

    private static Dl1PreviewStageReport Bypassed(
        string stageId,
        string message) =>
        new(stageId, true, Dl1PreviewStageStatus.Bypassed, message);

    private static Dl1PreviewStageReport Disabled(
        string stageId,
        string message) =>
        new(stageId, false, Dl1PreviewStageStatus.Disabled, message);

    private static Dl1PreviewStageReport Unavailable(
        string stageId,
        string message) =>
        new(stageId, true, Dl1PreviewStageStatus.Unavailable, message);
}
