using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterCompanionAuthoringTests
{
    private const string Body = """
        !include("body_rules.def")
        BodyElement(_ARM, 1, 0, 0., 20., "helper_arm")
        AddRelics("Arm", PHYSICS_SINGLE, "old.phx", "", [0,0,0], [0,0,0])
        AddMesh2Disable("arm_mesh") // retained
        """;
    private const string Physics = "PhysicsParams() { QuickStepNumIterations(12) }\n";
    private const string Wrapper = "MeshPartCloth(\"old.phx\", 1, 0)\n";

    [Fact]
    public void NumericEditKeepsOriginalCustodyAndInvalidatesEvidence()
    {
        CustomModelPackage original = Package(
            ("body", "data/characters/body.bel", Body, CharacterSubsystem.Damage));
        CharacterResourceRecord before = Record(original, "body");
        var proposal = new ReviewedCharacterNumericEdit(
            "body", CharacterCompanionFamily.BodyElements, before.ContentSha256!,
            1, 4, "20.", 31.5);

        CharacterCompanionAuthoringResult result = CharacterCompanionAuthoring.ApplyNumericEdit(
            original, proposal);

        Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(Body, Text(original, "body"));
        Assert.Contains("31.5", Text(result.Package, "body"));
        Assert.Contains("AddMesh2Disable(\"arm_mesh\") // retained", Text(result.Package, "body"));
        CharacterResourceRecord edited = Record(result.Package, "body");
        CharacterResourceRecord archived = Record(result.Package, "original:body");
        Assert.StartsWith("character/resources/edited-", edited.EntryPath);
        Assert.StartsWith("character/resources/original-", archived.EntryPath);
        Assert.NotEqual(before.EntryPath, edited.EntryPath);
        Assert.True(archived.IsOriginalArchive);
        Assert.False(archived.Required);
        Assert.Equal(before.LogicalName, archived.LogicalName);
        Assert.Equal(before.ContentSha256, archived.ContentSha256);
        Assert.Equal(Body, Encoding.UTF8.GetString(
            result.Package.CompanionPayloads[archived.EntryPath!].AsSpan()));
        Assert.Null(result.Package.Document.CharacterResources!.CompiledSemanticSha256);
        Assert.Null(result.Package.Document.CharacterResources.LoadedResourceSha256);
        Assert.Empty(result.Package.Document.CharacterResources.VerifiedPlayerScenarios);
        Assert.Equal(CharacterDependencyStatus.Ambiguous, edited.Status);
        Assert.DoesNotContain(edited, CharacterCompanionAuthoring.SelectCandidates(
            result.Package, CharacterCompanionFamily.Ragdoll));
        Assert.Single(CharacterCompanionAuthoring.SelectCandidates(
            result.Package, CharacterCompanionFamily.BodyElements));

        CharacterCompanionAuthoringResult again = CharacterCompanionAuthoring.ApplyNumericEdit(
            result.Package, proposal with
            {
                ExpectedSha256 = edited.ContentSha256!,
                ExpectedLiteral = "31.5",
                NewValue = 32,
            });
        Assert.True(again.Applied);
        Assert.Single(again.Package.Document.CharacterResources!.Resources.Where(r => r.IsOriginalArchive));
        Assert.Equal(archived.ContentSha256, Record(again.Package, "original:body").ContentSha256);
        Assert.Equal(Body, Encoding.UTF8.GetString(
            again.Package.CompanionPayloads[archived.EntryPath!].AsSpan()));
        Assert.Equal(Body, Text(original, "body"));
    }

    [Fact]
    public void RagdollScaleExportsAsFloatAndRelativeMassRequiresInteger()
    {
        const string source = "Bones() { UseBoneScale(\"arm\", \"capsule\", 2, 0.75) Future(9) }\r\n";
        var package = Package(("ragdoll", "data/physics/generic.phx", source, CharacterSubsystem.Ragdoll));
        string hash = Record(package, "ragdoll").ContentSha256!;
        var scale = new ReviewedCharacterNumericEdit("ragdoll", CharacterCompanionFamily.Ragdoll,
            hash, 1, 3, "0.75", 1);
        var changed = CharacterCompanionAuthoring.ApplyNumericEdit(package, scale);
        Assert.True(changed.Applied);
        Assert.Equal(source.Replace("0.75", "1.0", StringComparison.Ordinal), Text(changed.Package, "ragdoll"));
        Assert.Equal(source, Text(changed.Package, "original:ragdoll"));
        Assert.Equal(source, Text(package, "ragdoll"));
        Assert.False(CharacterCompanionAuthoring.ApplyNumericEdit(package, scale with { NewValue = double.MaxValue }).Applied);
        var mass = scale with { ArgumentIndex = 2, ExpectedLiteral = "2", NewValue = 1.5 };
        Assert.False(CharacterCompanionAuthoring.ApplyNumericEdit(package, mass).Applied);
        var whole = CharacterCompanionAuthoring.ApplyNumericEdit(package, mass with { NewValue = 4 });
        Assert.True(whole.Applied);
        Assert.Contains("capsule\", 4, 0.75", Text(whole.Package, "ragdoll"), StringComparison.Ordinal);
    }

    [Fact]
    public void RagdollTotalMassExportsAsFloatEvenFromAnIntegerValuedSource()
    {
        const string source = "RagdollParams() { Mass(20) }\r\n";
        var package = Package(("ragdoll", "data/physics/generic.phx", source, CharacterSubsystem.Ragdoll));
        var proposal = new ReviewedCharacterNumericEdit("ragdoll", CharacterCompanionFamily.Ragdoll,
            Record(package, "ragdoll").ContentSha256!, 1, 0, "20", 40);
        var result = CharacterCompanionAuthoring.ApplyNumericEdit(package, proposal);
        Assert.True(result.Applied);
        Assert.Equal("RagdollParams() { Mass(40.0) }\r\n", Text(result.Package, "ragdoll"));
        Assert.Equal(source, Text(result.Package, "original:ragdoll"));
        Assert.False(CharacterCompanionAuthoring.ApplyNumericEdit(package, proposal with { NewValue = double.MaxValue }).Applied);
    }

    [Fact]
    public void NameEditRequiresReviewedTargetInventoryAndPreservesSourceBom()
    {
        const string damage = "Damage(\"patch\", \"helper\") { Xform(1,0,0,0,1,0,0,0,1,0,0,0) }\r\r\n";
        CustomModelPackage package = Package(
            ("damage", "data/ai/damage.scr", damage, CharacterSubsystem.Damage),
            withUtf8Bom: true);
        string hash = Record(package, "damage").ContentSha256!;
        var proposal = new ReviewedCharacterNameEdit(
            "damage", CharacterCompanionFamily.DamagePatches, hash,
            NativeCharacterReferenceKind.Helper, "helper", "helper_new", ["helper_new"]);
        CharacterCompanionAuthoringResult refused = CharacterCompanionAuthoring.ApplyNameEdit(
            package, proposal with { ReviewedTargetNames = ["other"] });
        Assert.False(refused.Applied);
        Assert.Same(package, refused.Package);

        CharacterCompanionAuthoringResult applied = CharacterCompanionAuthoring.ApplyNameEdit(package, proposal);
        Assert.True(applied.Applied);
        ImmutableArray<byte> edited = applied.Package.CompanionPayloads[Record(applied.Package, "damage").EntryPath!];
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, edited.Take(3));
        Assert.Contains("helper_new", Text(applied.Package, "damage"));
        Assert.EndsWith("\r\r\n", Text(applied.Package, "damage"));
        Assert.Equal(hash, Record(applied.Package, "original:damage").ContentSha256);
    }

    [Fact]
    public void ResourceRenameUpdatesOnlyReviewedCompanionsAndArchivesEachOriginal()
    {
        CustomModelPackage original = Package(
            ("physics", "data/odephysics/old.phx", Physics, CharacterSubsystem.Ragdoll),
            ("body", "data/characters/body.bel", Body, CharacterSubsystem.Damage),
            ("wrapper", "data/characters/body.mpcloth", Wrapper, CharacterSubsystem.Cloth));
        CharacterResourceRecord before = Record(original, "physics");

        CharacterCompanionAuthoringResult changed = CharacterCompanionAuthoring.ApplyResourceRename(
            original, new("physics", before.ContentSha256!, "new.phx"));

        Assert.True(changed.Applied, string.Join("; ", changed.Diagnostics.Select(d => d.Message)));
        Assert.Equal(3, changed.ChangedResourceIds.Length);
        Assert.Equal("data/odephysics/new.phx", Record(changed.Package, "physics").LogicalName);
        Assert.Contains("\"new.phx\"", Text(changed.Package, "body"));
        Assert.Contains("\"new.phx\"", Text(changed.Package, "wrapper"));
        Assert.Equal(Body, Text(original, "body"));
        Assert.Equal(Wrapper, Text(original, "wrapper"));
        Assert.Equal(3, changed.Package.Document.CharacterResources!.Resources.Count(r => r.IsOriginalArchive));
        Assert.Equal(6, changed.Package.CompanionPayloads.Count);
        Assert.Equal(CharacterDependencyStatus.Ambiguous,
            Record(changed.Package, "physics").Status);
        Assert.False(changed.Package.Document.CharacterResources.IsDependencyComplete);
    }

    [Fact]
    public void RenameConflictUnknownReferenceAndStaleHashLeavePackageUnchanged()
    {
        CustomModelPackage package = Package(
            ("physics", "data/odephysics/old.phx", Physics, CharacterSubsystem.Ragdoll),
            ("other", "data/odephysics/existing.phx", Physics, CharacterSubsystem.Ragdoll),
            ("script", "data/scripts/refs.scr", "Unknown(\"old.phx\")\n", CharacterSubsystem.Damage));
        string hash = Record(package, "physics").ContentSha256!;
        Assert.False(CharacterCompanionAuthoring.ApplyResourceRename(
            package, new("physics", hash, "existing.phx")).Applied);
        CharacterCompanionAuthoringResult unknown = CharacterCompanionAuthoring.ApplyResourceRename(
            package, new("physics", hash, "new.phx"));
        Assert.False(unknown.Applied);
        Assert.Same(package, unknown.Package);
        Assert.Contains(unknown.Diagnostics, d =>
            d.Code == "character_resource_reference_unclassified");
        Assert.False(CharacterCompanionAuthoring.ApplyResourceRename(
            package, new("physics", new string('0', 64), "new.phx")).Applied);
        Assert.False(CharacterCompanionAuthoring.ApplyNumericEdit(package,
            new("physics", CharacterCompanionFamily.Ragdoll, hash,
                0, 0, "12", 4)).Applied);
    }

    [Fact]
    public void EqualOriginalBytesReceiveDistinctCustodyEntries()
    {
        CustomModelPackage package = Package(
            ("one", "data/physics/one.phx", Physics, CharacterSubsystem.Ragdoll),
            ("two", "data/physics/two.phx", Physics, CharacterSubsystem.Ragdoll));
        string sharedHash = Record(package, "one").ContentSha256!;
        CharacterCompanionAuthoringResult first = CharacterCompanionAuthoring.ApplyNumericEdit(
            package, new("one", CharacterCompanionFamily.Ragdoll, sharedHash,
                1, 0, "12", 13));
        Assert.True(first.Applied);
        CharacterCompanionAuthoringResult second = CharacterCompanionAuthoring.ApplyNumericEdit(
            first.Package, new("two", CharacterCompanionFamily.Ragdoll, sharedHash,
                1, 0, "12", 14));
        Assert.True(second.Applied);
        CharacterResourceRecord[] archives = second.Package.Document.CharacterResources!.Resources
            .Where(record => record.IsOriginalArchive).ToArray();
        Assert.Equal(2, archives.Length);
        Assert.Equal(2, archives.Select(record => record.EntryPath).Distinct(StringComparer.Ordinal).Count());
        Assert.All(archives, record => Assert.Equal(sharedHash, record.ContentSha256));
        Assert.Equal(Physics, Text(package, "one"));
        Assert.Equal(Physics, Text(package, "two"));
    }

    private static CustomModelPackage Package(
        (string Id, string LogicalName, string Text, CharacterSubsystem Subsystem) first,
        (string Id, string LogicalName, string Text, CharacterSubsystem Subsystem)? second = null,
        (string Id, string LogicalName, string Text, CharacterSubsystem Subsystem)? third = null,
        bool withUtf8Bom = false)
    {
        var resources = ImmutableArray.CreateBuilder<CharacterResourceRecord>();
        var payloads = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        var rows = new List<(string Id, string LogicalName, string Text, CharacterSubsystem Subsystem)> { first };
        if (second is { } next) rows.Add(next);
        if (third is { } last) rows.Add(last);
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            byte[] source = Encoding.UTF8.GetBytes(row.Text);
            byte[] bytes = withUtf8Bom ? [0xEF, 0xBB, 0xBF, .. source] : source;
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string path = $"character/resources/{index:D5}-{hash}.bin";
            resources.Add(new CharacterResourceRecord
            {
                Id = row.Id, LogicalName = row.LogicalName,
                ContentSha256 = hash, EntryPath = path, ByteLength = bytes.Length,
                Subsystem = row.Subsystem, Status = CharacterDependencyStatus.Preserved,
            });
            payloads.Add(path, ImmutableArray.Create(bytes));
        }
        byte[] decoded = [0];
        CharacterResourceInventory inventory = new()
        {
            DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(decoded)),
            DecodedByteLength = decoded.Length,
            RootResourceId = first.Id,
            Resources = resources.ToImmutable(),
            Subsystems = rows.Select(row => row.Subsystem).Distinct()
                .Select(subsystem => new CharacterSubsystemReview(subsystem,
                    CharacterDependencyStatus.Preserved, "Original source retained."))
                .ToImmutableArray(),
            CompiledSemanticSha256 = new string('a', 64),
            LoadedResourceSha256 = new string('a', 64),
            VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"],
        };
        inventory.Validate();
        return new CustomModelPackage(new CustomModelDocument
        {
            CharacterResources = inventory,
        }, [1], ImmutableDictionary<string, ImmutableArray<byte>>.Empty)
        {
            DecodedCharacterPayload = ImmutableArray.Create(decoded),
            CompanionPayloads = payloads.ToImmutable(),
        };
    }

    [Fact]
    public void ResourceRenameRefusesUnclassifiedEffectConsumersWithoutMutation()
    {
        var package = Package(
            ("target", "data/effects/old.fx", "UnknownDef() {}", CharacterSubsystem.Damage),
            ("consumer", "data/effects/consumer.fx", "UnknownLink(\"old.fx\");", CharacterSubsystem.Damage));
        var target = Record(package, "target");
        var result = CharacterCompanionAuthoring.ApplyResourceRename(package,
            new("target", target.ContentSha256!, "new.fx"));
        Assert.False(result.Applied);
        Assert.Same(package, result.Package);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "character_resource_reference_unclassified");
        Assert.Equal("old.fx", Path.GetFileName(Record(package, "target").LogicalName));
        Assert.Equal("UnknownLink(\"old.fx\");", Text(package, "consumer"));
    }

    private static CharacterResourceRecord Record(CustomModelPackage package, string id) =>
        package.Document.CharacterResources!.Resources.Single(record => record.Id == id);

    private static string Text(CustomModelPackage package, string id)
    {
        ImmutableArray<byte> bytes = package.CompanionPayloads[Record(package, id).EntryPath!];
        ReadOnlySpan<byte> span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        return Encoding.UTF8.GetString(span);
    }
}
