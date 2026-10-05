using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterRagdollShapeAuthoringTests
{
    private static readonly string Source = string.Join("\r\n",
        "!include(\"generic_base.phx\")",
        "Bones() {",
        "    UseBone(\"root\", \"sphere\", 3) // chosen bone",
        "    UseBoneScale(\"arm\", \"capsule\", 2, 0.75)",
        "    Future(\"sphere\", \"capsule\") // unclassified syntax retained",
        "}",
        string.Empty);

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReviewedObservedShapeChangesOneCallAndArchivesExactOriginalBytes()
    {
        CustomModelPackage original = Package();
        CharacterResourceRecord before = Record(original, "ragdoll");
        int call = Dl1RagdollCodec.Read(Source).Bones.Single(bone => bone.BoneName == "root").CallIndex;
        var proposal = new ReviewedRagdollShapeEdit("ragdoll", before.ContentSha256!, call,
            "sphere", "capsule", ["sphere", "capsule"]);

        CharacterCompanionAuthoringResult result = CharacterCompanionAuthoring.ApplyRagdollShapeEdit(original, proposal);

        Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal(Source, Text(original, "ragdoll"));
        string changed = Source.Replace("UseBone(\"root\", \"sphere\", 3)",
            "UseBone(\"root\", \"capsule\", 3)", StringComparison.Ordinal);
        Assert.Equal(changed, Text(result.Package, "ragdoll"));
        Assert.Contains("UseBoneScale(\"arm\", \"capsule\", 2, 0.75)", Text(result.Package, "ragdoll"), StringComparison.Ordinal);
        Assert.Contains("Future(\"sphere\", \"capsule\")", Text(result.Package, "ragdoll"), StringComparison.Ordinal);
        Assert.Equal(3, Dl1RagdollCodec.Read(Text(result.Package, "ragdoll")).Bones[0].RelativeMass);
        Assert.Equal(0.75, Dl1RagdollCodec.Read(Text(result.Package, "ragdoll")).Bones[1].RadiusMultiplier);
        CharacterResourceRecord archived = Record(result.Package, "original:ragdoll");
        Assert.True(archived.IsOriginalArchive);
        Assert.Equal(before.ContentSha256, archived.ContentSha256);
        Assert.Equal(Source, Text(result.Package, archived.Id));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, Bytes(result.Package, "ragdoll").Take(3));
        Assert.Equal(Bytes(original, "ragdoll"), Bytes(result.Package, archived.Id));
        Assert.Null(result.Package.Document.CharacterResources!.CompiledSemanticSha256);
        Assert.Null(result.Package.Document.CharacterResources.LoadedResourceSha256);
        Assert.Empty(result.Package.Document.CharacterResources.VerifiedPlayerScenarios);
        Assert.Equal(CharacterDependencyStatus.Ambiguous, Record(result.Package, "ragdoll").Status);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReviewedSupportedShapeCanBeAddedWithoutAnExistingDeclaration()
    {
        CustomModelPackage package = Package();
        CharacterResourceRecord resource = Record(package, "ragdoll");
        int call = Dl1RagdollCodec.Read(Source).Bones.Single(bone => bone.BoneName == "root").CallIndex;
        var result = CharacterCompanionAuthoring.ApplyRagdollShapeEdit(package,
            new("ragdoll", resource.ContentSha256!, call, "sphere", "box", ["box"]));
        Assert.True(result.Applied, string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.Equal("box", Dl1RagdollCodec.Read(Text(result.Package, "ragdoll")).Bones[0].ShapeToken);
        Assert.Equal(Source, Text(result.Package, "original:ragdoll"));
        Assert.Contains("Future(\"sphere\", \"capsule\")", Text(result.Package, "ragdoll"), StringComparison.Ordinal);
        Assert.Equal(Source, Text(package, "ragdoll"));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void StaleOrUnreviewedShapeProposalsLeaveSourceAndInventoryUnchanged()
    {
        CustomModelPackage package = Package();
        CharacterResourceRecord source = Record(package, "ragdoll");
        int call = Dl1RagdollCodec.Read(Source).Bones.Single(bone => bone.BoneName == "root").CallIndex;
        var valid = new ReviewedRagdollShapeEdit("ragdoll", source.ContentSha256!, call,
            "sphere", "capsule", ["capsule"]);
        ReviewedRagdollShapeEdit[] refused =
        [
            valid with { ExpectedSha256 = new string('0', 64) },
            valid with { CallIndex = 0 },
            valid with { CallIndex = 999 },
            valid with { ExpectedShape = "capsule" },
            valid with { NewShape = "cone", ReviewedSourceShapeTokens = ["cone"] },
            valid with { ReviewedSourceShapeTokens = ["sphere"] },
            valid with { NewShape = "sphere" },
        ];
        foreach (ReviewedRagdollShapeEdit proposal in refused)
        {
            CharacterCompanionAuthoringResult result = CharacterCompanionAuthoring.ApplyRagdollShapeEdit(package, proposal);
            Assert.False(result.Applied);
            Assert.Same(package, result.Package);
            Assert.Equal(Source, Text(result.Package, "ragdoll"));
            Assert.DoesNotContain(result.Package.Document.CharacterResources!.Resources,
                resource => resource.IsOriginalArchive);
        }
    }

    private static CustomModelPackage Package()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Source)];
        string hash = Sha(bytes);
        const string entry = "character/resources/ragdoll.bin";
        CharacterResourceRecord resource = new()
        {
            Id = "ragdoll", LogicalName = "data/physics/generic_ragdoll.phx",
            ProviderIdentity = "synthetic-provider", SourceFingerprint = new string('a', 64),
            ContentSha256 = hash, EntryPath = entry, ByteLength = bytes.Length,
            Subsystem = CharacterSubsystem.Ragdoll, Status = CharacterDependencyStatus.Preserved,
        };
        byte[] decoded = [0];
        CharacterResourceInventory inventory = new()
        {
            RootResourceId = resource.Id,
            Resources = [resource],
            DecodedSha256 = Sha(decoded), DecodedByteLength = decoded.Length,
            Subsystems = [new CharacterSubsystemReview(CharacterSubsystem.Ragdoll,
                CharacterDependencyStatus.Preserved, "Original source retained.")],
            CompiledSemanticSha256 = new string('b', 64),
            LoadedResourceSha256 = new string('b', 64),
            VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"],
        };
        inventory.Validate();
        return new CustomModelPackage(new CustomModelDocument { CharacterResources = inventory },
            [1], ImmutableDictionary<string, ImmutableArray<byte>>.Empty)
        {
            DecodedCharacterPayload = ImmutableArray.Create(decoded),
            CompanionPayloads = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(entry, ImmutableArray.Create(bytes)),
        };
    }

    private static CharacterResourceRecord Record(CustomModelPackage package, string id) =>
        package.Document.CharacterResources!.Resources.Single(resource => resource.Id == id);

    private static byte[] Bytes(CustomModelPackage package, string id) =>
        package.CompanionPayloads[Record(package, id).EntryPath!].ToArray();

    private static string Text(CustomModelPackage package, string id)
    {
        ReadOnlySpan<byte> bytes = Bytes(package, id);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
