using System.Collections.Immutable;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.DL1.Assets.Catalog;

public sealed record RetailRpackItemCustody(
    Rp6lItemDescriptor Descriptor,
    ImmutableArray<byte> Payload,
    string ContentSha256);

public sealed record RetailRpackChunkCustody(
    Rp6lChunkDescriptor Descriptor,
    string StoredSha256);

public sealed record RetailRpackResourceCustody(
    RetailAssetRecord Asset,
    Rp6lHeader Header,
    Rp6lResourceDescriptor Resource,
    ImmutableArray<RetailRpackItemCustody> Items,
    ImmutableArray<RetailRpackChunkCustody> Chunks,
    string ContentSha256);

public interface IRetailRpackResourceProvider
{
    ValueTask<RetailRpackResourceCustody> ReadRpackResourceCustodyAsync(
        RetailAssetRecord asset, CancellationToken cancellationToken = default);
}

public interface IRetailRpackResourceCatalog
{
    ValueTask<RetailRpackResourceCustody> ReadRpackResourceCustodyAsync(
        RetailAssetRecord asset, CancellationToken cancellationToken = default);
}
