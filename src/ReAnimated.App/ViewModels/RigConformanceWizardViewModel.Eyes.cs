using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed record EyeModeChoice(RigEyeSetupMode Mode, string Label);
public sealed record EyePreview(TransformMatrix Frame, double? Radius);
public sealed partial class EyeMorphChoice : ObservableObject
{
    public EyeMorphChoice(uint descriptor, string name) { Descriptor = descriptor; Name = name; }
    public uint Descriptor { get; }
    public string Name { get; }
    [ObservableProperty] private bool _selected;
}

public sealed partial class RigConformanceWizardViewModel
{
    private FbxEyeGeometryWork? _eyeWork;
    private bool _restoringEyes;
    private long _eyeGeneration;
    [ObservableProperty] private RigEyeSide _eyeSide;
    [ObservableProperty] private EyeModeChoice? _eyeMode;
    [ObservableProperty] private ContactComponentChoice? _eyeComponent;
    [ObservableProperty] private FbxEyeSourceNodeObservation? _eyeSourceNode;
    [ObservableProperty] private FbxEyeSourceNodeObservation? _eyeParent;
    [ObservableProperty] private int _eyeIslandIndex;
    [ObservableProperty] private bool _eyePainted;
    [ObservableProperty] private bool _eyeUseManualPivot = true;
    [ObservableProperty] private bool _eyeReviewed;
    [ObservableProperty] private bool _eyeReviewEnabled;
    [ObservableProperty] private string _eyeHelperName = "eye_pivot_left";
    [ObservableProperty] private string _eyeStatus = "Choose the eye setup to review. Existing source nodes and morph controls retain their identities.";
    public ContactVector EyePosition { get; } = new();
    public ContactVector EyeForward { get; } = new();
    public ContactVector EyeUp { get; } = new();
    public ObservableCollection<ContactComponentChoice> EyeComponents { get; } = [];
    public ObservableCollection<FbxEyeSourceNodeObservation> EyeNodes { get; } = [];
    public ObservableCollection<EyeMorphChoice> EyeMorphs { get; } = [];
    public IReadOnlyList<RigEyeSide> EyeSides { get; } = Enum.GetValues<RigEyeSide>();
    public IReadOnlyList<EyeModeChoice> EyeModeChoices { get; } = [new(RigEyeSetupMode.SourceEye, "Keep an existing eye node"),
        new(RigEyeSetupMode.GeometryPivot, "Fit or place an eye pivot"), new(RigEyeSetupMode.GazeReference, "Place a gaze reference"),
        new(RigEyeSetupMode.Mimic, "Record existing facial controls")];
    public IAsyncRelayCommand DetectEyeCommand { get; private set; } = null!;
    public IRelayCommand CancelEyeDetectionCommand { get; private set; } = null!;
    public IRelayCommand SaveEyeSetupCommand { get; private set; } = null!;
    public IRelayCommand ApplyEyeHelperCommand { get; private set; } = null!;
    public event EventHandler? EyePreviewChanged;
    public event EventHandler<BodyModelEventArgs>? EyeModelApplyRequested;
    public EyePivotDetectionResult? EyeDetection => _eyeWork?.Detection;
    public string EyeMorphSummary => EyeMorphs.Count == 0 ? "This model has no facial morph controls. Eye placement will not create them." :
        $"{EyeMorphs.Count} existing facial controls. Select the controls to associate with this review; their names and targets are preserved.";
    public bool IsSourceEyeMode => EyeMode?.Mode == RigEyeSetupMode.SourceEye;
    public bool IsGeometryEyeMode => EyeMode?.Mode == RigEyeSetupMode.GeometryPivot;
    public bool IsMimicEyeMode => EyeMode?.Mode == RigEyeSetupMode.Mimic;
    public bool IsPlacedEyeMode => EyeMode?.Mode is RigEyeSetupMode.GeometryPivot or RigEyeSetupMode.GazeReference;
    public bool CanDetectEye => !IsBusy && HasStudioSession && IsGeometryEyeMode && EyeComponent is not null && EyeIslandIndex >= 0;
    public bool CanSaveEyeSetup => !IsBusy && HasStudioSession && EyeReviewed && TryCreateEyeSetup(out _);
    public bool CanApplyEyeHelper => CanSaveEyeSetup && IsPlacedEyeMode && EyeParent is not null && !string.IsNullOrWhiteSpace(EyeHelperName);
    private RigEyeSetup? SavedEye => _model?.Package.Document.RiggingSession?.Eyes.FirstOrDefault(e => e.Side == EyeSide && e.Mode == EyeMode?.Mode);

