using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Codecs.Models;
using ReAnimated.DL1.Assets.Meshes;
using System.Text.Json;

namespace ReAnimated.Tests;

public sealed class CharacterDependencyMergeTests
{
    private const string SelectedId = "vf:actors/selected.scr";
    private const string OtherId = "vf:actors/other.scr";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void ExactSelectedUnchangedCandidatePromotesWithoutClearingOtherOrScanBlockers()
    {
        var selected = Resource(SelectedId, "actors/selected.scr", "selected", CharacterDependencyStatus.Ambiguous);
        var other = Resource(OtherId, "actors/other.scr", "other", CharacterDependencyStatus.Ambiguous);
        CustomModelPackage current = Package([Root(), selected, other, Diagnostic("discovery:scan", "Bounded script scan", CharacterDependencyStatus.Unsupported),
            Diagnostic("discovery:unknown", "Unknown referenced source", CharacterDependencyStatus.Ambiguous)]);
        CustomModelPackage refreshed = Package([Root(), selected with { Record = selected.Record with
            { Status = CharacterDependencyStatus.Preserved, Subsystem = CharacterSubsystem.FacialDefinitions } },
            other with { Record = other.Record with { Status = CharacterDependencyStatus.Preserved } }], current);

        CustomModelPackage merged = ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current, refreshed, [SelectedId]);
        CharacterResourceInventory inventory = merged.Document.CharacterResources!;
        Assert.Equal(CharacterDependencyStatus.Preserved, Assert.Single(inventory.Resources, r => r.Id == SelectedId).Status);
        Assert.Equal(CharacterSubsystem.FacialDefinitions, Assert.Single(inventory.Resources, r => r.Id == SelectedId).Subsystem);
        Assert.Equal(selected.Record.EntryPath, Assert.Single(inventory.Resources, r => r.Id == SelectedId).EntryPath);
        Assert.Equal(selected.Bytes, merged.CompanionPayloads[selected.Record.EntryPath!].ToArray());
        Assert.Contains("actor association and runtime behavior remain unverified",
            Assert.Single(inventory.Resources, r => r.Id == SelectedId).Detail, StringComparison.Ordinal);
        Assert.Equal(CharacterDependencyStatus.Ambiguous, Assert.Single(inventory.Resources, r => r.Id == OtherId).Status);
        Assert.Contains(inventory.Resources, r => r.Id == "discovery:scan" && r.Status == CharacterDependencyStatus.Unsupported);
        Assert.Contains(inventory.Resources, r => r.Id == "discovery:unknown" && r.Status == CharacterDependencyStatus.Ambiguous);
        Assert.False(inventory.IsDependencyComplete);
        Assert.Equal(current.Document.Bones, merged.Document.Bones);
        Assert.Equal(current.Document.Meshes, merged.Document.Meshes);
        Assert.Equal(current.SourceFbx, merged.SourceFbx);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void UnrelatedAuthoredEditAndOriginalArchiveSurviveAnotherRootResolution()
    {
        var original = Resource(SelectedId, "actors/selected.scr", "original", CharacterDependencyStatus.Preserved);
        var edited = Resource(SelectedId, "actors/selected.scr", "authored edit", CharacterDependencyStatus.Ambiguous,
            "character/resources/edited.bin");
        var archived = original with { Record = original.Record with { Id = "original:" + SelectedId,
            EntryPath = "character/resources/archive.bin", IsOriginalArchive = true, Required = false } };
        var other = Resource(OtherId, "actors/other.scr", "other", CharacterDependencyStatus.Ambiguous);
        CustomModelPackage current = Package([Root(), edited, archived, other]);
        CustomModelPackage refreshed = Package([Root(), original,
            other with { Record = other.Record with { Status = CharacterDependencyStatus.Preserved } }], current);

        CustomModelPackage merged = ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current, refreshed, [OtherId]);
        CharacterResourceInventory inventory = merged.Document.CharacterResources!;
        Assert.Equal(CharacterDependencyStatus.Ambiguous, Assert.Single(inventory.Resources, r => r.Id == SelectedId).Status);
        Assert.Equal(CharacterDependencyStatus.Preserved, Assert.Single(inventory.Resources, r => r.Id == OtherId).Status);
        Assert.True(Assert.Single(inventory.Resources, r => r.Id == "original:" + SelectedId).IsOriginalArchive);
        Assert.Equal(edited.Bytes, merged.CompanionPayloads[edited.Record.EntryPath!].ToArray());
        Assert.Equal(archived.Bytes, merged.CompanionPayloads[archived.Record.EntryPath!].ToArray());
        Assert.Equal(current.Document.Bones, merged.Document.Bones);
        Assert.Equal(current.Document.Meshes, merged.Document.Meshes);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void ChangedSelectedSourceOrAuthoredEditCannotBePromoted()
    {
        var old = Resource(SelectedId, "actors/selected.scr", "original", CharacterDependencyStatus.Ambiguous);
        CustomModelPackage current = Package([Root(), old]);
        var changed = Resource(SelectedId, "actors/selected.scr", "changed", CharacterDependencyStatus.Preserved);
        CustomModelPackage refreshed = Package([Root(), changed], current);
        Assert.Throws<InvalidDataException>(() => ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(
            current, refreshed, [SelectedId]));

        var archived = old with { Record = old.Record with { Id = "original:" + SelectedId,
            EntryPath = "character/resources/archive.bin", IsOriginalArchive = true, Required = false,
            Status = CharacterDependencyStatus.Preserved } };
        var edited = Resource(SelectedId, "actors/selected.scr", "authored edit", CharacterDependencyStatus.Ambiguous,
            "character/resources/edited.bin");
        CustomModelPackage authored = Package([Root(), edited, archived], current);
        CustomModelPackage unchangedSource = Package([Root(), old with { Record = old.Record with { Status = CharacterDependencyStatus.Preserved } }], current);
        Assert.Throws<InvalidDataException>(() => ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(
            authored, unchangedSource, [SelectedId]));
    }

