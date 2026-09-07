using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

public enum FacialControlGroup { Eyes, Brows, Mouth, Other }

public sealed record FacialControlDefinition
{
    public string MorphName { get; init; } = string.Empty;
    public FacialControlGroup Group { get; init; }
    public string? PartnerName { get; init; }
}

public sealed record FacialPresetDefinition
{
    public string Name { get; init; } = string.Empty;
    public string? SourceLabel { get; init; }
    public ImmutableDictionary<string, double> Weights { get; init; } = ImmutableDictionary<string, double>.Empty;
    public ImmutableDictionary<string, double> SpeechWeights { get; init; } = ImmutableDictionary<string, double>.Empty;
}

/// <summary>Portable model-owned expression data. Preview selection never authors an animation.</summary>
public sealed record FacialPresetLibrary
{
    public ImmutableArray<FacialControlDefinition> Controls { get; init; } = [];
    public ImmutableArray<FacialPresetDefinition> Presets { get; init; } = [];

    public void Validate(IEnumerable<string>? targetMorphNames = null)
    {
        if (Controls.IsDefault || Presets.IsDefault || Controls.Length > 4096 || Presets.Length > 1024)
            throw new ArgumentException("Facial library collections are missing or exceed their limits.");
        HashSet<string>? inventory = targetMorphNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (FacialControlDefinition control in Controls)
        {
            if (control is null) throw new ArgumentException("Facial control is missing.");
            CheckName(control.MorphName);
            if (!names.Add(control.MorphName) || !Enum.IsDefined(control.Group))
                throw new ArgumentException("Facial controls have duplicate names or an invalid group.");
            CheckTarget(control.MorphName);
            if (control.PartnerName is { } partner)
            {
                CheckName(partner);
                CheckTarget(partner);
                if (string.Equals(partner, control.MorphName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A linked control cannot name itself as its partner.");
            }
        }
        names.Clear();
        foreach (FacialPresetDefinition preset in Presets)
        {
            if (preset is null) throw new ArgumentException("Facial preset is missing.");
            CheckName(preset.Name);
            if (!names.Add(preset.Name) || preset.Weights is null || preset.Weights.Count > 4096)
                throw new ArgumentException("Facial presets have duplicate names or invalid weights.");
            if (preset.SourceLabel is { Length: > 1024 })
                throw new ArgumentException("Facial preset source label is too long.");
            HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
            ValidateWeights(preset.Weights);
            ValidateWeights(preset.SpeechWeights);
            void ValidateWeights(ImmutableDictionary<string, double> weights)
            {
                if (weights is null || weights.Count > 4096) throw new ArgumentException("Facial weights are invalid.");
                targets.Clear();
                foreach ((string target, double weight) in weights)
                {
                    CheckName(target);
                    CheckTarget(target);
                    if (!targets.Add(target) || !double.IsFinite(weight) || weight is < -4 or > 4)
                        throw new ArgumentException("Facial preset weights must be unique, finite, and within -4..4.");
                }
            }
        }
        return;
        void CheckTarget(string target)
        {
            if (inventory is not null && !inventory.Contains(target))
                throw new ArgumentException($"Facial target '{target}' is missing from the model.");
        }
        static void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Contains('\0'))
                throw new ArgumentException("Facial names must contain 1..256 non-NUL characters.");
        }
    }
}
