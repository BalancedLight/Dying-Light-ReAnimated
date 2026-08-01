using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Core.Project;

/// <summary>
/// Describes whether a target can participate in authoritative playback.
/// Cross-rig suggestions are deliberately not considered playable until the
/// shared mapping-review policy accepts every required decision.
/// </summary>
public enum TargetBindingStatus
{
    Direct,
    NeedsReview,
    Ready,
    Invalid,
}

/// <summary>
/// Content-addressed identity for one immutable animation source interpreted
/// against one exact retail target. Names are intentionally excluded.
/// </summary>
public readonly record struct AnimationVariantKey
{
    public AnimationVariantKey(string value)
    {
        ProjectAssetReference.ValidateSha256(value, nameof(value));
        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public static AnimationVariantKey Create(
        ProjectAnimation animation,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(assets);
        ProjectAnimationSourceBinding binding = animation.SourceBinding
            ?? throw new ArgumentException(
                "An animation variant requires an immutable source binding.",
                nameof(animation));
        ProjectAssetReference source = ResolveAsset(
            assets,
            binding.AssetId,
            "source animation");
        string sourceModel = binding.RetailSourceModelAssetId is { } modelId
            ? ResolveFingerprint(
                ResolveAsset(assets, modelId, "source model"),
                "source model")
            : "local-fbx";
        string target = animation.TargetAssetId is { } targetId
            ? ResolveFingerprint(
                ResolveAsset(assets, targetId, "target model"),
                "target model")
            : "target-not-selected";
        string animationIdentity = animation.VariantGroupId is { } groupId
            ? groupId.ToString("N")
            : CreateGroupId(animation, assets).ToString("N");
        string material = string.Join(
            "|",
            "dlra-animation-variant-v2",
            animationIdentity,
            ResolveFingerprint(source, "source animation"),
            sourceModel,
            binding.SourceRigSignature,
            binding.Partition?.Fingerprint ?? "no-partition",
            target);
        return new AnimationVariantKey(Hash(material));
    }

    public static Guid CreateGroupId(
        ProjectAnimation animation,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(assets);
        ProjectAnimationSourceBinding binding = animation.SourceBinding
            ?? throw new ArgumentException(
                "An animation variant group requires an immutable source binding.",
                nameof(animation));
        ProjectAssetReference source = ResolveAsset(
            assets,
            binding.AssetId,
            "source animation");
        string sourceModel = binding.RetailSourceModelAssetId is { } modelId
            ? ResolveFingerprint(
                ResolveAsset(assets, modelId, "source model"),
                "source model")
            : "local-fbx";
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join(
                "|",
                "dlra-animation-variant-group-v1",
                ResolveFingerprint(source, "source animation"),
                sourceModel,
                binding.SourceRigSignature,
                binding.Partition?.Fingerprint ?? "no-partition")));
        Span<byte> guidBytes = digest.AsSpan(0, 16);
        // Mark this as a name-derived UUID while retaining deterministic bytes.
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    public override string ToString() => Value;

    private static ProjectAssetReference ResolveAsset(
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets,
        Guid id,
        string role)
    {
        if (!assets.TryGetValue(id, out ProjectAssetReference? asset))
        {
            throw new ArgumentException(
                $"The animation variant's {role} is missing.",
                nameof(assets));
        }

        return asset;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string ResolveFingerprint(
        ProjectAssetReference asset,
        string role) =>
        asset.ContentSha256 ??
        throw new ArgumentException(
            $"The animation variant's {role} has no content fingerprint.",
            nameof(asset));
}
