using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterGeometryAttachmentTests
{
    [Fact]
    public void SaveAndReopenPreserveOriginalFaceRigMorphLodsVariantsAndCompanions()
    {
        var original = Target();
        var result = CharacterGeometryAuthoring.AddAttachment(original, Attachment("accessory_alpha", 0), Bone(original));
        AssertOriginalData(original, result);
        Assert.Null(result.Package.Document.CharacterResources!.CompiledSemanticSha256);
        Assert.Null(result.Package.Document.CharacterResources.LoadedResourceSha256);
        Assert.Empty(result.Package.Document.CharacterResources.VerifiedPlayerScenarios);
        string folder = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(folder, "authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(result.Package, path);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            AssertOriginalData(original, reopened);
            Assert.Equal(JsonSerializer.Serialize(result.SourceCharacterLods), JsonSerializer.Serialize(reopened.SourceCharacterLods));
            Assert.Equal(result.Surfaces.Length, reopened.Surfaces.Length);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(folder); }
    }

    [Fact]
    public void MultipleDistinctAttachmentsAllocateUniqueEntityIndexesAndCannotRepeatByAlias()
    {
        var original = Target();
        var firstSource = Attachment("accessory_alpha", 0);
        var first = CharacterGeometryAuthoring.AddAttachment(original, firstSource, Bone(original));
        var second = CharacterGeometryAuthoring.AddAttachment(first, Attachment("accessory_beta", .25), Bone(original));
        int[] indexes = second.SourceCharacterLods.Select(node => node.SourceEntityIndex).Where(index => index < 0).ToArray();
        Assert.Equal(2, indexes.Length);
        Assert.Equal(2, indexes.Distinct().Count());
        AssertOriginalData(original, second);
        var alias = firstSource with
        {
            Surfaces = firstSource.Surfaces.Select(surface => surface with { MeshName = "different_alias" }).ToImmutableArray(),
            Package = firstSource.Package with { Document = firstSource.Package.Document with
                { Meshes = firstSource.Package.Document.Meshes.Select(mesh => mesh with { Name = "different_alias" }).ToImmutableArray() } },
        };
        string before = JsonSerializer.Serialize(second);
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(second, alias, Bone(original)));
        Assert.Equal(before, JsonSerializer.Serialize(second));
    }

    [Fact]
    public void MaterialGeometryNameAndBindingFailuresLeaveTheTargetUnchanged()
    {
        var original = Target();
        var accessory = Attachment("accessory_alpha", 0);
        var surface = Assert.Single(accessory.Surfaces);
        string before = JsonSerializer.Serialize(original);
        var unknownMaterial = accessory with { Surfaces = [surface with { MaterialId = Guid.NewGuid() }] };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, unknownMaterial, Bone(original)));
        var boneCollision = accessory with
        {
            SourceCharacterLods = [new(Bone(original), -1, [new(0, [surface.Id])])],
        };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, boneCollision, Bone(original)));
        var duplicate = accessory with { Surfaces = [surface, surface] };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, duplicate, Bone(original)));
        var mixedComponents = accessory with { Surfaces = [surface, surface with
            { Id = surface.SourceGeometry!.Id, MeshName = "another_component", SourceGeometry = null }] };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, mixedComponents, Bone(original)));
        var malformed = accessory with { Surfaces = [surface with { Vertices = default }] };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, malformed));
        var unweighted = accessory with { Surfaces = [surface with { IsSkinned = false }] };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, unweighted));
        Assert.Equal(before, JsonSerializer.Serialize(original));
    }

    [Fact]
    public void ConsistentSharedGeometryMaterialDrawsShareOneLodButDisagreementIsRejected()
    {
        var original = Target();
        var accessory = Attachment("accessory_alpha", 0);
        var surface = Assert.Single(accessory.Surfaces);
        var split = accessory with { Surfaces = [surface, surface with { Id = surface.Id + "/material-part" }] };
        var result = CharacterGeometryAuthoring.AddAttachment(original, split, Bone(original));
        var node = Assert.Single(result.SourceCharacterLods);
        Assert.Equal(2, Assert.Single(node.Levels).SurfaceIds.Length);
        AssertOriginalData(original, result);
        var geometry = surface.SourceGeometry!;
        var disagree = split with { Surfaces = split.Surfaces.SetItem(1, split.Surfaces[1] with
            { SourceGeometry = geometry with { ControlPoints = geometry.ControlPoints.SetItem(0, geometry.ControlPoints[0] + Vector3D.UnitX) } }) };
        Assert.Throws<InvalidDataException>(() => CharacterGeometryAuthoring.AddAttachment(original, disagree, Bone(original)));
    }

    private static FbxModelAuthoringImportResult Target()
    {
        var package = ModelsWorkspaceMorphAuthoringTests.CreateGenericReferencePackage();
        package = package with { Document = package.Document with { CharacterResources = package.Document.CharacterResources! with
            { CompiledSemanticSha256 = new string('b', 64), LoadedResourceSha256 = new string('b', 64),
              VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"] } } };
        return FbxModelAuthoringImporter.ImportPackage(package);
    }

    private static FbxModelAuthoringImportResult Attachment(string name, double displacement)
    {
        var model = FbxModelAuthoringImporter.Import(ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(displacement),
            "accessory.fbx", new() { DecodeAnimationClips = false });
        var materialIds = model.Package.Document.Materials.ToDictionary(material => material.Id, _ => Guid.NewGuid());
        return model with
        {
            Surfaces = model.Surfaces.Select(surface => surface with { MeshName = name, MaterialId = materialIds[surface.MaterialId] }).ToImmutableArray(),
            Package = model.Package with { Document = model.Package.Document with
                { Meshes = model.Package.Document.Meshes.Select(mesh => mesh with { Name = name }).ToImmutableArray(),
                  Materials = model.Package.Document.Materials.Select(material => material with { Id = materialIds[material.Id], Name = name + "_material" }).ToImmutableArray() } },
        };
    }

    private static string Bone(FbxModelAuthoringImportResult model) => model.Package.Document.CreateEffectiveBones()[0].Name;

    private static void AssertOriginalData(FbxModelAuthoringImportResult original, FbxModelAuthoringImportResult changed)
    {
        Assert.Equal(JsonSerializer.Serialize(original.Surfaces), JsonSerializer.Serialize(changed.Surfaces.Take(original.Surfaces.Length).ToArray()));
        Assert.Equal(JsonSerializer.Serialize(original.Package.Document.Bones), JsonSerializer.Serialize(changed.Package.Document.Bones));
        Assert.Equal(JsonSerializer.Serialize(original.Package.Document.MorphChannels), JsonSerializer.Serialize(changed.Package.Document.MorphChannels));
        Assert.Equal(JsonSerializer.Serialize(original.Package.Document.Materials),
            JsonSerializer.Serialize(changed.Package.Document.Materials.Take(original.Package.Document.Materials.Length).ToArray()));
        Assert.Equal(original.Package.SourceFbx.ToArray(), changed.Package.SourceFbx.ToArray());
        Assert.Equal(original.Package.DecodedCharacterPayload.ToArray(), changed.Package.DecodedCharacterPayload.ToArray());
        Assert.Equal(JsonSerializer.Serialize(original.Package.Document.CharacterResources!.MorphBindings),
            JsonSerializer.Serialize(changed.Package.Document.CharacterResources!.MorphBindings));
        Assert.Equal(JsonSerializer.Serialize(original.Package.Document.CharacterResources.Resources),
            JsonSerializer.Serialize(changed.Package.Document.CharacterResources.Resources));
        Assert.Equal(original.Package.CompanionPayloads.Count, changed.Package.CompanionPayloads.Count);
        foreach (var pair in original.Package.CompanionPayloads)
            Assert.Equal(pair.Value.ToArray(), changed.Package.CompanionPayloads[pair.Key].ToArray());
        Assert.Equal(JsonSerializer.Serialize(original.SourceCharacterLods),
            JsonSerializer.Serialize(changed.SourceCharacterLods.Take(original.SourceCharacterLods.Length).ToArray()));
    }
}
