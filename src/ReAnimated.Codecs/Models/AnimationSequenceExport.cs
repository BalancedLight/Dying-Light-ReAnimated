using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Models;

public static class AnimationSequenceExport
{
    public static AnimationScrSections BuildCompiled(IReadOnlyList<AnimationSequenceUse> sequences)
    {
        try { return AnimationScrCodec.BuildWithEvents(sequences); }
        catch (NotSupportedException exception) { throw new InvalidDataException(exception.Message, exception); }
    }

    public static ImmutableArray<AnimationSequenceUse> Merge(IEnumerable<AnimationScrSequence> records, IEnumerable<AnimationSequenceUse>? authored = null, string? source = null)
    {
        ImmutableArray<AnimationSequenceUse> edits = authored?.ToImmutableArray() ?? [];
        if (edits.IsEmpty && source is not null) edits = AnimationScriptTextCodec.Read(source).Sequences;
        var sequences = records.Select(record => edits.FirstOrDefault(s => s.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase)) ?? new AnimationSequenceUse
        {
            Name = record.Name, Anm2Name = record.Anm2Name, SourceStartFrame = record.StartFrame, SourceEndFrame = record.EndFrame,
            FPS = record.FramesPerSecond, WeightMode = record.WeightMode, WeightTime = record.WeightTime,
        }).ToList();
        foreach (AnimationSequenceUse sequence in edits)
            if (!sequences.Any(s => s.Name.Equals(sequence.Name, StringComparison.OrdinalIgnoreCase))) sequences.Add(sequence);
        return sequences.ToImmutableArray();
    }

    public static ImmutableArray<AnimationScrSequence> Records(IEnumerable<AnimationSequenceUse> sequences) => sequences.Select(sequence => new AnimationScrSequence(
        sequence.Name, sequence.Anm2Name, (float)sequence.SourceStartFrame, (float)sequence.SourceEndFrame, (float)sequence.FPS, sequence.WeightMode, (float)sequence.WeightTime)).ToImmutableArray();

    public static string Source(IEnumerable<AnimationSequenceUse> sequences)
    {
        ImmutableArray<AnimationSequenceUse> items = sequences.ToImmutableArray();
        string text = AnimationScriptTextCodec.Write(new AnimationScriptTextDocument { Sequences = items });
        return IncludeDefinitions(text, items);
    }

    public static string IncludeDefinitions(string text, IEnumerable<AnimationSequenceUse> sequences)
    {
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AnimationEvent item in sequences.SelectMany(s => s.Events))
        {
            string? definition = Dl1AnimationEventCatalog.FindEvent(item.IdExpression)?.DefinitionFile;
            if (!string.IsNullOrWhiteSpace(definition)) includes.Add(Path.GetFileName(definition));
            if (!item.Actions.IsEmpty) includes.Add("anim_actions.def");
            if (item.Actions.Any(a => a.Arguments.Any(arg => arg.Text.Contains("GameVolumeSource_", StringComparison.Ordinal)))) includes.Add("game_volume_source.def");
        }
        string prefix = string.Join("", includes.Order(StringComparer.OrdinalIgnoreCase).Where(name => !text.Contains('"' + name + '"', StringComparison.OrdinalIgnoreCase)).Select(name => $"!include(\"{name}\")\n"));
        return prefix + text;
    }

    public static void AddAnimationAliases(IDictionary<string, byte[]> animations, IEnumerable<AnimationSequenceUse> sequences)
    {
        foreach (AnimationSequenceUse sequence in sequences)
        {
            string animation = Path.GetFileNameWithoutExtension(sequence.Anm2Name);
            if (!animations.TryGetValue(animation, out byte[]? payload)) throw new InvalidDataException($"Sequence '{sequence.Name}' references missing animation '{sequence.Anm2Name}'.");
            if (animations.TryGetValue(sequence.Name, out byte[]? existing) && !existing.AsSpan().SequenceEqual(payload)) throw new InvalidDataException($"Sequence '{sequence.Name}' conflicts with another animation resource.");
            animations[sequence.Name] = payload;
        }
    }
}
