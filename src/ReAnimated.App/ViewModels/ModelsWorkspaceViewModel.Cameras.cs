using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _cameraOverlayVisible;
    private CameraReviewFrame? _cameraViewFrame;

    private void OnCameraPreviewChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressPreviewRefresh) return;
        if (Conformance.CameraReviewEnabled && !Conformance.CameraAnimateReview && IsConformTabSelected && Conformance.IsStudioHelpers)
            Timeline.IsPlaying = false;
        RefreshPreview();
    }

    private RenderCamera? PrepareCameraReview(SkeletonRenderData? skeleton, bool active, AnimationClip? clip, double frame)
    {
        _cameraViewFrame = null;
        if (!active || skeleton is null || Conformance.CameraCalibrationPreview is not { } preview)
        {
            Conformance.PublishCameraViewFrame(null, "Camera review is inactive.");
            return null;
        }
        BoneRenderData[] matches = skeleton.Bones.Where(b => b.Name.Equals(preview.Node.Name, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            Conformance.PublishCameraViewFrame(null, "The selected camera is absent from the evaluated hierarchy.");
            return null;
        }
        TransformMatrix world = CorePreviewAdapter.ToCoreMatrix(matches[0].WorldTransform * skeleton.RootTransform);
        _cameraViewFrame = Conformance.CreateCameraViewFrame(world);
        string pose = clip is null ? "Prepared rest-frame review." : $"Prepared animation: {clip.Name}, frame {frame}. Source samples are unchanged.";
        Conformance.PublishCameraViewFrame(_cameraViewFrame, _cameraViewFrame is null ? "The camera or lens cannot be represented for review." : pose);
        if (_cameraViewFrame is null || !Conformance.CameraLookThrough) return null;
        RenderCamera? camera = CameraReviewOverlayBuilder.CreateRenderCamera(_cameraViewFrame);
        if (camera is null)
        {
            _cameraViewFrame = null;
            Conformance.PublishCameraViewFrame(null, "The camera position exceeds viewport precision; orbit view was restored.");
        }
        return camera;
    }

    private void PublishCameraCalibrationOverlay()
    {
        if (_cameraViewFrame is null)
        {
            if (_cameraOverlayVisible) Viewport.SceneSource.SetGizmos([]);
            _cameraOverlayVisible = false;
            return;
        }
        Viewport.SceneSource.SetGizmos(Conformance.CameraLookThrough ? [] : CameraReviewOverlayBuilder.Build(_cameraViewFrame, Conformance.CameraFrustumDepth));
        _cameraOverlayVisible = true;
        Viewport.SetPresentation($"Camera review - {Conformance.CameraCalibrationPreview?.Node.Name}",
            Conformance.CameraLookThrough ? "Preview lens view; near/far clipping is active. Native camera behavior remains unverified."
                : "Prepared view axes and near-plane frustum; drawing depth is limited separately from the lens far clip.");
    }
}
