using System.Collections.Immutable;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.DL1.Assets.Catalog;

public sealed record RetailEmbeddedEffectCustody(RetailAssetRecord Parent,
    Rp6lResourceDescriptor Resource,Rp6lItemDescriptor Item,Rp6lChunkDescriptor Chunk,
    ImmutableArray<byte> BundlePayload,Rp6lEffectDefinition Definition);

public interface IRetailEmbeddedEffectProvider
{
    ValueTask<RetailEmbeddedEffectCustody> ReadEmbeddedCustodyAsync(RetailAssetRecord asset,
        CancellationToken cancellationToken=default);
}

public interface IRetailEmbeddedEffectCatalog
{
    ValueTask<RetailEmbeddedEffectCustody> ReadEmbeddedCustodyAsync(RetailAssetRecord asset,
        CancellationToken cancellationToken=default);
}