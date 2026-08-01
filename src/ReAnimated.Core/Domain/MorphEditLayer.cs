using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ReAnimated.Core.Domain;

public enum MorphEditBlendMode
{
    Override,
    Additive,
}

public enum MorphEditLayerScope
{
    AuthoredExportable,
    PreviewOnly,
}

public sealed record MorphChannelBinding
{
    [JsonConstructor]
    public MorphChannelBinding(
        string sourceChannel,
        string targetMorph,
        double weight = 1,
        double bias = 0,
        bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceChannel);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetMorph);
        if (!double.IsFinite(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        if (!double.IsFinite(bias))
        {
            throw new ArgumentOutOfRangeException(nameof(bias));
        }

        SourceChannel = sourceChannel;
        TargetMorph = targetMorph;
        Weight = weight;
        Bias = bias;
        Enabled = enabled;
    }

    public string SourceChannel { get; }

    public string TargetMorph { get; }

    public double Weight { get; }

    public double Bias { get; }

    public bool Enabled { get; }
}

public sealed record MorphEditTrack
{
    private readonly ScalarTrack _track;

    public MorphEditTrack(
        string morphName,
        IEnumerable<ScalarKeyframe> keyframes)
        : this(
            morphName,
            keyframes?.ToImmutableArray() ??
                throw new ArgumentNullException(nameof(keyframes)))
    {
    }

    [JsonConstructor]
    public MorphEditTrack(
        string morphName,
        ImmutableArray<ScalarKeyframe> keyframes)
    {
        _track = new ScalarTrack(morphName, keyframes);
        MorphName = morphName;
        Keyframes = keyframes;
    }

    public string MorphName { get; }

    public ImmutableArray<ScalarKeyframe> Keyframes { get; }

    public double Sample(double frame) => _track.Sample(frame);
}

/// <summary>
/// A non-destructive facial layer. A FED expression is represented as one
/// constant key per referenced morph, while animated corrections can contain
/// a normal scalar-key sequence.
/// </summary>
public sealed record MorphEditLayer
{
    public MorphEditLayer(
        Guid id,
        string name,
        MorphEditBlendMode blendMode,
        MorphEditLayerScope scope,
        double weight,
        IEnumerable<MorphEditTrack> tracks,
        bool enabled = true)
        : this(
            id,
            name,
            blendMode,
            scope,
            weight,
            tracks?.ToImmutableArray() ??
                throw new ArgumentNullException(nameof(tracks)),
            enabled)
    {
    }

    [JsonConstructor]
    public MorphEditLayer(
        Guid id,
        string name,
        MorphEditBlendMode blendMode,
        MorphEditLayerScope scope,
        double weight,
        ImmutableArray<MorphEditTrack> tracks,
        bool enabled = true)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A morph layer requires a stable identifier.",
                nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!double.IsFinite(weight) || weight is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight),
                "Layer weight must be between zero and one.");
        }

        if (tracks.IsDefault)
        {
            throw new ArgumentException(
                "The morph-track collection cannot be uninitialized.",
                nameof(tracks));
        }

        if (tracks
            .Select(static track => track.MorphName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() != tracks.Length)
        {
            throw new ArgumentException(
                "A morph layer cannot contain duplicate target tracks.",
                nameof(tracks));
        }

        Id = id;
        Name = name;
        BlendMode = blendMode;
        Scope = scope;
        Weight = weight;
        Tracks = tracks;
        Enabled = enabled;
    }

    public Guid Id { get; }

    public string Name { get; }

    public MorphEditBlendMode BlendMode { get; }

    public MorphEditLayerScope Scope { get; }

    public double Weight { get; }

    public ImmutableArray<MorphEditTrack> Tracks { get; }

    public bool Enabled { get; }
}
