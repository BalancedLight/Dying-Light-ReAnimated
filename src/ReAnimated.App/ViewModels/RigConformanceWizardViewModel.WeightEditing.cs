using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed record WeightComponentChoice(string Id, string Name, int PointCount);
public sealed record WeightPointReview(int SourcePoint, string Before, string After, double RemovedWeight, double SourceRoundingError, bool InfluenceLocked);
public sealed record WeightOperationChoice(SkinWeightCorrectionKind Kind, string Label);

public sealed partial class RigConformanceWizardViewModel
{
    private SkinWeightAuthoringSnapshot? _weightSnapshot;
    private SkinWeightAuthoringPreview? _weightPreview;
    private long _weightJobGeneration;
    private Guid? _weightModelId;
    private Guid? _preferredWeightInfluence;
    private string? _preferredWeightComponent;
    [ObservableProperty] private SkinWeightInfluenceChoice? _selectedWeightInfluence;
    [ObservableProperty] private WeightComponentChoice? _selectedWeightComponent;
    [ObservableProperty] private WeightOperationChoice? _weightOperation;
    [ObservableProperty] private bool _showWeightHeatmap;
    [ObservableProperty] private bool _wholeWeightComponent;
    [ObservableProperty] private string _weightSourcePoints = "0";
    [ObservableProperty] private double _weightTargetValue = 1;
    [ObservableProperty] private double _weightEditStrength = 1;
    [ObservableProperty] private int _weightSmoothIterations = 1;
    [ObservableProperty] private string _weightEditingStatus = "Inspect source weights to select influences, lock fractions and preview corrections.";

    public ObservableCollection<SkinWeightInfluenceChoice> WeightInfluences { get; } = [];
    public ObservableCollection<WeightComponentChoice> WeightComponents { get; } = [];
    public ObservableCollection<WeightPointReview> WeightPointReviews { get; } = [];
    public IReadOnlyList<WeightOperationChoice> WeightOperations { get; } = [
        new(SkinWeightCorrectionKind.SetInfluence, "Set influence weight"),
        new(SkinWeightCorrectionKind.Normalize, "Normalize / limit influences"),
        new(SkinWeightCorrectionKind.Smooth, "Smooth along mesh edges"),
    ];
    public bool HasWeightEditingSource => _model?.Rig is not null;
    public bool CanInspectWeights => HasWeightEditingSource && !IsBusy;
    public bool CanCorrectWeights => _weightSnapshot is not null && !IsBusy && SelectedWeightComponent is not null && SelectedWeightInfluence is not null;
    public int? WeightHeatmapBoneIndex => ShowWeightHeatmap ? SelectedWeightInfluence?.BoneIndex : null;
    public SkinWeightAuthoringPreview? WeightPreview => _weightPreview;
    public IAsyncRelayCommand InspectWeightsCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewWeightCorrectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand ApplyWeightCorrectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand LockWeightInfluenceCommand { get; private set; } = null!;
    public IAsyncRelayCommand UnlockWeightInfluenceCommand { get; private set; } = null!;
    public IRelayCommand CancelWeightJobCommand { get; private set; } = null!;
    public event EventHandler? WeightDisplayChanged;

    private void InitializeWeightEditing()
    {
        InitializeWeightBrushing();
        WeightOperation = WeightOperations[0];
        InspectWeightsCommand = new AsyncRelayCommand(InspectWeightsAsync, () => CanInspectWeights);
        PreviewWeightCorrectionCommand = new AsyncRelayCommand(PreviewWeightsAsync, () => CanCorrectWeights && !HasUnsavedWeightMirroring);
        ApplyWeightCorrectionCommand = new AsyncRelayCommand(ApplyWeightsAsync, () => CanCorrectWeights && _weightPreview?.Correction is { CanApply: true, Changes.IsEmpty: false });
        LockWeightInfluenceCommand = new AsyncRelayCommand(token => SetWeightLockAsync(true, token), () => CanCorrectWeights);
        UnlockWeightInfluenceCommand = new AsyncRelayCommand(token => SetWeightLockAsync(false, token), () => CanCorrectWeights);
        CancelWeightJobCommand = new RelayCommand(CancelWeightJobs, () => InspectWeightsCommand.IsRunning || PreviewWeightCorrectionCommand.IsRunning || ApplyWeightCorrectionCommand.IsRunning || _brushCommitCancellation is not null);
        foreach (var command in new[] { InspectWeightsCommand, PreviewWeightCorrectionCommand, ApplyWeightCorrectionCommand })
            command.PropertyChanged += (_, _) => CancelWeightJobCommand.NotifyCanExecuteChanged();
    }

