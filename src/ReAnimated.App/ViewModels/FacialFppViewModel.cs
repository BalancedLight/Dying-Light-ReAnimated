using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

public sealed class FacialFppViewModel : ObservableObject
{
    private float _fieldOfView = 60.0f;
    private float _nearPlane = 0.02f;
    private bool _useFppCamera;
    private bool _enableHSpineBasisCorrection = true;
    private bool _enableHeadPositionCorrection;
    private bool _enableHandInertia;
    private bool _showHands = true;
    private bool _showCameraRig = true;
    private bool _useProjectionCapture;
    private string _projectionCaptureLabel = string.Empty;
    private double? _sceneCaptureFieldOfView;
    private double? _sceneCaptureAspectRatio;
    private double? _sceneCaptureNearPlane;
    private double? _handsCaptureFieldOfView;
    private Dl1ProjectionFovAxis? _handsCaptureFieldOfViewAxis;
    private double? _handsCaptureAspectRatio;
    private double? _handsCaptureNearPlane;
    private string _projectionCaptureStatus =
        "No runtime-capture projection is enabled. Editor fallback values are not game validated.";
    private bool _useMovieReferenceCameraCapture;
    private string _movieReferenceCameraCaptureLabel = string.Empty;
    private double? _movieCameraPositionX;
    private double? _movieCameraPositionY;
    private double? _movieCameraPositionZ;
    private double? _movieCameraRotationX;
    private double? _movieCameraRotationY;
    private double? _movieCameraRotationZ;
    private double? _movieCameraRotationW;
    private double? _movieCameraVerticalFieldOfView;
    private double? _movieCameraAspectRatio;
    private double? _movieCameraNearPlane;
    private double? _movieCameraFarPlane;
    private string _movieReferenceCameraStatus =
        "No external movie reference-camera snapshot is enabled.";
    private string _mimicFilter = string.Empty;
    private string? _selectedMimicPreset;
    private ProjectMorphSourceValueUnit?
        _selectedFacialSourceValueUnit;
    private string _facialMappingReviewStatus =
        "Choose Normalized or Percent, then import a facial FBX against the active body timeline and exact retail target.";
    private string _previewStatus =
        "Orbit preview active. Select FPP mode and enable the FPP camera to use the evaluated EyeCamera helper.";

    public FacialFppViewModel()
    {
        ResetMorphsCommand = new RelayCommand(ResetMorphs, () => Morphs.Count > 0);
        PreviewBlinkCommand = new RelayCommand(PreviewBlink, () => Morphs.Count > 0);
    }

    public event EventHandler? LensChanged;

    public event EventHandler? MorphWeightsChanged;

    public ObservableCollection<MorphChannelViewModel> Morphs { get; } = [];

    public ObservableCollection<MorphChannelViewModel> VisibleMorphs
    {
        get;
    } = [];

    public ObservableCollection<string> MimicPresets { get; } = [];

    public ObservableCollection<string> VisibleMimicPresets { get; } = [];

    public ObservableCollection<FacialMorphBindingReviewViewModel>
        FacialMappingReviews
    { get; } = [];

    public ObservableCollection<string> UnmappedFacialChannels
    { get; } = [];

    public IReadOnlyList<ProjectMorphSourceValueUnit>
        FacialSourceValueUnits
    { get; } =
        Enum.GetValues<ProjectMorphSourceValueUnit>();

    public IReadOnlyList<Dl1ProjectionFovAxis> ProjectionFovAxes { get; } =
        Enum.GetValues<Dl1ProjectionFovAxis>();

    public IRelayCommand ResetMorphsCommand { get; }

    public IRelayCommand PreviewBlinkCommand { get; }

