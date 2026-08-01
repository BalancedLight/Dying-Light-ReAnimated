using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Fed;

public sealed record FedLayerBuildResult(
    MorphEditLayer Layer,
    ImmutableArray<FedDiagnostic> Diagnostics,
    FedExpressionCompatibility Compatibility);

public enum FedLayerCompatibilityPolicy
{
    AllowPartial,
    RequireComplete,
}

public sealed record FedExpressionCompatibility(
    int SourceWeightCount,
    int ResolvedWeightCount,
    int ResolvedTargetCount,
    ImmutableArray<string> MissingSourceMorphNames)
{
    public bool IsComplete =>
        ResolvedWeightCount == SourceWeightCount &&
        MissingSourceMorphNames.IsEmpty;
}

/// <summary>
/// Converts a retail FED expression into an ordinary non-destructive facial
/// layer. The source FED remains external and is never embedded in a project.
/// </summary>
public static class FedDomainAdapter
{
    public static FedLayerBuildResult CreateLayer(
        FedDocument document,
        int expressionIndex,
        RigDefinition targetRig,
        IReadOnlyDictionary<string, string>? modelFamilyMapping = null,
        MorphEditLayerScope scope = MorphEditLayerScope.AuthoredExportable,
        MorphEditBlendMode blendMode = MorphEditBlendMode.Additive,
        double weight = 1,
        FedLayerCompatibilityPolicy compatibilityPolicy =
            FedLayerCompatibilityPolicy.AllowPartial)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(targetRig);
        if ((uint)expressionIndex >= (uint)document.Expressions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(expressionIndex));
        }

        FedExpression expression = document.Expressions[expressionIndex];
        HashSet<string> targetInventory = targetRig.MorphChannels
            .Select(static morph => morph.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);
        var diagnostics = ImmutableArray.CreateBuilder<FedDiagnostic>();
        var missingNames = ImmutableArray.CreateBuilder<string>();
        int resolvedWeightCount = 0;
        foreach (FedMorphWeight sourceWeight in expression.Weights)
        {
            string targetName;
            if (modelFamilyMapping is not null &&
                modelFamilyMapping.TryGetValue(
                    sourceWeight.MorphName,
                    out string? mapped))
            {
                targetName = mapped;
            }
            else
            {
                targetName = sourceWeight.MorphName;
            }

            if (!targetInventory.Contains(targetName))
            {
                diagnostics.Add(
                    new(
                        "FED101",
                        FedDiagnosticSeverity.Warning,
                        $"FED morph '{sourceWeight.MorphName}' has no target on rig '{targetRig.Id}'.",
                        expressionIndex));
                if (!missingNames.Contains(
                        sourceWeight.MorphName,
                        StringComparer.OrdinalIgnoreCase))
                {
                    missingNames.Add(sourceWeight.MorphName);
                }

                continue;
            }

            resolvedWeightCount++;
            values.TryGetValue(targetName, out double existing);
            values[targetName] = existing + sourceWeight.Weight;
        }

        var compatibility = new FedExpressionCompatibility(
            expression.Weights.Count,
            resolvedWeightCount,
            values.Count,
            missingNames.ToImmutable());
        if (compatibilityPolicy ==
                FedLayerCompatibilityPolicy.RequireComplete &&
            !compatibility.IsComplete)
        {
            string missing = string.Join(
                ", ",
                compatibility.MissingSourceMorphNames.Take(8));
            string suffix =
                compatibility.MissingSourceMorphNames.Length > 8
                    ? ", ..."
                    : string.Empty;
            throw new InvalidOperationException(
                $"FED expression '{expression.Name}' resolves {compatibility.ResolvedWeightCount} of {compatibility.SourceWeightCount} rows on rig '{targetRig.Id}'. Accurate application requires a complete model-family match. Missing: {missing}{suffix}");
        }

        ImmutableArray<MorphEditTrack> tracks = targetRig.MorphChannels
            .Where(morph => values.ContainsKey(morph.Name))
            .Select(morph =>
                new MorphEditTrack(
                    morph.Name,
                    [new ScalarKeyframe(0, values[morph.Name])]))
            .ToImmutableArray();
        if (tracks.IsEmpty)
        {
            throw new InvalidOperationException(
                $"FED expression '{expression.Name}' has no morphs compatible with rig '{targetRig.Id}'.");
        }

        return new(
            new MorphEditLayer(
                Guid.NewGuid(),
                $"FED: {expression.Name}",
                blendMode,
                scope,
                weight,
                tracks),
            diagnostics.ToImmutable(),
            compatibility);
    }

    public static FedLayerBuildResult CreateLayer(
        FedDocument document,
        string expressionName,
        RigDefinition targetRig,
        IReadOnlyDictionary<string, string>? modelFamilyMapping = null,
        MorphEditLayerScope scope = MorphEditLayerScope.AuthoredExportable,
        MorphEditBlendMode blendMode = MorphEditBlendMode.Additive,
        double weight = 1,
        FedLayerCompatibilityPolicy compatibilityPolicy =
            FedLayerCompatibilityPolicy.AllowPartial)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(expressionName);
        int index = -1;
        for (var current = 0;
             current < document.Expressions.Count;
             current++)
        {
            if (string.Equals(
                    document.Expressions[current].Name,
                    expressionName,
                    StringComparison.OrdinalIgnoreCase))
            {
                index = current;
                break;
            }
        }

        if (index < 0)
        {
            throw new KeyNotFoundException(
                $"FED expression '{expressionName}' was not found.");
        }

        return CreateLayer(
            document,
            index,
            targetRig,
            modelFamilyMapping,
            scope,
            blendMode,
            weight,
            compatibilityPolicy);
    }
}
