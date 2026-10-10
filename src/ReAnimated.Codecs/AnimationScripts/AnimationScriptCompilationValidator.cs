using System.Globalization;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.AnimationScripts;

internal static class AnimationScriptCompilationValidator
{
    public static void ValidateSequence(AnimationSequenceUse sequence)
    {
        ValidateNumber(sequence.SourceStartFrameExpression, sequence.SourceStartFrame, "start frame", sequence.Name);
        ValidateNumber(sequence.SourceEndFrameExpression, sequence.SourceEndFrame, "end frame", sequence.Name);
        ValidateNumber(sequence.FpsExpression, sequence.FPS, "frame rate", sequence.Name);
        ValidateNumber(sequence.WeightTimeExpression, sequence.WeightTime, "weight time", sequence.Name);

        if (sequence.WeightModeExpression is not null)
        {
            if (!int.TryParse(sequence.WeightModeExpression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode) ||
                mode != sequence.WeightMode)
            {
                throw Invalid(sequence.Name, "weight mode", sequence.WeightModeExpression);
            }
        }
    }

    public static void ValidateEvent(AnimationSequenceUse sequence, AnimationEvent animationEvent)
    {
        ValidateNumber(animationEvent.FrameExpression, animationEvent.LocalFrame, "event frame", sequence.Name);

        if (!animationEvent.EventId.HasValue ||
            animationEvent.EventId.Value is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException($"SeqTrack '{sequence.Name}' has an event without a numeric ID from 0 to 65535.");
        }

        string idExpression = (animationEvent.IdExpression ?? string.Empty).Trim();
        if (idExpression.Length > 0)
        {
            bool matches = int.TryParse(idExpression, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId)
                ? numericId == animationEvent.EventId.Value
                : Dl1AnimationEventCatalog.FindEvent(idExpression)?.Id == animationEvent.EventId.Value;
            if (!matches)
            {
                throw Invalid(sequence.Name, "event ID", idExpression);
            }
        }

        if (!animationEvent.RequiredSlot.HasValue)
        {
            throw new InvalidDataException($"SeqTrack '{sequence.Name}' has an event without a numeric slot.");
        }

        string slotExpression = (animationEvent.SlotExpression ?? string.Empty).Trim();
        if (slotExpression.Length > 0 &&
            (!short.TryParse(slotExpression, NumberStyles.Integer, CultureInfo.InvariantCulture, out short slot) ||
             slot != animationEvent.RequiredSlot.Value))
        {
            throw Invalid(sequence.Name, "event slot", slotExpression);
        }

        _ = AnimationEventTiming.ToTicks(animationEvent.LocalFrame);
    }

    private static void ValidateNumber(string? expression, double value, string field, string sequenceName)
    {
        if (expression is null) return;
        if (!double.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed != value)
        {
            throw Invalid(sequenceName, field, expression);
        }
    }

    private static InvalidDataException Invalid(string sequenceName, string field, string expression) =>
        new($"SeqTrack '{sequenceName}' has an unresolved or inconsistent {field} expression '{expression}'.");
}
