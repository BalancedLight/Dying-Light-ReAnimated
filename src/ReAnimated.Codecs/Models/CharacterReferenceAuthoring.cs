using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Adds a stock character's original companion evidence to a separate neutral
/// model. It preserves the target geometry and source; native behavior remains
/// unverified until each transferred family is reviewed for that target.
/// </summary>
public static class CharacterReferenceAuthoring
{
    private static readonly CharacterSubsystem[] TargetDecodedSubsystems =
        [CharacterSubsystem.Geometry, CharacterSubsystem.Rig, CharacterSubsystem.Skinning, CharacterSubsystem.Lods];

    public static FbxModelAuthoringImportResult AdoptReference(
        FbxModelAuthoringImportResult target,
        FbxModelAuthoringImportResult reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        CustomModelPackage targetPackage = target.Package;
        CustomModelPackage stockPackage = reference.Package;
        if (targetPackage.Document.CharacterResources is not null ||
            !targetPackage.DecodedCharacterPayload.IsEmpty ||
            targetPackage.CompanionPayloads.Count != 0)
            throw new InvalidOperationException("The target already owns character-resource state; reference adoption refuses to overwrite it.");
        CharacterResourceInventory original = stockPackage.Document.CharacterResources
            ?? throw new InvalidDataException("The reference has no original character-resource inventory.");
        targetPackage.Document.Validate();
        stockPackage.Document.Validate();
        original.Validate();
        CharacterActorSourceAuthoring.RevalidateAll(stockPackage);
        string targetHash = VerifySource(targetPackage);
        string referenceHash = VerifySource(stockPackage);
        if (stockPackage.DecodedCharacterPayload.IsDefaultOrEmpty ||
            stockPackage.DecodedCharacterPayload.Length != original.DecodedByteLength ||
            !Sha(stockPackage.DecodedCharacterPayload).Equals(original.DecodedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reference decoded snapshot differs from its original inventory.");

        // The target's current editable geometry and morph channels are the
        // baseline. Do not replace them with shapes or zero-delta placeholders
        // from the stock reference.
        CustomModelDocument baseline = targetPackage.Document with
        {
            CharacterResources = null,
            GeometryRevision = null,
            AuthoredLayer = null,
            LastBuildReceipt = null,
        };
        ImmutableArray<byte> decoded = DecodedCharacterSnapshotCodec.Encode(new(
            baseline, target.Surfaces, target.SourceLodGroups)
        {
            CharacterLods = target.SourceCharacterLods,
        });
        string decodedHash = Sha(decoded);
        var resources = ImmutableArray.CreateBuilder<CharacterResourceRecord>();
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        long totalBytes = targetPackage.SourceFbx.Length + decoded.Length;
        string targetRootId = "target-source:" + targetHash;
        string referenceArchiveId = "reference-source:" + referenceHash;

        Add(targetRootId, SourceName(targetPackage.Document, "target-source"), targetPackage.SourceFbx,
            CharacterSubsystem.Geometry, CharacterDependencyStatus.Preserved, required: false,
            archive: true, "target-source-sha256:" + targetHash, targetHash,
            "Immutable original target source; this model owns the decoded baseline.", []);
        Add(referenceArchiveId, SourceName(stockPackage.Document, "reference-source"), stockPackage.SourceFbx,
            CharacterSubsystem.Geometry, CharacterDependencyStatus.Preserved, required: false,
            archive: true, "reference-source-sha256:" + referenceHash, referenceHash,
            "Immutable stock reference raw source; retained as custody, not target geometry.", [targetRootId]);
        Add("reference-decoded:" + original.DecodedSha256, "reference-decoded.json",
            stockPackage.DecodedCharacterPayload, CharacterSubsystem.Geometry,
            CharacterDependencyStatus.Preserved, required: false, archive: true,
            "reference-source-sha256:" + referenceHash, referenceHash,
            "Immutable original decoded reference snapshot; target baseline is stored separately.", [referenceArchiveId]);

        if (!original.ActorSourceReviews.IsEmpty)
            Add("reference-actor-source-reviews:" + referenceHash, "reference-actor-source-reviews.json",
                ImmutableArray.Create(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(original.ActorSourceReviews)),
                CharacterSubsystem.Helpers, CharacterDependencyStatus.Preserved, required: false,
                archive: true, "reference-source-sha256:" + referenceHash, referenceHash,
                "Original actor-source review provenance retained as reference custody; it does not establish target actor associations.", [referenceArchiveId]);

        if (!original.BodyRegionReviews.IsEmpty)
            Add("reference-body-region-reviews:" + referenceHash, "reference-body-region-reviews.json",
                ImmutableArray.Create(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(original.BodyRegionReviews)),
                CharacterSubsystem.Damage, CharacterDependencyStatus.Preserved, required: false,
                archive: true, "reference-source-sha256:" + referenceHash, referenceHash,
                "Original body-region authoring links retained as reference provenance; the target requires its own geometry and asset review.", [referenceArchiveId]);
        var declared = original.Resources.Where(static resource => resource.EntryPath is not null).ToArray();
        if (declared.Length != stockPackage.CompanionPayloads.Count)
            throw new InvalidDataException("The reference companion payload inventory is incomplete or contains extras.");
        foreach (CharacterResourceRecord item in original.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = "reference-resource:" + item.Id;
            bool custodyOnly = item.IsOriginalArchive ||
                item.Id == original.RootResourceId ||
                TargetDecodedSubsystems.Contains(item.Subsystem) ||
                item.Subsystem == CharacterSubsystem.Morphs;
            CharacterDependencyStatus status = custodyOnly && item.EntryPath is not null
                ? CharacterDependencyStatus.Preserved : CharacterDependencyStatus.Ambiguous;
            string detail = "Adopted from original reference source SHA-256 " + referenceHash +
                "; target compatibility and runtime behavior require review. " + item.Detail;
            ImmutableArray<string> parents = item.ReferencedBy.IsDefault
                ? [referenceArchiveId]
                : item.ReferencedBy.Select(static value => "reference-resource:" + value)
                    .Append(referenceArchiveId).Distinct(StringComparer.Ordinal).ToImmutableArray();
            if (item.EntryPath is null)
            {
                resources.Add(item with
                {
                    Id = id,
                    Status = CharacterDependencyStatus.Ambiguous,
                    Required = true,
                    IsOriginalArchive = false,
                    ProviderIdentity = "reference:" + item.ProviderIdentity,
                    SourceFingerprint = referenceHash + ":" + item.SourceFingerprint,
                    ReferencedBy = parents,
                    Detail = detail,
                });
                continue;
            }
            if (!stockPackage.CompanionPayloads.TryGetValue(item.EntryPath, out ImmutableArray<byte> bytes) ||
                bytes.IsDefault || bytes.Length != item.ByteLength ||
                !Sha(bytes).Equals(item.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An original reference companion payload has changed.");
            if (item.SupplementalSource is { } supplemental)
                Add(id + ":supplemental-source", item.LogicalName, bytes, item.Subsystem,
                    CharacterDependencyStatus.Preserved, required: false, archive: true,
                    item.ProviderIdentity, item.SourceFingerprint,
                    "Original supplemental archive member.", parents, supplemental);
            Add(id, item.LogicalName, bytes, item.Subsystem, status, required: !custodyOnly,
                archive: custodyOnly, "reference:" + item.ProviderIdentity,
                referenceHash + ":" + item.SourceFingerprint, detail, parents, packed: item.PackedEffect is { } packed ? packed with { BundleResourceId = "reference-resource:" + packed.BundleResourceId } : null,
                material: item.Material is { } material ? material with
                {
                    ProviderResourceId="reference-resource:"+material.ProviderResourceId,
                    Textures=material.Textures.Select(texture=>texture with {ResourceId=texture.ResourceId is { } textureId?"reference-resource:"+textureId:null}).ToImmutableArray()
                }:null, native: item.NativeResource);
        }

        var bindings = ImmutableArray.CreateBuilder<CharacterMorphBinding>();
        var usedBindings = new HashSet<(int Source, string Name, uint Descriptor, string Surface)>(
            EqualityComparer<(int, string, uint, string)>.Default);
        foreach (CharacterMorphBinding originalBinding in original.MorphBindings)
            AddBinding(originalBinding);
        // Preserve source vocabulary even if an original channel has no
        // per-surface binding record in the imported reference inventory.
        foreach (CustomModelMorphChannel channel in stockPackage.Document.MorphChannels)
            if (!bindings.Any(binding => binding.SourceChannelIndex == channel.Index &&
                binding.Name == channel.Name && binding.DescriptorHash == channel.DescriptorHash))
                AddBinding(new(channel.Index, -1, channel.Name, channel.DescriptorHash,
                    string.Empty, -1, 0, 0, "VocabularyOnly"));

        bool missingMorph = bindings.Any(static binding => binding.TargetChannelSlot < 0);
        foreach (CharacterMorphBinding missing in bindings.Where(static binding => binding.TargetChannelSlot < 0))
            resources.Add(new CharacterResourceRecord
            {
                Id = $"unresolved:reference-morph:{missing.SourceChannelIndex}:{missing.DescriptorHash:x8}:{resources.Count}",
                LogicalName = missing.Name,
                ProviderIdentity = "reference-source-sha256:" + referenceHash,
                SourceFingerprint = referenceHash,
                Subsystem = CharacterSubsystem.Morphs,
                Status = CharacterDependencyStatus.Ambiguous,
                Required = true,
                Detail = "Original expression has no exact target name and descriptor mapping; no placeholder shape was created.",
                ReferencedBy = [referenceArchiveId],
            });
        var subsystems = Enum.GetValues<CharacterSubsystem>().Select(subsystem =>
        {
            if (TargetDecodedSubsystems.Contains(subsystem))
                return new CharacterSubsystemReview(subsystem, CharacterDependencyStatus.Decoded,
                    "Target geometry, rig, skinning and LODs remain the neutral model's decoded source.");
            if (subsystem == CharacterSubsystem.Morphs && !missingMorph)
                return new CharacterSubsystemReview(subsystem, CharacterDependencyStatus.Decoded,
                    "Target expression channels remain unchanged; original vocabulary is mapped by exact name and descriptor.");
            return new CharacterSubsystemReview(subsystem, CharacterDependencyStatus.Ambiguous,
                subsystem == CharacterSubsystem.Morphs
                    ? "Original face-channel vocabulary has unmapped target slots; review each mapping."
                    : "Reference companion evidence is retained, but applicability to target requires explicit review.");
        }).ToImmutableArray();
        if (totalBytes > CustomModelPackageSerializer.MaximumPackageBytes)
            throw new InvalidDataException("Reference adoption exceeds the bounded package size.");
        var inventory = new CharacterResourceInventory
        {
            RootResourceId = targetRootId,
            DecodedSha256 = decodedHash,
            DecodedByteLength = decoded.Length,
            Resources = resources.ToImmutable(),
            Subsystems = subsystems,
            MorphBindings = bindings.ToImmutable(),
            VariantNames = original.VariantNames,
            SkinVariants = original.SkinVariants,
            OriginalBuffers = original.OriginalBuffers,
            OriginalMaterials = original.OriginalMaterials,
            OriginalMaterialSlotCount=original.OriginalMaterialSlotCount,
            OriginalEntities = original.OriginalEntities,
            OriginalAppliedSkinName = original.OriginalAppliedSkinName,
            CompiledSemanticSha256 = null,
            LoadedResourceSha256 = null,
            VerifiedPlayerScenarios = [],
        };
        inventory.Validate();
        CustomModelDocument adopted = targetPackage.Document with
        {
            CharacterResources = inventory,
            LastBuildReceipt = null,
        };
        adopted.Validate();
        CustomModelPackage package = targetPackage with
        {
            Document = adopted,
            DecodedCharacterPayload = decoded,
            CompanionPayloads = payloads.ToImmutable(),
        };
        return target with { Package = package };

        void AddBinding(CharacterMorphBinding binding)
        {
            if (!usedBindings.Add((binding.SourceChannelIndex, binding.Name,
                binding.DescriptorHash, binding.SurfaceId))) return;
            CustomModelMorphChannel[] matches = targetPackage.Document.MorphChannels
                .Where(channel => channel.Name == binding.Name &&
                    channel.DescriptorHash == binding.DescriptorHash).ToArray();
            bindings.Add(binding with { TargetChannelSlot = matches.Length == 1 ? matches[0].Index : -1 });
        }

        void Add(string id, string logicalName, ImmutableArray<byte> bytes,
            CharacterSubsystem subsystem, CharacterDependencyStatus status,
            bool required, bool archive, string provider, string sourceFingerprint,
            string detail, ImmutableArray<string> parents, CharacterSupplementalSourceReceipt? supplemental = null, CharacterPackedEffectReceipt? packed = null, CharacterMaterialReceipt? material = null, CharacterNativeResourceReceipt? native = null)
        {
            if (bytes.IsDefault || bytes.Length > CustomModelPackageSerializer.MaximumSourceFbxBytes)
                throw new InvalidDataException("Reference source payload is missing or exceeds the per-resource limit.");
            totalBytes = checked(totalBytes + bytes.Length);
            if (totalBytes > CustomModelPackageSerializer.MaximumPackageBytes)
                throw new InvalidDataException("Reference adoption exceeds the bounded package size.");
            string hash = Sha(bytes);
            string path = $"character/resources/adopted-{resources.Count:D5}-{hash}.bin";
            payloads.Add(path, bytes);
            resources.Add(new CharacterResourceRecord
            {
                Id = id,
                LogicalName = logicalName,
                ProviderIdentity = provider,
                SourceFingerprint = sourceFingerprint,
                ContentSha256 = hash,
                EntryPath = path,
                ByteLength = bytes.Length,
                Subsystem = subsystem,
                Status = status,
                Required = required,
                IsOriginalArchive = archive,
                Detail = detail,
                ReferencedBy = parents,
                SupplementalSource = supplemental,
                PackedEffect = packed,
                Material = material,
                NativeResource = native,
            });
        }
    }

    private static string VerifySource(CustomModelPackage package)
    {
        if (package.SourceFbx.IsDefaultOrEmpty ||
            package.SourceFbx.Length > CustomModelPackageSerializer.MaximumSourceFbxBytes)
            throw new InvalidDataException("Original package source bytes are missing or oversized.");
        string hash = Sha(package.SourceFbx);
        if (!hash.Equals(package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Original package source bytes differ from source identity.");
        return hash;
    }

    private static string SourceName(CustomModelDocument document, string fallback) =>
        string.IsNullOrWhiteSpace(document.Source.OriginalFileName)
            ? fallback : Path.GetFileName(document.Source.OriginalFileName.Replace('\\', '/'));

    private static string Sha(ImmutableArray<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
}
