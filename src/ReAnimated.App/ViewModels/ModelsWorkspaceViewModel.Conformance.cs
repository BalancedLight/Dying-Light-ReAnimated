using System.ComponentModel;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _isConformTabSelected;
    private IRelayCommand? _frameSelectedConformanceJointCommand;

    public IRelayCommand FrameSelectedConformanceJointCommand =>
        _frameSelectedConformanceJointCommand ??= new RelayCommand(
            FrameSelectedConformanceJoint);

    private void FrameSelectedConformanceJoint()
    {
        RenderFrameSnapshot frame = Viewport.SceneSource.CaptureFrame();
        SkeletonRenderData? skeleton = frame.Skeleton;
        if (!IsConformTabSelected ||
            !Conformance.IsStudioFit ||
            Conformance.Stage != RigConformanceStage.Refine ||
            skeleton is null)
        {
            _setStatus("Select a joint in Place joints first.");
            return;
        }

        int selected = -1;
        for (int index = 0; index < skeleton.Bones.Count; index++)
        {
            if (skeleton.Bones[index].IsSelected)
            {
                selected = index;
                break;
            }
        }

        if (selected < 0)
        {
            _setStatus("Select a joint in Place joints first.");
            return;
        }

        Vector3 WorldPosition(BoneRenderData bone) =>
            (bone.WorldTransform * skeleton.RootTransform).Translation;
        Vector3 pivot = WorldPosition(skeleton.Bones[selected]);
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;
        foreach (BoneRenderData bone in skeleton.Bones.Where(static bone =>
                     bone.Role == BoneRenderRole.Deform))
        {
            float y = WorldPosition(bone).Y;
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        float height = float.IsFinite(maxY - minY)
            ? maxY - minY
            : 1.0f;
        float distance = Math.Clamp(height * 0.5f, 0.4f, 1.8f);
        Vector3 direction = frame.Camera.Eye - frame.Camera.Target;
        if (direction.LengthSquared() < 1.0e-8f)
        {
            direction = Vector3.UnitZ;
        }

        direction = Vector3.Normalize(direction);
        _cameraCoordinator.UpdateCamera(
            ViewportSide.Target,
            frame.Camera with
            {
                Target = pivot,
                Eye = pivot + direction * distance,
            });
        _setStatus("Framed the selected joint.");
    }

    /// <summary>
    /// The preview session built from the conformed model. Its mesh palettes
    /// address the conformed rig, so it is only valid while the emitted bone
    /// table is unchanged.
    /// </summary>
    private CustomModelPreviewSession? _conformanceSession;
    private ImmutableArray<int> _conformanceFitToEffective = [];

    /// <summary>
    /// Identifies the emitted bone table the cached session was built for.
    /// Includes mapping identity and fit frames, not only row order.
    /// </summary>
    private string? _conformanceTopologyKey;

    private bool FitPreviewIsActive =>
        _model is not null &&
        IsConformTabSelected &&
        Conformance.IsStudioFit &&
        !HandReviewActive &&
        !EyeReviewActive &&
        !EyeMotionActive &&
        !DoctorReviewActive &&
        !DerivedReviewActive &&
        !HierarchyReviewActive &&
        !RestReviewActive &&
        !Conformance.WeightBrushEnabled &&
        !Conformance.StressPreviewEnabled;

    /// <summary>
    /// Whether the Conform tab is showing. Bound from the tab so viewport joint
    /// dragging is only routed to the wizard while the author can actually see
    /// what they are moving.
    /// </summary>
    public bool IsConformTabSelected
    {
        get => _isConformTabSelected;
        set
        {
            if (SetProperty(ref _isConformTabSelected, value))
            {
                UpdateConformanceViewportBinding();
            }
        }
    }

    /// <summary>
    /// Previews the conformed model whenever the wizard re-solves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mesh skin palettes are indexes into the skeleton they were bound
    /// against, so the mesh and the skeleton must always come from the same
    /// rig. Showing the conformed skeleton over the imported mesh would skin it
    /// through unrelated bones - silently wrong where the row counts happen to
    /// fit, and rejected outright where they do not.
    /// </para>
    /// <para>
    /// Rebuild the pairing when either topology or fit frames change. The
    /// prepared skeleton and its baked mesh must come from the same candidate;
    /// swapping an independently built fit skeleton can use a different palette
    /// order or bind basis. Large-asset interaction cost remains a measured
    /// performance concern, not a reason to display a mismatched pair.
    /// </para>
    /// </remarks>
    private void OnConformanceFitChanged(object? sender, EventArgs e)
    {
        if (_model is not { } model || !FitPreviewIsActive)
        {
            return;
        }

        if (Conformance.Fit is not { } fit)
        {
            InvalidateConformancePreview();
            RefreshPreview();
            return;
        }

        try
        {
            string topologyKey = BuildConformanceTopologyKey(fit);
            bool rebuilt = false;
            if (_conformanceSession is null ||
                !string.Equals(_conformanceTopologyKey, topologyKey, StringComparison.Ordinal))
            {
                Dl1RigConformanceApplyResult applied = Dl1RigConformanceApplier.ApplyDetailed(
                    model,
                    fit,
                    Conformance.CreateSettings());
                _conformanceSession = CustomModelPreviewAdapter.CreateSession(
                    applied.Model,
                    CustomModelPreviewMode.Dl1Output);
                _conformanceFitToEffective = applied.FitToEffective;
                _conformanceTopologyKey = topologyKey;
                rebuilt = true;
            }

            CustomModelPreviewSession session = _conformanceSession;
            int selectedFit = Conformance.SelectedBoneIndex;
            Conformance.SetGizmoBoneIndexMap(_conformanceFitToEffective);
            int selectedEffective =
                (uint)selectedFit < (uint)_conformanceFitToEffective.Length
                    ? _conformanceFitToEffective[selectedFit]
                    : -1;
            SkeletonRenderData? skeleton = session.CreateSkeleton(null, 0,
                selectedEffective >= 0 ? selectedEffective : null);

            if (rebuilt || !ReferenceEquals(_sceneOwnerSession, session))
            {
                Viewport.SceneSource.SetScene(
                    session.Meshes,
                    skeleton,
                    [],
                    generation: Interlocked.Increment(ref _previewGeneration));
                _sceneOwnerSession = session;
            }
            else
            {
                Viewport.SceneSource.SetSkeleton(skeleton);
            }

            Viewport.SceneSource.SetGizmos(
                Conformance.Stage == RigConformanceStage.Refine
                    ? BuildJointPlacementGizmos(skeleton, selectedEffective)
                    : []);

            Viewport.SceneSource.SetMorphWeights(MergeGuidedMorphOverrides(model, []));
            Viewport.SceneSource.SetMeshVisibility(ShowMeshes);
            ApplySkeletonVisibility();
            Viewport.SetPresentation(
                Conformance.IsAdvancedSetupMode ? $"Conformance preview - {ModelName}" : $"Joint preview - {ModelName}",
                Conformance.IsAdvancedSetupMode ? Conformance.SolveStatus : "Review the joint positions and body proportions.");
            Viewport.SetDiagnosticOverlay(null);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            CustomModelFormatException or
            InvalidOperationException or
            ArgumentException)
        {
            InvalidateConformancePreview();
            Viewport.SetDiagnosticOverlay(
                $"The conformed model could not be previewed: {exception.Message}");
        }
    }

    private static IReadOnlyList<GizmoRenderData> BuildJointPlacementGizmos(
        SkeletonRenderData? skeleton,
        int selectedBoneIndex)
    {
        if (skeleton is null ||
            (uint)selectedBoneIndex >= (uint)skeleton.Bones.Count)
        {
            return [];
        }

        Vector3 WorldPosition(BoneRenderData bone) =>
            (bone.WorldTransform * skeleton.RootTransform).Translation;
        Vector3 origin = WorldPosition(skeleton.Bones[selectedBoneIndex]);
        float height = skeleton.Bones
            .Where(static bone => bone.Role == BoneRenderRole.Deform)
            .Select(WorldPosition)
            .Select(static point => point.Y)
            .DefaultIfEmpty(origin.Y)
            .Max() - skeleton.Bones
            .Where(static bone => bone.Role == BoneRenderRole.Deform)
            .Select(WorldPosition)
            .Select(static point => point.Y)
            .DefaultIfEmpty(origin.Y)
            .Min();
        float length = Math.Clamp(height * 0.08f, 0.06f, 0.35f);
        return
        [
            Handle(Vector3.UnitX, TranslationGizmoAxis.X, new(1f, .25f, .2f, 1f)),
            Handle(Vector3.UnitY, TranslationGizmoAxis.Y, new(.2f, 1f, .3f, 1f)),
            Handle(Vector3.UnitZ, TranslationGizmoAxis.Z, new(.3f, .55f, 1f, 1f)),
        ];

        GizmoRenderData Handle(
            Vector3 axis,
            TranslationGizmoAxis bindingAxis,
            Vector4 color) =>
            new(
                GizmoKind.TranslationHandle,
                origin,
                origin + axis * length,
                color,
                4f,
                new TranslationGizmoBinding(
                    selectedBoneIndex,
                    bindingAxis,
                    RenderGizmoSpace.Global),
                InteractionAxisWorld: axis);
    }

    /// <summary>
    /// The emitted bone table's identity: names, parents and kinds in order.
    /// Include exact fit frames so meshes, palettes and the prepared skeleton
    /// always describe the same authoring candidate.
    /// </summary>
    private static string BuildConformanceTopologyKey(RigConformanceResult fit)
    {
        var builder = new StringBuilder(fit.Bones.Length * 16);
        foreach (RigConformedBone bone in fit.Bones)
        {
            builder.Append(bone.Name)
                .Append('/')
                .Append(bone.ParentIndex)
                .Append('/')
                .Append((int)bone.Kind)
                .Append('/').Append(bone.SourceBoneIndex).Append('/').Append(bone.TemplateIndex)
                .Append('/').Append(bone.IsDeform).Append('/').Append((int)bone.Disposition)
                .Append('/').Append(bone.Position.X.ToString("R", CultureInfo.InvariantCulture))
                .Append('/').Append(bone.Position.Y.ToString("R", CultureInfo.InvariantCulture))
                .Append('/').Append(bone.Position.Z.ToString("R", CultureInfo.InvariantCulture))
                .Append('/').Append(bone.Orientation.ToString())
                .Append('|');
        }

        return builder.ToString();
    }

    private void InvalidateConformancePreview()
    {
        _conformanceSession = null;
        _conformanceFitToEffective = [];
        _conformanceTopologyKey = null;
        _sceneOwnerSession = null;
        Conformance.SetGizmoBoneIndexMap([]);
    }

    private void OnConformancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RigConformanceWizardViewModel.IsBusy)) ModelBatch?.RefreshAvailability();
        if (e.PropertyName is not (nameof(RigConformanceWizardViewModel.Stage) or nameof(RigConformanceWizardViewModel.StudioStage) or
            nameof(RigConformanceWizardViewModel.SelectedLandmark)))
        {
            return;
        }

        UpdateConformanceViewportBinding();
    }

    /// <summary>
    /// Routes the translation gizmo to the wizard only while the Conform tab's
    /// refine stage is visible, and clears it otherwise so a drag elsewhere can
    /// never move a conformance joint.
    /// </summary>
    private void UpdateConformanceViewportBinding()
    {
        if (HandReviewActive)
        {
            Viewport.SceneSource.SetBrushTarget(null);
            Viewport.SceneSource.SetTranslationGizmoTarget(Conformance.CanEditBodyGuide ? Conformance.BodyGuideGizmoTarget : null);
            InvalidateConformancePreview(); RefreshPreview(); return;
        }
        Viewport.SceneSource.SetBrushTarget(IsConformTabSelected && Conformance.WeightBrushEnabled && ShowMeshes ? Conformance.WeightBrushTarget : null);
        if (IsConformTabSelected && (Conformance.WeightBrushEnabled || Conformance.StressPreviewEnabled))
        {
            Viewport.SceneSource.SetTranslationGizmoTarget(null);
            InvalidateConformancePreview(); RefreshPreview(); return;
        }
        Conformance.CancelWeightBrush();
        if (!IsConformTabSelected) Conformance.StopStressReview();
        if (IsConformTabSelected && (Conformance.IsStudioDetect || Conformance.IsStudioFit) && _model is { Rig: null } && Conformance.BodyProposals.Count > 0)
        {
            Viewport.SceneSource.SetTranslationGizmoTarget(Conformance.CanEditBodyGuide ? Conformance.BodyGuideGizmoTarget : null);
            InvalidateConformancePreview();
            RefreshPreview();
            return;
        }
        Conformance.CancelBodyGuideDrag();
        bool refining =
            IsConformTabSelected &&
            Conformance.IsStudioFit &&
            Conformance.Stage == RigConformanceStage.Refine;
        Viewport.SceneSource.SetTranslationGizmoTarget(
            refining ? Conformance.GizmoTarget : null);

        if (IsConformTabSelected && Conformance.IsStudioFit)
        {
            if (FitPreviewIsActive)
            {
                // The Models workspace uses a target-side scene source. A
                // session preview camera overrides its orbit camera, and that
                // override blocks translation-gizmo input on target panes.
                // The conformance fit owns this viewport until we leave Fit;
                // RefreshPreview below restores the session/evaluated camera.
                _cameraCoordinator.SetTargetPreviewCameraOverride(null);
            }

            OnConformanceFitChanged(this, EventArgs.Empty);
        }
        else if (_model is not null)
        {
            // Leaving the tab restores the model's own preview so a conformed
            // scene cannot linger over an unrelated inspector.
            InvalidateConformancePreview();
            RefreshPreview();
        }
    }

    /// <summary>
    /// Commits the conformance onto the document. This is the one destructive
    /// step: the emitted bone table becomes DL1's. The source FBX stays in the
    /// package and the settings are recorded, so the conversion can be reopened
    /// and adjusted rather than redone.
    /// </summary>
    private void OnConformanceApplyRequested(object? sender, EventArgs e)
    {
        if (_model is not { } model ||
            Conformance.Fit is not { } fit)
        {
            return;
        }

        try
        {
            FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
                model,
                fit,
                Conformance.CreateSettings());

            // Prove the emitted rig before adopting it, so a refusal leaves the
            // imported model untouched.
            Dl1PreparedAuthoredRig prepared =
                Dl1CustomModelRigPreparer.Prepare(conformed);
            conformed = _captureAuthoredLayer(conformed, CancellationToken.None);

            AuthoringSnapshot before = CaptureAuthoringSnapshot();
            InvalidateConformancePreview();
            CommitModel(conformed, _sourcePath, _packagePath, preserveAuthoringHistory: true);
            RecordAuthoringUndo(before);
            PopulateHierarchyRows();

            string diagnostics = prepared.Diagnostics.IsEmpty
                ? "no diagnostics"
                : $"{prepared.Diagnostics.Length} diagnostic(s)";
            BuildStatus = Conformance.IsAdvancedSetupMode
                ? $"Applied the DL1 conformance: {prepared.Contract.Nodes.Length:N0} emitted nodes, {diagnostics}. " +
                  "The source FBX and these settings are retained, so the conversion can be reopened. " +
                  "Original FBX animation was not retargeted; review a separate clip through Derive Motion before export."
                : "Dying Light skeleton applied. Your source model and settings are retained." +
                  (prepared.Diagnostics.IsEmpty ? string.Empty : $" {prepared.Diagnostics.Length} model checks need review.");
            _setStatus(BuildStatus);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException)
        {
            BuildStatus = $"The conformance could not be applied: {exception.Message}";
            _setStatus(BuildStatus);
        }
        finally
        {
            NotifyCommands();
        }
    }
}
