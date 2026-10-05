using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record CharacterGoreAssetSelection(string ResourceId, string LogicalName, string ContentSha256);

/// <summary>Reviewed authoring assembly. This never proves a native relic-name branch or Player behavior.</summary>
public sealed record CharacterBodyRegionAssemblyReview
{
    public string BodyResourceId { get; init; } = string.Empty;
    public string BodySourceSha256 { get; init; } = string.Empty;
    public int BodyElementCallIndex { get; init; }
    public string ElementToken { get; init; } = string.Empty;
    public string HelperName { get; init; } = string.Empty;
    public string HelperFrameSha256 { get; init; } = string.Empty;
    public string GeometrySha256 { get; init; } = string.Empty;
    public ImmutableArray<string> CutCapSurfaceIds { get; init; } = [];
    public ImmutableArray<string> BodyHideEntityNames { get; init; } = [];
    public ImmutableArray<string> RelicHideEntityNames { get; init; } = [];
    public ImmutableArray<CharacterGoreAssetSelection> DetachedAssets { get; init; } = [];
    public ImmutableArray<CharacterGoreAssetSelection> PhysicsAssets { get; init; } = [];
    public ImmutableArray<CharacterGoreAssetSelection> EffectAssets { get; init; } = [];
    public bool ArtistGeometryReviewed { get; init; }
    public bool RelationshipsReviewed { get; init; }
    public bool Accepted { get; init; }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(BodyResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ElementToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(HelperName);
        ProjectAssetReference.ValidateSha256(BodySourceSha256, nameof(BodySourceSha256));
        ProjectAssetReference.ValidateSha256(HelperFrameSha256, nameof(HelperFrameSha256));
        ProjectAssetReference.ValidateSha256(GeometrySha256, nameof(GeometrySha256));
        if (BodyElementCallIndex < 0 || BodyResourceId.Length > 4096 || ElementToken.Length > 4096 || HelperName.Length > 4096)
            throw new ArgumentException("Body-region review identity is invalid.");
        Names(CutCapSurfaceIds); Names(BodyHideEntityNames); Names(RelicHideEntityNames);
        Assets(DetachedAssets); Assets(PhysicsAssets); Assets(EffectAssets);
        if (Accepted && (!ArtistGeometryReviewed || !RelationshipsReviewed))
            throw new ArgumentException("An accepted body-region assembly requires both reviews.");
        static void Names(ImmutableArray<string> names)
        {
            if (names.IsDefault || names.Length > 4096 || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 4096) ||
                names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ArgumentException("Body-region names must be bounded and unique.");
        }
        static void Assets(ImmutableArray<CharacterGoreAssetSelection> assets)
        {
            if (assets.IsDefault || assets.Length > 256 || assets.Select(asset => asset.ResourceId).Distinct(StringComparer.Ordinal).Count() != assets.Length)
                throw new ArgumentException("Body-region asset selections must be bounded and unique.");
            foreach (var asset in assets)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(asset.ResourceId);
                ArgumentException.ThrowIfNullOrWhiteSpace(asset.LogicalName);
                if (asset.ResourceId.Length > 4096 || asset.LogicalName.Length > 4096)
                    throw new ArgumentException("Body-region asset identity exceeds its bound.");
                ProjectAssetReference.ValidateSha256(asset.ContentSha256, nameof(asset.ContentSha256));
            }
        }
    }
}
