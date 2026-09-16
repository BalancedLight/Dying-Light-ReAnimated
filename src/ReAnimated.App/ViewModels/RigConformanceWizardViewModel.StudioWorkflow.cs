using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record StudioStageChoice(RigStudioStage Stage, string Label, string Purpose);
public sealed record StudioEntryChoice(RigStudioEntryPath Path, string Label);
public sealed record StudioFacetHistoryRow(string Facet, string RecordedResult, string Context);
public sealed class StudioMetadataEventArgs(FbxModelAuthoringImportResult model, RiggingSession session, bool undoable) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingSession Session { get; } = session;
    public bool Undoable { get; } = undoable;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private RigStudioStage _studioStage;
    [ObservableProperty] private StudioEntryChoice? _selectedStudioEntry;
    private bool _restoringStudio;
    private bool _routingStudioAction;
    private Guid? _studioModelId;
    public ModelsWorkspaceViewModel? StudioWorkspace { get; private set; }
    public IReadOnlyList<StudioStageChoice> StudioStages { get; } = [
        new(RigStudioStage.Import, "1 Import", "Review source identity, component selection and the intended workflow."),
        new(RigStudioStage.Detect, "2 Detect", "Inspect geometry proposals or existing-rig correspondence and remaining ambiguity."),
        new(RigStudioStage.Fit, "3 Fit", "Place and pin guides, or review rigged-model fitting before applying anatomy changes."),
        new(RigStudioStage.HelpersAndHooks, "4 Helpers & Hooks", "Inspect hierarchy, helper frames, ownership and unresolved runtime requirements."),
        new(RigStudioStage.Skin, "5 Skin", "Bind selected components, correct source weights and inspect deformation."),
        new(RigStudioStage.Animate, "6 Animate", "Review source stacks, selected stock clips and transient stress poses."),
        new(RigStudioStage.VerifyAndExport, "7 Verify & Export", "Inspect diagnostics, compile and keep compiled, loaded and live evidence distinct."),
    ];
    public ObservableCollection<StudioEntryChoice> StudioEntryChoices { get; } = [];
    public ObservableCollection<StudioFacetHistoryRow> StudioFacetHistory { get; } = [];
    public StudioStageChoice SelectedStudioStage
    {
        get => StudioStages.Single(s => s.Stage == StudioStage);
        set { if (value is not null) StudioStage = value.Stage; }
    }
    public bool IsStudioImport => StudioStage == RigStudioStage.Import;
    public bool IsStudioDetect => StudioStage == RigStudioStage.Detect;
    public bool IsStudioFit => StudioStage == RigStudioStage.Fit;
    public bool IsStudioHelpers => StudioStage == RigStudioStage.HelpersAndHooks;
    public bool IsStudioSkin => StudioStage == RigStudioStage.Skin;
    public bool IsStudioAnimate => StudioStage == RigStudioStage.Animate;
    public bool IsStudioVerify => StudioStage == RigStudioStage.VerifyAndExport;
    public bool HasStudioSession => _model?.Package.Document.RiggingSession is not null;
    public bool HasRiggedStudioSource => _model?.Rig is not null;
    public bool HasImportedRiggedStudioSource => HasRiggedStudioSource && !HasGeneratedBodyRig;
    public bool CanNavigateStudio => HasModel;
    public string StudioStagePurpose => SelectedStudioStage.Purpose;
    public string StudioSourceSummary => _model is { } model
        ? $"{model.Package.Document.Source.OriginalFileName} · {model.Package.Document.Bones.Length} source bones · {model.Package.Document.Meshes.Length} components · editor coordinates in metres"
        : "Import or open a model in this workspace to begin.";
    public string StudioSessionSummary => _model?.Package.Document.RiggingSession is { } session
        ? $"Saved workflow: {EntryLabel(session.EntryPath)}. Navigation and authoring reviews do not certify runtime behavior."
        : "Navigation is view-only until a studio session is started. Starting enables explicit per-node export decisions; existing source geometry and clips are retained.";
    public string StudioReviewSummary
    {
        get
        {
            if (_model?.Package.Document.RiggingSession is not { } session) return "No authoring review is recorded for this stage.";
            var stage = session.Stages.Single(s => s.Stage == StudioStage);
            return stage.ReviewedUtc is { } reviewed ? $"Authoring review recorded {reviewed:u}. This is a review record, not a capability pass." : "No current review record for this stage. Dependent edits clear affected reviews.";
        }
    }
    public string StudioProfileSummary => _model?.Package.Document.RiggingSession?.Recipe.Profile is { } profile
        ? $"Profile reference: {profile.Id} / {profile.Version}. Capability completeness requires its own evidence."
        : "No runtime capability profile is selected. A fitting template alone does not establish helper or gameplay completeness.";
    public IRelayCommand StartStudioCommand { get; private set; } = null!;
    public IRelayCommand PreviousStudioStageCommand { get; private set; } = null!;
    public IRelayCommand NextStudioStageCommand { get; private set; } = null!;
    public IRelayCommand RecordStudioReviewCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedMappingCommand { get; private set; } = null!;
    public event EventHandler<StudioMetadataEventArgs>? StudioMetadataRequested;

    private void InitializeStudioWorkflow()
    {
        StartStudioCommand = new RelayCommand(StartStudio, () => HasModel && !HasStudioSession && SelectedStudioEntry is not null && !IsBusy);
        PreviousStudioStageCommand = new RelayCommand(() => StudioStage = (RigStudioStage)((int)StudioStage - 1), () => CanNavigateStudio && StudioStage > RigStudioStage.Import);
        NextStudioStageCommand = new RelayCommand(() => StudioStage = (RigStudioStage)((int)StudioStage + 1), () => CanNavigateStudio && StudioStage < RigStudioStage.VerifyAndExport);
        RecordStudioReviewCommand = new RelayCommand(() => {
            if (_model is not { } model || model.Package.Document.RiggingSession is not { } session || !session.MatchesSource(model.Package.Document.Source.ContentSha256)) return;
            var reviewed = RiggingSessions.RecordReview(session, StudioStage, session.ComputeInputFingerprint(), DateTimeOffset.UtcNow);
            StudioMetadataRequested?.Invoke(this, new(model, reviewed, true));
        }, () => HasStudioSession && !IsBusy && _model!.Package.Document.RiggingSession!.MatchesSource(_model.Package.Document.Source.ContentSha256));
        OpenRiggedMappingCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Mapping; }, () => HasRiggedStudioSource);
    }

    internal void SetStudioWorkspace(ModelsWorkspaceViewModel workspace)
    { StudioWorkspace = workspace; OnPropertyChanged(nameof(StudioWorkspace)); }

    private void StartStudio()
    {
        if (_model is not { } model || model.Package.Document.RiggingSession is not null || SelectedStudioEntry is not { } selected) return;
        var pendingSession = _bodyDetectionWork?.Session ?? _weightSnapshot?.Session;
        if ((_bodyDetectionWork is not null || _weightPreview is not null) && pendingSession?.EntryPath != selected.Path)
        { _setStatus("Apply or discard the pending authoring proposal before starting a different workflow."); return; }
        var session = (pendingSession?.EntryPath == selected.Path ? pendingSession : RiggingSessions.Create(model.Package.Document, selected.Path)) with { Stage = StudioStage };
        StudioMetadataRequested?.Invoke(this, new(model, session, true));
    }

    private void RestoreStudioWorkflow()
    {
        bool sameModel = _studioModelId == _model?.Package.Document.ModelId;
        _studioModelId = _model?.Package.Document.ModelId;
        _restoringStudio = true;
        StudioStage = _model?.Package.Document.RiggingSession?.Stage ?? (sameModel ? StudioStage : RigStudioStage.Import);
        StudioEntryChoices.Clear();
        if (_model?.Package.Document.RiggingSession is { } session) StudioEntryChoices.Add(new(session.EntryPath, EntryLabel(session.EntryPath)));
        else if (HasUnriggedSource) StudioEntryChoices.Add(new(RigStudioEntryPath.AutoRigBiped, "Build a rig from geometry"));
        else if (HasModel)
        {
            StudioEntryChoices.Add(new(RigStudioEntryPath.RepairExistingRig, "Repair an existing DL rig"));
            StudioEntryChoices.Add(new(RigStudioEntryPath.AdaptExistingRig, "Adapt a rigged model"));
        }
        SelectedStudioEntry = StudioEntryChoices.Count == 1 ? StudioEntryChoices[0] : sameModel ? StudioEntryChoices.FirstOrDefault(c => c.Path == SelectedStudioEntry?.Path) : null;
        _restoringStudio = false;
        RefreshStudioHistory(); NotifyStudioWorkflow();
    }

    private void RefreshStudioHistory()
    {
        StudioFacetHistory.Clear();
        var session = _model?.Package.Document.RiggingSession;
        foreach (var facet in Enum.GetValues<RigValidationFacet>())
        {
            var receipt = session?.ValidationHistory.Where(r => r.Facet == facet).OrderByDescending(static r => r.ObservedUtc).FirstOrDefault();
            StudioFacetHistory.Add(new(FacetLabel(facet), receipt?.Status.ToString() ?? "Unverified", receipt is null
                ? "No evidence receipt."
                : $"Recorded {receipt.ObservedUtc:u}; capability {receipt.CapabilityId}. Current profile/build/actor applicability is not assessed by stage navigation."));
        }
    }

    private static string EntryLabel(RigStudioEntryPath entry) => entry switch {
        RigStudioEntryPath.RepairExistingRig => "Repair an existing DL rig", RigStudioEntryPath.AdaptExistingRig => "Adapt a rigged model",
        RigStudioEntryPath.AutoRigBiped => "Build a rig from geometry", _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };
    private static string FacetLabel(RigValidationFacet facet) => facet switch {
        RigValidationFacet.GeometryAndBind => "Geometry and bind", RigValidationFacet.Mapping => "Mapping", RigValidationFacet.RuntimeNodeCompleteness => "Runtime node completeness",
        RigValidationFacet.MotionCompatibility => "Motion compatibility", RigValidationFacet.CompiledVerification => "Compiled verification", RigValidationFacet.LoadedResourceIdentity => "Loaded resource identity",
        RigValidationFacet.LiveScenario => "Live scenario", _ => throw new ArgumentOutOfRangeException(nameof(facet)),
    };

    internal void RefreshStudioMetadataSnapshot(FbxModelAuthoringImportResult previous, FbxModelAuthoringImportResult current)
    {
        if (!ReferenceEquals(_model, previous)) { SetModel(current); return; }
        _model = current;
        if (_bodyDetectionWork is { } work && ReferenceEquals(work.Model, previous))
            _bodyDetectionWork = new(current, current.Package.Document.RiggingSession ?? work.Session, work.Token, work.Detection);
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
    }

    private void CancelStudioInteractions()
    {
        DeriveMotionCommand?.Cancel(); LoadDoctorRulesCommand?.Cancel(); RunDoctorCommand?.Cancel();
        PreviewHierarchyCommand?.Cancel(); PreviewRestPoseCommand?.Cancel(); CancelBodyGuideDrag(); CancelWeightBrush(); DetectEyeCommand?.Cancel(); CancelEyeRigCommand?.Execute(null);
        DetectBodyCommand?.Cancel(); BuildBodyRigCommand?.Cancel(); BindBodyGeometryCommand?.Cancel(); CancelWeightJobs();
        VerifyWithRetailClipCommand?.Cancel();
        FitContactCommand?.Cancel();
        DetectHandCommand?.Cancel();
        BuildHandRigCommand?.Cancel();
        PreviewHandBindingCommand?.Cancel();
        ApplyHandBindingCommand?.Cancel();
        if (StudioStage != RigStudioStage.HelpersAndHooks) DoctorPreviewEnabled = false;
        if (StudioStage != RigStudioStage.Skin) WeightBrushEnabled = false;
        if (StudioStage is not (RigStudioStage.Skin or RigStudioStage.Animate)) StopStressReview();
    }
    private void RouteStudioAction(RigStudioStage stage)
    {
        _routingStudioAction = true;
        try { StudioStage = stage; } finally { _routingStudioAction = false; }
    }
    partial void OnStudioStageChanging(RigStudioStage value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }
    partial void OnStudioStageChanged(RigStudioStage value)
    {
        if (!_restoringStudio)
        {
            if (!_routingStudioAction) CancelStudioInteractions();
            if (_model is { } model && model.Package.Document.RiggingSession is { } session)
                StudioMetadataRequested?.Invoke(this, new(model, RiggingSessions.Navigate(session, value), false));
        }
        NotifyStudioWorkflow();
    }
    partial void OnSelectedStudioEntryChanged(StudioEntryChoice? value) => StartStudioCommand?.NotifyCanExecuteChanged();
    private void NotifyStudioWorkflow()
    {
        foreach (var property in new[] { nameof(SelectedStudioStage), nameof(IsStudioImport), nameof(IsStudioDetect), nameof(IsStudioFit), nameof(IsStudioHelpers), nameof(IsStudioSkin), nameof(IsStudioAnimate), nameof(IsStudioVerify),
            nameof(CanNavigateStudio), nameof(HasStudioSession), nameof(HasRiggedStudioSource), nameof(HasImportedRiggedStudioSource), nameof(StudioStagePurpose), nameof(StudioSourceSummary), nameof(StudioSessionSummary), nameof(StudioReviewSummary), nameof(StudioProfileSummary) }) OnPropertyChanged(property);
        StartStudioCommand?.NotifyCanExecuteChanged(); PreviousStudioStageCommand?.NotifyCanExecuteChanged(); NextStudioStageCommand?.NotifyCanExecuteChanged();
        RecordStudioReviewCommand?.NotifyCanExecuteChanged(); OpenRiggedMappingCommand?.NotifyCanExecuteChanged();
    }
}
