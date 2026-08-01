using System.Collections.Immutable;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Anm2;

public sealed record Dl1MimicSourceCurve
{
    public Dl1MimicSourceCurve(
        string name,
        IEnumerable<double> values,
        IEnumerable<string>? aliases = null,
        bool isAnimated = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        ImmutableArray<double> valueArray = values.ToImmutableArray();
        if (valueArray.IsEmpty ||
            valueArray.Any(static value => !double.IsFinite(value)))
        {
            throw new ArgumentException(
                "A mimic source curve requires finite sampled values.",
                nameof(values));
        }

        Name = name;
        Values = valueArray;
        Aliases = aliases?.ToImmutableArray() ?? [name];
        IsAnimated = isAnimated;
    }

    public string Name { get; }

    public ImmutableArray<double> Values { get; }

    public ImmutableArray<string> Aliases { get; }

    public bool IsAnimated { get; }
}

/// <summary>
/// Format-neutral, already sampled facial source curves. FBX parsing is kept
/// outside this contract so all importers feed the same deterministic builder.
/// Values are expected to be normalized before construction.
/// </summary>
public sealed record Dl1MimicSourceScan
{
    public Dl1MimicSourceScan(
        string sourcePath,
        string animationStack,
        FrameRate frameRate,
        long frameCount,
        IEnumerable<Dl1MimicSourceCurve> curves,
        IEnumerable<string>? warnings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(animationStack);
        if (frameCount is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                "DL1 mimic source scans require 1..65535 frames.");
        }

        ArgumentNullException.ThrowIfNull(curves);
        ImmutableArray<Dl1MimicSourceCurve> curveArray =
            curves.ToImmutableArray();
        if (curveArray
                .Select(static curve => curve.Name)
                .Distinct(StringComparer.Ordinal)
                .Count() != curveArray.Length)
        {
            throw new ArgumentException(
                "Mimic source curve names must be unique.",
                nameof(curves));
        }

        foreach (Dl1MimicSourceCurve curve in curveArray)
        {
            if (curve.Values.Length != frameCount)
            {
                throw new ArgumentException(
                    $"Mimic source curve '{curve.Name}' has {curve.Values.Length} samples; " +
                    $"expected {frameCount}.",
                    nameof(curves));
            }
        }

        SourcePath = sourcePath;
        AnimationStack = animationStack;
        FrameRate = frameRate;
        FrameCount = frameCount;
        Curves = curveArray;
        Warnings = warnings?.ToImmutableArray() ?? [];
    }

    public string SourcePath { get; }

    public string AnimationStack { get; }

    public FrameRate FrameRate { get; }

    public long FrameCount { get; }

    public ImmutableArray<Dl1MimicSourceCurve> Curves { get; }

    public ImmutableArray<string> Warnings { get; }

    public ImmutableArray<string> AnimatedShapeNames =>
        Curves
            .Where(static curve => curve.IsAnimated)
            .Select(static curve => curve.Name)
            .ToImmutableArray();
}

public enum Dl1MimicClampMode
{
    None,
    Hard,
    Soft,
}

public sealed record Dl1MimicBuildRequest
{
    public required Dl1MimicSourceScan Source { get; init; }

    public required Dl1MimicProfile Profile { get; init; }

    public required RigDefinition ExactTargetRig { get; init; }

    /// <summary>
    /// Null selects conservative auto-mapping. An explicitly empty array keeps
    /// every profile target neutral.
    /// </summary>
    public ImmutableArray<Dl1MimicMappingRow>? Mapping { get; init; }

    public Dl1MimicClampMode ClampMode { get; init; }

    public double ConstantTolerance { get; init; } = 1e-7;
}

public sealed record Dl1MimicBuildReport(
    string ProfileId,
    string ProfileName,
    string WeightComponent,
    string SourcePath,
    string SourceAnimationStack,
    int SourceShapeCount,
    int AnimatedSourceShapeCount,
    int MappedSourceShapeCount,
    ImmutableArray<string> UnmappedAnimatedShapes,
    int TargetTrackCount,
    ImmutableArray<string> ActiveTargetTracks,
    ImmutableDictionary<uint, ImmutableArray<string>> ConsolidatedTargets,
    double CapturedSourceActivityRatio,
    Dl1MimicClampMode ClampMode,
    ImmutableArray<Dl1MimicMappingRow> Mapping,
    ImmutableArray<string> Warnings,
    ImmutableArray<int> DecodedSampleFrames,
    double DecodedMaximumComponentError);

public sealed record Dl1MimicBuildResult(
    AnimationClip Clip,
    byte[] Payload,
    Dl1MimicBuildReport Report);

