using System.Collections.Immutable;
using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Measures deformation-dependent mesh bounds once when an editor frame is
/// published. The render thread consumes only the resulting immutable values.
/// </summary>
public static class AuthoringOverlayBoundsPrecomputer
{
    public const int MaximumBoundsMeshCount = 4_096;

    public static ImmutableArray<DeformedMeshBoundsRenderData> Measure(
        RenderFrameSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int meshCount = Math.Min(
            frame.Meshes.Count,
            MaximumBoundsMeshCount);
        var bounds = ImmutableArray.CreateBuilder<
            DeformedMeshBoundsRenderData>(meshCount);
        for (int meshIndex = 0;
             meshIndex < meshCount;
             meshIndex++)
        {
            MeshRenderData mesh = frame.Meshes[meshIndex];
            if (!CpuMeshDeformationEvaluator.TryMeasureBounds(
                    mesh,
                    frame.Skeleton,
                    frame.MorphWeights,
                    out CpuDeformedBounds measured,
                    out _))
            {
                continue;
            }

            bounds.Add(new DeformedMeshBoundsRenderData(
                mesh.Id,
                measured.Minimum,
                measured.Maximum,
                mesh.IsSelected));
        }

        return bounds.MoveToImmutable();
    }
}

/// <summary>
/// Deterministically generates authoring-overlay lines on the CPU. Keeping the
/// geometry policy outside D3D11 makes scale/orientation behavior testable and
/// gives WARP and hardware devices identical inputs.
/// </summary>
public static class AuthoringOverlayGeometryBuilder
{
    public const int MaximumBoundsMeshCount =
        AuthoringOverlayBoundsPrecomputer.MaximumBoundsMeshCount;

    private static readonly Vector4 RootTrailColor =
        new(0.96f, 0.44f, 0.18f, 0.96f);
    private static readonly Vector4 CurrentRootColor =
        new(1.0f, 0.84f, 0.20f, 1.0f);
    private static readonly Vector4 BoundsColor =
        new(0.32f, 0.78f, 0.96f, 0.90f);
    private static readonly Vector4 SelectedMeshColor =
        new(1.0f, 0.78f, 0.12f, 1.0f);
    private static readonly Vector4 AxisXColor =
        new(0.96f, 0.22f, 0.18f, 1.0f);
    private static readonly Vector4 AxisYColor =
        new(0.24f, 0.90f, 0.30f, 1.0f);
    private static readonly Vector4 AxisZColor =
        new(0.24f, 0.48f, 1.0f, 1.0f);

    public static IReadOnlyList<AuthoringOverlayLine> BuildLines(
        RenderFrameSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RenderAuthoringOverlayState state = frame.AuthoringOverlays;
        if (state?.Options is not { } options)
        {
            return Array.Empty<AuthoringOverlayLine>();
        }

        List<AuthoringOverlayLine> lines = [];
        if (options.ShowRootMotionTrail &&
            state.RootMotionTrail is { } rootMotionTrail)
        {
            AddRootMotionTrail(
                lines,
                frame.Camera,
                options,
                rootMotionTrail);
        }

        if (options.ShowBoneLocalAxes &&
            frame.Skeleton is { } skeleton)
        {
            AddBoneAxes(
                lines,
                frame.Camera,
                options,
                skeleton);
        }

        if (options.ShowDeformedBounds ||
            options.HighlightSelectedMeshes)
        {
            AddMeshBounds(lines, state, options);
        }

        return lines.ToArray();
    }

    private static void AddRootMotionTrail(
        List<AuthoringOverlayLine> lines,
        RenderCamera camera,
        RenderAuthoringOverlayOptions options,
        RootMotionTrailRenderData trail)
    {
        ReadOnlySpan<Vector3> positions =
            trail.WorldPositions.AsSpan();
        int pointCount = Math.Min(
            positions.Length,
            RootMotionTrailRenderData.MaximumPointCount);
        for (int index = 1; index < pointCount; index++)
        {
            AddLine(
                lines,
                positions[index - 1],
                positions[index],
                RootTrailColor,
                AuthoringOverlayPrimitive.RootMotionTrail);
        }

        if (trail.CurrentSampleIndex < 0 ||
            trail.CurrentSampleIndex >= pointCount)
        {
            return;
        }

        Vector3 current = positions[trail.CurrentSampleIndex];
        float markerSize =
            ResolveBoneAxisLength(options, camera, current) * 0.38f;
        AddLine(
            lines,
            current - (Vector3.UnitX * markerSize),
            current + (Vector3.UnitX * markerSize),
            CurrentRootColor,
            AuthoringOverlayPrimitive.CurrentRootMarker);
        AddLine(
            lines,
            current - (Vector3.UnitY * markerSize),
            current + (Vector3.UnitY * markerSize),
            CurrentRootColor,
            AuthoringOverlayPrimitive.CurrentRootMarker);
        AddLine(
            lines,
            current - (Vector3.UnitZ * markerSize),
            current + (Vector3.UnitZ * markerSize),
            CurrentRootColor,
            AuthoringOverlayPrimitive.CurrentRootMarker);
    }

