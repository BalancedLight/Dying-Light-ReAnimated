using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _handOverlayVisible;
    private bool _handPreviewActive;
    private bool HandReviewActive => IsConformTabSelected && Conformance.HandReviewEnabled && (Conformance.IsStudioDetect || Conformance.IsStudioFit);
    private void OnHandBindingDisplayChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressPreviewRefresh) return;
        _previewSession = null; UpdateConformanceViewportBinding();
    }
    private void OnHandPreviewChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressPreviewRefresh) return;
        if (HandReviewActive) Timeline.IsPlaying = false;
        UpdateConformanceViewportBinding();
    }
    private void PublishHandOverlay()
    {
        if (!HandReviewActive)
        {
            if (_handOverlayVisible) Viewport.SceneSource.SetGizmos([]);
            _handOverlayVisible = false; return;
        }
        var lines = new List<GizmoRenderData>();
        var minimum = Conformance.HandMinimum.Value; var maximum = Conformance.HandMaximum.Value;
        if (minimum.IsFinite && maximum.IsFinite)
        {
            var corners = Enumerable.Range(0, 8).Select(i => new Vector3D((i & 1) == 0 ? minimum.X : maximum.X,
                (i & 2) == 0 ? minimum.Y : maximum.Y, (i & 4) == 0 ? minimum.Z : maximum.Z)).ToArray();
            for (int i = 0; i < 8; i++) foreach (int bit in new[] { 1, 2, 4 }) if ((i & bit) == 0) Line(corners[i], corners[i | bit], new(.65f, .35f, 1, 1));
        }
        if (Conformance.HandDetection is { } detection)
        {
            for (int i = 0; i < detection.Fingers.Length; i++)
            {
                var finger = detection.Fingers[i];
                var color = new Vector4(.25f + (i % 3) * .3f, 1 - (i % 2) * .4f, .3f + (i % 2) * .6f, 1);
                for (int j = 1; j < finger.Joints.Length; j++) Line(finger.Joints[j - 1].Position, finger.Joints[j].Position, color);
            }
        }
        if (Conformance.HandPalmPreview is { } palm)
        {
            double length = Math.Max(1e-5, (maximum - minimum).Length * .15);
            Line(palm.Translation, palm.TransformPoint(Vector3D.UnitX * length), new(1,.2f,.2f,1));
            Line(palm.Translation, palm.TransformPoint(Vector3D.UnitY * length), new(.2f,1,.2f,1));
            Line(palm.Translation, palm.TransformPoint(Vector3D.UnitZ * length), new(.3f,.5f,1,1));
        }
        var session = _model?.Package.Document.RiggingSession;
        var hand = session?.Hands.FirstOrDefault(h => h.Side == Conformance.HandSide);
        if (hand is not null)
        {
            var guides = session!.Landmarks.ToDictionary(static g => g.Id);
            foreach (var finger in hand.Fingers)
            {
                for (int i = 1; i < finger.JointGuideIds.Length; i++) Line(guides[finger.JointGuideIds[i - 1]].Position, guides[finger.JointGuideIds[i]].Position, new(1, .8f, .2f, 1));
            }
            if (Conformance.SelectedHandDigit is { } row && hand.Fingers.FirstOrDefault(f => f.Id == row.Id) is { JointGuideIds.Length: >= 2 } selectedFinger &&
                row.CurlNormal.Value.TryNormalize(out var curl) && double.IsFinite(row.RollDegrees))
            {
                var basePoint = guides[selectedFinger.JointGuideIds[0]].Position;
                var direction = guides[selectedFinger.JointGuideIds[1]].Position - basePoint;
                if (direction.TryNormalize(out var axis))
                {
                    curl = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(axis, row.RollDegrees * Math.PI / 180)).TransformDirection(curl);
                    Line(basePoint, basePoint + curl * Math.Max(1e-5, direction.Length), new(.3f,.75f,1,1));
                }
            }
            var selected = Conformance.SelectedBodyProposal;
            var id = Conformance.SelectedBodyGuideId;
            if (selected is not null && id is not null && Conformance.CanEditBodyGuide)
            {
                double size = Math.Max(1e-5, (maximum - minimum).Length * .02);
                var origin = ToVector(selected.Position);
                foreach (var (axis, binding, color) in new[] { (Vector3.UnitX, TranslationGizmoAxis.X, new Vector4(1,.2f,.2f,1)), (Vector3.UnitY, TranslationGizmoAxis.Y, new Vector4(.2f,1,.2f,1)), (Vector3.UnitZ, TranslationGizmoAxis.Z, new Vector4(.3f,.5f,1,1)) })
                    lines.Add(new(GizmoKind.TranslationHandle, origin, origin + axis * (float)size, color, 3,
                        TranslationGizmoBinding.ForTarget(id.Value, binding, RenderGizmoSpace.Global), InteractionAxisWorld: axis));
            }
        }
        Viewport.SceneSource.SetGizmos(lines); _handOverlayVisible = true;
        return;
        void Line(Vector3D a, Vector3D b, Vector4 color)
        {
            var start = ToVector(a); var end = ToVector(b);
            if (float.IsFinite(start.X) && float.IsFinite(start.Y) && float.IsFinite(start.Z) && float.IsFinite(end.X) && float.IsFinite(end.Y) && float.IsFinite(end.Z))
                lines.Add(new(GizmoKind.Line, start, end, color, 2));
        }
        static Vector3 ToVector(Vector3D p) => new((float)p.X, (float)p.Y, (float)p.Z);
    }
}