    private void InitializeEyes()
    {
        InitializeEyeRig();
        EyeMode = EyeModeChoices[0]; EyeForward.Set(Vector3D.UnitZ); EyeUp.Set(Vector3D.UnitY);
        foreach (var vector in new[] { EyePosition, EyeForward, EyeUp }) vector.PropertyChanged += (_, _) => EyeDraftChanged();
        DetectEyeCommand = new AsyncRelayCommand(DetectEyeAsync, () => CanDetectEye);
        CancelEyeDetectionCommand = new RelayCommand(() => DetectEyeCommand.Cancel(), () => DetectEyeCommand.IsRunning);
        SaveEyeSetupCommand = new RelayCommand(() => SaveEye(false), () => CanSaveEyeSetup);
        ApplyEyeHelperCommand = new RelayCommand(() => SaveEye(true), () => CanApplyEyeHelper);
        DetectEyeCommand.PropertyChanged += (_, _) => CancelEyeDetectionCommand.NotifyCanExecuteChanged();
    }

    private void RestoreEyes()
    {
        _restoringEyes = true;
        try
        {
            DetectEyeCommand?.Cancel(); _eyeGeneration++; _eyeWork = null; ClearEyeBinding();
            EyeMotionReviewEnabled = false; EyeYawDegrees = 0; EyePitchDegrees = 0;
            string? component = EyeComponent?.Id;
            EyeComponents.Clear(); EyeNodes.Clear(); EyeMorphs.Clear();
            if (_model is { } model && model.Package.Document.RiggingSession is not null)
            {
                foreach (var group in model.Surfaces.Where(s => s.SourceGeometry is not null).GroupBy(s => s.SourceGeometry!.Id))
                    EyeComponents.Add(new(group.Key, group.First().MeshName));
                foreach (var node in FbxEyeAuthoring.ObserveSourceNodes(model)) EyeNodes.Add(node);
                foreach (var morph in model.Package.Document.MorphChannels)
                {
                    var row = new EyeMorphChoice(morph.DescriptorHash, morph.Name);
                    row.PropertyChanged += (_, _) => EyeDraftChanged(); EyeMorphs.Add(row);
                }
            }
            var saved = SavedEye;
            EyeComponent = EyeComponents.FirstOrDefault(c => c.Id == (saved?.ComponentId ?? component)) ?? EyeComponents.FirstOrDefault();
            EyeSourceNode = EyeNodes.FirstOrDefault(n => n.EntityId == saved?.SourceEntityId);
            EyeParent = EyeNodes.FirstOrDefault(n => n.EntityId == saved?.ParentEntityId);
            EyeIslandIndex = saved?.IslandIndex ?? 0;
            EyePainted = saved?.GeometryKind == RigEyeGeometryKind.Painted;
            EyeUseManualPivot = saved?.GeometryKind != RigEyeGeometryKind.GlobeCandidate;
            EyeReviewed = saved?.UserApproved ?? false;
            var frame = saved?.GlobalFrame ?? TransformMatrix.Identity;
            EyePosition.Set(frame.Translation); EyeForward.Set(frame.TransformDirection(Vector3D.UnitZ)); EyeUp.Set(frame.TransformDirection(Vector3D.UnitY));
            EyeBoneName = EyeNodes.FirstOrDefault(n => n.EntityId == saved?.DeformEntityId)?.Name ?? $"eye_{EyeSide.ToString().ToLowerInvariant()}";
            EyeHelperName = EyeNodes.FirstOrDefault(n => n.EntityId == saved?.HelperEntityId)?.Name ??
                $"eye_{(EyeMode?.Mode == RigEyeSetupMode.GazeReference ? "gaze" : "pivot")}_{EyeSide.ToString().ToLowerInvariant()}";
            foreach (var row in EyeMorphs) row.Selected = saved?.MorphDescriptors.Contains(row.Descriptor) == true;
            EyeStatus = saved is null ? "Select the source or fit a draft, review the result, then save the setup." :
                "Saved eye setup restored. Source frames and facial descriptors remain separate from camera roles.";
        }
        catch (Exception error) when (EyeError(error)) { EyeStatus = "Eye setup needs a current source: " + error.Message; }
        finally { _restoringEyes = false; NotifyEyes(); }
    }

