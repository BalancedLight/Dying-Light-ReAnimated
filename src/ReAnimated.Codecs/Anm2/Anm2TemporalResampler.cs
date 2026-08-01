using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Anm2;

public readonly record struct Anm2TemporalResamplePlan
{
    internal Anm2TemporalResamplePlan(
        int sourceFrameCount,
        double inputFramesPerSecond,
        double outputFramesPerSecond,
        int outputFrameCount)
    {
        SourceFrameCount = sourceFrameCount;
        InputFramesPerSecond = inputFramesPerSecond;
        OutputFramesPerSecond = outputFramesPerSecond;
        OutputFrameCount = outputFrameCount;
    }

    public int SourceFrameCount { get; }

    public double InputFramesPerSecond { get; }

    public double OutputFramesPerSecond { get; }

    public int OutputFrameCount { get; }

    public double DurationSeconds =>
        SourceFrameCount <= 1
            ? 0.0
            : (SourceFrameCount - 1) /
              InputFramesPerSecond;

    public double GetSourcePosition(int outputFrame)
    {
        if (outputFrame < 0 ||
            outputFrame >= OutputFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputFrame));
        }

        if (OutputFrameCount <= 1 ||
            SourceFrameCount <= 1)
        {
            return 0.0;
        }

        if (outputFrame == 0)
        {
            return 0.0;
        }

        if (outputFrame == OutputFrameCount - 1)
        {
            return SourceFrameCount - 1;
        }

        double cadenceRatio =
            InputFramesPerSecond /
            OutputFramesPerSecond;
        return Math.Clamp(
            outputFrame *
            cadenceRatio,
            0.0,
            SourceFrameCount - 1);
    }
}

public sealed record Anm2TemporalResampleResult(
    double Anm2InputFps,
    double FbxOutputFps,
    ImmutableArray<ImmutableArray<TransformTRS>> Frames)
{
    public int FrameCount => Frames.Length;

    public int BoneCount =>
        Frames.IsDefaultOrEmpty
            ? 0
            : Frames[0].Length;
}

public static class Anm2TemporalResampler
{
    public const long MaximumOutputTransformCount =
        1_000_000;

    public static Anm2TemporalResamplePlan CreatePlan(
        int sourceFrameCount,
        double inputFramesPerSecond,
        double outputFramesPerSecond)
    {
        if (sourceFrameCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceFrameCount),
                "An animation must contain at least one frame.");
        }

        ValidateRate(
            inputFramesPerSecond,
            nameof(inputFramesPerSecond));
        ValidateRate(
            outputFramesPerSecond,
            nameof(outputFramesPerSecond));
        if (sourceFrameCount == 1)
        {
            return new Anm2TemporalResamplePlan(
                1,
                inputFramesPerSecond,
                outputFramesPerSecond,
                1);
        }

        double duration =
            (sourceFrameCount - 1) /
            inputFramesPerSecond;
        double roundedFrameSpan = Math.Round(
            duration * outputFramesPerSecond,
            MidpointRounding.ToEven);
        if (!double.IsFinite(roundedFrameSpan) ||
            roundedFrameSpan >
            int.MaxValue - 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputFramesPerSecond),
                "The requested cadence produces too many frames.");
        }

        int outputFrameCount = Math.Max(
            2,
            checked((int)roundedFrameSpan + 1));
        return new Anm2TemporalResamplePlan(
            sourceFrameCount,
            inputFramesPerSecond,
            outputFramesPerSecond,
            outputFrameCount);
    }

    public static Anm2TemporalResampleResult Resample(
        ImmutableArray<ImmutableArray<TransformTRS>>
            sourceFrames,
        double inputFramesPerSecond,
        double outputFramesPerSecond,
        long maximumOutputTransformCount =
            MaximumOutputTransformCount) =>
        Resample(
            sourceFrames,
            inputFramesPerSecond,
            outputFramesPerSecond,
            maximumOutputTransformCount,
            CancellationToken.None);

    public static Anm2TemporalResampleResult Resample(
        ImmutableArray<ImmutableArray<TransformTRS>>
            sourceFrames,
        double inputFramesPerSecond,
        double outputFramesPerSecond,
        long maximumOutputTransformCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceFrames.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "An animation must contain at least one frame.",
                nameof(sourceFrames));
        }

        int boneCount = sourceFrames[0].Length;
        if (boneCount < 1 ||
            sourceFrames.Any(frame =>
                frame.IsDefault ||
                frame.Length != boneCount))
        {
            throw new ArgumentException(
                "Every source frame must contain the same nonzero bone count.",
                nameof(sourceFrames));
        }

        foreach (ImmutableArray<TransformTRS> frame in
                 sourceFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (TransformTRS transform in frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!transform.IsFinite ||
                    transform.Rotation.LengthSquared <=
                    1.0e-24)
                {
                    throw new ArgumentException(
                        "Source transforms must be finite and have nonsingular rotations.",
                        nameof(sourceFrames));
                }
            }
        }

        Anm2TemporalResamplePlan plan = CreatePlan(
            sourceFrames.Length,
            inputFramesPerSecond,
            outputFramesPerSecond);
        long outputTransformCount = checked(
            (long)plan.OutputFrameCount * boneCount);
        if (maximumOutputTransformCount < 1 ||
            outputTransformCount >
            maximumOutputTransformCount)
        {
            throw new InvalidDataException(
                $"Temporal resampling requires {outputTransformCount:N0} transforms; the configured bound is {maximumOutputTransformCount:N0}.");
        }

        var output =
            ImmutableArray.CreateBuilder<
                ImmutableArray<TransformTRS>>(
                plan.OutputFrameCount);
        TransformTRS[]? previous = null;
        for (int outputFrame = 0;
             outputFrame < plan.OutputFrameCount;
             outputFrame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sourcePosition =
                plan.GetSourcePosition(outputFrame);
            int lower = Math.Clamp(
                (int)Math.Floor(sourcePosition),
                0,
                sourceFrames.Length - 1);
            int upper = Math.Min(
                lower + 1,
                sourceFrames.Length - 1);
            double amount = sourcePosition - lower;
            var current = new TransformTRS[boneCount];
            for (int boneIndex = 0;
                 boneIndex < boneCount;
                 boneIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransformTRS sampled = upper == lower
                    ? sourceFrames[lower][boneIndex]
                        .Normalized()
                    : TransformTRS.Interpolate(
                        sourceFrames[lower][boneIndex],
                        sourceFrames[upper][boneIndex],
                        amount);
                current[boneIndex] = previous is null
                    ? sampled
                    : AlignRotationHemisphere(
                        sampled,
                        previous[boneIndex].Rotation);
            }

            output.Add(current.ToImmutableArray());
            previous = current;
        }

        return new Anm2TemporalResampleResult(
            inputFramesPerSecond,
            outputFramesPerSecond,
            output.MoveToImmutable());
    }

    public static TransformTRS AlignRotationHemisphere(
        TransformTRS value,
        QuaternionD reference)
    {
        QuaternionD rotation = value.Rotation.Normalized();
        QuaternionD referenceUnit = reference.Normalized();
        if (QuaternionD.Dot(referenceUnit, rotation) < 0.0)
        {
            rotation = -rotation;
        }

        return value with
        {
            Rotation = rotation,
        };
    }

    private static void ValidateRate(
        double value,
        string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Animation cadence must be finite and positive.");
        }
    }
}
