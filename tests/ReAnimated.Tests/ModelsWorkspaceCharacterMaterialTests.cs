using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceCharacterMaterialTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ReviewedMaterialReusePreservesOriginalMorphsThroughUndoRedoAndSaveReload()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            var model = CreateWithOriginalMorph();
            CustomModelPackageSerializer.SaveAtomic(model.Package, path);
            using var workspace = CreateWorkspace(path);
            await workspace.OpenPackagePathAsync(path);
            Assert.True(workspace.HasModel, workspace.BuildStatus);
            var before = CurrentModel(workspace);
            Guid appended = before.Package.Document.Materials[^1].Id;
            var retained = Assert.Single(workspace.RetainedCharacterMaterialChoices);
            var accessory = Assert.Single(workspace.AccessoryCharacterMaterialChoices);
            Assert.Equal(appended, accessory.MaterialId);
            Assert.False(workspace.ApplyCharacterMaterialCommand.CanExecute(null));
            workspace.ApplyCharacterMaterialCommand.Execute(null);
            Assert.Same(before, CurrentModel(workspace));
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.CharacterMaterialReviewed = true;
            Assert.True(workspace.ApplyCharacterMaterialCommand.CanExecute(null));
            workspace.ApplyCharacterMaterialCommand.Execute(null);
            var after = CurrentModel(workspace);
            Assert.Equal("Material assigned.", workspace.BuildStatus);
            Assert.False(workspace.CharacterMaterialReviewed);
            Assert.Empty(workspace.AccessoryCharacterMaterialChoices);
            Assert.DoesNotContain(after.Package.Document.Materials, row => row.Id == appended);
            Assert.All(after.Surfaces.Where(surface => surface.Id.StartsWith("attachment:", StringComparison.Ordinal)),
                surface => Assert.Equal(retained.MaterialId, surface.MaterialId));
            AssertOriginalPreserved(before, after);

            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Contains(CurrentModel(workspace).Package.Document.Materials, row => row.Id == appended);
            Assert.Single(workspace.AccessoryCharacterMaterialChoices);
            Assert.False(workspace.CharacterMaterialReviewed);
            Assert.All(CurrentModel(workspace).Surfaces.Where(surface => surface.Id.StartsWith("attachment:", StringComparison.Ordinal)),
                surface => Assert.Equal(appended, surface.MaterialId));
            AssertOriginalPreserved(before, CurrentModel(workspace));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.DoesNotContain(CurrentModel(workspace).Package.Document.Materials, row => row.Id == appended);
            AssertOriginalPreserved(before, CurrentModel(workspace));

            workspace.SavePackageCommand.Execute(null);
            Assert.DoesNotContain("Save failed", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            using var reopened = CreateWorkspace(path);
            await reopened.OpenPackagePathAsync(path);
            AssertOriginalPreserved(before, CurrentModel(reopened));
            Assert.Equal(Json(after.Package.Document.Materials), Json(CurrentModel(reopened).Package.Document.Materials));
            Assert.Empty(reopened.AccessoryCharacterMaterialChoices);
            Assert.False(reopened.CharacterMaterialReviewed);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task SelectionAndModelChangesClearReviewAndStaleChoicesCannotApply()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "first.dlrmodel"), secondPath = Path.Combine(directory, "second.dlrmodel");
            var model = CreateWithOriginalMorph();
            CustomModelPackageSerializer.SaveAtomic(model.Package, path);
            CustomModelPackageSerializer.SaveAtomic(model.Package, secondPath);
            using var workspace = CreateWorkspace(path);
            await workspace.OpenPackagePathAsync(path);
            Assert.True(workspace.HasModel, workspace.BuildStatus);
            var retained = Assert.Single(workspace.RetainedCharacterMaterialChoices);
            var accessory = Assert.Single(workspace.AccessoryCharacterMaterialChoices);
            workspace.CharacterMaterialReviewed = true;
            workspace.SelectedAccessoryCharacterMaterial = null;
            Assert.False(workspace.CharacterMaterialReviewed);
            workspace.SelectedAccessoryCharacterMaterial = accessory;
            workspace.CharacterMaterialReviewed = true;
            workspace.SelectedRetainedCharacterMaterial = null;
            Assert.False(workspace.CharacterMaterialReviewed);
            workspace.SelectedRetainedCharacterMaterial = retained;
            workspace.CharacterMaterialReviewed = true;
            await workspace.OpenPackagePathAsync(secondPath);
            Assert.False(workspace.CharacterMaterialReviewed);
            var before = CurrentModel(workspace);
            workspace.SelectedAccessoryCharacterMaterial = accessory;
            workspace.SelectedRetainedCharacterMaterial = retained;
            workspace.CharacterMaterialReviewed = true;
            Assert.False(workspace.ApplyCharacterMaterialCommand.CanExecute(null));
            workspace.ApplyCharacterMaterialCommand.Execute(null);
            Assert.Same(before, CurrentModel(workspace));
            Assert.False(workspace.CharacterMaterialReviewed);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));

            workspace.SelectedAccessoryCharacterMaterial = new(Guid.NewGuid(), "Unknown");
            workspace.SelectedRetainedCharacterMaterial = Assert.Single(workspace.RetainedCharacterMaterialChoices);
            workspace.CharacterMaterialReviewed = true;
            workspace.ApplyCharacterMaterialCommand.Execute(null);
            Assert.Same(before, CurrentModel(workspace));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task MissingRetainedCustodyDoesNotOfferAnAssignmentOrChangeTheModel()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "character.dlrmodel");
            var model = CreateWithOriginalMorph();
            var inventory = model.Package.Document.CharacterResources!;
            var rows = inventory.Resources.Select(row => row.Material is not null
                ? row with { Status = CharacterDependencyStatus.Missing } : row).ToImmutableArray();
            var package = model.Package with { Document = model.Package.Document with { CharacterResources = inventory with { Resources = rows } } };
            CustomModelPackageSerializer.SaveAtomic(package, path);
            using var workspace = CreateWorkspace(path);
            await workspace.OpenPackagePathAsync(path);
            Assert.True(workspace.HasModel, workspace.BuildStatus);
            Assert.Empty(workspace.RetainedCharacterMaterialChoices);
            Assert.Single(workspace.AccessoryCharacterMaterialChoices);
            var before = CurrentModel(workspace);
            workspace.CharacterMaterialReviewed = true;
            Assert.False(workspace.ApplyCharacterMaterialCommand.CanExecute(null));
            workspace.ApplyCharacterMaterialCommand.Execute(null);
            Assert.Same(before, CurrentModel(workspace));
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static FbxModelAuthoringImportResult CreateWithOriginalMorph()
    {
        var model = CharacterAccessoryMaterialAuthoringTests.Create();
        var original = DecodedCharacterSnapshotCodec.DecodeSource(model.Package);
        var surface = original.Surfaces[0];
        const string expression = "expression";
        const uint descriptor = 0x1234;
        var morph = new FbxModelMorphTarget(expression, descriptor, 100, 101,
            surface.Vertices.Select((_, index) => index == 0 ? new Vector3D(.1, 0, 0) : Vector3D.Zero).ToImmutableArray());
        surface = surface with { MorphTargets = [morph] };
        ImmutableArray<CustomModelMorphChannel> channels = [new()
        {
            Index = 0, Name = expression, DescriptorHash = descriptor,
            BlendShapeChannelObjectId = 100, ShapeObjectId = 101, GeometryObjectIds = [200],
        }];
        var sourceSurfaces = original.Surfaces.SetItem(0, surface);
        string signature = FbxModelAuthoringImporter.ComputeMorphSignature(channels, sourceSurfaces);
        var sourceDocument = original.Package.Document with { MorphChannels = channels, MorphSignature = signature,
            Meshes = original.Package.Document.Meshes.SetItem(0, original.Package.Document.Meshes[0] with { GeometryObjectId = 200 }) };
        ImmutableArray<byte> decoded = DecodedCharacterSnapshotCodec.Encode(new(sourceDocument, sourceSurfaces, original.SourceLodGroups)
            { CharacterLods = original.SourceCharacterLods });
        var inventory = model.Package.Document.CharacterResources! with
        {
            DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(decoded.AsSpan())), DecodedByteLength = decoded.Length,
            MorphBindings = [new(5, 0, expression, descriptor, surface.Id, 0, 0, surface.Vertices.Length, "VertexDeltasDecoded")],
        };
        var surfaces = model.Surfaces.Select(row => row.Id == surface.Id ? row with { MorphTargets = [morph] } : row).ToImmutableArray();
        return ModelGeometryRevisionCodec.Capture(model with
        {
            Surfaces = surfaces,
            Package = model.Package with
            {
                DecodedCharacterPayload = decoded,
                Document = model.Package.Document with { MorphChannels = channels, MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(channels, surfaces), CharacterResources = inventory,
                    Meshes = model.Package.Document.Meshes.SetItem(0, model.Package.Document.Meshes[0] with { GeometryObjectId = 200 }) },
            },
        });
    }

    private static void AssertOriginalPreserved(FbxModelAuthoringImportResult before, FbxModelAuthoringImportResult after)
    {
        var originalIds = DecodedCharacterSnapshotCodec.DecodeSource(before.Package).Surfaces.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var originalSurfaces = before.Surfaces.Where(row => originalIds.Contains(row.Id)).ToImmutableArray();
        Assert.NotEmpty(originalSurfaces.SelectMany(row => row.MorphTargets));
        Assert.Contains(originalSurfaces.SelectMany(row => row.MorphTargets).SelectMany(row => row.PositionDeltas), delta => delta != Vector3D.Zero);
        Assert.Equal(Json(originalSurfaces), Json(after.Surfaces.Where(row => originalIds.Contains(row.Id)).ToImmutableArray()));
        Assert.Equal(Json(before.Package.Document.MorphChannels), Json(after.Package.Document.MorphChannels));
        Assert.Equal(Json(before.Package.Document.CharacterResources!.MorphBindings), Json(after.Package.Document.CharacterResources!.MorphBindings));
        Assert.Equal(Json(before.Package.Document.CharacterResources.SkinVariants), Json(after.Package.Document.CharacterResources.SkinVariants));
        Assert.Equal(before.Package.DecodedCharacterPayload.ToArray(), after.Package.DecodedCharacterPayload.ToArray());
        Assert.Equal(before.Package.SourceFbx.ToArray(), after.Package.SourceFbx.ToArray());
        foreach (var payload in before.Package.CompanionPayloads)
            Assert.Equal(payload.Value.ToArray(), after.Package.CompanionPayloads[payload.Key].ToArray());
    }

    private static ModelsWorkspaceViewModel CreateWorkspace(string path) =>
        new(new PackageDialogs(path), static _ => { }, static _ => Task.CompletedTask, static () => null);
    private static FbxModelAuthoringImportResult CurrentModel(ModelsWorkspaceViewModel workspace) => workspace.CaptureProjectSession().Model!;
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, CustomModelPackageSerializer.CreateSerializerOptions());
    private sealed class PackageDialogs(string path) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowSaveCustomModelPackageDialog(string suggestedName, string? initialPath) => path;
    }
}
