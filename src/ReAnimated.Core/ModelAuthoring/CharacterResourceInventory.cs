using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public enum CharacterSubsystem { Geometry, Rig, Skinning, Materials, Textures, Lods, Morphs, Variants, FacialDefinitions, Ragdoll, Cloth, Damage, Helpers, DetachedParts }
public enum CharacterDependencyStatus { Preserved, Decoded, Missing, Ambiguous, Unsupported, NotApplicable }

/// <summary>Portable provenance for one original character resource. Payloads belong to local packages, never repository fixtures.</summary>
public sealed record CharacterResourceRecord
{
    public string Id { get; init; } = string.Empty;
    public string LogicalName { get; init; } = string.Empty;
    public string ProviderIdentity { get; init; } = string.Empty;
    public string SourceFingerprint { get; init; } = string.Empty;
    public string? ContentSha256 { get; init; }
    public string? EntryPath { get; init; }
    public CharacterPackedEffectReceipt? PackedEffect { get; init; }
    public CharacterMaterialReceipt? Material { get; init; }
    public CharacterNativeResourceReceipt? NativeResource { get; init; }
    public long ByteLength { get; init; }
    public CharacterSubsystem Subsystem { get; init; }
    public CharacterDependencyStatus Status { get; init; }
    public bool Required { get; init; } = true;
    public bool IsOriginalArchive { get; init; }
    public string Detail { get; init; } = string.Empty;
    public ImmutableArray<string> ReferencedBy { get; init; } = [];
    public CharacterSupplementalSourceReceipt? SupplementalSource { get; init; }
}

public sealed record CharacterSkinMaterialOverride(int TargetMaterialSlotIndex,int ReplacementDatabaseEntryIndex);
public sealed record CharacterSkinEntityOverride(int SourceEntityIndex,ushort RawValue);
public sealed record CharacterSkinSurfaceOverride(byte OriginalSurfaceId,byte ReplacementSurfaceId,ushort Flags);
public sealed record CharacterSkinVariant(string Name,ushort RawFeatures,ImmutableArray<CharacterSkinMaterialOverride> MaterialOverrides,
    ImmutableArray<CharacterSkinEntityOverride> EntityOverrides,int SurfaceOverrideCount,int RandomizedChildCount)
{
    public ImmutableArray<byte> TagBytes {get;init;}=[];
    public ImmutableArray<byte> ColorBytes {get;init;}=[];
    public string? MorphsPreset {get;init;}
    public string? Character0 {get;init;}
    public string? Character1 {get;init;}
    public ImmutableArray<CharacterSkinSurfaceOverride> SurfaceOverrides {get;init;}=[];
}
public sealed record CharacterOriginalMaterial(int Index,string Name,uint RawLoadValue);
public sealed record CharacterOriginalEntity(int Index,string Name);

public sealed record CharacterOriginalBuffer(int ItemIndex,int SlotIndex,short StorageGroupId,int LogicalLength,string Role);

public sealed record CharacterSubsystemReview(CharacterSubsystem Subsystem, CharacterDependencyStatus Status, string Detail);
public sealed record CharacterMorphBinding(int SourceChannelIndex, int TargetChannelSlot, string Name, uint DescriptorHash,
    string SurfaceId, int EntityIndex, int LodIndex, int VertexCount, string PayloadStatus);

