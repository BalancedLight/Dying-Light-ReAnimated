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
    /// The retail resource each bounded profile is extracted from. FPP is
    /// deliberately absent: it carries the same 87 entity names with identical
    /// parent topology, so one template serves both perspectives.
    /// </summary>
    private static readonly Dictionary<string, string> ProfileResources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Dl1RigTemplateFactory.PlayerProfileName] =
                Dl1RigTemplateFactory.PlayerSourceResourceName,
        };

    public static IReadOnlyCollection<string> ProfileNames => ProfileResources.Keys;

    public async Task<Dl1RigTemplateResolution> ResolveAsync(
        Dl1AssetWorkspace workspace,
        string profileName = Dl1RigTemplateFactory.PlayerProfileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        if (!ProfileResources.TryGetValue(profileName, out string? resourceName))
        {
            return Dl1RigTemplateResolution.Failed(
                profileName,
                $"'{profileName}' is not a known DL1 rig template profile.");
        }

        if (workspace.Catalog is not { } catalog)
        {
            return Dl1RigTemplateResolution.Failed(
                profileName,
                "No Dying Light installation is indexed yet. Index a DL1 install to resolve the target skeleton.");
        }

        RetailAssetRecord? asset = FindMesh(catalog, resourceName);
        if (asset is null)
        {
            return Dl1RigTemplateResolution.Failed(
                profileName,
                $"The indexed installation does not contain a mesh resource named '{resourceName}'.");
        }

        Dl1MeshPreviewPayload payload;
        try
        {
            payload = await workspace
                .DecodeMeshAsync(asset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            IOException)
        {
            return Dl1RigTemplateResolution.Failed(
                profileName,
                $"'{resourceName}' could not be decoded: {exception.Message}");
        }

        string fingerprint = payload.ResourceSha256 ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return Dl1RigTemplateResolution.Failed(
                profileName,
                $"'{resourceName}' decoded without a content fingerprint, so its skeleton cannot be identified.");
        }

        string cacheKey = $"{profileName}|{fingerprint}";
        if (_cache.TryGetValue(cacheKey, out Dl1RigTemplate? cached))
        {
            return new Dl1RigTemplateResolution(
                cached,
                profileName,
                resourceName,
                fingerprint,
                DescribeSuccess(cached));
        }

        Dl1RigTemplate? template = Dl1RigTemplateFactory.TryCreate(
            profileName,
            resourceName,
            fingerprint,
            payload.Source.Hierarchy);
        if (template is null)
        {
            return Dl1RigTemplateResolution.Failed(
                profileName,
                $"'{resourceName}' decoded, but carries no usable animation skeleton to target.");
        }

        _cache.TryAdd(cacheKey, template);
        return new Dl1RigTemplateResolution(
            template,
            profileName,
            resourceName,
            fingerprint,
            DescribeSuccess(template));
    }

    private static string DescribeSuccess(Dl1RigTemplate template) =>
        $"{template.EntityCount} entities ({template.DeformCount} deform bones), " +
        $"{template.ReferenceHeight:F3} m reference height.";

    /// <summary>
    /// Prefers an exact logical-name match on a type-272 mesh. Catalog
    /// precedence already put the winning provider first, so the first match is
    /// the resource the game would load.
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
