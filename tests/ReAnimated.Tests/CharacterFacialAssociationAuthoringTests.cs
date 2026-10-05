using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterFacialAssociationAuthoringTests
{
    [Fact]
    public void ReviewedMissingLookupPreservesOriginalVocabularyAndPayloadsThroughReload()
    {
        var (package, proposal) = Create();
        var reviewed = CharacterFacialAssociationAuthoring.Review(package, proposal);
        var receipt = Assert.Single(reviewed.Document.CharacterResources!.FacialAssociationReviews);
        Assert.Equal("original_set", receipt.SelectedMimicName); Assert.False(receipt.MimicLookupFound);
        Assert.Equal("generic.fed", receipt.DerivedFedName); Assert.False(reviewed.Document.CharacterResources.IsGameReady);
        Assert.DoesNotContain(reviewed.Document.CharacterResources.ExportBlockers, b => b.Contains("Facial review pending", StringComparison.Ordinal));
        Assert.Equal(package.SourceFbx.ToArray(), reviewed.SourceFbx.ToArray());
        Assert.Equal(package.Document.MorphChannels, reviewed.Document.MorphChannels);
        foreach (var p in package.CompanionPayloads) Assert.Equal(p.Value.ToArray(), reviewed.CompanionPayloads[p.Key].ToArray());
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel"); CustomModelPackageSerializer.SaveAtomic(reviewed, path);
            var reopened = CustomModelPackageSerializer.Load(path); CharacterFacialAssociationAuthoring.Revalidate(reopened);
            Assert.Equal(receipt, Assert.Single(reopened.Document.CharacterResources!.FacialAssociationReviews));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    [Theory]
    [InlineData("unreviewed")]
    [InlineData("override")]
    [InlineData("scan")]
    [InlineData("absence")]
    [InlineData("actor")]
    public void UnreviewedExplicitOrStaleSourceAssociationsAreRejected(string change)
    {
        var (package, proposal) = Create(change == "override");
        if (change == "unreviewed") proposal = proposal with { Reviewed = false };
        if (change is "scan" or "absence" or "actor")
        {
            string id = change == "scan" ? "scan" : change == "absence" ? "absence" : "actor";
            var inventory = package.Document.CharacterResources!;
            package = package with { Document = package.Document with { CharacterResources = inventory with
                { Resources = inventory.Resources.Select(r => r.Id == id ? r with { ContentSha256 = new string('c', 64) } : r).ToImmutableArray() } } };
        }
        Assert.ThrowsAny<Exception>(() => CharacterFacialAssociationAuthoring.Review(package, proposal));
    }
    private static (CustomModelPackage, ReviewedCharacterFacialAssociation) Create(bool filenameOverride = false)
    {
        var baseline = RigConformanceWizardTests.CreateModel().Package;
        byte[] rootBytes = baseline.SourceFbx.ToArray(), decoded = [1, 2, 3];
        string actorText = "PresetDef(\"Character\") { Preset(\"Generic\") { SetField(\"MeshName\",\"generic.msh\"); SetField(\"m_FaceMimicPreset\",\"original_set\"); " +
            (filenameOverride ? "SetField(\"m_FaceMimicFile\",\"explicit.fed\");" : "") + "} }";
        var root = Record("root", "generic", rootBytes, CharacterSubsystem.Geometry);
        var actor = Record("actor", "actors/generic.pre", Encoding.UTF8.GetBytes(actorText), CharacterSubsystem.Damage);
        var mimic = Record("mimic", "facemimic.scr", Encoding.UTF8.GetBytes("MimicSet(\"different_set\") { Unknown(7); }"), CharacterSubsystem.FacialDefinitions);
        string snapshot = new string('d', 64);
        byte[] scanBytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "dl-reanimated-character-discovery-scan-v1", characterSourceSha256 = baseline.Document.Source.ContentSha256,
            rootContentSha256 = root.ContentSha256, scan = new { rootLogicalId = root.Id, rootSourceFingerprint = root.SourceFingerprint, rootProviderSha256 = Hash(Encoding.UTF8.GetBytes(root.ProviderIdentity)),
                catalogSnapshotSha256 = snapshot, finalCatalogSnapshotSha256 = snapshot, hostContext = 1, effectiveScriptCount = 1, attemptedScriptCount = 1, traversedResourceCount = 1, totalSourceBytes = 100,
                bounds = new { hostContext = 1, maximumScannedScripts = 4, maximumGraphResources = 4, maximumTotalSourceBytes = 1000 }, observations = new[] { new { status = 0 } }, limitFindings = Array.Empty<string>() } });
        var scan = Record("scan", "Catalog review", scanBytes, CharacterSubsystem.Helpers) with { Required = false, SourceFingerprint = snapshot };
        byte[] absenceBytes = JsonSerializer.SerializeToUtf8Bytes(new { format = "dl-reanimated-facial-catalog-absence-v1", rootResourceId = root.Id, rootSourceSha256 = root.ContentSha256,
            derivedFedName = "generic.fed", catalogSnapshotSha256 = snapshot, scanReceiptSha256 = scan.ContentSha256, exactLookupCandidateCount = 0 });
        var absence = Record("absence", "Facial catalog review", absenceBytes, CharacterSubsystem.Helpers) with { Required = false, SourceFingerprint = snapshot };
        var inventory = new CharacterResourceInventory { RootResourceId = root.Id, DecodedSha256 = Hash(decoded), DecodedByteLength = decoded.Length, Resources = [root, actor, mimic, scan, absence],
            Subsystems = Enum.GetValues<CharacterSubsystem>().Select(s => new CharacterSubsystemReview(s, s == CharacterSubsystem.FacialDefinitions ? CharacterDependencyStatus.Ambiguous : CharacterDependencyStatus.Decoded, "Facial review pending")).ToImmutableArray() };
        var package = baseline with { Document = baseline.Document with { Source = baseline.Document.Source with { Kind = CustomModelSourceKind.StockCharacter, EmbeddedEntryPath = "source/character.bin", OriginalFileName = "generic.skn" }, CharacterResources = inventory },
            DecodedCharacterPayload = ImmutableArray.Create(decoded), CompanionPayloads = new[] { (root, rootBytes), (actor, Encoding.UTF8.GetBytes(actorText)), (mimic, Encoding.UTF8.GetBytes("MimicSet(\"different_set\") { Unknown(7); }")), (scan, scanBytes), (absence, absenceBytes) }.ToImmutableDictionary(p => p.Item1.EntryPath!, p => ImmutableArray.Create(p.Item2)) };
        var syntax = NativeCharacterScriptCodec.Parse(actorText);
        int scope = syntax.Calls.Select((c,i)=>(c,i)).Single(p => p.c.Name == "Preset").i, model = syntax.Calls.Select((c,i)=>(c,i)).Single(p => CharacterActorSourceReview.GetModelNameArgument(p.c)?.Value == "generic.msh").i, field = syntax.Calls.Select((c,i)=>(c,i)).Single(p => p.c.QuotedArguments.Any(a => a.Value == "m_FaceMimicPreset")).i;
        package = CharacterActorSourceAuthoring.Apply(package, actor.Id, CharacterActorSourceAuthoring.Inspect(package, actor.Id, model, scope), []);
        return (package, new(actor.Id, model, scope, field, mimic.Id, scan.Id, absence.Id, new string('a', 64), new string('b', 64), new string('e', 64), true));
    }
    private static CharacterResourceRecord Record(string id, string name, byte[] bytes, CharacterSubsystem subsystem) => new() { Id = id, LogicalName = name, EntryPath = "character/resources/" + id + ".bin", ContentSha256 = Hash(bytes), ByteLength = bytes.Length, ProviderIdentity = "generic-provider", SourceFingerprint = new string('f', 64), Subsystem = subsystem, Status = CharacterDependencyStatus.Preserved, ReferencedBy = ["root"] };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}