public interface IDl1MimicBuilder
{
    Dl1MimicBuildResult Build(
        Dl1MimicBuildRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Consolidates sampled facial curves into the selected exact target's DL1
/// mimic descriptor inventory and encodes ordinary ANM2 tracks whose tx
/// component is the morph scalar.
/// </summary>
public sealed class Dl1MimicBuilder : IDl1MimicBuilder
{
    public const int MaximumSourceCurveCount = 4096;
    public const int MaximumAggregateSourceSamples = 1_000_000;
    public const int MaximumMappingRowCount = 16_384;

    public Dl1MimicBuildResult Build(
        Dl1MimicBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.ExactTargetRig);
        if (!double.IsFinite(request.ConstantTolerance) ||
            request.ConstantTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The ANM2 constant tolerance must be finite and non-negative.");
        }

        Dl1MimicSourceScan source = request.Source;
        Dl1MimicProfile profile = request.Profile;
        ValidateBounds(source, profile);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<uint, MorphChannelDefinition> targetMorphs =
            ResolveExactTargetMorphs(profile, request.ExactTargetRig);
        ImmutableArray<Dl1MimicMappingRow> mapping =
            request.Mapping ??
            Dl1MimicAutoMapper.AutoMap(
                source.Curves,
                profile);
        ValidateMapping(mapping, profile);
        cancellationToken.ThrowIfCancellationRequested();

        int frameCount = checked((int)source.FrameCount);
        var values = new double[profile.Targets.Length][];
        for (int targetIndex = 0;
             targetIndex < profile.Targets.Length;
             targetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[targetIndex] = new double[frameCount];
            Array.Fill(
                values[targetIndex],
                profile.Targets[targetIndex].Neutral);
        }

        var sourceActivity = new Dictionary<string, double>(
            StringComparer.Ordinal);
        var activeSources = source.Curves
            .Where(static curve => curve.IsAnimated)
            .Select(static curve => curve.Name)
            .ToHashSet(StringComparer.Ordinal);
        var mappedSources = new HashSet<string>(
            StringComparer.Ordinal);
        var mappingWarnings = ImmutableArray.CreateBuilder<string>();
        var contributions = new Dictionary<uint, List<string>>();

        foreach (Dl1MimicSourceCurve curve in source.Curves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double baseline = curve.Values[0];
            double activity = 0;
            for (var frameIndex = 0;
                 frameIndex < curve.Values.Length;
                 frameIndex++)
            {
                if ((frameIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                activity += Math.Abs(
                    curve.Values[frameIndex] - baseline);
            }

            sourceActivity.Add(
                curve.Name,
                activity / curve.Values.Length);
        }

        Dictionary<uint, int> profileIndices = profile.Targets
            .ToDictionary(
                static target => target.Descriptor,
                static target => target.Index);
        foreach (Dl1MimicMappingRow row in mapping)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!row.Enabled)
            {
                continue;
            }

            Dl1MimicSourceCurve? curve = LookupCurve(
                source,
                row.Source);
            if (curve is null)
            {
                mappingWarnings.Add(
                    "Mapped source shape was not found in the selected animation stack: " +
                    row.Source);
                continue;
            }

            int targetIndex = profileIndices[row.TargetDescriptor];
            mappedSources.Add(curve.Name);
            if (!contributions.TryGetValue(
                    row.TargetDescriptor,
                    out List<string>? targetContributions))
            {
                targetContributions = [];
                contributions.Add(
                    row.TargetDescriptor,
                    targetContributions);
            }

            targetContributions.Add(curve.Name);
            for (int frameIndex = 0;
                 frameIndex < frameCount;
                 frameIndex++)
            {
                if ((frameIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                values[targetIndex][frameIndex] +=
                    (curve.Values[frameIndex] * row.Weight) +
                    row.Bias;
            }
        }

        if (request.ClampMode != Dl1MimicClampMode.None)
        {
            for (int targetIndex = 0;
                 targetIndex < profile.Targets.Length;
                 targetIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dl1MimicTarget target = profile.Targets[targetIndex];
                for (int frameIndex = 0;
                     frameIndex < frameCount;
                     frameIndex++)
                {
                    if ((frameIndex & 0xFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    values[targetIndex][frameIndex] =
                        request.ClampMode switch
                        {
                            Dl1MimicClampMode.Hard => Math.Clamp(
                                values[targetIndex][frameIndex],
                                target.RecommendedMinimum,
                                target.RecommendedMaximum),
                            Dl1MimicClampMode.Soft => SoftClip(
                                values[targetIndex][frameIndex],
                                target.RecommendedMinimum,
                                target.RecommendedMaximum),
                            _ => throw new InvalidOperationException(
                                $"Unknown DL1 mimic clamp mode '{request.ClampMode}'."),
                        };
                }
            }
        }

        var scalarTracks = ImmutableArray.CreateBuilder<ScalarTrack>(
            profile.Targets.Length);
        var activeTargetTracks = ImmutableArray.CreateBuilder<string>();
        foreach (Dl1MimicTarget target in profile.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double[] targetValues = values[target.Index];
            double minimum = targetValues[0];
            double maximum = targetValues[0];
            for (var frameIndex = 1;
                 frameIndex < targetValues.Length;
                 frameIndex++)
            {
                if ((frameIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                minimum = Math.Min(
                    minimum,
                    targetValues[frameIndex]);
                maximum = Math.Max(
                    maximum,
                    targetValues[frameIndex]);
            }

            if (maximum - minimum > 1e-8 ||
                Math.Abs(targetValues[0] - target.Neutral) > 1e-8)
            {
                activeTargetTracks.Add(target.Name);
            }

            MorphChannelDefinition targetMorph =
                targetMorphs[target.Descriptor];
            var keys = new ScalarKeyframe[frameCount];
            for (int frameIndex = 0;
                 frameIndex < frameCount;
                 frameIndex++)
            {
                if ((frameIndex & 0xFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                keys[frameIndex] = new ScalarKeyframe(
                    frameIndex,
                    targetValues[frameIndex]);
            }

            scalarTracks.Add(
                new ScalarTrack(
                    targetMorph.Name,
                    keys));
        }

        var clip = new AnimationClip(
            Path.GetFileNameWithoutExtension(source.SourcePath) +
            " mimic",
            source.FrameRate,
            source.FrameCount,
            scalarTracks: scalarTracks.MoveToImmutable());
        cancellationToken.ThrowIfCancellationRequested();
        byte[] payload = Anm2DomainAdapter.ExportMimic(
            clip,
            request.ExactTargetRig,
            profile.Descriptors,
            request.ConstantTolerance,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        (ImmutableArray<int> decodedSampleFrames, double decodedError) =
            VerifyDecodedSamples(
                payload,
                values,
                cancellationToken);
        double totalActivity = activeSources.Sum(sourceName =>
            sourceActivity.GetValueOrDefault(sourceName));
        double capturedActivity = activeSources
            .Where(mappedSources.Contains)
            .Sum(sourceName =>
                sourceActivity.GetValueOrDefault(sourceName));
        ImmutableArray<string> unmapped = activeSources
            .Except(mappedSources, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        ImmutableDictionary<uint, ImmutableArray<string>>
            consolidatedTargets = contributions
                .Select(static pair => new
                {
                    pair.Key,
                    Sources = pair.Value
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToImmutableArray(),
                })
                .Where(static pair => pair.Sources.Length > 1)
                .ToImmutableDictionary(
                    static pair => pair.Key,
                    static pair => pair.Sources);
        ImmutableArray<string> warnings = source.Warnings
            .AddRange(mappingWarnings);

        var report = new Dl1MimicBuildReport(
            profile.ProfileId,
            profile.Name,
            "tx",
            source.SourcePath,
            source.AnimationStack,
            source.Curves.Length,
            activeSources.Count,
            activeSources.Count(mappedSources.Contains),
            unmapped,
            profile.Targets.Length,
            activeTargetTracks.ToImmutable(),
            consolidatedTargets,
            totalActivity > 1e-12
                ? capturedActivity / totalActivity
                : 1,
            request.ClampMode,
            mapping,
            warnings,
            decodedSampleFrames,
            decodedError);
        return new Dl1MimicBuildResult(
            clip,
            payload,
            report);
    }

    /// <summary>
    /// Detects conventional FBX DeformPercent values without assuming every
    /// DCC exporter uses 0..100. Values at or below magnitude two are already
    /// treated as normalized.
    /// </summary>
    public static double DetectPercentScale(
        double defaultValue,
        IEnumerable<double> rawValues)
    {
        if (!double.IsFinite(defaultValue))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue));
        }

        ArgumentNullException.ThrowIfNull(rawValues);
        double maximum = Math.Abs(defaultValue);
        foreach (double value in rawValues)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException(
                    "Facial source values must be finite.",
                    nameof(rawValues));
            }

            maximum = Math.Max(maximum, Math.Abs(value));
        }

        return maximum > 2
            ? 0.01
            : 1;
    }

    private static void ValidateBounds(
        Dl1MimicSourceScan source,
        Dl1MimicProfile profile)
    {
        if (source.Curves.Length > MaximumSourceCurveCount)
        {
            throw new InvalidDataException(
                $"A mimic source scan cannot exceed {MaximumSourceCurveCount} curves.");
        }

        long aggregateSamples = checked(
            source.FrameCount * source.Curves.Length);
        if (aggregateSamples > MaximumAggregateSourceSamples)
        {
            throw new InvalidDataException(
                "The mimic source scan exceeds the bounded aggregate sample count " +
                $"{MaximumAggregateSourceSamples}.");
        }

        long targetSamples = checked(
            source.FrameCount * profile.Targets.Length);
        if (targetSamples > MaximumAggregateSourceSamples)
        {
            throw new InvalidDataException(
                "The generated mimic target curves exceed the bounded aggregate sample count " +
                $"{MaximumAggregateSourceSamples}.");
        }
    }

    private static Dictionary<uint, MorphChannelDefinition>
        ResolveExactTargetMorphs(
            Dl1MimicProfile profile,
            RigDefinition targetRig)
    {
        var byDescriptor =
            new Dictionary<uint, MorphChannelDefinition>();
        foreach (MorphChannelDefinition morph in targetRig.MorphChannels)
        {
            if (morph.DescriptorHash is not uint descriptor)
            {
                continue;
            }

            if (!byDescriptor.TryAdd(descriptor, morph))
            {
                throw new InvalidOperationException(
                    $"Exact target rig '{targetRig.Id}' contains duplicate mimic descriptor " +
                    $"0x{descriptor:X8}.");
            }
        }

        uint[] missing = profile.Targets
            .Where(target => !byDescriptor.ContainsKey(
                target.Descriptor))
            .Select(static target => target.Descriptor)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Mimic profile '{profile.ProfileId}' contains descriptors absent from " +
                $"exact target rig '{targetRig.Id}': " +
                string.Join(
                    ", ",
                    missing.Select(static descriptor =>
                        $"0x{descriptor:X8}")) +
                ".");
        }

        return byDescriptor;
    }

    private static void ValidateMapping(
        ImmutableArray<Dl1MimicMappingRow> mapping,
        Dl1MimicProfile profile)
    {
        if (mapping.IsDefault)
        {
            throw new ArgumentException(
                "The mimic mapping collection cannot be uninitialized.",
                nameof(mapping));
        }

        if (mapping.Length > MaximumMappingRowCount)
        {
            throw new InvalidDataException(
                $"A mimic mapping cannot exceed {MaximumMappingRowCount} rows.");
        }

        uint[] unknown = mapping
            .Where(row =>
                profile.FindTarget(row.TargetDescriptor) is null)
            .Select(static row => row.TargetDescriptor)
            .Distinct()
            .Order()
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                "Mimic mapping refers to descriptors absent from the selected profile: " +
                string.Join(
                    ", ",
                    unknown.Select(static descriptor =>
                        $"0x{descriptor:X8}")) +
                ".");
        }
    }

    private static Dl1MimicSourceCurve? LookupCurve(
        Dl1MimicSourceScan source,
        string sourceName)
    {
        Dl1MimicSourceCurve? exact = source.Curves
            .FirstOrDefault(curve =>
                string.Equals(
                    curve.Name,
                    sourceName,
                    StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        return source.Curves.FirstOrDefault(curve =>
            curve.Aliases.Any(alias =>
                string.Equals(
                    alias,
                    sourceName,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static double SoftClip(
        double value,
        double minimum,
        double maximum)
    {
        if (maximum <= minimum)
        {
            return value;
        }

        double midpoint = 0.5 * (minimum + maximum);
        double half = 0.5 * (maximum - minimum);
        return midpoint +
               (half * Math.Tanh(
                   (value - midpoint) /
                   Math.Max(half, 1e-8)));
    }

    private static (
        ImmutableArray<int> SampleFrames,
        double MaximumError)
        VerifyDecodedSamples(
            byte[] payload,
            double[][] values,
            CancellationToken cancellationToken)
    {
        Anm2Clip decoded = Anm2Reader.Read(
            payload,
            "generated-mimic.anm2",
            cancellationToken: cancellationToken);
        int frameCount = decoded.Header.FrameCount;
        ImmutableArray<int> sampleFrames = new[]
            {
                0,
                frameCount / 2,
                frameCount - 1,
            }
            .Distinct()
            .Order()
            .ToImmutableArray();
        double maximumError = 0;
        foreach (int frameIndex in sampleFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Anm2Frame frame =
                Anm2SemanticDecoder.Sample(
                    decoded,
                    frameIndex).Frame;
            for (int targetIndex = 0;
                 targetIndex < values.Length;
                 targetIndex++)
            {
                Anm2TrackFrame track =
                    frame.Tracks[targetIndex];
                Span<double> expected =
                [
                    0,
                    0,
                    0,
                    values[targetIndex][frameIndex],
                    0,
                    0,
                    1,
                    1,
                    1,
                ];
                for (int component = 0; component < 9; component++)
                {
                    maximumError = Math.Max(
                        maximumError,
                        Math.Abs(
                            track[component] -
                            expected[component]));
                }
            }
        }

        return (sampleFrames, maximumError);
    }
}
