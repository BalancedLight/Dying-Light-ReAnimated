using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Anm2;

public enum Anm2ProvenanceStatus
{
    Missing,
    Valid,
    Invalid,
    HashMismatch,
    FrameCountMismatch,
}

public sealed record Anm2ProvenanceDocument
{
    public required string Anm2Sha256 { get; init; }

    public required string SourceFbx { get; init; }

    public required string SourceFbxSha256 { get; init; }

    public required double SourceFbxFps { get; init; }

    public required double SampleFps { get; init; }

    public required double PlaybackFps { get; init; }

    public required double SourceDurationSeconds { get; init; }

    public required int FrameCount { get; init; }

    public required string RootMotionMode { get; init; }

    public required string RootHeadingMode { get; init; }

    public string? SourceAnimationStack { get; init; }

    public string? FbxAnm2ExportBehavior { get; init; }

    public string? SamplerContract { get; init; }

    public string? SourceTargetCompatibilityClass { get; init; }

    public ImmutableArray<string> BindRetainedBones { get; init; } = [];

    public bool? WrapperReflectionDetected { get; init; }

    public bool? WrapperCanonicalized { get; init; }

    public ImmutableArray<ImmutableArray<double>> WrapperMatrix { get; init; } =
        [];

    public string? BilateralSemanticPolicy { get; init; }

    public bool? BilateralSwapApplied { get; init; }

    public int? BilateralSwappedRowCount { get; init; }

    public bool? PostCanonicalizationMirrorConjugationApplied { get; init; }
}

public sealed record Anm2ProvenanceLoadResult(
    Anm2ProvenanceStatus Status,
    Anm2ProvenanceDocument? Document,
    ImmutableArray<string> Warnings,
    string SidecarPath)
{
    public bool IsValid =>
        Status == Anm2ProvenanceStatus.Valid &&
        Document is not null;

    public string StatusName => Status switch
    {
        Anm2ProvenanceStatus.Missing => "missing",
        Anm2ProvenanceStatus.Valid => "valid",
        Anm2ProvenanceStatus.Invalid => "invalid",
        Anm2ProvenanceStatus.HashMismatch => "hash_mismatch",
        Anm2ProvenanceStatus.FrameCountMismatch =>
            "frame_count_mismatch",
        _ => throw new InvalidOperationException(
            $"Unsupported ANM2 provenance status {Status}."),
    };
}

public static class Anm2ProvenanceCodec
{
    public const string Format =
        "dl-reanimated-anm2-provenance";
    public const int SchemaVersion = 1;
    public const int MaximumSidecarBytes = 1024 * 1024;
    public const int MaximumJsonDepth = 32;

    private const int MaximumAggregateTextCharacters =
        MaximumSidecarBytes / 2;
    private const int MaximumRetainedBoneCount = 16_384;
    private const int HashBufferSize = 64 * 1024;

