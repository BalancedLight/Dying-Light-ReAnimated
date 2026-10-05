using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Source-custody review for an explicitly selected actor script. The receipt
/// does not assert that a native actor executes any quoted resource reference.
/// </summary>
public static class CharacterActorSourceAuthoring
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CharacterActorSourceReviewResult Inspect(CustomModelPackage package, string actorResourceId,
        int modelCallIndex, int actorScopeCallIndex = -1)
    {
        (CharacterResourceRecord actor, CharacterResourceRecord root, string text) = ReadActor(package, actorResourceId);
        NativeCharacterScriptDocument syntax = Parse(text);
        if ((uint)modelCallIndex >= (uint)syntax.Calls.Length)
            throw new InvalidDataException("The selected model declaration call is outside the actor source.");
        NativeCharacterCall model = syntax.Calls[modelCallIndex];
        if (!CharacterActorSourceReview.IsSupportedModelDeclaration(syntax.Calls, modelCallIndex))
            throw new InvalidDataException("The selected source call is not a supported model declaration.");
        NativeCharacterQuotedArgument? modelName = CharacterActorSourceReview.GetModelNameArgument(model);
        if (modelName is null || !ModelNameMatchesRoot(modelName.Value, root.LogicalName))
            throw new InvalidDataException("The selected model token does not identify the exact inventory root.");
        CharacterActorCallExpectation? scope = null;
        if (actorScopeCallIndex >= 0)
        {
            if ((uint)actorScopeCallIndex >= (uint)syntax.Calls.Length)
                throw new InvalidDataException("The selected enclosing actor call is outside the source.");
            NativeCharacterCall owner = syntax.Calls[actorScopeCallIndex];
            scope = new(actorScopeCallIndex, owner.Name,
                owner.QuotedArguments.FirstOrDefault(q => q.ArgumentIndex == 0)?.Value);
        }
        return CharacterActorSourceReview.Review(text,
            new(actor.ContentSha256!, modelName.Value, modelCallIndex, scope,
                modelName.Start, modelName.Length));
    }

    public static CustomModelPackage Apply(CustomModelPackage package, string actorResourceId,
        CharacterActorSourceReviewResult reviewed, IEnumerable<CharacterActorReferenceExpectation> reviewedReferences)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(reviewedReferences);
        var (actor, _, text) = ReadActor(package, actorResourceId);
        CharacterActorSourceReviewResult current = Inspect(package, actorResourceId, reviewed.ModelCallIndex,
            reviewed.EnclosingActorCallIndex ?? -1);
        if (reviewed.SourceSha256 != current.SourceSha256 || reviewed.Document.Write() != text ||
            reviewed.ModelArgumentIndex != current.ModelArgumentIndex ||
            reviewed.ModelTokenStart != current.ModelTokenStart || reviewed.ModelTokenLength != current.ModelTokenLength ||
            reviewed.EnclosingActorCallIndex != current.EnclosingActorCallIndex)
            throw new InvalidDataException("The selected actor source review is stale.");
        CharacterActorReferenceExpectation[] selected = reviewedReferences.ToArray();
        if (selected.Length > 4096)
            throw new InvalidDataException("Reviewed actor source references exceed their bound.");
        string modelName = current.Document.Calls[current.ModelCallIndex].QuotedArguments
            .Single(q => q.ArgumentIndex == current.ModelArgumentIndex).Value;
        var request = new CharacterActorSourceReviewRequest(current.SourceSha256, modelName,
            current.ModelCallIndex, ActorExpectation(current), current.ModelTokenStart, current.ModelTokenLength);
        CharacterActorSourceReview.Revalidate(text, request, selected);
        if (actor.Status is not (CharacterDependencyStatus.Ambiguous or CharacterDependencyStatus.Preserved))
            throw new InvalidDataException("The selected actor source is not reviewable source custody.");

        CharacterResourceInventory inventory = package.Document.CharacterResources!;
        var receipt = new CharacterActorSourceReceipt
        {
            ActorResourceId = actorResourceId,
            ProviderIdentity = actor.ProviderIdentity,
            SourceFingerprint = actor.SourceFingerprint,
            ContentSha256 = actor.ContentSha256!,
            ModelDeclarationCallIndex = current.ModelCallIndex,
            ActorScopeCallIndex = current.EnclosingActorCallIndex ?? -1,
            ModelArgumentIndex = current.ModelArgumentIndex,
            ExactModelName = modelName,
            ModelTokenStart = current.ModelTokenStart,
            ModelTokenLength = current.ModelTokenLength,
            ReviewedReferences = selected.Select(r => new CharacterActorReferenceToken(
                r.CallIndex, r.ArgumentIndex, r.SourceStart, r.SourceLength, r.Value)).ToImmutableArray(),
        };
        ImmutableArray<CharacterResourceRecord> resources = inventory.Resources.Select(r => r.Id == actorResourceId &&
            r.Status == CharacterDependencyStatus.Ambiguous ? r with
            {
                Status = CharacterDependencyStatus.Preserved,
                Detail = "Exact actor source, model token and reviewed references retained; native actor and runtime behavior remain unverified.",
            } : r).ToImmutableArray();
        CharacterResourceInventory updated = inventory with
        {
            Resources = resources,
            ActorSourceReviews = inventory.ActorSourceReviews.Where(r =>
                r.ActorResourceId != actorResourceId || r.ModelDeclarationCallIndex != current.ModelCallIndex)
                .Append(receipt).ToImmutableArray(),
            CompiledSemanticSha256 = null,
            LoadedResourceSha256 = null,
            VerifiedPlayerScenarios = [],
        };
        updated.Validate();
        return package with
        {
            Document = package.Document with { CharacterResources = updated, LastBuildReceipt = null },
        };
    }

    public static void RevalidateAll(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        CharacterResourceInventory? inventory = package.Document.CharacterResources;
        if (inventory is null) return;
        if (inventory.ActorSourceReviews.IsDefault)
            throw new InvalidDataException("The actor source review collection is uninitialized.");
        if (inventory.ActorSourceReviews.IsEmpty) return;
        if (package.Document.Source.Kind != CustomModelSourceKind.StockCharacter)
            throw new InvalidDataException("A custom FBX target cannot own a stock actor source receipt.");
        try { inventory.Validate(); }
        catch (ArgumentException exception)
        { throw new InvalidDataException("A saved actor source review is stale or malformed.", exception); }
        foreach (CharacterActorSourceReceipt receipt in inventory.ActorSourceReviews)
        {
            var (actor, _, text) = ReadActor(package, receipt.ActorResourceId);
            if (actor.ProviderIdentity != receipt.ProviderIdentity ||
                actor.SourceFingerprint != receipt.SourceFingerprint ||
                !string.Equals(actor.ContentSha256, receipt.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A reviewed actor source provider or content identity changed.");
            CharacterActorSourceReviewResult inspected = Inspect(package, receipt.ActorResourceId,
                receipt.ModelDeclarationCallIndex, receipt.ActorScopeCallIndex);
            string actualName = inspected.Document.Calls[inspected.ModelCallIndex].QuotedArguments
                .Single(q => q.ArgumentIndex == inspected.ModelArgumentIndex).Value;
            if (receipt.ModelArgumentIndex != inspected.ModelArgumentIndex ||
                receipt.ExactModelName != actualName || receipt.ModelTokenStart != inspected.ModelTokenStart ||
                receipt.ModelTokenLength != inspected.ModelTokenLength ||
                receipt.ActorScopeCallIndex != (inspected.EnclosingActorCallIndex ?? -1))
                throw new InvalidDataException("A reviewed actor model declaration changed.");
            var request = new CharacterActorSourceReviewRequest(receipt.ContentSha256, receipt.ExactModelName,
                receipt.ModelDeclarationCallIndex, ActorExpectation(inspected),
                receipt.ModelTokenStart, receipt.ModelTokenLength);
            CharacterActorSourceReview.Revalidate(text, request, receipt.ReviewedReferences.Select(r =>
                new CharacterActorReferenceExpectation(r.CallIndex, r.ArgumentIndex, r.SourceStart,
                    r.SourceLength, r.Name)));
        }
    }

    private static CharacterActorCallExpectation? ActorExpectation(CharacterActorSourceReviewResult review)
    {
        if (review.EnclosingActorCallIndex is not { } index) return null;
        NativeCharacterCall owner = review.Document.Calls[index];
        return new(index, owner.Name, owner.QuotedArguments.FirstOrDefault(q => q.ArgumentIndex == 0)?.Value);
    }

    private static (CharacterResourceRecord Actor, CharacterResourceRecord Root, string Text) ReadActor(
        CustomModelPackage package, string actorResourceId)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorResourceId);
        if (package.Document.Source.Kind != CustomModelSourceKind.StockCharacter)
            throw new InvalidDataException("Actor source review requires an imported stock character, not a custom FBX target.");
        CharacterResourceInventory inventory = package.Document.CharacterResources ??
            throw new InvalidDataException("The stock character inventory is missing.");
        CharacterResourceRecord root = inventory.Resources.SingleOrDefault(r => r.Id == inventory.RootResourceId) ??
            throw new InvalidDataException("The exact model root record is missing.");
        CharacterResourceRecord actor = inventory.Resources.SingleOrDefault(r => r.Id == actorResourceId) ??
            throw new InvalidDataException("The selected actor source record is missing.");
        string actorExtension = Path.GetExtension(actor.LogicalName).ToLowerInvariant();
        if (actor.Id == root.Id || actor.IsOriginalArchive || actor.Id.StartsWith("original:", StringComparison.Ordinal) ||
            actor.Status is not (CharacterDependencyStatus.Ambiguous or CharacterDependencyStatus.Preserved) ||
            actorExtension is not (".scr" or ".def" or ".pre") ||
            actor.EntryPath is null || string.IsNullOrWhiteSpace(actor.ProviderIdentity) ||
            string.IsNullOrWhiteSpace(actor.SourceFingerprint))
            throw new InvalidDataException("The selected record is not a supported original actor source.");
        ImmutableArray<byte> payload = VerifyPayload(package, actor);
        ImmutableArray<byte> rootPayload = VerifyPayload(package, root);
        if (!rootPayload.AsSpan().SequenceEqual(package.SourceFbx.AsSpan()) ||
            !Convert.ToHexStringLower(SHA256.HashData(rootPayload.AsSpan()))
                .Equals(package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The model root no longer matches its immutable stock source.");
        try { return (actor, root, StrictUtf8.GetString(payload.AsSpan())); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("The actor source is not valid UTF-8.", exception); }
    }

    private static ImmutableArray<byte> VerifyPayload(CustomModelPackage package, CharacterResourceRecord record)
    {
        if (record.EntryPath is null || !package.CompanionPayloads.TryGetValue(record.EntryPath, out ImmutableArray<byte> payload) ||
            payload.IsDefault || payload.Length != record.ByteLength || record.ContentSha256 is null ||
            !Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan()))
                .Equals(record.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The exact source payload does not match its recorded content hash.");
        return payload;
    }

    private static NativeCharacterScriptDocument Parse(string text)
    {
        try { return NativeCharacterScriptCodec.Parse(text); }
        catch (FormatException exception)
        { throw new InvalidDataException("The selected actor source syntax is malformed.", exception); }
    }

    public static bool ModelNameMatchesRoot(string modelName, string rootLogicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLogicalName);
        return NormalizeModelName(modelName).Equals(NormalizeModelName(rootLogicalName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelName(string value)
    {
        string normalized = value.Replace('\\', '/');
        string extension = Path.GetExtension(normalized).ToLowerInvariant();
        return extension is ".msh" or ".msh_obj" or ".skn"
            ? normalized[..^extension.Length] : normalized;
    }
}
