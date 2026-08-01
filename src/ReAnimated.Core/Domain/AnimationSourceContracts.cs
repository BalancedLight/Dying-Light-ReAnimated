using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

public enum AnimationSourceKind
{
    LocalFbx,
    LocalAnm2,
    RetailAnm2,
}

[Flags]
public enum AnimationSourceRoles
{
    None = 0,
    Body = 1 << 0,
    Facial = 1 << 1,
    Auxiliary = 1 << 2,
}

public enum AnimationTimingProvenance
{
    EmbeddedFbx,
    ExactRetailAnimationScript,
    UserSpecified,
    Manual30FpsFallback,
}

public enum FacialOutsideRangeBehavior
{
    Neutral,
    Hold,
    Loop,
    Stretch,
}

/// <summary>
/// Native timing retained for an independently-authored facial source. The
/// source range is inclusive. Facial data is neutral outside that range unless
/// the author explicitly selects another behavior.
/// </summary>
public sealed record FacialClipTiming
{
    public FrameRate NativeFrameRate { get; init; } = new(30, 1);

    public long SourceStartFrame { get; init; }

    public long SourceEndFrame { get; init; }

    /// <summary>
    /// Offset on the body/document timeline, expressed in document frames.
    /// </summary>
    public double TimelineOffsetFrames { get; init; }

    public FacialOutsideRangeBehavior OutsideRangeBehavior { get; init; } =
        FacialOutsideRangeBehavior.Neutral;

    public static FacialClipTiming ForClip(AnimationClip clip) =>
        new()
        {
            NativeFrameRate = clip.FrameRate,
            SourceStartFrame = 0,
            SourceEndFrame = clip.FrameCount - 1,
        };

    public void Validate(long documentFrameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentFrameCount);
        if (NativeFrameRate.Numerator <= 0 ||
            NativeFrameRate.Denominator <= 0 ||
            SourceStartFrame < 0 ||
            SourceEndFrame < SourceStartFrame ||
            !double.IsFinite(TimelineOffsetFrames) ||
            !Enum.IsDefined(OutsideRangeBehavior))
        {
            throw new ArgumentException(
                "Facial clip timing contains an invalid native range, offset, rate, or outside-range behavior.");
        }
    }
}

/// <summary>
/// Immutable descriptor partition produced from one ANM2 decode. A descriptor
/// appears in exactly one set; collisions and duplicate source descriptors are
/// assigned to <see cref="AmbiguousDescriptors"/> and must be reviewed before
/// playback or export.
/// </summary>
public sealed record Anm2TrackPartition
{
    public ImmutableArray<uint> BodyDescriptors { get; init; } = [];

    public ImmutableArray<uint> MorphDescriptors { get; init; } = [];

    public ImmutableArray<uint> AuxiliaryDescriptors { get; init; } = [];

    public ImmutableArray<uint> UnresolvedDescriptors { get; init; } = [];

    public ImmutableArray<uint> AmbiguousDescriptors { get; init; } = [];

    public string Fingerprint { get; init; } = string.Empty;

    public AnimationSourceRoles Roles =>
        (BodyDescriptors.IsEmpty ? AnimationSourceRoles.None : AnimationSourceRoles.Body) |
        (MorphDescriptors.IsEmpty ? AnimationSourceRoles.None : AnimationSourceRoles.Facial) |
        (AuxiliaryDescriptors.IsEmpty ? AnimationSourceRoles.None : AnimationSourceRoles.Auxiliary);

    public bool RequiresReview => !AmbiguousDescriptors.IsEmpty;

    public void Validate()
    {
        if (BodyDescriptors.IsDefault ||
            MorphDescriptors.IsDefault ||
            AuxiliaryDescriptors.IsDefault ||
            UnresolvedDescriptors.IsDefault ||
            AmbiguousDescriptors.IsDefault)
        {
            throw new ArgumentException(
                "ANM2 descriptor partitions must be initialized.");
        }

        uint[] all = BodyDescriptors
            .AddRange(MorphDescriptors)
            .AddRange(AuxiliaryDescriptors)
            .AddRange(UnresolvedDescriptors)
            .AddRange(AmbiguousDescriptors)
            .ToArray();
        if (all.Distinct().Count() != all.Length)
        {
            throw new ArgumentException(
                "An ANM2 descriptor cannot belong to more than one partition.");
        }

        if (Fingerprint.Length != 64 ||
            Fingerprint.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The ANM2 partition fingerprint must be a SHA-256 value.");
        }
    }
}

/// <summary>
/// A descriptor-addressed transform that is sampled alongside, but never
/// inserted into, the deform skeleton. DL1's 0xCCC3CDDF motion accumulator is
/// represented by this contract.
/// </summary>
public sealed class AuxiliaryTransformTrack
{
    public AuxiliaryTransformTrack(
        uint descriptor,
        IEnumerable<TransformKeyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        ImmutableArray<TransformKeyframe> values = keyframes.ToImmutableArray();
        if (values.IsEmpty)
        {
            throw new ArgumentException(
                "An auxiliary transform track requires at least one keyframe.",
                nameof(keyframes));
        }

        for (int index = 1; index < values.Length; index++)
        {
            if (values[index].Frame <= values[index - 1].Frame)
            {
                throw new ArgumentException(
                    "Auxiliary transform keyframes must be strictly increasing.",
                    nameof(keyframes));
            }
        }

        Descriptor = descriptor;
        Keyframes = values;
    }

    public uint Descriptor { get; }

    public ImmutableArray<TransformKeyframe> Keyframes { get; }

    public TransformTRS Sample(double frame)
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

        TransformKeyframe lower = Keyframes[low - 1];
        TransformKeyframe upper = Keyframes[low];
        double amount = (frame - lower.Frame) / (upper.Frame - lower.Frame);
        return TransformTRS.Interpolate(lower.Value, upper.Value, amount);
    }
}
