using ReAnimated.App.Infrastructure;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _contactOverlayVisible;
    private bool _contactPreviewActive;
    private void OnContactPreviewChanged(object? sender, EventArgs e)
    {
        if (_disposed || _suppressPreviewRefresh) return;
        if (Conformance.HasContactPreview && IsConformTabSelected && Conformance.IsStudioHelpers) Timeline.IsPlaying = false;
        RefreshPreview();
    }
    private void PublishContactOverlay()
    {
        if (IsConformTabSelected && Conformance.IsStudioHelpers && Conformance.GetContactPreview() is { } preview)
        {
            Viewport.SceneSource.SetGizmos(ContactOverlayBuilder.Build(preview));
            _contactOverlayVisible = true;
        }
        else if (_contactOverlayVisible)
        {
            Viewport.SceneSource.SetGizmos([]);
            _contactOverlayVisible = false;
        }
    }
}
