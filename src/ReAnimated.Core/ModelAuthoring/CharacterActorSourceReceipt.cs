using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>A reviewed source token, not evidence that a native consumer executes it.</summary>
public sealed record CharacterActorReferenceToken(int CallIndex, int ArgumentIndex,
    int SourceStart, int SourceLength, string Name);

/// <summary>Exact, portable actor-to-model source provenance. Runtime evidence remains separate.</summary>
public sealed record CharacterActorSourceReceipt
{
    public string ActorResourceId { get; init; } = string.Empty;
    public string ProviderIdentity { get; init; } = string.Empty;
    public string SourceFingerprint { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public int ModelDeclarationCallIndex { get; init; }
    public int ActorScopeCallIndex { get; init; } = -1;
    public int ModelArgumentIndex { get; init; }
    public string ExactModelName { get; init; } = string.Empty;
    public int ModelTokenStart { get; init; }
    public int ModelTokenLength { get; init; }
    public ImmutableArray<CharacterActorReferenceToken> ReviewedReferences { get; init; } = [];

    public void Validate(IReadOnlyCollection<CharacterResourceRecord> resources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ActorResourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProviderIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExactModelName);
        ProjectAssetReference.ValidateSha256(ContentSha256, nameof(ContentSha256));
        CharacterResourceRecord? source = resources.SingleOrDefault(r => r.Id == ActorResourceId);
        if (source is null || source.EntryPath is null || source.IsOriginalArchive ||
            source.ProviderIdentity != ProviderIdentity || source.SourceFingerprint != SourceFingerprint ||
            !string.Equals(source.ContentSha256, ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The reviewed actor source identity or content is stale.");
        if (ModelDeclarationCallIndex < 0 || ActorScopeCallIndex < -1 ||
            ModelArgumentIndex < 0 || ModelTokenStart < 0 || ModelTokenLength <= 0 ||
            (long)ModelTokenStart + ModelTokenLength > source.ByteLength ||
            ExactModelName.Length > 4096 || ReviewedReferences.IsDefault || ReviewedReferences.Length > 4096 ||
            ReviewedReferences.Select(r => (r.CallIndex, r.ArgumentIndex)).Distinct().Count() != ReviewedReferences.Length ||
            ReviewedReferences.Any(r => r.CallIndex < 0 || r.ArgumentIndex < 0 || r.SourceStart < 0 ||
                r.SourceLength <= 0 || (long)r.SourceStart + r.SourceLength > source.ByteLength ||
                string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 4096))
            throw new ArgumentException("The reviewed actor source tokens are invalid or exceed their bounds.");
    }
}
