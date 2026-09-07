using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Fed;

public sealed record SpeechCurveSource(string FileName, string Sha256, long ByteLength, int BankVersion, int EndianFlag);
public sealed record SpeechCurveTrack(string Label, double MaximumWeight, ImmutableArray<int> Samples, double MinimumWeight = 0, byte Flags = 0);
public sealed record SpeechCurveEntry(string Name, int PayloadVersion, int FrameCount, double FrameStepSeconds, ImmutableArray<SpeechCurveTrack> Tracks)
{
    public double DurationSeconds => FrameCount * FrameStepSeconds;
}
public sealed record SpeechCurveExchange(int SchemaVersion, SpeechCurveSource Source, int CurveValueMaximum,
    string WeightInterpretation, ImmutableArray<SpeechCurveEntry> Entries);

/// <summary>Strict reader for DyingAudio's versioned SPB inspection exchange.</summary>
public static class SpeechCurveExchangeReader
{
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public static SpeechCurveExchange Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    public static SpeechCurveExchange Read(Stream stream)
    {
        using MemoryStream bytes = new();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (bytes.Length + count > MaximumFileBytes) throw new InvalidDataException("Speech exchange exceeds 64 MiB.");
            bytes.Write(buffer, 0, count);
        }
        SpeechCurveExchange result = JsonSerializer.Deserialize<SpeechCurveExchange>(bytes.ToArray(), Options)
            ?? throw new InvalidDataException("Speech exchange document is missing.");
        Validate(result);
        return result;
    }

    public static void Validate(SpeechCurveExchange document)
    {
        if (document.SchemaVersion != 1 || document.CurveValueMaximum != 254 ||
            document.WeightInterpretation is not ("sample/curveValueMaximum*maximumWeight" or "lerp(minimumWeight,maximumWeight,sample/curveValueMaximum)"))
            throw new InvalidDataException("Unsupported speech exchange schema or sample interpretation.");
        SpeechCurveSource source = document.Source ?? throw new InvalidDataException("Speech source identity is missing.");
        if (string.IsNullOrWhiteSpace(source.FileName) || source.FileName != Path.GetFileName(source.FileName) ||
            source.Sha256 is not { Length: 64 } || !source.Sha256.All(Uri.IsHexDigit) ||
            source.ByteLength is < 20 or > MaximumFileBytes || source.BankVersion != 1 || source.EndianFlag != 1)
            throw new InvalidDataException("Speech source identity is invalid.");
        if (document.Entries.IsDefault || document.Entries.Length > 100_000)
            throw new InvalidDataException("Speech entries exceed the supported bound.");
        HashSet<string> entryNames = new(StringComparer.Ordinal);
        long sampleCount = 0;
        foreach (SpeechCurveEntry entry in document.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 256 || !entryNames.Add(entry.Name) ||
                entry.PayloadVersion != 4 || entry.FrameCount is < 0 or > 1_000_000 ||
                !double.IsFinite(entry.FrameStepSeconds) || entry.FrameStepSeconds is <= 0 or > 10 ||
                entry.Tracks.IsDefault || entry.Tracks.Length > 256)
                throw new InvalidDataException("Speech entry metadata is invalid.");
            HashSet<string> labels = new(StringComparer.Ordinal);
            foreach (SpeechCurveTrack track in entry.Tracks)
            {
                if (track is null) throw new InvalidDataException("Speech channel is missing.");
                sampleCount += track.Samples.Length;
                if (string.IsNullOrWhiteSpace(track.Label) || track.Label.Length > 256 || !labels.Add(track.Label) ||
                    !double.IsFinite(track.MaximumWeight) || track.MaximumWeight is < 0 or > 4 ||
                    !double.IsFinite(track.MinimumWeight) || track.MinimumWeight is < -4 or > 4 ||
                    (document.WeightInterpretation == "sample/curveValueMaximum*maximumWeight" && track.MinimumWeight != 0) ||
                    track.Samples.IsDefault || track.Samples.Length != entry.FrameCount || sampleCount > 8_000_000 ||
                    track.Samples.Any(value => value is < 0 or > 254))
                    throw new InvalidDataException("Speech channel samples or maximum weight are invalid.");
            }
        }
    }
}

