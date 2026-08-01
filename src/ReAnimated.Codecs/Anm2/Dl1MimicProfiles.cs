using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReAnimated.Codecs.Anm2;

public sealed record Dl1MimicTarget
{
    public Dl1MimicTarget(
        int index,
        uint descriptor,
        string name,
        string label,
        string semantic = "morph_scalar_tx",
        string component = "tx",
        string region = "unknown",
        string side = "center",
        IEnumerable<string>? aliases = null,
        double neutral = 0,
        double recommendedMinimum = -1.5,
        double recommendedMaximum = 1.5,
        string nameStatus = "unresolved",
        double confidence = 0,
        IEnumerable<string>? tags = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(semantic);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentException.ThrowIfNullOrWhiteSpace(side);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameStatus);
        if (!double.IsFinite(neutral) ||
            !double.IsFinite(recommendedMinimum) ||
            !double.IsFinite(recommendedMaximum) ||
            recommendedMaximum < recommendedMinimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recommendedMaximum),
                "Mimic target values must be finite and recommended bounds must be ordered.");
        }

        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                "Mimic target confidence must be between zero and one.");
        }

        Index = index;
        Descriptor = descriptor;
        Name = name;
        Label = label;
        Semantic = semantic;
        Component = component;
        Region = region;
        Side = side;
        Aliases = aliases?.ToImmutableArray() ?? [];
        Neutral = neutral;
        RecommendedMinimum = recommendedMinimum;
        RecommendedMaximum = recommendedMaximum;
        NameStatus = nameStatus;
        Confidence = confidence;
        Tags = tags?.ToImmutableArray() ?? [];
    }

    public int Index { get; }

    public uint Descriptor { get; }

    public string Name { get; }

    public string Label { get; }

    public string Semantic { get; }

    public string Component { get; }

    public string Region { get; }

    public string Side { get; }

    public ImmutableArray<string> Aliases { get; }

    public double Neutral { get; }

    public double RecommendedMinimum { get; }

    public double RecommendedMaximum { get; }

    public string NameStatus { get; }

    public double Confidence { get; }

    public ImmutableArray<string> Tags { get; }

    public IEnumerable<string> CandidateNames()
    {
        yield return Name;
        yield return Label;
        foreach (string alias in Aliases)
        {
            yield return alias;
        }
    }
}

/// <summary>
/// A declarative DL1 facial ANM2 descriptor inventory. The profile contains
/// metadata only; it does not contain a retail mesh, morph deltas, or game data.
/// </summary>
public sealed class Dl1MimicProfile
{
    public const string Format = "dl-reanimated-mimic-profile";
    public const int SchemaVersion = 1;
    public const string BuiltInCommon46Id = "builtin:human_common46";
    public const int MaximumTargetCount = 4096;

    private readonly ImmutableDictionary<uint, Dl1MimicTarget>
        _targetsByDescriptor;

