namespace ReAnimated.Renderer.D3D11;

internal sealed class D3D11EditorRenderPipeline : IDisposable
{
    private readonly IDisposable[] _disposables;
    private bool _disposed;

    public D3D11EditorRenderPipeline()
    {
        GpuSkinnedMeshRenderPass meshPass = new();
        SkeletonRenderPass skeletonPass = new();
        SelectionRenderPass selectionPass = new();
        AuthoringOverlayRenderPass authoringOverlayPass = new();
        GizmoRenderPass gizmoPass = new();
        FppSafeFrameRenderPass safeFramePass = new();
        Passes =
        [
            meshPass,
            skeletonPass,
            selectionPass,
            authoringOverlayPass,
            gizmoPass,
            safeFramePass,
        ];
        _disposables =
        [
            meshPass,
            skeletonPass,
            selectionPass,
            authoringOverlayPass,
            gizmoPass,
            safeFramePass,
        ];
    }

    public IReadOnlyList<ID3D11RenderPass> Passes { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable disposable in _disposables.Reverse())
        {
            disposable.Dispose();
        }
    }
}
