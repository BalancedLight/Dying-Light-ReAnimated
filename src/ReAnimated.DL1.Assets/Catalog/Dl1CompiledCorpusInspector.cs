using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.DL1.Assets.Catalog;

public sealed record Dl1CorpusItemIdentity(int Slot, int ItemIndex, uint Flags, short StorageGroupId, int Bytes, string Sha256);

public sealed record Dl1CompiledCorpusEvidence(
    RetailAssetId AssetId, string ContentSha256, ImmutableArray<Dl1CorpusItemIdentity> Items,
    CompactMeshDocument? Hierarchy, string? EmbeddedAnimationScriptAlias,
    ImmutableArray<string> VariantNames, ImmutableArray<CompiledMeshSkinDefinition> SkinDefinitions,
    ParsedAnimationScr? AnimationScript, ImmutableArray<string> Unknowns)
{
    public string EvidenceLevel { get; } = "Decoded compiled data; native loading and behavior unverified.";
    public bool RuntimeBindingVerified { get; }
}

/// <summary>Joins decoded native data to a corpus row by physical identity and full stream hash.</summary>
public static class Dl1CompiledCorpusInspector
{
    public static async Task<Dl1CompiledCorpusEvidence> InspectAsync(RetailAssetRecord asset,
        Rp6lArchive archive, Rp6lChunkCache cache, string expectedContentSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(cache);
        if (asset.Id.ResourceType is not (Rp6lResourceTypes.Mesh or Rp6lResourceTypes.AnimationScript))
            throw new ArgumentException("Compiled inspection requires a mesh or animation-script resource.", nameof(asset));
        int index = asset.Source.ResourceIndex ?? throw new InvalidDataException("The asset has no physical resource index.");
        if (index < 0 || index >= archive.Resources.Count || !Path.GetFullPath(asset.Source.ContainerPath).Equals(archive.Path, StringComparison.OrdinalIgnoreCase) ||
            !asset.Id.SourceFingerprint.Equals(archive.CacheIdentity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive does not match the catalog's physical resource identity.");
        Rp6lResourceDescriptor resource = archive.Resources[index];
        if (RetailAssetLogicalId.Rpack(resource.ResourceType, resource.Name) != asset.Id.LogicalId || resource.Items.Count > 64)
            throw new InvalidDataException("Resource identity changed or the item count exceeds the inspection bound.");
        var itemBytes = new List<byte[]>(resource.Items.Count);
        var items = ImmutableArray.CreateBuilder<Dl1CorpusItemIdentity>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long totalBytes = 0;
        foreach (Rp6lItemDescriptor item in resource.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.HasReadableSize || (totalBytes = checked(totalBytes + item.SizeOrHash)) > 256L * 1024 * 1024)
                throw new InvalidDataException("Compiled resource is unreadable or exceeds the inspection byte bound.");
            byte[] bytes = await archive.ReadItemBytesAsync(item, cache, maximumBytes: 256 * 1024 * 1024,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            hash.AppendData(bytes);
            items.Add(new(items.Count, item.Index, item.Flags, item.StorageGroupId, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            itemBytes.Add(bytes);
        }
        string digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!digest.Equals(expectedContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Compiled inspection bytes do not match the captured corpus content hash.");
        CompactMeshDocument? hierarchy = null;
        string? alias = null;
        ParsedAnimationScr? animationScript = null;
        ImmutableArray<string> variants = [];
        ImmutableArray<CompiledMeshSkinDefinition> skins = [];
        var unknowns = ImmutableArray.CreateBuilder<string>();
        if (resource.ResourceType == Rp6lResourceTypes.Mesh)
        {
            if (itemBytes.Count < 3) throw new InvalidDataException("Compiled mesh is missing its metadata, variant or resolver items.");
            hierarchy = CompactMeshDecoder.Decode(itemBytes[0]);
            alias = Dl1RuntimeMeshObjectValidator.ReadAnimationScriptAlias(itemBytes[0]);
            if (itemBytes.Count >= 5)
            {
                CompiledMeshGeometryDocument geometry = CompiledMeshGeometryDecoder.Decode(itemBytes[0], itemBytes[1], itemBytes[3], itemBytes[4],
                    retailResourceName: resource.Name, cancellationToken: cancellationToken);
                variants = geometry.VariantNames.ToImmutableArray();
                skins = geometry.SkinDefinitions.ToImmutableArray();
                foreach (CompactMeshDiagnostic diagnostic in geometry.Diagnostics) unknowns.Add(diagnostic.Code + ": " + diagnostic.Message);
                if (skins.IsEmpty) unknowns.Add("Variant names are lexical candidates; structured skin definitions were not decoded.");
            }
            else unknowns.Add("Metadata-only mesh: structured variant and geometry inspection is unavailable in this adapter.");
            unknowns.Add("Compiled skin definitions are not an authored CHR companion. Source CHR identity and transforms require separate evidence.");
            unknowns.Add("Entity inventory does not establish which runtime consumers require each role.");
        }
        else
        {
            if (itemBytes.Count != 2) throw new InvalidDataException("Animation-script inspection requires exactly two known sections.");
            animationScript = AnimationScrCodec.Parse(new(itemBytes[0], itemBytes[1]));
            unknowns.Add("Compiled sequence names do not prove clip references, source includes or event behavior; retain source dependencies separately.");
        }
        return new(asset.Id, digest, items.ToImmutable(), hierarchy, alias, variants, skins, animationScript, unknowns.ToImmutable());
    }
}
