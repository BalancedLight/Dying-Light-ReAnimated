using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class CharacterAccessoryMaterialAuthoring
{
    public static FbxModelAuthoringImportResult ReuseRetainedSlot(FbxModelAuthoringImportResult model,
        Guid appendedMaterialId, Guid retainedMaterialId, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!reviewed) throw new InvalidOperationException("Review the accessory material binding first.");
        if (appendedMaterialId == Guid.Empty || retainedMaterialId == Guid.Empty || appendedMaterialId == retainedMaterialId)
            throw new ArgumentException("Choose distinct appended and retained material identifiers.");
        CustomModelPackage package = model.Package;
        if (package.Document.Source.Kind != CustomModelSourceKind.StockCharacter ||
            package.Document.CharacterResources is not { } inventory)
            throw new InvalidDataException("Retained material reuse requires a decoded stock character.");
        _ = CustomModelPackageSerializer.Serialize(package);
        FbxModelAuthoringImportResult original = DecodedCharacterSnapshotCodec.DecodeSource(package);
        ImmutableArray<CustomModelMaterial> originalMaterials = original.Package.Document.Materials;
        ImmutableArray<CustomModelMaterial> materials = package.Document.Materials;
        if (originalMaterials.IsDefaultOrEmpty || materials.Length < originalMaterials.Length ||
            originalMaterials.Select(material => material.Id).Distinct().Count() != originalMaterials.Length)
            throw new InvalidDataException("The original material prefix is missing or ambiguous.");
        JsonSerializerOptions options = CustomModelPackageSerializer.CreateSerializerOptions();
        for (int index = 0; index < originalMaterials.Length; index++)
        {
            if (JsonSerializer.Serialize(originalMaterials[index], options) != JsonSerializer.Serialize(materials[index], options))
                throw new InvalidDataException("An original material row changed before the accessory binding.");
        }
        int appendedIndex = FindUniqueIndex(materials, appendedMaterialId);
        int retainedIndex = FindUniqueIndex(materials, retainedMaterialId);
        if (appendedIndex < originalMaterials.Length || retainedIndex >= originalMaterials.Length)
            throw new InvalidDataException("Choose an appended material and an original retained material slot.");
        string reference = materials[retainedIndex].ExistingDl1MaterialReference
            ?? throw new InvalidDataException("The retained slot has no exact native material reference.");
        string fileName = Path.GetFileName(reference.Replace('\\', '/'));
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string emitted = string.IsNullOrWhiteSpace(stem) ? string.Empty
            : Dl1SourceModelWriter.SanitizeName(stem, 55) + ".mat";
        if (!string.Equals(reference, emitted, StringComparison.Ordinal))
            throw new InvalidDataException("The native material reference would change during source emission.");
        CharacterResourceRecord[] receipts = inventory.Resources.Where(resource =>
            resource.Material?.MaterialName == reference && resource.LogicalName == reference).ToArray();
        if (receipts.Length != 1 || !HasCurrentCustody(receipts[0], package))
            throw new InvalidDataException("The retained material has missing, stale, or ambiguous custody.");
        CharacterResourceRecord materialRecord = receipts[0];
        CharacterResourceRecord? provider = inventory.Resources.SingleOrDefault(resource =>
            resource.Id == materialRecord.Material!.ProviderResourceId);
        if (provider is null || !HasCurrentCustody(provider, package) ||
            provider.ContentSha256 != materialRecord.Material!.ProviderSha256)
            throw new InvalidDataException("The retained material provider is missing or stale.");
        if (model.Surfaces.IsDefaultOrEmpty || model.Surfaces.Select(surface => surface.Id)
                .Distinct(StringComparer.Ordinal).Count() != model.Surfaces.Length)
            throw new InvalidDataException("The current surface inventory is missing or ambiguous.");
        HashSet<string> originalSurfaceIds = original.Surfaces.Select(surface => surface.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (originalSurfaceIds.Count != original.Surfaces.Length || originalSurfaceIds.Any(id =>
                !model.Surfaces.Any(surface => surface.Id == id)))
            throw new InvalidDataException("An original surface is missing or ambiguous.");
        var affected = model.Surfaces.Where(surface => surface.MaterialId == appendedMaterialId).ToArray();
        if (affected.Length == 0 || affected.Any(surface => originalSurfaceIds.Contains(surface.Id) ||
                !surface.Id.StartsWith("attachment:", StringComparison.Ordinal)))
            throw new InvalidDataException("The appended material must be used only by accessory surfaces.");
        var surfaces = model.Surfaces.Select(surface => surface.MaterialId == appendedMaterialId
            ? surface with { MaterialId = retainedMaterialId } : surface).ToImmutableArray();
        if (surfaces.Any(surface => surface.MaterialId == appendedMaterialId))
            throw new InvalidDataException("The appended material is still in use.");
        var survivingMaterials = materials.RemoveAt(appendedIndex);
        var survivingTexturePaths = survivingMaterials.SelectMany(material => material.Textures)
            .Where(texture => texture.PackageEntryPath is not null).Select(texture => texture.PackageEntryPath!)
            .ToHashSet(StringComparer.Ordinal);
        var orphanPaths = package.TexturePayloads.Keys.Where(path => !survivingTexturePaths.Contains(path)).ToArray();
        var texturePayloads = package.TexturePayloads;
        var companionPayloads = package.CompanionPayloads;
        var records = inventory.Resources;
        var archivedMaterial = JsonSerializer.SerializeToUtf8Bytes(materials[appendedIndex], options).ToImmutableArray();
        Archive("original:accessory-material:" + appendedMaterialId.ToString("N"), CharacterSubsystem.Materials,
            "accessory-material", archivedMaterial);
        foreach (string path in orphanPaths)
        {
            ImmutableArray<byte> bytes = texturePayloads[path];
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
            Archive("original:accessory-texture:" + hash, CharacterSubsystem.Textures, "accessory-texture", bytes);
            texturePayloads = texturePayloads.Remove(path);
        }
        void Archive(string id, CharacterSubsystem subsystem, string kind, ImmutableArray<byte> bytes)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
            string entry = "character/resources/" + kind + "-" + hash + ".bin";
            int prior = Array.FindIndex(records.ToArray(), resource => resource.Id == id);
            if (prior >= 0)
            {
                var archive = records[prior];
                if (!archive.IsOriginalArchive || archive.Required || archive.Subsystem != subsystem ||
                    archive.ContentSha256 != hash || archive.EntryPath is null ||
                    !companionPayloads.TryGetValue(archive.EntryPath, out var payload) ||
                    !payload.AsSpan().SequenceEqual(bytes.AsSpan()))
                    throw new InvalidDataException("An accessory archive identity conflicts with retained data.");
                records = records.SetItem(prior, archive with
                {
                    ReferencedBy = archive.ReferencedBy.AddRange(affected.Select(surface => surface.Id))
                        .Distinct(StringComparer.Ordinal).ToImmutableArray(),
                });
                return;
            }
            if (companionPayloads.TryGetValue(entry, out var existing) && !existing.AsSpan().SequenceEqual(bytes.AsSpan()))
                throw new InvalidDataException("An accessory archive payload conflicts with retained data.");
            companionPayloads = companionPayloads.SetItem(entry, bytes);
            records = records.Add(new()
            {
                Id = id, LogicalName = kind + "/" + hash + ".bin", EntryPath = entry,
                ProviderIdentity = "authored-accessory", SourceFingerprint = hash, ContentSha256 = hash,
                ByteLength = bytes.Length, Subsystem = subsystem, Status = CharacterDependencyStatus.Preserved,
                Required = false, IsOriginalArchive = true,
                ReferencedBy = affected.Select(surface => surface.Id).ToImmutableArray(),
                Detail = "Accessory data before material assignment.",
            });
        }
        var revised = model with
        {
            Surfaces = surfaces,
            Package = package with
            {
                Document = package.Document with
                {
                    Materials = survivingMaterials,
                    CharacterResources = inventory with { Resources = records },
                    LastBuildReceipt = null,
                },
                TexturePayloads = texturePayloads,
                CompanionPayloads = companionPayloads,
            },
        };
        FbxModelAuthoringImportResult result = ModelGeometryRevisionCodec.Capture(revised);
        _ = CustomModelPackageSerializer.Serialize(result.Package);
        return result;
    }

    private static int FindUniqueIndex(ImmutableArray<CustomModelMaterial> materials, Guid id)
    {
        int found = -1;
        for (int index = 0; index < materials.Length; index++)
        {
            if (materials[index].Id != id) continue;
            if (found >= 0) throw new InvalidDataException("Material identifiers are ambiguous.");
            found = index;
        }
        return found >= 0 ? found : throw new InvalidDataException("The requested material is missing.");
    }

    private static bool HasCurrentCustody(CharacterResourceRecord resource, CustomModelPackage package) =>
        resource.Subsystem == CharacterSubsystem.Materials &&
        resource.Status is CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded &&
        resource.EntryPath is not null && resource.ContentSha256 is not null &&
        package.CompanionPayloads.ContainsKey(resource.EntryPath);
}
