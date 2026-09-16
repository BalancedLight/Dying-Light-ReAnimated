using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public static class ContactOverlayBuilder
{
    public static ImmutableArray<GizmoRenderData> Build(ContactPreview preview)
    {
        var lines = ImmutableArray.CreateBuilder<GizmoRenderData>();
        var fit = preview.Fit;
        int stride = Math.Max(1, (fit.Footprint.Length + 511) / 512);
        for (int i = 0; i < fit.Footprint.Length; i += stride)
            Line(fit.Footprint[i], fit.Footprint[(i + stride) < fit.Footprint.Length ? i + stride : 0], new(0, 1, 1, 1));
        var corners = new Vector3D[8];
        for (int i = 0; i < corners.Length; i++)
            corners[i] = preview.GlobalFrame.TransformPoint(preview.Center + new Vector3D(
                (i & 1) == 0 ? -preview.HalfExtents.X : preview.HalfExtents.X,
                (i & 2) == 0 ? -preview.HalfExtents.Y : preview.HalfExtents.Y,
                (i & 4) == 0 ? -preview.HalfExtents.Z : preview.HalfExtents.Z));
        for (int i = 0; i < corners.Length; i++)
            foreach (int bit in new[] { 1, 2, 4 }) if ((i & bit) == 0) Line(corners[i], corners[i | bit], new(1, .6f, .1f, 1));
        foreach (var (a, b) in new[] { (0, 1), (1, 5), (5, 4), (4, 0), (0, 5), (1, 4) })
            Line(corners[a], corners[b], new(.25f, 1, .3f, 1));
        double length = Math.Max(1e-6, preview.HalfExtents.Length);
        var origin = preview.GlobalFrame.Translation;
        Line(origin, origin + preview.GlobalFrame.TransformDirection(Vector3D.UnitX) * length, new(1, .2f, .2f, 1));
        Line(origin, origin + preview.GlobalFrame.TransformDirection(Vector3D.UnitY) * length, new(.2f, 1, .2f, 1));
        Line(origin, origin + preview.GlobalFrame.TransformDirection(Vector3D.UnitZ) * length, new(.3f, .5f, 1, 1));
        return lines.ToImmutable();
        void Line(Vector3D a, Vector3D b, Vector4 color)
        {
            var start = new Vector3((float)a.X, (float)a.Y, (float)a.Z);
            var end = new Vector3((float)b.X, (float)b.Y, (float)b.Z);
            if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(start.Z) ||
                !float.IsFinite(end.X) || !float.IsFinite(end.Y) || !float.IsFinite(end.Z)) return;
            lines.Add(new(GizmoKind.Line, start, end, color, 2));
        }
    }
}