    private static void AddBoneAxes(
        List<AuthoringOverlayLine> lines,
        RenderCamera camera,
        RenderAuthoringOverlayOptions options,
        SkeletonRenderData skeleton)
    {
        foreach (BoneRenderData bone in skeleton.Bones)
        {
            if (!skeleton.IsVisible(bone))
            {
                continue;
            }

            Matrix4x4 world =
                bone.WorldTransform * skeleton.RootTransform;
            Vector3 origin = world.Translation;
            float length =
                ResolveBoneAxisLength(options, camera, origin);
            AddBoneAxis(
                lines,
                origin,
                Vector3.TransformNormal(Vector3.UnitX, world),
                length,
                AxisXColor,
                AuthoringOverlayPrimitive.BoneAxisX);
            AddBoneAxis(
                lines,
                origin,
                Vector3.TransformNormal(Vector3.UnitY, world),
                length,
                AxisYColor,
                AuthoringOverlayPrimitive.BoneAxisY);
            AddBoneAxis(
                lines,
                origin,
                Vector3.TransformNormal(Vector3.UnitZ, world),
                length,
                AxisZColor,
                AuthoringOverlayPrimitive.BoneAxisZ);
        }
    }

    private static void AddBoneAxis(
        List<AuthoringOverlayLine> lines,
        Vector3 origin,
        Vector3 direction,
        float length,
        Vector4 color,
        AuthoringOverlayPrimitive primitive)
    {
        float lengthSquared = direction.LengthSquared();
        if (!IsFinite(origin) ||
            !float.IsFinite(lengthSquared) ||
            lengthSquared <= 1.0e-12f ||
            !float.IsFinite(length) ||
            length <= 0.0f)
        {
            return;
        }

        Vector3 normalized = direction / MathF.Sqrt(lengthSquared);
        AddLine(
            lines,
            origin,
            origin + (normalized * length),
            color,
            primitive);
    }

    private static void AddMeshBounds(
        List<AuthoringOverlayLine> lines,
        RenderAuthoringOverlayState state,
        RenderAuthoringOverlayOptions options)
    {
        bool hasSelectedBounds = false;
        Vector3 selectedMinimum =
            new(float.PositiveInfinity);
        Vector3 selectedMaximum =
            new(float.NegativeInfinity);
        ImmutableArray<DeformedMeshBoundsRenderData> measuredBounds =
            state.DeformedMeshBounds.IsDefault
                ? []
                : state.DeformedMeshBounds;
        foreach (DeformedMeshBoundsRenderData measured in measuredBounds)
        {
            bool selectedHighlight =
                options.HighlightSelectedMeshes &&
                measured.IsSelected;
            if (!options.ShowDeformedBounds &&
                !selectedHighlight)
            {
                continue;
            }

            if (options.ShowDeformedBounds)
            {
                AddBounds(
                    lines,
                    new CpuDeformedBounds(
                        measured.Minimum,
                        measured.Maximum),
                    BoundsColor,
                    AuthoringOverlayPrimitive.DeformedBounds);
            }

            if (selectedHighlight)
            {
                selectedMinimum = Vector3.Min(
                    selectedMinimum,
                    measured.Minimum);
                selectedMaximum = Vector3.Max(
                    selectedMaximum,
                    measured.Maximum);
                hasSelectedBounds = true;
            }
        }

        if (hasSelectedBounds)
        {
            AddBounds(
                lines,
                new CpuDeformedBounds(
                    selectedMinimum,
                    selectedMaximum),
                SelectedMeshColor,
                AuthoringOverlayPrimitive.SelectedMeshHighlight);
        }
    }

