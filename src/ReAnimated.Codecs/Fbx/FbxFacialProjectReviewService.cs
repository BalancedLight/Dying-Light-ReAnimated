using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxFacialProjectReviewRequest
{
    public required string SourcePath { get; init; }

    public required FbxFacialAnimationImportResult Import { get; init; }

    /// <summary>
    /// The authoritative body/project timeline. The service never creates or
    /// resamples an independent facial timeline.
    /// </summary>
    public required AnimationTiming BodyTiming { get; init; }

    public required Dl1MimicProfile Profile { get; init; }

    public required RigDefinition ExactTargetRig { get; init; }
}

public sealed record FbxFacialProjectSourceChannel(
    string Name,
    ImmutableArray<string> Aliases,
    FbxFacialSourceValueUnit SourceValueUnit,
    double SourceToAuthoredScale,
    bool IsAnimated);

/// <summary>
/// A bounded set of automatic mapping suggestions ready to be displayed and
/// persisted for author review. Suggestions intentionally remain unlocked and
/// unreviewed, so ProjectMorphBindingResolver rejects them for export.
/// </summary>
public sealed record FbxFacialProjectReview
{
    internal FbxFacialProjectReview(
        AnimationTiming timing,
        string profileId,
        string mappingFingerprint,
        Dl1MimicSourceScan sourceScan,
        ImmutableArray<FbxFacialProjectSourceChannel> sourceChannels,
        ImmutableArray<ProjectMorphBinding> suggestedBindings,
        ImmutableArray<string> unmappedAnimatedChannels)
    {
        Timing = timing;
        ProfileId = profileId;
        MappingFingerprint = mappingFingerprint;
        SourceScan = sourceScan;
        SourceChannels = sourceChannels;
        SuggestedBindings = suggestedBindings;
        UnmappedAnimatedChannels = unmappedAnimatedChannels;
    }

    public AnimationTiming Timing { get; }

    public string ProfileId { get; }

    public string MappingFingerprint { get; }

    public Dl1MimicSourceScan SourceScan { get; }

    public ImmutableArray<FbxFacialProjectSourceChannel> SourceChannels
    {
        get;
    }

    public ImmutableArray<ProjectMorphBinding> SuggestedBindings { get; }

    public ImmutableArray<string> UnmappedAnimatedChannels { get; }

    /// <summary>
    /// Adds this review to an otherwise unmapped project animation. Existing
    /// facial review state is never overwritten implicitly.
    /// </summary>
    public ProjectAnimation ApplyTo(ProjectAnimation animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        if (animation.FrameRate != Timing.FrameRate ||
            animation.FrameCount != Timing.FrameCount)
        {
            throw new InvalidDataException(
                $"Project animation '{animation.Name}' uses " +
                $"{animation.FrameRate.Numerator}/" +
                $"{animation.FrameRate.Denominator} fps and " +
                $"{animation.FrameCount} frames, but the facial review is " +
                $"bound to {Timing.FrameRate.Numerator}/" +
                $"{Timing.FrameRate.Denominator} fps and " +
                $"{Timing.FrameCount} frames.");
        }

        if (!animation.MorphBindings.IsEmpty ||
            animation.MimicProfileId is not null ||
            animation.MimicMappingFingerprint is not null)
        {
            throw new InvalidOperationException(
                $"Project animation '{animation.Name}' already contains " +
                "facial mapping review state; automatic suggestions will not " +
                "overwrite it.");
        }

        return animation with
        {
            MimicProfileId = ProfileId,
            MimicMappingFingerprint = MappingFingerprint,
            MorphBindings = SuggestedBindings,
        };
    }
}

/// <summary>
/// Converts an explicitly-unitized FBX facial import into conservative DL1
/// mapping suggestions bound to an exact retail rig and an existing body
/// timeline.
/// </summary>
public static class FbxFacialProjectReviewService
{
    public const string FingerprintAlgorithm =
        "dlra-fbx-facial-project-review-v1";

    public static FbxFacialProjectReview Create(
        FbxFacialProjectReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentNullException.ThrowIfNull(request.Import);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.ExactTargetRig);
        cancellationToken.ThrowIfCancellationRequested();