    public Dl1MimicProfile(
        string profileId,
        string name,
        IEnumerable<Dl1MimicTarget> targets,
        string description = "",
        string author = "",
        string license = "",
        string weightComponent = "tx",
        JsonElement? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(author);
        ArgumentNullException.ThrowIfNull(license);
        ArgumentException.ThrowIfNullOrWhiteSpace(weightComponent);

        ImmutableArray<Dl1MimicTarget> targetArray =
            targets.ToImmutableArray();
        if (targetArray.IsEmpty ||
            targetArray.Length > MaximumTargetCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targets),
                $"A mimic profile requires 1..{MaximumTargetCount} target tracks.");
        }

        for (int index = 0; index < targetArray.Length; index++)
        {
            if (targetArray[index].Index != index)
            {
                throw new ArgumentException(
                    "Mimic profile target indexes must be contiguous and zero-based.",
                    nameof(targets));
            }

            if (!string.Equals(
                    targetArray[index].Semantic,
                    "morph_scalar_tx",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    targetArray[index].Component,
                    "tx",
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "DL1 mimic generation supports only morph_scalar_tx targets.",
                    nameof(targets));
            }
        }

        if (!string.Equals(
                weightComponent,
                "tx",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "DL1 mimic generation supports only the ANM2 tx component.",
                nameof(weightComponent));
        }

        if (targetArray
                .Select(static target => target.Descriptor)
                .Distinct()
                .Count() != targetArray.Length)
        {
            throw new ArgumentException(
                "Mimic profile descriptors must be unique.",
                nameof(targets));
        }

        if (targetArray
                .Select(static target => target.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != targetArray.Length)
        {
            throw new ArgumentException(
                "Mimic profile target names must be unique.",
                nameof(targets));
        }

        if (extensions is { ValueKind: not JsonValueKind.Object })
        {
            throw new ArgumentException(
                "Mimic profile extensions must be a JSON object.",
                nameof(extensions));
        }

        ProfileId = profileId;
        Name = name;
        Description = description;
        Author = author;
        License = license;
        WeightComponent = weightComponent;
        Targets = targetArray;
        Extensions = extensions?.Clone() ?? CreateEmptyExtensions();
        _targetsByDescriptor = targetArray.ToImmutableDictionary(
            static target => target.Descriptor);
    }

    public string ProfileId { get; }

    public string Name { get; }

    public string Description { get; }

    public string Author { get; }

    public string License { get; }

    public string WeightComponent { get; }

    public ImmutableArray<Dl1MimicTarget> Targets { get; }

    public JsonElement Extensions { get; }

    public ImmutableArray<uint> Descriptors =>
        Targets
            .Select(static target => target.Descriptor)
            .ToImmutableArray();

    public Dl1MimicTarget? FindTarget(uint descriptor) =>
        _targetsByDescriptor.GetValueOrDefault(descriptor);

    private static JsonElement CreateEmptyExtensions()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

public sealed record Dl1MimicMappingRow
{
    public Dl1MimicMappingRow(
        string source,
        uint targetDescriptor,
        double weight = 1,
        double bias = 0,
        bool enabled = true,
        double confidence = 1,
        string method = "manual")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (!double.IsFinite(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        if (!double.IsFinite(bias))
        {
            throw new ArgumentOutOfRangeException(nameof(bias));
        }

        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidence),
                "Mapping confidence must be between zero and one.");
        }

        Source = source;
        TargetDescriptor = targetDescriptor;
        Weight = weight;
        Bias = bias;
        Enabled = enabled;
        Confidence = confidence;
        Method = method;
    }

    public string Source { get; }

    public uint TargetDescriptor { get; }

    public double Weight { get; }

    public double Bias { get; }

    public bool Enabled { get; }

    public double Confidence { get; }

    public string Method { get; }
}

