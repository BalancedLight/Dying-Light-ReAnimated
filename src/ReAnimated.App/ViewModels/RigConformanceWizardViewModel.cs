using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

public enum RigConformanceStage
{
    Target = 0,
    Mapping = 1,
    Scale = 2,
    Refine = 3,
    Verify = 4,
}

/// <summary>One reviewable correspondence row surfaced to the mapping table.</summary>
public sealed partial class RigConformanceMappingItemViewModel : ObservableObject
{
    private readonly Func<string, string, bool> _applyRoleOverride;
    private string _selectedSourceName;

    public RigConformanceMappingItemViewModel(
        RigCorrespondenceRow row,
        ImmutableArray<string> candidates,
        Func<string, string, bool> applyRoleOverride,
        bool needsSourceMatch = false)
    {
        Row = row;
        Candidates = candidates;
        _applyRoleOverride = applyRoleOverride;
        _selectedSourceName = row.SourceName ?? string.Empty;
        NeedsSourceMatch = needsSourceMatch;
    }

    public RigCorrespondenceRow Row { get; }

    public ImmutableArray<string> Candidates { get; }

    public string Name => Row.Name;

    public string DisplayName => Row.Role switch
    {
        "body.root" => $"Motion root ({Row.Name})",
        "body.pelvis" => $"Pelvis ({Row.Name})",
        _ => Row.Name,
    };

    public bool HasGeneratedMotionRoot => Row.Role == "body.root" &&
        Row.Disposition == RigBoneDisposition.Synthesized && !NeedsSourceMatch;

    public string GeneratedMotionRootSummary =>
        HasGeneratedMotionRoot
            ? "A separate DL1 motion root will be generated above the fitted body. This generated node does not need a source-joint match."
            : string.Empty;

    public string Disposition => Row.Disposition.ToString();

    public string SourceName => Row.SourceName ?? "-";

    public string Role => Row.Role ?? "-";

    public string Confidence =>
        Row.Confidence.ToString("P0", CultureInfo.CurrentCulture);

    public string Evidence => Row.Evidence;

    public bool IsAmbiguous => Row.WasAmbiguous;

    public bool NeedsSourceMatch { get; }

    public bool CanChooseSource =>
        Row.TemplateIndex >= 0 && Candidates.Length > 0 && Row.Role is not null;

    /// <summary>
    /// The chosen source bone for an ambiguous role. Setting it records an
    /// explicit override and re-solves.
    /// </summary>
    public string SelectedSourceName
    {
        get => _selectedSourceName;
        set
        {
            if (Row.Role is not { } role ||
                string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string previous = _selectedSourceName;
            if (!SetProperty(ref _selectedSourceName, value))
            {
                return;
            }

            if (!_applyRoleOverride(role, value))
            {
                SetProperty(ref _selectedSourceName, previous);
            }
        }
    }
}

/// <summary>One step of the guided refine sequence, bound to a real fitted joint.</summary>
public sealed partial class RigConformanceLandmarkViewModel : ObservableObject
{
    [ObservableProperty]
    private double _segmentRatio = 1.0;

    [ObservableProperty]
    private double _movedCentimetres;

    [ObservableProperty]
    private bool _isAdjusted;

    [ObservableProperty]
    private bool _isResolved;

    public RigConformanceLandmarkViewModel(
        string boneName,
        string label,
        string instruction,
        string? mirrorBoneName)
    {
        BoneName = boneName;
        Label = label;
        Instruction = instruction;
        MirrorBoneName = mirrorBoneName;
    }

    public string BoneName { get; }

    public string Label { get; }

    public string Instruction { get; }

    /// <summary>The opposite-side joint, when mirroring applies.</summary>
    public string? MirrorBoneName { get; }

    /// <summary>
    /// Traffic-light severity against the solver's 15% warning threshold.
    /// </summary>
    public string Severity =>
        !IsResolved ? "Missing"
        : Math.Abs(SegmentRatio - 1.0) > 0.15 ? "Warning"
        : Math.Abs(SegmentRatio - 1.0) > 0.05 ? "Caution"
        : "Good";

    public string Summary => IsResolved
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"{SegmentRatio:P0} of DL1 rest length; moved {MovedCentimetres:F1} cm")
        : "not present on this rig";

