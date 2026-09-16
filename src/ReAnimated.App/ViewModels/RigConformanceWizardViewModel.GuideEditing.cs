using System.Collections.Immutable;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed class BodyGuideMoveEventArgs(FbxModelAuthoringImportResult model, RigLandmarkMove move, Vector3D delta) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RigLandmarkMove Move { get; } = move;
    public Vector3D Delta { get; } = delta;
}

public sealed class BodyGuideLockEventArgs(FbxModelAuthoringImportResult model, RiggingJobToken token, Guid guideId, bool locked) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingJobToken Token { get; } = token;
    public Guid GuideId { get; } = guideId;
    public bool Locked { get; } = locked;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private double _bodyGuideX;
    [ObservableProperty] private double _bodyGuideY;
    [ObservableProperty] private double _bodyGuideZ;
    [ObservableProperty] private bool _mirrorBodyGuide;
    [ObservableProperty] private string _bodyGuideEditStatus = "Save draft guides to enable position editing.";
    private GuideTranslationTarget? _bodyGuideGizmoTarget;
    private BodyGuideDrag? _bodyGuideDrag;

    public IRenderTranslationGizmoTarget BodyGuideGizmoTarget => _bodyGuideGizmoTarget ??= new(this);
    public ImmutableDictionary<Guid, Vector3D> BodyGuidePreview { get; private set; } = ImmutableDictionary<Guid, Vector3D>.Empty;
    public Guid? SelectedBodyGuideId => SelectedStoredBodyGuide?.Id;
    private RigLandmark? SelectedStoredBodyGuide
    {
        get
        {
            if (BodyDetection is not null || SelectedBodyProposal is not { } proposal || _model?.Package.Document.RiggingSession is not { } session) return null;
            RigLandmark? selected = null;
            foreach (var guide in session.Landmarks.Where(l => string.Equals(l.RoleId, proposal.Role, StringComparison.Ordinal)))
            { if (selected is not null) return null; selected = guide; }
            return selected;
        }
    }
    public bool HasSavedBodyGuide => SelectedStoredBodyGuide is not null;
    public bool CanEditBodyGuide => (HasUnriggedSource || CanEditPendingHandGuide) && !IsBusy && SelectedStoredBodyGuide is { Locked: false } &&
        _model?.Package.Document.RiggingSession is { RequiresSourceReview: false };
    public bool CanMirrorBodyGuide => CanEditBodyGuide && SelectedStoredBodyGuide is { MirrorPartnerId: { } partnerId } guide &&
        _model!.Package.Document.RiggingSession!.Landmarks.Any(l => l.Id == partnerId && l.MirrorPartnerId == guide.Id && !l.Locked);
    public string BodyGuidePinLabel => SelectedStoredBodyGuide is { Locked: true } ? "Unpin guide" : "Pin guide";
    public IRelayCommand ApplyBodyGuidePositionCommand { get; private set; } = null!;
    public IRelayCommand ToggleBodyGuidePinCommand { get; private set; } = null!;
    public IRelayCommand? UndoBodyGuideEditCommand { get; private set; }
    public IRelayCommand? RedoBodyGuideEditCommand { get; private set; }
    public event EventHandler? BodyGuidePreviewChanged;
    public event EventHandler<BodyGuideMoveEventArgs>? BodyGuideMoveRequested;
    public event EventHandler<BodyGuideLockEventArgs>? BodyGuideLockRequested;

    private void InitializeBodyGuideEditing()
    {
        ApplyBodyGuidePositionCommand = new RelayCommand(ApplyBodyGuidePosition, () => CanEditBodyGuide && _bodyGuideDrag is null &&
            (!MirrorBodyGuide || CanMirrorBodyGuide));
        ToggleBodyGuidePinCommand = new RelayCommand(() => {
            CancelBodyGuideDrag();
            if (_model is { } model && SelectedStoredBodyGuide is { } guide && model.Package.Document.RiggingSession is { } session)
                BodyGuideLockRequested?.Invoke(this, new(model, session.CreateJobToken(), guide.Id, !guide.Locked));
        }, () => !IsBusy && HasSavedBodyGuide);
    }

    internal void SetAuthoringHistoryCommands(IRelayCommand undo, IRelayCommand redo)
    {
        UndoBodyGuideEditCommand = undo; RedoBodyGuideEditCommand = redo;
        OnPropertyChanged(nameof(UndoBodyGuideEditCommand)); OnPropertyChanged(nameof(RedoBodyGuideEditCommand));
    }

    private void ApplyBodyGuidePosition()
    {
        if (!CanEditBodyGuide || _model is not { } model || SelectedStoredBodyGuide is not { } guide ||
            !RigLandmarkEditing.TryBeginMove(model.Package.Document.RiggingSession!, guide.Id, MirrorBodyGuide, out var move)) return;
        var delta = new Vector3D(BodyGuideX, BodyGuideY, BodyGuideZ) - guide.Position;
        if (!TryPreviewGuideMove(move!, delta, out _))
        { BodyGuideEditStatus = "Enter finite positions within the viewport range."; return; }
        BodyGuideMoveRequested?.Invoke(this, new(model, move!, delta));
    }

    public void SelectBodyGuide(Guid? id)
    {
        if (id is not { } guideId || _model?.Package.Document.RiggingSession is not { } session) return;
        var guide = session.Landmarks.FirstOrDefault(l => l.Id == guideId);
        if (guide is not null) SelectedBodyProposal = BodyProposals.FirstOrDefault(p => p.Role == guide.RoleId);
    }

    /// <summary>Rebinds a metadata-only save without resetting unsaved conformance placement decisions.</summary>
    internal void RefreshMetadataSnapshot(FbxModelAuthoringImportResult previous, FbxModelAuthoringImportResult current)
    {
        if (!ReferenceEquals(_model, previous)) { SetModel(current); return; }
        Guid? selected = SelectedBodyGuideId;
        bool mirror = MirrorBodyGuide;
        InvalidateBodyDetection();
        _model = current;
        RestoreStoredBodyGuides();
        RestoreBodyAuthoring();
        if (!ReferenceEquals(previous.Package.Document.RiggingSession, current.Package.Document.RiggingSession)) RestoreChannelPolicies();
        RefreshWeightMetadata(current);
        RefreshContactMetadata(current);
        RefreshHandMetadata(current);
        RefreshEyeMetadata(current);
        RefreshRestPoseMetadata(current);
        RefreshHierarchyMetadata(current);
        RefreshDerivedMotionMetadata(current);
        RefreshDoctorMetadata(current);
        RefreshStressReviewModel();
        RestoreStudioWorkflow();
        SelectBodyGuide(selected);
        MirrorBodyGuide = mirror && CanMirrorBodyGuide;
        NotifyBodyDetectionCommands();
    }

    private void RefreshBodyGuideEditor()
    {
        CancelBodyGuideDrag();
        if (SelectedStoredBodyGuide is { } guide)
        {
            BodyGuideX = guide.Position.X; BodyGuideY = guide.Position.Y; BodyGuideZ = guide.Position.Z;
            BodyGuideEditStatus = HasGeneratedBodyRig ? "These guides describe the generated skeleton. Undo its creation to revise guide positions before rebuilding." :
                guide.Locked ? "Pinned guide. Unpin it before changing its position." :
                "Drag an axis or apply a model-space position. Escape cancels a drag; Undo restores a saved edit.";
        }
        else BodyGuideEditStatus = "Use draft guides to save these positions before editing.";
        if (!CanMirrorBodyGuide) MirrorBodyGuide = false;
        NotifyBodyGuideEditing();
    }

    private void NotifyBodyGuideEditing()
    {
        OnPropertyChanged(nameof(SelectedBodyGuideId)); OnPropertyChanged(nameof(HasSavedBodyGuide));
        OnPropertyChanged(nameof(CanEditBodyGuide)); OnPropertyChanged(nameof(CanMirrorBodyGuide)); OnPropertyChanged(nameof(BodyGuidePinLabel));
        ApplyBodyGuidePositionCommand?.NotifyCanExecuteChanged(); ToggleBodyGuidePinCommand?.NotifyCanExecuteChanged();
    }

    public void CancelBodyGuideDrag()
    {
        bool hadPreview = _bodyGuideDrag is not null || !BodyGuidePreview.IsEmpty;
        _bodyGuideDrag = null; BodyGuidePreview = ImmutableDictionary<Guid, Vector3D>.Empty;
        if (hadPreview)
        {
            if (SelectedStoredBodyGuide is { } guide)
            { BodyGuideX = guide.Position.X; BodyGuideY = guide.Position.Y; BodyGuideZ = guide.Position.Z; }
            BodyGuidePreviewChanged?.Invoke(this, EventArgs.Empty);
            NotifyBodyGuideEditing();
        }
    }

    private bool BeginBodyGuideDrag(RenderTranslationGizmoDragStart start)
    {
        if (_bodyGuideDrag is not null || !CanEditBodyGuide || _model is not { } model ||
            !start.Binding.IsValid || start.Binding.Space != RenderGizmoSpace.Global || start.Binding.TargetId is not { } id ||
            id != SelectedBodyGuideId || start.AxisDirectionWorld != GuideAxis(start.Binding.Axis) ||
            !RigLandmarkEditing.TryBeginMove(model.Package.Document.RiggingSession!, id, MirrorBodyGuide, out var move)) return false;
        _bodyGuideDrag = new(model, move!, start.Binding, Vector3D.Zero);
        NotifyBodyGuideEditing();
        return true;
    }

    private bool UpdateBodyGuideDrag(RenderTranslationGizmoDragUpdate update)
    {
        if (_bodyGuideDrag is not { } drag || !IsCurrentGuideDrag(drag) || update.Binding != drag.Binding ||
            !float.IsFinite(update.AxisDistance) || !float.IsFinite(update.WorldDelta.X) || !float.IsFinite(update.WorldDelta.Y) ||
            !float.IsFinite(update.WorldDelta.Z) || Vector3.DistanceSquared(update.WorldDelta, GuideAxis(update.Binding.Axis) * update.AxisDistance) > 1e-12f)
        { CancelBodyGuideDrag(); return false; }
        var delta = new Vector3D(update.WorldDelta.X, update.WorldDelta.Y, update.WorldDelta.Z);
        if (!TryPreviewGuideMove(drag.Move, delta, out var preview)) { CancelBodyGuideDrag(); return false; }
        _bodyGuideDrag = drag with { Delta = delta };
        BodyGuidePreview = preview;
        var position = preview[drag.Move.Selected.Id];
        BodyGuideX = position.X; BodyGuideY = position.Y; BodyGuideZ = position.Z;
        BodyGuidePreviewChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CompleteBodyGuideDrag(bool commit)
    {
        var drag = _bodyGuideDrag;
        bool accept = commit && drag is not null && IsCurrentGuideDrag(drag);
        CancelBodyGuideDrag();
        if (accept) BodyGuideMoveRequested?.Invoke(this, new(drag!.Model, drag.Move, drag.Delta));
    }

    private bool IsCurrentGuideDrag(BodyGuideDrag drag) => !IsBusy && ReferenceEquals(_model, drag.Model) &&
        SelectedBodyGuideId == drag.Move.Selected.Id && _model.Package.Document.RiggingSession is { } session && session.Matches(drag.Move.Token);

    private static bool TryPreviewGuideMove(RigLandmarkMove move, Vector3D delta, out ImmutableDictionary<Guid, Vector3D> preview)
    {
        preview = ImmutableDictionary<Guid, Vector3D>.Empty;
        try
        {
            preview = move.Preview(delta);
            return preview.Values.All(static p => float.IsFinite((float)p.X) && float.IsFinite((float)p.Y) && float.IsFinite((float)p.Z));
        }
        catch (ArgumentException) { return false; }
    }

    private static Vector3 GuideAxis(TranslationGizmoAxis axis) => axis switch {
        TranslationGizmoAxis.X => Vector3.UnitX, TranslationGizmoAxis.Y => Vector3.UnitY, TranslationGizmoAxis.Z => Vector3.UnitZ,
        _ => Vector3.Zero,
    };
    partial void OnMirrorBodyGuideChanged(bool value) { CancelBodyGuideDrag(); NotifyBodyGuideEditing(); BodyDetectionChanged?.Invoke(this, EventArgs.Empty); }
    private sealed record BodyGuideDrag(FbxModelAuthoringImportResult Model, RigLandmarkMove Move, TranslationGizmoBinding Binding, Vector3D Delta);
    private sealed class GuideTranslationTarget(RigConformanceWizardViewModel owner) : IRenderTranslationGizmoTarget
    {
        public bool TryBeginTranslationGizmoDrag(RenderTranslationGizmoDragStart start) => owner.BeginBodyGuideDrag(start);
        public bool UpdateTranslationGizmoDrag(RenderTranslationGizmoDragUpdate update) => owner.UpdateBodyGuideDrag(update);
        public void CompleteTranslationGizmoDrag(bool commit) => owner.CompleteBodyGuideDrag(commit);
    }
}
