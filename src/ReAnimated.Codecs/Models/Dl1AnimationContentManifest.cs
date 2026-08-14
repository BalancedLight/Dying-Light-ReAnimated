using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public enum Dl1AnimationContentArtifactRole
{
    AliasScript,
    AnimationScript,
    Animation,
    AnimationRuntimePack,

    [Obsolete("Use AnimationRuntimePack. This alias remains for schema-1 source compatibility.")]
    FallbackRPack = AnimationRuntimePack,
}

public sealed record Dl1AnimationContentArtifact(
    Dl1AnimationContentArtifactRole Role,
    string RelativePath,
    string Sha256,
    string ResourceIdentity);

public sealed record Dl1AnimationContentManifest
{
    public const string CurrentFormat = "dl-reanimated-animation-content-manifest";
    public const int CurrentSchemaVersion = 2;
    public const int MaximumArtifactCount = 128;

    public string Format { get; init; } = CurrentFormat;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string DeploymentId { get; init; }
    public required string CharacterId { get; init; }
    public required string ModelResourceName { get; init; }
    public required string AnimationLibraryName { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public ImmutableArray<Dl1AnimationContentArtifact> Artifacts { get; init; } = [];
}
