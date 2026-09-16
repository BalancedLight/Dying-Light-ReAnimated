using ReAnimated.App.Infrastructure;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;
using ReAnimated.Renderer.D3D11;
using System.Numerics;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private void OnStressPreviewChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressPreviewRefresh) return;
        if (Conformance.StressPreviewEnabled)
        {
            Timeline.IsPlaying = false;
            Viewport.SceneSource.SetTranslationGizmoTarget(null);
            InvalidateConformancePreview();
            RefreshPreview();
        }
        else UpdateConformanceViewportBinding();
    }

    private void OnWeightDisplayChanged(object? sender, EventArgs e)
    {
        if (_suppressPreviewRefresh) return;
        _previewSession = null;
        if (Conformance.WeightBrushEnabled) Viewport.SceneSource.SetTranslationGizmoTarget(null);
        if (!Conformance.WeightBrushEnabled && IsConformTabSelected && Conformance.Stage == RigConformanceStage.Refine)
            UpdateConformanceViewportBinding();
        else RefreshPreview();
    }

    private void OnWeightBrushOverlayChanged(object? sender, EventArgs e)
    {
        if (_suppressPreviewRefresh || !IsConformTabSelected) return;
        if (Conformance.WeightBrushCenter is not { } position) { Viewport.SceneSource.SetGizmos([]); return; }
        Vector3 center = new((float)position.X, (float)position.Y, (float)position.Z);
        float radius = (float)Conformance.WeightBrushRadius;
        var lines = new List<GizmoRenderData>();
        for (int plane = 0; plane < 3; plane++) for (int segment = 0; segment < 32; segment++)
        {
            float a = segment * MathF.Tau / 32, b = (segment + 1) * MathF.Tau / 32;
            Vector3 Point(float angle) => plane switch { 0 => new(MathF.Cos(angle), MathF.Sin(angle), 0), 1 => new(MathF.Cos(angle), 0, MathF.Sin(angle)), _ => new(0, MathF.Cos(angle), MathF.Sin(angle)) };
            lines.Add(new(GizmoKind.Line, center + radius * Point(a), center + radius * Point(b), new Vector4(1, .7f, .1f, 1), 1.5f));
        }
        Viewport.SceneSource.SetGizmos(lines);
    }

    private void OnRigPoliciesApplyRequested(object? sender, RigPoliciesEventArgs e)
    {
        if (!ReferenceEquals(_model, e.Model) || _model?.Package.Document.RiggingSession is not { } current || !current.Matches(e.Token)) return;
        if (!ReferenceEquals(current, e.Session)) CommitBodyGuideSession(e.Session, "Saved animation channel ownership and LOD decisions.");
    }

    private void OnBodyComponentsApplyRequested(object? sender, BodyComponentsEventArgs e)
    {
        if (!ReferenceEquals(_model, e.Model)) return;
        var current = _model!.Package.Document.RiggingSession ?? e.Session;
        if (!current.Matches(e.Session.CreateJobToken())) return;
        if (_model.Package.Document.RiggingSession is not null && current.Components.SequenceEqual(e.Components)) return;
        var updated = RiggingSessions.Change(current, current with { Components = e.Components }, RiggingEditKind.Components);
        CommitBodyGuideSession(updated, "Saved anatomy and binding component choices.");
    }

    private void OnBodyModelApplyRequested(object? sender, BodyModelEventArgs e)
    {
        if (!ReferenceEquals(_model, e.Source)) return;
        var result = e.Result;
        if (result.Package.Document.RiggingSession is { } session)
            result = result with { Package = result.Package with { Document = result.Package.Document with { RiggingSession = RiggingSessions.Navigate(session, Conformance.StudioStage) } } };
        result.Package.Document.Validate();
        var before = CaptureAuthoringSnapshot();
        CommitModel(result, _sourcePath, _packagePath, preserveAuthoringHistory: true);
        RecordAuthoringUndo(before);
        UpdateConformanceViewportBinding();
        BuildStatus = e.Status; _setStatus(BuildStatus); NotifyCommands();
    }

    private void OnBodyDetectionChanged(object? sender, EventArgs e)
    {
        if (HandReviewActive && !_suppressPreviewRefresh)
        {
            Viewport.SceneSource.SetTranslationGizmoTarget(Conformance.CanEditBodyGuide ? Conformance.BodyGuideGizmoTarget : null);
            PublishHandOverlay(); return;
        }
        if (_suppressPreviewRefresh || !IsConformTabSelected || !(Conformance.IsStudioDetect || Conformance.IsStudioFit) || _model?.Rig is not null) return;
        Viewport.SceneSource.SetTranslationGizmoTarget(Conformance.CanEditBodyGuide ? Conformance.BodyGuideGizmoTarget : null);
        if (Conformance.BodyProposals.Count == 0) { Viewport.SceneSource.SetGizmos([]); return; }
        InvalidateConformancePreview();
        RefreshPreview();
    }

    private void OnBodyGuidePreviewChanged(object? sender, EventArgs e)
    {
        if (!_suppressPreviewRefresh) PublishBodyDetectionOverlay();
    }

    private void PublishBodyDetectionOverlay()
    {
        if (HandReviewActive) return;
        if (!IsConformTabSelected || !(Conformance.IsStudioDetect || Conformance.IsStudioFit) || _model?.Rig is not null || Conformance.BodyProposals.Count == 0) return;
        if (Conformance.BodyDetection is { } detection)
            Viewport.SceneSource.SetGizmos(AnatomicalGuideOverlayBuilder.Build(detection, Conformance.SelectedBodyProposal?.Role));
        else
        {
            var landmarks = _model?.Package.Document.RiggingSession?.Landmarks ?? [];
            var guides = Conformance.BodyProposals.Select(p => landmarks.FirstOrDefault(l => l.RoleId == p.Role) is { } guide &&
                Conformance.BodyGuidePreview.TryGetValue(guide.Id, out var position) ? p with { Position = position } : p).ToArray();
            Viewport.SceneSource.SetGizmos(AnatomicalGuideOverlayBuilder.BuildStored(guides, Conformance.SelectedBodyProposal?.Role,
                Conformance.CanEditBodyGuide ? Conformance.SelectedBodyGuideId : null));
        }
        Viewport.SetPresentation($"Draft anatomical guides - {ModelName}", "Source mesh with reviewable guide positions");
    }

    private void OnBodyGuidesApplyRequested(object? sender, BodyDetectionApplyEventArgs e)
    {
        if (_model is not { } model || !ReferenceEquals(model, e.Model))
        {
            BuildStatus = "The model changed after detection; run detection again before using its guides.";
            return;
        }
        try
        {
            var current = model.Package.Document.RiggingSession ?? e.Session;
            if (!AnatomicalDetectionAdoption.TryApply(current, e.Token, e.Detection.GridFingerprint, e.Detection, out var adopted))
            {
                BuildStatus = "The detection inputs changed; run detection again before using its guides.";
                return;
            }
            CommitBodyGuideSession(adopted, "Saved draft anatomical guides; source geometry and skinning were preserved.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            BuildStatus = "Draft guides could not be applied: " + exception.Message;
            _setStatus(BuildStatus);
        }
        NotifyCommands();
    }

    private void OnBodyGuideMoveRequested(object? sender, BodyGuideMoveEventArgs e)
    {
        if (_model is not { } model || !ReferenceEquals(model, e.Model) || model.Package.Document.RiggingSession is not { } session) return;
        if (!RigLandmarkEditing.TryCommitMove(session, e.Move, e.Delta, out var moved))
        { BuildStatus = "The guide inputs changed. Select the guide again before editing."; return; }
        if (!ReferenceEquals(session, moved)) CommitBodyGuideSession(moved, "Saved guide position. Source geometry, bones and skinning were preserved.");
    }

    private void OnBodyGuideLockRequested(object? sender, BodyGuideLockEventArgs e)
    {
        if (_model is not { } model || !ReferenceEquals(model, e.Model) || model.Package.Document.RiggingSession is not { } session || !session.Matches(e.Token)) return;
        var changed = RigLandmarkEditing.SetLocked(session, e.GuideId, e.Locked);
        if (!ReferenceEquals(session, changed)) CommitBodyGuideSession(changed, e.Locked ? "Pinned guide position." : "Unpinned guide position.");
    }

    private void CommitBodyGuideSession(RiggingSession session, string status)
    {
        if (_model is not { } model) return;
        var document = model.Package.Document with { RiggingSession = RiggingSessions.Navigate(session, Conformance.StudioStage) };
        document.Validate();
        var before = CaptureAuthoringSnapshot();
        CommitModel(model with { Package = model.Package with { Document = document } }, _sourcePath, _packagePath, preserveAuthoringHistory: true);
        RecordAuthoringUndo(before);
        UpdateConformanceViewportBinding();
        BuildStatus = status; _setStatus(status); NotifyCommands();
    }
}
