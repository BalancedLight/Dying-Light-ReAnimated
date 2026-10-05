using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterCompanionExportCoverageTests
{
    [Fact]
    public void OriginalPresetBytesAndVirtualPathSurviveSourceExport()
    {
        var package = CharacterBodyRegionAuthoringTests.CreateGenericBodyRegionPackage();
        var inventory = package.Document.CharacterResources!;
        byte[] source = System.Text.Encoding.UTF8.GetBytes("// retain spacing\r\nPresetDef(\"Character\") { Preset(\"ActorA\") { SetField(\"MeshName\", \"body.msh\"); Unknown(7); } }\r\n");
        const string entry = "character/resources/preset.bin";
        const string name = "data/presets/actors.pre";
        var record = new CharacterResourceRecord
        {
            Id = "file:" + name, LogicalName = name, EntryPath = entry,
            Subsystem = CharacterSubsystem.Helpers, Status = CharacterDependencyStatus.Preserved,
            ByteLength = source.Length, ContentSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source)),
            Required = true,
        };
        package = package with
        {
            Document = package.Document with { CharacterResources = inventory with { Resources = inventory.Resources.Add(record) } },
            CompanionPayloads = package.CompanionPayloads.Add(entry, source.ToImmutableArray()),
        };
        var exported = Dl1NativeCompanionWriter.BuildPreservedPackage(package, "generic_character", package.Document.Bones.Select(bone => bone.Name));
        Assert.Equal(source, exported.Files["character-resources/" + name]);
        Assert.Equal(name, Dl1NativeCompanionWriter.PreservedVirtualPath("character-resources/" + name));
    }

    [Fact]
    public void TypedDetachedMeshHasRepresentableOutputButDoesNotConferGameAcceptance()
    {
        var package=CharacterMaterialReceiptTests.Create();var inventory=package.Document.CharacterResources!;
        var texture=inventory.Resources.Single(resource=>resource.NativeResource is not null);
        var part=texture with {Id="detached",LogicalName="generic_part",EntryPath="character/resources/part.bin",Subsystem=CharacterSubsystem.DetachedParts,
            NativeResource=texture.NativeResource! with {ResourceName="generic_part",ResourceType=272}};
        inventory=inventory with {Resources=inventory.Resources.Add(part)};
        Assert.DoesNotContain(inventory.ExportBlockers,message=>message.Contains("generic_part",StringComparison.Ordinal));
        Assert.False(inventory.IsGameReady);
    }

    [Fact]
    public void PackedEffectReceiptsNoLongerBlockRepresentableGatheredOutput()
    {
        var package=CharacterEffectExportCommandTests.CreatePackage();
        var inventory=package.Document.CharacterResources!;
        Assert.DoesNotContain(inventory.ExportBlockers,message=>message.Contains("requires a gathered runtime resource",StringComparison.Ordinal));
        Assert.False(inventory.IsGameReady);
        var definitions=CharacterEffectResourceAuthoring.ReadRequiredDefinitions(package);
        Assert.Equal(2,definitions.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RequiredEffectAndDetachedAssetBlockCompleteExportBeforeFilesButKeepSources()
    {
        var package = CharacterBodyRegionAuthoringTests.CreateGenericBodyRegionPackage();
        var inventory = package.Document.CharacterResources!;
        package = package with { Document = package.Document with { CharacterResources = inventory with
        {
            Subsystems = inventory.Subsystems.Select(review => review with
            { Status = CharacterDependencyStatus.Preserved, Detail = string.Empty }).ToImmutableArray(),
            CompiledSemanticSha256 = new string('a',64), LoadedResourceSha256 = new string('a',64),
            VerifiedPlayerScenarios = ["stock-reuse","facial","ragdoll","gore"],
        } } };
        inventory = package.Document.CharacterResources!;
        Assert.Contains(inventory.ExportBlockers, message => message.Contains("impact.fx", StringComparison.Ordinal));
        Assert.Contains(inventory.ExportBlockers, message => message.Contains("part.msh", StringComparison.Ordinal));
        Assert.False(inventory.IsDependencyComplete);
        Assert.False(inventory.IsGameReady);
        var sources = Dl1NativeCompanionWriter.BuildPreservedPackage(package,"generic_character", package.Document.CreateEffectiveBones().Select(bone => bone.Name));
        var effect = inventory.Resources.Single(resource => resource.Id == "effect");
        Assert.Equal(package.CompanionPayloads[effect.EntryPath!].ToArray(), sources.Files["character-resources/data/effects/impact.fx"]);
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string output=Path.Combine(directory,"blocked");
            var failure=await Assert.ThrowsAsync<InvalidDataException>(() => Dl1SourceModelWriter.WriteAsync(new()
            { Model=FbxModelAuthoringImporter.ImportPackage(package), OutputDirectory=output, ResourceName="generic_character" }));
            Assert.Contains("gathered runtime resource",failure.Message,StringComparison.Ordinal);
            Assert.False(Directory.Exists(output));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
}