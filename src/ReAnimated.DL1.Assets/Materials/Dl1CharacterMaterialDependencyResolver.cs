using System.Collections.Immutable;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Codecs.Materials;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Materials;

public enum Dl1CharacterMaterialDependencyStatus
{
    Resolved,
    MissingMaterial,
    InvalidMaterial,
    MaterialHashCollision,
    MissingTexture,
    TextureHashCollision,
}

public sealed record Dl1CharacterMaterialTextureDependency(
    int TextureIndex,
    uint SamplerState,
    uint TextureNameHash,
    uint LoadFlags,
    Dl1CharacterMaterialDependencyStatus Status,
    string? TextureName,
    RetailAssetId? SelectedTextureId,
    ImmutableArray<RetailAssetId> CandidateTextureIds)
{
    public CharacterTextureNameReceipt? NameSource { get; init; }
}

public sealed record Dl1CharacterMaterialDependency(
    string RequestedName,
    string NormalizedName,
    uint NameHash,
    Dl1CharacterMaterialDependencyStatus Status,
    Dl1MaterialPayloadReceipt? Material,
    ImmutableArray<Dl1CharacterMaterialTextureDependency> Textures);

public sealed record Dl1CharacterMaterialDependencyFinding(
    string RequestedName,
    Dl1CharacterMaterialDependencyStatus Status,
    string Detail,
    uint? TextureNameHash = null);

public sealed record Dl1CharacterMaterialDependencyResolution(
    string ProviderSha256,
    int ProviderByteLength,
    ImmutableArray<Dl1CharacterMaterialDependency> Materials,
    ImmutableArray<Dl1CharacterMaterialDependencyFinding> Findings)
{
    public bool IsComplete => Findings.IsEmpty;
}

public static class Dl1CharacterMaterialDependencyResolver
{
    public const int MaximumMaterialNames = 256;
    public const int MaximumMaterialNameCharacters = 4096;

