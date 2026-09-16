using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Metadata for a derived animation payload; source clips remain authoritative.</summary>
public sealed record DerivedMotionReference
{
    public Guid SourceClipId { get; init; }
    public string SourceClipFingerprint { get; init; } = string.Empty;
    public string SourceFileSha256 { get; init; } = string.Empty;
    public string SourceRigSignature { get; init; } = string.Empty;
    public string TargetRigSignature { get; init; } = string.Empty;
    public string AlgorithmId { get; init; } = string.Empty;
    public int SampleMultiplier { get; init; } = 1;
    public string PayloadSha256 { get; init; } = string.Empty;
    public long PayloadLength { get; init; }
    public double? MaximumPositionError { get; init; }
    /// <summary>Maximum measured basis-axis angular error in radians.</summary>
    public double? MaximumAngularError { get; init; }
    public double? MaximumLinearError { get; init; }

    public void Validate()
    {
        RigContractRules.Identifier(SourceClipId, nameof(SourceClipId));
        RigContractRules.Hash(SourceClipFingerprint, nameof(SourceClipFingerprint));
        RigContractRules.Hash(SourceFileSha256, nameof(SourceFileSha256));
        RigContractRules.Hash(SourceRigSignature, nameof(SourceRigSignature));
        RigContractRules.Hash(TargetRigSignature, nameof(TargetRigSignature));
        RigContractRules.Text(AlgorithmId, nameof(AlgorithmId));
        if (Encoding.UTF8.GetByteCount(AlgorithmId) > 256)
            throw new ArgumentOutOfRangeException(nameof(AlgorithmId), "Derived animation algorithm identifiers are limited to 256 UTF-8 bytes.");
        if (SampleMultiplier is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(SampleMultiplier), "Derived animation sample multipliers must be in the range 1..8.");
        RigContractRules.Hash(PayloadSha256, nameof(PayloadSha256));
        if (PayloadLength <= 0 || PayloadLength > DerivedAnimationDataCodec.MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(PayloadLength), "Derived animation payload lengths exceed the supported bound.");
        ValidateMeasurement(MaximumPositionError, nameof(MaximumPositionError));
        ValidateMeasurement(MaximumAngularError, nameof(MaximumAngularError));
        ValidateMeasurement(MaximumLinearError, nameof(MaximumLinearError));
    }

    private static void ValidateMeasurement(double? value, string parameterName)
    {
        if (value is < 0 || value is { } measured && !double.IsFinite(measured))
            throw new ArgumentOutOfRangeException(parameterName, "Derived animation measurements must be finite and nonnegative.");
    }
}

public sealed record DerivedTransformTrack
{
    public string BoneName { get; init; } = string.Empty;
    public ImmutableArray<TransformKeyframe> Keyframes { get; init; } = [];

    public DerivedTransformTrack() { }
    public DerivedTransformTrack(string boneName, ImmutableArray<TransformKeyframe> keyframes) =>
        (BoneName, Keyframes) = (boneName, keyframes);
}

public sealed record DerivedScalarTrack
{
    public string ChannelName { get; init; } = string.Empty;
    public ImmutableArray<ScalarKeyframe> Keyframes { get; init; } = [];

    public DerivedScalarTrack() { }
    public DerivedScalarTrack(string channelName, ImmutableArray<ScalarKeyframe> keyframes) =>
        (ChannelName, Keyframes) = (channelName, keyframes);
}

public sealed record DerivedAuxiliaryTrack
{
    public uint Descriptor { get; init; }
    public ImmutableArray<TransformKeyframe> Keyframes { get; init; } = [];

    public DerivedAuxiliaryTrack() { }
    public DerivedAuxiliaryTrack(uint descriptor, ImmutableArray<TransformKeyframe> keyframes) =>
        (Descriptor, Keyframes) = (descriptor, keyframes);
}

/// <summary>
/// Portable derived animation samples. It contains no source FBX or base
/// geometry and remains inspectable when its target rig signature is stale.
/// </summary>
public sealed record DerivedAnimationData
{
    public Guid ClipId { get; init; }
    public string Name { get; init; } = string.Empty;
    public FrameRate FrameRate { get; init; } = new(30, 1);
    public long FrameCount { get; init; }
    public ImmutableArray<DerivedTransformTrack> TransformTracks { get; init; } = [];
    public ImmutableArray<DerivedScalarTrack> ScalarTracks { get; init; } = [];
    public ImmutableArray<DerivedAuxiliaryTrack> AuxiliaryTracks { get; init; } = [];

