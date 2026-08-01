using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Core.Domain;

namespace ReAnimated.Core.Project;

public class ProjectFormatException : FormatException
{
    public ProjectFormatException()
    {
    }

    public ProjectFormatException(string? message)
        : base(message)
    {
    }

    public ProjectFormatException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LegacyProjectFormatException : ProjectFormatException
{
    public LegacyProjectFormatException()
    {
    }

    public LegacyProjectFormatException(string? message)
        : base(message)
    {
    }

    public LegacyProjectFormatException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public LegacyProjectFormatException(int detectedSchemaVersion)
        : base(
            $"Project schema {detectedSchemaVersion} belongs to the legacy Python application. " +
            "This C# first pass neither imports, modifies, nor overwrites legacy projects; " +
            "create a fresh C# schema-1 project instead.")
    {
        DetectedSchemaVersion = detectedSchemaVersion;
    }

    public int? DetectedSchemaVersion { get; }
}

/// <summary>
/// Reads and atomically writes the fresh, DL1-only schema-1 project format.
/// </summary>
public static class ProjectSerializer
{
    public const long MaximumProjectBytes = 64L * 1024L * 1024L;

    private static readonly string[] RequiredRootProperties =
    [
        "schemaVersion",
        "format",
        "projectId",
        "name",
        "game",
        "assets",
        "animations",
        "dl1Settings",
        "previewMode",
        "previewProfile",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static DlraProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumProjectBytes)
            {
                throw new ProjectFormatException(
                    $"Project files cannot exceed {MaximumProjectBytes} bytes.");
            }

            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProjectFormatException("A project document must contain a JSON object.");
            }

            if (root.TryGetProperty("schema_version", out JsonElement legacySchemaElement))
            {
                int? legacyVersion = legacySchemaElement.TryGetInt32(out int detectedVersion)
                    ? detectedVersion
                    : null;
                throw legacyVersion.HasValue
                    ? new LegacyProjectFormatException(legacyVersion.Value)
                    : new LegacyProjectFormatException(
                        "A legacy snake_case schema marker is not accepted by the C# application.");
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement schemaElement) ||
                !schemaElement.TryGetInt32(out int schemaVersion))
            {
                throw new ProjectFormatException("The project does not declare an integer schemaVersion.");
            }

            if (schemaVersion != DlraProject.CurrentSchemaVersion)
            {
                throw new ProjectFormatException(
                    $"Project schema {schemaVersion} is not supported by this application.");
            }

            ValidateRequiredRootProperties(root);
            stream.Position = 0;
            DlraProject project = JsonSerializer.Deserialize<DlraProject>(
                stream,
                SerializerOptions) ??
                throw new ProjectFormatException("The project document was empty.");
            project = NormalizeSchema1(project);
            project.Validate();
            return project;
        }
        catch (ProjectFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProjectFormatException("The project contains invalid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException("The project contains invalid values.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProjectFormatException("The project contains invalid domain state.", exception);
        }
    }

    public static string SaveAtomic(DlraProject project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        project.Validate();

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The project path must have a parent directory.", nameof(path));
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
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, project, SerializerOptions);
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

    private static void EnsureExistingTargetCanBeReplaced(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return;
        }

        try
        {
            // A save may replace only a project already owned by this fresh
            // C# schema. In particular, a Save As choice must never turn the
            // file-dialog overwrite prompt into migration or destruction of
            // a legacy Python schema-1..10 project.
            _ = Load(fullPath);
        }
        catch (LegacyProjectFormatException)
        {
            throw;
        }
        catch (ProjectFormatException exception)
        {
            throw new ProjectFormatException(
                "Refusing to overwrite an existing .dlraproj that is not a valid " +
                "DL ReAnimated C# schema-1 project.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    /// <summary>
    /// Applies only additive C# schema-1 defaults. Local ANM2 sources are not
    /// guessed from the saved target: if their exact source model was not
    /// recorded by the creating build, SourceBinding intentionally remains
    /// null and the application offers signature candidates for Rebind Source.
    /// </summary>
    internal static DlraProject NormalizeSchema1(DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Dictionary<Guid, ProjectAssetReference> assets = project.Assets
            .ToDictionary(static asset => asset.Id);
        ImmutableArray<ProjectAnimation> animations = project.Animations
            .Select(animation => NormalizeAnimation(animation, assets))
            .ToImmutableArray();
        Guid? activeAnimationId = project.ActiveAnimationId;
        if (activeAnimationId is null && !animations.IsEmpty)
        {
            activeAnimationId = animations[0].Id;
        }

        return project with
        {
            Animations = animations,
            ActiveAnimationId = activeAnimationId,
        };
    }

    private static ProjectAnimation NormalizeAnimation(
        ProjectAnimation animation,
        Dictionary<Guid, ProjectAssetReference> assets)
    {
        ProjectAnimation normalized = animation;
        if (normalized.SourceBinding is null &&
            assets.TryGetValue(
                normalized.SourceAssetId,
                out ProjectAssetReference? source) &&
            string.Equals(
                Path.GetExtension(source.RelativePath),
                ".fbx",
                StringComparison.OrdinalIgnoreCase) &&
            normalized.SourceRigSignature is { Length: 64 } sourceSignature &&
            sourceSignature.All(static character => Uri.IsHexDigit(character)))
        {
            normalized = normalized with
            {
                SourceBinding = new ProjectAnimationSourceBinding
                {
                    Kind = AnimationSourceKind.LocalFbx,
                    AssetId = normalized.SourceAssetId,
                    Roles = AnimationSourceRoles.Body,
                    SourceRigSignature = sourceSignature,
                    TimingProvenance = AnimationTimingProvenance.EmbeddedFbx,
                },
            };
        }

        if (normalized.VariantGroupId is null &&
            normalized.SourceBinding is not null)
        {
            normalized = normalized with
            {
                VariantGroupId = AnimationVariantKey.CreateGroupId(
                    normalized,
                    assets),
            };
        }

        return normalized;
    }

    private static void ValidateRequiredRootProperties(
        JsonElement root)
    {
        foreach (string propertyName in RequiredRootProperties)
        {
            if (!root.TryGetProperty(propertyName, out _))
            {
                throw new ProjectFormatException(
                    $"The schema-1 project is missing required property '{propertyName}'.");
            }
        }
    }
}
