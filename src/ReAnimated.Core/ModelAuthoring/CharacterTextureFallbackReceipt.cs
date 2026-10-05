using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterTextureFallbackReceipt
{
    public const string BuiltInProfile = "dl1-player-default-texture-v1";
    public const string LoaderContract = "CTextureManager.LoadTexture/DefaultTexture";
    public string MissingResourceId { get; init; } = string.Empty;
    public string MaterialResourceId { get; init; } = string.Empty;
    public string MaterialSha256 { get; init; } = string.Empty;
    public string ProviderResourceId { get; init; } = string.Empty;
    public string ProviderSha256 { get; init; } = string.Empty;
    public int TextureIndex { get; init; }
    public uint TextureNameHash { get; init; }
    public uint SamplerState { get; init; }
    public uint LoadFlags { get; init; }
    public CharacterTextureNameReceipt RequestedName { get; init; } = new();
    public string Profile { get; init; } = BuiltInProfile;
    public string Contract { get; init; } = LoaderContract;
    public string ProfileModuleSha256 { get; init; } = string.Empty;
    public string ContractAssessmentSha256 { get; init; } = string.Empty;
    public int DefaultWidth { get; init; } = 16;
    public int DefaultHeight { get; init; } = 16;
    public uint DefaultPixel { get; init; } = 0xFF7F7F7F;
    public bool Reviewed { get; init; }

    public void Validate(CharacterResourceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ProjectAssetReference.ValidateSha256(MaterialSha256, nameof(MaterialSha256));
        ProjectAssetReference.ValidateSha256(ProviderSha256, nameof(ProviderSha256));
        ProjectAssetReference.ValidateSha256(ProfileModuleSha256, nameof(ProfileModuleSha256));
        ProjectAssetReference.ValidateSha256(ContractAssessmentSha256, nameof(ContractAssessmentSha256));
        if (!Reviewed || Profile != BuiltInProfile || Contract != LoaderContract || TextureIndex < 0 ||
            DefaultWidth != 16 || DefaultHeight != 16 || DefaultPixel != 0xFF7F7F7F)
            throw new ArgumentException("The reviewed default texture contract is invalid.");
        var missing = inventory.Resources.SingleOrDefault(r => r.Id == MissingResourceId);
        var material = inventory.Resources.SingleOrDefault(r => r.Id == MaterialResourceId);
        var provider = inventory.Resources.SingleOrDefault(r => r.Id == ProviderResourceId);
        var texture = material?.Material?.Textures.SingleOrDefault(t => t.Index == TextureIndex);
        if (missing is null || missing.Status != CharacterDependencyStatus.Missing || missing.EntryPath is not null ||
            missing.Subsystem != CharacterSubsystem.Textures || !missing.ReferencedBy.Contains(MaterialResourceId, StringComparer.Ordinal) ||
            material is null || material.EntryPath is null || material.ContentSha256 != MaterialSha256 ||
            material.Material is not { } receipt || receipt.ProviderResourceId != ProviderResourceId || receipt.ProviderSha256 != ProviderSha256 ||
            provider is null || provider.EntryPath is null || provider.ContentSha256 != ProviderSha256 ||
            texture is null || texture.ResourceId is not null || texture.TextureNameHash != TextureNameHash ||
            texture.SamplerState != SamplerState || texture.LoadFlags != LoadFlags || texture.NameSource != RequestedName)
            throw new ArgumentException("The texture fallback consumer or provider is missing, ambiguous or stale.");
        RequestedName.Validate(TextureNameHash, provider);
    }

    public bool MatchesCurrentInventory(CharacterResourceInventory inventory)
    {
        try { Validate(inventory); return true; }
        catch (ArgumentException) { return false; }
    }
}
