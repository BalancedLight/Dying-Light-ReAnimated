using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterAccessoryMaterialAuthoringTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReusePreservesOriginalSlotsSurfacesSkinsAndBytesAcrossPackageRoundTrip()
    {
        var model = Create();
        var baseline = DecodedCharacterSnapshotCodec.DecodeSource(model.Package);
        Guid retained = baseline.Package.Document.Materials[0].Id;
        Guid appended = model.Package.Document.Materials[^1].Id;
        byte[] input = CustomModelPackageSerializer.Serialize(model.Package).ToArray();
        var result = CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, appended, retained, reviewed: true);
        Assert.Equal(input, CustomModelPackageSerializer.Serialize(model.Package));
        Assert.Equal(model.Package.Document.Materials.Length - 1, result.Package.Document.Materials.Length);
        Assert.Equal(Json(model.Package.Document.Materials.RemoveAt(model.Package.Document.Materials.Length - 1)),
            Json(result.Package.Document.Materials));
        foreach (var original in baseline.Surfaces)
            Assert.Equal(Json(model.Surfaces.Single(surface => surface.Id == original.Id)),
                Json(result.Surfaces.Single(surface => surface.Id == original.Id)));
        Assert.All(result.Surfaces.Where(surface => surface.Id.StartsWith("attachment:", StringComparison.Ordinal)),
            surface => Assert.Equal(retained, surface.MaterialId));
        Assert.Equal(Json(model.Package.Document.CharacterResources!.SkinVariants), Json(result.Package.Document.CharacterResources!.SkinVariants));
        Assert.Equal(Json(model.Package.Document.CharacterResources.OriginalMaterials), Json(result.Package.Document.CharacterResources.OriginalMaterials));
        Assert.Equal(model.Package.DecodedCharacterPayload.ToArray(), result.Package.DecodedCharacterPayload.ToArray());
        Assert.Equal(model.Package.SourceFbx.ToArray(), result.Package.SourceFbx.ToArray());
        foreach (var pair in model.Package.TexturePayloads) Assert.Equal(pair.Value.ToArray(), result.Package.TexturePayloads[pair.Key].ToArray());
        foreach (var pair in model.Package.CompanionPayloads) Assert.Equal(pair.Value.ToArray(), result.Package.CompanionPayloads[pair.Key].ToArray());
        Assert.NotNull(result.Package.Document.GeometryRevision);
        Assert.Null(result.Package.Document.LastBuildReceipt);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "reused.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(result.Package, path);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal(Json(result.Surfaces), Json(reopened.Surfaces));
            Assert.Equal(Json(result.Package.Document.Materials), Json(reopened.Package.Document.Materials));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReviewIdentityAndOriginalSurfaceFailuresAreTransactional()
    {
        var model = Create();
        Guid retained = model.Package.Document.Materials[0].Id;
        Guid appended = model.Package.Document.Materials[^1].Id;
        AssertRejected(model, () => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, appended, retained, false));
        AssertRejected(model, () => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, retained, appended, true));
        AssertRejected(model, () => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, Guid.NewGuid(), retained, true));
        var changed = model with { Surfaces = model.Surfaces.SetItem(0, model.Surfaces[0] with { MaterialId = appended }) };
        AssertRejected(changed, () => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(changed, appended, retained, true));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void MissingStaleCandidateAndAmbiguousCustodyCannotAuthorizeReuse()
    {
        foreach (string change in new[] { "missing", "stale", "candidate", "ambiguous" })
        {
            var model = Create();
            var inventory = model.Package.Document.CharacterResources!;
            var record = inventory.Resources.Single(resource => resource.Material is not null);
            var package = model.Package;
            if (change == "missing") inventory = inventory with { Resources = inventory.Resources.Remove(record) };
            if (change == "candidate") inventory = inventory with
                { Resources = inventory.Resources.Select(resource => resource.Id == record.Id ? resource with { Status = CharacterDependencyStatus.Missing } : resource).ToImmutableArray() };
            if (change == "stale")
            {
                string path = inventory.Resources.Single(resource => resource.Id == record.Material!.ProviderResourceId).EntryPath!;
                byte[] bytes = package.CompanionPayloads[path].ToArray(); bytes[^1] ^= 1;
                package = package with { CompanionPayloads = package.CompanionPayloads.SetItem(path, bytes.ToImmutableArray()) };
            }
            if (change == "ambiguous")
            {
                var duplicate = record with { Id = "duplicate-material", EntryPath = "character/resources/duplicate-material.bin" };
                inventory = inventory with { Resources = inventory.Resources.Add(duplicate) };
                package = package with { CompanionPayloads = package.CompanionPayloads.Add(duplicate.EntryPath!, package.CompanionPayloads[record.EntryPath!]) };
            }
            model = model with { Package = package with { Document = package.Document with { CharacterResources = inventory } } };
            Guid retained = model.Package.Document.Materials[0].Id;
            Guid appended = model.Package.Document.Materials[^1].Id;
            byte[] before = JsonSerializer.SerializeToUtf8Bytes(model.Package.Document, CustomModelPackageSerializer.CreateSerializerOptions());
            Exception? error = Record.Exception(() => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, appended, retained, true));
            Assert.True(error is InvalidDataException or ArgumentException or FormatException, error?.ToString());
            Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(model.Package.Document, CustomModelPackageSerializer.CreateSerializerOptions()));
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void CaseAliasOrSanitizedReferenceChangesAreRefused()
    {
        var model = Create();
        Guid retained = model.Package.Document.Materials[0].Id;
        Guid appended = model.Package.Document.Materials[^1].Id;
        var stale = model with { Package = model.Package with { Document = model.Package.Document with
            { Materials = model.Package.Document.Materials.SetItem(0, model.Package.Document.Materials[0] with { ExistingDl1MaterialReference = "Generic.mat" }) } } };
        AssertRejected(stale, () => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(stale, appended, retained, true));
        var rewritten = Create("folder/generic.mat");
        AssertRejected(rewritten, () => CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(rewritten,
            rewritten.Package.Document.Materials[^1].Id, rewritten.Package.Document.Materials[0].Id, true));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ReplacedAccessoryTextureBytesAreArchivedAndSurviveReload()
    {
        var model = Create();
        byte[] bytes = [11, 22, 33, 44];
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        const string path = "textures/accessory.png";
        var appended = model.Package.Document.Materials[^1];
        var binding = new CustomModelTextureBinding
        {
            Semantic = CustomModelTextureSemantic.BaseColor, SourceKind = CustomModelTextureSourceKind.EmbeddedFbx,
            DisplayName = "accessory.png", PackageEntryPath = path, OriginalReference = "accessory.png",
            ContentSha256 = hash, MediaType = "image/png",
        };
        model = model with { Package = model.Package with
        {
            TexturePayloads = model.Package.TexturePayloads.Add(path, bytes.ToImmutableArray()),
            Document = model.Package.Document with { Materials = model.Package.Document.Materials.SetItem(
                model.Package.Document.Materials.Length - 1, appended with { Textures = [binding] }) },
        } };
        var result = CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(model, appended.Id,
            model.Package.Document.Materials[0].Id, true);
        Assert.False(result.Package.TexturePayloads.ContainsKey(path));
        var archive = Assert.Single(result.Package.Document.CharacterResources!.Resources,
            resource => resource.Subsystem == CharacterSubsystem.Textures && resource.IsOriginalArchive && resource.ContentSha256 == hash);
        Assert.False(archive.Required);
        Assert.Equal(bytes, result.Package.CompanionPayloads[archive.EntryPath!].ToArray());
        Assert.True(model.Package.TexturePayloads.ContainsKey(path));
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string file = Path.Combine(directory, "authored.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(result.Package, file);
            var reopened = CustomModelPackageSerializer.Load(file);
            Assert.Equal(bytes, reopened.CompanionPayloads[archive.EntryPath!].ToArray());
            Assert.Equal(Json(result.Package.Document.Materials), Json(reopened.Document.Materials));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    internal static FbxModelAuthoringImportResult Create(string reference = "generic.mat")
    {
        var package = CharacterMaterialReceiptTests.Create();
        var source = DecodedCharacterSnapshotCodec.DecodeSource(package);
        var materials = source.Package.Document.Materials.SetItem(0, source.Package.Document.Materials[0] with
            { ExistingDl1MaterialReference = reference });
        var sourceDoc = source.Package.Document with { Materials = materials };
        ImmutableArray<byte> decoded = DecodedCharacterSnapshotCodec.Encode(new(sourceDoc, source.Surfaces, source.SourceLodGroups)
            { CharacterLods = source.SourceCharacterLods });
        var inventory = package.Document.CharacterResources!;
        var record = inventory.Resources.Single(resource => resource.Material is not null);
        inventory = inventory with
        {
            DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(decoded.AsSpan())), DecodedByteLength = decoded.Length,
            OriginalMaterialSlotCount = materials.Length,
            OriginalMaterials = materials.Select((material, index) => new CharacterOriginalMaterial(index,
                material.ExistingDl1MaterialReference ?? material.Name, 1)).ToImmutableArray(),
            SkinVariants = [new("Default", 0x80, [new(0, 0)], [], 0, 0)],
            Resources = inventory.Resources.Select(resource => resource.Id == record.Id
                ? resource with { LogicalName = reference, Material = record.Material! with { MaterialName = reference } } : resource).ToImmutableArray(),
        };
        package = package with { Document = package.Document with { Materials = materials, CharacterResources = inventory }, DecodedCharacterPayload = decoded };
        var original = FbxModelAuthoringImporter.ImportPackage(package);
        var attachment = FbxModelAuthoringImporter.Import(ModelsWorkspaceMorphAuthoringTests.CreateGenericManualSculptFbx(0),
            "accessory.fbx", new() { DecodeAnimationClips = false });
        var ids = attachment.Package.Document.Materials.ToDictionary(material => material.Id, _ => Guid.NewGuid());
        attachment = attachment with
        {
            Surfaces = attachment.Surfaces.Select(surface => surface with { MeshName = "accessory", MaterialId = ids[surface.MaterialId] }).ToImmutableArray(),
            Package = attachment.Package with { Document = attachment.Package.Document with
            {
                Materials = attachment.Package.Document.Materials.Select(material => material with { Id = ids[material.Id], Name = "Accessory material" }).ToImmutableArray(),
                Meshes = attachment.Package.Document.Meshes.Select(mesh => mesh with { Name = "accessory" }).ToImmutableArray(),
            } },
        };
        return CharacterGeometryAuthoring.AddAttachment(original, attachment, original.Package.Document.CreateEffectiveBones()[0].Name);
    }

    private static void AssertRejected(FbxModelAuthoringImportResult model, Action operation)
    {
        byte[] before = CustomModelPackageSerializer.Serialize(model.Package).ToArray();
        Exception? error = Record.Exception(operation);
        Assert.True(error is InvalidDataException or InvalidOperationException or ArgumentException or FormatException, error?.ToString());
        Assert.Equal(before, CustomModelPackageSerializer.Serialize(model.Package));
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, CustomModelPackageSerializer.CreateSerializerOptions());
}