    public DerivedAnimationData() { }

    public DerivedAnimationData(
        Guid clipId,
        string name,
        FrameRate frameRate,
        long frameCount,
        ImmutableArray<DerivedTransformTrack> transformTracks = default,
        ImmutableArray<DerivedScalarTrack> scalarTracks = default,
        ImmutableArray<DerivedAuxiliaryTrack> auxiliaryTracks = default) =>
        (ClipId, Name, FrameRate, FrameCount, TransformTracks, ScalarTracks, AuxiliaryTracks) =
        (clipId, name, frameRate, frameCount,
            transformTracks.IsDefault ? [] : transformTracks,
            scalarTracks.IsDefault ? [] : scalarTracks,
            auxiliaryTracks.IsDefault ? [] : auxiliaryTracks);

    public void Validate()
    {
        RigContractRules.Identifier(ClipId, nameof(ClipId));
        RigContractRules.Text(Name, nameof(Name));
        if (FrameRate.Numerator <= 0 || FrameRate.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(FrameRate), "Derived animation frame rates must be positive.");
        if (FrameCount <= 0 || FrameCount > DerivedAnimationDataCodec.MaximumFrameCount)
            throw new ArgumentOutOfRangeException(nameof(FrameCount), "Derived animation frame counts exceed the supported bound.");
        RigContractRules.Array(TransformTracks, nameof(TransformTracks));
        RigContractRules.Array(ScalarTracks, nameof(ScalarTracks));
        RigContractRules.Array(AuxiliaryTracks, nameof(AuxiliaryTracks));
        if (TransformTracks.Length > DerivedAnimationDataCodec.MaximumTrackCount ||
            ScalarTracks.Length > DerivedAnimationDataCodec.MaximumTrackCount ||
            AuxiliaryTracks.Length > DerivedAnimationDataCodec.MaximumTrackCount)
            throw new ArgumentOutOfRangeException(nameof(TransformTracks), "Derived animation track counts exceed the supported bound.");
        if (TransformTracks.Select(static track => track?.BoneName).Distinct(StringComparer.Ordinal).Count() != TransformTracks.Length)
            throw new ArgumentException("Derived transform target names must be unique.", nameof(TransformTracks));
        if (ScalarTracks.Select(static track => track?.ChannelName).Distinct(StringComparer.Ordinal).Count() != ScalarTracks.Length)
            throw new ArgumentException("Derived scalar target names must be unique.", nameof(ScalarTracks));
        if (AuxiliaryTracks.Select(static track => track?.Descriptor).Distinct().Count() != AuxiliaryTracks.Length)
            throw new ArgumentException("Derived auxiliary descriptors must be unique.", nameof(AuxiliaryTracks));

        long keys = 0;
        foreach (DerivedTransformTrack track in TransformTracks)
        {
            if (track is null) throw new ArgumentException("Derived transform tracks cannot be null.", nameof(TransformTracks));
            RigContractRules.Text(track.BoneName, nameof(TransformTracks));
            ValidateTransformKeys(track.Keyframes, ref keys, nameof(TransformTracks));
        }
        foreach (DerivedScalarTrack track in ScalarTracks)
        {
            if (track is null) throw new ArgumentException("Derived scalar tracks cannot be null.", nameof(ScalarTracks));
            RigContractRules.Text(track.ChannelName, nameof(ScalarTracks));
            ValidateScalarKeys(track.Keyframes, ref keys, nameof(ScalarTracks));
        }
        foreach (DerivedAuxiliaryTrack track in AuxiliaryTracks)
        {
            if (track is null) throw new ArgumentException("Derived auxiliary tracks cannot be null.", nameof(AuxiliaryTracks));
            ValidateTransformKeys(track.Keyframes, ref keys, nameof(AuxiliaryTracks));
        }
        if (keys > DerivedAnimationDataCodec.MaximumKeyframeCount)
            throw new ArgumentOutOfRangeException(nameof(TransformTracks), "Derived animation keyframe counts exceed the supported bound.");
    }

