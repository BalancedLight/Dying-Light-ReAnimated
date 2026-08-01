using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Evaluation;

public sealed record MorphEvaluationResult(
    ImmutableDictionary<string, double> AuthoredWeights,
    ImmutableDictionary<string, double> DisplayWeights,
    ImmutableArray<EvaluationDiagnostic> Diagnostics);

public static class MorphEvaluator
{
    public static MorphEvaluationResult Evaluate(
        IReadOnlyDictionary<string, double> sampledWeights,
        RigDefinition targetRig,
        double sampleFrame,
        PreviewProfile previewProfile,
        EvaluationPurpose purpose,
        IEnumerable<MorphChannelBinding>? bindings = null,
        IEnumerable<MorphEditLayer>? layers = null)
    {
        ArgumentNullException.ThrowIfNull(sampledWeights);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(previewProfile);
        if (!double.IsFinite(sampleFrame))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleFrame));
        }

        ImmutableArray<MorphChannelBinding> bindingArray =
            bindings?.ToImmutableArray() ?? [];
        ImmutableArray<MorphEditLayer> layerArray =
            layers?.ToImmutableArray() ?? [];
        var diagnostics = ImmutableArray.CreateBuilder<EvaluationDiagnostic>();
        Dictionary<string, double> authored = BindChannels(
            sampledWeights,
            targetRig,
            bindingArray,
            diagnostics);
        ApplyLayers(
            authored,
            targetRig,
            sampleFrame,
            layerArray,
            MorphEditLayerScope.AuthoredExportable,
            diagnostics);

        Dictionary<string, double> display = new(
            authored,
            StringComparer.OrdinalIgnoreCase);
        if (purpose == EvaluationPurpose.Preview)
        {
            ApplyLayers(
                display,
                targetRig,
                sampleFrame,
                layerArray,
                MorphEditLayerScope.PreviewOnly,
                diagnostics);
            ApplyDl1DisplayPolicy(
                display,
                targetRig,
                previewProfile,
                diagnostics);
        }

        return new(
            authored.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            display.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            diagnostics.ToImmutable());
    }

    private static Dictionary<string, double> BindChannels(
        IReadOnlyDictionary<string, double> sampled,
        RigDefinition rig,
        ImmutableArray<MorphChannelBinding> bindings,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        HashSet<string> inventory = rig.MorphChannels
            .Select(static morph => morph.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);
        if (bindings.IsEmpty)
        {
            foreach ((string channel, double value) in sampled)
            {
                if (!double.IsFinite(value))
                {
                    throw new InvalidOperationException(
                        $"Sampled morph '{channel}' is non-finite.");
                }

                if (!inventory.Contains(channel))
                {
                    diagnostics.Add(
                        new(
                            "morph_channel_unmapped",
                            EvaluationDiagnosticSeverity.Warning,
                            $"Morph channel '{channel}' is absent from target rig '{rig.Id}'."));
                    continue;
                }

                result[channel] = value;
            }

            return result;
        }

        foreach (MorphChannelBinding binding in bindings)
        {
            if (!binding.Enabled)
            {
                continue;
            }

            if (!inventory.Contains(binding.TargetMorph))
            {
                diagnostics.Add(
                    new(
                        "morph_binding_target_missing",
                        EvaluationDiagnosticSeverity.Error,
                        $"Morph binding target '{binding.TargetMorph}' is absent from rig '{rig.Id}'."));
                continue;
            }

            if (!sampled.TryGetValue(binding.SourceChannel, out double value))
            {
                continue;
            }

            double weighted =
                (value * binding.Weight) + binding.Bias;
            result.TryGetValue(binding.TargetMorph, out double existing);
            result[binding.TargetMorph] = existing + weighted;
        }

        return result;
    }

    private static void ApplyLayers(
        Dictionary<string, double> values,
        RigDefinition rig,
        double frame,
        ImmutableArray<MorphEditLayer> layers,
        MorphEditLayerScope scope,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        HashSet<string> inventory = rig.MorphChannels
            .Select(static morph => morph.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (MorphEditLayer layer in layers.Where(layer =>
                     layer.Enabled &&
                     layer.Scope == scope &&
                     layer.Weight > 0))
        {
            foreach (MorphEditTrack track in layer.Tracks)
            {
                if (!inventory.Contains(track.MorphName))
                {
                    diagnostics.Add(
                        new(
                            "morph_layer_target_missing",
                            EvaluationDiagnosticSeverity.Error,
                            $"Facial layer '{layer.Name}' targets missing morph '{track.MorphName}'."));
                    continue;
                }

                values.TryGetValue(track.MorphName, out double existing);
                double sampled = track.Sample(frame);
                values[track.MorphName] = layer.BlendMode switch
                {
                    MorphEditBlendMode.Additive =>
                        existing + (sampled * layer.Weight),
                    MorphEditBlendMode.Override =>
                        existing + ((sampled - existing) * layer.Weight),
                    _ => throw new InvalidOperationException(
                        $"Unknown facial blend mode '{layer.BlendMode}'."),
                };
            }
        }
    }

    private static void ApplyDl1DisplayPolicy(
        Dictionary<string, double> values,
        RigDefinition rig,
        PreviewProfile profile,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics)
    {
        Dictionary<string, MorphChannelDefinition> inventory = rig.MorphChannels
            .ToDictionary(
                static morph => morph.Name,
                StringComparer.OrdinalIgnoreCase);
        if (profile.ClampMorphWeightsToRigBounds)
        {
            foreach (string name in values.Keys.ToArray())
            {
                if (!inventory.TryGetValue(
                        name,
                        out MorphChannelDefinition? morph))
                {
                    continue;
                }

                double clamped = Math.Clamp(
                    values[name],
                    morph.MinimumValue,
                    morph.MaximumValue);
                if (clamped != values[name])
                {
                    values[name] = clamped;
                    diagnostics.Add(
                        new(
                            "morph_runtime_clamped",
                            EvaluationDiagnosticSeverity.Information,
                            $"DL1 preview clamped morph '{name}' to its configured rig bounds."));
                }
            }
        }

        if (profile.MorphActivationThreshold > 0)
        {
            double thresholdTolerance = Math.Max(
                1.0e-9,
                profile.MorphActivationThreshold * 1.0e-6);
            foreach (string name in values.Keys.ToArray())
            {
                if (Math.Abs(values[name]) <=
                    profile.MorphActivationThreshold +
                    thresholdTolerance)
                {
                    values.Remove(name);
                }
            }
        }

        if (profile.MaximumActiveMorphTargets is not int maximum ||
            values.Count <= maximum)
        {
            return;
        }

        HashSet<string> retained = rig.MorphChannels
            .Where(morph => values.ContainsKey(morph.Name))
            .Take(maximum)
            .Select(static morph => morph.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in values.Keys.ToArray())
        {
            if (!retained.Contains(name))
            {
                values.Remove(name);
            }
        }

        diagnostics.Add(
            new(
                "morph_runtime_active_limit",
                EvaluationDiagnosticSeverity.Warning,
                $"DL1 preview retained the first {maximum} active morph targets in retail rig order."));
    }

}
