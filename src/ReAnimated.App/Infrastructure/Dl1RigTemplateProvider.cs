using System.Collections.Concurrent;
using System.IO;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// The outcome of resolving a DL1 target skeleton, including the reason a
/// resolution could not be made.
/// </summary>
public sealed record Dl1RigTemplateResolution(
    Dl1RigTemplate? Template,
    string ProfileName,
    string? ResourceName,
    string? Fingerprint,
    string Status)
{
    public bool Succeeded => Template is not null;

    public static Dl1RigTemplateResolution Failed(
        string profileName,
        string status) =>
        new(null, profileName, null, null, status);
}

/// <summary>
/// Resolves the DL1 skeleton a conformance targets from the user's own
/// installed game.
/// </summary>
/// <remarks>
/// Only the skeleton is retained - names, parents, kinds and rest transforms.
/// No retail geometry, texture or animation payload is kept, and nothing is
/// written to the repository or a release. Results are cached in memory for the
/// session, keyed by the decoded resource's SHA-256, so a game update naturally
/// invalidates them.
/// </remarks>
public sealed class Dl1RigTemplateProvider
{
    private readonly ConcurrentDictionary<string, Dl1RigTemplate> _cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Each perspective resolves its own resource and fingerprint. Similar
    /// skeleton topology does not imply identical frames or native consumers.
    /// </summary>
    private static readonly Dictionary<string, string> ProfileResources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Dl1RigTemplateFactory.PlayerProfileName] =
                Dl1RigTemplateFactory.PlayerSourceResourceName,
            [Dl1RigTemplateFactory.PlayerFppProfileName] =
                Dl1RigTemplateFactory.PlayerFppSourceResourceName,
        };

    public static IReadOnlyCollection<string> ProfileNames => ProfileResources.Keys;

    private const string PinnedPrefix = "mesh:";
    private const string PinnedSuffix = "@sha256:";

    /// <summary>Reopen a manually selected reference only at its exact decoded content revision.</summary>
    internal static bool TryParsePinnedProfile(string profileName, out string resourceName, out string fingerprint)
    {
        resourceName = string.Empty; fingerprint = string.Empty;
        if (!profileName.StartsWith(PinnedPrefix, StringComparison.Ordinal)) return false;
        int delimiter = profileName.LastIndexOf(PinnedSuffix, StringComparison.Ordinal);
        if (delimiter <= PinnedPrefix.Length || delimiter + PinnedSuffix.Length + 64 != profileName.Length) return false;
        string name = profileName[PinnedPrefix.Length..delimiter];
        string hash = profileName[(delimiter + PinnedSuffix.Length)..];
        if (name.Length > 160 || hash.Any(c => !char.IsAsciiHexDigit(c))) return false;
        resourceName = name; fingerprint = hash.ToLowerInvariant(); return true;
    }

    public static string PinnedProfileName(string resourceName, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName); ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        string name = PinnedPrefix + resourceName + PinnedSuffix + fingerprint.ToLowerInvariant();
        if (!TryParsePinnedProfile(name, out _, out _)) throw new ArgumentException("A selected reference requires a bounded resource name and exact SHA-256.");
        return name;
    }

    /// <summary>
    /// Reopens the precise retail mesh behind a resolved template so callers
    /// can inspect fields deliberately omitted from the skeleton-only template.
    /// Catalog presence alone is insufficient: the decoded resource hash must
    /// still match the template that was approved for conformance.
    /// </summary>
    public static async Task<Dl1MeshPreviewPayload> DecodeExactTemplateSourceAsync(
        Dl1AssetWorkspace workspace,
        Dl1RigTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(template);
        RetailAssetCatalog catalog = workspace.Catalog ??
            throw new InvalidOperationException("The DL1 asset catalog is not loaded.");
        RetailAssetRecord asset = catalog.Assets.SingleOrDefault(candidate =>
            candidate.Id.Namespace == RetailAssetNamespace.RpackResource &&
            candidate.Id.ResourceType == Rp6lResourceTypes.Mesh &&
            candidate.Id.Name.Equals(template.SourceResourceName, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidDataException(
                $"The indexed retail mesh '{template.SourceResourceName}' is unavailable.");

        Dl1MeshPreviewPayload payload = await workspace.DecodeMeshAsync(asset, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(payload.ResourceSha256, template.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The decoded retail mesh changed since this rig template was selected. Resolve and review the reference again.");
        }

        return payload;
    }

    public async Task<Dl1RigTemplateResolution> ResolveSelectedMeshAsync(
        Dl1AssetWorkspace workspace, RetailAssetRecord selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(selected);
        const string selection = "selected-retail-mesh";
        if (selected.Id.Namespace != RetailAssetNamespace.RpackResource || selected.Id.ResourceType != Rp6lResourceTypes.Mesh)
            return Dl1RigTemplateResolution.Failed(selection, "Select a type-272 retail mesh in Assets.");
        if (workspace.Catalog is not { } catalog || !catalog.Assets.Any(a => a.Id == selected.Id && a.Source == selected.Source))
            return Dl1RigTemplateResolution.Failed(selection, "The selected resource is absent from the current indexed catalog. Reopen Assets and select it again.");
        Dl1MeshPreviewPayload payload;
        try { payload = await workspace.DecodeMeshAsync(selected, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
        { return Dl1RigTemplateResolution.Failed(selection, "Selected mesh could not be decoded: " + error.Message); }
        if (payload.ResourceSha256 is not { Length: 64 } fingerprint || fingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            return Dl1RigTemplateResolution.Failed(selection, "The selected mesh has no exact decoded content fingerprint.");
        string profileName = PinnedProfileName(selected.Id.Name, fingerprint);
        return FromDecoded(selected, profileName, payload);
    }

    public async Task<Dl1RigTemplateResolution> ResolveAsync(
        Dl1AssetWorkspace workspace,
        string profileName = Dl1RigTemplateFactory.PlayerProfileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        if (workspace.Catalog is not { } catalog)
            return Dl1RigTemplateResolution.Failed(profileName, "No Dying Light installation is indexed yet. Index a DL1 install to resolve the target skeleton.");
        if (ProfileResources.TryGetValue(profileName, out string? knownResource))
        {
            RetailAssetRecord? known = FindMesh(catalog, knownResource);
            return known is null
                ? Dl1RigTemplateResolution.Failed(profileName, $"The indexed installation does not contain a mesh resource named '{knownResource}'.")
                : await DecodeKnownAsync(workspace, known, profileName, cancellationToken).ConfigureAwait(false);
        }
        if (!TryParsePinnedProfile(profileName, out string resourceName, out string hash))
            return Dl1RigTemplateResolution.Failed(profileName, $"'{profileName}' is not a known or pinned DL1 rig reference.");
        foreach (RetailAssetRecord candidate in catalog.Assets.Where(a => a.Id.Namespace == RetailAssetNamespace.RpackResource &&
            a.Id.ResourceType == Rp6lResourceTypes.Mesh && a.Id.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dl1MeshPreviewPayload payload;
            try { payload = await workspace.DecodeMeshAsync(candidate, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException) { continue; }
            if (hash.Equals(payload.ResourceSha256, StringComparison.OrdinalIgnoreCase))
                return FromDecoded(candidate, profileName, payload);
        }
        return Dl1RigTemplateResolution.Failed(profileName,
            $"No indexed '{resourceName}' mesh matches the saved SHA-256. Restore that exact reference or choose and review a new one.");
    }

    private async Task<Dl1RigTemplateResolution> DecodeKnownAsync(Dl1AssetWorkspace workspace,
        RetailAssetRecord asset, string profileName, CancellationToken cancellationToken)
    {
        try { return FromDecoded(asset, profileName, await workspace.DecodeMeshAsync(asset, cancellationToken).ConfigureAwait(false)); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException)
        { return Dl1RigTemplateResolution.Failed(profileName, $"'{asset.Id.Name}' could not be decoded: {error.Message}"); }
    }

    private Dl1RigTemplateResolution FromDecoded(RetailAssetRecord asset, string profileName, Dl1MeshPreviewPayload payload)
    {
        string resourceName = asset.Id.Name;
        string fingerprint = payload.ResourceSha256 ?? string.Empty;
        if (fingerprint.Length != 64 || fingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            return Dl1RigTemplateResolution.Failed(profileName, $"'{resourceName}' has no exact decoded content fingerprint.");
        string cacheKey = $"{profileName}|{fingerprint}";
        if (!_cache.TryGetValue(cacheKey, out Dl1RigTemplate? template))
        {
            template = Dl1RigTemplateFactory.TryCreate(profileName, resourceName, fingerprint, payload.Source.Hierarchy);
            if (template is null)
                return Dl1RigTemplateResolution.Failed(profileName, $"'{resourceName}' has no structurally usable decoded animation hierarchy. Partial and malformed rigs remain separate review cases.");
            _cache.TryAdd(cacheKey, template);
        }
        string status = DescribeSuccess(template);
        if (profileName.StartsWith(PinnedPrefix, StringComparison.Ordinal))
        {
            if (payload.Profile is { } classification)
            {
                Dl1RetailFamilyCandidate candidate = Dl1RetailFamilyCandidate.Assess(asset, payload.Source, classification, template);
                if (candidate.Diagnostics.Any(d => d.Code == "identity.catalog-decoded-hash-conflict"))
                    return Dl1RigTemplateResolution.Failed(profileName,
                        "Catalog identity and decoded mesh hash disagree. Refresh the installed catalog and review this reference again.");
                status += $" Indexed family candidate: {classification.RigFamily} / {classification.Perspective}; {candidate.ActualRoleIds.Length} observed roles. Native consumer and animation-bank coverage unverified.";
            }
            else status += " Family classification unavailable; native role and animation-bank coverage unverified.";
        }
        return new(template, profileName, resourceName, fingerprint, status);
    }

    private static string DescribeSuccess(Dl1RigTemplate template) =>
        $"{template.EntityCount} entities ({template.DeformCount} deform bones), " +
        $"{template.ReferenceHeight:F3} m reference height.";

    /// <summary>
    /// Prefers an exact logical-name match on a type-272 mesh. Catalog
    /// precedence selects the indexed provider; actual native mount/load
    /// identity remains a separate validation step.
    /// </summary>
    private static RetailAssetRecord? FindMesh(
        RetailAssetCatalog catalog,
        string resourceName)
    {
        foreach (RetailAssetRecord asset in catalog.Assets)
        {
            if (asset.Id.Namespace == RetailAssetNamespace.RpackResource &&
                asset.Id.ResourceType == Rp6lResourceTypes.Mesh &&
                string.Equals(
                    asset.Id.Name,
                    resourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return null;
    }
}