    private static void AddBounds(
        List<AuthoringOverlayLine> lines,
        CpuDeformedBounds bounds,
        Vector4 color,
        AuthoringOverlayPrimitive primitive)
    {
        Vector3 minimum = bounds.Minimum;
        Vector3 maximum = bounds.Maximum;
        if (!IsFinite(minimum) || !IsFinite(maximum))
        {
            return;
        }

        Vector3[] corners =
        [
            new(minimum.X, minimum.Y, minimum.Z),
            new(maximum.X, minimum.Y, minimum.Z),
            new(maximum.X, maximum.Y, minimum.Z),
            new(minimum.X, maximum.Y, minimum.Z),
            new(minimum.X, minimum.Y, maximum.Z),
            new(maximum.X, minimum.Y, maximum.Z),
            new(maximum.X, maximum.Y, maximum.Z),
            new(minimum.X, maximum.Y, maximum.Z),
        ];
        AddEdge(lines, corners, 0, 1, color, primitive);
        AddEdge(lines, corners, 1, 2, color, primitive);
        AddEdge(lines, corners, 2, 3, color, primitive);
        AddEdge(lines, corners, 3, 0, color, primitive);
        AddEdge(lines, corners, 4, 5, color, primitive);
        AddEdge(lines, corners, 5, 6, color, primitive);
        AddEdge(lines, corners, 6, 7, color, primitive);
        AddEdge(lines, corners, 7, 4, color, primitive);
        AddEdge(lines, corners, 0, 4, color, primitive);
        AddEdge(lines, corners, 1, 5, color, primitive);
        AddEdge(lines, corners, 2, 6, color, primitive);
        AddEdge(lines, corners, 3, 7, color, primitive);
    }

    private static void AddEdge(
        List<AuthoringOverlayLine> lines,
        Vector3[] corners,
        int start,
        int end,
        Vector4 color,
        AuthoringOverlayPrimitive primitive) =>
        AddLine(
            lines,
            corners[start],
            corners[end],
            color,
            primitive);

    private static void AddLine(
        List<AuthoringOverlayLine> lines,
        Vector3 start,
        Vector3 end,
        Vector4 color,
        AuthoringOverlayPrimitive primitive)
    {
        if (!IsFinite(start) ||
            !IsFinite(end) ||
            !IsFinite(color))
        {
            return;
        }

        lines.Add(new AuthoringOverlayLine(
            start,
            end,
            color,
            primitive));
    }

    private static float ResolveBoneAxisLength(
        RenderAuthoringOverlayOptions options,
        RenderCamera camera,
        Vector3 position)
    {
        float scale =
            float.IsFinite(options.BoneAxisDistanceScale) &&
            options.BoneAxisDistanceScale > 0.0f
                ? options.BoneAxisDistanceScale
                : 0.018f;
        float minimum =
            float.IsFinite(options.MinimumBoneAxisLength) &&
            options.MinimumBoneAxisLength > 0.0f
                ? options.MinimumBoneAxisLength
                : 0.025f;
        float maximum =
            float.IsFinite(options.MaximumBoneAxisLength) &&
            options.MaximumBoneAxisLength >= minimum
                ? options.MaximumBoneAxisLength
                : MathF.Max(minimum, 0.35f);
        float distance = Vector3.Distance(camera.Eye, position);
        if (!float.IsFinite(distance))
        {
            return minimum;
        }

        return Math.Clamp(distance * scale, minimum, maximum);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W);
}

/// <summary>
/// Renders opt-in root trails, current deformed bounds, mesh selection
/// highlights, and per-bone local axes above the scene depth buffer.
/// </summary>
public sealed class AuthoringOverlayRenderPass : D3D11LineRenderPassBase
{
    public AuthoringOverlayRenderPass()
        : base(renderAsOverlay: true)
    {
    }

    public override string Name => "Authoring overlays";

    public override RenderFeature Feature =>
        RenderFeature.AuthoringOverlays;

    protected override void BuildVertices(
        RenderFrameSnapshot frame,
        int width,
        int height,
        List<LineRenderVertex> vertices)
    {
        foreach (AuthoringOverlayLine line in
                 AuthoringOverlayGeometryBuilder.BuildLines(frame))
        {
            AddLine(
                vertices,
                line.Start,
                line.End,
                line.Color);
        }
    }
}