    internal void Update(RigConformedBone? bone, bool adjusted)
    {
        IsResolved = bone is not null;
        IsAdjusted = adjusted;
        SegmentRatio = bone?.SegmentRatio ?? 1.0;
        MovedCentimetres = (bone?.OffsetFromSourceJoint ?? 0.0) * 100.0;
        OnPropertyChanged(nameof(Severity));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>
/// How far a conversion goes toward DL1's proportions. Bone directions always
/// come from DL1's rest pose regardless; only segment lengths differ.
/// </summary>
public enum RigConformanceFitMode
{
    /// <summary>
    /// DL1's rest pose exactly, at one fitted uniform scale. Stock clips play
    /// undistorted; the character's limb proportions become DL1's.
    /// </summary>
    MatchDl1Exactly = 0,

    /// <summary>
    /// DL1's rest directions with the model's own segment lengths. The
    /// silhouette is preserved, but stock clips still stretch limbs toward
    /// DL1's lengths because they key per-bone translation.
    /// </summary>
    PreserveSourceProportions = 1,

    /// <summary>A blend between the two.</summary>
    Custom = 2,
}

/// <summary>One selectable fit mode, with the label shown in the wizard.</summary>
public sealed record RigConformanceFitModeChoice(
    RigConformanceFitMode Mode,
    string Label);

/// <summary>One selectable scale policy, with the label shown in the wizard.</summary>
public sealed record RigConformanceScaleModeChoice(
    CustomModelConformanceScaleMode Mode,
    string Label);

/// <summary>
/// Drives the guided conversion of an imported rig onto a DL1 target skeleton.
/// </summary>
/// <remarks>
/// <para>
/// The view model owns only decisions - template profile, mapping overrides,
/// scale policy, conformance strength and manual joint placements. The
/// conformed rig is always re-derived from those decisions by the solvers in
/// <c>ReAnimated.Retargeting.Conformance</c>, so nothing here can drift from
/// what an export would produce.
/// </para>
/// <para>
/// Fitting stages preview the conformed <em>skeleton</em> over the unchanged
/// source mesh. That is deliberate: while placing joints the mesh must hold
/// still so the user can see whether bones sit inside it, and it avoids
/// re-transferring skin weights on every slider tick.
/// </para>
/// </remarks>
public sealed partial class RigConformanceWizardViewModel : ObservableObject
{
    /// <summary>
    /// The guided sequence, in the order a user should work through it: the
    /// spine first because everything hangs off it, then limbs outward.
    /// </summary>
    private static readonly (string Bone, string Label, string Instruction, string? Mirror)[]
        LandmarkSequence =
        [
            ("pelvis", "Pelvis", "Place at the hip joint centre, level with the tops of the thigh bones.", null),
            ("spine1", "Lower spine", "Place just above the waist, where the torso starts to bend.", null),
            ("spine2", "Chest", "Place at the base of the rib cage.", null),
            ("neck", "Neck", "Place at the base of the neck, between the collarbones.", null),
            ("head", "Head", "Place at the skull base, where the head pivots on the neck.", null),
            ("l_clavicle", "Left collarbone", "Place at the inner end of the collarbone.", "r_clavicle"),
            ("l_upperarm", "Left shoulder", "Place at the shoulder joint centre.", "r_upperarm"),
            ("l_forearm", "Left elbow", "Place at the elbow joint centre.", "r_forearm"),
            ("l_hand", "Left wrist", "Place at the wrist joint centre.", "r_hand"),
            ("r_clavicle", "Right collarbone", "Place at the inner end of the collarbone.", "l_clavicle"),
            ("r_upperarm", "Right shoulder", "Place at the shoulder joint centre.", "l_upperarm"),
            ("r_forearm", "Right elbow", "Place at the elbow joint centre.", "l_forearm"),
            ("r_hand", "Right wrist", "Place at the wrist joint centre.", "l_hand"),
            ("l_thigh", "Left hip", "Place at the hip joint centre.", "r_thigh"),
            ("l_calf", "Left knee", "Place at the knee joint centre.", "r_calf"),
            ("l_foot", "Left ankle", "Place at the ankle joint centre.", "r_foot"),
            ("l_toebase", "Left toe", "Place at the ball of the foot.", "r_toebase"),
            ("r_thigh", "Right hip", "Place at the hip joint centre.", "l_thigh"),
            ("r_calf", "Right knee", "Place at the knee joint centre.", "l_calf"),
            ("r_foot", "Right ankle", "Place at the ankle joint centre.", "l_foot"),
            ("r_toebase", "Right toe", "Place at the ball of the foot.", "l_toebase"),
        ];

    private readonly Func<string, CancellationToken, Task<Dl1RigTemplateResolution>> _resolveTemplate;
    private readonly Action<string> _setStatus;
    private IRigConformanceSolveScheduler _solveScheduler;
    private CancellationTokenSource? _fitSolveCancellation;
    private long _fitInputRevision;
    private long _publishedFitRevision = -1;

    private FbxModelAuthoringImportResult? _model;
    private Dl1RigTemplate? _template;
    private ImmutableDictionary<string, string> _roleOverrides =
        ImmutableDictionary<string, string>.Empty;
    private MappingEditSnapshot? _previousMappingEdit;
    private bool _sourceConformanceReviewRequired;
    // The saved conformance describes the original FBX source. When the exact
    // applied output is reopened, retain that record as provenance while the
    // interactive solver works from the current DL rig instead.
    private bool _editingAppliedOutputRig;
    private sealed record MappingEditSnapshot(
        ImmutableDictionary<string, string> Roles,
        ImmutableDictionary<string, Vector3D>? Positions,
        bool RequiresSourceReview);
    private sealed record PendingRoleOverride(
        ImmutableDictionary<string, string> RolesBefore,
        MappingEditSnapshot? PreviousUndo,
        int MappedBefore,
        int CoreBefore,
        string Role,
        string SourceBoneName,
        bool AllowMajorLoss);
    private PendingRoleOverride? _pendingRoleOverride;
    private ImmutableDictionary<string, Vector3D> _positionOverrides =
        ImmutableDictionary<string, Vector3D>.Empty;
    private ImmutableDictionary<string, Vector3D>? _previousPositionOverrides;

    [ObservableProperty]
    private RigConformanceStage _stage = RigConformanceStage.Target;

    [ObservableProperty]
    private string _templateProfileName = "player";

    [ObservableProperty]
    private string _templateStatus = "No target skeleton resolved yet.";

    [ObservableProperty]
    private bool _keepExtraBones = true;

    [ObservableProperty]
    private CustomModelConformanceScaleMode _scaleMode =
        CustomModelConformanceScaleMode.Automatic;

    [ObservableProperty]
    private double _manualScale = 1.0;

    [ObservableProperty]
    private double _conformanceStrength;

    [ObservableProperty]
    private bool _useGeometryCorrespondence = true;

    private RigGeometryEvidence? _geometryEvidence;
    private bool _geometryEvidenceCaptured;
    private bool _settingModel;

    [ObservableProperty]
    private bool _mirrorEdits = true;

    [ObservableProperty]
    private RigConformanceLandmarkViewModel? _selectedLandmark;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _solveStatus = "Import a model to begin.";

    [ObservableProperty]
    private string _mappingEditStatus = string.Empty;

    private string? _guidedMappingConflictTargetRole;
    private string? _guidedMappingConflictOwnerRole;
    private string? _guidedMappingConflictSourceName;

    public RigConformanceWizardViewModel(
        Func<string, CancellationToken, Task<Dl1RigTemplateResolution>> resolveTemplate,
        Action<string> setStatus)
        : this(resolveTemplate, setStatus, new ThreadPoolRigConformanceSolveScheduler())
    {
    }

    internal RigConformanceWizardViewModel(
        Func<string, CancellationToken, Task<Dl1RigTemplateResolution>> resolveTemplate,
        Action<string> setStatus,
        IRigConformanceSolveScheduler solveScheduler)
    {
        _resolveTemplate = resolveTemplate ?? throw new ArgumentNullException(nameof(resolveTemplate));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _solveScheduler = solveScheduler ?? throw new ArgumentNullException(nameof(solveScheduler));

        foreach ((string bone, string label, string instruction, string? mirror) in LandmarkSequence)
        {
            Landmarks.Add(new RigConformanceLandmarkViewModel(bone, label, instruction, mirror));
        }

        ResolveTemplateCommand = new AsyncRelayCommand(
            ResolveTemplateAsync,
            () => !IsBusy);
        ResetBoneCommand = new RelayCommand(
            ResetSelectedBone,
            () => SelectedJointBoneName is { } selectedBone && HasOverride(selectedBone));
        ResetAllCommand = new RelayCommand(
            ResetAllOverrides,
            () => !_sourceConformanceReviewRequired && !_positionOverrides.IsEmpty);
        UndoLastJointPlacementCommand = new RelayCommand(
            UndoLastJointPlacement,
            () => _previousPositionOverrides is not null && !IsBusy);
        NextStageCommand = new RelayCommand(
            () => Stage = (RigConformanceStage)((int)Stage + 1),
            () => Stage < RigConformanceStage.Verify && CanAdvance);
        PreviousStageCommand = new RelayCommand(
            () => Stage = (RigConformanceStage)((int)Stage - 1),
            () => Stage > RigConformanceStage.Target);
        ApplyConformanceCommand = new RelayCommand(
            () => ApplyRequested?.Invoke(this, EventArgs.Empty),
            () => CanApply && !IsBusy);
        AcceptMappingProposalsCommand = new RelayCommand(AcceptMappingProposals, () => HasPendingMappingReview && !IsBusy);
        RerunAutomaticMatchingCommand = new RelayCommand(
            RerunAutomaticMatching,
            () => CanAdvance && !IsBusy);
        UndoLastMappingChangeCommand = new RelayCommand(
            UndoLastMappingChange,
            () => _previousMappingEdit is not null && !IsBusy);
        InitializeBodyDetection();
        InitializeChannelPolicies();
        InitializeContacts();
        InitializeHands();
        InitializeEyes();
        InitializeCameras();
        InitializeStructural();
        InitializeCapabilityProfile();
        InitializeSetupTransfer();
        InitializeRetailReferenceSelection();
        InitializeRestPose();
        InitializeHierarchy();
        InitializeDerivedMotion();
        InitializeDoctor();
        InitializeWeightEditing();
        InitializeStressReview();
        InitializeStudioWorkflow();
    }

    /// <summary>
    /// Raised when the author asks to commit the conformance. The workspace
    /// owns the document mutation and its undo entry, so the wizard only asks.
    /// </summary>
    public event EventHandler? ApplyRequested;

    /// <summary>Raised whenever a re-solve produced a new fit to preview.</summary>
    public event EventHandler? FitChanged;

    public ObservableCollection<RigConformanceMappingItemViewModel> Mappings { get; } = [];

    public IReadOnlyList<RigConformanceMappingItemViewModel> VisibleMappings =>
        IsAdvancedSetupMode
            ? Mappings.ToArray()
            : Mappings.Where(row => row.Row.TemplateIndex >= 0 &&
                row.Role is "body.root" ||
                row.Row.TemplateIndex >= 0 &&
                CoreRoles.Contains(row.Role, StringComparer.Ordinal))
                .ToArray();

    /// <summary>
    /// Rows needed to resolve a missing normal-flow target. If the last choice
    /// was rejected because its source is already reserved, also expose the
    /// owning row so the author can free that source without opening Advanced.
    /// </summary>
    public IReadOnlyList<RigConformanceMappingItemViewModel> GuidedMappingReviewRows
    {
        get
        {
            var rows = Mappings.Where(row => row.Row.TemplateIndex >= 0 &&
                    row.Row.Disposition != RigBoneDisposition.Mapped &&
                    row.Role is { } role && MissingCoreRoles.Contains(role, StringComparer.Ordinal))
                .ToList();

            if (_guidedMappingConflictOwnerRole is { } ownerRole &&
                _guidedMappingConflictSourceName is { } sourceName &&
                _roleOverrides.TryGetValue(ownerRole, out string? assignedName) &&
                string.Equals(assignedName, sourceName, StringComparison.OrdinalIgnoreCase))
            {
                RigConformanceMappingItemViewModel? owner = Mappings.FirstOrDefault(row =>
                    string.Equals(row.Role, ownerRole, StringComparison.Ordinal) &&
                    string.Equals(row.SourceName, sourceName, StringComparison.OrdinalIgnoreCase));
                if (owner is not null && !rows.Contains(owner))
                {
                    rows.Add(owner);
                }
            }

            return rows;
        }
    }

    /// <summary>Other rows shown in the normal-flow mapping review expander.</summary>
    public IReadOnlyList<RigConformanceMappingItemViewModel> GuidedEditableMappingRows =>
        VisibleMappings.Where(row => !GuidedMappingReviewRows.Contains(row)).ToArray();

    public bool HasGuidedMappingConflict =>
        _guidedMappingConflictTargetRole is not null &&
        _guidedMappingConflictOwnerRole is not null &&
        _guidedMappingConflictSourceName is not null;

    public ObservableCollection<RigConformanceLandmarkViewModel> Landmarks { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public IAsyncRelayCommand ResolveTemplateCommand { get; }

    public IRelayCommand ResetBoneCommand { get; }

    public IRelayCommand AcceptMappingProposalsCommand { get; }

    public IRelayCommand RerunAutomaticMatchingCommand { get; }

    public IRelayCommand UndoLastMappingChangeCommand { get; }

    public IRelayCommand ResetAllCommand { get; }

    public IRelayCommand UndoLastJointPlacementCommand { get; }

    public IRelayCommand NextStageCommand { get; }

    public IRelayCommand PreviousStageCommand { get; }

    public IRelayCommand ApplyConformanceCommand { get; }

    /// <summary>
    /// Scale policies offered in the wizard. Legs are called out because
    /// locomotion clips plant the feet at template proportions.
    /// </summary>
    public ImmutableArray<RigConformanceScaleModeChoice> ScaleModeChoices { get; } =
    [
        new(CustomModelConformanceScaleMode.Automatic, "All limb and torso segments"),
        new(CustomModelConformanceScaleMode.Leg, "Legs only (keeps feet planted)"),
        new(CustomModelConformanceScaleMode.Torso, "Torso only"),
        new(CustomModelConformanceScaleMode.Arm, "Arms only"),
        new(CustomModelConformanceScaleMode.Manual, "Manual value"),
    ];

    /// <summary>The selected scale policy, as the combo box binds it.</summary>
    public RigConformanceScaleModeChoice? SelectedScaleModeChoice
    {
        get => ScaleModeChoices.FirstOrDefault(choice => choice.Mode == ScaleMode);
        set
        {
            if (value is not null && value.Mode != ScaleMode)
            {
                ScaleMode = value.Mode;
            }
        }
    }

    public bool IsManualScale => ScaleMode == CustomModelConformanceScaleMode.Manual;

    public ImmutableArray<RigConformanceFitModeChoice> FitModeChoices { get; } =
    [
        new(RigConformanceFitMode.PreserveSourceProportions, "Keep this model's proportions"),
        new(RigConformanceFitMode.MatchDl1Exactly, "Use DL1 proportions"),
    ];

    /// <summary>
    /// Derived from <see cref="ConformanceStrength"/> rather than stored, so the
    /// two can never disagree.
    /// </summary>
    public RigConformanceFitMode FitMode =>
        ConformanceStrength >= 0.999 ? RigConformanceFitMode.MatchDl1Exactly
        : ConformanceStrength <= 0.001 ? RigConformanceFitMode.PreserveSourceProportions
        : RigConformanceFitMode.Custom;

    public RigConformanceFitModeChoice? SelectedFitModeChoice
    {
        get => FitModeChoices.FirstOrDefault(choice => choice.Mode == FitMode);
        set
        {
            if (value is null || value.Mode == FitMode)
            {
                return;
            }

            ConformanceStrength = value.Mode switch
            {
                RigConformanceFitMode.PreserveSourceProportions => 0.0,
                _ => 1.0,
            };
        }
    }

    /// <summary>
    /// Core humanoid roles the correspondence could not find. A rig where these
    /// are missing still converts, but the result will not resemble a person
    /// and stock clips will not drive it.
    /// </summary>
    public ImmutableArray<string> MissingCoreRoles { get; private set; } = [];

    public bool HasMissingCoreRoles => !MissingCoreRoles.IsEmpty;

    public string MissingCoreRolesMessage => MissingCoreRoles.IsEmpty
        ? string.Empty
        : IsAdvancedSetupMode
            ? $"{MissingCoreRoles.Length} required {(MissingCoreRoles.Length == 1 ? "joint needs" : "joints need")} a match: {string.Join(", ", MissingCoreRoles)}."
            : $"Match the highlighted joints ({MissingCoreRoles.Length} remaining).";

    public Dl1RigTemplate? Template => _template;

    public RigCorrespondence? Correspondence { get; private set; }

    public RigLandmarkSolution? Landmark { get; private set; }

    public RigConformanceResult? Fit { get; private set; }

    public bool HasTemplate => _template is not null;

    public bool HasModel => _model is not null;

    public bool CanAdvance => HasTemplate && HasModel;

    public bool RequiresSourceRematch => _sourceConformanceReviewRequired;

    public string AutomaticMatchingLabel => RequiresSourceRematch
        ? "Re-match changed model"
        : "Re-run automatic matching";

    public bool HasPendingMappingReview => UseGeometryCorrespondence && _geometryEvidence is not null &&
        Correspondence?.Rows.Any(static r => r.Disposition == RigBoneDisposition.Mapped && r.WasAmbiguous) == true;

    public bool HasMissingAnatomicalCorrespondence => UseGeometryCorrespondence && _geometryEvidence is not null && _template is { } template &&
        Correspondence?.Rows.Any(r => r.Disposition == RigBoneDisposition.Synthesized && r.Role is not null &&
            CoreRoles.Contains(r.Role, StringComparer.Ordinal) && template[r.TemplateIndex].IsDeform) == true;

    public string MappingReviewMessage => HasMissingAnatomicalCorrespondence ? "Some anatomical roles need a source bone. Choose them in the mapping table before applying." :
        HasPendingMappingReview ? "Review the proposed mappings, then accept them or choose different source bones." : string.Empty;

    public bool CanApply => IsFitCurrent && !IsBusy && !RequiresSourceRematch && !HasPendingMappingReview && !HasMissingAnatomicalCorrespondence;
    private bool IsFitCurrent => Fit is not null &&
        Interlocked.Read(ref _publishedFitRevision) == Interlocked.Read(ref _fitInputRevision);

    /// <summary>True when a saved output rig is being edited from its current bones.</summary>
    public bool IsEditingAppliedOutputRig => _editingAppliedOutputRig;

    public int MappedCount => Correspondence?.MappedCount ?? 0;

    public int SynthesizedCount => Correspondence?.SynthesizedCount ?? 0;

    public int ExtraCount => Correspondence?.ExtraCount ?? 0;

    public int DroppedCount => Correspondence?.DroppedCount ?? 0;

    public string ScaleSummary => Landmark is { } landmark
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"Uniform scale {landmark.UniformScale:F4}; proportion spread {landmark.ProportionResidual:P1}, worst segment {landmark.WorstSampleDeviation:P1}.")
        : "No scale solved yet.";

    public ImmutableArray<RigRegionFit> RegionFits =>
        Landmark?.RegionFits ?? [];

    /// <summary>
    /// Binds the wizard to an imported model. Passing null clears every solved
    /// artifact so a stale fit cannot outlive the model it came from.
    /// </summary>
    public void SetModel(FbxModelAuthoringImportResult? model)
        => SetModelCore(model, solveSynchronously: true);

    public async Task SetModelAsync(
        FbxModelAuthoringImportResult? model,
        CancellationToken cancellationToken = default)
    {
        SetModelCore(model, solveSynchronously: false);
        if (model is null || _template is null || model.Rig is null) return;
        await StartSolveAsync(debounce: false, cancellationToken).ConfigureAwait(true);
    }

    private void SetModelCore(FbxModelAuthoringImportResult? model, bool solveSynchronously)
    {
        CancelPendingFitSolve();
        Interlocked.Increment(ref _fitInputRevision);
        Interlocked.Exchange(ref _publishedFitRevision, -1);
        IsBusy = false;
        Guid? selectedGuide = model?.Package.Document.ModelId == _model?.Package.Document.ModelId ? SelectedBodyGuideId : null;
        bool mirrorGuide = selectedGuide is not null && MirrorBodyGuide;
        bool sameSourceModel = model is not null && _model?.Package.Document.ModelId == model.Package.Document.ModelId;
        bool draftRequiresRematch = sameSourceModel && model?.Rig is not null &&
            (_sourceConformanceReviewRequired || !string.Equals(
                _model!.Package.Document.Source.ContentSha256,
                model.Package.Document.Source.ContentSha256,
                StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(_model, model)) InvalidateBodyDetection();
        bool freshModel = model is not null && _model?.Package.Document.ModelId != model.Package.Document.ModelId;
        _settingModel = true;
        try
        {
            _model = model;
            _previousMappingEdit = null;
            _previousPositionOverrides = null;
            _pendingRoleOverride = null;
            _sourceConformanceReviewRequired = false;
            _editingAppliedOutputRig = false;
            MappingEditStatus = string.Empty;
            _geometryEvidence = null;
            _geometryEvidenceCaptured = false;
            Correspondence = null;
            Landmark = null;
            Fit = null;
            if (model is null)
            {
                Mappings.Clear();
                Warnings.Clear();
                SolveStatus = "Import a model to begin.";
            }
            else if (model.Package.Document.RigConformance is { } persisted)
            {
                RestoreSettings(persisted, model.Package.Document.Source.ContentSha256);
            }
            else if (freshModel)
            {
                _roleOverrides = ImmutableDictionary<string, string>.Empty;
                _positionOverrides = ImmutableDictionary<string, Vector3D>.Empty;
                UseGeometryCorrespondence = true;
                ConformanceStrength = 0;
            }
            else
            {
                _sourceConformanceReviewRequired = draftRequiresRematch;
            }
        }
        finally { _settingModel = false; }
        RestoreStoredBodyGuides();
        RestoreBodyAuthoring();
        RestoreChannelPolicies();
        RestoreContacts();
        RestoreHands();
        RestoreEyes();
        RestoreCameras();
        RestoreStructural();
        RestoreCapabilityProfile();
        ResetSetupTransfer();
        RestoreRestPose();
        RestoreHierarchy();
        RestoreDerivedMotion();
        RestoreDoctor();
        ResetWeightEditing();
        RefreshStressReviewModel();
        RestoreStudioWorkflow();
        SelectBodyGuide(selectedGuide);
        MirrorBodyGuide = mirrorGuide && CanMirrorBodyGuide;
        if (solveSynchronously) Solve();
        NotifyStateChanged();
    }

    internal FbxModelAuthoringImportResult? UpdateWorkflowMode(CustomModelWorkflowMode workflowMode)
    {
        if (_model is not { } model)
            return null;

        if (model.Package.Document.WorkflowMode == workflowMode)
            return model;

        CustomModelDocument document = model.Package.Document with
        {
            WorkflowMode = workflowMode,
        };
        document.Validate();
        _model = model with { Package = model.Package with { Document = document } };
        return _model;
    }

    /// <summary>
    /// Replays previously authored settings. Settings solved against a
    /// different source model are reported rather than silently reused.
    /// </summary>
    private void RestoreSettings(
        CustomModelRigConformance settings,
        string? sourceFbxSha256)
    {
        TemplateProfileName = settings.TemplateProfileName;
        KeepExtraBones = !settings.DropExtraBones;
        ScaleMode = settings.ScaleMode;
        ManualScale = settings.ManualScale ?? 1.0;
        ConformanceStrength = settings.ConformanceStrength;
        UseGeometryCorrespondence = settings.CorrespondenceMethod == CustomModelCorrespondenceMethod.GeometryHierarchyV1;
        _roleOverrides = settings.RoleOverrides.ToImmutableDictionary(
            static row => row.Role,
            static row => row.SourceBoneName,
            StringComparer.Ordinal);
        _positionOverrides = settings.PositionOverrides.ToImmutableDictionary(
            static row => row.BoneName,
            static row => row.Position,
            StringComparer.OrdinalIgnoreCase);
        _sourceConformanceReviewRequired = !settings.MatchesSource(sourceFbxSha256);

        SolveStatus = settings.MatchesSource(sourceFbxSha256)
            ? "Restored the saved conformance settings."
            : "The saved conformance was solved against a different source model; review it before applying.";
    }

    /// <summary>Captures the current decisions for persistence.</summary>
    public CustomModelRigConformance? CreateSettings()
    {
        // Keep the source-space decisions that produced an applied output. New
        // edits are embodied by the current rig; they must not replace its
        // original source mapping with current-output names.
        if (_editingAppliedOutputRig)
        {
            return null;
        }

        if (_sourceConformanceReviewRequired ||
            _template is not { } template ||
            _model?.Package.Document.Source.ContentSha256 is not { } sourceHash ||
            string.IsNullOrWhiteSpace(sourceHash))
        {
            return null;
        }

        return new CustomModelRigConformance
        {
            CorrespondenceMethod = UseGeometryCorrespondence ? CustomModelCorrespondenceMethod.GeometryHierarchyV1 : CustomModelCorrespondenceMethod.LegacyNameRoles,
            TemplateId = template.TemplateId,
            TemplateProfileName = template.ProfileName,
            TemplateSourceResourceName = template.SourceResourceName,
            TemplateFingerprint = template.SourceFingerprint,
            SourceFbxSha256 = sourceHash,
            DropExtraBones = !KeepExtraBones,
            RoleOverrides = _roleOverrides
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new CustomModelConformanceRoleOverride
                {
                    Role = pair.Key,
                    SourceBoneName = pair.Value,
                })
                .ToImmutableArray(),
            ExcludedSourceBones = [],
            ScaleMode = ScaleMode,
            ManualScale = ScaleMode == CustomModelConformanceScaleMode.Manual
                ? ManualScale
                : null,
            ConformanceStrength = ConformanceStrength,
            PositionOverrides = _positionOverrides
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new CustomModelConformancePositionOverride
                {
                    BoneName = pair.Key,
                    Position = pair.Value,
                })
                .ToImmutableArray(),
        };
    }

    private async Task ResolveTemplateAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            string requested = TemplateProfileName;
            var source = _model;
            Dl1RigTemplateResolution resolution =
                await _resolveTemplate(requested, cancellationToken)
                    .ConfigureAwait(true);
            if (!ReferenceEquals(source, _model) || !requested.Equals(TemplateProfileName, StringComparison.Ordinal)) return;
            UseTemplateResolution(resolution);
            if (_model?.Rig is not null && _template is not null)
                await StartSolveAsync(debounce: false, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = _fitSolveCancellation is not null;
            NotifyStateChanged();
        }
    }

