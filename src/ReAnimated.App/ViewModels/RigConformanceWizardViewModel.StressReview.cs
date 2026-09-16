using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed record StressJointChoice(int Index, string Name) { public string Label => $"{Name}  [#{Index}]"; }
public sealed record StressJointOffset(int Index, string Name, double X, double Y, double Z);
public sealed record StressMorphOffset(string Name, double Weight);

public sealed partial class RigConformanceWizardViewModel
{
    private ImmutableArray<CustomModelBone> _stressBones = [];
    private Guid? _stressModelId;
    private string? _stressSourceHash;
    private string? _stressMorphSignature;
    private DispatcherTimer? _stressTimer;
    private readonly Stopwatch _stressClock = new();
    private Func<CancellationToken, Task<StressDeformationReport>>? _measureStress;
    [ObservableProperty] private bool _stressPreviewEnabled;
    [ObservableProperty] private StressJointChoice? _selectedStressJoint;
    [ObservableProperty] private double _stressAngleX;
    [ObservableProperty] private double _stressAngleY;
    [ObservableProperty] private double _stressAngleZ;
    [ObservableProperty] private double _stressAmount = 1;
    [ObservableProperty] private double _stressCycleSeconds = 3;
    [ObservableProperty] private bool _isStressCycling;
    [ObservableProperty] private string? _selectedStressMorph;
    [ObservableProperty] private double _stressMorphValue;
    [ObservableProperty] private bool _scaleStressMorphsWithAmount;
    [ObservableProperty] private string _stressReviewStatus = "Choose a joint and preview local rotations without changing the saved rig.";
    [ObservableProperty] private string _stressMeasurementStatus = "Measurements compare the same morphed mesh at rest and in the current stress pose. They do not approve deformation quality.";
    public ObservableCollection<StressJointChoice> StressJoints { get; } = [];
    public ObservableCollection<StressJointOffset> StressOffsets { get; } = [];
    public ObservableCollection<string> StressMorphChoices { get; } = [];
    public ObservableCollection<StressMorphOffset> StressMorphOffsets { get; } = [];
    public bool HasStressReviewSource => _model?.Rig is not null;
    public bool CanEditStressReview => HasStressReviewSource && !IsBusy;
    public IRelayCommand SetJointStressCommand { get; private set; } = null!;
    public IRelayCommand RemoveJointStressCommand { get; private set; } = null!;
    public IRelayCommand SetStressMorphCommand { get; private set; } = null!;
    public IRelayCommand ReturnStressToRestCommand { get; private set; } = null!;
    public IRelayCommand ClearStressOffsetsCommand { get; private set; } = null!;
    public IRelayCommand ToggleStressCycleCommand { get; private set; } = null!;
    public IAsyncRelayCommand MeasureStressCommand { get; private set; } = null!;
    public event EventHandler? StressPreviewChanged;

    private void InitializeStressReview()
    {
        MeasureStressCommand = new AsyncRelayCommand(MeasureStressAsync, () => CanEditStressReview && StressPreviewEnabled && !IsStressCycling && _measureStress is not null);
        StressPreviewChanged += (_, _) => {
            MeasureStressCommand.Cancel();
            if (!MeasureStressCommand.IsRunning) StressMeasurementStatus = "Pose inputs changed. Measure the current deformation after pausing the cycle.";
        };
        SetJointStressCommand = new RelayCommand(SetJointStress, () => CanEditStressReview && SelectedStressJoint is not null);
        RemoveJointStressCommand = new RelayCommand(() => {
            if (SelectedStressJoint is { } selected && StressOffsets.FirstOrDefault(o => o.Index == selected.Index) is { } offset) StressOffsets.Remove(offset);
            StressPreviewChanged?.Invoke(this, EventArgs.Empty);
        }, () => CanEditStressReview && SelectedStressJoint is not null);
        SetStressMorphCommand = new RelayCommand(SetStressMorph, () => CanEditStressReview && SelectedStressMorph is not null);
        ReturnStressToRestCommand = new RelayCommand(() => { StopStressCycle(); StressAmount = 0; }, () => CanEditStressReview);
        ClearStressOffsetsCommand = new RelayCommand(() => {
            StopStressCycle(); StressOffsets.Clear(); StressMorphOffsets.Clear(); StressAngleX = StressAngleY = StressAngleZ = 0; StressMorphValue = 0; StressAmount = 1;
            StressPreviewChanged?.Invoke(this, EventArgs.Empty);
        }, () => CanEditStressReview);
        ToggleStressCycleCommand = new RelayCommand(() => {
            if (IsStressCycling) { StopStressCycle(); return; }
            if (!double.IsFinite(StressCycleSeconds) || StressCycleSeconds is < .5 or > 30) { StressReviewStatus = "Choose a cycle duration from 0.5 to 30 seconds."; return; }
            StressPreviewEnabled = true; IsStressCycling = true; StressAmount = 0; _stressClock.Restart();
            _stressTimer ??= CreateStressTimer(); _stressTimer.Start();
        }, () => CanEditStressReview);
    }