    private void ResetWeightEditing()
    {
        CancelWeightBrush();
        _weightBrushSurface = null;
        _weightJobGeneration++;
        CancelWeightJobs();
        if (_weightModelId != _model?.Package.Document.ModelId)
        { _preferredWeightInfluence = null; _preferredWeightComponent = null; ShowWeightHeatmap = false; WholeWeightComponent = false; WeightSourcePoints = "0"; WeightBrushEnabled = false; }
        _weightModelId = _model?.Package.Document.ModelId;
        _weightSnapshot = null; _weightPreview = null;
        WeightInfluences.Clear(); WeightComponents.Clear(); WeightPointReviews.Clear();
        SelectedWeightInfluence = null; SelectedWeightComponent = null;
        WeightEditingStatus = "Inspect source weights to select influences, lock fractions and preview corrections.";
        NotifyWeightEditing(); WeightDisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelWeightJobs()
    { InspectWeightsCommand?.Cancel(); PreviewWeightCorrectionCommand?.Cancel(); ApplyWeightCorrectionCommand?.Cancel(); _brushCommitCancellation?.Cancel(); }

    private void RefreshWeightMetadata(FbxModelAuthoringImportResult current)
    {
        if (_weightSnapshot is { } snapshot && FbxSkinWeightAuthoring.TryRefreshMetadata(snapshot, _weightPreview, current, out var refreshed, out var preview))
        { _weightSnapshot = refreshed; _weightPreview = preview; RefreshWeightBrushMetadata(current, refreshed); return; }
        ResetWeightEditing();
    }

    private async Task InspectWeightsAsync(CancellationToken token)
    {
        RouteStudioAction(RigStudioStage.Skin);
        if (_model is not { Rig: not null } source) return;
        long generation = ++_weightJobGeneration;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var inspected = await Task.Run(() => {
                var snapshot = FbxSkinWeightAuthoring.Inspect(source, token);
                return (Snapshot: snapshot, Surface: SkinWeightBrushSurface.Build(snapshot.Points, snapshot.Positions, snapshot.Triangles, token));
            }, token);
            var snapshot = inspected.Snapshot;
            if (token.IsCancellationRequested || generation != _weightJobGeneration || !ReferenceEquals(_model, source)) return;
            _weightSnapshot = snapshot; _weightPreview = null;
            _weightBrushSurface = inspected.Surface;
            WeightInfluences.Clear(); WeightComponents.Clear(); WeightPointReviews.Clear();
            foreach (var influence in snapshot.Influences) WeightInfluences.Add(influence);
            foreach (var component in snapshot.Points.GroupBy(static p => p.ComponentId))
                WeightComponents.Add(new(component.Key, snapshot.Session.Components.FirstOrDefault(c => c.Id == component.Key)?.DisplayName ?? component.Key, component.Count()));
            SelectedWeightInfluence = WeightInfluences.FirstOrDefault(i => i.EntityId == _preferredWeightInfluence) ?? WeightInfluences.FirstOrDefault();
            SelectedWeightComponent = WeightComponents.FirstOrDefault(c => c.Id == _preferredWeightComponent) ?? WeightComponents.FirstOrDefault();
            RestoreWeightMirroring();
            WeightEditingStatus = $"Observed {snapshot.Points.Length} original points and {snapshot.Session.WeightLocks.Length} saved influence locks. Viewing does not change weights.";
        }
        catch (OperationCanceledException) { WeightEditingStatus = "Weight inspection cancelled."; }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Weights need attention: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyWeightEditing(); WeightDisplayChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task PreviewWeightsAsync(CancellationToken token)
    {
        if (_weightSnapshot is not { } snapshot || WeightOperation is not { } operation || _model is not { } source) return;
        long generation = ++_weightJobGeneration;
        try
        {
            var selection = SelectedWeightPoints().Select(i => new SkinWeightCorrectionSelection(i, WeightEditStrength)).ToArray();
            var options = new SkinWeightCorrectionOptions { Kind = operation.Kind, TargetInfluence = SelectedWeightInfluence?.EntityId,
                TargetWeight = WeightTargetValue, SmoothingIterations = WeightSmoothIterations };
            IsBusy = true; NotifyStateChanged();
            var preview = await Task.Run(() => FbxSkinWeightAuthoring.Preview(snapshot, selection, options, token), token);
            if (token.IsCancellationRequested || generation != _weightJobGeneration || !ReferenceEquals(_model, source)) return;
            _weightPreview = preview;
            WeightPointReviews.Clear();
            var names = snapshot.Influences.ToDictionary(static b => b.EntityId, static b => b.Name);
            foreach (var change in preview.Correction.Changes.Take(1000))
                WeightPointReviews.Add(new(snapshot.Points[change.PointIndex].ControlPointIndex,
                    DescribeWeights(change.Before, names), DescribeWeights(change.After, names), change.RemovedWeightBeforeNormalization,
                    Dl1SkinWeightQuantization.MaximumNormalizedError(change.After.Select(static w => w.Weight).ToArray()),
                    snapshot.Points[change.PointIndex].LockedInfluences.Contains(SelectedWeightInfluence!.EntityId)));
            string diagnostics = string.Join(" ", preview.Correction.Diagnostics.Take(4).Select(static d => d.Message));
            WeightEditingStatus = $"Preview: {preview.Correction.Changes.Length} changed source points. {diagnostics}" +
                (preview.Correction.Changes.Length > 1000 ? " Showing the first 1,000 changes." : string.Empty);
        }
        catch (OperationCanceledException) { WeightEditingStatus = "Correction preview cancelled."; }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Correction was not prepared: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyWeightEditing(); WeightDisplayChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task ApplyWeightsAsync(CancellationToken token)
    {
        if (_model is not { } source || _weightPreview is not { } preview) return;
        long generation = ++_weightJobGeneration;
        bool applied = false;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var result = await Task.Run(() => FbxSkinWeightAuthoring.TryApply(source, preview, out var updated, token) ? updated : null, token);
            if (result is null || token.IsCancellationRequested || generation != _weightJobGeneration || !ReferenceEquals(_model, source)) return;
            if (!ReferenceEquals(result, source)) BodyModelApplyRequested?.Invoke(this, new(source, result, "Saved source-point weight correction. Deformation review remains required."));
            applied = true;
        }
        catch (OperationCanceledException) { WeightEditingStatus = "Weight correction cancelled before commit."; }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Weights were not changed: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyWeightEditing(); }
        if (applied) await InspectWeightsAsync(CancellationToken.None);
    }