    public static async Task<Dl1CharacterMaterialDependencyResolution> ResolveAsync(
        ImmutableArray<byte> providerBytes,
        IEnumerable<string> exactNames,
        IRetailAssetCatalog catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactNames);
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        var requests = new List<(string Requested, string Normalized, uint Hash)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int suppliedCount = 0;
        foreach (string name in exactNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++suppliedCount > MaximumMaterialNames)
                throw new ArgumentException("At most 256 material names can be resolved at once.", nameof(exactNames));
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumMaterialNameCharacters)
                throw new ArgumentException("A material name is empty or exceeds the supported length.", nameof(exactNames));
            string normalized = Dl1ResourceNameHash.NormalizeFileName(name);
            if (seen.Add(name)) requests.Add((name, normalized, Dl1ResourceNameHash.Compute(normalized)));
        }
        HashSet<uint> materialCollisions = requests.GroupBy(request => request.Hash)
            .Where(group => group.Select(request => request.Normalized).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key).ToHashSet();
        Dictionary<uint, RetailAssetRecord[]> texturesByHash = BuildTextureHashIndex(catalog, cancellationToken);
        await using Dl1MaterialPackReader reader = await Dl1MaterialPackReader.OpenMemoryAsync(
            providerBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        string providerHash = await reader.GetProviderSha256Async(cancellationToken).ConfigureAwait(false);
        var stringTable = Dl1CompiledMaterialStringTable.Open(providerBytes, cancellationToken: cancellationToken);
        var materials = ImmutableArray.CreateBuilder<Dl1CharacterMaterialDependency>();
        var findings = ImmutableArray.CreateBuilder<Dl1CharacterMaterialDependencyFinding>();
        foreach ((string requested, string normalized, uint hash) in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (materialCollisions.Contains(hash))
            {
                findings.Add(new(requested, Dl1CharacterMaterialDependencyStatus.MaterialHashCollision,
                    "Distinct requested material names share one lookup hash."));
                materials.Add(new(requested, normalized, hash,
                    Dl1CharacterMaterialDependencyStatus.MaterialHashCollision, null, []));
                continue;
            }
            Dl1MaterialPayloadReceipt? receipt;
            try
            {
                receipt = await reader.ReadMaterialReceiptAsync(requested, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                findings.Add(new(requested, Dl1CharacterMaterialDependencyStatus.InvalidMaterial, exception.Message));
                materials.Add(new(requested, normalized, hash,
                    Dl1CharacterMaterialDependencyStatus.InvalidMaterial, null, []));
                continue;
            }
            if (receipt is null)
            {
                findings.Add(new(requested, Dl1CharacterMaterialDependencyStatus.MissingMaterial,
                    "The selected provider has no material with the requested lookup hash."));
                materials.Add(new(requested, normalized, hash,
                    Dl1CharacterMaterialDependencyStatus.MissingMaterial, null, []));
                continue;
            }
            var textures = ImmutableArray.CreateBuilder<Dl1CharacterMaterialTextureDependency>();
            Dl1CharacterMaterialDependencyStatus materialStatus = Dl1CharacterMaterialDependencyStatus.Resolved;
            for (int index = 0; index < receipt.Textures.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dl1MaterialPackTextureRecord texture = receipt.Textures[index];
                RetailAssetRecord[] candidates = texturesByHash.GetValueOrDefault(texture.TextureNameHash) ?? [];
                Dl1CharacterMaterialDependencyStatus status = candidates.Length switch
                {
                    0 => Dl1CharacterMaterialDependencyStatus.MissingTexture,
                    1 => Dl1CharacterMaterialDependencyStatus.Resolved,
                    _ => Dl1CharacterMaterialDependencyStatus.TextureHashCollision,
                };
                if (status != Dl1CharacterMaterialDependencyStatus.Resolved)
                {
                    if (materialStatus != Dl1CharacterMaterialDependencyStatus.TextureHashCollision)
                        materialStatus = status;
                    findings.Add(new(requested, status,
                        status == Dl1CharacterMaterialDependencyStatus.MissingTexture
                            ? "A material texture hash has no effective catalog name."
                            : "A material texture hash matches distinct effective catalog names.",
                        texture.TextureNameHash));
                }
                RetailAssetRecord? selected = candidates.Length == 1 ? candidates[0] : null;
                var originalName = stringTable.Find(texture.TextureNameHash,
                    cancellationToken: cancellationToken);
                textures.Add(new(index, texture.SamplerState, texture.TextureNameHash, texture.LoadFlags,
                    status, originalName?.Value ?? selected?.DisplayName, selected?.Id,
                    candidates.Select(candidate => candidate.Id).ToImmutableArray())
                {
                    NameSource = originalName is null ? null : new()
                    {
                        Name = originalName.Value, TableIndex = originalName.EntryIndex,
                        PayloadOffset = originalName.SourceOffset, ByteLength = originalName.LogicalByteLength,
                        PayloadSha256 = originalName.LogicalSha256,
                    },
                });
            }
            materials.Add(new(requested, normalized, hash, materialStatus, receipt, textures.ToImmutable()));
        }
        return new(providerHash, providerBytes.Length, materials.ToImmutable(), findings.ToImmutable());
    }

    private static Dictionary<uint, RetailAssetRecord[]> BuildTextureHashIndex(IRetailAssetCatalog catalog,
        CancellationToken cancellationToken)
    {
        var grouped = new Dictionary<uint, List<RetailAssetRecord>>();
        var visited = new HashSet<RetailAssetLogicalId>();
        foreach (RetailAssetRecord asset in catalog.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.Id.Namespace != RetailAssetNamespace.RpackResource ||
                asset.Id.ResourceType != Rp6lResourceTypes.Texture) continue;
            if (!visited.Add(asset.Id.LogicalId)) continue;
            RetailAssetRecord? effective = catalog.Resolve(asset.Id.LogicalId);
            if (effective is null) continue;
            if (effective.Id.LogicalId != asset.Id.LogicalId)
                throw new InvalidDataException("The catalog selected a different logical texture identity.");
            uint hash;
            try { hash = Dl1ResourceNameHash.ComputeTextureResource(effective.DisplayName); }
            catch (InvalidDataException) { continue; }
            if (!grouped.TryGetValue(hash, out List<RetailAssetRecord>? rows))
            {
                rows = [];
                grouped.Add(hash, rows);
            }
            rows.Add(effective);
        }
        return grouped.ToDictionary(pair => pair.Key, pair => pair.Value
            .OrderBy(asset => asset.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.Id.StableKey, StringComparer.Ordinal).ToArray());
    }
}


