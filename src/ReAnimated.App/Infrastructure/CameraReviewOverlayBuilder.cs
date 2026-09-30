using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

internal static class CameraReviewOverlayBuilder
{
    public static RenderCamera? CreateRenderCamera(CameraReviewFrame frame)
    {
        Vector3 eye = ToVector(frame.Position);
        Vector3 target = ToVector(frame.Position + frame.Forward);
        if (!Finite(eye) || !Finite(target) || Vector3.DistanceSquared(eye, target) < 1e-12f) return null;
        return new RenderCamera(eye, target, ToVector(frame.Up), (float)frame.Lens.VerticalFieldOfViewDegrees,
            (float)frame.Lens.NearClipMeters, (float)frame.Lens.FarClipMeters)
        { ProjectionAspectRatio = (float)frame.Lens.AspectRatio };
    }

    public static IReadOnlyList<GizmoRenderData> Build(CameraReviewFrame frame, double drawingDepth)
    {
        if (!double.IsFinite(drawingDepth) || drawingDepth <= 0) return [];
        double depth = Math.Min(frame.Lens.FarClipMeters, Math.Max(frame.Lens.NearClipMeters, drawingDepth));
        var end = frame.GetCornersAtDepth(depth);
        var lines = new List<GizmoRenderData>();
        Line(frame.Position, frame.Position + frame.Right * .08, new(1,.2f,.2f,1));
        Line(frame.Position, frame.Position + frame.Up * .08, new(.2f,1,.2f,1));
        Line(frame.Position, frame.Position + frame.Forward * .18, new(.2f,.5f,1,1));
        for (int i = 0; i < 4; i++)
        {
            Line(frame.NearCorners[i], frame.NearCorners[(i+1)%4], new(1,.3f,.15f,1));
            Line(end[i], end[(i+1)%4], new(.1f,.8f,1,1));
            Line(frame.NearCorners[i], end[i], new(.2f,.65f,.8f,1));
        }
        return lines;
        void Line(Vector3D start, Vector3D finish, Vector4 color)
        {
            Vector3 a = ToVector(start), b = ToVector(finish);
            if (Finite(a) && Finite(b)) lines.Add(new(GizmoKind.Line,a,b,color,2));
        }
    }
    private static Vector3 ToVector(Vector3D v) => new((float)v.X,(float)v.Y,(float)v.Z);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
