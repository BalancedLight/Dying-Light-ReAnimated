using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

public enum PlaybackMode
{
    Clamp,
    Loop,
}

public readonly record struct FrameRate
{
    [JsonConstructor]
    public FrameRate(int numerator, int denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);

        int divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    public double FramesPerSecond => (double)Numerator / Denominator;

    public double SecondsForFrame(double frame)
    {
        if (!double.IsFinite(frame))
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        return frame * Denominator / Numerator;
    }

    public double FrameForSeconds(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        return seconds * Numerator / Denominator;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }
}

public sealed record TransformKeyframe
{
    public TransformKeyframe(double frame, TransformTRS value)
    {
        if (!double.IsFinite(frame) || frame < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (!value.IsFinite)
        {
            throw new ArgumentException("Keyframe transforms must be finite.", nameof(value));
        }

        Frame = frame;
        Value = value.Normalized();
    }

    public double Frame { get; }

    public TransformTRS Value { get; }
}

public sealed record ScalarKeyframe
{
    public ScalarKeyframe(double frame, double value)
    {
        if (!double.IsFinite(frame) || frame < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Frame = frame;
        Value = value;
    }

    public double Frame { get; }

    public double Value { get; }
}

public sealed class TransformTrack
{
    public TransformTrack(
        int boneIndex,
        IEnumerable<TransformKeyframe> keyframes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boneIndex);

        ArgumentNullException.ThrowIfNull(keyframes);
        ImmutableArray<TransformKeyframe> array = keyframes.ToImmutableArray();
        ValidateFrames(array, nameof(keyframes));

        BoneIndex = boneIndex;
        Keyframes = array;
    }

    public int BoneIndex { get; }

    public ImmutableArray<TransformKeyframe> Keyframes { get; }

    public TransformTRS Sample(double frame)
    {
        if (!double.IsFinite(frame))
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (Keyframes.IsEmpty)
        {
            throw new InvalidOperationException("An empty transform track cannot be sampled.");
        }

        int upperIndex = FindUpperKeyframe(Keyframes, frame);
        if (upperIndex <= 0)
        {
            return Keyframes[0].Value;
        }

        if (upperIndex >= Keyframes.Length)
        {
            return Keyframes[^1].Value;
        }

        TransformKeyframe lower = Keyframes[upperIndex - 1];
        TransformKeyframe upper = Keyframes[upperIndex];
        double amount = (frame - lower.Frame) / (upper.Frame - lower.Frame);
        return TransformTRS.Interpolate(lower.Value, upper.Value, amount);
    }

