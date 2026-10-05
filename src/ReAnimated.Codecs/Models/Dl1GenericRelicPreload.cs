using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public enum Dl1RelicPreloadSelection { Generic, Excluded, Conditional, Unresolved }
public sealed record Dl1RelicPreloadDependency(
    int CallIndex, int BodyElementCallIndex, string BodyElementToken, int? BodyElementId,
    string RelicName, bool WithDestroyedChild, Dl1RelicPreloadSelection Selection, string? Name);
public sealed record Dl1RelicPreloadResult(
    ImmutableArray<Dl1RelicPreloadDependency> Selections,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static row => !row.IsError);
    public bool IsComplete => IsValid && Selections.All(static row => row.Selection != Dl1RelicPreloadSelection.Unresolved);
}

public static class Dl1GenericRelicPreload
{
    public static Dl1RelicPreloadResult Select(Dl1BodyElementsDocument body,
        IReadOnlyDictionary<string, int> exactBodySymbolIds)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(exactBodySymbolIds);
        if (exactBodySymbolIds.Count > Dl1BodyElementSymbolMap.MaximumRequiredSymbols)
            throw new ArgumentException("Too many body symbol mappings.", nameof(exactBodySymbolIds));
        var diagnostics = body.Diagnostics.ToBuilder();
        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        var invalidKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in exactBodySymbolIds)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is < 0 or > Dl1BodyElementSymbolMap.MaximumBodyElementId ||
                invalidKeys.Contains(pair.Key) || !symbols.TryAdd(pair.Key, pair.Value))
            {
                if (pair.Key is not null) { invalidKeys.Add(pair.Key); symbols.Remove(pair.Key); }
                diagnostics.Add(new("body_symbol_invalid", "The supplied body symbol mapping is invalid.", true));
            }
        }
        var collisions = symbols.GroupBy(pair => pair.Value).Where(group => group.Count() > 1).SelectMany(group => group.Select(pair => pair.Key)).ToArray();
        foreach (string key in collisions) symbols.Remove(key);
        if (collisions.Length > 0) diagnostics.Add(new("body_symbol_id_collision", "Multiple body symbols map to the same ID.", true));
        var selections = ImmutableArray.CreateBuilder<Dl1RelicPreloadDependency>();
        foreach (Dl1BodyRelic relic in body.Relics)
        {
            Dl1BodyElement? element = body.Elements.FirstOrDefault(row => row.CallIndex == relic.BodyElementCallIndex);
            string token = element?.ElementToken ?? string.Empty;
            int? id = symbols.TryGetValue(token, out int found) ? found : null;
            Dl1RelicPreloadSelection selection;
            string? name = null;
            if (!body.IsValid || element is null || id is null || string.IsNullOrWhiteSpace(relic.Name))
            {
                selection = Dl1RelicPreloadSelection.Unresolved;
                diagnostics.Add(new("relic_preload_unresolved", $"Relic call {relic.CallIndex} has no valid exact body mapping."));
            }
            else if (id is >= 7 and <= 10) selection = Dl1RelicPreloadSelection.Excluded;
            else if (!body.ForceGenericRelics) selection = Dl1RelicPreloadSelection.Conditional;
            else
            {
                selection = Dl1RelicPreloadSelection.Generic;
                name = relic.Name.ToLowerInvariant() + ".msh";
            }
            selections.Add(new(relic.CallIndex, relic.BodyElementCallIndex, token, id, relic.Name,
                relic.WithDestroyedChild, selection, name));
        }
        return new(selections.ToImmutable(), diagnostics.ToImmutable());
    }
}