    private void UseTemplateResolution(Dl1RigTemplateResolution resolution)
    {
        _template = resolution.Template;
        _previousMappingEdit = null;
        _previousPositionOverrides = null;
        MappingEditStatus = string.Empty;
        if (_cameraPreview?.IsCreation == true) InvalidateCameraDraft();
        RefreshCameraCreation();
        TemplateStatus = resolution.Succeeded
            ? $"{resolution.ResourceName}: {resolution.Status}"
            : resolution.Status;
        _setStatus(TemplateStatus);
    }

    /// <summary>
    /// Re-derives the correspondence, scale and fit from the current decisions.
    /// Every solver refusal is reported rather than leaving a stale result on
    /// screen.
    /// </summary>
    public void Solve()
    {
        if (_settingModel) return;
        CancelPendingFitSolve();
        long revision = Interlocked.Increment(ref _fitInputRevision);
        IsBusy = false;
        if (_sourceConformanceReviewRequired)
        {
            Correspondence = null;
            Landmark = null;
            Fit = null;
            Mappings.Clear();
            OnPropertyChanged(nameof(VisibleMappings));
            RefreshLandmarks();
            RefreshWarnings();
            RefreshMissingCoreRoles();
            SolveStatus = "The previous fit belongs to a different source model. Re-match the changed model before fitting.";
            Interlocked.Exchange(ref _publishedFitRevision, revision);
            NotifyStateChanged();
            FitChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        PrepareAppliedOutputRigForSolve();

        if (_template is not { } template ||
            _model?.Rig is not { } source)
        {
            Fit = null;
            if (HasUnriggedSource && _model!.Package.Document.RigConformance is null)
                SolveStatus = "Unrigged model loaded. Review and edit its body guides.";
            Interlocked.Exchange(ref _publishedFitRevision, revision);
            NotifyStateChanged();
            return;
        }

        try
        {
            if (UseGeometryCorrespondence && !_geometryEvidenceCaptured)
            {
                var captured = FbxRigGeometryEvidence.Build(_model);
                _geometryEvidence = captured.Supports.Any(static s => s.SurfaceMass > 0) ? captured : null;
                _geometryEvidenceCaptured = true;
            }
            Correspondence = RigCorrespondenceSolver.Solve(
                template,
                source,
                new RigCorrespondenceOptions
                {
                    DropExtraBones = !KeepExtraBones,
                    RoleOverrides = _roleOverrides,
                    GeometryEvidence = UseGeometryCorrespondence ? _geometryEvidence : null,
                });
            Landmark = RigLandmarkSolver.Solve(
                template,
                source,
                Correspondence,
                new RigLandmarkOptions
                {
                    ScaleOverride = ScaleMode == CustomModelConformanceScaleMode.Manual
                        ? ManualScale
                        : null,
                    RestrictScaleToRegion = ScaleMode switch
                    {
                        CustomModelConformanceScaleMode.Leg => RigScaleRegion.Leg,
                        CustomModelConformanceScaleMode.Torso => RigScaleRegion.Torso,
                        CustomModelConformanceScaleMode.Arm => RigScaleRegion.Arm,
                        _ => null,
                    },
                });
            Fit = RigConformanceSolver.Solve(
                template,
                source,
                Correspondence,
                Landmark,
                new RigConformanceOptions
                {
                    ConformanceStrength = ConformanceStrength,
                    PositionOverrides = _positionOverrides,
                });
            SolveStatus = _editingAppliedOutputRig
                ? "Reopened the applied DL rig for editing. Original source mapping decisions remain saved; the current rig is the fit source. " +
                  $"{Fit.Bones.Length} bones - {MappedCount} mapped, {SynthesizedCount} synthesized."
                : string.Create(
                CultureInfo.CurrentCulture,
                $"{Fit.Bones.Length} bones - {MappedCount} mapped, {SynthesizedCount} synthesized, " +
                $"{ExtraCount} retained, {DroppedCount} dropped. {Fit.Warnings.Length} proportion warnings.");
            if (UseGeometryCorrespondence && _geometryEvidence is null)
                SolveStatus += " No usable surface support; only name-based proposals are available.";
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException)
        {
            Fit = null;
            Correspondence = null;
            Landmark = null;
            SolveStatus = $"The conformance could not be solved: {exception.Message}";
        }

        if (!AcceptPendingRoleOverride(new(
                Correspondence, Landmark, Fit, _geometryEvidence, _geometryEvidenceCaptured, SolveStatus)))
            return;
        Interlocked.Exchange(ref _publishedFitRevision, revision);
        RefreshMappings();
        RefreshLandmarks();
        RefreshWarnings();
        RefreshMissingCoreRoles();
        NotifyStateChanged();
        FitChanged?.Invoke(this, EventArgs.Empty);
    }

    private void QueueSolve(bool debounce = true)
    {
        if (_settingModel || _restoringStudio) return;
        _ = StartSolveAsync(debounce, CancellationToken.None);
    }

    internal void SetModelQueued(FbxModelAuthoringImportResult? model)
    {
        SetModelCore(model, solveSynchronously: false);
        if (model is not null && _template is not null && model.Rig is not null)
            QueueSolve(debounce: false);
        else
            Solve();
    }

    private async Task StartSolveAsync(bool debounce, CancellationToken cancellationToken)
    {
        if (_settingModel) return;
        PrepareAppliedOutputRigForSolve();
        CancelPendingFitSolve();
        long revision = Interlocked.Increment(ref _fitInputRevision);
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _fitSolveCancellation = linkedCancellation;
        RigConformanceSolveRequest request = CaptureSolveRequest(revision);
        IsBusy = true;
        await RunScheduledSolveAsync(request, debounce, linkedCancellation).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunScheduledSolveAsync(
        RigConformanceSolveRequest request,
        bool debounce,
        CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            if (debounce)
                await _solveScheduler.DelayAsync(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            RigConformanceSolveResult result = await _solveScheduler.SolveAsync(request, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSolve(request, token)) return;
            if (!AcceptPendingRoleOverride(result)) return;
            PublishSolveResult(result, request.Revision);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (IsCurrentSolve(request, token)) IsBusy = false;
        }
        catch (Exception exception)
        {
            if (IsCurrentSolve(request, token))
            {
                PublishSolveResult(new(null, null, null, null, false,
                    $"The conformance could not be solved: {exception.Message}"), request.Revision);
            }
        }
        finally
        {
            if (ReferenceEquals(_fitSolveCancellation, cancellation))
            {
                _fitSolveCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentSolve(RigConformanceSolveRequest request, CancellationToken token) =>
        !token.IsCancellationRequested &&
        request.Revision == Interlocked.Read(ref _fitInputRevision) &&
        ReferenceEquals(request.Model, _model);

    private RigConformanceSolveRequest CaptureSolveRequest(long revision)
    {
        FbxModelAuthoringImportResult? model = _model;
        Dl1RigTemplate? template = _template;
        bool useGeometry = UseGeometryCorrespondence;
        bool requiresSourceReview = _sourceConformanceReviewRequired;
        bool editingAppliedOutput = _editingAppliedOutputRig;
        bool keepExtraBones = KeepExtraBones;
        CustomModelConformanceScaleMode scaleMode = ScaleMode;
        double manualScale = ManualScale;
        double conformanceStrength = ConformanceStrength;
        ImmutableDictionary<string, string> roleOverrides = _roleOverrides;
        ImmutableDictionary<string, Vector3D> positionOverrides = _positionOverrides;
        RigGeometryEvidence? geometryEvidence = _geometryEvidence;
        bool geometryEvidenceCaptured = _geometryEvidenceCaptured;
        return new(revision, model, cancellationToken => ComputeSolveResult(
            model, template, useGeometry, requiresSourceReview, editingAppliedOutput,
            keepExtraBones, scaleMode, manualScale, conformanceStrength,
            roleOverrides, positionOverrides, geometryEvidence, geometryEvidenceCaptured,
            cancellationToken));
    }

    private static RigConformanceSolveResult ComputeSolveResult(
        FbxModelAuthoringImportResult? model,
        Dl1RigTemplate? template,
        bool useGeometry,
        bool requiresSourceReview,
        bool editingAppliedOutput,
        bool keepExtraBones,
        CustomModelConformanceScaleMode scaleMode,
        double manualScale,
        double conformanceStrength,
        ImmutableDictionary<string, string> roleOverrides,
        ImmutableDictionary<string, Vector3D> positionOverrides,
        RigGeometryEvidence? geometryEvidence,
        bool geometryEvidenceCaptured,
        CancellationToken cancellationToken)
    {
        if (requiresSourceReview)
        {
            return new(null, null, null, geometryEvidence, geometryEvidenceCaptured,
                "The previous fit belongs to a different source model. Re-match the changed model before fitting.");
        }
        if (template is null || model?.Rig is not { } source)
        {
            string status = model?.Rig is null && model?.Package.Document.RigConformance is null
                ? "Unrigged model loaded. Review and edit its body guides."
                : "Import a model and resolve a target skeleton to begin.";
            return new(null, null, null, geometryEvidence, geometryEvidenceCaptured, status);
        }

        try
        {
            if (useGeometry && !geometryEvidenceCaptured)
            {
                RigGeometryEvidence captured = FbxRigGeometryEvidence.Build(model, cancellationToken);
                geometryEvidence = captured.Supports.Any(static support => support.SurfaceMass > 0)
                    ? captured
                    : null;
                geometryEvidenceCaptured = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(
                template,
                source,
                new RigCorrespondenceOptions
                {
                    DropExtraBones = !keepExtraBones,
                    RoleOverrides = roleOverrides,
                    GeometryEvidence = useGeometry ? geometryEvidence : null,
                },
                cancellationToken);
            RigLandmarkSolution landmark = RigLandmarkSolver.Solve(
                template,
                source,
                correspondence,
                new RigLandmarkOptions
                {
                    ScaleOverride = scaleMode == CustomModelConformanceScaleMode.Manual ? manualScale : null,
                    RestrictScaleToRegion = scaleMode switch
                    {
                        CustomModelConformanceScaleMode.Leg => RigScaleRegion.Leg,
                        CustomModelConformanceScaleMode.Torso => RigScaleRegion.Torso,
                        CustomModelConformanceScaleMode.Arm => RigScaleRegion.Arm,
                        _ => null,
                    },
                },
                cancellationToken);
            RigConformanceResult fit = RigConformanceSolver.Solve(
                template,
                source,
                correspondence,
                landmark,
                new RigConformanceOptions
                {
                    ConformanceStrength = conformanceStrength,
                    PositionOverrides = positionOverrides,
                },
                cancellationToken);
            string status = editingAppliedOutput
                ? $"Reopened the applied DL rig for editing. Original source mapping decisions remain saved; the current rig is the fit source. {fit.Bones.Length} bones - {correspondence.MappedCount} mapped, {correspondence.SynthesizedCount} synthesized."
                : string.Create(CultureInfo.CurrentCulture,
                    $"{fit.Bones.Length} bones - {correspondence.MappedCount} mapped, {correspondence.SynthesizedCount} synthesized, {correspondence.ExtraCount} retained, {correspondence.DroppedCount} dropped. {fit.Warnings.Length} proportion warnings.");
            if (useGeometry && geometryEvidence is null)
                status += " No usable surface support; only name-based proposals are available.";
            return new(correspondence, landmark, fit, geometryEvidence, geometryEvidenceCaptured, status);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            return new(null, null, null, geometryEvidence, geometryEvidenceCaptured,
                $"The conformance could not be solved: {exception.Message}");
        }
    }

    private void PublishSolveResult(RigConformanceSolveResult result, long revision)
    {
        _geometryEvidence = result.GeometryEvidence;
        _geometryEvidenceCaptured = result.GeometryEvidenceCaptured;
        Correspondence = result.Correspondence;
        Landmark = result.Landmark;
        Fit = result.Fit;
        SolveStatus = result.Status;
        Interlocked.Exchange(ref _publishedFitRevision, revision);
        RefreshMappings();
        RefreshLandmarks();
        RefreshWarnings();
        RefreshMissingCoreRoles();
        NotifyStateChanged();
        FitChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelPendingFitSolve()
    {
        CancellationTokenSource? cancellation = _fitSolveCancellation;
        _fitSolveCancellation = null;
        cancellation?.Cancel();
    }

    internal void CancelPendingSolve()
    {
        Interlocked.Increment(ref _fitInputRevision);
        CancelPendingFitSolve();
        IsBusy = false;
    }

    internal void SetSolveScheduler(IRigConformanceSolveScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        CancelPendingSolve();
        _solveScheduler = scheduler;
    }

    private void PrepareAppliedOutputRigForSolve()
    {
        if (_sourceConformanceReviewRequired || _editingAppliedOutputRig ||
            _model is not { Rig: { } displayedRig } model ||
            model.Package.Document.RigConformance is not { } savedConformance ||
            !savedConformance.MatchesAppliedOutputRig(
                displayedRig,
                model.Package.Document.Source.ContentSha256))
            return;

        _editingAppliedOutputRig = true;
        _roleOverrides = ImmutableDictionary<string, string>.Empty;
        _positionOverrides = ImmutableDictionary<string, Vector3D>.Empty;
        _previousMappingEdit = null;
        _previousPositionOverrides = null;
        _sourceConformanceReviewRequired = false;
        _settingModel = true;
        try
        {
            ScaleMode = CustomModelConformanceScaleMode.Automatic;
            ConformanceStrength = 0.0;
        }
        finally
        {
            _settingModel = false;
        }
    }

    private void RefreshMappings()
    {
        Mappings.Clear();
        if (Correspondence is not { } correspondence)
        {
            OnPropertyChanged(nameof(VisibleMappings));
            OnPropertyChanged(nameof(GuidedMappingReviewRows));
            OnPropertyChanged(nameof(GuidedEditableMappingRows));
            return;
        }

        Dictionary<string, ImmutableArray<string>> candidatesByRole =
            correspondence.Ambiguities.ToDictionary(
                static row => row.Role,
                static row => row.CandidateSourceNames,
                StringComparer.Ordinal);
        ImmutableArray<string> sourceBoneNames = _model?.Rig is { } sourceRig
            ? sourceRig.Bones.Select(static bone => bone.Name).ToImmutableArray()
            : [];

        foreach (RigCorrespondenceRow row in correspondence.Rows)
        {
            ImmutableArray<string> candidates =
                row.Role is { } role &&
                candidatesByRole.TryGetValue(role, out ImmutableArray<string> options)
                    ? options
                    : row.SourceName is { } single ? [single] : [];
            if (row.TemplateIndex >= 0 && row.Role is not null)
                candidates = candidates.Concat(sourceBoneNames)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();
            if (row.Role is { } targetRole && !_roleOverrides.IsEmpty)
            {
                candidates = candidates.Where(candidate =>
                        !IsSourceReservedForAnotherRole(targetRole, candidate) ||
                        string.Equals(row.SourceName, candidate, StringComparison.OrdinalIgnoreCase))
                    .ToImmutableArray();
            }
            Mappings.Add(new RigConformanceMappingItemViewModel(
                row,
                candidates,
                ApplyRoleOverride,
                row.TemplateIndex >= 0 && row.Disposition != RigBoneDisposition.Mapped &&
                    CoreRoles.Contains(row.Role, StringComparer.Ordinal)));
        }

        OnPropertyChanged(nameof(VisibleMappings));
        OnPropertyChanged(nameof(GuidedMappingReviewRows));
        OnPropertyChanged(nameof(GuidedEditableMappingRows));
    }

    private bool IsSourceReservedForAnotherRole(string role, string sourceBoneName) =>
        _roleOverrides.Any(pair =>
            !string.Equals(pair.Key, role, StringComparison.Ordinal) &&
            string.Equals(pair.Value, sourceBoneName, StringComparison.OrdinalIgnoreCase));

    private void ClearGuidedMappingConflict()
    {
        _guidedMappingConflictTargetRole = null;
        _guidedMappingConflictOwnerRole = null;
        _guidedMappingConflictSourceName = null;
        OnPropertyChanged(nameof(HasGuidedMappingConflict));
        OnPropertyChanged(nameof(GuidedMappingReviewRows));
        OnPropertyChanged(nameof(GuidedEditableMappingRows));
    }

    partial void OnUseGeometryCorrespondenceChanged(bool value) => QueueSolve();

    private void AcceptMappingProposals()
    {
        if (!HasPendingMappingReview || Correspondence is not { } correspondence) return;
        _previousMappingEdit = CaptureMappingEdit();
        foreach (var row in correspondence.Rows.Where(static r => r.Disposition == RigBoneDisposition.Mapped && r.Role is not null && r.SourceName is not null))
            _roleOverrides = _roleOverrides.SetItem(row.Role!, row.SourceName!);
        QueueSolve();
        MappingEditStatus = "Suggested matches accepted. Undo restores the previous mapping choices.";
    }

    private void RerunAutomaticMatching()
    {
        if (!CanAdvance || IsBusy)
        {
            return;
        }

        ImmutableDictionary<string, string> previous = _roleOverrides;
        bool sourceChanged = _sourceConformanceReviewRequired;
        _previousMappingEdit = previous.IsEmpty && !sourceChanged ? null : CaptureMappingEdit();
        _roleOverrides = ImmutableDictionary<string, string>.Empty;
        if (sourceChanged)
        {
            _positionOverrides = ImmutableDictionary<string, Vector3D>.Empty;
            _previousPositionOverrides = null;
            _sourceConformanceReviewRequired = false;
        }
        _geometryEvidence = null;
        _geometryEvidenceCaptured = false;
        QueueSolve();
        MappingEditStatus = "Automatic matching queued.";
        UndoLastMappingChangeCommand.NotifyCanExecuteChanged();
    }

    private void UndoLastMappingChange()
    {
        if (_previousMappingEdit is not { } previous || IsBusy)
        {
            return;
        }

        _roleOverrides = previous.Roles;
        if (previous.Positions is { } positions)
        {
            _positionOverrides = positions;
            _previousPositionOverrides = null;
        }
        _sourceConformanceReviewRequired = previous.RequiresSourceReview;
        _previousMappingEdit = null;
        QueueSolve();
        MappingEditStatus = RequiresSourceRematch
            ? "Saved fit restored for review. Re-match before fitting the changed model."
            : "Previous bone mapping choices restored.";
    }

    private MappingEditSnapshot CaptureMappingEdit() => new(
        _roleOverrides,
        _sourceConformanceReviewRequired ? _positionOverrides : null,
        _sourceConformanceReviewRequired);

    private void RefreshLandmarks()
    {
        foreach (RigConformanceLandmarkViewModel landmark in Landmarks)
        {
            RigConformedBone? bone = Fit?.Bones.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Name,
                    landmark.BoneName,
                    StringComparison.OrdinalIgnoreCase));
            landmark.Update(bone, HasOverride(landmark.BoneName));
        }
        RefreshFittedFingerJoints();
    }

    /// <summary>
    /// The body roles a humanoid conversion depends on. Anything missing here
    /// is reported prominently rather than silently producing a rig that only
    /// looks plausible in a table.
    /// </summary>
    private static readonly string[] CoreRoles =
    [
        "body.pelvis", "body.spine.1", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left",
        "arm.right.upper", "arm.right.lower", "hand.right",
        "leg.left.upper", "leg.left.lower", "foot.left",
        "leg.right.upper", "leg.right.lower", "foot.right",
    ];

    private void RefreshMissingCoreRoles()
    {
        if (Correspondence is not { } correspondence)
        {
            MissingCoreRoles = [];
        }
        else
        {
            HashSet<string> mapped = correspondence.Rows
                .Where(static row =>
                    row.Disposition == RigBoneDisposition.Mapped &&
                    row.Role is not null)
                .Select(static row => row.Role!)
                .ToHashSet(StringComparer.Ordinal);
            MissingCoreRoles = CoreRoles
                .Where(role => !mapped.Contains(role))
                .ToImmutableArray();
        }

        OnPropertyChanged(nameof(MissingCoreRoles));
        OnPropertyChanged(nameof(HasMissingCoreRoles));
        OnPropertyChanged(nameof(MissingCoreRolesMessage));
        OnPropertyChanged(nameof(GuidedMappingReviewRows));
        OnPropertyChanged(nameof(GuidedEditableMappingRows));
    }

    private void RefreshWarnings()
    {
        Warnings.Clear();
        if (Fit is not { } fit)
        {
            return;
        }

        foreach (RigConformanceWarning warning in fit.Warnings
                     .OrderBy(static row => row.SegmentRatio))
        {
            Warnings.Add(warning.Message);
        }
    }

    private bool ApplyRoleOverride(string role, string sourceBoneName)
    {
        if (IsBusy)
        {
            MappingEditStatus = "Wait for the current fit to finish before changing a bone match.";
            return false;
        }
        if (_sourceConformanceReviewRequired) return false;
        if (_model?.Rig is not { } sourceRig ||
            sourceRig.GetBoneIndex(sourceBoneName) < 0)
        {
            MappingEditStatus = "That source bone is unavailable. Choose a bone from the current model.";
            return false;
        }

        KeyValuePair<string, string> conflict = _roleOverrides.FirstOrDefault(pair =>
            pair.Key != role &&
            string.Equals(pair.Value, sourceBoneName, StringComparison.OrdinalIgnoreCase));
        if (conflict.Key is not null)
        {
            _guidedMappingConflictTargetRole = role;
            _guidedMappingConflictOwnerRole = conflict.Key;
            _guidedMappingConflictSourceName = sourceBoneName;
            OnPropertyChanged(nameof(HasGuidedMappingConflict));
            MappingEditStatus =
                $"{sourceBoneName} is already assigned to {conflict.Key}, so it was not assigned to {role}. " +
                $"Choose a different source for {conflict.Key} below to release it, then retry {role}.";
            OnPropertyChanged(nameof(GuidedMappingReviewRows));
            OnPropertyChanged(nameof(GuidedEditableMappingRows));
            return false;
        }

        if (_roleOverrides.TryGetValue(role, out string? current) &&
            string.Equals(current, sourceBoneName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        ImmutableDictionary<string, string> previous = _roleOverrides;
        MappingEditSnapshot? previousUndo = _previousMappingEdit;
        int mappedBefore = MappedCount;
        int coreBefore = CoreRoles.Length - MissingCoreRoles.Length;
        _previousMappingEdit = CaptureMappingEdit();
        _pendingRoleOverride = new(previous, previousUndo, mappedBefore, coreBefore,
            role, sourceBoneName, IsAdvancedSetupMode);
        _roleOverrides = previous.SetItem(role, sourceBoneName);
        MappingEditStatus = $"Checking the {sourceBoneName} match.";
        QueueSolve();
        return true;
    }

    private bool AcceptPendingRoleOverride(RigConformanceSolveResult result)
    {
        if (_pendingRoleOverride is not { } pending) return true;
        int mappedCount = result.Correspondence?.MappedCount ?? 0;
        var mappedCoreRoles = new HashSet<string>(StringComparer.Ordinal);
        if (result.Correspondence is { } solvedCorrespondence)
        {
            foreach (RigCorrespondenceRow row in solvedCorrespondence.Rows.Where(
                         static row => row.Disposition == RigBoneDisposition.Mapped && row.Role is not null))
                mappedCoreRoles.Add(row.Role!);
        }
        int coreCount = CoreRoles.Count(mappedCoreRoles.Contains);
        int lostMapped = pending.MappedBefore - mappedCount;
        int lostCore = pending.CoreBefore - coreCount;
        bool majorLoss =
            pending.MappedBefore >= 4 && lostMapped >= Math.Max(3, pending.MappedBefore / 2) ||
            pending.CoreBefore >= 3 && lostCore >= Math.Max(2, pending.CoreBefore / 2);
        if (result.Fit is null || result.Correspondence is null || majorLoss && !pending.AllowMajorLoss)
        {
            _roleOverrides = pending.RolesBefore;
            _previousMappingEdit = pending.PreviousUndo;
            _pendingRoleOverride = null;
            MappingEditStatus = result.Fit is null || result.Correspondence is null
                ? "That bone choice could not be fitted. The previous mappings were restored."
                : $"Would unmap {lostMapped} bones ({Math.Max(0, lostCore)} core). Previous fit restored; Advanced can allow it.";
            QueueSolve();
            return false;
        }

        _pendingRoleOverride = null;
        MappingEditStatus = majorLoss
            ? $"This choice unmapped {lostMapped} bones. Undo restores the previous mapping."
            : lostMapped > 0
                ? $"This choice changed {lostMapped} other bone mappings. Undo restores them."
                : "Bone match applied. Undo restores the previous mapping.";
        if (_guidedMappingConflictTargetRole is { } conflictTarget &&
            _guidedMappingConflictOwnerRole is { } conflictOwner &&
            _guidedMappingConflictSourceName is { } conflictSource)
        {
            if (string.Equals(pending.Role, conflictOwner, StringComparison.Ordinal) &&
                !string.Equals(pending.SourceBoneName, conflictSource, StringComparison.OrdinalIgnoreCase))
            {
                MappingEditStatus = $"{conflictSource} is now free from {conflictOwner}. Choose it for {conflictTarget} when ready.";
                OnPropertyChanged(nameof(GuidedMappingReviewRows));
                OnPropertyChanged(nameof(GuidedEditableMappingRows));
            }
            else if (string.Equals(pending.Role, conflictTarget, StringComparison.Ordinal) &&
                     string.Equals(pending.SourceBoneName, conflictSource, StringComparison.OrdinalIgnoreCase))
            {
                ClearGuidedMappingConflict();
            }
        }
        else
        {
            ClearGuidedMappingConflict();
        }
        return true;
    }

    /// <summary>
    /// Records a manual world placement for one joint, mirroring it to the
    /// opposite side when mirroring is enabled. Descendants follow because the
    /// solver applies overrides before children read their parent position.
    /// </summary>
    public void SetBonePosition(string boneName, Vector3D position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boneName);
        if (_sourceConformanceReviewRequired || !position.IsFinite)
        {
            return;
        }

        // DL1 model space is X-left, so a mirrored joint is the same point with
        // X negated. ApplyPlacement owns that rule for both this path and the
        // viewport gizmo.
        _previousPositionOverrides = _positionOverrides;
        ApplyPlacement(boneName, position);
    }

    public bool HasOverride(string boneName) =>
        !_sourceConformanceReviewRequired && _positionOverrides.ContainsKey(boneName);

    public Vector3D? TryGetBonePosition(string boneName) =>
        Fit?.Bones
            .FirstOrDefault(bone => string.Equals(
                bone.Name,
                boneName,
                StringComparison.OrdinalIgnoreCase))
            ?.Position;

    private static string? TryFindMirror(string boneName)
    {
        foreach ((string bone, _, _, string? mirror) in LandmarkSequence)
        {
            if (string.Equals(bone, boneName, StringComparison.OrdinalIgnoreCase))
            {
                return mirror;
            }
        }

        return null;
    }

    private void ResetSelectedBone()
    {
        if (SelectedJointBoneName is not { } selectedBone)
        {
            return;
        }

        _previousPositionOverrides = _positionOverrides;
        _positionOverrides = _positionOverrides.Remove(selectedBone);
        if (SelectedLandmark?.MirrorBoneName is { } mirror)
        {
            _positionOverrides = _positionOverrides.Remove(mirror);
        }

        QueueSolve();
    }

    private void ResetAllOverrides()
    {
        _previousPositionOverrides = _positionOverrides;
        _positionOverrides = ImmutableDictionary<string, Vector3D>.Empty;
        QueueSolve();
    }

    private void UndoLastJointPlacement()
    {
        if (_previousPositionOverrides is not { } previous || IsBusy)
        {
            return;
        }

        _positionOverrides = previous;
        _previousPositionOverrides = null;
        QueueSolve();
    }

    partial void OnKeepExtraBonesChanged(bool value) => QueueSolve();

    partial void OnScaleModeChanged(CustomModelConformanceScaleMode value)
    {
        OnPropertyChanged(nameof(SelectedScaleModeChoice));
        OnPropertyChanged(nameof(IsManualScale));
        QueueSolve();
    }

    partial void OnManualScaleChanged(double value)
    {
        if (ScaleMode == CustomModelConformanceScaleMode.Manual)
        {
            QueueSolve();
        }
    }

    partial void OnConformanceStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(FitMode));
        OnPropertyChanged(nameof(SelectedFitModeChoice));
        QueueSolve();
    }

    partial void OnStageChanged(RigConformanceStage value)
    {
        if (!_settingModel && !_restoringStudio && StudioStage != RigStudioStage.Fit) StudioStage = RigStudioStage.Fit;
        NotifyStateChanged();
    }

    partial void OnSelectedLandmarkChanged(RigConformanceLandmarkViewModel? value)
    {
        if (value is not null) SelectedFittedFingerJoint = null;
        OnPropertyChanged(nameof(SelectedJointBoneName));
        OnPropertyChanged(nameof(SelectedBoneIndex));
        ResetBoneCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        NotifySetupTransfer();
        OnPropertyChanged(nameof(Template));
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(RequiresSourceRematch));
        OnPropertyChanged(nameof(AutomaticMatchingLabel));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(IsEditingAppliedOutputRig));
        OnPropertyChanged(nameof(CanPlaceGuidedBodyJoints));
        OnPropertyChanged(nameof(CanReviewGuidedHands));
        OnPropertyChanged(nameof(CanPreviewGuidedFit));
        OnPropertyChanged(nameof(CanApplyGuidedFit));
        OnPropertyChanged(nameof(GuidedMissingMappings));
        OnPropertyChanged(nameof(GuidedMappingReviewRows));
        OnPropertyChanged(nameof(GuidedEditableMappingRows));
        OnPropertyChanged(nameof(GuidedFitPrimaryAction));
        OnPropertyChanged(nameof(GuidedFitBlockReason));
        OnPropertyChanged(nameof(HasPendingMappingReview));
        OnPropertyChanged(nameof(HasMissingAnatomicalCorrespondence));
        OnPropertyChanged(nameof(MappingReviewMessage));
        OnPropertyChanged(nameof(MappedCount));
        OnPropertyChanged(nameof(SynthesizedCount));
        OnPropertyChanged(nameof(ExtraCount));
        OnPropertyChanged(nameof(DroppedCount));
        OnPropertyChanged(nameof(ScaleSummary));
        OnPropertyChanged(nameof(RegionFits));
        OnPropertyChanged(nameof(CanVerifyWithRetailClip));
        ResolveTemplateCommand.NotifyCanExecuteChanged();
        UseSelectedRetailMeshCommand?.NotifyCanExecuteChanged();
        ApplyConformanceCommand.NotifyCanExecuteChanged();
        AcceptMappingProposalsCommand.NotifyCanExecuteChanged();
        RerunAutomaticMatchingCommand.NotifyCanExecuteChanged();
        UndoLastMappingChangeCommand.NotifyCanExecuteChanged();
        VerifyWithRetailClipCommand.NotifyCanExecuteChanged();
        ResetBoneCommand.NotifyCanExecuteChanged();
        ResetAllCommand.NotifyCanExecuteChanged();
        UndoLastJointPlacementCommand.NotifyCanExecuteChanged();
        NextStageCommand.NotifyCanExecuteChanged();
        PreviousStageCommand.NotifyCanExecuteChanged();
        NotifyBodyDetectionCommands();
        NotifyChannelPolicies();
        NotifyContacts();
        NotifyHands();
        NotifyEyes();
        NotifyCameras();
        NotifyStructural();
        NotifyCapabilityProfile();
        NotifyRestPose();
        NotifyHierarchy();
        NotifyDerivedMotion();
        NotifyDoctor();
        NotifyWeightEditing();
        NotifyStressReview();
        NotifyStudioWorkflow();
    }
}