    [Fact]
    public void CompleteRefreshSupersedesOnlyKnownLegacyLimitsAndPreservesSourceReviews()
    {
        const string limit = "Source-script scan was bounded; unscanned scripts may contain additional explicit declarations.";
        var selected = Resource(SelectedId, "actors/selected.scr", "selected", CharacterDependencyStatus.Ambiguous);
        var other = Resource(OtherId, "actors/other.scr", "other", CharacterDependencyStatus.Ambiguous);
        var scan = Diagnostic("discovery:scan", "Dependency discovery", CharacterDependencyStatus.Unsupported);
        scan = scan with { Record = scan.Record with { Detail = limit } };
        var unknown = Diagnostic("discovery:unknown", "Unknown referenced source", CharacterDependencyStatus.Unsupported);
        var current = Package([Root(), selected, other, scan, unknown]);
        var receipt = new CharacterActorSourceReceipt
        {
            ActorResourceId = SelectedId, ProviderIdentity = selected.Record.ProviderIdentity,
            SourceFingerprint = selected.Record.SourceFingerprint, ContentSha256 = selected.Record.ContentSha256!,
            ExactModelName = "model", ModelTokenStart = 0, ModelTokenLength = 1,
        };
        current = current with { Document = current.Document with { CharacterResources =
            current.Document.CharacterResources! with { ActorSourceReviews = [receipt] } } };
        var refreshed = WithScanEvidence(Package([Root(),
            selected with { Record = selected.Record with { Status = CharacterDependencyStatus.Preserved } },
            other with { Record = other.Record with { Status = CharacterDependencyStatus.Preserved } }], current));

        var merged = ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current, refreshed, [SelectedId]);

