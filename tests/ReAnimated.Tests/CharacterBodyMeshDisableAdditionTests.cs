using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterBodyMeshDisableAdditionTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void FlatAdditionPreservesOtherRegionsAndAllOriginalCharacters()
    {
        const string source = "// header\r\nBodyElement(_ARM, 1, 0, 0., 20., \"helper_arm\"); // selected\r\n" +
            "AddMesh2Disable(\"arm_mesh\")\r\nFuture(\"retained\")\r\n" +
            "BodyElement(_LEG, 1, 0, 0., 10., \"helper_leg\")\r\nAddMesh2Disable(\"accessory_mesh\")\r\n";
        var package = Create(source);
        var proposal = Proposal(package);
        var before = CharacterCompanionAuthoring.Read(package, "body", CharacterCompanionFamily.BodyElements);
        var result = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(package, proposal);
        Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(row => row.Message)));
        var after = CharacterCompanionAuthoring.Read(result.Package, "body", CharacterCompanionFamily.BodyElements);
        Assert.Single(after.BodyElements!.MeshDisables.Where(row => row.BodyElementCallIndex == proposal.BodyElementCallIndex && row.EntityName == proposal.EntityName));
        Assert.Equal(before.Syntax.Calls[proposal.BodyElementCallIndex].ParentCallIndex,
            after.Syntax.Calls[proposal.BodyElementCallIndex + 1].ParentCallIndex);
        AssertSingleInsertion(source, Text(result.Package, "body"));
        Assert.Equal(source, Text(package, "body"));
        Assert.Equal(source, Text(result.Package, "original:body"));
        Assert.Equal(2, after.BodyElements.Elements.Length);
        Assert.Equal(3, after.BodyElements.MeshDisables.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void BracedAdditionKeepsNestedRelicHideListsCommentsAndHelpers()
    {
        const string source = "Regions() {\n" +
            "  BodyElement(_ARM, 1, 0, 0., 20., \"helper_arm\") /* { ignored } */ { // block\n" +
            "    AddRelics(\"part\", _PHYSICS, \"part.phx\", \"\", _ZERO, _ZERO) {\n" +
            "      AddMesh2DisableFromRelic(\"cap_mesh\") // keep\n      Future(\"brace }\")\n    }\n" +
            "    AddMesh2Disable(\"arm_mesh\")\n  }\n}\nsub Helper() { Unknown(1) }\n";
        var package = Create(source);
        var proposal = Proposal(package);
        var result = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(package, proposal);
        Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(row => row.Message)));
        var reopened = CharacterCompanionAuthoring.Read(result.Package, "body", CharacterCompanionFamily.BodyElements);
        Assert.Equal(proposal.BodyElementCallIndex,
            reopened.Syntax.Calls[proposal.BodyElementCallIndex + 1].ParentCallIndex);
        var relic = Assert.Single(reopened.BodyElements!.Relics);
        var relicHide = Assert.Single(reopened.BodyElements.MeshDisables.Where(row => row.FromRelic));
        Assert.Equal("cap_mesh", relicHide.EntityName);
        Assert.Equal(relic.CallIndex, reopened.Syntax.Calls[relicHide.CallIndex].ParentCallIndex);
        Assert.Contains("/* { ignored } */", Text(result.Package, "body"), StringComparison.Ordinal);
        Assert.Contains("sub Helper() { Unknown(1) }", Text(result.Package, "body"), StringComparison.Ordinal);
        AssertSingleInsertion(source, Text(result.Package, "body"));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void BomArchiveAndEditedBytesSurviveSaveReloadAndSecondRegionEdit()
    {
        const string source = "BodyElement(_ARM, 1, 0, 0., 20., \"helper_arm\") {}\r\n" +
            "BodyElement(_LEG, 1, 0, 0., 10., \"helper_leg\") // eof";
        var package = Create(source, bom: true);
        var result = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(package, Proposal(package));
        Assert.True(result.Applied);
        var secondProposal = Proposal(result.Package, "_LEG", "helper_leg") with { EntityName = "second_mesh", ReviewedAvailableEntityNames = ["second_mesh"] };
        var second = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(result.Package, secondProposal);
        Assert.True(second.Applied);
        Assert.Single(second.Package.Document.CharacterResources!.Resources.Where(row => row.IsOriginalArchive));
        Assert.Equal(Bytes(package, "body").ToArray(), Bytes(second.Package, "original:body").ToArray());
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, Bytes(second.Package, "body").Take(3));
        Assert.Null(second.Package.Document.CharacterResources.CompiledSemanticSha256);
        Assert.Null(second.Package.Document.CharacterResources.LoadedResourceSha256);
        Assert.Empty(second.Package.Document.CharacterResources.VerifiedPlayerScenarios);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(second.Package, path);
            var reopened = CustomModelPackageSerializer.Load(path);
            Assert.Equal(Bytes(second.Package, "body").ToArray(), Bytes(reopened, "body").ToArray());
            Assert.Equal(Bytes(package, "body").ToArray(), Bytes(reopened, "original:body").ToArray());
            Assert.True(CharacterCompanionAuthoring.Read(reopened, "body", CharacterCompanionFamily.BodyElements).IsValid);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RefusalsPreservePackageForStaleUnreviewedUnknownDuplicateAndAmbiguousInputs()
    {
        const string source = "BodyElement(_ARM, 1, 0, 0., 20., \"helper_arm\")\nAddMesh2Disable(\"existing_mesh\")\n";
        var package = Create(source);
        var proposal = Proposal(package);
        foreach (var rejected in new[]
        {
            proposal with { Reviewed = false },
            proposal with { ExpectedSha256 = new string('0', 64) },
            proposal with { ExpectedElementToken = "_OTHER" },
            proposal with { ExpectedHelperName = "other_helper" },
            proposal with { BodyElementCallIndex = -1 },
            proposal with { ReviewedAvailableEntityNames = ["ACCESSORY_MESH"] },
            proposal with { EntityName = "existing_mesh", ReviewedAvailableEntityNames = ["existing_mesh"] },
            proposal with { EntityName = "mesh\") Unknown(1)", ReviewedAvailableEntityNames = ["mesh\") Unknown(1)"] },
        })
        {
            var refusal = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(package, rejected);
            Assert.False(refusal.Applied);
            Assert.Same(package, refusal.Package);
            Assert.Equal(source, Text(package, "body"));
            Assert.DoesNotContain(package.Document.CharacterResources!.Resources, row => row.IsOriginalArchive);
        }
        var ambiguous = Create("BodyElement(_ARM, 1, 0, 0., 20., \"helper_arm\"); /* gap */ { Future(1) }");
        var ambiguousResult = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(ambiguous, Proposal(ambiguous));
        Assert.False(ambiguousResult.Applied);
        Assert.Same(ambiguous, ambiguousResult.Package);
        var applied = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(package, proposal);
        Assert.True(applied.Applied);
        var duplicate = CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(applied.Package, Proposal(applied.Package));
        Assert.False(duplicate.Applied);
        Assert.Same(applied.Package, duplicate.Package);
    }

    private static CustomModelPackage Create(string text, bool bom = false)
    {
        var package = CharacterMaterialReceiptTests.Create();
        byte[] source = Encoding.UTF8.GetBytes(text);
        byte[] bytes = bom ? [0xEF, 0xBB, 0xBF, .. source] : source;
        var record = new CharacterResourceRecord
        {
            Id = "body", LogicalName = "generic.bel", EntryPath = "character/resources/body.bin",
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), ByteLength = bytes.Length,
            Subsystem = CharacterSubsystem.Damage, Status = CharacterDependencyStatus.Preserved,
        };
        var inventory = package.Document.CharacterResources!;
        return package with
        {
            Document = package.Document with { CharacterResources = inventory with { Resources = inventory.Resources.Add(record) } },
            CompanionPayloads = package.CompanionPayloads.Add(record.EntryPath!, bytes.ToImmutableArray()),
        };
    }

    private static ReviewedBodyMeshDisableAddition Proposal(CustomModelPackage package,
        string token = "_ARM", string helper = "helper_arm")
    {
        var read = CharacterCompanionAuthoring.Read(package, "body", CharacterCompanionFamily.BodyElements);
        Assert.True(read.IsValid);
        int index = read.BodyElements!.Elements.Single(row => row.ElementToken == token).CallIndex;
        return new("body", read.Resource.ContentSha256!, index, token, helper,
            "accessory_mesh", ["accessory_mesh"], true);
    }

    private static ImmutableArray<byte> Bytes(CustomModelPackage package, string id) =>
        package.CompanionPayloads[package.Document.CharacterResources!.Resources.Single(row => row.Id == id).EntryPath!];

    private static string Text(CustomModelPackage package, string id)
    {
        ReadOnlySpan<byte> bytes = Bytes(package, id).AsSpan();
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        return Encoding.UTF8.GetString(bytes);
    }

    private static void AssertSingleInsertion(string original, string changed)
    {
        Assert.True(changed.Length > original.Length);
        int prefix = 0;
        while (prefix < original.Length && original[prefix] == changed[prefix]) prefix++;
        Assert.Equal(original, changed.Remove(prefix, changed.Length - original.Length));
    }
}
