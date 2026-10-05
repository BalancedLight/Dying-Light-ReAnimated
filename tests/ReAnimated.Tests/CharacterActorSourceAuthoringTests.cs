using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterActorSourceAuthoringTests
{
    private const string ActorId = "vf:actors/source.scr";
    private const string ActorScript = "Actor(\"one\") { Model(\"actors/body.msh\"); PhysicsScript(\"body.phx\"); " +
        "UnknownResource(\"possible.fed\"); } Actor(\"two\") { Model(\"actors/other.msh\"); PhysicsScript(\"other.phx\"); }";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RevalidationLeavesPackagesWithoutActorReviewsUntouched()
    {
        CustomModelPackage customFbx = RigConformanceWizardTests.CreateModel().Package;
        CharacterActorSourceAuthoring.RevalidateAll(customFbx);
        CharacterActorSourceAuthoring.RevalidateAll(CreateStockPackage());

        CustomModelPackage stock = CreateStockPackage();
        CharacterActorSourceReviewResult review = CharacterActorSourceAuthoring.Inspect(stock, ActorId, 1, 0);
        CustomModelPackage withReceipt = CharacterActorSourceAuthoring.Apply(stock, ActorId, review, []);
        CustomModelPackage invalidFbx = withReceipt with { Document = withReceipt.Document with
            { Source = withReceipt.Document.Source with { Kind = CustomModelSourceKind.BinaryFbx,
                OriginalFileName = "generic.fbx" } } };
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(invalidFbx));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReviewedActorSourceRoundTripsWithoutChangingNeutralModelOrUnrelatedBlockers()
    {
        CustomModelPackage original = CreateStockPackage();
        CharacterActorSourceReviewResult review = CharacterActorSourceAuthoring.Inspect(original, ActorId, 1, 0);
        CharacterActorQuotedReference physics = Assert.Single(review.ScopedReferences, r => r.Value == "body.phx");
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "other.phx");
        Assert.Contains(review.UnverifiedLeads, r => r.Value == "possible.fed");
        CustomModelPackage accepted = CharacterActorSourceAuthoring.Apply(original, ActorId, review,
            [Expectation(physics)]);

        CharacterResourceInventory inventory = accepted.Document.CharacterResources!;
        CharacterActorSourceReceipt receipt = Assert.Single(inventory.ActorSourceReviews);
        Assert.Equal(review.ModelCallIndex, receipt.ModelDeclarationCallIndex);
        Assert.Equal(review.ModelTokenStart, receipt.ModelTokenStart);
        Assert.Equal("actors/body.msh", receipt.ExactModelName);
        Assert.Equal(CharacterDependencyStatus.Preserved,
            Assert.Single(inventory.Resources, r => r.Id == ActorId).Status);
        Assert.Equal(CharacterDependencyStatus.Ambiguous,
            Assert.Single(inventory.Resources, r => r.Id == "vf:actors/unrelated.phx").Status);
        Assert.Null(inventory.CompiledSemanticSha256);
        Assert.Null(inventory.LoadedResourceSha256);
        Assert.Empty(inventory.VerifiedPlayerScenarios);
        Assert.Equal(original.Document.Bones, accepted.Document.Bones);
        Assert.Equal(original.Document.Meshes, accepted.Document.Meshes);
        Assert.Equal(original.SourceFbx, accepted.SourceFbx);
        Assert.Equal(original.CompanionPayloads[ActorPath], accepted.CompanionPayloads[ActorPath]);
        CharacterActorSourceAuthoring.RevalidateAll(accepted);

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "reviewed.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(accepted, path);
            CustomModelPackage reopened = CustomModelPackageSerializer.Load(path);
            CharacterActorSourceAuthoring.RevalidateAll(reopened);
            CharacterActorSourceReceipt reopenedReceipt = Assert.Single(reopened.Document.CharacterResources!.ActorSourceReviews);
            Assert.Equal(receipt.ActorResourceId, reopenedReceipt.ActorResourceId);
            Assert.Equal(receipt.ContentSha256, reopenedReceipt.ContentSha256);
            Assert.Equal(receipt.ModelTokenStart, reopenedReceipt.ModelTokenStart);
            Assert.Equal(receipt.ReviewedReferences.ToArray(), reopenedReceipt.ReviewedReferences.ToArray());
            Assert.Equal(original.Document.Bones.Select(b => (b.Name, b.ParentIndex, b.ExactLocalBindMatrix, b.LocalBounds)),
                reopened.Document.Bones.Select(b => (b.Name, b.ParentIndex, b.ExactLocalBindMatrix, b.LocalBounds)));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void InspectRejectsBasenameGuessAndUnsupportedOrArchivedSources()
    {
        CustomModelPackage original = CreateStockPackage();
        CustomModelPackage wrongRoot = ReplaceRecord(original, "root", r => r with { LogicalName = "body.msh" });
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Inspect(wrongRoot, ActorId, 1, 0));
        CustomModelPackage archived = ReplaceRecord(original, ActorId, r => r with { IsOriginalArchive = true });
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Inspect(archived, ActorId, 1, 0));
        CustomModelPackage unsupported = ReplaceRecord(original, ActorId, r => r with { Status = CharacterDependencyStatus.Unsupported });
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Inspect(unsupported, ActorId, 1, 0));
        CustomModelPackage customFbx = original with { Document = original.Document with
            { Source = original.Document.Source with { Kind = CustomModelSourceKind.BinaryFbx, OriginalFileName = "model.fbx" } } };
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Inspect(customFbx, ActorId, 1, 0));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ApplyRejectsUnverifiedLeadsAndStaleInspection()
    {
        CustomModelPackage package = CreateStockPackage();
        CharacterActorSourceReviewResult review = CharacterActorSourceAuthoring.Inspect(package, ActorId, 1, 0);
        CharacterActorQuotedReference unverified = Assert.Single(review.UnverifiedLeads, r => r.Value == "other.phx");
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Apply(package, ActorId, review,
            [Expectation(unverified)]));
        CustomModelPackage changed = ChangeActorBytes(package, ActorScript.Replace("body.phx", "changed.phx", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Apply(changed, ActorId, review, []));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Inspect(package, ActorId, 5, 0));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Inspect(package, ActorId, 1, 4));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SavedReceiptRejectsProviderHashModelTokenAndActorScopeTampering()
    {
        CustomModelPackage original = CreateStockPackage();
        CharacterActorSourceReviewResult review = CharacterActorSourceAuthoring.Inspect(original, ActorId, 1, 0);
        CharacterActorQuotedReference physics = Assert.Single(review.ScopedReferences, r => r.Value == "body.phx");
        CustomModelPackage accepted = CharacterActorSourceAuthoring.Apply(original, ActorId, review, [Expectation(physics)]);
        CharacterActorSourceReceipt receipt = Assert.Single(accepted.Document.CharacterResources!.ActorSourceReviews);

        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ReplaceRecord(accepted, ActorId, r => r with { ProviderIdentity = "changed-provider" })));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ReplaceRecord(accepted, ActorId, r => r with { SourceFingerprint = new string('c', 64) })));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ReplaceReceipt(accepted, receipt with { ModelTokenStart = receipt.ModelTokenStart + 1 })));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ReplaceReceipt(accepted, receipt with { ActorScopeCallIndex = 4 })));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ReplaceRecord(accepted, "root", r => r with { LogicalName = "actors/different.msh" })));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ChangeActorBytes(accepted, ActorScript.Replace("body.phx", "edited.phx", StringComparison.Ordinal))));
    }

    [Fact]
    public void PresetSourceReviewRecordsArgumentOneAndRoundTripsUnchangedBytes()
    {
        const string script = "PresetDef(\"Character\") { Preset(\"one\") { SetField(\"MeshName\", \"actors/body.msh\"); " +
            "SetField(\"PhysicsScript\", \"body.phx\"); SetField(\"UnknownResource\", \"lead.fed\"); } }";
        var original = ReplaceRecord(ChangeActorBytes(CreateStockPackage(), script), ActorId,
            record => record with { LogicalName = "actors/source.pre" });
        var review = CharacterActorSourceAuthoring.Inspect(original, ActorId, 2, 1);
        Assert.Equal(1, review.ModelArgumentIndex);
        Assert.Equal(1, Assert.Single(review.ScopedReferences).ArgumentIndex);
        var accepted = CharacterActorSourceAuthoring.Apply(original, ActorId, review,
            review.ScopedReferences.Select(Expectation));
        var receipt = Assert.Single(accepted.Document.CharacterResources!.ActorSourceReviews);
        Assert.Equal(1, receipt.ModelArgumentIndex);
        Assert.Equal("actors/body.msh", receipt.ExactModelName);
        Assert.Equal(original.CompanionPayloads[ActorPath], accepted.CompanionPayloads[ActorPath]);
        Assert.Equal(original.Document.Bones, accepted.Document.Bones);
        Assert.False(accepted.Document.CharacterResources.IsGameReady);
        CharacterActorSourceAuthoring.RevalidateAll(accepted);
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.RevalidateAll(
            ReplaceReceipt(accepted, receipt with { ModelArgumentIndex = 0 })));
        Assert.Throws<InvalidDataException>(() => CharacterActorSourceAuthoring.Apply(
            ChangeActorBytes(original, script + "\n"), ActorId, review, []));
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "preset-reviewed.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(accepted, path);
            var reopened = CustomModelPackageSerializer.Load(path);
            CharacterActorSourceAuthoring.RevalidateAll(reopened);
            Assert.Equal(1, Assert.Single(reopened.Document.CharacterResources!.ActorSourceReviews).ModelArgumentIndex);
            Assert.Equal(original.CompanionPayloads[ActorPath].ToArray(), reopened.CompanionPayloads[ActorPath].ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static CharacterActorReferenceExpectation Expectation(CharacterActorQuotedReference reference) =>
        new(reference.CallIndex, reference.ArgumentIndex, reference.ArgumentStart, reference.ArgumentLength, reference.Value);

    private const string ActorPath = "character/resources/actor.bin";

    private static CustomModelPackage CreateStockPackage()
    {
        CustomModelPackage baseline = RigConformanceWizardTests.CreateModel().Package;
        byte[] actor = Encoding.UTF8.GetBytes(ActorScript);
        byte[] root = baseline.SourceFbx.ToArray();
        byte[] unrelated = Encoding.UTF8.GetBytes("unused source");
        byte[] decoded = [1, 2, 3];
        CharacterResourceRecord[] resources =
        [
            Record("root", "actors/body.msh", "character/resources/root.bin", root, CharacterDependencyStatus.Preserved),
            Record(ActorId, "actors/source.scr", ActorPath, actor, CharacterDependencyStatus.Ambiguous),
            Record("vf:actors/unrelated.phx", "actors/unrelated.phx", "character/resources/unrelated.bin",
                unrelated, CharacterDependencyStatus.Ambiguous),
        ];
        var inventory = new CharacterResourceInventory
        {
            RootResourceId = "root", DecodedSha256 = Sha(decoded), DecodedByteLength = decoded.Length,
            Resources = resources.ToImmutableArray(),
            Subsystems = Enum.GetValues<CharacterSubsystem>().Select(s =>
                new CharacterSubsystemReview(s, CharacterDependencyStatus.Missing, "Source review pending.")).ToImmutableArray(),
            CompiledSemanticSha256 = new string('a', 64), LoadedResourceSha256 = new string('a', 64),
            VerifiedPlayerScenarios = ["stock-reuse"],
        };
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        payloads.Add(resources[0].EntryPath!, ImmutableArray.Create(root));
        payloads.Add(resources[1].EntryPath!, ImmutableArray.Create(actor));
        payloads.Add(resources[2].EntryPath!, ImmutableArray.Create(unrelated));
        CustomModelDocument document = baseline.Document with
        {
            Source = baseline.Document.Source with { Kind = CustomModelSourceKind.StockCharacter,
                OriginalFileName = "actors/body.skn", EmbeddedEntryPath = "source/character.bin" },
            CharacterResources = inventory,
        };
        document.Validate();
        return baseline with { Document = document, DecodedCharacterPayload = ImmutableArray.Create(decoded),
            CompanionPayloads = payloads.ToImmutable() };
    }

    private static CharacterResourceRecord Record(string id, string name, string path, byte[] bytes,
        CharacterDependencyStatus status) => new()
    {
        Id = id, LogicalName = name, ProviderIdentity = "synthetic-provider", SourceFingerprint = new string('b', 64),
        ContentSha256 = Sha(bytes), EntryPath = path, ByteLength = bytes.Length,
        Subsystem = CharacterSubsystem.Damage, Status = status, ReferencedBy = ["root"],
    };

    private static CustomModelPackage ReplaceRecord(CustomModelPackage package, string id,
        Func<CharacterResourceRecord, CharacterResourceRecord> change)
    {
        CharacterResourceInventory inventory = package.Document.CharacterResources!;
        return package with { Document = package.Document with { CharacterResources = inventory with
            { Resources = inventory.Resources.Select(r => r.Id == id ? change(r) : r).ToImmutableArray() } } };
    }

    private static CustomModelPackage ReplaceReceipt(CustomModelPackage package, CharacterActorSourceReceipt receipt)
    {
        CharacterResourceInventory inventory = package.Document.CharacterResources!;
        return package with { Document = package.Document with { CharacterResources = inventory with
            { ActorSourceReviews = [receipt] } } };
    }

    private static CustomModelPackage ChangeActorBytes(CustomModelPackage package, string script)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(script);
        return ReplaceRecord(package with { CompanionPayloads = package.CompanionPayloads.SetItem(ActorPath,
            ImmutableArray.Create(bytes)) }, ActorId, r => r with { ContentSha256 = Sha(bytes), ByteLength = bytes.Length });
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