    private void EyeDraftChanged()
    {
        if (_restoringEyes) return;
        ClearEyeBinding(); EyeReviewed = false; NotifyEyes(); EyePreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    private void InvalidateEyeGeometry()
    {
        if (_restoringEyes) return;
        DetectEyeCommand?.Cancel(); _eyeGeneration++; _eyeWork = null; EyeUseManualPivot = true; EyeDraftChanged();
    }
    partial void OnEyeSideChanged(RigEyeSide value) { if (!_restoringEyes) RestoreEyes(); }
    partial void OnEyeModeChanged(EyeModeChoice? value) { if (!_restoringEyes && DetectEyeCommand is not null) RestoreEyes(); }
    partial void OnEyeComponentChanged(ContactComponentChoice? value) => InvalidateEyeGeometry();
    partial void OnEyeIslandIndexChanged(int value) => InvalidateEyeGeometry();
    partial void OnEyePaintedChanged(bool value) => InvalidateEyeGeometry();
    partial void OnEyeUseManualPivotChanged(bool value) => EyeDraftChanged();
    partial void OnEyeParentChanged(FbxEyeSourceNodeObservation? value) => EyeDraftChanged();
    partial void OnEyeSourceNodeChanged(FbxEyeSourceNodeObservation? value) => EyeDraftChanged();
    partial void OnEyeHelperNameChanged(string value) => EyeDraftChanged();
    partial void OnEyeReviewedChanged(bool value) => NotifyEyes();
    partial void OnEyeReviewEnabledChanged(bool value) => EyePreviewChanged?.Invoke(this, EventArgs.Empty);

    private async Task DetectEyeAsync(CancellationToken token)
    {
        if (_model is not { } model || EyeComponent is not { } component) return;
        RouteStudioAction(RigStudioStage.Detect); model = _model!;
        int island = EyeIslandIndex; bool painted = EyePainted; long generation = ++_eyeGeneration;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var work = await Task.Run(() => FbxEyeAuthoring.InspectGeometry(model, component.Id, island, new() { PaintedSurface = painted }, token), token);
            if (generation != _eyeGeneration || !ReferenceEquals(model, _model)) return;
            _eyeWork = work; _restoringEyes = true;
            try
            {
                if (work.Detection.Center is { } center) EyePosition.Set(center);
                EyeUseManualPivot = work.Detection.Status != EyePivotDetectionStatus.GlobeCandidate;
                EyeReviewed = false; EyeReviewEnabled = true;
            }
            finally { _restoringEyes = false; }
            string resultLabel = work.Detection.Status switch { EyePivotDetectionStatus.GlobeCandidate => "Globe fit — review required", EyePivotDetectionStatus.NeedsReview => "Manual review needed", _ => "No usable globe fit" };
            EyeStatus = $"{resultLabel}: {work.Detection.SourceControlPointIds.Length} source points. " +
                (work.Detection.Radius is { } radius ? $"Radius {radius * 1000:0.###} mm; surface error {work.Detection.NormalizedSurfaceError:P2}. " : "") +
                string.Join(" ", work.Detection.Diagnostics.Select(d => d.Message));
        }
        catch (OperationCanceledException) { if (generation == _eyeGeneration) EyeStatus = "Eye detection cancelled."; }
        catch (Exception error) when (EyeError(error)) { if (generation == _eyeGeneration) EyeStatus = "Eye fit needs review: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyEyes(); EyePreviewChanged?.Invoke(this, EventArgs.Empty); }
    }

    private bool TryCreateEyeSetup(out RigEyeSetup? setup)
    {
        setup = null;
        if (_model?.Package.Document.RiggingSession is not { } session || EyeMode is null) return false;
        try
        {
            RigEyeSetup draft = new() { Side = EyeSide, Mode = EyeMode.Mode, UserApproved = EyeReviewed,
                MorphDescriptors = EyeMorphs.Where(m => m.Selected).Select(m => m.Descriptor).ToImmutableArray(),
                Evidence = [new() { Id = $"eye-review:{EyeSide}:{EyeMode.Mode}", Kind = RigEvidenceKind.UserOverride,
                    ArtifactSha256 = session.SourceSha256, Description = "Explicit eye setup review; native gaze, camera and facial behavior require separate validation." }] };
            if (IsSourceEyeMode)
            {
                if (EyeSourceNode is null || EyeSourceNode.EffectiveBoneKind == ReAnimated.Core.Domain.BoneKind.Camera ||
                    EyeSourceNode.Name.Equals("EyeCamera", StringComparison.OrdinalIgnoreCase) || EyeSourceNode.Name.Equals("RefCamera", StringComparison.OrdinalIgnoreCase)) return false;
                draft = draft with { SourceEntityId = EyeSourceNode.EntityId, GlobalFrame = EyeSourceNode.GlobalFrame,
                    GeometryKind = EyePainted ? RigEyeGeometryKind.Painted : RigEyeGeometryKind.Unspecified };
            }
            else if (IsPlacedEyeMode)
            {
                if (EyePainted && IsGeometryEyeMode) return false;
                var frame = EyeFrame();
                draft = draft with { GlobalFrame = frame, ParentEntityId = EyeParent?.EntityId, HelperEntityId = SavedEye?.HelperEntityId,
                    DeformEntityId = SavedEye?.DeformEntityId,
                    GeometryKind = EyePainted ? RigEyeGeometryKind.Painted : RigEyeGeometryKind.ManualPivot };
                if (IsGeometryEyeMode && EyeUseManualPivot)
                {
                    var support = EyeDetection;
                    var savedSupport = SavedEye;
                    bool matchesSaved = savedSupport?.ComponentId == EyeComponent?.Id && savedSupport?.IslandIndex == EyeIslandIndex;
                    draft = draft with { ComponentId = support?.ComponentId ?? (matchesSaved ? savedSupport?.ComponentId : null),
                        IslandIndex = support?.IslandIndex ?? (matchesSaved ? savedSupport?.IslandIndex : null),
                        SourceControlPointIds = support?.SourceControlPointIds ?? (matchesSaved ? savedSupport!.SourceControlPointIds : []) };
                }
                if (IsGeometryEyeMode && !EyeUseManualPivot)
                {
                    var detected = EyeDetection;
                    var saved = SavedEye;
                    bool currentCandidate = detected?.Status == EyePivotDetectionStatus.GlobeCandidate && detected.Center == frame.Translation;
                    bool savedCandidate = detected is null && saved?.GeometryKind == RigEyeGeometryKind.GlobeCandidate &&
                        saved.ComponentId == EyeComponent?.Id && saved.IslandIndex == EyeIslandIndex && saved.GlobalFrame.Translation == frame.Translation;
                    if (!currentCandidate && !savedCandidate) return false;
                    draft = draft with { GeometryKind = RigEyeGeometryKind.GlobeCandidate, ComponentId = EyeComponent!.Id, IslandIndex = EyeIslandIndex,
                        SourceControlPointIds = currentCandidate ? detected!.SourceControlPointIds : saved!.SourceControlPointIds,
                        GlobeRadius = currentCandidate ? detected!.Radius : saved!.GlobeRadius,
                        Evidence = currentCandidate ? draft.Evidence.Add(new() { Id = "eye-fit", Kind = RigEvidenceKind.GeometryInference,
                            ArtifactSha256 = detected!.InputFingerprint,
                            Description = "Selected source island sphere fit; eye identity was reviewed separately." }) : saved!.Evidence };
                }
            }
            draft.Validate(); setup = draft; return true;
        }
        catch (Exception error) when (EyeError(error)) { return false; }
    }

    private TransformMatrix EyeFrame()
    {
        if (!EyePosition.Value.IsFinite || !EyeForward.Value.TryNormalize(out var forward)) throw new InvalidOperationException("Enter a finite position and nonzero gaze direction.");
        var upHint = EyeUp.Value;
        if (!upHint.IsFinite || !Vector3D.Cross(upHint, forward).TryNormalize(out var right)) throw new InvalidOperationException("Up and forward must be independent directions.");
        var up = Vector3D.Cross(forward, right);
        var p = EyePosition.Value;
        return new(right.X, up.X, forward.X, p.X, right.Y, up.Y, forward.Y, p.Y, right.Z, up.Z, forward.Z, p.Z, 0, 0, 0, 1);
    }

    public EyePreview? GetEyePreview()
    {
        if (IsMimicEyeMode) return null;
        try { return IsSourceEyeMode ? EyeSourceNode is { } node ? new(node.GlobalFrame, null) : null :
            new(EyeFrame(), !EyeUseManualPivot ? EyeDetection?.Radius ?? SavedEye?.GlobeRadius : null); }
        catch (Exception error) when (EyeError(error)) { return null; }
    }

    private void SaveEye(bool createHelper)
    {
        if (_model is not { } model || !CanSaveEyeSetup || !TryCreateEyeSetup(out var setup)) return;
        try
        {
            if (setup!.DeformEntityId is not null && SavedEye is { } built &&
                (!built.GlobalFrame.NearlyEquals(setup.GlobalFrame, 1e-10) || built.ParentEntityId != setup.ParentEntityId))
                throw new InvalidOperationException("Changing an existing eye bone requires a reviewed rest-transform transaction.");
            if (!createHelper && setup!.HelperEntityId is not null && SavedEye is { } previous &&
                (previous.GlobalFrame != setup.GlobalFrame || previous.ParentEntityId != setup.ParentEntityId || previous.GlobeRadius != setup.GlobeRadius))
                throw new InvalidOperationException("Use Save and create/update helper to apply a changed helper frame or bounds.");
            var token = model.Package.Document.RiggingSession!.CreateJobToken();
            var saved = FbxEyeAuthoring.SaveSetup(model, token, setup!);
            if (createHelper)
            {
                var doc = saved.Package.Document;
                doc = RigEyeHelperAuthoring.Apply(doc, doc.RiggingSession!.CreateJobToken(), setup!, EyeHelperName);
                saved = saved with { Package = saved.Package with { Document = doc }, Rig = doc.CreateRigDefinition() };
            }
            EyeModelApplyRequested?.Invoke(this, new(model, saved, createHelper ? "Saved an unweighted eye helper and its reviewed setup." : "Saved eye setup without changing source nodes or morph controls."));
            EyeStatus = createHelper ? "Eye helper saved. Skin binding and native gaze behavior require separate review." : "Eye setup saved. Facial controls and source transforms were preserved.";
        }
        catch (Exception error) when (EyeError(error)) { EyeStatus = "Eye setup was not saved: " + error.Message; }
    }
    private void RefreshEyeMetadata(FbxModelAuthoringImportResult model)
    { if (_eyeWork is { } work) _eyeWork = FbxEyeAuthoring.RefreshMetadata(work, model); RefreshEyeBindingMetadata(model); NotifyEyes(); }
    private static bool EyeError(Exception error) => error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException;
    private void NotifyEyes()
    {
        foreach (string property in new[] { nameof(EyeDetection), nameof(EyeMorphSummary), nameof(IsSourceEyeMode), nameof(IsGeometryEyeMode), nameof(IsMimicEyeMode), nameof(IsPlacedEyeMode), nameof(CanDetectEye), nameof(CanSaveEyeSetup), nameof(CanApplyEyeHelper) }) OnPropertyChanged(property);
        DetectEyeCommand?.NotifyCanExecuteChanged(); SaveEyeSetupCommand?.NotifyCanExecuteChanged(); ApplyEyeHelperCommand?.NotifyCanExecuteChanged(); NotifyEyeRig();
    }
}
