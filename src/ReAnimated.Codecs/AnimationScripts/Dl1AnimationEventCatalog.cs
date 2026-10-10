using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReAnimated.Codecs.AnimationScripts;

public sealed record Dl1AnimationEventDefinition(string Symbol, int Id, string Category, string DefinitionFile, bool Legacy, int ObservedCount, ImmutableArray<string> ObservedIn)
{
    public string DisplayName => Humanize(Symbol);

    private static string Humanize(string value)
    {
        foreach (string prefix in new[] { "VIS_EVENT_", "SOUND_EVENT_", "EVENT_" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { value = value[prefix.Length..]; break; }
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' ').ToLowerInvariant());
    }
}
public sealed record Dl1AnimationEventAlias(int Id, ImmutableArray<string> Symbols, string Note);
public sealed record Dl1UnresolvedAnimationEvent(string Expression, int Count, string Status);
public sealed record Dl1AnimationActionDefinition(string Keyword, ImmutableArray<string> Signature, ImmutableArray<string> ParameterNames, int UsageCount, string? SemanticSummary)
{
    public string DisplayName => Keyword.ToLowerInvariant() switch
    {
        "playsound2d" => "Play sound (2D)", "playsound3d" => "Play sound (3D)", "playsound23d" => "Play sound (23D)",
        "playsound2dexternal" => "Play external sound", "playaisound" => "Play AI sound", "playfx" => "Play effect",
        "playforcefeedback" => "Controller feedback", "activateslowmo" => "Slow motion", "forcereaction" => "Reaction",
        "setragdollbehavior" => "Ragdoll behavior", "setragdollbehaviorontarget" => "Target ragdoll behavior",
        "showelement" => "Show element", "hideelement" => "Hide element", "enableupdateextents" => "Update bounds",
        "screenfadein" => "Fade in", "screenfadeout" => "Fade out", "_attributes" => "Attributes",
        var name when name.StartsWith("playplayersound", StringComparison.Ordinal) => "Player sound (" + name[15..] + ")",
        _ => Keyword,
    };
}
public sealed record Dl1AnimationSoundMapping(int EventId, string EventSymbol, string SoundName, string SoundTypeSymbol, string TargetSymbol)
{
    public string DisplayName => string.IsNullOrWhiteSpace(SoundName) ? EventSymbol : $"{EventSymbol} · {SoundName}";
}
public sealed record Dl1AnimationEventColor(ImmutableArray<string> Symbols, bool IsDefault, ImmutableArray<int> Rgb);
public sealed record Dl1AnimationEventGroup(ImmutableArray<string> Symbols, bool IsDefault, string Name);

/// <summary>Compact shipped-data definitions for the DL1 animation event editor.</summary>
public static class Dl1AnimationEventCatalog
{
    private const string ResourceName = "ReAnimated.Codecs.AnimationScripts.dl1_event_catalog.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly CatalogPayload Data = Load();

    public static ImmutableArray<Dl1AnimationEventDefinition> Events => Data.Events;
    public static ImmutableArray<Dl1AnimationEventAlias> Aliases => Data.Aliases;
    public static ImmutableArray<Dl1UnresolvedAnimationEvent> Unresolved => Data.Unresolved;
    public static ImmutableArray<Dl1AnimationActionDefinition> Actions => Data.Actions;
    public static ImmutableArray<Dl1AnimationSoundMapping> SoundMappings => Data.SoundMappings;
    public static ImmutableArray<string> DefinitionRegistry => Data.Editor.DefinitionRegistry;
    public static ImmutableArray<string> IkEvents => Data.Editor.IkEvents;
    public static ImmutableArray<Dl1AnimationEventColor> Colors => Data.Editor.Colors;
    public static ImmutableArray<Dl1AnimationEventGroup> Groups => Data.Editor.Groups;

    public static Dl1AnimationEventDefinition? FindEvent(string symbol) =>
        Events.FirstOrDefault(e => e.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

    public static ImmutableArray<Dl1AnimationEventDefinition> FindEvents(int id) =>
        Events.Where(e => e.Id == id).ToImmutableArray();

    public static Dl1AnimationActionDefinition? FindAction(string keyword) =>
        Actions.FirstOrDefault(a => a.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase));