    private DispatcherTimer CreateStressTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += OnStressTimerTick;
        return timer;
    }
    private void OnStressTimerTick(object? sender, EventArgs e)
    {
        if (!StressPreviewEnabled || !CanEditStressReview) { StopStressCycle(); return; }
        StressAmount = RigStressPose.CycleAmount(_stressClock.Elapsed.TotalSeconds, StressCycleSeconds);
    }
    private void StopStressCycle() { _stressTimer?.Stop(); _stressClock.Stop(); IsStressCycling = false; }
    public void StopStressReview()
    {
        MeasureStressCommand?.Cancel();
        StopStressCycle(); StressPreviewEnabled = false;
        if (_stressTimer is { } timer) { timer.Tick -= OnStressTimerTick; _stressTimer = null; }
    }

    private void RefreshStressReviewModel()
    {
        var bones = _model?.Package.Document.CreateEffectiveBones() ?? [];
        bool same = _stressModelId == _model?.Package.Document.ModelId && _stressSourceHash == _model?.Package.Document.Source.ContentSha256 &&
            _stressMorphSignature == _model?.Package.Document.MorphSignature &&
            _stressBones.Length == bones.Length && _stressBones.Zip(bones).All(pair => pair.First.Name == pair.Second.Name && pair.First.ParentIndex == pair.Second.ParentIndex &&
                pair.First.ExactLocalBindMatrix == pair.Second.ExactLocalBindMatrix && pair.First.LocalBindTransform == pair.Second.LocalBindTransform);
        _stressModelId = _model?.Package.Document.ModelId; _stressSourceHash = _model?.Package.Document.Source.ContentSha256; _stressBones = bones;
        _stressMorphSignature = _model?.Package.Document.MorphSignature;
        if (!same)
        {
            StopStressReview(); StressOffsets.Clear(); StressMorphOffsets.Clear(); StressAmount = 1;
            StressJoints.Clear(); StressMorphChoices.Clear();
            foreach (var bone in bones) StressJoints.Add(new(bone.Index, bone.Name));
            foreach (var morph in _model?.Package.Document.MorphChannels ?? []) StressMorphChoices.Add(morph.Name);
            SelectedStressJoint = StressJoints.FirstOrDefault(); SelectedStressMorph = StressMorphChoices.FirstOrDefault();
            StressReviewStatus = "Choose a joint and preview local rotations without changing the saved rig.";
        }
        NotifyStressReview();
    }

    private void SetJointStress()
    {
        if (StudioStage != RigStudioStage.Animate) RouteStudioAction(RigStudioStage.Skin);
        if (SelectedStressJoint is not { } selected) return;
        var angles = new Vector3D(StressAngleX, StressAngleY, StressAngleZ);
        if (!angles.IsFinite || Math.Abs(angles.X) > 360 || Math.Abs(angles.Y) > 360 || Math.Abs(angles.Z) > 360)
        { StressReviewStatus = "Use finite angles from -360 to 360 degrees."; return; }
        var previous = StressOffsets.FirstOrDefault(p => p.Index == selected.Index);
        if (previous is null && angles != Vector3D.Zero && StressOffsets.Count >= 64) { StressReviewStatus = "A stress review can contain at most 64 selected joints."; return; }
        if (previous is not null) StressOffsets.Remove(previous);
        if (angles != Vector3D.Zero) StressOffsets.Add(new(selected.Index, selected.Name, angles.X, angles.Y, angles.Z));
        StressPreviewEnabled = true;
        StressReviewStatus = "Previewing local X, then Y, then Z rotations. This does not change rest transforms, weights or animation tracks.";
        StressPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStressMorph()
    {
        if (SelectedStressMorph is not { } name || !StressMorphChoices.Contains(name)) return;
        if (!double.IsFinite(StressMorphValue) || StressMorphValue is < -1 or > 1) { StressReviewStatus = "Use a finite morph weight from -1 to 1."; return; }
        var previous = StressMorphOffsets.FirstOrDefault(m => m.Name == name);
        if (previous is null && StressMorphValue != 0 && StressMorphOffsets.Count >= MorphTargetSelection.MaximumActiveTargetCount)
        { StressReviewStatus = "This preview supports up to 64 active morph targets."; return; }
        if (previous is not null) StressMorphOffsets.Remove(previous);
        if (StressMorphValue != 0) StressMorphOffsets.Add(new(name, StressMorphValue));
        StressPreviewEnabled = true; StressPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryGetStressPreview(out SkeletonPose? pose, out ImmutableArray<MorphWeight> morphs)
    {
        pose = null; morphs = [];
        if (!StressPreviewEnabled || _model?.Rig is not { } rig) return false;
        try
        {
            pose = RigStressPose.Evaluate(_model.Package.Document, rig, StressOffsets.Select(static o => new RigStressJointRotation(o.Index, new(o.X, o.Y, o.Z))).ToArray(), StressAmount);
            morphs = StressMorphOffsets.Select(m => new MorphWeight(m.Name, (float)(m.Weight * (ScaleStressMorphsWithAmount ? StressAmount : 1)))).ToImmutableArray();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        { StressReviewStatus = "Stress preview unavailable: " + error.Message; StopStressCycle(); return false; }
    }

    public void SetStressMeasurement(Func<CancellationToken, Task<StressDeformationReport>> measure) { _measureStress = measure; MeasureStressCommand.NotifyCanExecuteChanged(); }
    private async Task MeasureStressAsync(CancellationToken token)
    {
        if (_measureStress is null) return;
        StressMeasurementStatus = "Measuring current pose…";
        try
        {
            var report = await _measureStress(token);
            if (token.IsCancellationRequested) return;
            StressMeasurementStatus = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{report.VertexSamples:N0} vertex samples; maximum movement {report.MaximumDisplacement:0.#####} m; edge ratios {report.MinimumEdgeRatio:0.###}–{report.MaximumEdgeRatio:0.###}. Collapsed edges: {report.CollapsedPosedEdges}; opened zero-length edges: {report.OpenedDegenerateEdges}; finite normals: {report.FiniteNormals}. Visual review is still required.");
        }
        catch (OperationCanceledException) { StressMeasurementStatus = "Measurement cancelled after the pose or model changed."; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException)
        { StressMeasurementStatus = "Measurement unavailable: " + error.Message; }
    }

    partial void OnSelectedStressJointChanged(StressJointChoice? value)
    {
        var row = StressOffsets.FirstOrDefault(o => o.Index == value?.Index);
        StressAngleX = row?.X ?? 0; StressAngleY = row?.Y ?? 0; StressAngleZ = row?.Z ?? 0; NotifyStressReview();
    }
    partial void OnSelectedStressMorphChanged(string? value) { StressMorphValue = StressMorphOffsets.FirstOrDefault(m => m.Name == value)?.Weight ?? 0; NotifyStressReview(); }
    partial void OnStressPreviewEnabledChanged(bool value)
    {
        MeasureStressCommand?.Cancel(); MeasureStressCommand?.NotifyCanExecuteChanged();
        if (value) { WeightBrushEnabled = false; CancelWeightBrush(); }
        else StopStressCycle();
        StressPreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnStressAmountChanged(double value) { MeasureStressCommand?.Cancel(); StressPreviewChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnScaleStressMorphsWithAmountChanged(bool value) { MeasureStressCommand?.Cancel(); StressPreviewChanged?.Invoke(this, EventArgs.Empty); }
    partial void OnIsStressCyclingChanged(bool value) => MeasureStressCommand?.NotifyCanExecuteChanged();
    partial void OnStressCycleSecondsChanged(double value) => StopStressCycle();
    private void NotifyStressReview()
    {
        OnPropertyChanged(nameof(HasStressReviewSource)); OnPropertyChanged(nameof(CanEditStressReview));
        SetJointStressCommand?.NotifyCanExecuteChanged(); RemoveJointStressCommand?.NotifyCanExecuteChanged(); SetStressMorphCommand?.NotifyCanExecuteChanged();
        ReturnStressToRestCommand?.NotifyCanExecuteChanged(); ClearStressOffsetsCommand?.NotifyCanExecuteChanged(); ToggleStressCycleCommand?.NotifyCanExecuteChanged();
        MeasureStressCommand?.NotifyCanExecuteChanged();
    }
}
