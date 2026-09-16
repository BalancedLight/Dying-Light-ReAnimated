using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1ResolvedBoneScriptPolicy(
    Guid EntityId, int PhysicalIndex, string Name, RigAnimationComponents Mask,
    RigAnimationLod AnimationLod, AnimationComponentPolicy Decision)
{
    public string Components => Dl1BoneScriptPolicyResolver.FormatComponents(Mask);
    public string LodToken => Dl1BoneScriptPolicyResolver.FormatLod(AnimationLod);
}

/// <summary>
/// Resolves explicit studio choices against the one emitted hierarchy. Ownership
/// evidence is retained for review; this operation does not certify native composition.
/// </summary>
public static class Dl1BoneScriptPolicyResolver
{
    public static ImmutableArray<Dl1ResolvedBoneScriptPolicy> Resolve(CustomModelDocument document, Dl1AuthoredRigContract contract)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate();
        ArgumentNullException.ThrowIfNull(contract);
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("Explicit component resolution requires a studio session.");
        if (!session.MatchesSource(document.Source.ContentSha256)) throw new InvalidDataException("Studio source identity requires review before component export.");
        var policies = session.Recipe.ComponentPolicies.ToDictionary(static p => p.EntityId);
        var entities = session.Recipe.Entities.ToDictionary(static e => e.EntityId);
        var emitted = new HashSet<Guid>();
        var result = ImmutableArray.CreateBuilder<Dl1ResolvedBoneScriptPolicy>(contract.Nodes.Length);
        foreach (Dl1AuthoredRigNode node in contract.Nodes)
        {
            if (node.SemanticEntityId is not { } id || !entities.TryGetValue(id, out RigEntityBinding? entity) ||
                entity.OwnerAssetId != document.ModelId || entity.NativeName != node.Name || !emitted.Add(id))
                throw new InvalidDataException($"Emitted node '{node.Name}' has no unambiguous studio identity in this model.");
            if (!policies.TryGetValue(id, out AnimationComponentPolicy? policy) || policy.EmittedMask is null ||
                policy.AnimationLod is null || policy.LodRuleId is null)
                throw new InvalidDataException($"Node '{node.Name}' requires explicit POS/ROT/SCL and animation LOD decisions before studio export.");
            RequireEvidence(policy.Position, node.Name, "position");
            RequireEvidence(policy.Rotation, node.Name, "rotation");
            RequireEvidence(policy.Scale, node.Name, "scale");
            if (!policy.LodEvidence.Any(static e => e.ArtifactSha256 is not null))
                throw new InvalidDataException($"Node '{node.Name}' has no artifact-backed LOD evidence.");
            result.Add(new(id, node.PhysicalIndex, node.Name, policy.EmittedMask.Value, policy.AnimationLod.Value, policy));
        }
        foreach (AnimationComponentPolicy policy in session.Recipe.ComponentPolicies)
            if (entities[policy.EntityId].OwnerAssetId == document.ModelId && !emitted.Contains(policy.EntityId))
                throw new InvalidDataException("A component decision has no emitted animation entity. It cannot be silently omitted from BSCR output.");
        return result.MoveToImmutable();
    }

    public static string FormatComponents(RigAnimationComponents components)
    {
        if ((components & ~(RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale)) != 0)
            throw new ArgumentOutOfRangeException(nameof(components));
        if (components == RigAnimationComponents.None) return "NONE";
        var tokens = new List<string>(3);
        if (components.HasFlag(RigAnimationComponents.Position)) tokens.Add("POS");
        if (components.HasFlag(RigAnimationComponents.Rotation)) tokens.Add("ROT");
        if (components.HasFlag(RigAnimationComponents.Scale)) tokens.Add("SCL");
        return string.Join(" | ", tokens);
    }

    public static string FormatLod(RigAnimationLod lod) => lod switch
    {
        RigAnimationLod.Lod0 => "LOD_0", RigAnimationLod.Lod1 => "LOD_1", RigAnimationLod.Lod2 => "LOD_2",
        RigAnimationLod.Lod3 => "LOD_3", RigAnimationLod.Off => "LOD_OFF", _ => throw new ArgumentOutOfRangeException(nameof(lod)),
    };

    private static void RequireEvidence(RigChannelOwnership channel, string node, string component)
    {
        if (channel.Owners.IsEmpty || channel.Owners.Contains(RigComponentOwner.Unknown) ||
            !channel.Evidence.Any(static e => e.ArtifactSha256 is not null))
            throw new InvalidDataException($"Node '{node}' has unresolved {component} ownership or lacks artifact-backed evidence.");
    }
}
