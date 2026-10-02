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
public sealed class StudioEntryPathChangeEventArgs(FbxModelAuthoringImportResult model, RiggingJobToken expectedToken, RigStudioEntryPath entryPath) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingJobToken ExpectedToken { get; } = expectedToken;
    public RigStudioEntryPath EntryPath { get; } = entryPath;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private RigStudioStage _studioStage;
    [ObservableProperty] private StudioEntryChoice? _selectedStudioEntry;
    [ObservableProperty] private bool _isAdvancedSetupMode;
    private bool _restoringStudio;
    private bool _routingStudioAction;
    private Guid? _studioModelId;
    public ModelsWorkspaceViewModel? StudioWorkspace { get; private set; }
    public IReadOnlyList<StudioStageChoice> StudioStages { get; } = [
        new(RigStudioStage.Import, "1 Import", "Choose a workflow and select model parts."),
        new(RigStudioStage.Detect, "2 Detect", "Find joints or match the existing rig to a reference."),
        new(RigStudioStage.Fit, "3 Fit", "Review joint positions, mapping, and proportions."),
        new(RigStudioStage.HelpersAndHooks, "4 Helpers", "Add contacts, eyes, grips, and camera helpers."),
        new(RigStudioStage.Skin, "5 Skin", "Review weights and deformation."),
        new(RigStudioStage.Animate, "6 Animate", "Play stock clips and check the fit."),
        new(RigStudioStage.VerifyAndExport, "7 Export", "Build the model and check the exported files."),
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
    public string SetupModeLabel => IsAdvancedSetupMode ? "Advanced setup" : "Normal setup";
    public bool IsNormalRiggedSetup => !IsAdvancedSetupMode && HasRiggedStudioSource;
    public bool IsNormalUnriggedSetup => !IsAdvancedSetupMode && HasModel && !HasRiggedStudioSource;
    public bool HasStudioSession => _model?.Package.Document.RiggingSession is not null;
    public bool HasRiggedStudioSource => _model is { } model && !model.Package.Document.Bones.IsEmpty;
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
            if (_model?.Package.Document.RiggingSession is not { } session) return "Start setup before recording a review.";
            var stage = session.Stages.Single(s => s.Stage == StudioStage);
            return stage.ReviewedUtc is { } reviewed ? $"Reviewed {reviewed:u}." : "This stage has no current review.";
        }
    }
    public string StudioProfileSummary => _model?.Package.Document.RiggingSession?.Recipe.Profile is { } profile
        ? $"Profile reference: {profile.Id} / {profile.Version}. Capability completeness requires its own evidence."
        : "No runtime capability profile is selected. A fitting template alone does not establish helper or gameplay completeness.";
    public string StudioStartReason => !HasModel
        ? "Import or open a model before starting a studio session."
        : HasStudioSession
            ? "A studio session is already saved for this model."
            : IsBusy
                ? "Finish the current model job before starting a session."
                : SelectedStudioEntry is null
                    ? "Choose Repair or Adapt above, then start the studio session. A runtime capability profile is not required to begin."
                    : $"Ready to start: {SelectedStudioEntry.Label}.";
    public IRelayCommand StartStudioCommand { get; private set; } = null!;
    public IRelayCommand StartAdaptStudioCommand { get; private set; } = null!;
    public IRelayCommand StartRepairStudioCommand { get; private set; } = null!;
    public IRelayCommand StartAutoRigStudioCommand { get; private set; } = null!;
    public IRelayCommand PreviousStudioStageCommand { get; private set; } = null!;
    public IRelayCommand NextStudioStageCommand { get; private set; } = null!;
    public IRelayCommand RecordStudioReviewCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedMappingCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedRefineCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedTargetCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedScaleCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedVerifyCommand { get; private set; } = null!;
    public IRelayCommand OpenRiggedCheckCommand { get; private set; } = null!;
    public event EventHandler<StudioMetadataEventArgs>? StudioMetadataRequested;
    public event EventHandler<StudioEntryPathChangeEventArgs>? StudioEntryPathChangeRequested;

    private void InitializeStudioWorkflow()
    {
        StartStudioCommand = new RelayCommand(StartStudio, () => HasModel && !HasStudioSession && SelectedStudioEntry is not null && !IsBusy);
        StartAdaptStudioCommand = new RelayCommand(
            () => StartStudioWithEntry(RigStudioEntryPath.AdaptExistingRig),
            () => CanStartStudioWithEntry(RigStudioEntryPath.AdaptExistingRig));
        StartRepairStudioCommand = new RelayCommand(
            () => StartStudioWithEntry(RigStudioEntryPath.RepairExistingRig),
            () => CanStartStudioWithEntry(RigStudioEntryPath.RepairExistingRig));
        StartAutoRigStudioCommand = new RelayCommand(
            () => StartStudioWithEntry(RigStudioEntryPath.AutoRigBiped),
            () => CanStartStudioWithEntry(RigStudioEntryPath.AutoRigBiped));
        PreviousStudioStageCommand = new RelayCommand(() => StudioStage = (RigStudioStage)((int)StudioStage - 1), () => CanNavigateStudio && StudioStage > RigStudioStage.Import);
        NextStudioStageCommand = new RelayCommand(() => StudioStage = (RigStudioStage)((int)StudioStage + 1), () => CanNavigateStudio && StudioStage < RigStudioStage.VerifyAndExport);
        RecordStudioReviewCommand = new RelayCommand(() => {
            if (_model is not { } model || model.Package.Document.RiggingSession is not { } session || !session.MatchesSource(model.Package.Document.Source.ContentSha256)) return;
            var reviewed = RiggingSessions.RecordReview(session, StudioStage, session.ComputeInputFingerprint(), DateTimeOffset.UtcNow);
            StudioMetadataRequested?.Invoke(this, new(model, reviewed, true));
        }, () => HasStudioSession && !IsBusy && _model!.Package.Document.RiggingSession!.MatchesSource(_model.Package.Document.Source.ContentSha256));
        OpenRiggedMappingCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Mapping; }, () => HasRiggedStudioSource);
        OpenRiggedRefineCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Refine; }, () => HasImportedRiggedStudioSource);
        OpenRiggedTargetCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Target; }, () => HasImportedRiggedStudioSource);
        OpenRiggedScaleCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Scale; }, () => HasImportedRiggedStudioSource);
        OpenRiggedVerifyCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Verify; }, () => HasImportedRiggedStudioSource);
        OpenRiggedCheckCommand = new RelayCommand(() => { StudioStage = RigStudioStage.Fit; Stage = RigConformanceStage.Verify; }, () => HasImportedRiggedStudioSource && !IsBusy);
    }

    internal void SetStudioWorkspace(ModelsWorkspaceViewModel workspace)
    { StudioWorkspace = workspace; workspace.BindStudioEntryPathChange(this); OnPropertyChanged(nameof(StudioWorkspace)); }

    private void StartStudio()
    {
        if (_model is not { } model || model.Package.Document.RiggingSession is not null || SelectedStudioEntry is not { } selected) return;
        var pendingSession = _bodyDetectionWork?.Session ?? _weightSnapshot?.Session;
        if ((_bodyDetectionWork is not null || _weightPreview is not null) && pendingSession?.EntryPath != selected.Path)
        { _setStatus("Apply or discard the pending authoring proposal before starting a different workflow."); return; }
        var session = (pendingSession?.EntryPath == selected.Path
            ? pendingSession
            : CreateStudioSession(model.Package.Document, selected.Path)) with { Stage = StudioStage };
        StudioMetadataRequested?.Invoke(this, new(model, session, true));
    }

    private static RiggingSession CreateStudioSession(
        ReAnimated.Core.ModelAuthoring.CustomModelDocument document,
        RigStudioEntryPath path)
    {
        RiggingSession session = RiggingSessions.Create(document, path);
        return session with
        {
            Components = session.Components
                .Select(component => ClassifyDefaultComponent(component, session.Components.Length == 1))
                .ToImmutableArray(),
        };
    }

    private static RigGeometryComponent ClassifyDefaultComponent(
        RigGeometryComponent component,
        bool isOnlyComponent)
    {
        string name = component.DisplayName;
        if (ContainsAny(name, "accessory", "weapon", "shield", "sword", "gun", "prop", "backpack", "pouch", "holster", "hat", "helmet", "hair"))
        {
            return component with
            {
                Kind = RigGeometryComponentKind.Accessory,
                UseForAnatomy = false,
            };
        }

        if (ContainsAny(name, "cloth", "clothing", "outfit", "shirt", "pants", "trouser", "skirt", "dress", "coat", "jacket", "armor", "glove", "boot", "shoe", "sleeve"))
        {
            return component with
            {
                Kind = RigGeometryComponentKind.Clothing,
                UseForAnatomy = true,
            };
        }

        if (isOnlyComponent || ContainsAny(name, "body", "torso", "skin", "upperbody", "lowerbody", "head", "face", "arm", "leg", "hand", "foot"))
        {
            return component with
            {
                Kind = RigGeometryComponentKind.Body,
                UseForAnatomy = true,
            };
        }

        // Unknown names remain included in geometry evidence until an author
        // explicitly classifies them. This avoids silently dropping anatomy.
        return component;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private bool CanStartStudioWithEntry(RigStudioEntryPath path) =>
        HasModel && !HasStudioSession && !IsBusy &&
        StudioEntryChoices.Any(choice => choice.Path == path);

    private void StartStudioWithEntry(RigStudioEntryPath path)
    {
        StudioEntryChoice? choice = StudioEntryChoices.FirstOrDefault(
            candidate => candidate.Path == path);
        if (choice is null || !CanStartStudioWithEntry(path))
        {
            return;
        }

        SelectedStudioEntry = choice;
        StartStudio();
        if (HasStudioSession)
        {
            StudioStage = RigStudioStage.Detect;
        }
    }

    private void RestoreStudioWorkflow()
    {
        bool sameModel = _studioModelId == _model?.Package.Document.ModelId;
        _studioModelId = _model?.Package.Document.ModelId;
        _restoringStudio = true;
        StudioStage = _model?.Package.Document.RiggingSession?.Stage ?? (sameModel ? StudioStage : RigStudioStage.Import);
        StudioEntryChoices.Clear();
        if (_model?.Package.Document.RiggingSession is { } session)
        {
            if (session.EntryPath == RigStudioEntryPath.AutoRigBiped)
                StudioEntryChoices.Add(new(RigStudioEntryPath.AutoRigBiped, EntryLabel(RigStudioEntryPath.AutoRigBiped)));
            else
            {
                StudioEntryChoices.Add(new(RigStudioEntryPath.RepairExistingRig, EntryLabel(RigStudioEntryPath.RepairExistingRig)));
                StudioEntryChoices.Add(new(RigStudioEntryPath.AdaptExistingRig, EntryLabel(RigStudioEntryPath.AdaptExistingRig)));
            }
        }
        else if (_model is { } sourceModel && sourceModel.Package.Document.Bones.IsEmpty) StudioEntryChoices.Add(new(RigStudioEntryPath.AutoRigBiped, "Build a rig from geometry"));
        else if (HasModel)
        {
            StudioEntryChoices.Add(new(RigStudioEntryPath.RepairExistingRig, "Repair an existing DL rig"));
            StudioEntryChoices.Add(new(RigStudioEntryPath.AdaptExistingRig, "Adapt a rigged model"));
        }
        SelectedStudioEntry = _model?.Package.Document.RiggingSession is { } activeSession
            ? StudioEntryChoices.FirstOrDefault(c => c.Path == activeSession.EntryPath)
            : StudioEntryChoices.Count == 1 ? StudioEntryChoices[0] : sameModel ? StudioEntryChoices.FirstOrDefault(c => c.Path == SelectedStudioEntry?.Path) : null;
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
        // A newly started session creates the stable node identities consumed by
        // the channel editor. Refresh that editor now; navigation-only metadata
        // changes must leave any in-progress channel review intact.
        if (previous.Package.Document.RiggingSession is null &&
            current.Package.Document.RiggingSession is not null)
            RestoreChannelPolicies();
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
        RefreshCameraMetadata(current);
        RefreshStructuralMetadata(current);
        RefreshCapabilityProfileMetadata(current);
        RefreshSetupMetadata(current);
        RefreshStressReviewModel();
        RestoreStudioWorkflow();
    }

    private void CancelStudioInteractions()
    {
        DeriveMotionCommand?.Cancel(); LoadDoctorRulesCommand?.Cancel(); RunDoctorCommand?.Cancel();
        PreviewCameraCalibrationCommand?.Cancel(); PreviewCameraCreationCommand?.Cancel();
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
    partial void OnSelectedStudioEntryChanged(StudioEntryChoice? value)
    {
        OnPropertyChanged(nameof(StudioStartReason));
        StartStudioCommand?.NotifyCanExecuteChanged();
        StartAdaptStudioCommand?.NotifyCanExecuteChanged();
        StartRepairStudioCommand?.NotifyCanExecuteChanged();
        StartAutoRigStudioCommand?.NotifyCanExecuteChanged();
        if (_restoringStudio || value is null || _model is not { } model || model.Package.Document.RiggingSession is not { } session || session.EntryPath == value.Path) return;
        if (value.Path is not (RigStudioEntryPath.RepairExistingRig or RigStudioEntryPath.AdaptExistingRig)) return;
        StudioEntryPathChangeRequested?.Invoke(this, new(model, session.CreateJobToken(), value.Path));
    }
    private void NotifyStudioWorkflow()
    {
        foreach (var property in new[] { nameof(SelectedStudioStage), nameof(IsStudioImport), nameof(IsStudioDetect), nameof(IsStudioFit), nameof(IsStudioHelpers), nameof(IsStudioSkin), nameof(IsStudioAnimate), nameof(IsStudioVerify),
            nameof(CanNavigateStudio), nameof(HasStudioSession), nameof(HasRiggedStudioSource), nameof(HasImportedRiggedStudioSource), nameof(IsNormalRiggedSetup), nameof(IsNormalUnriggedSetup), nameof(StudioStagePurpose), nameof(StudioSourceSummary), nameof(StudioSessionSummary), nameof(StudioReviewSummary), nameof(StudioProfileSummary), nameof(StudioStartReason), nameof(SetupModeLabel) }) OnPropertyChanged(property);
        StartStudioCommand?.NotifyCanExecuteChanged(); PreviousStudioStageCommand?.NotifyCanExecuteChanged(); NextStudioStageCommand?.NotifyCanExecuteChanged();
        StartAdaptStudioCommand?.NotifyCanExecuteChanged(); StartRepairStudioCommand?.NotifyCanExecuteChanged();
        StartAutoRigStudioCommand?.NotifyCanExecuteChanged();
        RecordStudioReviewCommand?.NotifyCanExecuteChanged(); OpenRiggedMappingCommand?.NotifyCanExecuteChanged();
        OpenRiggedCheckCommand?.NotifyCanExecuteChanged();
        OpenRiggedRefineCommand?.NotifyCanExecuteChanged();
        OpenRiggedTargetCommand?.NotifyCanExecuteChanged(); OpenRiggedScaleCommand?.NotifyCanExecuteChanged(); OpenRiggedVerifyCommand?.NotifyCanExecuteChanged();
    }

    partial void OnIsAdvancedSetupModeChanged(bool value)
    {
        OnPropertyChanged(nameof(SetupModeLabel));
        OnPropertyChanged(nameof(VisibleMappings));
        OnPropertyChanged(nameof(MissingCoreRolesMessage));
        OnPropertyChanged(nameof(IsNormalRiggedSetup));
        OnPropertyChanged(nameof(IsNormalUnriggedSetup));
    }
}
