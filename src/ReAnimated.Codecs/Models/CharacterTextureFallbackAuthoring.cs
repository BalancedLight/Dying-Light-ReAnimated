using System.Collections.Immutable;
using ReAnimated.Codecs.Materials;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class CharacterTextureFallbackAuthoring
{
    public static CustomModelPackage Review(CustomModelPackage package, CharacterTextureFallbackReceipt proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        _ = CustomModelPackageSerializer.Serialize(package);
        var inventory = package.Document.CharacterResources ?? throw new InvalidDataException("The character inventory is missing.");
        var updated = inventory with
        {
            TextureFallbackReviews = inventory.TextureFallbackReviews.Where(r => r.MissingResourceId != proposal.MissingResourceId ||
                r.MaterialResourceId != proposal.MaterialResourceId || r.TextureIndex != proposal.TextureIndex).Append(proposal).ToImmutableArray(),
            CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [],
        };
        proposal.Validate(updated);
        var result = package with { Document = package.Document with { CharacterResources = updated, LastBuildReceipt = null } };
        Revalidate(result);
        return result;
    }

    public static void Revalidate(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var inventory = package.Document.CharacterResources;
        if (inventory is null || inventory.TextureFallbackReviews.IsEmpty) return;
        _ = CustomModelPackageSerializer.Serialize(package);
        var tables = new Dictionary<string, Dl1CompiledMaterialStringTable>(StringComparer.Ordinal);
        foreach (var receipt in inventory.TextureFallbackReviews)
        {
            receipt.Validate(inventory);
            var provider = inventory.Resources.Single(r => r.Id == receipt.ProviderResourceId);
            if (!tables.TryGetValue(provider.Id, out var table))
            {
                table = Dl1CompiledMaterialStringTable.Open(package.CompanionPayloads[provider.EntryPath!]);
                tables.Add(provider.Id, table);
            }
            var entry = table.Find(receipt.TextureNameHash)
                ?? throw new InvalidDataException("The requested texture name is absent from the original provider.");
            if (entry.Value != receipt.RequestedName.Name || entry.EntryIndex != receipt.RequestedName.TableIndex ||
                entry.SourceOffset != receipt.RequestedName.PayloadOffset || entry.LogicalByteLength != receipt.RequestedName.ByteLength ||
                entry.LogicalSha256 != receipt.RequestedName.PayloadSha256)
                throw new InvalidDataException("The reviewed texture lookup string changed.");
        }
    }
}

