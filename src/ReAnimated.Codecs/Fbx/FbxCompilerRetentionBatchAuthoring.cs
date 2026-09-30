using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>An all-or-nothing, explicit batch proposal for compiler-retention helpers.</summary>
public sealed class FbxCompilerRetentionBatchPreview
{
    internal FbxCompilerRetentionBatchPreview(FbxModelAuthoringImportResult source,
        FbxModelAuthoringImportResult candidate, Guid modelId, RiggingJobToken sessionToken,
        ImmutableArray<Guid> parentEntityIds, ImmutableArray<Guid> addedHelperIds)
    {
        Source = source;
        Candidate = candidate;
        ModelId = modelId;
        SessionToken = sessionToken;
        ParentEntityIds = parentEntityIds;
        AddedHelperIds = addedHelperIds;
    }

    internal FbxModelAuthoringImportResult Source { get; }
    internal RiggingJobToken SessionToken { get; }
    public FbxModelAuthoringImportResult Candidate { get; }
    public Guid ModelId { get; }
    public Guid SessionId => SessionToken.SessionId;
    public ImmutableArray<Guid> ParentEntityIds { get; }
    public ImmutableArray<Guid> AddedHelperIds { get; }
    public int AddedHelperCount => AddedHelperIds.Length;
    public string Summary => $"Preview only: propose {AddedHelperCount} compiler-retention helpers. Compile the candidate to verify retention, then validate native behavior.";
    public bool HasChanges => !ReferenceEquals(Source, Candidate);

    /// <summary>Adapts the batch candidate for the existing single-entity viewport overlay.</summary>
    /// <remarks>The overlay focuses the first deterministically ordered parent; Candidate still contains the full batch.</remarks>
    public StructuralHelperPreview ToStructuralHelperPreview() =>
        new(Source, Candidate, ParentEntityIds[0], Summary) { AddedHelperId = AddedHelperIds[0] };
}

/// <summary>
/// Builds explicit retention proposals by composing the single-node authoring operation.
/// This does not select eligible nodes automatically or establish compiler/native retention.
/// </summary>
public static class FbxCompilerRetentionBatchAuthoring
{
    public const int MaximumBatchSize = 32;

    public static FbxCompilerRetentionBatchPreview Preview(FbxModelAuthoringImportResult model,
        IEnumerable<Guid> parentEntityIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(parentEntityIds);
        cancellationToken.ThrowIfCancellationRequested();

        var requestedBuilder = ImmutableArray.CreateBuilder<Guid>();
        var seen = new HashSet<Guid>();
        foreach (Guid parentId in parentEntityIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requestedBuilder.Count == MaximumBatchSize)
                throw new ArgumentOutOfRangeException(nameof(parentEntityIds), $"A retention batch may contain at most {MaximumBatchSize} parents.");
            if (parentId == Guid.Empty)
                throw new ArgumentException("Retention parent identifiers must be non-empty.", nameof(parentEntityIds));
            if (!seen.Add(parentId))
                throw new ArgumentException("A retention parent may appear only once in a batch.", nameof(parentEntityIds));
            requestedBuilder.Add(parentId);
        }
        if (requestedBuilder.Count == 0)
            throw new ArgumentException("Select at least one retention parent explicitly.", nameof(parentEntityIds));

        // Sorting makes the result and generated helper ordering stable for equivalent explicit selections.
        ImmutableArray<Guid> ordered = requestedBuilder.ToImmutable().Order().ToImmutableArray();
        CustomModelDocument originalDocument = model.Package.Document;
        RiggingSession originalSession = originalDocument.RiggingSession
            ?? throw new InvalidOperationException("Start a studio session before proposing compiler retention.");
        Guid modelId = originalDocument.ModelId;
        RiggingJobToken sourceSessionToken = originalSession.CreateJobToken();
        if (originalSession.OwnerModelId != modelId)
            throw new InvalidOperationException("The current studio session belongs to a different model.");

        FbxModelAuthoringImportResult candidate = model;
        var helperIds = ImmutableArray.CreateBuilder<Guid>(ordered.Length);
        foreach (Guid parentId in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureModelSession(candidate, modelId, sourceSessionToken.SessionId);
            StructuralHelperPreview single = FbxCompilerRetentionAuthoring.Preview(candidate, parentId, cancellationToken);
            if (single.AddedHelperId is not { } helperId)
                throw new InvalidDataException("A single-node retention proposal did not report its added helper identifier.");
            candidate = single.Candidate;
            EnsureModelSession(candidate, modelId, sourceSessionToken.SessionId);
            helperIds.Add(helperId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(model, candidate, modelId, sourceSessionToken, ordered, helperIds.ToImmutable());
    }

    /// <summary>Applies a completed preview only to the exact source model and session snapshot.</summary>
    public static bool TryApply(FbxModelAuthoringImportResult current, FbxCompilerRetentionBatchPreview preview,
        out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(preview);
        result = current;
        if (!ReferenceEquals(current, preview.Source) || current.Package.Document.ModelId != preview.ModelId ||
            current.Package.Document.RiggingSession is not { } session ||
            !session.Matches(preview.SessionToken))
            return false;
        result = preview.Candidate;
        return true;
    }

    private static void EnsureModelSession(FbxModelAuthoringImportResult model, Guid modelId, Guid sessionId)
    {
        CustomModelDocument document = model.Package.Document;
        if (document.ModelId != modelId || document.RiggingSession is not { } session ||
            session.Id != sessionId || session.OwnerModelId != modelId)
            throw new InvalidOperationException("The retention batch no longer targets the current model and studio session.");
    }
}