        Assert.DoesNotContain(merged.Document.CharacterResources!.Resources, row => row.Id == "discovery:scan");
        Assert.Contains(merged.Document.CharacterResources.Resources, row => row.Id == "discovery:unknown");
        Assert.Equal(CharacterDependencyStatus.Ambiguous, Assert.Single(merged.Document.CharacterResources.Resources, row => row.Id == OtherId).Status);
        Assert.Equal(JsonSerializer.Serialize(current.Document.CharacterResources!.ActorSourceReviews),
            JsonSerializer.Serialize(merged.Document.CharacterResources.ActorSourceReviews));
        Assert.Equal(current.SourceFbx.ToArray(), merged.SourceFbx.ToArray());
        Assert.Equal(current.DecodedCharacterPayload.ToArray(), merged.DecodedCharacterPayload.ToArray());
        Assert.Equal(current.GeometryRevisionPayload.ToArray(), merged.GeometryRevisionPayload.ToArray());
        Assert.Equal(JsonSerializer.Serialize(current.Document.MorphAuthoringRecords), JsonSerializer.Serialize(merged.Document.MorphAuthoringRecords));
        Assert.True(CharacterDependencyScanEvidence.Read(merged)!.Scan.IsComplete);
        Assert.NotEmpty(merged.Document.CharacterResources.ExportBlockers);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unknown-legacy")]
    public void IncompleteCoverageAndUnknownLegacyScanTextRemainBlocked(string scenario)
    {
        var scan = Diagnostic("discovery:scan", "Dependency discovery", CharacterDependencyStatus.Unsupported);
        scan = scan with { Record = scan.Record with { Detail = scenario == "unknown-legacy"
            ? "Unclassified discovery warning." : "Source-script scan was bounded; unscanned scripts may contain additional explicit declarations." } };
        var current = Package([Root(), scan]);
        var refreshed = WithScanEvidence(Package([Root()], current), incomplete: scenario == "incomplete");

        var merged = ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current, refreshed, []);

