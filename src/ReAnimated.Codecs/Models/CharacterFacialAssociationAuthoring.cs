using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record ReviewedCharacterFacialAssociation(string ActorResourceId, int ModelCallIndex,
    int ActorScopeCallIndex, int MimicSetFieldCallIndex, string MimicSourceResourceId,
    string ScanReceiptResourceId, string AbsenceEvidenceResourceId, string ProfileModuleSha256,
    string ConstructorAssessmentSha256, string ConsumerAssessmentSha256, bool Reviewed)
{
    public string Profile { get; init; } = CharacterFacialAssociationReceipt.HumanConstructorEmptyProfile;
}

public static class CharacterFacialAssociationAuthoring
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CustomModelPackage Review(CustomModelPackage package, ReviewedCharacterFacialAssociation proposal)
    {
        ArgumentNullException.ThrowIfNull(package); ArgumentNullException.ThrowIfNull(proposal);
        if (!proposal.Reviewed) throw new InvalidOperationException("Review the actor facial association first.");
        _ = CustomModelPackageSerializer.Serialize(package);
        CharacterActorSourceAuthoring.RevalidateAll(package);
        var inventory = package.Document.CharacterResources ?? throw new InvalidDataException("Character inventory is missing.");
        var actorReview = inventory.ActorSourceReviews.SingleOrDefault(r => r.ActorResourceId == proposal.ActorResourceId &&
            r.ModelDeclarationCallIndex == proposal.ModelCallIndex && r.ActorScopeCallIndex == proposal.ActorScopeCallIndex)
            ?? throw new InvalidDataException("Save the exact actor model review first.");
        var root = Record(package, inventory.RootResourceId);
        var actor = Record(package, proposal.ActorResourceId);
        var mimic = Record(package, proposal.MimicSourceResourceId);
        var scan = Record(package, proposal.ScanReceiptResourceId);
        var absence = Record(package, proposal.AbsenceEvidenceResourceId);
        var actorSyntax = NativeCharacterScriptCodec.Parse(Text(package, actor));
        var selected = ReadSelectedMimic(actorSyntax, proposal.ActorScopeCallIndex, proposal.MimicSetFieldCallIndex);
        string derived = Path.GetFileNameWithoutExtension(actorReview.ExactModelName.Replace('\\', '/')) + ".fed";
        var mimicSyntax = NativeCharacterScriptCodec.Parse(Text(package, mimic));
        bool found = MimicFound(mimicSyntax, selected.Value);
        string snapshot = ValidateCatalog(package, root, scan, absence, derived);
        var receipt = new CharacterFacialAssociationReceipt
        {
            Profile = proposal.Profile, Reviewed = true, ActorResourceId = actor.Id, ActorSourceSha256 = actor.ContentSha256!,
            ModelDeclarationCallIndex = actorReview.ModelDeclarationCallIndex, ActorScopeCallIndex = actorReview.ActorScopeCallIndex,
            ExactModelName = actorReview.ExactModelName, RootResourceId = root.Id, RootSourceSha256 = root.ContentSha256!,
            MimicSetFieldCallIndex = proposal.MimicSetFieldCallIndex, MimicSetArgumentIndex = selected.ArgumentIndex,
            MimicSetTokenStart = selected.Start, MimicSetTokenLength = selected.Length, SelectedMimicName = selected.Value,
            MimicSourceResourceId = mimic.Id, MimicSourceSha256 = mimic.ContentSha256!, MimicLookupFound = found, DerivedFedName = derived,
            ScanReceiptResourceId = scan.Id, ScanReceiptSha256 = scan.ContentSha256!, CatalogSnapshotSha256 = snapshot,
            AbsenceEvidenceResourceId = absence.Id, AbsenceEvidenceSha256 = absence.ContentSha256!, ProfileModuleSha256 = proposal.ProfileModuleSha256,
            ConstructorAssessmentSha256 = proposal.ConstructorAssessmentSha256, ConsumerAssessmentSha256 = proposal.ConsumerAssessmentSha256,
        };
        var updated = inventory with { FacialAssociationReviews = inventory.FacialAssociationReviews.Where(r =>
            r.ActorResourceId != actor.Id || r.ModelDeclarationCallIndex != proposal.ModelCallIndex || r.ActorScopeCallIndex != proposal.ActorScopeCallIndex)
            .Append(receipt).ToImmutableArray(), CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [] };
        receipt.Validate(updated);
        var result = package with { Document = package.Document with { CharacterResources = updated, LastBuildReceipt = null } };
        Revalidate(result); return result;
    }

    public static void Revalidate(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Document.CharacterResources is not { } inventory || inventory.FacialAssociationReviews.IsEmpty) return;
        _ = CustomModelPackageSerializer.Serialize(package); CharacterActorSourceAuthoring.RevalidateAll(package);
        if (package.Document.Source.Kind != CustomModelSourceKind.StockCharacter)
            throw new InvalidDataException("Facial source association requires a stock character.");
        foreach (var receipt in inventory.FacialAssociationReviews)
        {
            receipt.Validate(inventory);
            var actor = Record(package, receipt.ActorResourceId);
            var selected = ReadSelectedMimic(NativeCharacterScriptCodec.Parse(Text(package, actor)), receipt.ActorScopeCallIndex, receipt.MimicSetFieldCallIndex);
            if (selected.Value != receipt.SelectedMimicName || selected.Start != receipt.MimicSetTokenStart || selected.Length != receipt.MimicSetTokenLength)
                throw new InvalidDataException("The selected actor mimic token changed.");
            string derived = Path.GetFileNameWithoutExtension(receipt.ExactModelName.Replace('\\', '/')) + ".fed";
            var mimic = Record(package, receipt.MimicSourceResourceId);
            if (derived != receipt.DerivedFedName || MimicFound(NativeCharacterScriptCodec.Parse(Text(package, mimic)), selected.Value) != receipt.MimicLookupFound)
                throw new InvalidDataException("The facial lookup association changed.");
            string snapshot = ValidateCatalog(package, Record(package, receipt.RootResourceId), Record(package, receipt.ScanReceiptResourceId),
                Record(package, receipt.AbsenceEvidenceResourceId), derived);
            if (snapshot != receipt.CatalogSnapshotSha256) throw new InvalidDataException("The reviewed facial catalog changed.");
        }
    }

    private static NativeCharacterQuotedArgument ReadSelectedMimic(NativeCharacterScriptDocument syntax, int scope, int index)
    {
        if ((uint)scope >= (uint)syntax.Calls.Length || (uint)index >= (uint)syntax.Calls.Length || syntax.Calls[scope].Name != "Preset")
            throw new InvalidDataException("Select one actor preset scope.");
        var fields = syntax.Calls.Select((call, i) => (call, i)).Where(row => row.call.ParentCallIndex == scope && row.call.Name == "SetField").ToArray();
        if (fields.Any(row => row.call.QuotedArguments.Any(a => a.ArgumentIndex == 0 && a.Value == "m_FaceMimicFile")))
            throw new InvalidDataException("An explicit facial filename requires its own resource review.");
        var selected = fields.Where(row => row.call.QuotedArguments.Any(a => a.ArgumentIndex == 0 && a.Value == "m_FaceMimicPreset")).ToArray();
        if (selected.Length != 1 || selected[0].i != index)
            throw new InvalidDataException("The selected actor mimic setting is missing or ambiguous.");
        return selected[0].call.QuotedArguments.SingleOrDefault(a => a.ArgumentIndex == 1)
            ?? throw new InvalidDataException("The actor mimic name must be one complete quoted token.");
    }

    private static bool MimicFound(NativeCharacterScriptDocument syntax, string name)
    {
        var declarations = syntax.Calls.Where(c => c.Name == "MimicSet").Select(c => c.QuotedArguments.FirstOrDefault(a => a.ArgumentIndex == 0)?.Value).ToArray();
        if (declarations.Any(n => n is null) || declarations.Distinct(StringComparer.Ordinal).Count() != declarations.Length)
            throw new InvalidDataException("The retained mimic declarations are malformed or ambiguous.");
        return declarations.Contains(name, StringComparer.Ordinal);
    }

    private static string ValidateCatalog(CustomModelPackage package, CharacterResourceRecord root, CharacterResourceRecord scan, CharacterResourceRecord absence, string derived)
    {
        using var scanJson = JsonDocument.Parse(Bytes(package, scan).AsMemory());
        var s = scanJson.RootElement;
        if (String(s,"format") != "dl-reanimated-character-discovery-scan-v1" || String(s,"characterSourceSha256") != package.Document.Source.ContentSha256 ||
            String(s,"rootContentSha256") != root.ContentSha256) throw new InvalidDataException("The facial dependency scan source changed.");
        var p = s.GetProperty("scan"); var bounds = p.GetProperty("bounds");
        string snapshot = String(p,"catalogSnapshotSha256");
        if (String(p,"rootLogicalId") != root.Id || String(p,"rootSourceFingerprint") != root.SourceFingerprint ||
            String(p,"rootProviderSha256") != Hash(Encoding.UTF8.GetBytes(root.ProviderIdentity)) ||
            snapshot != String(p,"finalCatalogSnapshotSha256") || snapshot != scan.SourceFingerprint ||
            p.GetProperty("hostContext").GetInt32() != 1 || bounds.GetProperty("hostContext").GetInt32() != 1 ||
            p.GetProperty("effectiveScriptCount").GetInt32() != p.GetProperty("attemptedScriptCount").GetInt32() ||
            p.GetProperty("attemptedScriptCount").GetInt32() < 0 || p.GetProperty("attemptedScriptCount").GetInt32() > bounds.GetProperty("maximumScannedScripts").GetInt32() ||
            p.GetProperty("traversedResourceCount").GetInt32() > bounds.GetProperty("maximumGraphResources").GetInt32() ||
            p.GetProperty("totalSourceBytes").GetInt64() > bounds.GetProperty("maximumTotalSourceBytes").GetInt64() ||
            p.GetProperty("limitFindings").GetArrayLength() != 0)
            throw new InvalidDataException("A complete source catalog scan is required for the facial lookup review.");
        var observations = p.GetProperty("observations");
        if (observations.GetArrayLength() < p.GetProperty("attemptedScriptCount").GetInt32() ||
            observations.EnumerateArray().Any(o => o.GetProperty("status").GetInt32() is 5 or 6) ||
            package.Document.CharacterResources!.Resources.Any(r => r.Required && Path.GetFileName(r.LogicalName).Equals(derived, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The facial catalog review has changed source observations or a declared FED dependency.");
        using var absenceJson = JsonDocument.Parse(Bytes(package, absence).AsMemory()); var a = absenceJson.RootElement;
        if (String(a,"format") != "dl-reanimated-facial-catalog-absence-v1" || String(a,"rootResourceId") != root.Id ||
            String(a,"rootSourceSha256") != root.ContentSha256 || String(a,"derivedFedName") != derived ||
            String(a,"catalogSnapshotSha256") != snapshot || String(a,"scanReceiptSha256") != scan.ContentSha256 ||
            a.GetProperty("exactLookupCandidateCount").GetInt32() != 0 || absence.SourceFingerprint != snapshot || absence.Required)
            throw new InvalidDataException("The exact facial filename catalog review is missing or stale.");
        return snapshot;
    }
    private static string String(JsonElement value, string property) => value.GetProperty(property).GetString() ?? throw new InvalidDataException("A facial review string is missing.");
    private static CharacterResourceRecord Record(CustomModelPackage package, string id) => package.Document.CharacterResources!.Resources.SingleOrDefault(r => r.Id == id && !r.IsOriginalArchive && r.EntryPath is not null)
        ?? throw new InvalidDataException("A facial source record is missing.");
    private static string Text(CustomModelPackage package, CharacterResourceRecord record) => StrictUtf8.GetString(Bytes(package, record).AsSpan());
    private static ImmutableArray<byte> Bytes(CustomModelPackage package, CharacterResourceRecord record)
    {
        if (record.ByteLength > 32L * 1024 * 1024 || !package.CompanionPayloads.TryGetValue(record.EntryPath!, out var bytes) || bytes.Length != record.ByteLength || Hash(bytes.AsSpan()) != record.ContentSha256)
            throw new InvalidDataException("Facial review source bytes changed or exceed their bounds.");
        return bytes;
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}


