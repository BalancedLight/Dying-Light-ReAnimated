using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

public static class RenderCameraFraming
{
    private const float MinimumRadius = 0.05f;
    private const float MinimumDistance = 0.25f;
    private const float Padding = 1.10f;

    public static bool TryFrame(
        RenderFrameSnapshot frame,
        out RenderCamera camera)
    {
        ArgumentNullException.ThrowIfNull(frame);
        BoundsAccumulator bounds = new();
        foreach (MeshRenderData mesh in frame.Meshes)
        {
            AccumulateMesh(frame, mesh, ref bounds);
        }

        if (frame.Skeleton is { } skeleton)
        {
            foreach (BoneRenderData bone in skeleton.Bones)
            {
                if (!skeleton.IsVisible(bone))
                {
                    continue;
                }

                Matrix4x4 world =
                    bone.WorldTransform * skeleton.RootTransform;
                bounds.Add(world.Translation);
            }
        }

        foreach (GizmoRenderData gizmo in frame.Gizmos)
        {
            bounds.Add(gizmo.Start);
            bounds.Add(gizmo.End);
        }

        if (!bounds.HasValue)
        {
            camera = frame.Camera;
            return false;
        }

        Vector3 center = (bounds.Minimum + bounds.Maximum) * 0.5f;
        float radius = MathF.Max(
            MinimumRadius,
            Vector3.Distance(bounds.Minimum, bounds.Maximum) * 0.5f);
        float fieldOfView = Math.Clamp(
            frame.Camera.VerticalFieldOfViewDegrees,
            1.0f,
            179.0f);
        float halfRadians =
            fieldOfView * (MathF.PI / 360.0f);
        Vector3 viewDirection =
            frame.Camera.Eye - frame.Camera.Target;
        if (!IsFinite(viewDirection) ||
            viewDirection.LengthSquared() < 1.0e-8f)
        {
            viewDirection = Vector3.UnitZ;
        }

        viewDirection = Vector3.Normalize(viewDirection);
        Vector3 up = frame.Camera.Up;
        if (!IsFinite(up) ||
            up.LengthSquared() < 1.0e-8f ||
            MathF.Abs(Vector3.Dot(
                Vector3.Normalize(up),
                viewDirection)) > 0.999f)
        {
            up = Vector3.UnitY;
            if (MathF.Abs(Vector3.Dot(up, viewDirection)) > 0.999f)
            {
                up = Vector3.UnitZ;
            }
        }

        up = Vector3.Normalize(up);
        Vector3 right = Vector3.Normalize(
            Vector3.Cross(up, viewDirection));
        float verticalTangent = MathF.Tan(halfRadians);
        float aspectRatio =
            frame.Camera.ProjectionAspectRatio is > 0.0f and var aspect &&
            float.IsFinite(aspect)
                ? aspect
                : 1.0f;
        float horizontalTangent =
            verticalTangent * aspectRatio;
        float distance = MathF.Max(
            MinimumDistance,
            CalculateRequiredDistance(
                bounds.Minimum,
                bounds.Maximum,
                center,
                viewDirection,
                up,
                right,
                verticalTangent,
                horizontalTangent));

        camera = frame.Camera with
        {
            Eye = center + viewDirection * distance,
            Target = center,
            Up = up,
            FarPlane = MathF.Max(
                frame.Camera.FarPlane,
                distance + radius * 4.0f),
        };
        return true;
    }

    private static float CalculateRequiredDistance(
        Vector3 minimum,
        Vector3 maximum,
        Vector3 center,
        Vector3 viewDirection,
        Vector3 up,
        Vector3 right,
        float verticalTangent,
        float horizontalTangent)
    {
        float requiredDistance = 0.0f;
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new(
                (corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y,
                (corner & 4) == 0 ? minimum.Z : maximum.Z);
            Vector3 relative = point - center;
            float towardEye =
                Vector3.Dot(relative, viewDirection);
            float verticalDistance =
                MathF.Abs(Vector3.Dot(relative, up)) *
                Padding /
                verticalTangent;
            float horizontalDistance =
                MathF.Abs(Vector3.Dot(relative, right)) *
                Padding /
                horizontalTangent;
            requiredDistance = MathF.Max(
                requiredDistance,
                towardEye + MathF.Max(
                    verticalDistance,
                    horizontalDistance));
        }

        return requiredDistance;
    }

    private static void AccumulateMesh(
        RenderFrameSnapshot frame,
        MeshRenderData mesh,
        ref BoundsAccumulator bounds)
    {
        Matrix4x4[]? skinPalette = null;
        if (mesh.IsSkinned && frame.Skeleton is not null)
        {
            try
            {
                skinPalette = GpuSkinningPalette.Build(
                    mesh,
                    frame.Skeleton);
            }
            catch (ArgumentException)
            {
                skinPalette = null;
            }
        }

        ActiveMorphTarget[] activeMorphs =
            MorphTargetSelection.Select(
                mesh,
                frame.MorphWeights);
        ReadOnlySpan<MeshVertex> vertices = mesh.Vertices.Span;
        for (int vertexIndex = 0;
             vertexIndex < vertices.Length;
             vertexIndex++)
        {
            MeshVertex vertex = vertices[vertexIndex];
            Vector3 localPosition = vertex.Position;
            foreach (ActiveMorphTarget morph in activeMorphs)
            {
                if (vertexIndex <
                    morph.Target.PositionDeltas.Length)
                {
                    localPosition +=
                        morph.Target.PositionDeltas.Span[vertexIndex]
                        * morph.Weight;
                }
            }

            Vector3 worldPosition = skinPalette is null
                ? Vector3.Transform(
                    localPosition,
                    mesh.LocalToWorld)
                : SkinPosition(
                    localPosition,
                    vertex,
                    skinPalette);
            bounds.Add(worldPosition);
        }
    }

    private static Vector3 SkinPosition(
        Vector3 position,
        MeshVertex vertex,
        Matrix4x4[] palette)
    {
        Vector4 weights = vertex.BoneWeights;
        float sum = weights.X + weights.Y + weights.Z + weights.W;
        if (!float.IsFinite(sum) || sum <= 1.0e-6f)
        {
            return position;
        }

        weights /= sum;
        return
            Vector3.Transform(
                position,
                palette[ToBoneIndex(
                    vertex.BoneIndices.X,
                    palette.Length)]) * weights.X +
            Vector3.Transform(
                position,
                palette[ToBoneIndex(
                    vertex.BoneIndices.Y,
                    palette.Length)]) * weights.Y +
            Vector3.Transform(
                position,
                palette[ToBoneIndex(
                    vertex.BoneIndices.Z,
                    palette.Length)]) * weights.Z +
            Vector3.Transform(
                position,
                palette[ToBoneIndex(
                    vertex.BoneIndices.W,
                    palette.Length)]) * weights.W;
    }

    private static int ToBoneIndex(float value, int count)
    {
        if (!float.IsFinite(value))
        {
            return 0;
        }

        float rounded = MathF.Round(value);
        if (rounded <= 0.0f)
        {
            return 0;
        }

        if (rounded >= count - 1)
        {
            return count - 1;
        }

        return (int)rounded;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private struct BoundsAccumulator
    {
        public Vector3 Minimum;

        public Vector3 Maximum;

        public bool HasValue;

        public void Add(Vector3 point)
        {
            if (!IsFinite(point))
            {
                return;
            }

            if (!HasValue)
            {
                Minimum = point;
                Maximum = point;
                HasValue = true;
                return;
            }

            Minimum = Vector3.Min(Minimum, point);
            Maximum = Vector3.Max(Maximum, point);
        }
    }
}