public static partial class Dl1MimicAutoMapper
{
    private static readonly Dictionary<string, string>
        SemanticAliases = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            ["eyeblinkleft"] = "morph_l_u_lid",
            ["eyeblinkright"] = "morph_r_u_lid",
            ["leftblink"] = "morph_l_u_lid",
            ["rightblink"] = "morph_r_u_lid",
            ["jawopen"] = "morph_jaw_open",
            ["mouthopen"] = "morph_jaw_open",
            ["visemeaa"] = "morph_jaw_open",
            ["mouthsmileleft"] = "morph_lips_L_smile",
            ["mouthsmileright"] = "morph_lips_R_smile",
            ["mouthdimpleleft"] = "morph_lips_L_dimple",
            ["mouthdimpleright"] = "morph_lips_R_dimple",
            ["mouthfunnel"] = "morph_lips_funnel",
            ["mouthpucker"] = "morph_lips_funnel",
            ["mouthupperupleft"] = "morph_lips_U_up",
            ["mouthupperupright"] = "morph_lips_U_up",
            ["mouthlowerdownleft"] = "morph_lips_B_down",
            ["mouthlowerdownright"] = "morph_lips_B_down",
            ["visemepbm"] = "pbm",
            ["visemefv"] = "fv",
            ["visemew"] = "w",
            ["visemewide"] = "wide",
            ["visemeopen"] = "open",
        };

    public static ImmutableArray<Dl1MimicMappingRow> AutoMap(
        IEnumerable<string> sourceNames,
        Dl1MimicProfile profile)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);
        ArgumentNullException.ThrowIfNull(profile);

        Dictionary<string, List<Dl1MimicTarget>> aliasIndex =
            BuildAliasIndex(profile);
        Dictionary<string, Dl1MimicTarget> targetsByUniqueName =
            BuildUniqueNormalizedNameIndex(profile);
        var result = ImmutableArray.CreateBuilder<Dl1MimicMappingRow>();

        foreach (string source in sourceNames)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            string normalized = Normalize(source);
            List<Dl1MimicTarget> candidates =
                aliasIndex.TryGetValue(
                    normalized,
                    out List<Dl1MimicTarget>? found)
                        ? [.. found]
                        : [];
            string method = "exact_alias";
            double confidence = 1;
            Dl1MimicTarget? semanticTarget = null;
            if (SemanticAliases.TryGetValue(
                    normalized,
                    out string? semanticName))
            {
                targetsByUniqueName.TryGetValue(
                    Normalize(semanticName),
                    out semanticTarget);
            }

            if (semanticTarget is not null && candidates.Count != 1)
            {
                candidates = [semanticTarget];
                method = "semantic_disambiguation";
                confidence = 0.92;
            }
            else if (candidates.Count == 0 && semanticTarget is not null)
            {
                candidates = [semanticTarget];
                method = "semantic_alias";
                confidence = 0.92;
            }

            if (candidates.Count != 1)
            {
                continue;
            }

            result.Add(
                new Dl1MimicMappingRow(
                    source,
                    candidates[0].Descriptor,
                    confidence: confidence,
                    method: method));
        }

        string[] mappedSources = result
            .Select(static row => row.Source)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string source in mappedSources)
        {
            string normalized = Normalize(source);
            if (normalized is not (
                    "eyeblinkleft" or
                    "leftblink" or
                    "eyeblinkright" or
                    "rightblink"))
            {
                continue;
            }

            string lowerName = normalized.Contains(
                "left",
                StringComparison.Ordinal)
                    ? "morph_l_b_lid"
                    : "morph_r_b_lid";
            if (targetsByUniqueName.TryGetValue(
                    Normalize(lowerName),
                    out Dl1MimicTarget? lower))
            {
                result.Add(
                    new Dl1MimicMappingRow(
                        source,
                        lower.Descriptor,
                        weight: 0.65,
                        confidence: 0.78,
                        method: "blink_lower_lid_companion"));
            }
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Conservatively maps sampled source curves while preserving the curve's
    /// canonical channel name in every result row. A shape alias is used only
    /// when the canonical name has no mapping and every recognized alias
    /// resolves to the same target set.
    /// </summary>
    public static ImmutableArray<Dl1MimicMappingRow> AutoMap(
        IEnumerable<Dl1MimicSourceCurve> sourceCurves,
        Dl1MimicProfile profile)
    {
        ArgumentNullException.ThrowIfNull(sourceCurves);
        ArgumentNullException.ThrowIfNull(profile);
        var result = ImmutableArray.CreateBuilder<Dl1MimicMappingRow>();
        foreach (Dl1MimicSourceCurve curve in sourceCurves.Where(
                     static curve => curve.IsAnimated))
        {
            ImmutableArray<Dl1MimicMappingRow> canonical =
                AutoMap([curve.Name], profile);
            if (!canonical.IsEmpty)
            {
                AddCanonicalRows(canonical, curve.Name, aliasDriven: false);
                continue;
            }

            ImmutableArray<ImmutableArray<Dl1MimicMappingRow>>
                aliasCandidates = curve.Aliases
                    .Where(alias =>
                        !string.IsNullOrWhiteSpace(alias) &&
                        !string.Equals(
                            alias,
                            curve.Name,
                            StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(alias => AutoMap([alias], profile))
                    .Where(static rows => !rows.IsEmpty)
                    .ToImmutableArray();
            if (aliasCandidates.IsEmpty)
            {
                continue;
            }

            string[] signatures = aliasCandidates
                .Select(MappingSignature)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (signatures.Length != 1)
            {
                // Conflicting shape aliases are review-required; guessing one
                // would silently animate the wrong retail morph.
                continue;
            }

            AddCanonicalRows(
                aliasCandidates[0],
                curve.Name,
                aliasDriven: true);
        }

        return result.ToImmutable();

        void AddCanonicalRows(
            ImmutableArray<Dl1MimicMappingRow> rows,
            string sourceName,
            bool aliasDriven)
        {
            foreach (Dl1MimicMappingRow row in rows)
            {
                result.Add(
                    new Dl1MimicMappingRow(
                        sourceName,
                        row.TargetDescriptor,
                        row.Weight,
                        row.Bias,
                        row.Enabled,
                        row.Confidence,
                        aliasDriven
                            ? "shape_alias:" + row.Method
                            : row.Method));
            }
        }

        static string MappingSignature(
            ImmutableArray<Dl1MimicMappingRow> rows) =>
            string.Join(
                "|",
                rows
                    .OrderBy(static row => row.TargetDescriptor)
                    .ThenBy(static row => row.Weight)
                    .Select(static row =>
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{row.TargetDescriptor:X8}:{row.Weight:R}:{row.Bias:R}:{row.Enabled}")));
    }

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int nullIndex = value.IndexOf('\0');
        string text = nullIndex < 0
            ? value
            : value[..nullIndex];
        int namespaceIndex = text.LastIndexOf(
            "::",
            StringComparison.Ordinal);
        if (namespaceIndex >= 0)
        {
            text = text[(namespaceIndex + 2)..];
        }

        int hierarchyIndex = text.LastIndexOf('|');
        if (hierarchyIndex >= 0)
        {
            text = text[(hierarchyIndex + 1)..];
        }

        text = PrefixRegex().Replace(text, string.Empty);
        text = LeftSuffixRegex().Replace(text, "_left");
        text = RightSuffixRegex().Replace(text, "_right");
        return NonAlphaNumericRegex()
            .Replace(text.ToLowerInvariant(), string.Empty);
    }

    private static Dictionary<string, List<Dl1MimicTarget>>
        BuildAliasIndex(Dl1MimicProfile profile)
    {
        var result = new Dictionary<string, List<Dl1MimicTarget>>(
            StringComparer.Ordinal);
        foreach (Dl1MimicTarget target in profile.Targets)
        {
            foreach (string candidateName in target.CandidateNames())
            {
                string normalized = Normalize(candidateName);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (!result.TryGetValue(
                        normalized,
                        out List<Dl1MimicTarget>? bucket))
                {
                    bucket = [];
                    result.Add(normalized, bucket);
                }

                if (bucket.All(existing =>
                        existing.Descriptor != target.Descriptor))
                {
                    bucket.Add(target);
                }
            }
        }

        return result;
    }

    private static Dictionary<string, Dl1MimicTarget>
        BuildUniqueNormalizedNameIndex(Dl1MimicProfile profile)
    {
        return profile.Targets
            .GroupBy(
                target => Normalize(target.Name),
                StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single(),
                StringComparer.Ordinal);
    }

    [GeneratedRegex(
        "^(blendshape|shape|morph|bs|face)[_:\\- ]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(
        "(left|_l|\\.l)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeftSuffixRegex();

    [GeneratedRegex(
        "(right|_r|\\.r)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RightSuffixRegex();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();
}

public static class Dl1MimicProfileCodec
{
    public const int MaximumProfileBytes = 1024 * 1024;
    private const string BuiltInCommon46Resource =
        "ReAnimated.Codecs.MimicProfiles.human_common46.dlrmimic.json";

    public static Dl1MimicProfile Read(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0 ||
            utf8Json.Length > MaximumProfileBytes)
        {
            throw new InvalidDataException(
                $"A mimic profile must contain 1..{MaximumProfileBytes} bytes.");
        }

        bool hasUtf8ByteOrderMark =
            utf8Json.Length >= 3 &&
            utf8Json[0] == 0xEF &&
            utf8Json[1] == 0xBB &&
            utf8Json[2] == 0xBF;
        ReadOnlySpan<byte> payload =
            hasUtf8ByteOrderMark
                ? utf8Json[3..]
                : utf8Json;
        if (payload.IsEmpty)
        {
            throw new InvalidDataException(
                "A mimic profile cannot contain only a UTF-8 byte-order mark.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                payload.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "A mimic profile root must be a JSON object.");
            }

            string format = RequiredString(root, "format");
            if (!string.Equals(
                    format,
                    Dl1MimicProfile.Format,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The file is not a DL ReAnimated mimic profile.");
            }

            int schemaVersion = OptionalInt32(
                root,
                "schema_version",
                0);
            if (schemaVersion < 0 ||
                schemaVersion > Dl1MimicProfile.SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Mimic profile schema {schemaVersion} is not supported; " +
                    $"maximum supported schema is {Dl1MimicProfile.SchemaVersion}.");
            }

            JsonElement tracksElement = RequiredProperty(root, "tracks");
            if (tracksElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Mimic profile tracks must be an array.");
            }

            var targets = new List<Dl1MimicTarget>();
            foreach (JsonElement row in tracksElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        "Each mimic profile track must be an object.");
                }

                uint descriptor = Descriptor(
                    RequiredProperty(row, "descriptor"));
                targets.Add(
                    new Dl1MimicTarget(
                        RequiredInt32(row, "index"),
                        descriptor,
                        OptionalString(
                            row,
                            "name",
                            $"morph_{descriptor:X8}"),
                        OptionalString(
                            row,
                            "label",
                            OptionalString(
                                row,
                                "name",
                                "Unnamed morph")),
                        OptionalString(
                            row,
                            "semantic",
                            "morph_scalar_tx"),
                        OptionalString(row, "component", "tx"),
                        OptionalString(row, "region", "unknown"),
                        OptionalString(row, "side", "center"),
                        OptionalStrings(row, "aliases"),
                        OptionalDouble(row, "neutral", 0),
                        OptionalDouble(
                            row,
                            "recommended_min",
                            -1.5),
                        OptionalDouble(
                            row,
                            "recommended_max",
                            1.5),
                        OptionalString(
                            row,
                            "name_status",
                            "unresolved"),
                        OptionalDouble(row, "confidence", 0),
                        OptionalStrings(row, "tags")));
            }

            targets.Sort(static (left, right) =>
                left.Index.CompareTo(right.Index));
            int declaredTrackCount = OptionalInt32(
                root,
                "track_count",
                targets.Count);
            if (declaredTrackCount != targets.Count)
            {
                throw new InvalidDataException(
                    $"Mimic profile track_count {declaredTrackCount} does not match " +
                    $"the {targets.Count} track rows.");
            }

            JsonElement? extensions = null;
            if (root.TryGetProperty(
                    "extensions",
                    out JsonElement extensionsElement))
            {
                extensions = extensionsElement.Clone();
            }

            return new Dl1MimicProfile(
                OptionalString(
                    root,
                    "profile_id",
                    "custom:mimic"),
                OptionalString(
                    root,
                    "name",
                    "Custom mimic profile"),
                targets,
                OptionalString(root, "description", string.Empty),
                OptionalString(root, "author", string.Empty),
                OptionalString(root, "license", string.Empty),
                OptionalString(root, "weight_component", "tx"),
                extensions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The mimic profile JSON is malformed.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The mimic profile contains an invalid domain value: " +
                exception.Message,
                exception);
        }
    }

    public static Dl1MimicProfile ReadBuiltInCommon46()
    {
        using Stream stream = typeof(Dl1MimicProfileCodec)
            .Assembly
            .GetManifestResourceStream(BuiltInCommon46Resource)
            ?? throw new InvalidOperationException(
                "The bundled DL1 common-46 mimic profile is missing.");
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumProfileBytes)
            {
                throw new InvalidDataException(
                    "The bundled DL1 common-46 mimic profile exceeds its bounded size.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Read(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    public static byte[] WriteCanonical(Dl1MimicProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = true,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Dl1MimicProfile.Format);
            writer.WriteNumber(
                "schema_version",
                Dl1MimicProfile.SchemaVersion);
            writer.WriteString("profile_id", profile.ProfileId);
            writer.WriteString("name", profile.Name);
            writer.WriteString("description", profile.Description);
            writer.WriteString("author", profile.Author);
            writer.WriteString("license", profile.License);
            writer.WriteNumber("track_count", profile.Targets.Length);
            writer.WriteString(
                "weight_component",
                profile.WeightComponent);
            writer.WriteStartArray("default_components");
            foreach (double value in new double[]
                     {
                         0,
                         0,
                         0,
                         0,
                         0,
                         0,
                         1,
                         1,
                         1,
                     })
            {
                writer.WriteNumberValue(value);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("tracks");
            foreach (Dl1MimicTarget target in profile.Targets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", target.Index);
                writer.WriteString(
                    "descriptor",
                    $"0x{target.Descriptor:X8}");
                writer.WriteString("name", target.Name);
                writer.WriteString("label", target.Label);
                writer.WriteString("semantic", target.Semantic);
                writer.WriteString("component", target.Component);
                writer.WriteString("region", target.Region);
                writer.WriteString("side", target.Side);
                WriteStrings(writer, "aliases", target.Aliases);
                writer.WriteNumber("neutral", target.Neutral);
                writer.WriteNumber(
                    "recommended_min",
                    target.RecommendedMinimum);
                writer.WriteNumber(
                    "recommended_max",
                    target.RecommendedMaximum);
                writer.WriteString(
                    "name_status",
                    target.NameStatus);
                writer.WriteNumber("confidence", target.Confidence);
                WriteStrings(writer, "tags", target.Tags);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("extensions");
            profile.Extensions.WriteTo(writer);
            writer.WriteEndObject();
        }

        byte[] result = GC.AllocateUninitializedArray<byte>(
            buffer.WrittenCount + 1);
        buffer.WrittenSpan.CopyTo(result);
        result[^1] = (byte)'\n';
        if (result.Length > MaximumProfileBytes)
        {
            throw new InvalidDataException(
                "The canonical mimic profile exceeds its bounded size.");
        }

        return result;
    }

    private static JsonElement RequiredProperty(
        JsonElement owner,
        string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            throw new InvalidDataException(
                $"Mimic profile property '{name}' is required.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement owner,
        string name)
    {
        JsonElement value = RequiredProperty(owner, name);
        return value.ValueKind == JsonValueKind.String &&
               value.GetString() is { } text
            ? text
            : throw new InvalidDataException(
                $"Mimic profile property '{name}' must be a string.");
    }

    private static int RequiredInt32(
        JsonElement owner,
        string name)
    {
        JsonElement value = RequiredProperty(owner, name);
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out int result)
            ? result
            : throw new InvalidDataException(
                $"Mimic profile property '{name}' must be an integer.");
    }

    private static int OptionalInt32(
        JsonElement owner,
        string name,
        int defaultValue) =>
        owner.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind == JsonValueKind.Number &&
              value.TryGetInt32(out int result)
                ? result
                : throw new InvalidDataException(
                    $"Mimic profile property '{name}' must be an integer.")
            : defaultValue;

    private static string OptionalString(
        JsonElement owner,
        string name,
        string defaultValue) =>
        owner.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind == JsonValueKind.String &&
              value.GetString() is { } result
                ? result
                : throw new InvalidDataException(
                    $"Mimic profile property '{name}' must be a string.")
            : defaultValue;

    private static double OptionalDouble(
        JsonElement owner,
        string name,
        double defaultValue)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            return defaultValue;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException(
                $"Mimic profile property '{name}' must be a finite number.");
        }

        return result;
    }

    private static ImmutableArray<string> OptionalStrings(
        JsonElement owner,
        string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Mimic profile property '{name}' must be a string array.");
        }

        var result = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { } text)
            {
                throw new InvalidDataException(
                    $"Mimic profile property '{name}' must contain only strings.");
            }

            result.Add(text);
        }

        return result.ToImmutable();
    }

    private static uint Descriptor(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetUInt32(out uint unsigned))
            {
                return unsigned;
            }

            if (value.TryGetInt64(out long signed))
            {
                return unchecked((uint)signed);
            }
        }
        else if (value.ValueKind == JsonValueKind.String &&
                 value.GetString() is { } text)
        {
            text = text.Trim();
            if (text.StartsWith(
                    "0x",
                    StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(
                    text.AsSpan(2),
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out uint hexadecimal))
            {
                return hexadecimal;
            }

            if (uint.TryParse(
                    text,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out uint decimalValue))
            {
                return decimalValue;
            }
        }

        throw new InvalidDataException(
            "A mimic target descriptor must be a uint32 number or decimal/0x hexadecimal string.");
    }

    private static void WriteStrings(
        Utf8JsonWriter writer,
        string propertyName,
        ImmutableArray<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (string value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
