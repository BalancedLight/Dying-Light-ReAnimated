using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record ReviewedCharacterMaterialFallback(
    string MissingResourceId, string ConsumerResourceId, int MaterialIndex,
    uint ExpectedOriginalLoadValue, string ExpectedConsumerSha256,
    string DefaultMaterialResourceId, string ProfileModuleSha256,
    string ContractAssessmentSha256, bool Reviewed)
{
    public string Profile { get; init; } = CharacterMaterialFallbackReceipt.OrdinaryProfile;
    public string Contract { get; init; } = CharacterMaterialFallbackReceipt.ConsumerContract;
}

public static class CharacterMaterialFallbackAuthoring
{
    public static CustomModelPackage Review(CustomModelPackage package, ReviewedCharacterMaterialFallback proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.Reviewed) throw new InvalidOperationException("Review the ordinary material loader contract before recording fallback.");
        _ = CustomModelPackageSerializer.Serialize(package);
        var inventory = package.Document.CharacterResources ?? throw new InvalidDataException("Character material inventory is missing.");
        var missing = inventory.Resources.SingleOrDefault(resource => resource.Id == proposal.MissingResourceId)
            ?? throw new InvalidDataException("The missing material row was not found.");
        var consumer = inventory.Resources.SingleOrDefault(resource => resource.Id == proposal.ConsumerResourceId)
            ?? throw new InvalidDataException("The original material consumer was not found.");
        var fallback = inventory.Resources.SingleOrDefault(resource => resource.Id == proposal.DefaultMaterialResourceId)
            ?? throw new InvalidDataException("The retained default material was not found.");
        if (consumer.ContentSha256 != proposal.ExpectedConsumerSha256)
            throw new InvalidDataException("The original material consumer source changed.");
        var entries = ReadBaseEntries(package, consumer);
        var entry = entries.SingleOrDefault(value => value.Index == proposal.MaterialIndex)
            ?? throw new InvalidDataException("The selected base material entry was not found.");
        if (entry.Name != missing.LogicalName || entry.RawLoadValue != proposal.ExpectedOriginalLoadValue)
            throw new InvalidDataException("The original requested material name or load flags changed.");
        var material = fallback.Material ?? throw new InvalidDataException("The default material has no exact provider receipt.");
        var receipt = new CharacterMaterialFallbackReceipt
        {
            MissingResourceId = missing.Id, RequestedName = missing.LogicalName,
            ConsumerResourceId = consumer.Id, ConsumerSourceSha256 = consumer.ContentSha256!,
            Consumer = consumer.Id == inventory.RootResourceId ? CharacterMaterialFallbackConsumer.RootBaseSlot : CharacterMaterialFallbackConsumer.DetachedBaseSlot,
            MaterialIndex = entry.Index, OriginalLoadValue = entry.RawLoadValue,
            MatchingConsumerEntries = entries.Where(value => value.Name == missing.LogicalName).Select(value => value.Index).Order().ToImmutableArray(),
            DefaultMaterialResourceId = fallback.Id, DefaultMaterialSha256 = fallback.ContentSha256!,
            ProviderResourceId = material.ProviderResourceId, ProviderSha256 = material.ProviderSha256,
            Profile = proposal.Profile, Contract = proposal.Contract,
            ProfileModuleSha256 = proposal.ProfileModuleSha256,
            ContractAssessmentSha256 = proposal.ContractAssessmentSha256, Reviewed = true,
        };
        receipt.Validate(inventory);
        var updated = package with { Document = package.Document with
        {
            CharacterResources = inventory with
            {
                MaterialFallbackReviews = inventory.MaterialFallbackReviews.Where(previous =>
                    previous.MissingResourceId != missing.Id || previous.ConsumerResourceId != consumer.Id ||
                    previous.MaterialIndex != entry.Index).Append(receipt).ToImmutableArray(),
                CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [],
            },
            LastBuildReceipt = null,
        } };
        Revalidate(updated);
        return updated;
    }

    public static void Revalidate(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        _ = CustomModelPackageSerializer.Serialize(package);
        if (package.Document.CharacterResources is not { } inventory) return;
        foreach (var receipt in inventory.MaterialFallbackReviews)
        {
            receipt.Validate(inventory);
            var consumer = inventory.Resources.Single(resource => resource.Id == receipt.ConsumerResourceId);
            var entries = ReadBaseEntries(package, consumer);
            var entry = entries.SingleOrDefault(value => value.Index == receipt.MaterialIndex);
            if (entry is null || entry.Name != receipt.RequestedName || entry.RawLoadValue != receipt.OriginalLoadValue ||
                !entries.Where(value => value.Name == receipt.RequestedName).Select(value => value.Index).Order()
                    .SequenceEqual(receipt.MatchingConsumerEntries.Order()))
                throw new InvalidDataException("The reviewed base material consumer changed.");
            var fallback = inventory.Resources.Single(resource => resource.Id == receipt.DefaultMaterialResourceId);
            _ = Bytes(package, fallback);
            var provider = inventory.Resources.Single(resource => resource.Id == receipt.ProviderResourceId);
            _ = Bytes(package, provider);
        }
    }

    private static ImmutableArray<CharacterOriginalMaterial> ReadBaseEntries(CustomModelPackage package, CharacterResourceRecord consumer)
    {
        var inventory = package.Document.CharacterResources!;
        var bytes = Bytes(package, consumer);
        if (consumer.Id != inventory.RootResourceId && consumer.Subsystem != CharacterSubsystem.DetachedParts)
            throw new InvalidDataException("Only original root or detached mesh base slots support this contract.");
        if (consumer.NativeResource is not { ResourceType: 272 } native)
            throw new InvalidDataException("Only original root or detached mesh base slots support this contract.");
        native.Validate(consumer);
        ReadOnlyMemory<byte> Item(int index)
        {
            var item = native.Items.ElementAtOrDefault(index)
                ?? throw new InvalidDataException("The detached mesh material source items are incomplete.");
            var payload = bytes.AsMemory().Slice((int)item.PayloadOffset, item.ByteLength);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(payload.Span)), item.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The detached mesh material source item changed.");
            return payload;
        }
        var decoded = CompiledMeshGeometryDecoder.Decode(Item(0), Item(1), Item(3), Item(4),
            retailResourceName: native.ResourceName);
        if (decoded.Diagnostics.Any(diagnostic => diagnostic.Severity == CompactMeshDiagnosticSeverity.Error))
            throw new InvalidDataException("The detached mesh material source cannot be decoded.");
        if (consumer.Id == inventory.RootResourceId &&
            (decoded.MaterialDatabase.DeclaredSlotCount != inventory.OriginalMaterialSlotCount ||
             !decoded.MaterialDatabase.Entries.Select(entry => new CharacterOriginalMaterial(entry.Index, entry.DatabaseName, entry.RawLoadValue))
                .SequenceEqual(inventory.OriginalMaterials)))
            throw new InvalidDataException("The root material metadata differs from original native source entries.");
        return decoded.MaterialDatabase.Entries.Where(entry => entry.Index < decoded.MaterialDatabase.DeclaredSlotCount)
            .Select(entry => new CharacterOriginalMaterial(entry.Index, entry.DatabaseName, entry.RawLoadValue)).ToImmutableArray();
    }

    private static ImmutableArray<byte> Bytes(CustomModelPackage package, CharacterResourceRecord resource)
    {
        if (resource.EntryPath is null || !package.CompanionPayloads.TryGetValue(resource.EntryPath, out var bytes) ||
            bytes.IsDefault || bytes.Length != resource.ByteLength ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())), resource.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Material fallback source bytes changed.");
        return bytes;
    }
}


