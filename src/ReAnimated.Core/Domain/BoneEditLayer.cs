using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ReAnimated.Core.Domain;

public enum BoneEditBlendMode
{
    Override,
    Additive,
}

/// <summary>
/// The bounded interpolation modes supported by authored bone-edit tracks.
/// Source animation tracks keep their own independent sampling contract.
/// </summary>
public enum BoneEditInterpolation
{
    /// <summary>
    /// Linearly interpolates translation and scale while using
    /// shortest-hemisphere normalized quaternion slerp for rotation.
    /// </summary>
    Linear,

    /// <summary>
    /// Holds the complete local TRS from the preceding key until the next
    /// keyed frame.
    /// </summary>
    Step,
}

/// <summary>
/// Explicitly separates edits that affect exported animation bytes from
/// temporary display-only adjustments.
/// </summary>
public enum BoneEditLayerScope
{
    AuthoredExportable,
    PreviewOnly,
}

public sealed record BoneEditTrack
{
    private readonly TransformTrack _track;

    public BoneEditTrack(
        int boneIndex,
        IEnumerable<TransformKeyframe> keyframes,
        BoneEditInterpolation interpolation = BoneEditInterpolation.Linear)
        : this(
            boneIndex,
            keyframes?.ToImmutableArray() ??
                throw new ArgumentNullException(nameof(keyframes)),
            interpolation)
    {
    }

    [JsonConstructor]
    public BoneEditTrack(
        int boneIndex,
        ImmutableArray<TransformKeyframe> keyframes,
        BoneEditInterpolation interpolation = BoneEditInterpolation.Linear)
    {
        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interpolation),
                interpolation,
                "The bone-edit interpolation mode is not supported.");
        }

        _track = new TransformTrack(boneIndex, keyframes);
        BoneIndex = boneIndex;
        Keyframes = keyframes;
        Interpolation = interpolation;
    }

    public int BoneIndex { get; }

    public ImmutableArray<TransformKeyframe> Keyframes { get; }

    /// <summary>
    /// Defaults to <see cref="BoneEditInterpolation.Linear"/> when absent
    /// from an earlier C# schema-1 project.
    /// </summary>
    public BoneEditInterpolation Interpolation { get; }

    public Mathematics.TransformTRS Sample(double frame) =>
        Interpolation switch
        {
            BoneEditInterpolation.Linear => _track.Sample(frame),
            BoneEditInterpolation.Step => SampleStep(frame),
            _ => throw new InvalidOperationException(
                $"Unsupported bone-edit interpolation mode '{Interpolation}'."),
        };

    private Mathematics.TransformTRS SampleStep(double frame)
    {
        if (!double.IsFinite(frame))
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        int low = 0;
        int high = Keyframes.Length;
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

        int heldIndex = Math.Clamp(low - 1, 0, Keyframes.Length - 1);
        return Keyframes[heldIndex].Value;
    }
}

/// <summary>
/// A non-destructive local-space edit layer. Additive rotations are
/// post-multiplied onto the underlying local rotation.
/// </summary>
public sealed record BoneEditLayer
{
    public BoneEditLayer(
        Guid id,
        string name,
        BoneEditBlendMode blendMode,
        BoneEditLayerScope scope,
        double weight,
        IEnumerable<BoneEditTrack> tracks,
        bool enabled = true,
        IReadOnlyDictionary<int, double>? boneMask = null)
        : this(
            id,
            name,
            blendMode,
            scope,
            weight,
            tracks?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(tracks)),
            enabled,
            boneMask?.ToImmutableDictionary() ??
                ImmutableDictionary<int, double>.Empty)
    {
    }

    [JsonConstructor]
    public BoneEditLayer(
        Guid id,
        string name,
        BoneEditBlendMode blendMode,
        BoneEditLayerScope scope,
        double weight,
        ImmutableArray<BoneEditTrack> tracks,
        bool enabled = true,
        ImmutableDictionary<int, double>? boneMask = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An edit layer requires a stable identifier.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!double.IsFinite(weight) || weight < 0.0 || weight > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Layer weight must be between zero and one.");
        }

        if (tracks.IsDefault)
        {
            throw new ArgumentException("The track collection cannot be uninitialized.", nameof(tracks));
        }

        if (tracks.Select(static track => track.BoneIndex).Distinct().Count() != tracks.Length)
        {
            throw new ArgumentException("An edit layer cannot contain duplicate bone tracks.", nameof(tracks));
        }

        ImmutableDictionary<int, double> mask =
            boneMask ?? ImmutableDictionary<int, double>.Empty;
        foreach ((int boneIndex, double maskWeight) in mask)
        {
            if (boneIndex < 0 ||
                !double.IsFinite(maskWeight) ||
                maskWeight is < 0 or > 1)
            {
                throw new ArgumentException(
                    "Bone-mask entries require non-negative indexes and weights from zero to one.",
                    nameof(boneMask));
            }
        }

        Id = id;
        Name = name;
        BlendMode = blendMode;
        Scope = scope;
        Weight = weight;
        Tracks = tracks;
        Enabled = enabled;
        BoneMask = mask;
    }

    public Guid Id { get; }

    public string Name { get; }

    public BoneEditBlendMode BlendMode { get; }

    public BoneEditLayerScope Scope { get; }

    public double Weight { get; }

    public ImmutableArray<BoneEditTrack> Tracks { get; }

    public bool Enabled { get; }

    /// <summary>
    /// Optional per-bone layer multipliers. Unlisted bones use full layer
    /// weight.
    /// </summary>
    public ImmutableDictionary<int, double> BoneMask { get; }
}
