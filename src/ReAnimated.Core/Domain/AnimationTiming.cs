namespace ReAnimated.Core.Domain;

/// <summary>
/// The exact rational timeline shared by body and facial authoring data.
/// Timing compatibility is deliberately stricter than duration equality:
/// callers must explicitly resample before constructing a shared document.
/// </summary>
public readonly record struct AnimationTiming
{
    public AnimationTiming(
        FrameRate frameRate,
        long frameCount)
    {
        if (frameRate.Numerator <= 0 ||
            frameRate.Denominator <= 0)
        {
            throw new ArgumentException(
                "Animation timing requires a valid rational frame rate.",
                nameof(frameRate));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);
        FrameRate = frameRate;
        FrameCount = frameCount;
    }

    public FrameRate FrameRate { get; }

    public long FrameCount { get; }

    public double DurationSeconds =>
        FrameRate.SecondsForFrame(Math.Max(0, FrameCount - 1));

    public static AnimationTiming FromClip(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return new AnimationTiming(
            clip.FrameRate,
            clip.FrameCount);
    }

    public bool IsCompatibleWith(AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return FrameRate == clip.FrameRate &&
               FrameCount == clip.FrameCount;
    }
}