    private static CatalogPayload Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The animation event catalog resource is missing.");
        CatalogDto dto = JsonSerializer.Deserialize<CatalogDto>(stream, JsonOptions)
            ?? throw new InvalidDataException("The animation event catalog is empty.");
        return new CatalogPayload(
            dto.Events.Select(e => new Dl1AnimationEventDefinition(e.Symbol, e.Id, e.Category ?? "", e.DefinitionFile ?? "", e.Legacy, e.ObservedCount, e.ObservedIn.ToImmutableArray())).ToImmutableArray(),
            dto.Aliases.Select(a => new Dl1AnimationEventAlias(a.Id, a.Symbols.ToImmutableArray(), a.Note ?? "")).ToImmutableArray(),
            dto.Unresolved.Select(u => new Dl1UnresolvedAnimationEvent(u.Expression, u.Count, u.Status ?? "")).ToImmutableArray(),
            dto.Actions.Select(a => new Dl1AnimationActionDefinition(a.Keyword, a.Signature.ToImmutableArray(), a.ParameterNames.ToImmutableArray(), a.UsageCount, a.SemanticSummary)).ToImmutableArray(),
            dto.SoundMappings.Select(s => new Dl1AnimationSoundMapping(s.EventId, s.EventSymbol ?? "", s.SoundName ?? "", s.SoundTypeSymbol ?? "", s.TargetSymbol ?? "")).ToImmutableArray(),
            new EditorPayload(dto.Editor.DefinitionRegistry.ToImmutableArray(), dto.Editor.IkEvents.ToImmutableArray(), dto.Editor.Colors.Select(c => new Dl1AnimationEventColor(c.Symbols.ToImmutableArray(), c.IsDefault, c.Rgb.ToImmutableArray())).ToImmutableArray(), dto.Editor.Groups.Select(g => new Dl1AnimationEventGroup(g.Symbols.ToImmutableArray(), g.IsDefault, g.Name ?? "")).ToImmutableArray()));
    }

    private sealed record CatalogPayload(ImmutableArray<Dl1AnimationEventDefinition> Events, ImmutableArray<Dl1AnimationEventAlias> Aliases, ImmutableArray<Dl1UnresolvedAnimationEvent> Unresolved, ImmutableArray<Dl1AnimationActionDefinition> Actions, ImmutableArray<Dl1AnimationSoundMapping> SoundMappings, EditorPayload Editor);
    private sealed record EditorPayload(ImmutableArray<string> DefinitionRegistry, ImmutableArray<string> IkEvents, ImmutableArray<Dl1AnimationEventColor> Colors, ImmutableArray<Dl1AnimationEventGroup> Groups);
    private sealed class CatalogDto
    {
        [JsonPropertyName("events")] public EventDto[] Events { get; init; } = [];
        [JsonPropertyName("aliases")] public AliasDto[] Aliases { get; init; } = [];
        [JsonPropertyName("unresolved")] public UnresolvedDto[] Unresolved { get; init; } = [];
        [JsonPropertyName("actions")] public ActionDto[] Actions { get; init; } = [];
        [JsonPropertyName("sound_mappings")] public SoundDto[] SoundMappings { get; init; } = [];
        [JsonPropertyName("editor")] public EditorDto Editor { get; init; } = new();
    }
    private sealed class EventDto { public string Symbol { get; init; } = ""; public int Id { get; init; } [JsonPropertyName("category")] public string? Category { get; init; } [JsonPropertyName("definition_file")] public string? DefinitionFile { get; init; } [JsonPropertyName("legacy")] public bool Legacy { get; init; } [JsonPropertyName("observed_count")] public int ObservedCount { get; init; } [JsonPropertyName("observed_in")] public string[] ObservedIn { get; init; } = []; }
    private sealed class AliasDto { public int Id { get; init; } public string[] Symbols { get; init; } = []; public string? Note { get; init; } }
    private sealed class UnresolvedDto { public string Expression { get; init; } = ""; public int Count { get; init; } public string? Status { get; init; } }
    private sealed class ActionDto { public string Keyword { get; init; } = ""; public string[] Signature { get; init; } = []; [JsonPropertyName("parameter_names")] public string[] ParameterNames { get; init; } = []; [JsonPropertyName("usage_count")] public int UsageCount { get; init; } [JsonPropertyName("semantic_summary")] public string? SemanticSummary { get; init; } }
    private sealed class SoundDto { [JsonPropertyName("event_id")] public int EventId { get; init; } [JsonPropertyName("event_symbol")] public string? EventSymbol { get; init; } [JsonPropertyName("sound_name")] public string? SoundName { get; init; } [JsonPropertyName("sound_type_symbol")] public string? SoundTypeSymbol { get; init; } [JsonPropertyName("target_symbol")] public string? TargetSymbol { get; init; } }
    private sealed class EditorDto { [JsonPropertyName("definition_registry")] public string[] DefinitionRegistry { get; init; } = []; [JsonPropertyName("ik_events")] public string[] IkEvents { get; init; } = []; public ColorDto[] Colors { get; init; } = []; public GroupDto[] Groups { get; init; } = []; }
    private sealed class ColorDto { public string[] Symbols { get; init; } = []; [JsonPropertyName("is_default")] public bool IsDefault { get; init; } public int[] Rgb { get; init; } = []; }
    private sealed class GroupDto { public string[] Symbols { get; init; } = []; [JsonPropertyName("is_default")] public bool IsDefault { get; init; } public string? Name { get; init; } }
}
