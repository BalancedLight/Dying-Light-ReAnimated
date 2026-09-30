using ReAnimated.App.Infrastructure;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Task<Dl1RigTemplateResolution> PickConformanceRetailReferenceAsync(CancellationToken token)
    {
        if (AssetBrowser.SelectedAsset is not { Kind: AssetKind.Mesh, RetailAsset: { } asset })
            return Task.FromResult(Dl1RigTemplateResolution.Failed("selected-retail-mesh",
                "Select a retail mesh in Assets, then return here to use its exact skeleton."));
        return _rigTemplateProvider.ResolveSelectedMeshAsync(_assetWorkspace, asset, token);
    }
}