public sealed record CharacterResourceInventory
{
    private static readonly string[] RequiredPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"];
    public const string DecodedEntryPath = "character/decoded.json";
    public string DecodedSha256 { get; init; } = string.Empty;
    public long DecodedByteLength { get; init; }
    public string RootResourceId { get; init; } = string.Empty;
    public ImmutableArray<CharacterResourceRecord> Resources { get; init; } = [];
    public ImmutableArray<CharacterActorSourceReceipt> ActorSourceReviews { get; init; } = [];
    public ImmutableArray<CharacterBodyRegionAssemblyReview> BodyRegionReviews { get; init; } = [];
    public ImmutableArray<CharacterMaterialFallbackReceipt> MaterialFallbackReviews { get; init; } = [];
    public ImmutableArray<CharacterTextureFallbackReceipt> TextureFallbackReviews { get; init; } = [];
    public ImmutableArray<CharacterFacialAssociationReceipt> FacialAssociationReviews { get; init; } = [];
    public ImmutableArray<CharacterSubsystemReview> Subsystems { get; init; } = [];
    public ImmutableArray<CharacterMorphBinding> MorphBindings { get; init; } = [];
    public ImmutableArray<string> VariantNames { get; init; } = [];
    public ImmutableArray<CharacterSkinVariant> SkinVariants { get; init; } = [];
    public ImmutableArray<CharacterOriginalBuffer> OriginalBuffers { get; init; } = [];
    public int OriginalMaterialSlotCount {get;init;}
    public ImmutableArray<CharacterOriginalMaterial> OriginalMaterials { get; init; } = [];
    public ImmutableArray<CharacterOriginalEntity> OriginalEntities { get; init; } = [];
    public string? OriginalAppliedSkinName { get; init; }
    // Evidence must be tied to the emitted and actually loaded resource hashes.
    // A complete dependency inventory alone never confers Player acceptance.
    public string? CompiledSemanticSha256 { get; init; }
    public string? LoadedResourceSha256 { get; init; }
    public ImmutableArray<string> VerifiedPlayerScenarios { get; init; } = [];

    public ImmutableArray<string> ExportBlockers => Resources.Where(r => r.Required &&
        (r.Status is CharacterDependencyStatus.Missing or CharacterDependencyStatus.Ambiguous or CharacterDependencyStatus.Unsupported) && !HasApplicableMaterialFallback(r) && !HasApplicableTextureFallback(r))
        .Select(r => $"{r.Subsystem}: {r.LogicalName}: {r.Detail}")
        .Concat(Resources.Where(resource=>resource.Required && !resource.IsOriginalArchive && resource.Material is not null)
            .SelectMany(resource=>resource.Material!.Textures.Where(texture=>!HasApplicableTextureFallback(resource, texture) && (texture.ResourceId is null ||
                !Resources.Any(candidate=>candidate.Id==texture.ResourceId && candidate.EntryPath is not null && candidate.NativeResource is {ResourceType:8480})))
                .Select(texture=>$"Material '{resource.LogicalName}' has unresolved texture 0x{texture.TextureNameHash:X8}.")))
        .Concat(Enum.GetValues<CharacterSubsystem>().Where(s => !Subsystems.Any(r => r.Subsystem == s))
            .Select(s => $"{s}: dependency discovery has not been completed."))
        .Concat(Subsystems.Where(s => (s.Status is CharacterDependencyStatus.Missing or CharacterDependencyStatus.Ambiguous or CharacterDependencyStatus.Unsupported) &&
            !(s.Subsystem == CharacterSubsystem.FacialDefinitions && s.Status == CharacterDependencyStatus.Ambiguous &&
              !FacialAssociationReviews.IsDefault && FacialAssociationReviews.Any(review => review.MatchesCurrentInventory(this))))
            .Select(s => $"{s.Subsystem}: {s.Detail}"))
        .Concat(Resources.Where(r => r.Required && !r.IsOriginalArchive && r.EntryPath is not null && r.Id != RootResourceId)
            .Select(r => (Resource: r, Extension: System.IO.Path.GetExtension(r.LogicalName).ToLowerInvariant()))
            .Where(item => (item.Extension is ".msh" or ".msh_obj" or ".skn") && item.Resource.NativeResource is not {ResourceType:272} ||
                item.Extension==".fx" && item.Resource.PackedEffect is null)
            .Select(item => item.Extension == ".fx"
                ? $"Effect '{item.Resource.LogicalName}' requires a gathered runtime resource."
                : $"Detached asset '{item.Resource.LogicalName}' requires a verified runtime export."))
        .Distinct(StringComparer.Ordinal)
        .ToImmutableArray();

    public bool HasApplicableMaterialFallback(CharacterResourceRecord resource) =>
        resource.Status == CharacterDependencyStatus.Missing && resource.Subsystem == CharacterSubsystem.Materials &&
        resource.EntryPath is null && !resource.ReferencedBy.IsDefaultOrEmpty &&
        !MaterialFallbackReviews.IsDefault &&
        resource.ReferencedBy.All(consumer => MaterialFallbackReviews.Any(receipt =>
            receipt.MissingResourceId == resource.Id && receipt.ConsumerResourceId == consumer &&
            receipt.MatchesCurrentInventory(this) && receipt.MatchingConsumerEntries.All(index =>
                MaterialFallbackReviews.Any(other => other.MissingResourceId == resource.Id &&
                    other.ConsumerResourceId == consumer && other.MaterialIndex == index && other.MatchesCurrentInventory(this)))));

