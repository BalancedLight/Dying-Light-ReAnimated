using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public sealed class CustomModelFormatException : FormatException
{
    public CustomModelFormatException(string message)
        : base(message)
    {
    }

    public CustomModelFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Reads and atomically writes deterministic .dlrmodel containers. Entry
/// timestamps and ordering are fixed so the same document and payloads produce
/// the same package bytes.
/// </summary>
public static class CustomModelPackageSerializer
{
    public const long MaximumPackageBytes = 1024L * 1024L * 1024L;

    public const long MaximumSourceFbxBytes = 768L * 1024L * 1024L;

    public const long MaximumTextureBytes = 256L * 1024L * 1024L;

    private static readonly DateTimeOffset DeterministicTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static CustomModelPackage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumPackageBytes)
            {
                throw new CustomModelFormatException(
                    $"Custom-model packages cannot exceed {MaximumPackageBytes:N0} bytes.");
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            Dictionary<string, ZipArchiveEntry> entries = BuildEntryMap(archive);
            ZipArchiveEntry manifestEntry = GetRequiredEntry(entries, CustomModelPackage.ManifestEntryPath);
            if (manifestEntry.Length > 8L * 1024L * 1024L)
            {
                throw new CustomModelFormatException("The custom-model manifest is unreasonably large.");
            }

            CustomModelDocument document;
            using (Stream manifestStream = manifestEntry.Open())
            {
                document = JsonSerializer.Deserialize<CustomModelDocument>(
                    manifestStream,
                    SerializerOptions) ??
                    throw new CustomModelFormatException("The custom-model manifest was empty.");
            }

            document = MigrateToCurrent(document);
            document.Validate();
            ZipArchiveEntry sourceEntry = GetRequiredEntry(entries, document.Source.EmbeddedEntryPath);
            ImmutableArray<byte> sourceFbx = ReadBoundedEntry(sourceEntry, MaximumSourceFbxBytes);
            VerifySha256(sourceFbx.AsSpan(), document.Source.ContentSha256, document.Source.EmbeddedEntryPath);

            var texturePayloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(
                StringComparer.Ordinal);
            foreach (CustomModelTextureBinding texture in document.Materials
                         .SelectMany(static material => material.Textures)
                         .Where(static texture => texture.PackageEntryPath is not null)
                         .OrderBy(static texture => texture.PackageEntryPath, StringComparer.Ordinal))
            {
                string entryPath = texture.PackageEntryPath!;
                if (texturePayloads.ContainsKey(entryPath))
                {
                    continue;
                }

                ZipArchiveEntry textureEntry = GetRequiredEntry(entries, entryPath);
                ImmutableArray<byte> payload = ReadBoundedEntry(textureEntry, MaximumTextureBytes);
                VerifySha256(payload.AsSpan(), texture.ContentSha256, entryPath);
                texturePayloads.Add(entryPath, payload);
            }

            return new CustomModelPackage(document, sourceFbx, texturePayloads.ToImmutable());
        }
        catch (CustomModelFormatException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new CustomModelFormatException("The .dlrmodel ZIP container is invalid.", exception);
        }
        catch (JsonException exception)
        {
            throw new CustomModelFormatException("The custom-model manifest contains invalid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new CustomModelFormatException("The custom-model manifest contains invalid domain state.", exception);
        }
    }

    public static string SaveAtomic(CustomModelPackage package, string path)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ImmutableArray<byte> serialized = Serialize(package);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The model-package path must have a parent directory.", nameof(path));
        }

        EnsureExistingTargetCanBeReplaced(fullPath);
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough))
            {
                stream.Write(serialized.AsSpan());
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Materializes the deterministic package bytes used both by SaveAtomic
    /// and by project-source handoff. Keeping this path shared guarantees that
    /// embedded and user-selected textures, stack selections, cadence, and
    /// root policies survive a Models-to-Animate transition byte-for-byte.
    /// </summary>
    public static ImmutableArray<byte> Serialize(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Document.Validate();
        ValidatePayloads(package);

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteManifest(archive, package.Document);
            WriteEntry(
                archive,
                package.Document.Source.EmbeddedEntryPath,
                package.SourceFbx.AsSpan(),
                CompressionLevel.Optimal);
            foreach ((string entryPath, ImmutableArray<byte> payload) in package.TexturePayloads
                         .OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                WriteEntry(archive, entryPath, payload.AsSpan(), CompressionLevel.Optimal);
            }
        }

        if (stream.Length > MaximumPackageBytes)
        {
            throw new CustomModelFormatException(
                $"The serialized custom-model package exceeds {MaximumPackageBytes:N0} bytes.");
        }

        return ImmutableArray.Create(stream.ToArray());
    }

    /// <summary>
    /// Migrates package metadata in memory while leaving the embedded FBX and
    /// texture bytes untouched. Schema 1 had no authored-helper, camera, or
    /// morph inventory fields, so their schema-2 defaults are authoritative.
    /// </summary>
    public static CustomModelDocument MigrateToCurrent(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.Format,
                CustomModelDocument.CurrentFormat,
                StringComparison.Ordinal))
        {
            throw new CustomModelFormatException(
                $"Unsupported custom-model format '{document.Format}'.");
        }

        return document.SchemaVersion switch
        {
            CustomModelDocument.CurrentSchemaVersion => document,
            1 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                AuthoredHelpers = [],
                Camera = new CustomModelCameraMetadata(),
                MorphChannels = [],
                MorphSignature = CustomModelDocument.EmptyMorphSignature,
                RigConformance = null,
                SecondaryMotion = new(),
                FacialPresets = new(),
            },
            // Schema 2 predates rig conformance. An absent layer simply means
            // the model keeps its imported rig, so the migration is additive.
            2 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                RigConformance = null,
                SecondaryMotion = new(),
                FacialPresets = new(),
            },
            // Schema 3 has no model-owned secondary motion or facial preset library.
            3 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                SecondaryMotion = new(),
                FacialPresets = new(),
            },
            // Schema 4 already owns physics/presets; retain them. The new bank
            // reference switch defaults false so earlier authored behavior stays intact.
            4 => document with
            {
                SchemaVersion = CustomModelDocument.CurrentSchemaVersion,
                BuildSettings = document.BuildSettings with { ReferenceExistingAnimationLibrary = false },
            },
            _ => throw new CustomModelFormatException(
                $"Unsupported custom-model schema {document.SchemaVersion}; expected schema 1, 2, 3, 4, or {CustomModelDocument.CurrentSchemaVersion}."),
        };
    }

    private static Dictionary<string, ZipArchiveEntry> BuildEntryMap(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            CustomModelSourceIdentity.ValidatePackageEntryPath(path, nameof(archive));
            if (!entries.TryAdd(path, entry))
            {
                throw new CustomModelFormatException($"The .dlrmodel contains duplicate entry '{path}'.");
            }
        }

        return entries;
    }

    private static ZipArchiveEntry GetRequiredEntry(
        Dictionary<string, ZipArchiveEntry> entries,
        string path)
    {
        if (!entries.TryGetValue(path, out ZipArchiveEntry? entry))
        {
            throw new CustomModelFormatException($"The .dlrmodel is missing required entry '{path}'.");
        }

        return entry;
    }

    private static ImmutableArray<byte> ReadBoundedEntry(ZipArchiveEntry entry, long maximumBytes)
    {
        if (entry.Length < 0 || entry.Length > maximumBytes || entry.CompressedLength > MaximumPackageBytes)
        {
            throw new CustomModelFormatException(
                $"Entry '{entry.FullName}' exceeds its {maximumBytes:N0}-byte limit.");
        }

        if (entry.Length > int.MaxValue)
        {
            throw new CustomModelFormatException($"Entry '{entry.FullName}' cannot be materialized safely.");
        }

        var bytes = new byte[(int)entry.Length];
        using Stream input = entry.Open();
        int totalRead = 0;
        while (totalRead < bytes.Length)
        {
            int read = input.Read(bytes, totalRead, bytes.Length - totalRead);
            if (read == 0)
            {
                throw new CustomModelFormatException($"Entry '{entry.FullName}' ended unexpectedly.");
            }

            totalRead += read;
        }

        if (input.ReadByte() != -1)
        {
            throw new CustomModelFormatException($"Entry '{entry.FullName}' expanded beyond its declared size.");
        }

        return ImmutableArray.Create(bytes);
    }

    private static void ValidatePayloads(CustomModelPackage package)
    {
        if (package.SourceFbx.IsDefaultOrEmpty || package.SourceFbx.Length > MaximumSourceFbxBytes)
        {
            throw new ArgumentException("A model package must contain a bounded source FBX snapshot.", nameof(package));
        }

        VerifySha256(package.SourceFbx.AsSpan(), package.Document.Source.ContentSha256, "source FBX");
        HashSet<string> declaredPaths = package.Document.Materials
            .SelectMany(static material => material.Textures)
            .Where(static texture => texture.PackageEntryPath is not null)
            .Select(static texture => texture.PackageEntryPath!)
            .ToHashSet(StringComparer.Ordinal);
        foreach ((string entryPath, ImmutableArray<byte> payload) in package.TexturePayloads)
        {
            CustomModelSourceIdentity.ValidatePackageEntryPath(entryPath, nameof(package));
            if (!declaredPaths.Contains(entryPath))
            {
                throw new ArgumentException(
                    $"Texture payload '{entryPath}' is not declared by the model manifest.",
                    nameof(package));
            }

            if (payload.IsDefaultOrEmpty || payload.Length > MaximumTextureBytes)
            {
                throw new ArgumentException($"Texture payload '{entryPath}' is empty or too large.", nameof(package));
            }

            string expectedHash = package.Document.Materials
                .SelectMany(static material => material.Textures)
                .First(texture => string.Equals(texture.PackageEntryPath, entryPath, StringComparison.Ordinal))
                .ContentSha256;
            VerifySha256(payload.AsSpan(), expectedHash, entryPath);
        }

        foreach (string declaredPath in declaredPaths)
        {
            if (!package.TexturePayloads.ContainsKey(declaredPath))
            {
                throw new ArgumentException(
                    $"The manifest declares texture payload '{declaredPath}', but no bytes were supplied.",
                    nameof(package));
            }
        }
    }

    private static void VerifySha256(ReadOnlySpan<byte> payload, string expectedHash, string subject)
    {
        string actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomModelFormatException(
                $"The SHA-256 for '{subject}' is {actualHash}, expected {expectedHash}.");
        }
    }

    private static void EnsureExistingTargetCanBeReplaced(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            _ = Load(fullPath);
        }
        catch (CustomModelFormatException exception)
        {
            throw new CustomModelFormatException(
                "Refusing to overwrite an existing .dlrmodel that is not a valid DL ReAnimated C# schema-1/2 model package.",
                exception);
        }
    }

    private static void WriteManifest(ZipArchive archive, CustomModelDocument document)
    {
        ZipArchiveEntry entry = archive.CreateEntry(CustomModelPackage.ManifestEntryPath, CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicTimestamp;
        using Stream output = entry.Open();
        JsonSerializer.Serialize(output, document, SerializerOptions);
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        ReadOnlySpan<byte> payload,
        CompressionLevel compressionLevel)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, compressionLevel);
        entry.LastWriteTime = DeterministicTimestamp;
        using Stream output = entry.Open();
        output.Write(payload);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = CreateTypeInfoResolver(),
        };
        options.Converters.Add(new FrameRateJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static DefaultJsonTypeInfoResolver CreateTypeInfoResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            // These math types intentionally expose calculated diagnostic
            // getters to evaluators. Suppress only those getters: record-
            // constructor coordinates and exact matrix cells remain authored
            // package state and must round-trip.
            string[] calculatedProperties = typeInfo.Type switch
            {
                Type type when type == typeof(Vector3D) =>
                    ["LengthSquared", "Length", "IsFinite"],
                Type type when type == typeof(QuaternionD) =>
                    ["LengthSquared", "IsFinite"],
                Type type when type == typeof(TransformTRS) =>
                    ["IsFinite"],
                Type type when type == typeof(TransformMatrix) =>
                    ["Translation", "LinearDeterminant", "IsFinite"],
                _ => [],
            };
            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (calculatedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    property.ShouldSerialize = static (_, _) => false;
                }
            }
        });
        return resolver;
    }

    private sealed class FrameRateJsonConverter : JsonConverter<FrameRate>
    {
        public override FrameRate Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("A custom-model frame rate must be an object.");
            }

            int? numerator = null;
            int? denominator = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("A custom-model frame rate contains malformed JSON.");
                }

                string propertyName = reader.GetString() ?? string.Empty;
                if (!reader.Read())
                {
                    throw new JsonException("A custom-model frame rate ended unexpectedly.");
                }

                switch (propertyName)
                {
                    case "numerator":
                        numerator = reader.GetInt32();
                        break;
                    case "denominator":
                        denominator = reader.GetInt32();
                        break;
                    // Early development packages included this calculated
                    // getter. Accept it on read so those packages are not
                    // stranded, but never write it again.
                    case "framesPerSecond":
                        _ = reader.GetDouble();
                        break;
                    default:
                        throw new JsonException($"Unsupported frame-rate property '{propertyName}'.");
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || numerator is null || denominator is null)
            {
                throw new JsonException("A custom-model frame rate requires numerator and denominator.");
            }

            return new FrameRate(numerator.Value, denominator.Value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            FrameRate value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("numerator", value.Numerator);
            writer.WriteNumber("denominator", value.Denominator);
            writer.WriteEndObject();
        }
    }
}