    public float FieldOfView
    {
        get => _fieldOfView;
        set
        {
            float normalized = Math.Clamp(value, 30.0f, 120.0f);
            if (SetProperty(ref _fieldOfView, normalized))
            {
                LensChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public float NearPlane
    {
        get => _nearPlane;
        set
        {
            float normalized = Math.Clamp(value, 0.001f, 1.0f);
            if (SetProperty(ref _nearPlane, normalized))
            {
                LensChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool UseFppCamera
    {
        get => _useFppCamera;
        set => SetProperty(ref _useFppCamera, value);
    }

    public bool EnableHSpineBasisCorrection
    {
        get => _enableHSpineBasisCorrection;
        set => SetProperty(
            ref _enableHSpineBasisCorrection,
            value);
    }

    public bool EnableHeadPositionCorrection
    {
        get => _enableHeadPositionCorrection;
        set => SetProperty(
            ref _enableHeadPositionCorrection,
            value);
    }

    public bool EnableHandInertia
    {
        get => _enableHandInertia;
        set => SetProperty(ref _enableHandInertia, value);
    }

    public bool ShowHands
    {
        get => _showHands;
        set => SetProperty(ref _showHands, value);
    }

    public bool ShowCameraRig
    {
        get => _showCameraRig;
        set => SetProperty(ref _showCameraRig, value);
    }

    public bool UseProjectionCapture
    {
        get => _useProjectionCapture;
        set => SetProperty(ref _useProjectionCapture, value);
    }

    public string ProjectionCaptureLabel
    {
        get => _projectionCaptureLabel;
        set => SetProperty(
            ref _projectionCaptureLabel,
            value ?? string.Empty);
    }

    public double? SceneCaptureFieldOfView
    {
        get => _sceneCaptureFieldOfView;
        set => SetProperty(ref _sceneCaptureFieldOfView, value);
    }

    public double? SceneCaptureAspectRatio
    {
        get => _sceneCaptureAspectRatio;
        set => SetProperty(ref _sceneCaptureAspectRatio, value);
    }

    public double? SceneCaptureNearPlane
    {
        get => _sceneCaptureNearPlane;
        set => SetProperty(ref _sceneCaptureNearPlane, value);
    }

    public double? HandsCaptureFieldOfView
    {
        get => _handsCaptureFieldOfView;
        set => SetProperty(ref _handsCaptureFieldOfView, value);
    }

    public Dl1ProjectionFovAxis? HandsCaptureFieldOfViewAxis
    {
        get => _handsCaptureFieldOfViewAxis;
        set => SetProperty(
            ref _handsCaptureFieldOfViewAxis,
            value);
    }

    public double? HandsCaptureAspectRatio
    {
        get => _handsCaptureAspectRatio;
        set => SetProperty(ref _handsCaptureAspectRatio, value);
    }

    public double? HandsCaptureNearPlane
    {
        get => _handsCaptureNearPlane;
        set => SetProperty(ref _handsCaptureNearPlane, value);
    }

    public string ProjectionCaptureStatus
    {
        get => _projectionCaptureStatus;
        internal set => SetProperty(
            ref _projectionCaptureStatus,
            value ?? string.Empty);
    }

    public bool UseMovieReferenceCameraCapture
    {
        get => _useMovieReferenceCameraCapture;
        set => SetProperty(
            ref _useMovieReferenceCameraCapture,
            value);
    }

    public string MovieReferenceCameraCaptureLabel
    {
        get => _movieReferenceCameraCaptureLabel;
        set => SetProperty(
            ref _movieReferenceCameraCaptureLabel,
            value ?? string.Empty);
    }

    public double? MovieCameraPositionX
    {
        get => _movieCameraPositionX;
        set => SetProperty(ref _movieCameraPositionX, value);
    }

    public double? MovieCameraPositionY
    {
        get => _movieCameraPositionY;
        set => SetProperty(ref _movieCameraPositionY, value);
    }

    public double? MovieCameraPositionZ
    {
        get => _movieCameraPositionZ;
        set => SetProperty(ref _movieCameraPositionZ, value);
    }

    public double? MovieCameraRotationX
    {
        get => _movieCameraRotationX;
        set => SetProperty(ref _movieCameraRotationX, value);
    }

    public double? MovieCameraRotationY
    {
        get => _movieCameraRotationY;
        set => SetProperty(ref _movieCameraRotationY, value);
    }

    public double? MovieCameraRotationZ
    {
        get => _movieCameraRotationZ;
        set => SetProperty(ref _movieCameraRotationZ, value);
    }

    public double? MovieCameraRotationW
    {
        get => _movieCameraRotationW;
        set => SetProperty(ref _movieCameraRotationW, value);
    }

    public double? MovieCameraVerticalFieldOfView
    {
        get => _movieCameraVerticalFieldOfView;
        set => SetProperty(
            ref _movieCameraVerticalFieldOfView,
            value);
    }

    public double? MovieCameraAspectRatio
    {
        get => _movieCameraAspectRatio;
        set => SetProperty(ref _movieCameraAspectRatio, value);
    }

    public double? MovieCameraNearPlane
    {
        get => _movieCameraNearPlane;
        set => SetProperty(ref _movieCameraNearPlane, value);
    }

    public double? MovieCameraFarPlane
    {
        get => _movieCameraFarPlane;
        set => SetProperty(ref _movieCameraFarPlane, value);
    }

    public string MovieReferenceCameraStatus
    {
        get => _movieReferenceCameraStatus;
        internal set => SetProperty(
            ref _movieReferenceCameraStatus,
            value ?? string.Empty);
    }

    public string MimicFilter
    {
        get => _mimicFilter;
        set
        {
            if (SetProperty(
                    ref _mimicFilter,
                    value ?? string.Empty))
            {
                RebuildVisibleFacialItems();
            }
        }
    }

    public string? SelectedMimicPreset
    {
        get => _selectedMimicPreset;
        set => SetProperty(ref _selectedMimicPreset, value);
    }

    public ProjectMorphSourceValueUnit?
        SelectedFacialSourceValueUnit
    {
        get => _selectedFacialSourceValueUnit;
        set => SetProperty(
            ref _selectedFacialSourceValueUnit,
            value);
    }

    public string FacialMappingReviewStatus
    {
        get => _facialMappingReviewStatus;
        internal set => SetProperty(
            ref _facialMappingReviewStatus,
            value ?? string.Empty);
    }

    public string PreviewStatus
    {
        get => _previewStatus;
        internal set => SetProperty(
            ref _previewStatus,
            value ?? string.Empty);
    }

    public void ReplaceMimicPresets(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string? previous = SelectedMimicPreset;
        MimicPresets.Clear();
        foreach (string name in names)
        {
            MimicPresets.Add(name);
        }

        RebuildVisibleFacialItems();
        SelectedMimicPreset = previous is not null &&
                              VisibleMimicPresets.Contains(previous)
            ? previous
            : VisibleMimicPresets.FirstOrDefault();
    }

    public void ReplaceMorphs(IEnumerable<MorphChannelViewModel> morphs)
    {
        ArgumentNullException.ThrowIfNull(morphs);
        foreach (MorphChannelViewModel morph in Morphs)
        {
            morph.PropertyChanged -= OnMorphPropertyChanged;
        }

        Morphs.Clear();
        foreach (MorphChannelViewModel morph in morphs)
        {
            Morphs.Add(morph);
            morph.PropertyChanged += OnMorphPropertyChanged;
        }

        RebuildVisibleFacialItems();
        ResetMorphsCommand.NotifyCanExecuteChanged();
        PreviewBlinkCommand.NotifyCanExecuteChanged();
        MorphWeightsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadProjectionCapture(
        bool enabled,
        Dl1FppProjectionCapture? capture)
    {
        UseProjectionCapture = enabled;
        ProjectionCaptureLabel = capture?.CaptureLabel ?? string.Empty;
        SceneCaptureFieldOfView =
            capture?.SceneVerticalFieldOfViewDegrees;
        SceneCaptureAspectRatio = capture?.SceneAspectRatio;
        SceneCaptureNearPlane = capture?.SceneNearClipMeters;
        HandsCaptureFieldOfView =
            capture?.HandsFieldOfViewDegrees;
        HandsCaptureFieldOfViewAxis =
            capture?.HandsFieldOfViewAxis;
        HandsCaptureAspectRatio = capture?.HandsAspectRatio;
        HandsCaptureNearPlane = capture?.HandsNearClipMeters;
    }

    public bool TryCreateProjectionCapture(
        out Dl1FppProjectionCapture? capture,
        out string? error)
    {
        if (SceneCaptureFieldOfView is not double sceneFov ||
            SceneCaptureAspectRatio is not double sceneAspect ||
            SceneCaptureNearPlane is not double sceneNear ||
            HandsCaptureFieldOfView is not double handsFov ||
            HandsCaptureFieldOfViewAxis is not
                Dl1ProjectionFovAxis handsAxis ||
            HandsCaptureAspectRatio is not double handsAspect ||
            HandsCaptureNearPlane is not double handsNear)
        {
            capture = null;
            error =
                "Scene FOV/aspect/near and hands FOV/axis/aspect/near are all required.";
            return false;
        }

        string? captureLabel = string.IsNullOrWhiteSpace(
            ProjectionCaptureLabel)
                ? null
                : ProjectionCaptureLabel.Trim();
        if (captureLabel is { Length: > 256 })
        {
            capture = null;
            error =
                "The capture label cannot exceed 256 characters.";
            return false;
        }

        capture = new Dl1FppProjectionCapture
        {
            CaptureLabel = captureLabel,
            SceneVerticalFieldOfViewDegrees = sceneFov,
            SceneAspectRatio = sceneAspect,
            SceneNearClipMeters = sceneNear,
            HandsFieldOfViewDegrees = handsFov,
            HandsFieldOfViewAxis = handsAxis,
            HandsAspectRatio = handsAspect,
            HandsNearClipMeters = handsNear,
        };
        try
        {
            _ = capture.CreateSnapshot(
                CameraLens.Default.FarClipMeters);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            capture = null;
            error = exception.Message;
            return false;
        }
    }

    public void LoadMovieReferenceCameraCapture(
        bool enabled,
        Dl1MovieReferenceCameraCapture? capture)
    {
        UseMovieReferenceCameraCapture = enabled;
        MovieReferenceCameraCaptureLabel =
            capture?.CaptureLabel ?? string.Empty;
        MovieCameraPositionX =
            capture?.WorldTransform.Translation.X;
        MovieCameraPositionY =
            capture?.WorldTransform.Translation.Y;
        MovieCameraPositionZ =
            capture?.WorldTransform.Translation.Z;
        MovieCameraRotationX =
            capture?.WorldTransform.Rotation.X;
        MovieCameraRotationY =
            capture?.WorldTransform.Rotation.Y;
        MovieCameraRotationZ =
            capture?.WorldTransform.Rotation.Z;
        MovieCameraRotationW =
            capture?.WorldTransform.Rotation.W;
        MovieCameraVerticalFieldOfView =
            capture?.Lens.VerticalFieldOfViewDegrees;
        MovieCameraAspectRatio =
            capture?.Lens.AspectRatio;
        MovieCameraNearPlane =
            capture?.Lens.NearClipMeters;
        MovieCameraFarPlane =
            capture?.Lens.FarClipMeters;
    }

    public bool TryCreateMovieReferenceCameraCapture(
        out Dl1MovieReferenceCameraCapture? capture,
        out string? error)
    {
        if (MovieCameraPositionX is not double positionX ||
            MovieCameraPositionY is not double positionY ||
            MovieCameraPositionZ is not double positionZ ||
            MovieCameraRotationX is not double rotationX ||
            MovieCameraRotationY is not double rotationY ||
            MovieCameraRotationZ is not double rotationZ ||
            MovieCameraRotationW is not double rotationW ||
            MovieCameraVerticalFieldOfView is not double fieldOfView ||
            MovieCameraAspectRatio is not double aspectRatio ||
            MovieCameraNearPlane is not double nearPlane ||
            MovieCameraFarPlane is not double farPlane)
        {
            capture = null;
            error =
                "Position XYZ, quaternion XYZW, vertical FOV, aspect, near, and far are all required.";
            return false;
        }

        try
        {
            string? captureLabel = string.IsNullOrWhiteSpace(
                MovieReferenceCameraCaptureLabel)
                    ? null
                    : MovieReferenceCameraCaptureLabel.Trim();
            if (captureLabel is { Length: > 256 })
            {
                capture = null;
                error =
                    "The capture label cannot exceed 256 characters.";
                return false;
            }

            QuaternionD rotation = new QuaternionD(
                rotationX,
                rotationY,
                rotationZ,
                rotationW).Normalized();
            capture = new Dl1MovieReferenceCameraCapture
            {
                CaptureLabel = captureLabel,
                WorldTransform = new TransformTRS(
                    new Vector3D(
                        positionX,
                        positionY,
                        positionZ),
                    rotation,
                    Vector3D.One),
                Lens = new CameraLens(
                    fieldOfView,
                    aspectRatio,
                    nearPlane,
                    farPlane),
            };
            _ = capture.CreateSnapshot();
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException)
        {
            capture = null;
            error = exception.Message;
            return false;
        }
    }

    private void ResetMorphs()
    {
        foreach (MorphChannelViewModel morph in Morphs)
        {
            morph.Weight = 0.0f;
        }
    }

    private void PreviewBlink()
    {
        MorphChannelViewModel? blink = Morphs.FirstOrDefault(item =>
            item.Name.Contains("blink", StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains("eye", StringComparison.OrdinalIgnoreCase));
        if (blink is not null)
        {
            blink.Weight = blink.Weight > 0.5f ? 0.0f : 1.0f;
        }
    }

    private void RebuildVisibleFacialItems()
    {
        string filter = MimicFilter.Trim();
        VisibleMorphs.Clear();
        foreach (MorphChannelViewModel morph in Morphs.Where(item =>
                     MatchesFilter(item.Name, filter)))
        {
            VisibleMorphs.Add(morph);
        }

        VisibleMimicPresets.Clear();
        foreach (string preset in MimicPresets.Where(item =>
                     MatchesFilter(item, filter)))
        {
            VisibleMimicPresets.Add(preset);
        }

        if (SelectedMimicPreset is null ||
            !VisibleMimicPresets.Contains(SelectedMimicPreset))
        {
            SelectedMimicPreset =
                VisibleMimicPresets.FirstOrDefault();
        }
    }

    private static bool MatchesFilter(
        string value,
        string filter) =>
        filter.Length == 0 ||
        value.Contains(
            filter,
            StringComparison.OrdinalIgnoreCase);

    private void OnMorphPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (string.Equals(
                args.PropertyName,
                nameof(MorphChannelViewModel.Weight),
                StringComparison.Ordinal))
        {
            MorphWeightsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