    public bool HasApplicableTextureFallback(CharacterResourceRecord material, CharacterMaterialTextureReference texture) =>
        !TextureFallbackReviews.IsDefault && TextureFallbackReviews.Any(receipt => receipt.MaterialResourceId == material.Id && receipt.TextureIndex == texture.Index &&
            receipt.MatchesCurrentInventory(this));

    public bool HasApplicableTextureFallback(CharacterResourceRecord missing) =>
        missing.Status == CharacterDependencyStatus.Missing && missing.Subsystem == CharacterSubsystem.Textures &&
        missing.EntryPath is null && !TextureFallbackReviews.IsDefault && !missing.ReferencedBy.IsDefaultOrEmpty && missing.ReferencedBy.All(consumer =>
            Resources.SingleOrDefault(r => r.Id == consumer) is { Material: { } material } row &&
            material.Textures.Where(texture => texture.ResourceId is null).Any() &&
            material.Textures.Where(texture => texture.ResourceId is null).All(texture =>
                TextureFallbackReviews.Any(receipt => receipt.MissingResourceId == missing.Id &&
                    receipt.MaterialResourceId == row.Id && receipt.TextureIndex == texture.Index && receipt.MatchesCurrentInventory(this))));
    public bool IsDependencyComplete => ExportBlockers.IsEmpty;
    public bool IsGameReady => IsDependencyComplete && CompiledSemanticSha256 is not null &&
        string.Equals(CompiledSemanticSha256, LoadedResourceSha256, StringComparison.OrdinalIgnoreCase) &&
        RequiredPlayerScenarios.All(s => VerifiedPlayerScenarios.Contains(s, StringComparer.Ordinal));