        AnimationClip facialClip = request.Import.Clip;
        if (!request.BodyTiming.IsCompatibleWith(facialClip))
        {
            throw new InvalidDataException(
                $"FBX facial take '{facialClip.Name}' uses " +
                $"{facialClip.FrameRate.Numerator}/" +
                $"{facialClip.FrameRate.Denominator} fps and " +
                $"{facialClip.FrameCount} frames, but the supplied body " +
                $"timeline uses {request.BodyTiming.FrameRate.Numerator}/" +
                $"{request.BodyTiming.FrameRate.Denominator} fps and " +
                $"{request.BodyTiming.FrameCount} frames. Explicitly resample " +
                "the facial take to the body timeline before mapping it.");
        }

        Dictionary<string, ScalarTrack> tracks = facialClip.ScalarTracks
            .ToDictionary(
                static track => track.ChannelName,
                StringComparer.OrdinalIgnoreCase);
        var sourceCurves =
            ImmutableArray.CreateBuilder<Dl1MimicSourceCurve>(
                request.Import.Channels.Length);
        var sourceReviews =
            ImmutableArray.CreateBuilder<FbxFacialProjectSourceChannel>(
                request.Import.Channels.Length);
        var sourceUnits =
            new Dictionary<string, FbxFacialSourceValueUnit>(
                StringComparer.OrdinalIgnoreCase);
        foreach (FbxFacialChannel channel in request.Import.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tracks.TryGetValue(
                    channel.Name,
                    out ScalarTrack? track))
            {
                throw new InvalidDataException(
                    $"FBX facial channel '{channel.Name}' has no sampled " +
                    "authored scalar track.");
            }

            if (track.Keyframes.Length != request.BodyTiming.FrameCount)
            {
                throw new InvalidDataException(
                    $"FBX facial channel '{channel.Name}' has " +
                    $"{track.Keyframes.Length} samples; expected exactly " +
                    $"{request.BodyTiming.FrameCount} body-timeline samples.");
            }

