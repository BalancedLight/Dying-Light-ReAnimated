using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Evaluation;

public sealed record AnimationEventPreviewState(
    bool? IkEnabled,
    ImmutableDictionary<string, bool> LimbIk,
    ImmutableDictionary<string, bool> ElementVisibility)
{
    public static AnimationEventPreviewState Reconstruct(AnimationSequenceUse? sequence, double frame, short? slot = null)
    {
        bool? enabled = null;
        var limbs = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.OrdinalIgnoreCase);
        var visibility = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (sequence is null) return new(enabled, limbs.ToImmutable(), visibility.ToImmutable());
        foreach (AnimationEvent item in sequence.Events.Where(e => e.LocalFrame <= frame && AnimationEventPlaybackCursor.MatchesSlot(e, slot)).OrderBy(e => e.LocalFrame))
        {
            switch (item.EventId)
            {
                case 1040: enabled = true; break;
                case 1041: enabled = false; break;
                case 1060: limbs["left hand"] = true; break;
                case 1061: limbs["left hand"] = false; break;
                case 1062: limbs["right hand"] = true; break;
                case 1063: limbs["right hand"] = false; break;
                case 1011: limbs["left foot"] = true; break;
                case 1013: limbs["left foot"] = false; break;
                case 1015: limbs["right foot"] = true; break;
                case 1017: limbs["right foot"] = false; break;
            }
            foreach (AnimationEventAction action in item.Actions)
            {
                if (action.Arguments.IsEmpty) continue;
                string element = Unquote(action.Arguments[0].Text);
                if (action.Keyword.Equals("ShowElement", StringComparison.OrdinalIgnoreCase)) visibility[element] = true;
                else if (action.Keyword.Equals("HideElement", StringComparison.OrdinalIgnoreCase)) visibility[element] = false;
            }
        }
        return new(enabled, limbs.ToImmutable(), visibility.ToImmutable());
    }

    public bool AllowsIk(string chainName)
    {
        if (IkEnabled == false) return false;
        string lower = chainName.ToLowerInvariant();
        foreach ((string limb, bool value) in LimbIk)
        {
            string[] words = limb.Split(' ');
            if (lower.Contains(words[0], StringComparison.Ordinal) && lower.Contains(words[1], StringComparison.Ordinal)) return value;
        }
        return true;
    }
    public static string Unquote(string text) => text.Trim().Trim('"');
}

public static class AnimationEventPlaybackCursor
{
    public static bool MatchesSlot(AnimationEvent item, short? slot) => item.RequiredSlot == -1 || slot is null || item.RequiredSlot == slot;

    public static ImmutableArray<AnimationEvent> Start(AnimationSequenceUse sequence, double frame, short? slot = null) =>
        sequence.Events.Where(e => MatchesSlot(e, slot) && (e.LocalFrame == frame || e.LocalFrame < frame && e.Delivery == AnimationEventDelivery.MustSendEvent)).OrderBy(e => e.LocalFrame).ToImmutableArray();

    public static IEnumerable<AnimationEventPlaybackSegment> CrossSegments(AnimationSequenceUse sequence, double from, double to, long loops, short? slot = null)
    {
        if (loops < 0 || !double.IsFinite(from) || !double.IsFinite(to)) throw new ArgumentOutOfRangeException(nameof(loops));
        if (loops == 0 && to <= from) yield break;
        var events = sequence.Events.Where(e => MatchesSlot(e, slot)).OrderBy(e => e.LocalFrame).ToImmutableArray();
        if (loops == 0) { yield return new(events.Where(e => e.LocalFrame > from && e.LocalFrame <= to).ToImmutableArray(), false); yield break; }
        double end = sequence.SourceEndFrame - sequence.SourceStartFrame;
        yield return new(events.Where(e => e.LocalFrame > from && e.LocalFrame <= end).ToImmutableArray(), false);
        for (long cycle = 1; cycle < Math.Min(loops, 65); cycle++) yield return new(events.Where(e => e.LocalFrame >= 0 && e.LocalFrame <= end).ToImmutableArray(), true);
        yield return new(events.Where(e => e.LocalFrame >= 0 && e.LocalFrame <= to).ToImmutableArray(), true);
    }

    public static ImmutableArray<AnimationEvent> Cross(AnimationSequenceUse sequence, double from, double to, long loops, short? slot = null)
    {
        if (loops < 0 || !double.IsFinite(from) || !double.IsFinite(to)) throw new ArgumentOutOfRangeException(nameof(loops));
        if (loops == 0 && to <= from) return [];
        AnimationEvent[] events = sequence.Events.Where(e => MatchesSlot(e, slot)).OrderBy(e => e.LocalFrame).ToArray();
        var crossed = ImmutableArray.CreateBuilder<AnimationEvent>();
        if (loops == 0) crossed.AddRange(events.Where(e => e.LocalFrame > from && e.LocalFrame <= to));
        else
        {
            crossed.AddRange(events.Where(e => e.LocalFrame > from && e.LocalFrame <= sequence.SourceEndFrame - sequence.SourceStartFrame));
            // UI wakeups can span many loops. Bound the materialized preview batch.
            for (long cycle = 1; cycle < Math.Min(loops, 65); cycle++) crossed.AddRange(events.Where(e => e.LocalFrame >= 0 && e.LocalFrame <= sequence.SourceEndFrame - sequence.SourceStartFrame));
            crossed.AddRange(events.Where(e => e.LocalFrame >= 0 && e.LocalFrame <= to));
        }
        return crossed.ToImmutable();
    }
}

public sealed record AnimationEventPlaybackSegment(ImmutableArray<AnimationEvent> Events, bool StartsNewCycle);
