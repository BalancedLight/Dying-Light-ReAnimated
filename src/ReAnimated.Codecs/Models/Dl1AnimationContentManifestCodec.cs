using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1AnimationContentManifestBytes(
    Dl1AnimationContentManifest Manifest,
    ImmutableArray<byte> Utf8Json,
    string Sha256,
    string ManifestRelativePath);

public static partial class Dl1DeveloperToolsProjectDeployer
{
    public const string AnimationRefreshDirectoryRelativePath =
        ".dl-reanimated/animation-refresh";

    private static readonly JsonSerializerOptions AnimationContentManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new AnimationContentArtifactRoleJsonConverter(),
            new CanonicalUtcDateTimeOffsetJsonConverter(),
        },
    };

    public static string GetAnimationRuntimePackRelativePath(ReadOnlySpan<byte> rpackBytes)
    {
        if (rpackBytes.IsEmpty)
        {
            throw new InvalidDataException("The animation runtime RPack is empty.");
        }

        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(rpackBytes));
        return $"{AnimationRefreshDirectoryRelativePath}/packages/{fingerprint}.rpack";
    }

    [Obsolete("Use GetAnimationRuntimePackRelativePath.")]
    public static string GetAnimationFallbackRpackRelativePath(ReadOnlySpan<byte> rpackBytes) =>
        GetAnimationRuntimePackRelativePath(rpackBytes);

    /// <param name="declaresAnimations">
    /// Whether this deployment ships sequences of its own. A character staged
    /// to drive an existing base-game bank publishes the alias script, the
    /// loose animation script and the runtime pack, but no ANM2 - so the
    /// loose-ANM2 requirement is stated by the caller rather than assumed.
    /// </param>
    public static Dl1AnimationContentManifestBytes CreateAnimationContentManifest(
        string deploymentId,
        string characterId,
        string modelResourceName,
        string animationLibraryName,
        DateTimeOffset createdUtc,
        IEnumerable<Dl1AnimationContentArtifact> artifacts,
        bool declaresAnimations = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(animationLibraryName);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!string.Equals(NormalizeCharacterId(characterId), characterId, StringComparison.Ordinal) ||
            !string.Equals(
                Dl1SourceModelWriter.RequireExactResourceName(modelResourceName, 55, "model resource name"),
                modelResourceName,
                StringComparison.Ordinal) ||
            !string.Equals(
                Dl1SourceModelWriter.RequireExactResourceName(animationLibraryName, 63, "animation library name"),
                animationLibraryName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Animation content manifest identities are not canonical.");
        }
        if (!Regex.IsMatch(deploymentId, "^[0-9a-f]{24}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("Animation content deployment ID is not canonical.");
        }

        ImmutableArray<Dl1AnimationContentArtifact> rows = artifacts.ToImmutableArray();
        if (rows.IsDefaultOrEmpty || rows.Length > Dl1AnimationContentManifest.MaximumArtifactCount)
        {
            throw new InvalidDataException(
                $"Animation refresh manifests support between 1 and " +
                $"{Dl1AnimationContentManifest.MaximumArtifactCount:N0} artifacts.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var animationIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Dl1AnimationContentArtifact artifact in rows)
        {
            ValidateAnimationManifestRelativePath(artifact.RelativePath);
            if (!Regex.IsMatch(artifact.Sha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(artifact.ResourceIdentity) ||
                artifact.ResourceIdentity.Length > 63 ||
                artifact.ResourceIdentity.IndexOfAny(['/', '\\', ':', '\r', '\n']) >= 0)
            {
                throw new InvalidDataException(
                    $"Animation content manifest artifact '{artifact.RelativePath}' has invalid identity or hash metadata.");
            }

            if (artifact.Role == Dl1AnimationContentArtifactRole.Animation &&
                !animationIdentities.Add(artifact.ResourceIdentity))
            {
                throw new InvalidDataException(
                    $"Animation content manifest repeats animation identity '{artifact.ResourceIdentity}'.");
            }

            if (!paths.Add(artifact.RelativePath))
            {
                throw new InvalidDataException(
                    $"Animation content manifest repeats '{artifact.RelativePath}'.");
            }
        }

        RequireManifestRoleCount(rows, Dl1AnimationContentArtifactRole.AliasScript, 1);
        RequireManifestRoleCount(rows, Dl1AnimationContentArtifactRole.AnimationScript, 1);
        RequireManifestRoleCount(rows, Dl1AnimationContentArtifactRole.AnimationRuntimePack, 1);
        bool hasAnimationRow = rows.Any(static artifact =>
            artifact.Role == Dl1AnimationContentArtifactRole.Animation);
        if (declaresAnimations && !hasAnimationRow)
        {
            throw new InvalidDataException("Animation content manifest contains no loose ANM2 artifact.");
        }

        if (!declaresAnimations && hasAnimationRow)
        {
            throw new InvalidDataException(
                "Animation content manifest declares no animations but carries a loose ANM2 artifact.");
        }

        foreach (Dl1AnimationContentArtifact artifact in rows)
        {
            string expectedPath = artifact.Role switch
            {
                Dl1AnimationContentArtifactRole.AliasScript =>
                    $"data/characters/{characterId}/{modelResourceName}.ascr",
                Dl1AnimationContentArtifactRole.AnimationScript =>
                    $"data/characters/animations/animscripts/{animationLibraryName}.scr",
                Dl1AnimationContentArtifactRole.Animation =>
                    $"data/characters/animations/{artifact.ResourceIdentity}.anm2",
                Dl1AnimationContentArtifactRole.AnimationRuntimePack =>
                    $"{AnimationRefreshDirectoryRelativePath}/packages/{artifact.Sha256}.rpack",
                _ => throw new InvalidDataException("Animation content manifest has an unknown artifact role."),
            };
            if (!string.Equals(artifact.RelativePath, expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Animation content {artifact.Role} path '{artifact.RelativePath}' must be '{expectedPath}'.");
            }

            string? expectedIdentity = artifact.Role switch
            {
                Dl1AnimationContentArtifactRole.AliasScript => modelResourceName,
                Dl1AnimationContentArtifactRole.AnimationScript => animationLibraryName,
                Dl1AnimationContentArtifactRole.AnimationRuntimePack => animationLibraryName,
                _ => null,
            };
            if (expectedIdentity is not null &&
                !string.Equals(
                    artifact.ResourceIdentity,
                    expectedIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Animation content {artifact.Role} identity '{artifact.ResourceIdentity}' " +
                    $"must be '{expectedIdentity}'.");
            }
        }

        var manifest = new Dl1AnimationContentManifest
        {
            DeploymentId = deploymentId,
            CharacterId = characterId,
            ModelResourceName = modelResourceName,
            AnimationLibraryName = animationLibraryName,
            CreatedUtc = createdUtc,
            Artifacts = rows,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            AnimationContentManifestJsonOptions);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new Dl1AnimationContentManifestBytes(
            manifest,
            bytes.ToImmutableArray(),
            hash,
            $"{AnimationRefreshDirectoryRelativePath}/manifests/{deploymentId}.json");
    }

    private sealed class CanonicalUtcDateTimeOffsetJsonConverter
        : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            if (!DateTimeOffset.TryParseExact(
                    value,
                    Format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset parsed))
            {
                throw new JsonException(
                    "Animation content manifest UTC timestamp is not canonical.");
            }

            return parsed;
        }

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(
                value.ToUniversalTime().ToString(
                    Format,
                    CultureInfo.InvariantCulture));
    }

    private sealed class AnimationContentArtifactRoleJsonConverter
        : JsonConverter<Dl1AnimationContentArtifactRole>
    {
        public override Dl1AnimationContentArtifactRole Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            return value switch
            {
                nameof(Dl1AnimationContentArtifactRole.AliasScript) =>
                    Dl1AnimationContentArtifactRole.AliasScript,
                nameof(Dl1AnimationContentArtifactRole.AnimationScript) =>
                    Dl1AnimationContentArtifactRole.AnimationScript,
                nameof(Dl1AnimationContentArtifactRole.Animation) =>
                    Dl1AnimationContentArtifactRole.Animation,
                nameof(Dl1AnimationContentArtifactRole.AnimationRuntimePack) or
                    "FallbackRPack" =>
                    Dl1AnimationContentArtifactRole.AnimationRuntimePack,
                _ => throw new JsonException(
                    $"Unknown animation content artifact role '{value}'."),
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            Dl1AnimationContentArtifactRole value,
            JsonSerializerOptions options)
        {
            string name = value switch
            {
                Dl1AnimationContentArtifactRole.AliasScript =>
                    nameof(Dl1AnimationContentArtifactRole.AliasScript),
                Dl1AnimationContentArtifactRole.AnimationScript =>
                    nameof(Dl1AnimationContentArtifactRole.AnimationScript),
                Dl1AnimationContentArtifactRole.Animation =>
                    nameof(Dl1AnimationContentArtifactRole.Animation),
                Dl1AnimationContentArtifactRole.AnimationRuntimePack =>
                    nameof(Dl1AnimationContentArtifactRole.AnimationRuntimePack),
                _ => throw new JsonException(
                    $"Unknown animation content artifact role '{value}'."),
            };
            writer.WriteStringValue(name);
        }
    }

    private static void RequireManifestRoleCount(
        ImmutableArray<Dl1AnimationContentArtifact> rows,
        Dl1AnimationContentArtifactRole role,
        int requiredCount)
    {
        int actual = rows.Count(artifact => artifact.Role == role);
        if (actual != requiredCount)
        {
            throw new InvalidDataException(
                $"Animation content manifest requires exactly {requiredCount:N0} {role} artifact(s); found {actual:N0}.");
        }
    }

    private static void ValidateAnimationManifestRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.Contains('\\') ||
            path.Contains(':') ||
            path.StartsWith('/') ||
            path.EndsWith('/') ||
            path.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or ".." ||
                segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new InvalidDataException(
                $"Animation content path '{path}' is not a canonical project-relative path.");
        }
    }
}
