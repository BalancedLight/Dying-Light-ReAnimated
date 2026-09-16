using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private RigidEyeBindingPreview? _eyeBinding;
    private long _eyeBindingGeneration;
    [ObservableProperty] private string _eyeBoneName = "eye_left";
    [ObservableProperty] private string _eyeRigStatus = "Save the reviewed pivot and selected geometry, then create an eye bone and preview its binding.";
    [ObservableProperty] private bool _eyeMotionReviewEnabled;
    [ObservableProperty] private double _eyeYawDegrees;
    [ObservableProperty] private double _eyePitchDegrees;
    public IAsyncRelayCommand BuildEyeRigCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewEyeBindingCommand { get; private set; } = null!;
    public IAsyncRelayCommand ApplyEyeBindingCommand { get; private set; } = null!;
    public IRelayCommand CancelEyeRigCommand { get; private set; } = null!;
    public IRelayCommand ResetEyeMotionCommand { get; private set; } = null!;
    public RigidEyeBindingPreview? EyeBinding => _eyeBinding;
    public SkinWeightAuthoringPreview? EyeBindingWeightPreview => _eyeBinding?.WeightPreview;
    public int? EyeBindingHeatmapBoneIndex => _eyeBinding?.WeightPreview.Influences.FirstOrDefault(i => i.EntityId == _eyeBinding.EyeEntityId)?.BoneIndex;
    public bool HasEyeDeformBone => IsGeometryEyeMode && SavedEye?.DeformEntityId is not null;
    public bool CanBuildEyeRig => !IsBusy && IsGeometryEyeMode && EyeReviewed && !string.IsNullOrWhiteSpace(EyeBoneName) &&
        SavedEye is { UserApproved: true, ComponentId: not null, IslandIndex: not null, ParentEntityId: not null, SourceControlPointIds.IsEmpty: false } saved &&
        TryCreateEyeSetup(out var draft) && draft!.GlobalFrame.NearlyEquals(saved.GlobalFrame, 1e-10) && draft.ParentEntityId == saved.ParentEntityId &&
        draft.ComponentId == saved.ComponentId && draft.IslandIndex == saved.IslandIndex &&
        EyeParent?.EffectiveBoneKind is BoneKind.Root or BoneKind.Deform;
    public bool CanPreviewEyeBinding => CanBuildEyeRig && HasEyeDeformBone;
    public bool CanApplyEyeBinding => !IsBusy && _eyeBinding is { ChangedPointCount: > 0 };
    public event EventHandler? EyeBindingDisplayChanged;

    private void InitializeEyeRig()
    {
        BuildEyeRigCommand = new AsyncRelayCommand(BuildEyeRigAsync, () => CanBuildEyeRig);
        PreviewEyeBindingCommand = new AsyncRelayCommand(PreviewEyeBindingAsync, () => CanPreviewEyeBinding);
        ApplyEyeBindingCommand = new AsyncRelayCommand(ApplyEyeBindingAsync, () => CanApplyEyeBinding);
        CancelEyeRigCommand = new RelayCommand(() => { BuildEyeRigCommand.Cancel(); PreviewEyeBindingCommand.Cancel(); ApplyEyeBindingCommand.Cancel(); },
            () => BuildEyeRigCommand.IsRunning || PreviewEyeBindingCommand.IsRunning || ApplyEyeBindingCommand.IsRunning);
        ResetEyeMotionCommand = new RelayCommand(() => { EyeYawDegrees = 0; EyePitchDegrees = 0; EyeMotionReviewEnabled = false; });
        foreach (var command in new[] { BuildEyeRigCommand, PreviewEyeBindingCommand, ApplyEyeBindingCommand }) command.PropertyChanged += (_, _) => CancelEyeRigCommand.NotifyCanExecuteChanged();
    }
    private void ClearEyeBinding()
    {
        _eyeBindingGeneration++; _eyeBinding = null;
        BuildEyeRigCommand?.Cancel(); PreviewEyeBindingCommand?.Cancel(); ApplyEyeBindingCommand?.Cancel();
        NotifyEyeRig(); EyeBindingDisplayChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnEyeBoneNameChanged(string value) { if (!_restoringEyes) { ClearEyeBinding(); NotifyEyeRig(); } }
    partial void OnEyeMotionReviewEnabledChanged(bool value)
    {
        if (value) { EyeReviewEnabled = false; StressPreviewEnabled = false; }
        EyePreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnEyeYawDegreesChanged(double value) => EyePreviewChanged?.Invoke(this, EventArgs.Empty);
    partial void OnEyePitchDegreesChanged(double value) => EyePreviewChanged?.Invoke(this, EventArgs.Empty);

    private async Task BuildEyeRigAsync(CancellationToken token)
    {
        if (_model is not { } model || !CanBuildEyeRig) return;
        var side = EyeSide; string name = EyeBoneName.Trim(); IsBusy = true; NotifyStateChanged();
        try
        {
            var result = await Task.Run(() => FbxGeneratedEyeAuthoring.Append(model, side, name, token), token);
            if (!ReferenceEquals(model, _model)) return;
            if (!ReferenceEquals(result, model)) EyeModelApplyRequested?.Invoke(this, new(model, result, "Created the reviewed eye bone. Existing skinning is preserved until the selected eye is explicitly bound."));
            EyeRigStatus = "Eye bone saved. Preview and apply the selected eye binding before reviewing its motion.";
        }
        catch (OperationCanceledException) { EyeRigStatus = "Eye bone creation cancelled."; }
        catch (Exception error) when (EyeError(error)) { EyeRigStatus = "Eye bone was not changed: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyEyeRig(); }
    }
    private async Task PreviewEyeBindingAsync(CancellationToken token)
    {
        if (_model is not { } model) return;
        var side = EyeSide; long generation = ++_eyeBindingGeneration; IsBusy = true; NotifyStateChanged();
        try
        {
            var preview = await Task.Run(() => FbxRigidEyeBinding.Preview(model, side, token), token);
            if (generation != _eyeBindingGeneration || !ReferenceEquals(model, _model)) return;
            _eyeBinding = preview; EyeReviewEnabled = true; EyeMotionReviewEnabled = false;
            EyeRigStatus = $"{preview.SelectedPointCount} selected eye points; {preview.ChangedPointCount} proposed changes. Red marks full eye-bone weight. Other islands are preserved.";
        }
        catch (OperationCanceledException) { EyeRigStatus = "Eye binding preview cancelled."; }
        catch (Exception error) when (EyeError(error)) { EyeRigStatus = "Eye weights were not changed: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyEyeRig(); EyeBindingDisplayChanged?.Invoke(this, EventArgs.Empty); }
    }
    private async Task ApplyEyeBindingAsync(CancellationToken token)
    {
        if (_model is not { } model || _eyeBinding is not { } preview) return;
        long generation = ++_eyeBindingGeneration; IsBusy = true; NotifyStateChanged();
        try
        {
            var result = await Task.Run(() => FbxRigidEyeBinding.TryApply(model, preview, out var changed, token) ? changed : null, token);
            if (generation != _eyeBindingGeneration || !ReferenceEquals(model, _model)) return;
            if (result is null) { EyeRigStatus = "The source changed; preview the current eye binding again."; return; }
            if (!ReferenceEquals(result, model)) EyeModelApplyRequested?.Invoke(this, new(model, result, "Bound the selected eye island. Outside weights and all existing morph targets were preserved."));
            EyeRigStatus = "Eye binding saved. Review horizontal and vertical gaze, eyelid contact and facial expressions.";
        }
        catch (OperationCanceledException) { EyeRigStatus = "Eye binding cancelled."; }
        catch (Exception error) when (EyeError(error)) { EyeRigStatus = "Eye weights were not applied: " + error.Message; }
        finally { IsBusy = false; NotifyStateChanged(); NotifyEyeRig(); EyeBindingDisplayChanged?.Invoke(this, EventArgs.Empty); }
    }
    public bool TryGetEyeMotion(out SkeletonPose? pose)
    {
        pose = null;
        if (!EyeMotionReviewEnabled || !HasEyeDeformBone || _model?.Rig is not { } rig) return false;
        try { pose = RigEyeMotion.Evaluate(_model.Package.Document, rig, EyeSide, EyeYawDegrees, EyePitchDegrees); return true; }
        catch (Exception error) when (EyeError(error)) { EyeRigStatus = "Eye motion review unavailable: " + error.Message; return false; }
    }
    private void RefreshEyeBindingMetadata(FbxModelAuthoringImportResult current)
    { if (_eyeBinding is { } preview) _eyeBinding = FbxRigidEyeBinding.RefreshMetadata(preview, current); NotifyEyeRig(); }
    private void NotifyEyeRig()
    {
        foreach (string name in new[] { nameof(EyeBinding), nameof(EyeBindingWeightPreview), nameof(EyeBindingHeatmapBoneIndex), nameof(HasEyeDeformBone), nameof(CanBuildEyeRig), nameof(CanPreviewEyeBinding), nameof(CanApplyEyeBinding) }) OnPropertyChanged(name);
        BuildEyeRigCommand?.NotifyCanExecuteChanged(); PreviewEyeBindingCommand?.NotifyCanExecuteChanged(); ApplyEyeBindingCommand?.NotifyCanExecuteChanged();
    }
}
