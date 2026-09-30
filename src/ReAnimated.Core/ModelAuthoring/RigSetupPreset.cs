using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Portable decisions, without donor entity identities, fitted coordinates or acceptance receipts.</summary>
public sealed record RigSetupPreset
{
    public string Format { get; init; } = "dl-reanimated-rig-setup";
    public int Version { get; init; } = 1;
    public string Name { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public required RigCapabilityProfile Profile { get; init; }
    public ImmutableArray<string> Capabilities { get; init; } = [];
    public ImmutableArray<string> CharacterOwnerRoles { get; init; } = [];
    public ImmutableArray<RigSetupNode> Nodes { get; init; } = [];
}

public sealed record RigSetupNode
{
    public string Key { get; init; } = string.Empty;
    public string NativeName { get; init; } = string.Empty;
    public RigNativeEntityKind Kind { get; init; }
    public ImmutableArray<string> Roles { get; init; } = [];
    public RigSetupChannels? Channels { get; init; }
    public bool RequiresFrameReview { get; init; }
}

public sealed record RigSetupChannels
{
    public ImmutableArray<RigComponentOwner> PositionOwners { get; init; } = [];
    public ImmutableArray<RigComponentOwner> RotationOwners { get; init; } = [];
    public ImmutableArray<RigComponentOwner> ScaleOwners { get; init; } = [];
    public string? PositionRule { get; init; }
    public string? RotationRule { get; init; }
    public string? ScaleRule { get; init; }
    public RigAnimationComponents? EmittedMask { get; init; }
    public RigAnimationLod? AnimationLod { get; init; }
    public string? LodRuleId { get; init; }

    public AnimationComponentPolicy Bind(Guid entityId) => new()
    {
        EntityId = entityId,
        Position = new() { Owners = PositionOwners, CompositionRuleId = PositionRule },
        Rotation = new() { Owners = RotationOwners, CompositionRuleId = RotationRule },
        Scale = new() { Owners = ScaleOwners, CompositionRuleId = ScaleRule },
        EmittedMask = EmittedMask, AnimationLod = AnimationLod, LodRuleId = LodRuleId,
    };

    internal static RigSetupChannels From(AnimationComponentPolicy policy) => new()
    {
        PositionOwners = policy.Position.Owners, PositionRule = policy.Position.CompositionRuleId,
        RotationOwners = policy.Rotation.Owners, RotationRule = policy.Rotation.CompositionRuleId,
        ScaleOwners = policy.Scale.Owners, ScaleRule = policy.Scale.CompositionRuleId,
        EmittedMask = policy.EmittedMask, AnimationLod = policy.AnimationLod, LodRuleId = policy.LodRuleId,
    };
}

public static class RigSetupPresetSerializer
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = CustomModelPackageSerializer.CreateSerializerOptions();

    public static RigSetupPreset Capture(CustomModelDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate();
        var session = document.RiggingSession ?? throw new InvalidOperationException("Start a Studio session before saving a reusable setup.");
        if (!session.MatchesSource(document.Source.ContentSha256)) throw new InvalidOperationException("Review the changed source before saving this setup.");
        var recipe = session.Recipe;
        var profile = recipe.ProfileSnapshot ?? throw new InvalidOperationException("Save a reviewed profile definition before creating a reusable setup.");
        var used = recipe.Assignments.Select(a => a.EntityId).Concat(recipe.ComponentPolicies.Select(p => p.EntityId))
            .Concat(recipe.Helpers.Select(h => h.EntityId)).Concat(recipe.FramePolicies.Select(f => f.EntityId)).ToHashSet();
        if (recipe.Entities.Any(e => used.Contains(e.EntityId) && e.OwnerAssetId != document.ModelId))
            throw new InvalidOperationException("This setup includes another asset's nodes. Save that asset's setup separately; equipment owners cannot be reassigned to the character.");
        var nodes = recipe.Entities.Where(e => used.Contains(e.EntityId)).Select(e => new RigSetupNode
        {
            // An opaque, deterministic preset-local key. Resolution never compares
            // it to destination IDs or treats it as a physical node index.
            Key = "node-" + Convert.ToHexStringLower(SHA256.HashData(e.EntityId.ToByteArray())), NativeName = e.NativeName, Kind = e.Kind,
            Roles = recipe.Assignments.Where(a => a.EntityId == e.EntityId).Select(a => a.RoleId).ToImmutableArray(),
            Channels = recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == e.EntityId) is { } channels ? RigSetupChannels.From(channels) : null,
            RequiresFrameReview = recipe.Helpers.Any(h => h.EntityId == e.EntityId) || recipe.FramePolicies.Any(f => f.EntityId == e.EntityId),
        }).ToImmutableArray();
        return Seal(new() { Name = name, Profile = profile, Capabilities = recipe.SelectedCapabilityIds,
            CharacterOwnerRoles = recipe.AssetRoles.Where(a => a.AssetId == document.ModelId).Select(a => a.RoleId).ToImmutableArray(), Nodes = nodes });
    }

    public static RigSetupPreset Seal(RigSetupPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var candidate = preset with { ContentSha256 = new string('0', 64) };
        Validate(candidate);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, Options);
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Rig setup exceeds 8 MiB.");
        return candidate with { ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    public static void Verify(RigSetupPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset); Validate(preset);
        if (!RigContractRules.SameHash(preset.ContentSha256, Seal(preset).ContentSha256))
            throw new ArgumentException("Rig setup content differs from its recorded revision.");
    }

    public static byte[] Serialize(RigSetupPreset preset)
    { Verify(preset); return JsonSerializer.SerializeToUtf8Bytes(preset, Options); }

    public static RigSetupPreset Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Rig setup exceeds 8 MiB.");
        var preset = JsonSerializer.Deserialize<RigSetupPreset>(bytes, Options) ?? throw new ArgumentException("Rig setup is empty.");
        Verify(preset); return preset;
    }

    private static void Validate(RigSetupPreset preset)
    {
        if (preset.Format != "dl-reanimated-rig-setup" || preset.Version != 1) throw new ArgumentException("Unsupported rig setup format.");
        RigContractRules.Text(preset.Name, nameof(preset.Name)); RigContractRules.Hash(preset.ContentSha256, nameof(preset.ContentSha256));
        ArgumentNullException.ThrowIfNull(preset.Profile); RigCapabilityProfileSerializer.Verify(preset.Profile);
        RigRecipeRules.Names(preset.Capabilities, nameof(preset.Capabilities));
        RigRecipeRules.Names(preset.CharacterOwnerRoles, nameof(preset.CharacterOwnerRoles));
        if (preset.Nodes.IsDefault || preset.Nodes.Length > 4096 || preset.Nodes.Any(n => n is null) || preset.Nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != preset.Nodes.Length)
            throw new ArgumentException("Rig setups require at most 4096 distinct node selectors.");
        var roles = preset.Profile.Roles.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var capabilities = preset.Profile.Capabilities.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (preset.Capabilities.Any(c => !capabilities.Contains(c))) throw new ArgumentException("A setup capability is not declared by its profile.");
        foreach (var node in preset.Nodes)
        {
            RigContractRules.Text(node.Key, nameof(node.Key)); RigContractRules.Text(node.NativeName, nameof(node.NativeName));
            RigContractRules.Defined(node.Kind, nameof(node.Kind)); RigRecipeRules.Names(node.Roles, nameof(node.Roles));
            if (node.Roles.Any(r => !roles.Contains(r))) throw new ArgumentException("A setup assignment is not declared by its profile.");
            node.Channels?.Bind(Guid.Parse("00000000-0000-0000-0000-000000000001")).Validate();
        }
    }
}
