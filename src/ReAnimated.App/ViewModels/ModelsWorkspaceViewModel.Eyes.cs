using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _eyePreviewActive;
    private bool _eyeOverlayVisible;
    private bool _eyeMotionActive;
    private bool EyeMotionActive => IsConformTabSelected && Conformance.EyeMotionReviewEnabled && Conformance.HasEyeDeformBone;
    private void OnEyeBindingDisplayChanged(object? sender, EventArgs e)
    { if (_disposed || _suppressPreviewRefresh) return; _previewSession = null; RefreshPreview(); }
    private bool EyeReviewActive => IsConformTabSelected && Conformance.EyeReviewEnabled &&
        (Conformance.IsStudioDetect || Conformance.IsStudioHelpers) && Conformance.GetEyePreview() is not null;
    private void OnEyePreviewChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressPreviewRefresh) return;
        if (EyeReviewActive || EyeMotionActive) Timeline.IsPlaying = false;
        RefreshPreview();
    }
    private void PublishEyeOverlay()
    {
        if (!EyeReviewActive || Conformance.GetEyePreview() is not { } preview)
        {
            if (_eyeOverlayVisible) Viewport.SceneSource.SetGizmos([]);
            _eyeOverlayVisible = false; return;
        }
        var lines = new List<GizmoRenderData>();
        double length = preview.Radius is { } radius ? radius * 1.5 : .025;
        var frame = preview.Frame;
        Line(frame.Translation, frame.TransformPoint(Vector3D.UnitX * length), new(1, .2f, .2f, 1));
        Line(frame.Translation, frame.TransformPoint(Vector3D.UnitY * length), new(.2f, 1, .2f, 1));
        Line(frame.Translation, frame.TransformPoint(Vector3D.UnitZ * length * 2), new(.3f, .65f, 1, 1));
        if (preview.Radius is { } globeRadius)
            foreach (var (a, b) in new[] { (Vector3D.UnitX, Vector3D.UnitY), (Vector3D.UnitX, Vector3D.UnitZ), (Vector3D.UnitY, Vector3D.UnitZ) })
                for (int i = 0; i < 48; i++)
                {
                    double t0 = i * Math.Tau / 48, t1 = (i + 1) * Math.Tau / 48;
                    Line(frame.TransformPoint((a * Math.Cos(t0) + b * Math.Sin(t0)) * globeRadius),
                        frame.TransformPoint((a * Math.Cos(t1) + b * Math.Sin(t1)) * globeRadius), new(1, .7f, .2f, 1));
                }
        Viewport.SceneSource.SetGizmos(lines); _eyeOverlayVisible = true;
        return;
        void Line(Vector3D a, Vector3D b, Vector4 color)
        {
            var start = new Vector3((float)a.X, (float)a.Y, (float)a.Z); var end = new Vector3((float)b.X, (float)b.Y, (float)b.Z);
            if (float.IsFinite(start.X) && float.IsFinite(start.Y) && float.IsFinite(start.Z) && float.IsFinite(end.X) && float.IsFinite(end.Y) && float.IsFinite(end.Z))
                lines.Add(new(GizmoKind.Line, start, end, color, 2));
        }
    }
}