    private void ValidateTransformKeys(ImmutableArray<TransformKeyframe> keyframes, ref long total, string parameterName)
    {
        RigContractRules.Array(keyframes, parameterName);
        if (keyframes.IsEmpty) throw new ArgumentException("Derived tracks require at least one keyframe.", parameterName);
        total = checked(total + keyframes.Length);
        for (int index = 0; index < keyframes.Length; index++)
        {
            TransformKeyframe? key = keyframes[index];
            if (key is null || !double.IsFinite(key.Frame) || key.Frame < 0 || key.Frame > FrameCount - 1 ||
                !key.Value.IsFinite || !double.IsFinite(key.Value.Scale.X) || !double.IsFinite(key.Value.Scale.Y) ||
                !double.IsFinite(key.Value.Scale.Z) || Math.Abs(key.Value.Scale.X) <= 1e-12 ||
                Math.Abs(key.Value.Scale.Y) <= 1e-12 || Math.Abs(key.Value.Scale.Z) <= 1e-12)
                throw new ArgumentException("Derived transform keyframes must contain finite nonsingular TRS values within the clip range.", parameterName);
            if (index > 0 && key.Frame <= keyframes[index - 1].Frame)
                throw new ArgumentException("Derived transform keyframes must be strictly increasing.", parameterName);
        }
    }

    private void ValidateScalarKeys(ImmutableArray<ScalarKeyframe> keyframes, ref long total, string parameterName)
    {
        RigContractRules.Array(keyframes, parameterName);
        if (keyframes.IsEmpty) throw new ArgumentException("Derived tracks require at least one keyframe.", parameterName);
        total = checked(total + keyframes.Length);
        for (int index = 0; index < keyframes.Length; index++)
        {
            ScalarKeyframe? key = keyframes[index];
            if (key is null || !double.IsFinite(key.Frame) || key.Frame < 0 || key.Frame > FrameCount - 1 || !double.IsFinite(key.Value))
                throw new ArgumentException("Derived scalar keyframes must contain finite values within the clip range.", parameterName);
            if (index > 0 && key.Frame <= keyframes[index - 1].Frame)
                throw new ArgumentException("Derived scalar keyframes must be strictly increasing.", parameterName);
        }
    }
}

public static class DerivedAnimationDataCodec
{
    public const int MaximumPayloadBytes = 256 * 1024 * 1024;
    public const int MaximumFrameCount = 8_000_000;
    public const int MaximumKeyframeCount = 8_000_000;
    public const int MaximumTrackCount = 1_000_000;

    public static ImmutableArray<byte> Serialize(DerivedAnimationData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Validate();
        DerivedAnimationData canonical = Canonical(data);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, CustomModelPackageSerializer.CreateSerializerOptions());
        if (bytes.Length > MaximumPayloadBytes)
            throw new CustomModelFormatException("The derived animation payload exceeds its supported size.");
        return ImmutableArray.Create(bytes);
    }

    public static DerivedAnimationData Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new CustomModelFormatException("The derived animation payload is empty or too large.");
        try
        {
            DerivedAnimationData data = JsonSerializer.Deserialize<DerivedAnimationData>(payload, CustomModelPackageSerializer.CreateSerializerOptions()) ??
                throw new CustomModelFormatException("The derived animation payload was empty.");
            data.Validate();
            return data;
        }
        catch (JsonException exception)
        {
            throw new CustomModelFormatException("The derived animation payload contains invalid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new CustomModelFormatException("The derived animation payload contains invalid domain state.", exception);
        }
    }

    public static string EntryPath(Guid clipId)
    {
        RigContractRules.Identifier(clipId, nameof(clipId));
        return $"animation/derived/{clipId:N}.json";
    }

    private static DerivedAnimationData Canonical(DerivedAnimationData data) => data with
    {
        TransformTracks = data.TransformTracks.OrderBy(static track => track.BoneName, StringComparer.Ordinal).ToImmutableArray(),
        ScalarTracks = data.ScalarTracks.OrderBy(static track => track.ChannelName, StringComparer.Ordinal).ToImmutableArray(),
        AuxiliaryTracks = data.AuxiliaryTracks.OrderBy(static track => track.Descriptor).ToImmutableArray(),
    };
}
