using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private CameraReviewFrame? _cameraEvaluatedFrame;
    [ObservableProperty] private double _cameraVerticalFov = 60;
    [ObservableProperty] private double _cameraAspect = 16.0 / 9;
    [ObservableProperty] private double _cameraNearClip = .02;
    [ObservableProperty] private double _cameraFarClip = 2000;
    [ObservableProperty] private double _cameraFrustumDepth = 1;
    [ObservableProperty] private bool _cameraInvertUp = true;
    [ObservableProperty] private bool _cameraLookThrough;
    [ObservableProperty] private bool _cameraAnimateReview;
    [ObservableProperty] private string _cameraPoseStatus = "Rest-frame review.";

    public string CameraLensStatus => TryCreateCameraLens(out _, out string reason)
        ? $"Preview lens only: near {CameraNearClip:0.###} m, far {CameraFarClip:0.###} m. Frustum lines stop at {Math.Min(CameraFarClip, Math.Max(CameraNearClip, CameraFrustumDepth)):0.###} m; the view uses the full clip range."
        : reason;
    public string CameraViewBasis => CameraInvertUp ? "View basis: +Z forward, -Y up (existing helper preview convention)." : "View basis: +Z forward, +Y up (diagnostic convention).";

    internal bool TryCreateCameraLens(out CameraLens? lens, out string reason)
    {
        lens = null;
        try
        {
            double[] values = [CameraVerticalFov, CameraAspect, CameraNearClip, CameraFarClip, CameraFrustumDepth];
            if (values.Any(v => !float.IsFinite((float)v) || (float)v <= 0) || (float)CameraFarClip <= (float)CameraNearClip)
                throw new ArgumentException("Lens values must be positive, finite and distinct at viewport precision.");
            lens = new CameraLens(CameraVerticalFov, CameraAspect, CameraNearClip, CameraFarClip);
            reason = string.Empty;
            return true;
        }
        catch (ArgumentException error)
        {
            reason = "Preview lens is invalid: " + error.Message;
            return false;
        }
    }

    internal CameraReviewFrame? CreateCameraViewFrame(TransformMatrix world)
    {
        if (!TryCreateCameraLens(out CameraLens? lens, out _)) return null;
        try { return CameraReviewGeometry.Create(world, lens!.Value, CameraInvertUp); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    internal void PublishCameraViewFrame(CameraReviewFrame? frame, string poseStatus)
    {
        if (ReferenceEquals(_cameraEvaluatedFrame, frame) && CameraPoseStatus == poseStatus) return;
        _cameraEvaluatedFrame = frame;
        CameraPoseStatus = poseStatus;
        OnPropertyChanged(nameof(CameraPreviewDirection));
        OnPropertyChanged(nameof(CameraPreviewRoll));
    }

    private CameraReviewFrame? CameraReadoutFrame => _cameraEvaluatedFrame ??
        (_cameraPreview is { } preview ? CreateCameraViewFrame(preview.WorldFrame) : null);

    private void CameraLensChanged()
    {
        _cameraEvaluatedFrame = null;
        OnPropertyChanged(nameof(CameraLensStatus));
        OnPropertyChanged(nameof(CameraViewBasis));
        OnPropertyChanged(nameof(CameraPreviewDirection));
        OnPropertyChanged(nameof(CameraPreviewRoll));
        if (!_restoringCameras) CameraPreviewChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnCameraVerticalFovChanged(double value) => CameraLensChanged();
    partial void OnCameraAspectChanged(double value) => CameraLensChanged();
    partial void OnCameraNearClipChanged(double value) => CameraLensChanged();
    partial void OnCameraFarClipChanged(double value) => CameraLensChanged();
    partial void OnCameraFrustumDepthChanged(double value) => CameraLensChanged();
    partial void OnCameraInvertUpChanged(bool value) => CameraLensChanged();
    partial void OnCameraLookThroughChanged(bool value) => CameraLensChanged();
    partial void OnCameraAnimateReviewChanged(bool value) => CameraLensChanged();
}
