using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _structuralOverlayVisible;
    private void PublishStructuralOverlay(SkeletonRenderData? skeleton, bool active = true)
    {
        if (!active)
        {
            if (_structuralOverlayVisible) Viewport.SceneSource.SetGizmos([]);
            _structuralOverlayVisible = false;
            return;
        }
        var lines = new List<GizmoRenderData>();
        if (skeleton is not null && Conformance.StructuralNode is { } node)
        {
            BoneRenderData[] matches = skeleton.Bones.Where(b => b.Name.Equals(node.Name, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length == 1)
            {
                TransformMatrix frame = CorePreviewAdapter.ToCoreMatrix(matches[0].WorldTransform * skeleton.RootTransform);
                foreach (var (axis, color) in new[]
                {
                    (Vector3D.UnitX, new Vector4(1, .2f, .2f, 1)),
                    (Vector3D.UnitY, new Vector4(.2f, 1, .2f, 1)),
                    (Vector3D.UnitZ, new Vector4(.3f, .6f, 1, 1)),
                })
                {
                    Line(node.PreparedFrame.Translation, node.PreparedFrame.TransformPoint(axis * .08), new(.5f, .5f, .5f, 1));
                    Line(frame.Translation, frame.TransformPoint(axis * .08), color);
                }
                Line(node.PreparedFrame.Translation, frame.Translation, new(1, .8f, .2f, 1));
            }
        }
        if (skeleton is not null && Conformance.StructuralPreviewEnabled &&
            Conformance.StructuralPreview is { AddedHelperId: { } added } preview)
        {
            string? name = preview.Candidate.Package.Document.AuthoredHelpers.FirstOrDefault(h => h.Id == added)?.Name;
            var addedBones = skeleton.Bones.Where(b => b.Name == name).Take(2).ToArray();
            if (addedBones.Length == 1)
            {
                var frame = CorePreviewAdapter.ToCoreMatrix(addedBones[0].WorldTransform * skeleton.RootTransform);
                // A visible marker for the proposed zero-offset helper; this is an
                // editor overlay, not collision geometry or a native bounds claim.
                var corners = Enumerable.Range(0, 8).Select(i => frame.TransformPoint(new(
                    (i & 1) == 0 ? -.012 : .012, (i & 2) == 0 ? -.012 : .012, (i & 4) == 0 ? -.012 : .012))).ToArray();
                for (int i = 0; i < 8; i++)
                    for (int bit = 1; bit <= 4; bit <<= 1)
                        if ((i & bit) == 0) Line(corners[i], corners[i | bit], new(1, .8f, .15f, 1));
            }
        }
        if (skeleton is not null && Conformance.StructuralPreviewEnabled &&
            Conformance.StructuralPreview?.RemovedHelperId is not null && Conformance.StructuralNode is { } removedNode)
        {
            var oldFrame = CorePreviewAdapter.ToCoreMatrix(skeleton.RootTransform) * removedNode.PreparedFrame;
            foreach (var axis in new[] { Vector3D.UnitX, Vector3D.UnitY, Vector3D.UnitZ })
                Line(oldFrame.TransformPoint(axis * -.018), oldFrame.TransformPoint(axis * .018), new(1, .2f, .2f, 1));
        }
        Viewport.SceneSource.SetGizmos(lines);
        _structuralOverlayVisible = true;
        void Line(Vector3D a, Vector3D b, Vector4 color)
        {
            var start = new Vector3((float)a.X, (float)a.Y, (float)a.Z);
            var end = new Vector3((float)b.X, (float)b.Y, (float)b.Z);
            if (float.IsFinite(start.X) && float.IsFinite(start.Y) && float.IsFinite(start.Z) &&
                float.IsFinite(end.X) && float.IsFinite(end.Y) && float.IsFinite(end.Z))
                lines.Add(new(GizmoKind.Line, start, end, color, 2));
        }
    }
}
