using System.ComponentModel;
using System.IO;
using System.Text;
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

    /// <summary>
    /// The preview session built from the conformed model. Its mesh palettes
    /// address the conformed rig, so it is only valid while the emitted bone
    /// table is unchanged.
    /// </summary>
    private CustomModelPreviewSession? _conformanceSession;

    /// <summary>
    /// Identifies the emitted bone table the cached session was built for.
    /// Moving a joint changes positions only; the table changes when the
    /// correspondence does.
    /// </summary>
    private string? _conformanceTopologyKey;

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
    /// Rebuilding that pairing costs roughly a quarter second, so it is done
    /// only when the emitted bone table changes - resolving a template,
    /// toggling extra bones, overriding a mapping. Placing a joint or moving a
    /// slider changes positions alone, which the cached session absorbs through
    /// a skeleton swap costing a couple of milliseconds.
    /// </para>
    /// <para>
    /// At the bind pose this is visually identical to the imported model, since
    /// skinning through a bind pose is the identity. The author sees their own
    /// mesh with DL1 bones laid into it, which is exactly what the fitting
    /// stages are for.
    /// </para>
    /// </remarks>
    private void OnConformanceFitChanged(object? sender, EventArgs e)
    {
        if (_model is null || !IsConformTabSelected)
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
                FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
                    _model,
                    fit,
                    Conformance.CreateSettings());
                _conformanceSession = CustomModelPreviewAdapter.CreateSession(
                    conformed,
                    CustomModelPreviewMode.Dl1Output);
                _conformanceTopologyKey = topologyKey;
                rebuilt = true;
            }

            CustomModelPreviewSession session = _conformanceSession;
            RigDefinition rig = Dl1RigConformanceApplier.CreateRigDefinition(fit);
            SkeletonRenderData skeleton = CorePreviewAdapter.ToRenderSkeleton(
                rig.CreateBindPose(),
                Conformance.SelectedBoneIndex >= 0
                    ? Conformance.SelectedBoneIndex
                    : null);

            if (rebuilt)
            {
                Viewport.SceneSource.SetScene(
                    session.Meshes,
                    skeleton,
                    [],
                    generation: Interlocked.Increment(ref _previewGeneration));
            }
            else
            {
                Viewport.SceneSource.SetSkeleton(skeleton);
            }

            Viewport.SceneSource.SetMeshVisibility(ShowMeshes);
            ApplySkeletonVisibility();
            Viewport.SetPresentation(
                $"Conformance preview - {ModelName}",
                Conformance.SolveStatus);
            Viewport.SetDiagnosticOverlay(null);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException)
        {
            InvalidateConformancePreview();
            Viewport.SetDiagnosticOverlay(
                $"The conformed model could not be previewed: {exception.Message}");
        }
    }

    /// <summary>
    /// The emitted bone table's identity: names, parents and kinds in order.
    /// Positions are deliberately excluded, because a moved joint reuses the
    /// same skin palettes.
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
                .Append('|');
        }

        return builder.ToString();
    }

    private void InvalidateConformancePreview()
    {
        _conformanceSession = null;
        _conformanceTopologyKey = null;
    }

    private void OnConformancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RigConformanceWizardViewModel.Stage) or
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
        bool refining =
            IsConformTabSelected &&
            Conformance.Stage == RigConformanceStage.Refine;
        Viewport.SceneSource.SetTranslationGizmoTarget(
            refining ? Conformance.GizmoTarget : null);

        if (IsConformTabSelected)
        {
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

            _helperUndo.Push(model.Package.Document);
            InvalidateConformancePreview();
            CommitModel(conformed, _sourcePath, _packagePath);
            PopulateHierarchyRows();

            string diagnostics = prepared.Diagnostics.IsEmpty
                ? "no diagnostics"
                : $"{prepared.Diagnostics.Length} diagnostic(s)";
            BuildStatus =
                $"Applied the DL1 conformance: {prepared.Contract.Nodes.Length:N0} emitted nodes, {diagnostics}. " +
                "The source FBX and these settings are retained, so the conversion can be reopened.";
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
