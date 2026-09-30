using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Portable declared profile content. Content identity is not native evidence certification.</summary>
public static class RigCapabilityProfileSerializer
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = CustomModelPackageSerializer.CreateSerializerOptions();
        options.WriteIndented = false; options.MaxDepth = 64;
        return options;
    }

    public static RigCapabilityProfile Seal(RigCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Identity);
        CheckLimits(profile);
        var candidate = profile with { Identity = profile.Identity with { ContentSha256 = new string('0', 64) } };
        candidate.Validate(); CheckLimits(candidate);
        string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(candidate, Options)));
        return candidate with { Identity = candidate.Identity with { ContentSha256 = hash } };
    }

    public static void Verify(RigCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile); CheckLimits(profile); profile.Validate();
        if (!RigContractRules.SameHash(profile.Identity.ContentSha256, Seal(profile).Identity.ContentSha256))
            throw new ArgumentException("The capability profile content differs from its recorded fingerprint. Obtain a reviewed profile revision.");
    }

    public static byte[] Serialize(RigCapabilityProfile profile)
    {
        Verify(profile);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Options);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Capability profiles must be at most 4 MiB.");
        return bytes;
    }

    public static RigCapabilityProfile Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Capability profiles must be at most 4 MiB.");
        var profile = JsonSerializer.Deserialize<RigCapabilityProfile>(bytes, Options)
            ?? throw new ArgumentException("The capability profile is empty.");
        Verify(profile); return profile;
    }

    private static void CheckLimits(RigCapabilityProfile profile)
    {
        if (profile.Roles.IsDefault || profile.Capabilities.IsDefault || profile.Consumers.IsDefault ||
            profile.Roles.Any(r => r is null || r.Aliases.IsDefault || r.Evidence.IsDefault ||
                r.Aliases.Any(a => a is null || a.Evidence.IsDefault || a.Evidence.Any(e => e is null)) || r.Evidence.Any(e => e is null)) ||
            profile.Capabilities.Any(c => c is null || c.Facets.IsDefault || c.Facets.Any(f => f is null)) ||
            profile.Consumers.Any(c => c is null || c.Evidence.IsDefault || c.Evidence.Any(e => e is null)))
            throw new ArgumentException("Capability profile collections must contain complete, non-null entries.");
        if (profile.Roles.Length > 4096 || profile.Capabilities.Length > 256 || profile.Consumers.Length > 4096)
            throw new ArgumentException("Capability profiles exceed the supported role, capability or consumer limits.");
    }
}
