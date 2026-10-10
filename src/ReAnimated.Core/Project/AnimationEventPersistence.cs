using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

public sealed record AnimationScriptBinaryBacking
{
    public byte[] RecordsAndNames { get; init; } = [];
    public byte[] IndexAndNames { get; init; } = [];
}

public static class AnimationEventPersistence
{
    public static void Validate(ImmutableArray<AnimationSequenceUse> sequences)
    {
        if (sequences.IsDefault || sequences.Length > 1_000_000) throw new ArgumentException("Invalid sequence collection.");
        var ids = new HashSet<Guid>();
        foreach (AnimationSequenceUse sequence in sequences)
        {
            if (sequence.Id == Guid.Empty || !ids.Add(sequence.Id)) throw new ArgumentException("Sequence identities must be unique.");
            if (string.IsNullOrWhiteSpace(sequence.Name) || string.IsNullOrWhiteSpace(sequence.Anm2Name) || sequence.Name.Length > 1024 || sequence.Anm2Name.Length > 4096 || !double.IsFinite(sequence.SourceStartFrame) || !double.IsFinite(sequence.SourceEndFrame) || !double.IsFinite(sequence.FPS) || !double.IsFinite(sequence.WeightTime) || sequence.Events.IsDefault) throw new ArgumentException("Invalid sequence metadata.");
            var eventIds = new HashSet<Guid>();
            foreach (AnimationEvent item in sequence.Events)
            {
                if (item.Id == Guid.Empty || !eventIds.Add(item.Id) || !double.IsFinite(item.LocalFrame) || item.EventId is < 0 or > ushort.MaxValue || item.IdExpression is null || item.SlotExpression is null || !Enum.IsDefined(item.Delivery) || item.Actions.IsDefault) throw new ArgumentException("Invalid event metadata.");
                var actionIds = new HashSet<Guid>();
                foreach (AnimationEventAction action in item.Actions)
                {
                    if (action.Id == Guid.Empty || !actionIds.Add(action.Id) || string.IsNullOrWhiteSpace(action.Keyword) || action.Arguments.IsDefault || action.Arguments.Any(a => a.Text is null)) throw new ArgumentException("Invalid event action.");
                }
            }
        }
    }
}
