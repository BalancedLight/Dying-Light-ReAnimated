using ReAnimated.Core.Project;
using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

public enum CharacterMaterialFallbackConsumer { RootBaseSlot, DetachedBaseSlot }

public sealed record CharacterMaterialFallbackReceipt
{
    public const string OrdinaryProfile = "dl1-player-ordinary-material-manager-v1";
    public const string ConsumerContract = "MeshFile.MaterialsDatabase.Entry.Load/BaseSlotFill";
    public string MissingResourceId { get; init; } = string.Empty;
    public string RequestedName { get; init; } = string.Empty;
    public string ConsumerResourceId { get; init; } = string.Empty;
    public string ConsumerSourceSha256 { get; init; } = string.Empty;
    public CharacterMaterialFallbackConsumer Consumer { get; init; }
    public int MaterialIndex { get; init; }
    public uint OriginalLoadValue { get; init; }
    public ImmutableArray<int> MatchingConsumerEntries { get; init; } = [];
    public string DefaultMaterialResourceId { get; init; } = string.Empty;
    public string DefaultMaterialSha256 { get; init; } = string.Empty;
    public string ProviderResourceId { get; init; } = string.Empty;
    public string ProviderSha256 { get; init; } = string.Empty;
    public string Profile { get; init; } = OrdinaryProfile;
    public string Contract { get; init; } = ConsumerContract;
    public string ProfileModuleSha256 { get; init; } = string.Empty;
    public string ContractAssessmentSha256 { get; init; } = string.Empty;
    public uint DefaultLoadValue { get; init; }
    public bool Reviewed { get; init; }

    public void Validate(CharacterResourceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ProjectAssetReference.ValidateSha256(ConsumerSourceSha256, nameof(ConsumerSourceSha256));
        ProjectAssetReference.ValidateSha256(DefaultMaterialSha256, nameof(DefaultMaterialSha256));
        ProjectAssetReference.ValidateSha256(ProviderSha256, nameof(ProviderSha256));
        ProjectAssetReference.ValidateSha256(ProfileModuleSha256, nameof(ProfileModuleSha256));
        ProjectAssetReference.ValidateSha256(ContractAssessmentSha256, nameof(ContractAssessmentSha256));
        if (!Reviewed || Profile != OrdinaryProfile || Contract != ConsumerContract ||

            DefaultLoadValue != 0 || !Enum.IsDefined(Consumer) || MaterialIndex < 0 || MatchingConsumerEntries.IsDefaultOrEmpty ||
            MatchingConsumerEntries.Length > 65535 || MatchingConsumerEntries.Any(index => index < 0) ||
            MatchingConsumerEntries.Distinct().Count() != MatchingConsumerEntries.Length || !MatchingConsumerEntries.Contains(MaterialIndex))
            throw new ArgumentException("The reviewed ordinary material fallback contract is invalid.");
        var missing = inventory.Resources.SingleOrDefault(resource => resource.Id == MissingResourceId);
        var consumer = inventory.Resources.SingleOrDefault(resource => resource.Id == ConsumerResourceId);
        var fallback = inventory.Resources.SingleOrDefault(resource => resource.Id == DefaultMaterialResourceId);
        var provider = inventory.Resources.SingleOrDefault(resource => resource.Id == ProviderResourceId);
        if (missing is null || missing.Status != CharacterDependencyStatus.Missing || missing.EntryPath is not null ||
            missing.Subsystem != CharacterSubsystem.Materials || missing.LogicalName != RequestedName ||
            !missing.ReferencedBy.Contains(ConsumerResourceId, StringComparer.Ordinal) ||
            consumer is null || consumer.IsOriginalArchive || consumer.EntryPath is null ||
            consumer.ContentSha256 != ConsumerSourceSha256 ||
            fallback is null || !fallback.Required || fallback.IsOriginalArchive || fallback.EntryPath is null ||
            fallback.Status is not (CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded) ||
            fallback.ContentSha256 != DefaultMaterialSha256 || fallback.Material is not { } material ||
            !material.MaterialName.Equals("default.mat", StringComparison.Ordinal) ||
            material.ProviderResourceId != ProviderResourceId || material.ProviderSha256 != ProviderSha256 ||
            provider is null || provider.EntryPath is null || provider.ContentSha256 != ProviderSha256 ||
            inventory.Resources.Count(resource => !resource.IsOriginalArchive && resource.Material is not null &&
                resource.Material.MaterialName.Equals("default.mat", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new ArgumentException("The material fallback consumer, default or provider is missing, ambiguous or stale.");
        if (Consumer == CharacterMaterialFallbackConsumer.RootBaseSlot)
        {
            var slot = inventory.OriginalMaterials.SingleOrDefault(entry => entry.Index == MaterialIndex);
            if (ConsumerResourceId != inventory.RootResourceId || MaterialIndex >= inventory.OriginalMaterialSlotCount ||
                slot is null || slot.Name != RequestedName || slot.RawLoadValue != OriginalLoadValue ||
                !inventory.OriginalMaterials.Where(entry => entry.Index < inventory.OriginalMaterialSlotCount && entry.Name == RequestedName)
                    .Select(entry => entry.Index).Order().SequenceEqual(MatchingConsumerEntries.Order()))
                throw new ArgumentException("The original base material entry changed.");
        }
        else if (ConsumerResourceId == inventory.RootResourceId || consumer.Subsystem != CharacterSubsystem.DetachedParts ||
                 consumer.NativeResource is not { ResourceType: 272 })
            throw new ArgumentException("The detached material consumer has no exact native mesh source.");
    }

    public bool MatchesCurrentInventory(CharacterResourceInventory inventory)
    {
        try { Validate(inventory); return true; }
        catch (ArgumentException) { return false; }
    }
}