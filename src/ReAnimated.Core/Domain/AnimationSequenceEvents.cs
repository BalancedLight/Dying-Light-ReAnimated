using System.Collections.Immutable;

namespace ReAnimated.Core.Domain;

/// <summary>Source coordinates for a syntax node in an animation script.</summary>
public readonly record struct AnimationScriptSourceSpan(int Start, int Length)
{
    public int End => checked(Start + Length);
}

public enum AnimationEventDelivery
{
    Event,
    MustSendEvent,
}

/// <summary>An ordered argument retained in its original source spelling.</summary>
public sealed record AnimationEventArgument
{
    public string Text { get; init; } = string.Empty;
    public string? Value { get; init; }
    public AnimationScriptSourceSpan? SourceSpan { get; init; }
}

/// <summary>A child action call attached to an animation event.</summary>
public sealed record AnimationEventAction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Keyword { get; init; } = string.Empty;
    public ImmutableArray<AnimationEventArgument> Arguments { get; init; } = [];
    public AnimationScriptSourceSpan? SourceSpan { get; init; }
    public string? OriginalSource { get; init; }
}

/// <summary>A sequence-local animation notification and its ordered actions.</summary>
public sealed record AnimationEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public double LocalFrame { get; init; }
    public string? FrameExpression { get; init; }
    public int? EventId { get; init; }
    public string IdExpression { get; init; } = string.Empty;
    public short? RequiredSlot { get; init; }
    public string SlotExpression { get; init; } = string.Empty;
    public AnimationEventDelivery Delivery { get; init; }
    public ushort? RawTicks { get; init; }
    public ushort RawFlags { get; init; }
    public uint? RawActionReference { get; init; }
    public ImmutableArray<AnimationEventAction> Actions { get; init; } = [];
    public AnimationScriptSourceSpan? SourceSpan { get; init; }
    public string? OriginalSource { get; init; }
}

/// <summary>A SeqTrack entry and the events authored on its local timeline.</summary>
public sealed record AnimationSequenceUse
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Anm2Name { get; init; } = string.Empty;
    public double SourceStartFrame { get; init; }
    public double SourceEndFrame { get; init; }
    public double FPS { get; init; }
    public int WeightMode { get; init; }
    public double WeightTime { get; init; }
    public string? SourceStartFrameExpression { get; init; }
    public string? SourceEndFrameExpression { get; init; }
    public string? FpsExpression { get; init; }
    public string? WeightModeExpression { get; init; }
    public string? WeightTimeExpression { get; init; }
    public Guid? AnimationReferenceId { get; init; }
    public ImmutableArray<AnimationEvent> Events { get; init; } = [];
    public AnimationScriptSourceSpan? SourceSpan { get; init; }
    public string? OriginalSource { get; init; }
}

public static class AnimationEventTiming
{
    public static ushort ToTicks(double localFrame)
    {
        if (!double.IsFinite(localFrame) || localFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(localFrame), "Event frames must be finite and non-negative.");
        float scaled = (float)localFrame * 5f;
        if (!float.IsFinite(scaled) || scaled > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(localFrame), "The event frame exceeds the 16-bit timestamp range.");
        return checked((ushort)MathF.Truncate(scaled));
    }

    public static ushort ToTicks(AnimationEvent current, AnimationEvent original)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(original);
        return current.LocalFrame == original.LocalFrame && original.RawTicks is { } ticks
            ? ticks
            : ToTicks(current.LocalFrame);
    }
}

public enum AnimationSequenceRetimeMode
{
    KeepFrames,
    PreserveSeconds,
    KeepSourcePositions,
    ScaleToSourceRange,
}

