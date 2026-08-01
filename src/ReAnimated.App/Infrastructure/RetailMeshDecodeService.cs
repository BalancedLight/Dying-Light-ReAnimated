using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.App.Infrastructure;

public interface IRetailMeshDecodeService
{
    Task<Dl1MeshPreviewPayload> DecodeAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken);
}

internal sealed class RetailMeshDecodeService(
    Dl1AssetWorkspace workspace) : IRetailMeshDecodeService
{
    private readonly Dl1AssetWorkspace _workspace = workspace
        ?? throw new ArgumentNullException(nameof(workspace));

    public Task<Dl1MeshPreviewPayload> DecodeAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken) =>
        _workspace.DecodeMeshAsync(asset, cancellationToken);
}
