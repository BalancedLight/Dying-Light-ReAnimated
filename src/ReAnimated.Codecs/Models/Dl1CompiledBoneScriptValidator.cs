using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1BoneScriptReadBack(Guid EntityId, string SourceName, string CompiledName, int CompiledEntityIndex,
    uint ComponentBits, uint LodBits);

/// <summary>
/// Checks the FMeshEntity component/LOD fields defined by DL1's bscr.def.
/// This verifies stored compiler output, not the runtime meaning of those fields.
/// </summary>
public static class Dl1CompiledBoneScriptValidator
{
    public static ImmutableArray<Dl1BoneScriptReadBack> Validate(CompactMeshDocument hierarchy,
        ImmutableArray<Dl1ResolvedBoneScriptPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        if (policies.IsDefault) throw new ArgumentException("Explicit source policies are required.", nameof(policies));
        if (!hierarchy.IsStructurallyValid) throw new InvalidDataException("Component read-back requires a valid compiled hierarchy.");
        var byName = hierarchy.Entities.ToLookup(static node => node.Name, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<Dl1BoneScriptReadBack>(policies.Length);
        foreach (Dl1ResolvedBoneScriptPolicy policy in policies)
        {
            if (policy.EntityId == Guid.Empty || !ids.Add(policy.EntityId) || !names.Add(policy.Name))
                throw new InvalidDataException("Source component decisions must have distinct entity identities and native names.");
            _ = policy.Components; _ = policy.LodToken;
            CompactMeshEntity[] matches = byName[policy.Name].ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Compiled component policy for '{policy.Name}' has {matches.Length} matching entities; exactly one is required.");
            CompactMeshEntity node = matches[0];
            uint componentBits = node.Flags & 0x0700;
            uint lodBits = node.Flags & 0x7000;
            uint expectedComponents = (uint)policy.Mask << 8;
            uint expectedLod = (uint)policy.AnimationLod << 12;
            if (componentBits != expectedComponents || lodBits != expectedLod)
                throw new InvalidDataException($"Compiled node '{node.Name}' changed its requested {policy.Components}/{policy.LodToken} policy " +
                    $"(components 0x{componentBits:X4}, LOD 0x{lodBits:X4}). No model RPack was published.");
            result.Add(new(policy.EntityId, policy.Name, node.Name, node.Index, componentBits, lodBits));
        }
        return result.MoveToImmutable();
    }
}
