using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private StartWorkflowSettingsStore? _startWorkflowSettings;
    private StartWorkflowMode? _startWorkflowMode;
    private bool _isWelcomeWorkflowVisible;

    public StartWorkflowMode? StartWorkflowMode
    {
        get => _startWorkflowMode;
        private set
        {
            if (SetProperty(ref _startWorkflowMode, value))
            {
                OnPropertyChanged(nameof(IsAnimationWorkflowAvailable));
                OnPropertyChanged(nameof(IsRetargetWorkflowAvailable));
            }
        }
    }

    public bool IsWelcomeWorkflowVisible
    {
        get => _isWelcomeWorkflowVisible;
        private set => SetProperty(ref _isWelcomeWorkflowVisible, value);
    }

    public bool IsAnimationWorkflowAvailable =>
        StartWorkflowMode != global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsOnly;

    public bool IsRetargetWorkflowAvailable =>
        StartWorkflowMode != global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsOnly;

    public RelayCommand<StartWorkflowMode> ChooseStartWorkflowCommand { get; private set; } = null!;

    public RelayCommand ChangeStartWorkflowCommand { get; private set; } = null!;

    /// <summary>Call once from the main view-model constructor after command setup.</summary>
    public void InitializeStartWorkflow(StartWorkflowSettingsStore? settings = null)
    {
        _startWorkflowSettings = settings ?? StartWorkflowSettingsStore.CreateDefault();
        StartWorkflowMode = _startWorkflowSettings.Load();
        IsWelcomeWorkflowVisible = StartWorkflowMode is null;
        ChooseStartWorkflowCommand = new RelayCommand<StartWorkflowMode>(ChooseStartWorkflow);
        ChangeStartWorkflowCommand = new RelayCommand(() => IsWelcomeWorkflowVisible = true);
        Models.SetImportedAnimationWorkspaceOffer(OfferAnimationWorkspaceForModelAsync);
        if (ShouldOpenGuidedModelImport(
                StartWorkflowMode,
                projectHasModels: !_project.Models.IsEmpty,
                workspaceHasModel: Models.HasModel))
        {
            OpenCustomModelAuthoring();
        }
        OnPropertyChanged(nameof(ChooseStartWorkflowCommand));
        OnPropertyChanged(nameof(ChangeStartWorkflowCommand));
    }

    internal static bool ShouldOpenGuidedModelImport(
        StartWorkflowMode? mode,
        bool projectHasModels,
        bool workspaceHasModel) =>
        mode == global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsOnly &&
        !projectHasModels &&
        !workspaceHasModel;

    /// <summary>Detects actual temporal transform or scalar changes in decoded clips.</summary>
    public static bool AnimationContainsTemporalMovement(IEnumerable<AnimationClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        foreach (AnimationClip clip in clips)
        {
            ArgumentNullException.ThrowIfNull(clip);
            if (clip.FrameCount < 2) continue;

            if (clip.TransformTracks.Any(track => HasTransformMovement(track.Keyframes)) ||
                clip.AuxiliaryTransformTracks.Any(track => HasTransformMovement(track.Keyframes)) ||
                clip.ScalarTracks.Any(track => HasScalarMovement(track.Keyframes)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Prompts after a newly imported model has yielded decoded clips.</summary>
    public Task<bool> OfferAnimationWorkspaceForModelAsync(
        string modelName,
        IEnumerable<AnimationClip> decodedClips)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (StartWorkflowMode != global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsOnly)
        {
            return Task.FromResult(false);
        }

        if (!AnimationContainsTemporalMovement(decodedClips)) return Task.FromResult(false);

        if (!_fileDialogs.ConfirmEnableAnimationWorkspace(modelName))
        {
            return Task.FromResult(false);
        }

        ChooseStartWorkflow(global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsAndAnimations);
        ActiveWorkspace = EditorWorkspaceMode.Animations;
        return Task.FromResult(true);
    }

    /// <summary>
    /// Applies the Models-only entry preference after the startup project has
    /// actually loaded, without reacting to unrelated later busy transitions.
    /// </summary>
    public void ApplyStartWorkflowAfterProjectOpen()
    {
        if (StartWorkflowMode != global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsOnly ||
            _project.Models.IsEmpty)
        {
            return;
        }

        ActiveWorkspace = EditorWorkspaceMode.Models;
        OpenRetailModelBrowser();
    }

    private void ChooseStartWorkflow(StartWorkflowMode mode)
    {
        if (!Enum.IsDefined(mode)) return;
        StartWorkflowMode = mode;
        _startWorkflowSettings?.Save(mode);
        IsWelcomeWorkflowVisible = false;
        ActiveWorkspace = mode == global::ReAnimated.App.Infrastructure.StartWorkflowMode.AnimationsOnly
            ? EditorWorkspaceMode.Animations
            : EditorWorkspaceMode.Models;
        if (mode == global::ReAnimated.App.Infrastructure.StartWorkflowMode.ModelsOnly)
        {
            if (ShouldOpenGuidedModelImport(
                    mode,
                    projectHasModels: !_project.Models.IsEmpty,
                    workspaceHasModel: Models.HasModel))
            {
                OpenCustomModelAuthoring();
            }
            else if (!_project.Models.IsEmpty && !Models.HasModel)
            {
                OpenRetailModelBrowser();
            }
        }
    }

    private static bool HasTransformMovement(IReadOnlyList<TransformKeyframe> keyframes)
    {
        if (keyframes.Count < 2) return false;
        TransformTRS first = keyframes[0].Value;
        for (int index = 1; index < keyframes.Count; index++)
        {
            TransformTRS current = keyframes[index].Value;
            if ((current.Translation - first.Translation).Length > 1e-4 ||
                MaxAbsDifference(current.Scale, first.Scale) > 1e-4)
            {
                return true;
            }

            double dot = Math.Abs(QuaternionD.Dot(first.Rotation.Normalized(), current.Rotation.Normalized()));
            double angle = 2.0 * Math.Acos(Math.Clamp(dot, 0.0, 1.0));
            if (angle > 1e-3) return true;
        }

        return false;
    }

    private static bool HasScalarMovement(IReadOnlyList<ScalarKeyframe> keyframes)
    {
        if (keyframes.Count < 2) return false;
        double first = keyframes[0].Value;
        return keyframes.Skip(1).Any(keyframe => Math.Abs(keyframe.Value - first) > 1e-4);
    }

    private static double MaxAbsDifference(Vector3D left, Vector3D right) =>
        Math.Max(Math.Abs(left.X - right.X), Math.Max(Math.Abs(left.Y - right.Y), Math.Abs(left.Z - right.Z)));
}
