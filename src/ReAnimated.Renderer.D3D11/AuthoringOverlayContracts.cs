using System.Collections.Immutable;
using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Opt-in editor overlays that are evaluated from the immutable render-frame
/// snapshot. Every switch defaults off so publishing the contract cannot add
/// visual clutter before the editor explicitly exposes the corresponding
/// authoring control.
/// </summary>
public sealed record RenderAuthoringOverlayOptions(
    bool ShowRootMotionTrail = false,
    bool ShowDeformedBounds = false,
    bool ShowBoneLocalAxes = false,
    bool HighlightSelectedMeshes = false)
{
    /// <summary>
    /// Scales a bone axis by its distance from the camera so it remains
    /// readable without inheriting scale or reflection from the bone matrix.
    /// </summary>
    public float BoneAxisDistanceScale { get; init; } = 0.018f;

    public float MinimumBoneAxisLength { get; init; } = 0.025f;

    public float MaximumBoneAxisLength { get; init; } = 0.35f;

    public static RenderAuthoringOverlayOptions Disabled { get; } = new();
}

/// <summary>
/// Sampled root positions in renderer world space. The editor/evaluator owns
/// timing; the renderer only connects the immutable samples and marks the
/// current sample when it is in range.
/// </summary>
public sealed record RootMotionTrailRenderData
{
    public const int MaximumPointCount = 65_536;

    public RootMotionTrailRenderData(
        ReadOnlyMemory<Vector3> worldPositions,
        int currentSampleIndex = -1)
        : this(
            worldPositions.ToArray().ToImmutableArray(),
            currentSampleIndex)
    {
    }

    public RootMotionTrailRenderData(
        ImmutableArray<Vector3> worldPositions,
        int currentSampleIndex = -1)
    {
        if (worldPositions.IsDefault)
        {
            throw new ArgumentException(
                "Root-motion positions must be initialized.",
                nameof(worldPositions));
        }

        WorldPositions = worldPositions;
        CurrentSampleIndex = currentSampleIndex;
    }

    public ImmutableArray<Vector3> WorldPositions { get; init; }

    public int CurrentSampleIndex { get; init; }
}

/// <summary>
/// Bounds measured from one immutable mesh/skeleton/morph snapshot before the
/// renderer begins traversing the frame.
/// </summary>
public readonly record struct DeformedMeshBoundsRenderData(
    string MeshId,
    Vector3 Minimum,
    Vector3 Maximum,
    bool IsSelected);

/// <summary>
/// Immutable state consumed by the authoring-overlay pass.
/// </summary>
public sealed record RenderAuthoringOverlayState(
    RenderAuthoringOverlayOptions Options,
    RootMotionTrailRenderData? RootMotionTrail,
    ImmutableArray<DeformedMeshBoundsRenderData> DeformedMeshBounds = default)
{
    public static RenderAuthoringOverlayState Disabled { get; } =
        new(RenderAuthoringOverlayOptions.Disabled, null, []);
}

/// <summary>
/// Shared mesh-tint policy for the GPU pass and deterministic tests.
/// </summary>
public static class MeshSelectionHighlightPolicy
{
    public static bool ShouldRenderOutline(
        MeshRenderData mesh,
        RenderAuthoringOverlayState authoringOverlays)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(authoringOverlays);
        return mesh.IsSelected &&
            authoringOverlays.Options.HighlightSelectedMeshes;
    }

    public static Vector4 ResolveTint(
        MeshRenderData mesh,
        RenderAuthoringOverlayState authoringOverlays)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(authoringOverlays);
        return ShouldRenderOutline(mesh, authoringOverlays)
                ? Vector4.Min(
                    mesh.Tint *
                    new Vector4(1.32f, 1.24f, 0.72f, 1.0f),
                    Vector4.One)
                : mesh.Tint;
    }
}

public enum AuthoringOverlayPrimitive
{
    RootMotionTrail,
    CurrentRootMarker,
    DeformedBounds,
    SelectedMeshHighlight,
    BoneAxisX,
    BoneAxisY,
    BoneAxisZ,
}

/// <summary>
/// A deterministic CPU-generated line used both by the D3D11 pass and by
/// reference tests.
/// </summary>
public readonly record struct AuthoringOverlayLine(
    Vector3 Start,
    Vector3 End,
    Vector4 Color,
    AuthoringOverlayPrimitive Primitive);
