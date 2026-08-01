using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

public enum ProjectMorphBindingResolutionMode
{
    Preview,
    Export,
}

/// <summary>
/// Revalidates persisted facial mappings against the exact decoded retail
/// morph inventory before preview or export. Descriptor hashes prevent a
/// same-name mapping from silently crossing model families.
/// </summary>
public static class ProjectMorphBindingResolver
{
    public static ImmutableArray<MorphChannelBinding> Resolve(
        IEnumerable<ProjectMorphBinding> bindings,
        RigDefinition exactTargetRig,
        ProjectMorphBindingResolutionMode mode)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(exactTargetRig);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Dictionary<string, MorphChannelDefinition> targetMorphs =
            exactTargetRig.MorphChannels.ToDictionary(
                static morph => morph.Name,
                StringComparer.OrdinalIgnoreCase);
        var result =
            ImmutableArray.CreateBuilder<MorphChannelBinding>();
        foreach (ProjectMorphBinding binding in bindings)
        {
            if (mode == ProjectMorphBindingResolutionMode.Export &&
                binding.Enabled &&
                (!binding.IsReviewed || !binding.IsLocked))
            {
                throw new InvalidDataException(
                    $"Enabled morph binding '{binding.SourceChannel}' -> " +
                    $"'{binding.TargetMorph}' is not reviewed and locked for export.");
            }

            if (!targetMorphs.TryGetValue(
                    binding.TargetMorph,
                    out MorphChannelDefinition? target))
            {
                throw new InvalidDataException(
                    $"Persisted morph target '{binding.TargetMorph}' is absent from exact retail rig '{exactTargetRig.Id}'.");
            }

            if (binding.TargetDescriptorHash is uint expectedDescriptor &&
                target.DescriptorHash != expectedDescriptor)
            {
                string actual = target.DescriptorHash is uint value
                    ? $"0x{value:X8}"
                    : "missing";
                throw new InvalidDataException(
                    $"Persisted morph target '{binding.TargetMorph}' expects descriptor 0x{expectedDescriptor:X8}, but exact retail rig '{exactTargetRig.Id}' reports {actual}.");
            }

            result.Add(
                new MorphChannelBinding(
                    binding.SourceChannel,
                    target.Name,
                    binding.Weight,
                    binding.Bias,
                    binding.Enabled));
        }

        return result.ToImmutable();
    }
}
