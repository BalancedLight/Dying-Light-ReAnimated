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
    private readonly Action<string, string> _applyRoleOverride;
    private string _selectedSourceName;

    public RigConformanceMappingItemViewModel(
        RigCorrespondenceRow row,
        ImmutableArray<string> candidates,
        Action<string, string> applyRoleOverride)
    {
        Row = row;
        Candidates = candidates;
        _applyRoleOverride = applyRoleOverride;
        _selectedSourceName = row.SourceName ?? string.Empty;
    }

    public RigCorrespondenceRow Row { get; }

    public ImmutableArray<string> Candidates { get; }

    public string Name => Row.Name;

    public string Disposition => Row.Disposition.ToString();

    public string SourceName => Row.SourceName ?? "-";

    public string Role => Row.Role ?? "-";

    public string Confidence =>
        Row.Confidence.ToString("P0", CultureInfo.CurrentCulture);

    public string Evidence => Row.Evidence;

    public bool IsAmbiguous => Row.WasAmbiguous;

    public bool CanChooseSource => Candidates.Length > 1 && Row.Role is not null;

    /// <summary>
    /// The chosen source bone for an ambiguous role. Setting it records an
    /// explicit override and re-solves.
    /// </summary>
    public string SelectedSourceName
    {
        get => _selectedSourceName;
        set
        {
            if (!SetProperty(ref _selectedSourceName, value) ||
                Row.Role is not { } role ||
                string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            _applyRoleOverride(role, value);
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

    private FbxModelAuthoringImportResult? _model;
    private Dl1RigTemplate? _template;
    private ImmutableDictionary<string, string> _roleOverrides =
        ImmutableDictionary<string, string>.Empty;
    private ImmutableDictionary<string, Vector3D> _positionOverrides =
        ImmutableDictionary<string, Vector3D>.Empty;

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
    private double _conformanceStrength = 1.0;

    [ObservableProperty]
    private bool _mirrorEdits = true;

    [ObservableProperty]
    private RigConformanceLandmarkViewModel? _selectedLandmark;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _solveStatus = "Import a model to begin.";

    public RigConformanceWizardViewModel(
        Func<string, CancellationToken, Task<Dl1RigTemplateResolution>> resolveTemplate,
        Action<string> setStatus)
    {
        _resolveTemplate = resolveTemplate ?? throw new ArgumentNullException(nameof(resolveTemplate));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));

        foreach ((string bone, string label, string instruction, string? mirror) in LandmarkSequence)
        {
            Landmarks.Add(new RigConformanceLandmarkViewModel(bone, label, instruction, mirror));
        }

        ResolveTemplateCommand = new AsyncRelayCommand(
            ResolveTemplateAsync,
            () => !IsBusy);
        ResetBoneCommand = new RelayCommand(
            ResetSelectedBone,
            () => SelectedLandmark is not null && HasOverride(SelectedLandmark.BoneName));
        ResetAllCommand = new RelayCommand(
            ResetAllOverrides,
            () => !_positionOverrides.IsEmpty);
        NextStageCommand = new RelayCommand(
            () => Stage = (RigConformanceStage)((int)Stage + 1),
            () => Stage < RigConformanceStage.Verify && CanAdvance);
        PreviousStageCommand = new RelayCommand(
            () => Stage = (RigConformanceStage)((int)Stage - 1),
            () => Stage > RigConformanceStage.Target);
        ApplyConformanceCommand = new RelayCommand(
            () => ApplyRequested?.Invoke(this, EventArgs.Empty),
            () => CanApply && !IsBusy);
    }

    /// <summary>
    /// Raised when the author asks to commit the conformance. The workspace
    /// owns the document mutation and its undo entry, so the wizard only asks.
    /// </summary>
    public event EventHandler? ApplyRequested;

    /// <summary>Raised whenever a re-solve produced a new fit to preview.</summary>
    public event EventHandler? FitChanged;

    public ObservableCollection<RigConformanceMappingItemViewModel> Mappings { get; } = [];

    public ObservableCollection<RigConformanceLandmarkViewModel> Landmarks { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public IAsyncRelayCommand ResolveTemplateCommand { get; }

    public IRelayCommand ResetBoneCommand { get; }

    public IRelayCommand ResetAllCommand { get; }

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
        new(RigConformanceFitMode.MatchDl1Exactly, "Match DL1 exactly (best animation)"),
        new(RigConformanceFitMode.PreserveSourceProportions, "Keep this model's proportions"),
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
        : $"{MissingCoreRoles.Length} core body role(s) could not be matched by name: " +
          $"{string.Join(", ", MissingCoreRoles)}. Assign them on the Mapping stage, " +
          "or the conversion will not follow this model.";

    public Dl1RigTemplate? Template => _template;

    public RigCorrespondence? Correspondence { get; private set; }

    public RigLandmarkSolution? Landmark { get; private set; }

    public RigConformanceResult? Fit { get; private set; }

    public bool HasTemplate => _template is not null;

    public bool HasModel => _model is not null;

    public bool CanAdvance => HasTemplate && HasModel;

    public bool CanApply => Fit is not null;

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
    {
        _model = model;
        if (model is null)
        {
            Correspondence = null;
            Landmark = null;
            Fit = null;
            Mappings.Clear();
            Warnings.Clear();
            SolveStatus = "Import a model to begin.";
        }
        else if (model.Package.Document.RigConformance is { } persisted)
        {
            RestoreSettings(persisted, model.Package.Document.Source.ContentSha256);
        }

        NotifyStateChanged();
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
        _roleOverrides = settings.RoleOverrides.ToImmutableDictionary(
            static row => row.Role,
            static row => row.SourceBoneName,
            StringComparer.Ordinal);
        _positionOverrides = settings.PositionOverrides.ToImmutableDictionary(
            static row => row.BoneName,
            static row => row.Position,
            StringComparer.OrdinalIgnoreCase);

        SolveStatus = settings.MatchesSource(sourceFbxSha256)
            ? "Restored the saved conformance settings."
            : "The saved conformance was solved against a different source model; review it before applying.";
    }

    /// <summary>Captures the current decisions for persistence.</summary>
    public CustomModelRigConformance? CreateSettings()
    {
        if (_template is not { } template ||
            _model?.Package.Document.Source.ContentSha256 is not { } sourceHash ||
            string.IsNullOrWhiteSpace(sourceHash))
        {
            return null;
        }

        return new CustomModelRigConformance
        {
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
            Dl1RigTemplateResolution resolution =
                await _resolveTemplate(TemplateProfileName, cancellationToken)
                    .ConfigureAwait(true);
            _template = resolution.Template;
            TemplateStatus = resolution.Succeeded
                ? $"{resolution.ResourceName}: {resolution.Status}"
                : resolution.Status;
            _setStatus(TemplateStatus);
            Solve();
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// Re-derives the correspondence, scale and fit from the current decisions.
    /// Every solver refusal is reported rather than leaving a stale result on
    /// screen.
    /// </summary>
    public void Solve()
    {
        if (_template is not { } template ||
            _model?.Rig is not { } source)
        {
            Fit = null;
            NotifyStateChanged();
            return;
        }

        try
        {
            Correspondence = RigCorrespondenceSolver.Solve(
                template,
                source,
                new RigCorrespondenceOptions
                {
                    DropExtraBones = !KeepExtraBones,
                    RoleOverrides = _roleOverrides,
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
            SolveStatus = string.Create(
                CultureInfo.CurrentCulture,
                $"{Fit.Bones.Length} bones - {MappedCount} mapped, {SynthesizedCount} synthesized, " +
                $"{ExtraCount} retained, {DroppedCount} dropped. {Fit.Warnings.Length} proportion warnings.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException)
        {
            Fit = null;
            SolveStatus = $"The conformance could not be solved: {exception.Message}";
        }

        RefreshMappings();
        RefreshLandmarks();
        RefreshWarnings();
        RefreshMissingCoreRoles();
        NotifyStateChanged();
        FitChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshMappings()
    {
        Mappings.Clear();
        if (Correspondence is not { } correspondence)
        {
            return;
        }

        Dictionary<string, ImmutableArray<string>> candidatesByRole =
            correspondence.Ambiguities.ToDictionary(
                static row => row.Role,
                static row => row.CandidateSourceNames,
                StringComparer.Ordinal);

        foreach (RigCorrespondenceRow row in correspondence.Rows)
        {
            ImmutableArray<string> candidates =
                row.Role is { } role &&
                candidatesByRole.TryGetValue(role, out ImmutableArray<string> options)
                    ? options
                    : row.SourceName is { } single ? [single] : [];
            Mappings.Add(new RigConformanceMappingItemViewModel(
                row,
                candidates,
                ApplyRoleOverride));
        }
    }

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

    private void ApplyRoleOverride(string role, string sourceBoneName)
    {
        _roleOverrides = _roleOverrides.SetItem(role, sourceBoneName);
        Solve();
    }

    /// <summary>
    /// Records a manual world placement for one joint, mirroring it to the
    /// opposite side when mirroring is enabled. Descendants follow because the
    /// solver applies overrides before children read their parent position.
    /// </summary>
    public void SetBonePosition(string boneName, Vector3D position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boneName);
        if (!position.IsFinite)
        {
            return;
        }

        // DL1 model space is X-left, so a mirrored joint is the same point with
        // X negated. ApplyPlacement owns that rule for both this path and the
        // viewport gizmo.
        ApplyPlacement(boneName, position);
    }

    public bool HasOverride(string boneName) =>
        _positionOverrides.ContainsKey(boneName);

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
        if (SelectedLandmark is not { } landmark)
        {
            return;
        }

        _positionOverrides = _positionOverrides.Remove(landmark.BoneName);
        if (landmark.MirrorBoneName is { } mirror)
        {
            _positionOverrides = _positionOverrides.Remove(mirror);
        }

        Solve();
    }

    private void ResetAllOverrides()
    {
        _positionOverrides = ImmutableDictionary<string, Vector3D>.Empty;
        Solve();
    }

    partial void OnKeepExtraBonesChanged(bool value) => Solve();

    partial void OnScaleModeChanged(CustomModelConformanceScaleMode value)
    {
        OnPropertyChanged(nameof(SelectedScaleModeChoice));
        OnPropertyChanged(nameof(IsManualScale));
        Solve();
    }

    partial void OnManualScaleChanged(double value)
    {
        if (ScaleMode == CustomModelConformanceScaleMode.Manual)
        {
            Solve();
        }
    }

    partial void OnConformanceStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(FitMode));
        OnPropertyChanged(nameof(SelectedFitModeChoice));
        Solve();
    }

    partial void OnStageChanged(RigConformanceStage value) => NotifyStateChanged();

    partial void OnSelectedLandmarkChanged(RigConformanceLandmarkViewModel? value) =>
        ResetBoneCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasTemplate));
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(MappedCount));
        OnPropertyChanged(nameof(SynthesizedCount));
        OnPropertyChanged(nameof(ExtraCount));
        OnPropertyChanged(nameof(DroppedCount));
        OnPropertyChanged(nameof(ScaleSummary));
        OnPropertyChanged(nameof(RegionFits));
        OnPropertyChanged(nameof(CanVerifyWithRetailClip));
        ResolveTemplateCommand.NotifyCanExecuteChanged();
        ApplyConformanceCommand.NotifyCanExecuteChanged();
        VerifyWithRetailClipCommand.NotifyCanExecuteChanged();
        ResetBoneCommand.NotifyCanExecuteChanged();
        ResetAllCommand.NotifyCanExecuteChanged();
        NextStageCommand.NotifyCanExecuteChanged();
        PreviousStageCommand.NotifyCanExecuteChanged();
    }
}
