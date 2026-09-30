using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record CameraParentChoice(Guid EntityId, string Name);

public sealed partial class RigConformanceWizardViewModel
{
    private bool _restoringCameraCreation;
    [ObservableProperty] private CameraTemplateObservation? _cameraTemplateChoice;
    [ObservableProperty] private CameraParentChoice? _cameraCreationParent;
    [ObservableProperty] private string _cameraCreationStatus = "Resolve a reference skeleton to inspect its camera frames.";
    public ObservableCollection<CameraTemplateObservation> CameraTemplateChoices { get; } = [];
    public ObservableCollection<CameraParentChoice> CameraCreationParents { get; } = [];
    public IReadOnlyCollection<string> AvailableTemplateProfiles =>
        TemplateProfileName.StartsWith("mesh:", StringComparison.Ordinal)
            ? Dl1RigTemplateProvider.ProfileNames.Append(TemplateProfileName).ToArray()
            : Dl1RigTemplateProvider.ProfileNames;
    public IAsyncRelayCommand PreviewCameraCreationCommand { get; private set; } = null!;
    public bool CanPreviewCameraCreation => !IsBusy && HasStudioSession && _template is not null &&
        TemplateProfileName.Equals(_template.ProfileName, StringComparison.OrdinalIgnoreCase) &&
        CameraTemplateChoice is not null && CameraCreationParent is not null;
    public string CameraCurrentParent => _cameraPreview?.Node.ParentName ?? SelectedCameraNode?.ParentName ?? "-";
    public string CameraCurrentChannels => _cameraPreview?.Node.ChannelSummary ?? SelectedCameraNode?.ChannelSummary ?? "-";

    private void InitializeCameraCreation()
    {
        PreviewCameraCreationCommand = new AsyncRelayCommand(PreviewCameraCreationAsync, () => CanPreviewCameraCreation);
        PreviewCameraCreationCommand.PropertyChanged += (_, _) => CancelCameraCalibrationCommand?.NotifyCanExecuteChanged();
    }

    private void RefreshCameraCreation()
    {
        string? selected = CameraTemplateChoice?.Name;
        Guid? parent = CameraCreationParent?.EntityId;
        _restoringCameraCreation = true;
        try
        {
            CameraTemplateChoices.Clear(); CameraCreationParents.Clear();
            if (_model?.Package.Document is { RiggingSession: not null } document)
            {
                var observed = RiggingSessions.ObserveSourceHierarchy(document);
                foreach (var bone in document.CreateEffectiveBones())
                    CameraCreationParents.Add(new(observed[bone.Index].EntityId, bone.Name));
            }
            if (_template is not null)
                foreach (var choice in FbxCameraHelperAuthoring.ObserveTemplate(_template)) CameraTemplateChoices.Add(choice);
            CameraTemplateChoice = CameraTemplateChoices.FirstOrDefault(c => c.Name == selected) ?? CameraTemplateChoices.FirstOrDefault();
            CameraCreationParent = CameraCreationParents.FirstOrDefault(p => p.EntityId == parent) ?? SuggestedCameraParent();
            CameraCreationStatus = _template is null ? "Resolve a reference skeleton to inspect its camera frames." :
                $"Reference {_template.ProfileName}/{_template.SourceResourceName}; {_template.SourceFingerprint}. " +
                "Frames are asset observations. Native parent requirements, channel ownership and LOD rules remain unverified.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            CameraTemplateChoices.Clear(); CameraTemplateChoice = null;
            CameraCreationStatus = "Camera reference unavailable: " + error.Message;
        }
        finally { _restoringCameraCreation = false; NotifyCameraCreation(); }
    }

    private CameraParentChoice? SuggestedCameraParent()
    {
        if (CameraTemplateChoice is not { } choice) return null;
        CameraParentChoice[] matches = CameraCreationParents.Where(p => p.Name.Equals(choice.ParentName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task PreviewCameraCreationAsync(CancellationToken cancellationToken)
    {
        if (!CanPreviewCameraCreation || _model is not { } model || _template is not { } template ||
            CameraTemplateChoice is not { } choice || CameraCreationParent is not { } parent) return;
        long generation = ++_cameraGeneration;
        IsBusy = true; NotifyStateChanged();
        try
        {
            var offset = CreateCameraOffset();
            var preview = await Task.Run(() => FbxCameraHelperAuthoring.PreviewCreation(model, template,
                choice.Name, parent.EntityId, offset, cancellationToken), cancellationToken);
            if (generation != _cameraGeneration || !ReferenceEquals(_model, model) || !ReferenceEquals(_template, template)) return;
            _cameraPreview = preview; _cameraEvaluatedFrame = null; CameraReviewed = false; CameraReviewEnabled = true;
            string parentNote = parent.Name.Equals(choice.ParentName, StringComparison.OrdinalIgnoreCase)
                ? "The selected parent name matches the reference."
                : $"The selected parent differs from observed parent {choice.ParentName}; native parenting requires review.";
            CameraStatus = $"New {choice.Name} preview under {parent.Name}. {parentNote} Review the candidate, then apply below. No channel/LOD policy was inferred.";
        }
        catch (OperationCanceledException) { if (generation == _cameraGeneration) CameraStatus = "Camera creation preview cancelled."; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            if (generation == _cameraGeneration) { _cameraPreview = null; CameraStatus = "Camera creation was rejected: " + error.Message; }
        }
        finally
        {
            IsBusy = false; NotifyStateChanged(); NotifyCameras();
            if (generation == _cameraGeneration) CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnCameraTemplateChoiceChanged(CameraTemplateObservation? value)
    {
        if (_restoringCameraCreation) return;
        ResetCameraDraft(); CameraCreationParent = SuggestedCameraParent(); NotifyCameraCreation();
    }
    partial void OnCameraCreationParentChanged(CameraParentChoice? value)
    {
        if (!_restoringCameraCreation) InvalidateCameraDraft();
        NotifyCameraCreation();
    }
    partial void OnTemplateProfileNameChanged(string value)
    {
        OnPropertyChanged(nameof(AvailableTemplateProfiles));
        if (_cameraPreview?.IsCreation == true) InvalidateCameraDraft();
        NotifyCameraCreation();
    }
    private void RefreshCameraMetadata(FbxModelAuthoringImportResult current)
    {
        if (_cameraPreview is { } preview) _cameraPreview = FbxCameraHelperAuthoring.RefreshMetadata(preview, current);
        NotifyCameras();
    }

    private void NotifyCameraCreation()
    {
        OnPropertyChanged(nameof(CanPreviewCameraCreation));
        OnPropertyChanged(nameof(CameraCurrentParent)); OnPropertyChanged(nameof(CameraCurrentChannels));
        PreviewCameraCreationCommand?.NotifyCanExecuteChanged();
    }
}
