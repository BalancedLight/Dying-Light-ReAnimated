using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererFeatureManifestTests
{
    [Fact]
    public void ManifestReportsImplementedEditorPassesAndRemainingGaps()
    {
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.ClearSurface).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.MeshPassHook).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.SkeletonPassHook).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.GpuSkinning).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.SelectionHighlight).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.AuthoringOverlays).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.MorphTargets).Availability);
        Assert.Equal(
            RenderFeatureAvailability.Available,
            RenderFeatureManifest.Get(RenderFeature.Dl1MaterialShading).Availability);
    }

    [Fact]
    public void DevicePolicyAttemptsHardwareBeforeWarp()
    {
        Assert.Equal(
            [RendererAdapterMode.Hardware, RendererAdapterMode.Warp],
            RendererDeviceSelectionPolicy.GetAttempts(allowWarpFallback: true));
        Assert.Equal(
            [RendererAdapterMode.Hardware],
            RendererDeviceSelectionPolicy.GetAttempts(allowWarpFallback: false));
    }
}