    public static string GetSidecarPath(string anm2Path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anm2Path);
        return Path.GetFullPath(anm2Path) + ".dlrmeta.json";
    }

    public static Anm2ProvenanceDocument Create(
        ReadOnlySpan<byte> anm2Payload,
        string sourceFbx,
        string sourceFbxSha256,
        double sourceFbxFps,
        double sampleFps,
        double playbackFps,
        double sourceDurationSeconds,
        int frameCount,
        string rootMotionMode,
        string rootHeadingMode,
        string? sourceAnimationStack = null,
        string? fbxAnm2ExportBehavior = null,
        string? samplerContract = null,
        string? sourceTargetCompatibilityClass = null,
        IEnumerable<string>? bindRetainedBones = null,
        bool? wrapperReflectionDetected = null,
        bool? wrapperCanonicalized = null,
        IEnumerable<IEnumerable<double>>? wrapperMatrix = null,
        string? bilateralSemanticPolicy = null,
        bool? bilateralSwapApplied = null,
        int? bilateralSwappedRowCount = null,
        bool? postCanonicalizationMirrorConjugationApplied = null)
    {
        var document = new Anm2ProvenanceDocument
        {
            Anm2Sha256 =
                Convert.ToHexString(SHA256.HashData(anm2Payload)),
            SourceFbx = sourceFbx,
            SourceFbxSha256 =
                sourceFbxSha256?.ToUpperInvariant()!,
            SourceFbxFps = sourceFbxFps,
            SampleFps = sampleFps,
            PlaybackFps = playbackFps,
            SourceDurationSeconds = sourceDurationSeconds,
            FrameCount = frameCount,
            RootMotionMode = rootMotionMode,
            RootHeadingMode = rootHeadingMode,
            SourceAnimationStack =
                NullIfEmpty(sourceAnimationStack),
            FbxAnm2ExportBehavior =
                NullIfEmpty(fbxAnm2ExportBehavior),
            SamplerContract = NullIfEmpty(samplerContract),
            SourceTargetCompatibilityClass =
                NullIfEmpty(sourceTargetCompatibilityClass),
            BindRetainedBones = bindRetainedBones is null
                ? []
                : ToBoundedStrings(bindRetainedBones),
            WrapperReflectionDetected =
                wrapperReflectionDetected,
            WrapperCanonicalized = wrapperCanonicalized,
            WrapperMatrix = wrapperMatrix is null
                ? []
                : ToBoundedMatrix(wrapperMatrix),
            BilateralSemanticPolicy =
                NullIfEmpty(bilateralSemanticPolicy),
            BilateralSwapApplied = bilateralSwapApplied,
            BilateralSwappedRowCount =
                bilateralSwappedRowCount,
            PostCanonicalizationMirrorConjugationApplied =
                postCanonicalizationMirrorConjugationApplied,
        };
        ValidateDocument(document);
        return document;
    }

    public static Anm2ProvenanceLoadResult Load(
        string anm2Path,
        string? knownAnm2Sha256 = null,
        int? expectedFrameCount = null,
        CancellationToken cancellationToken = default)
    {
        string sourcePath = Path.GetFullPath(anm2Path);
        string sidecarPath = GetSidecarPath(sourcePath);
        if (!File.Exists(sidecarPath))
        {
            return new Anm2ProvenanceLoadResult(
                Anm2ProvenanceStatus.Missing,
                null,
                [],
                sidecarPath);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = ReadBoundedSidecar(
                sidecarPath,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument json = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    MaxDepth = MaximumJsonDepth,
                });
            Anm2ProvenanceDocument document =
                ParseDocument(json.RootElement);

            string actualHash = knownAnm2Sha256 is null
                ? ComputeFileSha256Hex(
                    sourcePath,
                    cancellationToken)
                : NormalizeSha256(
                    knownAnm2Sha256,
                    nameof(knownAnm2Sha256));
            if (!string.Equals(
                    document.Anm2Sha256,
                    actualHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Mismatch(
                    Anm2ProvenanceStatus.HashMismatch,
                    sidecarPath,
                    "its SHA-256 does not match the selected ANM2");
            }

            if (expectedFrameCount is not null &&
                document.FrameCount != expectedFrameCount.Value)
            {
                return Mismatch(
                    Anm2ProvenanceStatus.FrameCountMismatch,
                    sidecarPath,
                    "its frame count does not match the selected ANM2");
            }

            return new Anm2ProvenanceLoadResult(
                Anm2ProvenanceStatus.Valid,
                document,
                [],
                sidecarPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or OverflowException)
        {
            return Invalid(sidecarPath, exception.Message);
        }
    }

    public static string Write(
        string anm2Path,
        Anm2ProvenanceDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string sourcePath = Path.GetFullPath(anm2Path);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "The ANM2 provenance source does not exist.",
                sourcePath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        string actualHash = ComputeFileSha256Hex(
            sourcePath,
            cancellationToken);
        Anm2ProvenanceDocument renderedDocument =
            document with
            {
                Anm2Sha256 = actualHash,
            };
        ValidateDocument(renderedDocument);
        byte[] bytes = RenderCanonical(renderedDocument);
        if (bytes.Length > MaximumSidecarBytes)
        {
            throw new InvalidDataException(
                $"Rendered ANM2 provenance exceeds {MaximumSidecarBytes:N0} bytes.");
        }

        string destination = GetSidecarPath(sourcePath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException(
                "The ANM2 provenance path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.SequentialScan))
            {
                stream.Write(bytes);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(
                temporaryPath,
                destination,
                overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Anm2ProvenanceDocument ParseDocument(
        JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "metadata root is not an object");
        }

        if (!string.Equals(
                ReadRequiredString(root, "format"),
                Format,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "metadata format is not recognized");
        }

        JsonElement schema = ReadRequired(root, "schema_version");
        if (schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out int schemaVersion) ||
            schemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                "metadata schema version is not supported");
        }

        var document = new Anm2ProvenanceDocument
        {
            Anm2Sha256 = NormalizeSha256(
                ReadRequiredString(root, "anm2_sha256"),
                "anm2_sha256"),
            SourceFbx = ReadRequiredString(root, "source_fbx"),
            SourceFbxSha256 =
                ReadRequiredString(root, "source_fbx_sha256"),
            SourceFbxFps =
                ReadPositiveDouble(root, "source_fbx_fps"),
            SampleFps =
                ReadPositiveDouble(root, "sample_fps"),
            PlaybackFps =
                ReadPositiveDouble(root, "playback_fps"),
            SourceDurationSeconds =
                ReadNonnegativeDouble(
                    root,
                    "source_duration_seconds"),
            FrameCount =
                ReadPositiveInteger(root, "frame_count"),
            RootMotionMode =
                ReadRequiredString(root, "root_motion_mode"),
            RootHeadingMode =
                ReadRequiredString(root, "root_heading_mode"),
            SourceAnimationStack =
                ReadOptionalString(
                    root,
                    "source_animation_stack"),
            FbxAnm2ExportBehavior =
                ReadOptionalString(
                    root,
                    "fbx_anm2_export_behavior"),
            SamplerContract =
                ReadOptionalString(root, "sampler_contract"),
            SourceTargetCompatibilityClass =
                ReadOptionalString(
                    root,
                    "source_target_compatibility_class"),
            BindRetainedBones =
                ReadOptionalStringArray(
                    root,
                    "bind_retained_bones"),
            WrapperReflectionDetected =
                ReadOptionalBoolean(
                    root,
                    "wrapper_reflection_detected"),
            WrapperCanonicalized =
                ReadOptionalBoolean(
                    root,
                    "wrapper_canonicalized"),
            WrapperMatrix =
                ReadOptionalMatrix(root, "wrapper_matrix"),
            BilateralSemanticPolicy =
                ReadOptionalString(
                    root,
                    "bilateral_semantic_policy"),
            BilateralSwapApplied =
                ReadOptionalBoolean(
                    root,
                    "bilateral_swap_applied"),
            BilateralSwappedRowCount =
                ReadOptionalNonnegativeInteger(
                    root,
                    "bilateral_swapped_row_count"),
            PostCanonicalizationMirrorConjugationApplied =
                ReadOptionalBoolean(
                    root,
                    "post_canonicalization_mirror_conjugation_applied"),
        };
        ValidateDocument(document);
        return document;
    }

    private static byte[] RenderCanonical(
        Anm2ProvenanceDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder =
                           JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = true,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "anm2_sha256",
                document.Anm2Sha256);
            WriteOptionalString(
                writer,
                "bilateral_semantic_policy",
                document.BilateralSemanticPolicy);
            WriteOptionalBoolean(
                writer,
                "bilateral_swap_applied",
                document.BilateralSwapApplied);
            WriteOptionalNumber(
                writer,
                "bilateral_swapped_row_count",
                document.BilateralSwappedRowCount);
            if (!document.BindRetainedBones.IsDefaultOrEmpty)
            {
                writer.WritePropertyName("bind_retained_bones");
                writer.WriteStartArray();
                foreach (string bone in
                         document.BindRetainedBones)
                {
                    writer.WriteStringValue(bone);
                }

                writer.WriteEndArray();
            }

            WriteOptionalString(
                writer,
                "fbx_anm2_export_behavior",
                document.FbxAnm2ExportBehavior);
            writer.WriteString("format", Format);
            writer.WriteNumber(
                "frame_count",
                document.FrameCount);
            writer.WriteNumber(
                "playback_fps",
                document.PlaybackFps);
            WriteOptionalBoolean(
                writer,
                "post_canonicalization_mirror_conjugation_applied",
                document
                    .PostCanonicalizationMirrorConjugationApplied);
            writer.WriteString(
                "root_heading_mode",
                document.RootHeadingMode);
            writer.WriteString(
                "root_motion_mode",
                document.RootMotionMode);
            writer.WriteNumber(
                "sample_fps",
                document.SampleFps);
            WriteOptionalString(
                writer,
                "sampler_contract",
                document.SamplerContract);
            writer.WriteNumber(
                "schema_version",
                SchemaVersion);
            WriteOptionalString(
                writer,
                "source_animation_stack",
                document.SourceAnimationStack);
            writer.WriteNumber(
                "source_duration_seconds",
                document.SourceDurationSeconds);
            writer.WriteString(
                "source_fbx",
                document.SourceFbx);
            writer.WriteNumber(
                "source_fbx_fps",
                document.SourceFbxFps);
            writer.WriteString(
                "source_fbx_sha256",
                document.SourceFbxSha256);
            WriteOptionalString(
                writer,
                "source_target_compatibility_class",
                document.SourceTargetCompatibilityClass);
            WriteOptionalBoolean(
                writer,
                "wrapper_canonicalized",
                document.WrapperCanonicalized);
            if (!document.WrapperMatrix.IsDefaultOrEmpty)
            {
                writer.WritePropertyName("wrapper_matrix");
                writer.WriteStartArray();
                foreach (ImmutableArray<double> row in
                         document.WrapperMatrix)
                {
                    writer.WriteStartArray();
                    foreach (double value in row)
                    {
                        writer.WriteNumberValue(value);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
            }

            WriteOptionalBoolean(
                writer,
                "wrapper_reflection_detected",
                document.WrapperReflectionDetected);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void ValidateDocument(
        Anm2ProvenanceDocument document)
    {
        _ = NormalizeSha256(
            document.Anm2Sha256,
            nameof(document.Anm2Sha256));
        int aggregateTextLength = 0;
        AddRequiredText(
            document.SourceFbx,
            nameof(document.SourceFbx),
            ref aggregateTextLength);
        AddRequiredText(
            document.SourceFbxSha256,
            nameof(document.SourceFbxSha256),
            ref aggregateTextLength);
        AddRequiredText(
            document.RootMotionMode,
            nameof(document.RootMotionMode),
            ref aggregateTextLength);
        AddRequiredText(
            document.RootHeadingMode,
            nameof(document.RootHeadingMode),
            ref aggregateTextLength);
        AddOptionalText(
            document.SourceAnimationStack,
            nameof(document.SourceAnimationStack),
            ref aggregateTextLength);
        AddOptionalText(
            document.FbxAnm2ExportBehavior,
            nameof(document.FbxAnm2ExportBehavior),
            ref aggregateTextLength);
        AddOptionalText(
            document.SamplerContract,
            nameof(document.SamplerContract),
            ref aggregateTextLength);
        AddOptionalText(
            document.SourceTargetCompatibilityClass,
            nameof(document.SourceTargetCompatibilityClass),
            ref aggregateTextLength);
        AddOptionalText(
            document.BilateralSemanticPolicy,
            nameof(document.BilateralSemanticPolicy),
            ref aggregateTextLength);

        ValidatePositive(
            document.SourceFbxFps,
            nameof(document.SourceFbxFps));
        ValidatePositive(
            document.SampleFps,
            nameof(document.SampleFps));
        ValidatePositive(
            document.PlaybackFps,
            nameof(document.PlaybackFps));
        if (!double.IsFinite(
                document.SourceDurationSeconds) ||
            document.SourceDurationSeconds < 0.0)
        {
            throw new InvalidDataException(
                "source_duration_seconds must be finite and non-negative");
        }

        if (document.FrameCount < 1)
        {
            throw new InvalidDataException(
                "frame_count must be a positive integer");
        }

        if (document.FbxAnm2ExportBehavior is not null &&
            document.FbxAnm2ExportBehavior is not
                ("current" or "legacy_5_0"))
        {
            throw new InvalidDataException(
                "fbx_anm2_export_behavior must be current or legacy_5_0");
        }

        if (document.BilateralSemanticPolicy is not null &&
            document.BilateralSemanticPolicy is not
                ("auto"
                or "preserve_source_names"
                or "swap_bilateral_explicit"))
        {
            throw new InvalidDataException(
                "invalid bilateral_semantic_policy");
        }

        if (document.BilateralSwappedRowCount < 0)
        {
            throw new InvalidDataException(
                "bilateral_swapped_row_count must be a nonnegative integer");
        }

        if (!document.BindRetainedBones.IsDefaultOrEmpty)
        {
            foreach (string bone in
                     document.BindRetainedBones)
            {
                AddRequiredText(
                    bone,
                    "bind_retained_bones entry",
                    ref aggregateTextLength);
            }
        }

        if (!document.WrapperMatrix.IsDefaultOrEmpty)
        {
            if (document.WrapperMatrix.Length != 4 ||
                document.WrapperMatrix.Any(static row =>
                    row.IsDefault ||
                    row.Length != 4 ||
                    row.Any(static value =>
                        !double.IsFinite(value))))
            {
                throw new InvalidDataException(
                    "wrapper_matrix must be a finite 4x4 number matrix");
            }
        }

        if (aggregateTextLength >
            MaximumAggregateTextCharacters)
        {
            throw new InvalidDataException(
                "ANM2 provenance text exceeds its bounded aggregate length");
        }
    }

    private static JsonElement ReadRequired(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            throw new InvalidDataException(
                $"{propertyName} is required");
        }

        return value;
    }

    private static string ReadRequiredString(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a string");
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a string");
        }

        return value.GetString()!;
    }

    private static double ReadPositiveDouble(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) ||
            result <= 0.0)
        {
            throw new InvalidDataException(
                $"{propertyName} must be finite and positive");
        }

        return result;
    }

    private static double ReadNonnegativeDouble(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) ||
            result < 0.0)
        {
            throw new InvalidDataException(
                $"{propertyName} must be finite and non-negative");
        }

        return result;
    }

    private static int ReadPositiveInteger(
        JsonElement root,
        string propertyName)
    {
        JsonElement value = ReadRequired(root, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !TryReadInteger(value, out int result) ||
            result < 1)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a positive integer");
        }

        return result;
    }

    private static int? ReadOptionalNonnegativeInteger(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result) ||
            result < 0)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a nonnegative integer");
        }

        return result;
    }

    private static bool? ReadOptionalBoolean(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"{propertyName} must be a boolean"),
        };
    }

    private static ImmutableArray<string>
        ReadOptionalStringArray(
            JsonElement root,
            string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a list of strings");
        }

        if (value.GetArrayLength() >
            MaximumRetainedBoneCount)
        {
            throw new InvalidDataException(
                $"{propertyName} exceeds its bounded item count");
        }

        var result = ImmutableArray.CreateBuilder<string>(
            value.GetArrayLength());
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"{propertyName} must be a list of strings");
            }

            result.Add(item.GetString()!);
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<ImmutableArray<double>>
        ReadOptionalMatrix(
            JsonElement root,
            string propertyName)
    {
        if (!root.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() != 4)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a finite 4x4 number matrix");
        }

        var rows =
            ImmutableArray.CreateBuilder<ImmutableArray<double>>(4);
        foreach (JsonElement row in value.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array ||
                row.GetArrayLength() != 4)
            {
                throw new InvalidDataException(
                    $"{propertyName} must be a finite 4x4 number matrix");
            }

            var values = ImmutableArray.CreateBuilder<double>(4);
            foreach (JsonElement item in row.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number ||
                    !item.TryGetDouble(out double number) ||
                    !double.IsFinite(number))
                {
                    throw new InvalidDataException(
                        $"{propertyName} must be a finite 4x4 number matrix");
                }

                values.Add(number);
            }

            rows.Add(values.MoveToImmutable());
        }

        return rows.MoveToImmutable();
    }

    private static bool TryReadInteger(
        JsonElement value,
        out int result)
    {
        if (value.TryGetInt32(out result))
        {
            return true;
        }

        if (!value.TryGetDouble(out double floating) ||
            !double.IsFinite(floating) ||
            floating < int.MinValue ||
            floating > int.MaxValue ||
            floating != Math.Truncate(floating))
        {
            result = 0;
            return false;
        }

        result = (int)floating;
        return true;
    }

    private static void ValidatePositive(
        double value,
        string propertyName)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new InvalidDataException(
                $"{propertyName} must be finite and positive");
        }
    }

    private static void AddRequiredText(
        string value,
        string propertyName,
        ref int aggregateLength)
    {
        if (value is null)
        {
            throw new InvalidDataException(
                $"{propertyName} must be a string");
        }

        aggregateLength = checked(
            aggregateLength + value.Length);
    }

    private static void AddOptionalText(
        string? value,
        string propertyName,
        ref int aggregateLength)
    {
        if (value is null)
        {
            return;
        }

        AddRequiredText(
            value,
            propertyName,
            ref aggregateLength);
    }

    private static string NormalizeSha256(
        string value,
        string propertyName)
    {
        if (value is null ||
            value.Length != 64 ||
            value.Any(static character =>
                !IsHexCharacter(character)))
        {
            throw new InvalidDataException(
                $"{propertyName} must be a SHA-256 digest");
        }

        return value.ToUpperInvariant();
    }

    private static bool IsHexCharacter(char value) =>
        value is >= '0' and <= '9'
        or >= 'a' and <= 'f'
        or >= 'A' and <= 'F';

    private static string ComputeFileSha256Hex(
        string path,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HashBufferSize,
            FileOptions.SequentialScan);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[HashBufferSize];
        int count;
        while ((count = stream.Read(
                   buffer,
                   0,
                   buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static byte[] ReadBoundedSidecar(
        string path,
        CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HashBufferSize,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 ||
            stream.Length > MaximumSidecarBytes)
        {
            throw new InvalidDataException(
                $"metadata size must be between 1 and {MaximumSidecarBytes:N0} bytes");
        }

        using var output = new MemoryStream(
            checked((int)stream.Length));
        byte[] buffer = new byte[HashBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remaining = checked(
                MaximumSidecarBytes -
                (int)output.Length);
            if (remaining == 0)
            {
                if (stream.ReadByte() >= 0)
                {
                    throw new InvalidDataException(
                        $"metadata exceeds {MaximumSidecarBytes:N0} bytes");
                }

                break;
            }

            int count = stream.Read(
                buffer,
                0,
                Math.Min(buffer.Length, remaining));
            if (count == 0)
            {
                break;
            }

            output.Write(buffer, 0, count);
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException(
                "metadata is empty");
        }

        return output.ToArray();
    }

    private static ImmutableArray<string> ToBoundedStrings(
        IEnumerable<string> values)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (string value in values)
        {
            if (result.Count >= MaximumRetainedBoneCount)
            {
                throw new InvalidDataException(
                    "bind_retained_bones exceeds its bounded item count");
            }

            result.Add(value);
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<ImmutableArray<double>>
        ToBoundedMatrix(
            IEnumerable<IEnumerable<double>> rows)
    {
        var matrix =
            ImmutableArray.CreateBuilder<
                ImmutableArray<double>>(4);
        foreach (IEnumerable<double> row in rows)
        {
            if (matrix.Count >= 4)
            {
                throw new InvalidDataException(
                    "wrapper_matrix must be a finite 4x4 number matrix");
            }

            var values =
                ImmutableArray.CreateBuilder<double>(4);
            foreach (double value in row)
            {
                if (values.Count >= 4)
                {
                    throw new InvalidDataException(
                        "wrapper_matrix must be a finite 4x4 number matrix");
                }

                values.Add(value);
            }

            matrix.Add(values.ToImmutable());
        }

        return matrix.ToImmutable();
    }

    private static Anm2ProvenanceLoadResult Mismatch(
        Anm2ProvenanceStatus status,
        string sidecarPath,
        string reason) =>
        new(
            status,
            null,
            [
                $"{Path.GetFileName(sidecarPath)} was ignored because {reason}.",
            ],
            sidecarPath);

    private static Anm2ProvenanceLoadResult Invalid(
        string sidecarPath,
        string reason) =>
        new(
            Anm2ProvenanceStatus.Invalid,
            null,
            [
                $"{Path.GetFileName(sidecarPath)} was ignored: {reason}",
            ],
            sidecarPath);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value)
            ? null
            : value;

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is not null)
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteOptionalBoolean(
        Utf8JsonWriter writer,
        string propertyName,
        bool? value)
    {
        if (value.HasValue)
        {
            writer.WriteBoolean(
                propertyName,
                value.Value);
        }
    }

    private static void WriteOptionalNumber(
        Utf8JsonWriter writer,
        string propertyName,
        int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(
                propertyName,
                value.Value);
        }
    }
}
