using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterCompanionScopedReferenceTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private const string Body = """
        Model("body.msh")
        BodyElement(_ARM, 1, 0, 0., 20., "helper_shared")
        AddMesh2Disable("mesh_shared") // first region
        AddRelics("Arm", PHYSICS_SINGLE, "shared.phx", "blood.fx", [0,0,0], [0,0,0])
        BodyElement(_LEG, 1, 0, 0., 20., "helper_shared")
        AddMesh2Disable("mesh_shared") // second region
        UnknownStatement("mesh_shared") // opaque occurrence remains
        """;

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ScopedMeshAndHelperEditsChangeOnlySelectedRegionAndRetainOriginalEvidence()
    {
        CustomModelPackage original = Package(Body, "data/characters/body.scr", CharacterSubsystem.Damage,
            withBom: true, withActorReview: true);
        CharacterResourceRecord before = Record(original, "body");
        var firstMesh = new ReviewedCharacterReferenceEdit("body", CharacterCompanionFamily.BodyElements,
            before.ContentSha256!, 2, 0, NativeCharacterReferenceKind.MeshEntity,
            "mesh_shared", "mesh_new", ["mesh_new"]);

        CharacterCompanionAuthoringResult changed = CharacterCompanionAuthoring.ApplyReferenceEdit(original, firstMesh);

        Assert.True(changed.Applied, string.Join("; ", changed.Diagnostics.Select(d => d.Message)));
        Assert.Equal(Body, Text(original, "body"));
        string editedText = Text(changed.Package, "body");
        Assert.Contains("AddMesh2Disable(\"mesh_new\") // first region", editedText, StringComparison.Ordinal);
        Assert.Contains("AddMesh2Disable(\"mesh_shared\") // second region", editedText, StringComparison.Ordinal);
        Assert.Contains("UnknownStatement(\"mesh_shared\") // opaque occurrence remains", editedText, StringComparison.Ordinal);
        Assert.Equal(1, editedText.Split("mesh_new", StringSplitOptions.None).Length - 1);
        CharacterResourceRecord edited = Record(changed.Package, "body");
        CharacterResourceRecord archive = Record(changed.Package, "original:body");
        Assert.Equal(CharacterDependencyStatus.Ambiguous, edited.Status);
        Assert.True(archive.IsOriginalArchive);
        Assert.Equal(before.ContentSha256, archive.ContentSha256);
        Assert.Equal(Body, Text(changed.Package, "original:body"));
        Assert.Equal(Bom, changed.Package.CompanionPayloads[edited.EntryPath!].Take(3));
        Assert.Equal(Bom, changed.Package.CompanionPayloads[archive.EntryPath!].Take(3));
        Assert.Empty(changed.Package.Document.CharacterResources!.ActorSourceReviews);
        Assert.Null(changed.Package.Document.CharacterResources.CompiledSemanticSha256);
        Assert.Null(changed.Package.Document.CharacterResources.LoadedResourceSha256);
        Assert.Empty(changed.Package.Document.CharacterResources.VerifiedPlayerScenarios);

        var secondHelper = new ReviewedCharacterReferenceEdit("body", CharacterCompanionFamily.BodyElements,
            edited.ContentSha256!, 4, 5, NativeCharacterReferenceKind.Helper,
            "helper_shared", "helper_new", ["helper_new"]);
        CharacterCompanionAuthoringResult next = CharacterCompanionAuthoring.ApplyReferenceEdit(changed.Package, secondHelper);
        Assert.True(next.Applied, string.Join("; ", next.Diagnostics.Select(d => d.Message)));
        Assert.Contains("BodyElement(_ARM, 1, 0, 0., 20., \"helper_shared\")", Text(next.Package, "body"), StringComparison.Ordinal);
        Assert.Contains("BodyElement(_LEG, 1, 0, 0., 20., \"helper_new\")", Text(next.Package, "body"), StringComparison.Ordinal);
        Assert.Single(next.Package.Document.CharacterResources!.Resources, row => row.IsOriginalArchive);
        Assert.Equal(Body, Text(next.Package, "original:body"));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void StaleCallArgumentNameHashKindAndMissingReviewedTargetLeavePackageUnchanged()
    {
        CustomModelPackage package = Package(Body, "data/characters/body.scr", CharacterSubsystem.Damage);
        string hash = Record(package, "body").ContentSha256!;
        var valid = new ReviewedCharacterReferenceEdit("body", CharacterCompanionFamily.BodyElements,
            hash, 2, 0, NativeCharacterReferenceKind.MeshEntity,
            "mesh_shared", "mesh_new", ["mesh_new"]);
        ReviewedCharacterReferenceEdit[] invalid =
        [
            valid with { ExpectedSha256 = new string('0', 64) },
            valid with { CallIndex = 999 },
            valid with { ArgumentIndex = 1 },
            valid with { ExpectedName = "not_the_source" },
            valid with { Kind = NativeCharacterReferenceKind.PhysicsResource },
            valid with { ReviewedTargetNames = ["other"] },
            valid with { Kind = NativeCharacterReferenceKind.Bone },
            valid with { Kind = NativeCharacterReferenceKind.Patch },
        ];
        foreach (ReviewedCharacterReferenceEdit proposal in invalid)
        {
            CharacterCompanionAuthoringResult result = CharacterCompanionAuthoring.ApplyReferenceEdit(package, proposal);
            Assert.False(result.Applied);
            Assert.Same(package, result.Package);
            Assert.Equal(Body, Text(result.Package, "body"));
            Assert.DoesNotContain(result.Package.Document.CharacterResources!.Resources, row => row.IsOriginalArchive);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void BoneAndPatchDeclarationsRemainOnTheirGlobalRenameContracts()
    {
        const string ragdoll = "UseBone(\"original_bone\", \"capsule\", 1)\n";
        CustomModelPackage physics = Package(ragdoll, "data/odephysics/actor.phx", CharacterSubsystem.Ragdoll);
        string physicsHash = Record(physics, "body").ContentSha256!;
        Assert.False(CharacterCompanionAuthoring.ApplyReferenceEdit(physics,
            new("body", CharacterCompanionFamily.Ragdoll, physicsHash, 0, 0,
                NativeCharacterReferenceKind.Bone, "original_bone", "other_bone", ["other_bone"])).Applied);

        const string damage = "Damage(\"original_patch\", \"helper\") { Xform(1,0,0,0,1,0,0,0,1,0,0,0) }\n";
        CustomModelPackage patches = Package(damage, "data/ai/damage.scr", CharacterSubsystem.Damage);
        string damageHash = Record(patches, "body").ContentSha256!;
        Assert.False(CharacterCompanionAuthoring.ApplyReferenceEdit(patches,
            new("body", CharacterCompanionFamily.DamagePatches, damageHash, 0, 0,
                NativeCharacterReferenceKind.Patch, "original_patch", "other_patch", ["other_patch"])).Applied);
    }

    private static CustomModelPackage Package(string text, string logicalName,
        CharacterSubsystem subsystem, bool withBom = false, bool withActorReview = false)
    {
        byte[] content = Encoding.UTF8.GetBytes(text);
        byte[] bytes = withBom ? [0xEF, 0xBB, 0xBF, .. content] : content;
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var resource = new CharacterResourceRecord
        {
            Id = "body", LogicalName = logicalName, ProviderIdentity = "synthetic-provider",
            SourceFingerprint = new string('a', 64), ContentSha256 = hash,
            EntryPath = "character/resources/body.bin", ByteLength = bytes.Length,
            Subsystem = subsystem, Status = CharacterDependencyStatus.Preserved,
        };
        ImmutableArray<CharacterActorSourceReceipt> reviews = [];
        if (withActorReview)
        {
            NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
            NativeCharacterQuotedArgument model = syntax.Calls[0].QuotedArguments.Single(arg => arg.ArgumentIndex == 0);
            reviews = [new CharacterActorSourceReceipt
            {
                ActorResourceId = resource.Id, ProviderIdentity = resource.ProviderIdentity,
                SourceFingerprint = resource.SourceFingerprint, ContentSha256 = resource.ContentSha256!,
                ModelDeclarationCallIndex = 0, ActorScopeCallIndex = -1, ModelArgumentIndex = 0,
                ExactModelName = model.Value, ModelTokenStart = model.Start, ModelTokenLength = model.Length,
            }];
        }
        byte[] decoded = [0];
        var inventory = new CharacterResourceInventory
        {
            RootResourceId = resource.Id, Resources = [resource], ActorSourceReviews = reviews,
            DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(decoded)), DecodedByteLength = decoded.Length,
            Subsystems = [new CharacterSubsystemReview(subsystem, CharacterDependencyStatus.Preserved, "Original source retained.")],
            CompiledSemanticSha256 = new string('b', 64), LoadedResourceSha256 = new string('b', 64),
            VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"],
        };
        inventory.Validate();
        return new CustomModelPackage(new CustomModelDocument { CharacterResources = inventory },
            [1], ImmutableDictionary<string, ImmutableArray<byte>>.Empty)
        {
            DecodedCharacterPayload = ImmutableArray.Create(decoded),
            CompanionPayloads = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(resource.EntryPath!, ImmutableArray.Create(bytes)),
        };
    }

    private static CharacterResourceRecord Record(CustomModelPackage package, string id) =>
        package.Document.CharacterResources!.Resources.Single(row => row.Id == id);

    private static string Text(CustomModelPackage package, string id)
    {
        ReadOnlySpan<byte> bytes = package.CompanionPayloads[Record(package, id).EntryPath!].AsSpan();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        return Encoding.UTF8.GetString(bytes);
    }
}
