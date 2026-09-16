using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private RegionalHandBindingPreview? _regionalHandBinding;
    private long _handBindingGeneration;
    [ObservableProperty] private double _handBindingFalloff = 2;
    [ObservableProperty] private int _handBindingSegment = 1;
    [ObservableProperty] private string _handBindingStatus = "Preview weights after appending the reviewed finger rig. Outside points and locks are preserved.";
    public IAsyncRelayCommand PreviewHandBindingCommand { get; private set; } = null!;
    public IAsyncRelayCommand ApplyHandBindingCommand { get; private set; } = null!;
    public IRelayCommand ClearHandBindingCommand { get; private set; } = null!;
    public IRelayCommand CancelHandBindingCommand { get; private set; } = null!;
    public event EventHandler? HandBindingDisplayChanged;
    public RegionalHandBindingPreview? RegionalHandBinding => _regionalHandBinding;
    public SkinWeightAuthoringPreview? HandBindingWeightPreview => _regionalHandBinding?.WeightPreview;
    public bool CanPreviewHandBinding => CanBuildHandRig && HandComponent is not null && _model!.Package.Document.Bones.Any(b =>
        b.Name.StartsWith("finger_" + HandSide.ToString().ToLowerInvariant() + "_", StringComparison.Ordinal));
    public bool CanApplyHandBinding => !IsBusy && _regionalHandBinding is { CanApply: true, ChangedPointCount: > 0 };
    public int? HandBindingHeatmapBoneIndex => _regionalHandBinding is null || SelectedHandDigit is null ? null :
        _model?.Package.Document.Bones.FirstOrDefault(b => b.Name == $"finger_{HandSide.ToString().ToLowerInvariant()}_{SelectedHandDigit.Id}_{HandBindingSegment}")?.Index;

    private void InitializeHandBinding()
    {
        PreviewHandBindingCommand = new AsyncRelayCommand(PreviewHandBindingAsync, () => CanPreviewHandBinding);
        ApplyHandBindingCommand = new AsyncRelayCommand(ApplyHandBindingAsync, () => CanApplyHandBinding);
        ClearHandBindingCommand = new RelayCommand(ClearHandBinding, () => _regionalHandBinding is not null);
        CancelHandBindingCommand = new RelayCommand(() => { PreviewHandBindingCommand.Cancel(); ApplyHandBindingCommand.Cancel(); },
            () => PreviewHandBindingCommand.IsRunning || ApplyHandBindingCommand.IsRunning);
        foreach (var command in new[] { PreviewHandBindingCommand, ApplyHandBindingCommand }) command.PropertyChanged += (_, _) => CancelHandBindingCommand.NotifyCanExecuteChanged();
    }
    private void ClearHandBinding()
    {
        _handBindingGeneration++; PreviewHandBindingCommand?.Cancel(); ApplyHandBindingCommand?.Cancel();
        _regionalHandBinding = null; NotifyHandBinding(); HandBindingDisplayChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnHandBindingFalloffChanged(double value) => ClearHandBinding();
    partial void OnHandBindingSegmentChanged(int value) => HandBindingDisplayChanged?.Invoke(this, EventArgs.Empty);

    private async Task PreviewHandBindingAsync(CancellationToken token)
    {
        if (_model is not { } model || HandComponent is not { } component) return;
        var side = HandSide; var minimum = HandMinimum.Value; var maximum = HandMaximum.Value;
        int resolution = HandResolution; double falloff = HandBindingFalloff; long generation = ++_handBindingGeneration;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var preview = await Task.Run(() => FbxRegionalHandBinding.Preview(model, side, component.Id, minimum, maximum,
                new SourceVolumeGridOptions { LongestAxisCells = resolution }, new AutomaticSkinBindingOptions { DistancePower = falloff }, token), token);
            if (generation != _handBindingGeneration || !ReferenceEquals(_model, model)) return;
            _regionalHandBinding = preview; HandReviewEnabled = true;
            HandBindingStatus = preview.CanApply ? $"{preview.SelectedPointCount} selected points; {preview.ChangedPointCount} proposed changes. Blue 0 · yellow 0.5 · red 1 for the selected digit segment. Apply explicitly, then inspect deformation." :
                "Some selected hand points remain unassigned: " + string.Join(" ", preview.Binding.Diagnostics.Select(static d => d.Message).Take(5));
        }
        catch (OperationCanceledException) { if (generation == _handBindingGeneration) HandBindingStatus = "Hand binding cancelled."; }
        catch (Exception error) when (HandError(error)) { if (generation == _handBindingGeneration) HandBindingStatus = "Hand weights were not changed: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyHandBinding(); HandBindingDisplayChanged?.Invoke(this, EventArgs.Empty); }
    }
    private async Task ApplyHandBindingAsync(CancellationToken token)
    {
        if (_model is not { } model || _regionalHandBinding is not { CanApply: true } preview) return;
        long generation = ++_handBindingGeneration; IsBusy = true; NotifyStateChanged();
        try
        {
            var result = await Task.Run(() => FbxRegionalHandBinding.TryApply(model, preview, out var updated, token) ? updated : null, token);
            if (generation != _handBindingGeneration || !ReferenceEquals(_model, model)) return;
            if (result is null) { HandBindingStatus = "The source or rig changed; preview current hand weights again."; return; }
            if (!ReferenceEquals(result, model)) HandModelApplyRequested?.Invoke(this, new(model, result, "Applied regional hand weights. Outside-point weights and locked fractions were retained."));
            HandBindingStatus = "Regional hand weights saved. Review open, curled and wrist poses before acceptance.";
        }
        catch (OperationCanceledException) { HandBindingStatus = "Hand weight application cancelled."; }
        catch (Exception error) when (HandError(error)) { HandBindingStatus = "Hand weights were not applied: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyHandBinding(); HandBindingDisplayChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void RefreshHandBindingMetadata(FbxModelAuthoringImportResult current)
    { if (_regionalHandBinding is { } preview) _regionalHandBinding = FbxRegionalHandBinding.RefreshMetadata(preview, current); NotifyHandBinding(); }
    private void NotifyHandBinding()
    {
        foreach (var name in new[] { nameof(RegionalHandBinding), nameof(HandBindingWeightPreview), nameof(HandBindingHeatmapBoneIndex), nameof(CanPreviewHandBinding), nameof(CanApplyHandBinding) }) OnPropertyChanged(name);
        PreviewHandBindingCommand?.NotifyCanExecuteChanged(); ApplyHandBindingCommand?.NotifyCanExecuteChanged(); ClearHandBindingCommand?.NotifyCanExecuteChanged();
    }
}