    private async Task SetWeightLockAsync(bool locked, CancellationToken token)
    {
        if (_model is not { } source || _weightSnapshot is not { } snapshot || SelectedWeightInfluence is not { } influence) return;
        try
        {
            if (!FbxSkinWeightAuthoring.TrySetLocks(source, snapshot, SelectedWeightPoints(), influence.EntityId, locked, out var updated)) return;
            if (!ReferenceEquals(updated, source)) BodyModelApplyRequested?.Invoke(this, new(source, updated, locked ? "Locked selected influence fractions, including zero weights." : "Unlocked selected influence fractions."));
            await InspectWeightsAsync(token);
        }
        catch (Exception error) when (IsWeightError(error)) { WeightEditingStatus = "Locks were not changed: " + error.Message; }
    }

    private int[] SelectedWeightPoints()
    {
        if (_weightSnapshot is not { } snapshot || SelectedWeightComponent is not { } component) throw new InvalidOperationException("Inspect and select a component first.");
        var candidates = snapshot.Points.Select((point, index) => (point, index)).Where(p => p.point.ComponentId == component.Id).ToArray();
        if (WholeWeightComponent) return candidates.Select(static p => p.index).ToArray();
        var available = candidates.ToDictionary(static p => p.point.ControlPointIndex, static p => p.index);
        var selected = new HashSet<int>();
        foreach (string token in WeightSourcePoints.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] range = token.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out int start) ||
                !int.TryParse(range[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int end) || end < start || (long)end - start > 250_000)
                throw new InvalidOperationException("Use source point numbers or inclusive ranges, such as 0, 4, 10-20.");
            for (long point = start; point <= end; point++)
            {
                if (!available.TryGetValue((int)point, out int index)) throw new InvalidOperationException($"Source point {point} is not rendered in this component.");
                selected.Add(index);
            }
        }
        if (selected.Count == 0) throw new InvalidOperationException("Select source point numbers or the whole component.");
        return selected.Order().ToArray();
    }

    private static string DescribeWeights(ImmutableArray<GeneratedSkinInfluence> weights, Dictionary<Guid, string> names) =>
        string.Join("; ", weights.Select(w => string.Create(CultureInfo.InvariantCulture, $"{names[w.HandleId]}={w.Weight:0.####}")));
    private static bool IsWeightError(Exception error) => error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or CustomModelFormatException;
    private void InvalidateWeightPreview()
    {
        CancelWeightBrush();
        _weightJobGeneration++; PreviewWeightCorrectionCommand?.Cancel();
        _weightPreview = null; WeightPointReviews.Clear(); NotifyWeightEditing(); WeightDisplayChanged?.Invoke(this, EventArgs.Empty);
        ShowCurrentWeights();
    }
    private void ShowCurrentWeights()
    {
        if (_weightSnapshot is not { } snapshot || SelectedWeightComponent is not { } component || SelectedWeightInfluence is not { } influence) return;
        var names = snapshot.Influences.ToDictionary(static b => b.EntityId, static b => b.Name);
        foreach (var point in snapshot.Points.Where(p => p.ComponentId == component.Id).Take(1000))
            WeightPointReviews.Add(new(point.ControlPointIndex, DescribeWeights(point.Weights, names), string.Empty, 0,
                point.Weights.IsEmpty ? 0 : Dl1SkinWeightQuantization.MaximumNormalizedError(point.Weights.Select(static w => w.Weight).ToArray()),
                point.LockedInfluences.Contains(influence.EntityId)));
    }
    partial void OnSelectedWeightInfluenceChanged(SkinWeightInfluenceChoice? value) { if (value is not null) _preferredWeightInfluence = value.EntityId; InvalidateWeightPreview(); RestoreWeightMirroring(); }
    partial void OnSelectedWeightComponentChanged(WeightComponentChoice? value) { if (value is not null) _preferredWeightComponent = value.Id; InvalidateWeightPreview(); }
    partial void OnShowWeightHeatmapChanged(bool value) => WeightDisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnWholeWeightComponentChanged(bool value) => InvalidateWeightPreview();
    partial void OnWeightSourcePointsChanged(string value) => InvalidateWeightPreview();
    partial void OnWeightOperationChanged(WeightOperationChoice? value) => InvalidateWeightPreview();
    partial void OnWeightTargetValueChanged(double value) => InvalidateWeightPreview();
    partial void OnWeightEditStrengthChanged(double value) => InvalidateWeightPreview();
    partial void OnWeightSmoothIterationsChanged(int value) => InvalidateWeightPreview();
    private void NotifyWeightEditing()
    {
        OnPropertyChanged(nameof(HasWeightEditingSource)); OnPropertyChanged(nameof(CanInspectWeights)); OnPropertyChanged(nameof(CanCorrectWeights));
        InspectWeightsCommand?.NotifyCanExecuteChanged(); PreviewWeightCorrectionCommand?.NotifyCanExecuteChanged(); ApplyWeightCorrectionCommand?.NotifyCanExecuteChanged();
        LockWeightInfluenceCommand?.NotifyCanExecuteChanged(); UnlockWeightInfluenceCommand?.NotifyCanExecuteChanged(); CancelWeightJobCommand?.NotifyCanExecuteChanged();
        SaveWeightMirroringCommand?.NotifyCanExecuteChanged();
    }
}
