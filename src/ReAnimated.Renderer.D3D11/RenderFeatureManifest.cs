namespace ReAnimated.Renderer.D3D11;

public enum RenderFeature
{
    ClearSurface,
    MeshPassHook,
    SkeletonPassHook,
    SelectionHighlight,
    AuthoringOverlays,
    GizmoPassHook,
    GpuSkinning,
    MorphTargets,
    Dl1MaterialShading,
    FppSafeFrame,
    FppPostProcessing,
}

public enum RenderFeatureAvailability
{
    Available,
    HookOnly,
    NotImplemented,
}

public sealed record RenderFeatureStatus(
    RenderFeature Feature,
    RenderFeatureAvailability Availability,
    string Detail);

public static class RenderFeatureManifest
{
    private static readonly IReadOnlyList<RenderFeatureStatus> FeaturesValue =
    [
        new(RenderFeature.ClearSurface, RenderFeatureAvailability.Available,
            "D3D11 swap-chain clear and presentation are implemented."),
        new(RenderFeature.MeshPassHook, RenderFeatureAvailability.Available,
            "Indexed static and skinned meshes render with neutral preview lighting."),
        new(RenderFeature.SkeletonPassHook, RenderFeatureAvailability.Available,
            "Current-pose deform bones render as bounded white tapered wireframes; helper and prop pivots use a gold line-and-marker treatment, while camera helpers remain orange, in an always-visible editor overlay."),
        new(RenderFeature.SelectionHighlight, RenderFeatureAvailability.Available,
            "Selected bones receive an always-visible locator and parent highlight; selected meshes can opt into a skinned/morphed silhouette outline plus an exact current-deformation bounds highlight."),
        new(RenderFeature.AuthoringOverlays, RenderFeatureAvailability.Available,
            "Opt-in CPU-reference overlays render sampled root motion, current morphed/skinned bounds, selected-mesh bounds highlights, and scale-normalized per-bone local axes."),
        new(RenderFeature.GizmoPassHook, RenderFeatureAvailability.Available,
            "Editor line and axis gizmos render in an overlay pass."),
        new(RenderFeature.GpuSkinning, RenderFeatureAvailability.Available,
            "A D3D11 vertex shader evaluates four weighted influences against a bounded 256-matrix per-draw palette remapped into the complete retail skeleton; inverse-transpose normal palettes preserve lighting under non-uniform bone and local scale."),
        new(RenderFeature.MorphTargets, RenderFeatureAvailability.Available,
            "A structured-buffer vertex path blends the first 64 active position/normal targets in retail inventory order before skinning, using verbatim authored weights above the DL1 activity threshold."),
        new(RenderFeature.Dl1MaterialShading, RenderFeatureAvailability.Available,
            "Evidence-backed ABDM material names resolve retail type-8480 BC1/BC2/BC3 base-color textures for neutral preview lighting; exact DL1 techniques, variants, parameters, and shaders remain outside this pass."),
        new(RenderFeature.FppSafeFrame, RenderFeatureAvailability.Available,
            "Captured scene aspect is preserved by a centered D3D11 viewport and an in-airspace safe-frame overlay; evidence-classified FPP hands meshes can use a separate explicit projection."),
        new(RenderFeature.FppPostProcessing, RenderFeatureAvailability.NotImplemented,
            "EyeCamera and helper-basis preview is live; controller-dependent head/spine, inertia, anti-wall, and post effects remain unavailable."),
    ];

    public static IReadOnlyList<RenderFeatureStatus> Features => FeaturesValue;

    public static RenderFeatureStatus Get(RenderFeature feature)
    {
        return FeaturesValue.First(item => item.Feature == feature);
    }
}

public enum RendererAdapterMode
{
    Hardware,
    Warp,
}

public static class RendererDeviceSelectionPolicy
{
    private static readonly IReadOnlyList<RendererAdapterMode> HardwareOnly =
        [RendererAdapterMode.Hardware];

    private static readonly IReadOnlyList<RendererAdapterMode> HardwareThenWarp =
        [RendererAdapterMode.Hardware, RendererAdapterMode.Warp];

    private static readonly IReadOnlyList<RendererAdapterMode> WarpOnly =
        [RendererAdapterMode.Warp];

    public static IReadOnlyList<RendererAdapterMode> GetAttempts(
        bool allowWarpFallback,
        bool forceWarp = false)
    {
        if (forceWarp)
        {
            return WarpOnly;
        }

        return allowWarpFallback ? HardwareThenWarp : HardwareOnly;
    }
}

public enum RendererLifecycleState
{
    Starting,
    Ready,
    Recovering,
    Stopped,
    Faulted,
}

public sealed record D3D11RendererStatus(
    RendererLifecycleState State,
    RendererAdapterMode? AdapterMode,
    string Message,
    double FramesPerSecond,
    long PresentedFrames)
{
    public int ViewportPixelWidth { get; init; }

    public int ViewportPixelHeight { get; init; }
}