        Assert.Contains(merged.Document.CharacterResources!.Resources, row => row.Id == "discovery:scan");
        Assert.Equal(scan.Record.Detail, Assert.Single(merged.Document.CharacterResources.Resources, row => row.Id == "discovery:scan").Detail);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("catalog")]
    [InlineData("receipt-bytes")]
    [InlineData("modified-companion")]
    public void RefreshRejectsStaleSourceAndScanProvenance(string scenario)
    {
        var companion = Resource(SelectedId, "actors/selected.scr", "original", CharacterDependencyStatus.Preserved);
        var current = WithScanEvidence(Package([Root(), companion]));
        var refreshed = WithScanEvidence(Package([Root(), companion], current), catalog: scenario == "catalog" ? new string('d',64) : null);
        if (scenario == "root")
        {
            var inventory = refreshed.Document.CharacterResources!;
            refreshed = refreshed with { Document = refreshed.Document with { CharacterResources = inventory with
            { Resources = inventory.Resources.Select(row => row.Id == "root" ? row with { ProviderIdentity = "other-provider" } : row).ToImmutableArray() } } };
        }
        if (scenario == "receipt-bytes")
        {
            var record = refreshed.Document.CharacterResources!.Resources.Single(CharacterDependencyScanEvidence.IsReceipt);
            refreshed = refreshed with { CompanionPayloads = refreshed.CompanionPayloads.SetItem(record.EntryPath!, ImmutableArray.Create(new byte[] {1,2,3})) };
        }
        if (scenario == "modified-companion")
        {
            var changed = Resource(SelectedId, "actors/selected.scr", "changed", CharacterDependencyStatus.Preserved);
            refreshed = WithScanEvidence(Package([Root(), changed], current));
        }
        Assert.Throws<InvalidDataException>(() => ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current, refreshed, []));
        Assert.Equal(companion.Bytes, current.CompanionPayloads[companion.Record.EntryPath!].ToArray());
    }

    [Theory]
    [InlineData("native")]
    [InlineData("diagnosed")]
    [InlineData("incomplete")]
    [InlineData("unrelated")]
    public void IndexedLegacyFindingRequiresUnchangedDeclarationAndAllConcreteOutcomes(string outcome)
    {
        const string belId = "vf:data/characters/body.bel";
        const string template = "part_XX.msh";
        var bel = Resource(belId, "data/characters/body.bel",
            "BodyElement(Head,1,0,0,0,\"root\"); DestroyedHeadParts(\"part_XX.msh\",2);",
            CharacterDependencyStatus.Preserved);
        var literal = Diagnostic("unresolved:DetachedParts:part_XX.msh", template, CharacterDependencyStatus.Missing);
        literal = literal with { Record = literal.Record with
        {
            Subsystem = CharacterSubsystem.DetachedParts, ReferencedBy = [belId],
        } };
        if (outcome == "unrelated")
            literal = literal with { Record = literal.Record with { ReferencedBy = ["unknown-source"] } };
        var unknown = Diagnostic("discovery:unknown", "Unknown dependency", CharacterDependencyStatus.Unsupported);
        var current = Package([Root(), bel, literal, unknown]);
        var first = Resource("r:272:part_00", "part_00", "compiled part zero", CharacterDependencyStatus.Preserved);
        first = first with { Record = first.Record with
        {
            Subsystem = CharacterSubsystem.DetachedParts, ReferencedBy = [belId],
            NativeResource = MeshReceipt(first.Record, first.Bytes!),
        } };
        var second = Resource("r:272:part_01", "part_01", "compiled part one", CharacterDependencyStatus.Preserved);
        second = second with { Record = second.Record with
        {
            Subsystem = CharacterSubsystem.DetachedParts, ReferencedBy = [belId],
            NativeResource = MeshReceipt(second.Record, second.Bytes!),
        } };
        List<(CharacterResourceRecord Record, byte[]? Bytes)> fresh = [Root(), bel, first];
        if (outcome == "diagnosed")
        {
            var missing = Diagnostic("unresolved:DetachedParts:data/characters/part_01.msh",
                "data/characters/part_01.msh", CharacterDependencyStatus.Missing);
            fresh.Add(missing with { Record = missing.Record with
            { Subsystem = CharacterSubsystem.DetachedParts, ReferencedBy = [belId] } });
        }
        else if (outcome != "incomplete") fresh.Add(second);
        var refreshed = WithScanEvidence(Package(fresh, current));

        var merged = ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current, refreshed, []);

        bool superseded = outcome is "native" or "diagnosed";
        Assert.Equal(superseded, !merged.Document.CharacterResources!.Resources.Any(row => row.Id == literal.Record.Id));
        Assert.Contains(merged.Document.CharacterResources.Resources, row => row.Id == "discovery:unknown");
        if (outcome == "diagnosed")
            Assert.Contains(merged.Document.CharacterResources.Resources, row =>
                row.LogicalName == "data/characters/part_01.msh" && row.Status == CharacterDependencyStatus.Missing);
        Assert.Equal(bel.Bytes, merged.CompanionPayloads[bel.Record.EntryPath!].ToArray());
        Assert.False(CharacterDependencyScanEvidence.CanSupersedeIndexedTemplate(literal.Record, current,
            Package([Root(), bel with { Record = bel.Record with { SourceFingerprint = new string('d',64) } }, first, second], current)));
    }

    [Fact]
    public void OptionalScanReceiptDoesNotProduceRuntimeCompanionOutput()
    {
        var original = RigConformanceWizardTests.CreateModel();
        var package = WithScanEvidence(Package([Root()], original.Package));
        var without = Dl1NativeCompanionWriter.BuildPreservedPackage(original.Package, "generic_character",
            original.Package.Document.CreateEffectiveBones().Select(bone => bone.Name));
        var withReceipt = Dl1NativeCompanionWriter.BuildPreservedPackage(package, "generic_character",
            package.Document.CreateEffectiveBones().Select(bone => bone.Name));
        Assert.Equal(without.Files.Keys.Order(StringComparer.Ordinal), withReceipt.Files.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in without.Files) Assert.Equal(bytes, withReceipt.Files[name]);
        Assert.DoesNotContain(withReceipt.Files.Keys, path => path.Contains("discovery", StringComparison.OrdinalIgnoreCase));
        Assert.False(package.Document.CharacterResources!.Resources.Single(CharacterDependencyScanEvidence.IsReceipt).Required);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericBranchAdvisoryRequiresExactPhysicalParentAndUnchangedSymbolSource(bool changedSymbols)
    {
        const string belId = "vf:data/body/body.bel";
        const string symbolId = "vf:data/body/symbols.def";
        const string physicalParent = "physical:generic-provider:body-source";
        var bel = Resource(belId,"data/body/body.bel",
            "!include(\"symbols.def\"); ForceGenericRelics(); BodyElement(_HEAD,1,0,0,0,\"root\"); AddRelics(\"Head\",PHYSICS_SINGLE,\"part.phx\",\"\",[0,0,0],[0,0,0]);",
            CharacterDependencyStatus.Preserved);
        var symbols = Resource(symbolId,"data/body/symbols.def","$_HEAD(i,0);",CharacterDependencyStatus.Preserved);
        var legacy = Diagnostic("discovery:conditional","model_Head.msh",CharacterDependencyStatus.Missing);
        legacy = legacy with {Record=legacy.Record with {Subsystem=CharacterSubsystem.DetachedParts,ReferencedBy=[physicalParent]}};
        var current=Package([Root(),bel,symbols,legacy]);
        var part=Resource("r:272:head","head","native head bytes",CharacterDependencyStatus.Preserved);
        part=part with {Record=part.Record with {Subsystem=CharacterSubsystem.DetachedParts,ReferencedBy=[belId],NativeResource=MeshReceipt(part.Record,part.Bytes!)}};
        var body=ReAnimated.Codecs.Models.Dl1BodyElementsCodec.Read(Encoding.UTF8.GetString(bel.Bytes!));
        var map=ReAnimated.Codecs.Models.Dl1BodyElementSymbolMap.Read(Encoding.UTF8.GetString(symbols.Bytes!),["_HEAD"]);
        var selected=ReAnimated.Codecs.Models.Dl1GenericRelicPreload.Select(body,map.Symbols);
        var branch=new Dl1GenericRelicBranchEvidence(belId,bel.Record.ContentSha256!,
            [new(symbolId,Sha(symbols.Record.ProviderIdentity),symbols.Record.SourceFingerprint,symbols.Record.ContentSha256!)],
            map.Symbols,selected.Selections) {BodyPhysicalIdentitySha256=Sha(physicalParent)};
        var freshSymbols=changedSymbols ? symbols with {Record=symbols.Record with {SourceFingerprint=new string('d',64)}} : symbols;
        var refreshed=WithScanEvidence(Package([Root(),bel,freshSymbols,part],current),branch:branch);
        if(changedSymbols)
            Assert.Throws<InvalidDataException>(()=>ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current,refreshed,[]));
        else
        {
            var merged=ModelsWorkspaceViewModel.MergeResolvedCharacterDependencies(current,refreshed,[]);
            var advisory=Assert.Single(merged.Document.CharacterResources!.Resources,row=>row.Id==legacy.Record.Id);
            Assert.False(advisory.Required);
            Assert.Equal(physicalParent,Assert.Single(advisory.ReferencedBy));
            Assert.Contains(merged.Document.CharacterResources.Resources,row=>row.Id==part.Record.Id && row.Required && row.NativeResource is not null);
        }
    }

    private static CharacterNativeResourceReceipt MeshReceipt(CharacterResourceRecord record, byte[] bytes) => new()
    {
        HeaderVersion = 1, ResourceName = record.LogicalName, ResourceType = 272,
        Items = [new(0,0,0,0,0,0,0,0,0,0,bytes.Length,Convert.ToHexStringLower(SHA256.HashData(bytes)),new string('a',64))],
    };

    private static CustomModelPackage WithScanEvidence(CustomModelPackage package, bool incomplete = false, string? catalog = null, Dl1GenericRelicBranchEvidence? branch = null)
    {
        var root = package.Document.CharacterResources!.Resources.Single(row => row.Id == "root");
        var scan = new Dl1CharacterDependencyScanProvenance
        {
            RootLogicalId = root.Id, RootProviderSha256 = Sha(root.ProviderIdentity),
            RootSourceFingerprint = root.SourceFingerprint, RootContentFingerprint = root.ContentSha256,
            CatalogSnapshotSha256 = catalog ?? new string('c',64), FinalCatalogSnapshotSha256 = catalog ?? new string('c',64),
            EffectiveScriptCount = 2, AttemptedScriptCount = incomplete ? 1 : 2,
            GenericRelicBranches = branch is null ? [] : [branch],
            Bounds = new() { MaximumScannedScripts = incomplete ? 1 : 65536, MaximumTotalSourceBytes = 256L*1024*1024 },
            Observations = incomplete
                ? [new(Sha("one"), Sha("source-one"), Dl1CharacterDependencyStatus.Verified)]
                : [new(Sha("one"), Sha("source-one"), Dl1CharacterDependencyStatus.Verified),
                    new(Sha("two"), Sha("source-two"), Dl1CharacterDependencyStatus.Verified)],
        };
        var discovery = new Dl1CharacterDependencyDiscoveryResult([], [], []) { ScanProvenance = scan };
        var model = RigConformanceWizardTests.CreateModel() with { Package = package };
        return CharacterDependencyScanEvidence.Apply(model, discovery).Package;
    }

    private static string Sha(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static (CharacterResourceRecord Record, byte[]? Bytes) Root() =>
        Resource("root", "model", "root source", CharacterDependencyStatus.Preserved);

    private static (CharacterResourceRecord Record, byte[]? Bytes) Resource(string id, string name, string text,
        CharacterDependencyStatus status, string? path = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return (new CharacterResourceRecord
        {
            Id = id, LogicalName = name, ProviderIdentity = "synthetic-provider",
            SourceFingerprint = new string('a', 64), ContentSha256 = hash,
            EntryPath = path ?? "character/resources/" + hash + ".bin", ByteLength = bytes.Length,
            Subsystem = CharacterSubsystem.Damage, Status = status, ReferencedBy = ["root"],
        }, bytes);
    }

    private static (CharacterResourceRecord Record, byte[]? Bytes) Diagnostic(string id, string name,
        CharacterDependencyStatus status) =>
        (new CharacterResourceRecord { Id = id, LogicalName = name, Subsystem = CharacterSubsystem.Helpers,
            Status = status, Detail = "Source association needs separate evidence." }, null);

    private static CustomModelPackage Package(IEnumerable<(CharacterResourceRecord Record, byte[]? Bytes)> entries,
        CustomModelPackage? baseline = null)
    {
        CustomModelPackage model = baseline ?? RigConformanceWizardTests.CreateModel().Package;
        var rows = entries.ToArray();
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var (record, bytes) in rows)
            if (bytes is not null) payloads.Add(record.EntryPath!, ImmutableArray.Create(bytes));
        var inventory = new CharacterResourceInventory
        {
            RootResourceId = "root", DecodedSha256 = new string('b', 64), DecodedByteLength = 1,
            Resources = rows.Select(row => row.Record).ToImmutableArray(),
            Subsystems = Enum.GetValues<CharacterSubsystem>().Select(s =>
                new CharacterSubsystemReview(s, CharacterDependencyStatus.Missing, "Review required.")).ToImmutableArray(),
        };
        inventory.Validate();
        return model with { Document = model.Document with { CharacterResources = inventory },
            CompanionPayloads = payloads.ToImmutable() };
    }
}