            sourceCurves.Add(
                new Dl1MimicSourceCurve(
                    channel.Name,
                    track.Keyframes.Select(static key => key.Value),
                    channel.Aliases,
                    channel.Animated));
            sourceReviews.Add(
                new FbxFacialProjectSourceChannel(
                    channel.Name,
                    channel.Aliases,
                    channel.SourceValueUnit,
                    channel.SourceToAuthoredScale,
                    channel.Animated));
            sourceUnits.Add(
                channel.Name,
                channel.SourceValueUnit);
        }

        var sourceScan = new Dl1MimicSourceScan(
            request.SourcePath,
            request.Import.AnimationStack.Name,
            request.BodyTiming.FrameRate,
            request.BodyTiming.FrameCount,
            sourceCurves.MoveToImmutable());
        ImmutableArray<Dl1MimicMappingRow> rows =
            Dl1MimicAutoMapper.AutoMap(
                sourceScan.Curves,
                request.Profile);
        if (rows.Length > Dl1MimicBuilder.MaximumMappingRowCount)
        {
            throw new InvalidDataException(
                $"FBX facial auto-mapping produced {rows.Length} suggestions; " +
                $"the bounded limit is " +
                $"{Dl1MimicBuilder.MaximumMappingRowCount}.");
        }

        Dictionary<uint, ImmutableArray<MorphChannelDefinition>>
            exactTargets = request.ExactTargetRig.MorphChannels
                .Where(static morph => morph.DescriptorHash.HasValue)
                .GroupBy(static morph => morph.DescriptorHash!.Value)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray());
        var suggestions =
            ImmutableArray.CreateBuilder<ProjectMorphBinding>(rows.Length);
        foreach (Dl1MimicMappingRow row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Profile.FindTarget(row.TargetDescriptor) is null)
            {
                throw new InvalidDataException(
                    $"Auto-mapping produced descriptor " +
                    $"0x{row.TargetDescriptor:X8}, which is absent from " +
                    $"profile '{request.Profile.ProfileId}'.");
            }

            if (!exactTargets.TryGetValue(
                    row.TargetDescriptor,
                    out ImmutableArray<MorphChannelDefinition> candidates) ||
                candidates.Length != 1)
            {
                throw new InvalidDataException(
                    $"DL1 mimic descriptor 0x{row.TargetDescriptor:X8} must " +
                    $"resolve to exactly one morph on exact retail rig " +
                    $"'{request.ExactTargetRig.Id}' before it can become a " +
                    "project review suggestion.");
            }

            if (!sourceUnits.TryGetValue(
                    row.Source,
                    out FbxFacialSourceValueUnit sourceUnit))
            {
                throw new InvalidDataException(
                    $"Auto-mapping refers to unknown FBX facial source " +
                    $"channel '{row.Source}'.");
            }

            suggestions.Add(
                new ProjectMorphBinding
                {
                    SourceChannel = row.Source,
                    SourceValueUnit = ToProjectUnit(sourceUnit),
                    TargetMorph = candidates[0].Name,
                    TargetDescriptorHash = row.TargetDescriptor,
                    Weight = row.Weight,
                    Bias = row.Bias,
                    Enabled = row.Enabled,
                    Confidence = row.Confidence,
                    Method = row.Method,
                    IsReviewed = false,
                    IsLocked = false,
                });
        }

        ImmutableArray<ProjectMorphBinding> bindingArray =
            suggestions.MoveToImmutable();
        HashSet<string> mappedChannels = bindingArray
            .Select(static binding => binding.SourceChannel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<string> unmapped = sourceScan.Curves
            .Where(curve =>
                curve.IsAnimated &&
                !mappedChannels.Contains(curve.Name))
            .Select(static curve => curve.Name)
            .ToImmutableArray();
        string fingerprint = ComputeMappingFingerprint(
            request.Profile.ProfileId,
            request.ExactTargetRig,
            request.BodyTiming,
            bindingArray);

        return new FbxFacialProjectReview(
            request.BodyTiming,
            request.Profile.ProfileId,
            fingerprint,
            sourceScan,
            sourceReviews.MoveToImmutable(),
            bindingArray,
            unmapped);
    }

    private static ProjectMorphSourceValueUnit ToProjectUnit(
        FbxFacialSourceValueUnit unit) =>
        unit switch
        {
            FbxFacialSourceValueUnit.Normalized =>
                ProjectMorphSourceValueUnit.Normalized,
            FbxFacialSourceValueUnit.Percent =>
                ProjectMorphSourceValueUnit.Percent,
            _ => throw new InvalidDataException(
                "Every FBX facial channel must carry an explicit source " +
                "value unit before project review."),
        };

    public static string ComputeMappingFingerprint(
        string profileId,
        RigDefinition exactTargetRig,
        AnimationTiming timing,
        IEnumerable<ProjectMorphBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(exactTargetRig);
        ArgumentNullException.ThrowIfNull(bindings);
        if (timing.FrameRate.Numerator <= 0 ||
            timing.FrameRate.Denominator <= 0 ||
            timing.FrameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timing),
                "Facial mapping fingerprints require a valid body timeline.");
        }

        ProjectMorphBinding[] bindingArray = bindings.ToArray();
        if (bindingArray.Length >
            Dl1MimicBuilder.MaximumMappingRowCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindings),
                $"A facial mapping fingerprint cannot contain more than " +
                $"{Dl1MimicBuilder.MaximumMappingRowCount} rows.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendString(hash, FingerprintAlgorithm);
        AppendString(hash, profileId);
        AppendString(hash, RigSignature.Compute(exactTargetRig));
        AppendInt32(hash, timing.FrameRate.Numerator);
        AppendInt32(hash, timing.FrameRate.Denominator);
        AppendInt64(hash, timing.FrameCount);
        ProjectMorphBinding[] ordered = bindingArray
            .OrderBy(
                static binding => binding.SourceChannel,
                StringComparer.Ordinal)
            .ThenBy(static binding =>
                binding.TargetDescriptorHash ?? uint.MaxValue)
            .ThenBy(
                static binding => binding.TargetMorph,
                StringComparer.Ordinal)
            .ToArray();
        AppendInt32(hash, ordered.Length);
        foreach (ProjectMorphBinding binding in ordered)
        {
            AppendString(hash, binding.SourceChannel);
            AppendInt32(hash, (int)binding.SourceValueUnit);
            AppendString(hash, binding.TargetMorph);
            AppendUInt32(
                hash,
                binding.TargetDescriptorHash ?? uint.MaxValue);
            AppendInt64(
                hash,
                BitConverter.DoubleToInt64Bits(binding.Weight));
            AppendInt64(
                hash,
                BitConverter.DoubleToInt64Bits(binding.Bias));
            AppendInt32(hash, binding.Enabled ? 1 : 0);
            AppendInt64(
                hash,
                BitConverter.DoubleToInt64Bits(binding.Confidence));
            AppendString(hash, binding.Method);
            AppendInt32(hash, binding.IsReviewed ? 1 : 0);
            AppendInt32(hash, binding.IsLocked ? 1 : 0);
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(
        IncrementalHash hash,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(
        IncrementalHash hash,
        long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