    private static int FindUpperKeyframe(
        ImmutableArray<TransformKeyframe> keyframes,
        double frame)
    {
        int low = 0;
        int high = keyframes.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (keyframes[middle].Frame <= frame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static void ValidateFrames(
        ImmutableArray<TransformKeyframe> keyframes,
        string parameterName)
    {
        if (keyframes.IsEmpty)
        {
            throw new ArgumentException("A transform track requires at least one keyframe.", parameterName);
        }

        for (int index = 1; index < keyframes.Length; index++)
        {
            if (keyframes[index].Frame <= keyframes[index - 1].Frame)
            {
                throw new ArgumentException(
                    "Transform keyframes must be strictly increasing.",
                    parameterName);
            }
        }
    }
}

public sealed class ScalarTrack
{
    public ScalarTrack(
        string channelName,
        IEnumerable<ScalarKeyframe> keyframes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ArgumentNullException.ThrowIfNull(keyframes);

        ImmutableArray<ScalarKeyframe> array = keyframes.ToImmutableArray();
        if (array.IsEmpty)
        {
            throw new ArgumentException("A scalar track requires at least one keyframe.", nameof(keyframes));
        }

        for (int index = 1; index < array.Length; index++)
        {
            if (array[index].Frame <= array[index - 1].Frame)
            {
                throw new ArgumentException(
                    "Scalar keyframes must be strictly increasing.",
                    nameof(keyframes));
            }
        }

        ChannelName = channelName;
        Keyframes = array;
    }

    public string ChannelName { get; }

    public ImmutableArray<ScalarKeyframe> Keyframes { get; }

    public double Sample(double frame)
    {
        if (!double.IsFinite(frame))
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (frame <= Keyframes[0].Frame)
        {
            return Keyframes[0].Value;
        }

        if (frame >= Keyframes[^1].Frame)
        {
            return Keyframes[^1].Value;
        }

        int low = 1;
        int high = Keyframes.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (Keyframes[middle].Frame <= frame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        int upperIndex = low;
        ScalarKeyframe lower = Keyframes[upperIndex - 1];
        ScalarKeyframe upper = Keyframes[upperIndex];
        double amount = (frame - lower.Frame) / (upper.Frame - lower.Frame);
        return lower.Value + ((upper.Value - lower.Value) * amount);
    }
}

/// <summary>
/// A sampled animation domain independent of FBX or ANM2 storage.
/// </summary>
public sealed class AnimationClip
{
    private readonly ImmutableDictionary<int, TransformTrack> _transformTracks;
    private readonly ImmutableDictionary<string, ScalarTrack> _scalarTracks;

    public AnimationClip(
        string name,
        FrameRate frameRate,
        long frameCount,
        IEnumerable<TransformTrack>? transformTracks = null,
        IEnumerable<ScalarTrack>? scalarTracks = null,
        IEnumerable<AuxiliaryTransformTrack>? auxiliaryTransformTracks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCount);

        ImmutableArray<TransformTrack> transforms =
            transformTracks?.ToImmutableArray() ?? [];
        ImmutableArray<ScalarTrack> scalars =
            scalarTracks?.ToImmutableArray() ?? [];
        ImmutableArray<AuxiliaryTransformTrack> auxiliary =
            auxiliaryTransformTracks?.ToImmutableArray() ?? [];

        if (transforms.Any(track => track.Keyframes[^1].Frame > frameCount - 1) ||
            scalars.Any(track => track.Keyframes[^1].Frame > frameCount - 1) ||
            auxiliary.Any(track => track.Keyframes[^1].Frame > frameCount - 1))
        {
            throw new ArgumentException("Clip keyframes cannot exceed the final frame.");
        }

        Name = name;
        FrameRate = frameRate;
        FrameCount = frameCount;
        TransformTracks = transforms;
        ScalarTracks = scalars;
        AuxiliaryTransformTracks = auxiliary;
        _transformTracks = transforms.ToImmutableDictionary(static track => track.BoneIndex);
        _scalarTracks = scalars.ToImmutableDictionary(
            static track => track.ChannelName,
            StringComparer.OrdinalIgnoreCase);
        if (auxiliary.Select(static track => track.Descriptor).Distinct().Count() !=
            auxiliary.Length)
        {
            throw new ArgumentException(
                "Clip auxiliary transform descriptors must be unique.",
                nameof(auxiliaryTransformTracks));
        }
    }

    public string Name { get; }

    public FrameRate FrameRate { get; }

    public long FrameCount { get; }

    public double DurationSeconds =>
        FrameRate.SecondsForFrame(Math.Max(0, FrameCount - 1));

    public ImmutableArray<TransformTrack> TransformTracks { get; }

    public ImmutableArray<ScalarTrack> ScalarTracks { get; }

    public ImmutableArray<AuxiliaryTransformTrack> AuxiliaryTransformTracks { get; }

    public double ResolveFrame(double timeSeconds, PlaybackMode playbackMode)
    {
        double frame = FrameRate.FrameForSeconds(timeSeconds);
        double finalFrame = FrameCount - 1;

        if (playbackMode == PlaybackMode.Clamp || finalFrame <= 0.0)
        {
            return Math.Clamp(frame, 0.0, finalFrame);
        }

        double wrapped = frame % finalFrame;
        return wrapped < 0.0 ? wrapped + finalFrame : wrapped;
    }

    public SkeletonPose SamplePose(
        RigDefinition rig,
        double timeSeconds,
        PlaybackMode playbackMode = PlaybackMode.Clamp)
    {
        ArgumentNullException.ThrowIfNull(rig);
        double frame = ResolveFrame(timeSeconds, playbackMode);
        var locals = ImmutableArray.CreateBuilder<TransformTRS>(rig.BoneCount);

        for (int index = 0; index < rig.BoneCount; index++)
        {
            locals.Add(
                _transformTracks.TryGetValue(index, out TransformTrack? track)
                    ? track.Sample(frame)
                    : rig.Bones[index].LocalBindPose);
        }

        foreach (int boneIndex in _transformTracks.Keys)
        {
            if (boneIndex >= rig.BoneCount)
            {
                throw new InvalidOperationException(
                    $"Animation track {boneIndex} is outside rig '{rig.Id}'.");
            }
        }

        return new SkeletonPose(rig, locals.MoveToImmutable());
    }

    public ImmutableDictionary<string, double> SampleScalars(
        double timeSeconds,
        PlaybackMode playbackMode = PlaybackMode.Clamp)
    {
        double frame = ResolveFrame(timeSeconds, playbackMode);
        return _scalarTracks.ToImmutableDictionary(
            static pair => pair.Key,
            pair => pair.Value.Sample(frame),
            StringComparer.OrdinalIgnoreCase);
    }
}