public static class AnimationSequenceRetimer
{
    public static AnimationSequenceUse Retime(
        AnimationSequenceUse sequence,
        double newFps,
        AnimationSequenceRetimeMode mode,
        double? newSourceEndFrame = null,
        double? newSourceStartFrame = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (!double.IsFinite(newFps) || newFps <= 0)
            throw new ArgumentOutOfRangeException(nameof(newFps));
        if (mode == AnimationSequenceRetimeMode.PreserveSeconds && (!double.IsFinite(sequence.FPS) || sequence.FPS <= 0))
            throw new ArgumentException("The current sequence frame rate must be positive to preserve seconds.", nameof(sequence));
        double startFrame = newSourceStartFrame ?? sequence.SourceStartFrame;
        double endFrame = newSourceEndFrame ?? sequence.SourceEndFrame;
        if (!double.IsFinite(startFrame) || startFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(newSourceStartFrame), "The source start must be finite and non-negative.");
        if (!double.IsFinite(endFrame) || endFrame < startFrame)
            throw new ArgumentOutOfRangeException(nameof(newSourceEndFrame), "The source end must be finite and at or after the source start.");
        double scale = mode switch
        {
            AnimationSequenceRetimeMode.KeepFrames => 1,
            AnimationSequenceRetimeMode.PreserveSeconds => newFps / sequence.FPS,
            AnimationSequenceRetimeMode.KeepSourcePositions => 1,
            AnimationSequenceRetimeMode.ScaleToSourceRange =>
                sequence.SourceEndFrame == sequence.SourceStartFrame ? 1 : (endFrame - startFrame) / (sequence.SourceEndFrame - sequence.SourceStartFrame),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        double offset = mode == AnimationSequenceRetimeMode.KeepSourcePositions
            ? sequence.SourceStartFrame - startFrame
            : 0;
        if ((scale != 1 || offset != 0) && sequence.Events.Any(e => e.FrameExpression is not null))
            throw new InvalidOperationException("Symbolic event frames must be resolved before scaling the sequence timeline.");
        ImmutableArray<AnimationEvent> events = sequence.Events.Select(e => e with
        {
            LocalFrame = e.LocalFrame * scale + offset,
            FrameExpression = scale == 1 && offset == 0 ? e.FrameExpression : null,
            RawTicks = scale == 1 && offset == 0 ? e.RawTicks : null,
        }).ToImmutableArray();
        return sequence with
        {
            FPS = newFps,
            SourceStartFrame = startFrame,
            SourceEndFrame = endFrame,
            Events = events,
            FpsExpression = null,
            SourceStartFrameExpression = newSourceStartFrame is null ? sequence.SourceStartFrameExpression : null,
            SourceEndFrameExpression = newSourceEndFrame is null ? sequence.SourceEndFrameExpression : null,
        };
    }
}

public static class AnimationSequenceUseValidation
{
    /// <summary>Returns authoring issues while leaving imported records intact.</summary>
    public static ImmutableArray<string> Validate(AnimationSequenceUse sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ImmutableArray<string>.Builder issues = ImmutableArray.CreateBuilder<string>();
        if (sequence.Id == Guid.Empty) issues.Add("Sequence identity is empty.");
        if (string.IsNullOrWhiteSpace(sequence.Name)) issues.Add("Sequence name is empty.");
        if (string.IsNullOrWhiteSpace(sequence.Anm2Name)) issues.Add("Animation name is empty.");
        if (!double.IsFinite(sequence.SourceStartFrame) || sequence.SourceStartFrame < 0) issues.Add("Source start frame must be finite and non-negative.");
        if (!double.IsFinite(sequence.SourceEndFrame) || sequence.SourceEndFrame < sequence.SourceStartFrame) issues.Add("Source end frame must be finite and at or after the source start.");
        if (!double.IsFinite(sequence.FPS) || sequence.FPS <= 0) issues.Add("Frame rate must be finite and positive.");
        if (!double.IsFinite(sequence.WeightTime)) issues.Add("Weight time must be finite.");
        foreach (AnimationEvent e in sequence.Events)
        {
            if (e.Id == Guid.Empty) issues.Add("An event identity is empty.");
            if (!double.IsFinite(e.LocalFrame) || e.LocalFrame < 0) issues.Add($"Event {e.Id} has an invalid local frame.");
            if (e.EventId is < 0 or > ushort.MaxValue) issues.Add($"Event {e.Id} has an ID outside the 16-bit range.");
            try { _ = AnimationEventTiming.ToTicks(e.LocalFrame); }
            catch (ArgumentOutOfRangeException) { issues.Add($"Event {e.Id} exceeds the 16-bit timestamp range."); }
            foreach (AnimationEventAction action in e.Actions)
                if (action.Id == Guid.Empty) issues.Add($"An action on event {e.Id} has an empty identity.");
            if (e.Actions.Select(action => action.Id).Distinct().Count() != e.Actions.Length)
                issues.Add($"Action identities must be unique on event {e.Id}.");
        }
        if (sequence.Events.Select(e => e.Id).Distinct().Count() != sequence.Events.Length) issues.Add("Event identities must be unique within a sequence.");
        return issues.ToImmutable();
    }
}