public enum SpeechResolutionSeverity { Information, Warning, Error }
public sealed record SpeechResolutionDiagnostic(string Code, SpeechResolutionSeverity Severity, string Message);
public sealed record SpeechTargetResolution(string SourceLabel, string? TargetMorph, int? TargetInventoryIndex,
    bool ExplicitConfiguredMapping, ImmutableArray<string> PrefixCandidates);
public sealed record SpeechResolutionReport(string EvidenceBoundary, ImmutableArray<SpeechTargetResolution> Targets,
    ImmutableArray<SpeechResolutionDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(x => x.Severity != SpeechResolutionSeverity.Error);
}
public sealed record SpeechPreviewBuildResult(MorphEditLayer Layer, SpeechCurveSource Source,
    string EntryName, SpeechResolutionReport Resolution);

public static class SpeechCurveDomainAdapter
{
    public const int WindowsPlayerMaximumSpeechTracks = 15;
    public const string WindowsPlayerEvidenceBoundary =
        "Windows DevTools Player static-evidence preview: case-insensitive prefix-first inventory lookup, 15 speech tracks, and minimum/maximum interpolation with sample/254. Retail Game equivalence, compilation and live playback are not established.";

    public static SpeechResolutionReport ResolveTargets(SpeechCurveEntry entry, RigDefinition rig,
        IReadOnlyDictionary<string, string>? mapping = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rig);
        var rows = ImmutableArray.CreateBuilder<SpeechTargetResolution>();
        var diagnostics = ImmutableArray.CreateBuilder<SpeechResolutionDiagnostic>();
        if (entry.Tracks.Length > WindowsPlayerMaximumSpeechTracks)
            diagnostics.Add(new("SPB_TRACK_LIMIT", SpeechResolutionSeverity.Error,
                $"Speech entry has {entry.Tracks.Length} tracks; Windows Player supports at most {WindowsPlayerMaximumSpeechTracks}. No tracks were silently truncated."));
        foreach (SpeechCurveTrack track in entry.Tracks)
        {
            string? configured = mapping?.FirstOrDefault(x => string.Equals(x.Key, track.Label, StringComparison.Ordinal)).Value;
            bool explicitMapping = configured is not null;
            var matches = rig.MorphChannels.Select((morph, index) => (morph.Name, Index: index))
                .Where(x => explicitMapping ? string.Equals(x.Name, configured, StringComparison.OrdinalIgnoreCase)
                    : x.Name.StartsWith(track.Label, StringComparison.OrdinalIgnoreCase)).ToArray();
            string? name = matches.Length > 0 ? matches[0].Name : null;
            int? index = matches.Length > 0 ? matches[0].Index : null;
            rows.Add(new(track.Label, name, index, explicitMapping, matches.Select(x => x.Name).ToImmutableArray()));
            if (name is null)
                diagnostics.Add(new("SPB_MISSING_TARGET", SpeechResolutionSeverity.Error,
                    $"Speech label '{track.Label}' has no {(explicitMapping ? "configured exact" : "prefix")} target in the current morph inventory."));
            else if (explicitMapping)
                diagnostics.Add(new("SPB_CONFIGURED_MAPPING", SpeechResolutionSeverity.Information,
                    $"Literal source label '{track.Label}' explicitly maps to '{name}' at inventory index {index}; this overrides native prefix lookup."));
            else if (!string.Equals(name, track.Label, StringComparison.OrdinalIgnoreCase) || matches.Length > 1)
                diagnostics.Add(new("SPB_PREFIX_FIRST", string.Equals(name, track.Label, StringComparison.OrdinalIgnoreCase)
                        ? SpeechResolutionSeverity.Information : SpeechResolutionSeverity.Warning,
                    $"Literal source label '{track.Label}' resolves to first prefix '{name}' at inventory index {index}. Candidates in order: {string.Join(", ", matches.Select(x => x.Name))}."));
            if (track.Label.Any(c => c is < (char)32 or > (char)126))
                diagnostics.Add(new("SPB_NON_ASCII_LABEL", SpeechResolutionSeverity.Error,
                    $"Speech label '{track.Label}' is outside the ASCII label contract verified for Windows Player."));
            if (track.Flags != 0)
                diagnostics.Add(new("SPB_TRACK_FLAGS", SpeechResolutionSeverity.Warning,
                    $"Speech label '{track.Label}' retains flags {track.Flags}; preview evaluates its scalar curve without claiming those flag semantics."));
        }
        foreach (var collision in rows.Where(x => x.TargetMorph is not null).GroupBy(x => x.TargetMorph, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            diagnostics.Add(new("SPB_TARGET_COLLISION", SpeechResolutionSeverity.Error,
                $"Speech labels {string.Join(", ", collision.Select(x => x.SourceLabel))} resolve to the same target '{collision.Key}'. Reorder the morph inventory or configure explicit mappings."));
        return new(WindowsPlayerEvidenceBoundary, rows.ToImmutable(), diagnostics.ToImmutable());
    }

    public static ImmutableArray<string> MissingTargets(SpeechCurveEntry entry, RigDefinition rig,
        IReadOnlyDictionary<string, string>? mapping = null) => ResolveTargets(entry, rig, mapping).Targets
            .Where(x => x.TargetMorph is null).Select(x => x.SourceLabel).ToImmutableArray();

    public static MorphEditLayer CreateLayer(SpeechCurveExchange document, string entryName, RigDefinition rig,
        double timelineFramesPerSecond, IReadOnlyDictionary<string, string>? mapping = null) =>
        BuildPreview(document, entryName, rig, timelineFramesPerSecond, mapping).Layer;

    public static SpeechPreviewBuildResult BuildPreview(SpeechCurveExchange document, string entryName, RigDefinition rig,
        double timelineFramesPerSecond, IReadOnlyDictionary<string, string>? mapping = null)
    {
        SpeechCurveExchangeReader.Validate(document);
        if (!double.IsFinite(timelineFramesPerSecond) || timelineFramesPerSecond is <= 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(timelineFramesPerSecond));
        SpeechCurveEntry entry = document.Entries.FirstOrDefault(x => x.Name == entryName)
            ?? throw new KeyNotFoundException($"Speech entry '{entryName}' is missing.");
        SpeechResolutionReport resolution = ResolveTargets(entry, rig, mapping);
        if (!resolution.IsValid) throw new InvalidOperationException(string.Join(" ", resolution.Diagnostics
            .Where(x => x.Severity == SpeechResolutionSeverity.Error).Select(x => x.Message)));
        ImmutableArray<MorphEditTrack> tracks = entry.Tracks.Select((track, index) =>
        {
            List<ScalarKeyframe> keys = track.Samples.Select((value, i) => new ScalarKeyframe(
                i * entry.FrameStepSeconds * timelineFramesPerSecond,
                track.MinimumWeight + value / (double)document.CurveValueMaximum * (track.MaximumWeight - track.MinimumWeight))).ToList();
            // The authored clip returns to neutral after its final sample interval.
            keys.Add(new ScalarKeyframe(entry.DurationSeconds * timelineFramesPerSecond, 0));
            return new MorphEditTrack(resolution.Targets[index].TargetMorph!, keys);
        }).ToImmutableArray();
        return new(new MorphEditLayer(Guid.NewGuid(), $"SPB: {entry.Name}", MorphEditBlendMode.Override,
            MorphEditLayerScope.PreviewOnly, 1, tracks), document.Source, entry.Name, resolution);
    }
}