    public void Validate()
    {
        ProjectAssetReference.ValidateSha256(DecodedSha256, nameof(DecodedSha256));
        if (DecodedByteLength <= 0 || DecodedByteLength > 768L * 1024 * 1024 || Resources.IsDefault || Subsystems.IsDefault || MorphBindings.IsDefault || VariantNames.IsDefault || SkinVariants.IsDefault || OriginalBuffers.IsDefault || OriginalMaterials.IsDefault || OriginalEntities.IsDefault)
            throw new ArgumentException("The character inventory is missing or exceeds its bounds.");
        if (Resources.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != Resources.Length ||
            Subsystems.Select(s => s.Subsystem).Distinct().Count() != Subsystems.Length)
            throw new ArgumentException("Character resource and subsystem identities must be unique.");
        foreach (var r in Resources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(r.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(r.LogicalName);
            r.SupplementalSource?.Validate(r);
            if (!Enum.IsDefined(r.Subsystem) || !Enum.IsDefined(r.Status) || r.ReferencedBy.IsDefault || r.ByteLength < 0)
                throw new ArgumentException("Invalid companion-resource record.");
            if (r.EntryPath is { } path)
            {
                CustomModelSourceIdentity.ValidatePackageEntryPath(path, nameof(Resources));
                if (!path.StartsWith("character/resources/", StringComparison.Ordinal)) throw new ArgumentException("Companion resources must use the reserved character resource directory.");
                ProjectAssetReference.ValidateSha256(r.ContentSha256!, nameof(Resources));
            }
            else if (r.Status is CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded)
                throw new ArgumentException("A preserved resource requires a hashed payload.");
        }
        if (!Resources.Any(r => r.Id == RootResourceId && r.EntryPath is not null)) throw new ArgumentException("The character root resource is missing.");
        if (Resources.Where(r => r.EntryPath is not null).GroupBy(r => r.EntryPath, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Companion entry paths must be unique.");
        if (ActorSourceReviews.IsDefault || ActorSourceReviews.Length > 256 ||
            ActorSourceReviews.Select(r => (r.ActorResourceId, r.ModelDeclarationCallIndex)).Distinct().Count() != ActorSourceReviews.Length)
            throw new ArgumentException("Actor source reviews must be bounded and unique.");
        foreach (var review in ActorSourceReviews) review.Validate(Resources);
        if (BodyRegionReviews.IsDefault || BodyRegionReviews.Length > 512 || BodyRegionReviews.Select(review => (review.BodyResourceId, review.BodyElementCallIndex)).Distinct().Count() != BodyRegionReviews.Length)
            throw new ArgumentException("Body-region reviews must be bounded and unique.");
        foreach (var review in BodyRegionReviews) review.Validate();
        if (MaterialFallbackReviews.IsDefault || MaterialFallbackReviews.Length > 4096 ||
            MaterialFallbackReviews.Select(receipt => (receipt.MissingResourceId, receipt.ConsumerResourceId, receipt.MaterialIndex)).Distinct().Count() != MaterialFallbackReviews.Length)
            throw new ArgumentException("Material fallback reviews must be bounded and unique.");
        foreach (var receipt in MaterialFallbackReviews) receipt.Validate(this);
        if (TextureFallbackReviews.IsDefault || TextureFallbackReviews.Length > 4096 ||
            TextureFallbackReviews.Select(r => (r.MissingResourceId, r.MaterialResourceId, r.TextureIndex)).Distinct().Count() != TextureFallbackReviews.Length)
            throw new ArgumentException("Texture fallback reviews must be bounded and unique.");
        foreach (var receipt in TextureFallbackReviews) receipt.Validate(this);
        if (FacialAssociationReviews.IsDefault || FacialAssociationReviews.Length > 256 ||
            FacialAssociationReviews.Select(r => (r.ActorResourceId, r.ModelDeclarationCallIndex, r.ActorScopeCallIndex)).Distinct().Count() != FacialAssociationReviews.Length)
            throw new ArgumentException("Facial association reviews exceed their bounds.");
        foreach (var receipt in FacialAssociationReviews) receipt.Validate(this);
        foreach(var resource in Resources)
        {
            resource.PackedEffect?.Validate(resource,Resources);
            resource.Material?.Validate(resource,Resources);
            resource.NativeResource?.Validate(resource);
        }
        foreach (var b in MorphBindings)
            if (b.SourceChannelIndex < 0 || b.TargetChannelSlot < -1 || b.VertexCount < 0 || b.LodIndex < 0 || string.IsNullOrEmpty(b.Name))
                throw new ArgumentException("Invalid original morph binding.");
        if (OriginalMaterials.Any(m=>m.Index<0 || string.IsNullOrWhiteSpace(m.Name)) || OriginalMaterials.Select(m=>m.Index).Distinct().Count()!=OriginalMaterials.Length ||
            OriginalMaterialSlotCount<0 || OriginalMaterialSlotCount>OriginalMaterials.Length ||
            OriginalEntities.Any(e=>e.Index<0 || string.IsNullOrWhiteSpace(e.Name)) || OriginalEntities.Select(e=>e.Index).Distinct().Count()!=OriginalEntities.Length ||
            SkinVariants.Select(v=>v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=SkinVariants.Length)
            throw new ArgumentException("Original skin identity maps are invalid or ambiguous.");
        foreach(var variant in SkinVariants)
            if(string.IsNullOrWhiteSpace(variant.Name) || variant.MaterialOverrides.IsDefault || variant.EntityOverrides.IsDefault || variant.TagBytes.IsDefault || variant.ColorBytes.IsDefault || variant.SurfaceOverrides.IsDefault ||
                variant.TagBytes.Length is not (0 or 8) || variant.ColorBytes.Length is not (0 or 8) || !variant.SurfaceOverrides.IsEmpty && variant.SurfaceOverrides.Length!=variant.SurfaceOverrideCount || variant.SurfaceOverrideCount<0 || variant.RandomizedChildCount<0 ||
                variant.MaterialOverrides.Any(m=>m.TargetMaterialSlotIndex<0 || m.ReplacementDatabaseEntryIndex<0) || variant.EntityOverrides.Any(e=>e.SourceEntityIndex<0 || (e.RawValue&0x3fff)!=e.SourceEntityIndex))
                throw new ArgumentException("Invalid original skin variant.");
        if (CompiledSemanticSha256 is { } compiled) ProjectAssetReference.ValidateSha256(compiled, nameof(CompiledSemanticSha256));
        if (LoadedResourceSha256 is { } loaded) ProjectAssetReference.ValidateSha256(loaded, nameof(LoadedResourceSha256));
    }
}




