using System.Collections.Immutable;

namespace ReAnimated.Core.Domain;

/// <summary>
/// Combines a body animation and its separately stored DL1 mimic animation on
/// one exact rational timeline. This is the sole clip consumed by preview and
/// export after a mimic asset has been resolved.
/// </summary>
public static class AnimationClipSynchronization
{
    public static AnimationClip Synchronize(
        AnimationClip body,
        AnimationClip? mimic)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (mimic is null)
        {
            return body;
        }

        if (body.FrameRate != mimic.FrameRate ||
            body.FrameCount != mimic.FrameCount)
        {
            throw new ArgumentException(
                "Body and mimic animations must use the same rational frame rate and frame count.",
                nameof(mimic));
        }

        if (!mimic.TransformTracks.IsEmpty)
        {
            throw new ArgumentException(
                "A synchronized mimic animation may contain scalar channels only.",
                nameof(mimic));
        }

        ImmutableArray<ScalarTrack> scalars = body.ScalarTracks
            .AddRange(mimic.ScalarTracks);
        if (scalars
            .Select(static track => track.ChannelName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != scalars.Length)
        {
            throw new ArgumentException(
                "Body and mimic animations contain duplicate scalar channel names.",
                nameof(mimic));
        }

        return new AnimationClip(
            body.Name,
            body.FrameRate,
            body.FrameCount,
            body.TransformTracks,
            scalars,
            body.AuxiliaryTransformTracks);
    }

    /// <summary>
    /// Places an independently timed facial clip on the document timeline.
    /// Native cadence is sampled in seconds and values are neutral outside the
    /// authored source range unless another behavior was explicitly selected.
    /// </summary>
    public static AnimationClip Synchronize(
        AnimationClip body,
        AnimationClip? facial,
        FacialClipTiming timing)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(timing);
        if (facial is null)
        {
            return body;
        }

        if (!facial.TransformTracks.IsEmpty ||
            !facial.AuxiliaryTransformTracks.IsEmpty)
        {
            throw new ArgumentException(
                "An attached facial animation may contain scalar channels only.",
                nameof(facial));
        }

        timing.Validate(body.FrameCount);
        if (facial.FrameRate != timing.NativeFrameRate ||
            timing.SourceEndFrame >= facial.FrameCount)
        {
            throw new ArgumentException(
                "Facial timing does not match the attached clip's native cadence or range.",
                nameof(timing));
        }

        var resampled = ImmutableArray.CreateBuilder<ScalarTrack>(
            facial.ScalarTracks.Length);
        foreach (ScalarTrack track in facial.ScalarTracks)
        {
            var keys = new ScalarKeyframe[checked((int)body.FrameCount)];
            for (int documentFrame = 0; documentFrame < keys.Length; documentFrame++)
            {
                double sourceFrame = ResolveFacialFrame(
                    documentFrame,
                    body,
                    timing,
                    out bool neutral);
                keys[documentFrame] = new ScalarKeyframe(
                    documentFrame,
                    neutral ? 0.0 : track.Sample(sourceFrame));
            }

            resampled.Add(new ScalarTrack(track.ChannelName, keys));
        }

        ImmutableArray<ScalarTrack> scalars = body.ScalarTracks
            .AddRange(resampled.ToImmutable());
        if (scalars
            .Select(static track => track.ChannelName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != scalars.Length)
        {
            throw new ArgumentException(
                "Body and facial animations contain duplicate scalar channel names.",
                nameof(facial));
        }

        return new AnimationClip(
            body.Name,
            body.FrameRate,
            body.FrameCount,
            body.TransformTracks,
            scalars,
            body.AuxiliaryTransformTracks);
    }

    private static double ResolveFacialFrame(
        int documentFrame,
        AnimationClip body,
        FacialClipTiming timing,
        out bool neutral)
    {
        double relativeDocumentFrame =
            documentFrame - timing.TimelineOffsetFrames;
        double sourceFrame = timing.OutsideRangeBehavior ==
                FacialOutsideRangeBehavior.Stretch
            ? timing.SourceStartFrame +
              (relativeDocumentFrame /
               Math.Max(1.0, body.FrameCount - 1 - timing.TimelineOffsetFrames)) *
              (timing.SourceEndFrame - timing.SourceStartFrame)
            : timing.NativeFrameRate.FrameForSeconds(
                body.FrameRate.SecondsForFrame(relativeDocumentFrame));
        // Rational frame-rate conversion passes through seconds, so an exact
        // endpoint such as frame 2 at 30000/1001 can return a value a few
        // ulps above 2. Treat those representational errors as the authored
        // endpoint instead of incorrectly applying the outside-range policy.
        const double FrameBoundaryTolerance = 1e-9;
        if (sourceFrame >=
                timing.SourceStartFrame - FrameBoundaryTolerance &&
            sourceFrame <=
                timing.SourceEndFrame + FrameBoundaryTolerance)
        {
            sourceFrame = Math.Clamp(
                sourceFrame,
                timing.SourceStartFrame,
                timing.SourceEndFrame);
        }

        bool outside = sourceFrame < timing.SourceStartFrame ||
                       sourceFrame > timing.SourceEndFrame;
        neutral = false;
        if (!outside)
        {
            return sourceFrame;
        }

        switch (timing.OutsideRangeBehavior)
        {
            case FacialOutsideRangeBehavior.Neutral:
                neutral = true;
                return timing.SourceStartFrame;
            case FacialOutsideRangeBehavior.Hold:
            case FacialOutsideRangeBehavior.Stretch:
                return Math.Clamp(
                    sourceFrame,
                    timing.SourceStartFrame,
                    timing.SourceEndFrame);
            case FacialOutsideRangeBehavior.Loop:
            {
                double length =
                    timing.SourceEndFrame - timing.SourceStartFrame + 1.0;
                double wrapped =
                    (sourceFrame - timing.SourceStartFrame) % length;
                if (wrapped < 0.0)
                {
                    wrapped += length;
                }

                return Math.Min(
                    timing.SourceEndFrame,
                    timing.SourceStartFrame + wrapped);
            }
            default:
                throw new InvalidOperationException(
                    "The facial outside-range behavior is unsupported.");
        }
    }
}
