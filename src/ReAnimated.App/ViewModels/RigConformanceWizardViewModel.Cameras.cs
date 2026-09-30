using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private FbxCameraCalibrationPreview? _cameraPreview;
    private bool _restoringCameras;
    private long _cameraGeneration;

    [ObservableProperty] private FbxCameraNode? _selectedCameraNode;
    [ObservableProperty] private double _cameraOffsetX;
    [ObservableProperty] private double _cameraOffsetY;
    [ObservableProperty] private double _cameraOffsetZ;
    [ObservableProperty] private double _cameraRotationX;
    [ObservableProperty] private double _cameraRotationY;
    [ObservableProperty] private double _cameraRotationZ;
    [ObservableProperty] private bool _cameraReviewed;
    [ObservableProperty] private bool _cameraReviewEnabled;
    [ObservableProperty] private string _cameraStatus = "Choose an observed camera/helper node to calibrate its local frame.";

    public ObservableCollection<FbxCameraNode> CameraNodes { get; } = [];
    public IAsyncRelayCommand PreviewCameraCalibrationCommand { get; private set; } = null!;
    public IRelayCommand CancelCameraCalibrationCommand { get; private set; } = null!;
    public IRelayCommand ApplyCameraCalibrationCommand { get; private set; } = null!;
    public IRelayCommand ResetCameraCalibrationCommand { get; private set; } = null!;
    public event EventHandler? CameraPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? CameraModelApplyRequested;
    public FbxCameraCalibrationPreview? CameraCalibrationPreview => _cameraPreview;
    public bool HasCameraCalibrationPreview => _cameraPreview is not null;
    public bool CanPreviewCameraCalibration => !IsBusy && HasStudioSession && SelectedCameraNode is not null;
    public bool CanApplyCameraCalibration => !IsBusy && CameraReviewed && _cameraPreview is { HasChanges: true };
    public string CameraPreviewDirection => CameraReadoutFrame is { } preview
        ? $"Forward {preview.Forward.X:0.###}, {preview.Forward.Y:0.###}, {preview.Forward.Z:0.###}; Up {preview.Up.X:0.###}, {preview.Up.Y:0.###}, {preview.Up.Z:0.###}"
        : "No frame preview yet.";
    public string CameraPreviewRoll => CameraReadoutFrame?.RollDegrees is { } roll
        ? $"Roll {roll:0.##} deg"
        : "Roll unavailable for this frame.";

    private void InitializeCameras()
    {
        InitializeCameraCreation();
        PreviewCameraCalibrationCommand = new AsyncRelayCommand(PreviewCameraCalibrationAsync, () => CanPreviewCameraCalibration);
        CancelCameraCalibrationCommand = new RelayCommand(() => { PreviewCameraCalibrationCommand.Cancel(); PreviewCameraCreationCommand.Cancel(); }, () => PreviewCameraCalibrationCommand.IsRunning || PreviewCameraCreationCommand.IsRunning);
        ApplyCameraCalibrationCommand = new RelayCommand(ApplyCameraCalibration, () => CanApplyCameraCalibration);
        ResetCameraCalibrationCommand = new RelayCommand(() => ResetCameraDraft(), () => SelectedCameraNode is not null || _cameraPreview is not null || CameraTemplateChoice is not null);
        PreviewCameraCalibrationCommand.PropertyChanged += (_, _) => CancelCameraCalibrationCommand.NotifyCanExecuteChanged();
    }

    internal void RestoreCameras()
    {
        Guid? selected = SelectedCameraNode?.EntityId;
        _restoringCameras = true;
        try
        {
            PreviewCameraCalibrationCommand?.Cancel();
            PreviewCameraCreationCommand?.Cancel();
            _cameraGeneration++;
            _cameraPreview = null;
        _cameraEvaluatedFrame = null;
            CameraNodes.Clear();
            if (_model is { } model && model.Package.Document.RiggingSession is not null)
            {
                foreach (FbxCameraNode node in FbxCameraHelperAuthoring.Inspect(model))
                    CameraNodes.Add(node);
            }

            SelectedCameraNode = selected is { } id
                ? CameraNodes.FirstOrDefault(node => node.EntityId == id)
                : CameraNodes.FirstOrDefault();
            ResetCameraDraft(notify: false);
            RefreshCameraCreation();
            CameraStatus = CameraNodes.Count == 0
                ? "No observed camera/helper nodes are available in the current source."
                : "Select an observed camera/helper node, edit its local frame, preview it, then explicitly review and apply.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            CameraStatus = "Camera calibration needs a current source: " + error.Message;
        }
        finally
        {
            _restoringCameras = false;
            NotifyCameras();
            CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetCameraDraft(bool notify = true)
    {
        if (!_restoringCameras) InvalidateCameraDraft();
        _cameraPreview = null;
        _cameraEvaluatedFrame = null;
        CameraOffsetX = 0; CameraOffsetY = 0; CameraOffsetZ = 0;
        CameraRotationX = 0; CameraRotationY = 0; CameraRotationZ = 0;
        CameraReviewed = false;
        CameraReviewEnabled = false;
        if (notify)
        {
            NotifyCameras();
            CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private TransformTRS CreateCameraOffset()
    {
        const double radians = Math.PI / 180.0;
        TransformMatrix rotation = new TransformTRS(Vector3D.Zero,
            QuaternionD.FromAxisAngle(Vector3D.UnitX, CameraRotationX * radians), Vector3D.One).ToMatrix() *
            new TransformTRS(Vector3D.Zero,
                QuaternionD.FromAxisAngle(Vector3D.UnitY, CameraRotationY * radians), Vector3D.One).ToMatrix() *
            new TransformTRS(Vector3D.Zero,
                QuaternionD.FromAxisAngle(Vector3D.UnitZ, CameraRotationZ * radians), Vector3D.One).ToMatrix();
        TransformTRS trs = rotation.Decompose(1e-7);
        return new(new Vector3D(CameraOffsetX, CameraOffsetY, CameraOffsetZ), trs.Rotation, Vector3D.One);
    }

    private async Task PreviewCameraCalibrationAsync(CancellationToken cancellationToken)
    {
        if (_model is not { } model || SelectedCameraNode is not { } node) return;
        long generation = ++_cameraGeneration;
        IsBusy = true;
        NotifyStateChanged();
        try
        {
            TransformTRS offset = CreateCameraOffset();
            FbxCameraCalibrationPreview preview = await Task.Run(
                () => FbxCameraHelperAuthoring.Preview(model, node.EntityId, offset, cancellationToken),
                cancellationToken);
            if (generation != _cameraGeneration || !ReferenceEquals(model, _model)) return;
            _cameraPreview = preview;
            CameraReviewed = false;
            CameraReviewEnabled = true;
            CameraStatus = _cameraPreview.HasChanges
                ? $"Previewed {node.Name} under parent {node.ParentName}. Review the frame and apply explicitly."
                : "The requested camera frame is unchanged. No edit is pending.";
        }
        catch (OperationCanceledException)
        {
            if (generation == _cameraGeneration) CameraStatus = "Camera preview cancelled.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            if (generation == _cameraGeneration)
            {
                _cameraPreview = null;
        _cameraEvaluatedFrame = null;
                CameraStatus = "Camera preview was rejected: " + error.Message;
            }
        }
        finally
        {
            // Draft invalidation changes the generation but still owns this
            // command's busy state until the cancelled worker has returned.
            IsBusy = false;
            NotifyStateChanged();
            NotifyCameras();
            if (generation == _cameraGeneration)
                CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyCameraCalibration()
    {
        if (_model is not { } model || _cameraPreview is not { } preview || !CanApplyCameraCalibration) return;
        try
        {
            if (!FbxCameraHelperAuthoring.TryApply(model, preview, _template, out FbxModelAuthoringImportResult result))
            {
                CameraStatus = "The source or reference changed. Preview the current camera frame again.";
                return;
            }

            if (!RequestBodyChange(CameraModelApplyRequested, new(model, result,
                $"Applied reviewed camera/helper frame for {preview.Node.Name}; source mesh, skinning and morphs were preserved."))) return;
            CameraStatus = "Camera/helper frame applied. Native camera behavior and visual lens calibration remain separate review items.";
            _cameraPreview = null;
        _cameraEvaluatedFrame = null;
            CameraReviewed = false;
            CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            CameraStatus = "Camera frame was not applied: " + error.Message;
        }
        NotifyCameras();
    }

    partial void OnSelectedCameraNodeChanged(FbxCameraNode? value)
    {
        if (!_restoringCameras) ResetCameraDraft();
    }
    partial void OnCameraOffsetXChanged(double value) => InvalidateCameraDraft();
    partial void OnCameraOffsetYChanged(double value) => InvalidateCameraDraft();
    partial void OnCameraOffsetZChanged(double value) => InvalidateCameraDraft();
    partial void OnCameraRotationXChanged(double value) => InvalidateCameraDraft();
    partial void OnCameraRotationYChanged(double value) => InvalidateCameraDraft();
    partial void OnCameraRotationZChanged(double value) => InvalidateCameraDraft();
    partial void OnCameraReviewedChanged(bool value) => NotifyCameras();
    partial void OnCameraReviewEnabledChanged(bool value) => CameraPreviewChanged?.Invoke(this, EventArgs.Empty);

    private void InvalidateCameraDraft()
    {
        if (_restoringCameras) return;
        PreviewCameraCalibrationCommand?.Cancel();
            PreviewCameraCreationCommand?.Cancel();
        _cameraGeneration++;
        _cameraPreview = null;
        _cameraEvaluatedFrame = null;
        CameraReviewed = false;
        NotifyCameras();
        CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyCameras()
    {
        NotifyCameraCreation();
        OnPropertyChanged(nameof(HasCameraCalibrationPreview));
        OnPropertyChanged(nameof(CanPreviewCameraCalibration));
        OnPropertyChanged(nameof(CanApplyCameraCalibration));
        OnPropertyChanged(nameof(CameraPreviewDirection));
        OnPropertyChanged(nameof(CameraPreviewRoll));
        PreviewCameraCalibrationCommand?.NotifyCanExecuteChanged();
        CancelCameraCalibrationCommand?.NotifyCanExecuteChanged();
        ApplyCameraCalibrationCommand?.NotifyCanExecuteChanged();
        ResetCameraCalibrationCommand?.NotifyCanExecuteChanged();
    }
}
