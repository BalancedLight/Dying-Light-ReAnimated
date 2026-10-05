namespace ReAnimated.DL1.Assets.Catalog;

/// <summary>
/// Allows providers to withhold persistent catalog updates when enumeration
/// could not account for all of their sources.
/// </summary>
public interface IRetailAssetCatalogCachePolicy
{
    bool CanPersistCatalog { get; }
}
