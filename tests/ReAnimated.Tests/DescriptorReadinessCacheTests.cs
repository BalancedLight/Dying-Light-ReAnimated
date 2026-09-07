using System.Reflection;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class DescriptorReadinessCacheTests
{
    [Fact]
    public async Task VariantReadinessImportsEachVerifiedAssetOnceAndNewHashImportsAgain()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = new Dl1AssetWorkspace(
                Path.Combine(root, "assets.sqlite3"),
                Path.Combine(root, "cache"));
            await using var vm = new MainWindowViewModel(
                new JsonWorkspaceStateStore(Path.Combine(root, "state.json")),
                new NoDialogs(),
                assets);
            vm.ProjectPath = Path.Combine(root, "workspace.dlraproj");
            var imported = FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic.fbx");
            var (asset, model) = Write(root, imported.Package, "original.dlrmodel");
            for (int variant = 0; variant < 24; variant++)
            {
                Assert.True(Validate(vm, model, asset).IsValid);
            }
            Assert.Equal(1, Imports(vm));
            var changed = imported.Package with { Document = imported.Package.Document with { Name = "Renamed source" } };
            var (newAsset, _) = Write(root, changed, "changed.dlrmodel");
            newAsset = newAsset with { Id = asset.Id };
            Assert.True(Validate(vm, model, newAsset).IsValid);
            Assert.Equal(2, Imports(vm));
            Assert.True(Validate(vm, model with { Id = Guid.NewGuid() }, newAsset).IsValid);
            Assert.Equal(3, Imports(vm));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CachedReadinessNeverHidesMissingOrChangedFileBytesAndClearsOnReload()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var assets = new Dl1AssetWorkspace(
                Path.Combine(root, "assets.sqlite3"),
                Path.Combine(root, "cache"));
            await using var vm = new MainWindowViewModel(
                new JsonWorkspaceStateStore(Path.Combine(root, "state.json")),
                new NoDialogs(),
                assets);
            vm.ProjectPath = Path.Combine(root, "workspace.dlraproj");
            var imported = FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic.fbx");
            var (asset, model) = Write(root, imported.Package, "model.dlrmodel");
            string path = Path.Combine(root, asset.RelativePath);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(Validate(vm, model, asset).IsValid);
            Assert.Equal(1, Imports(vm));
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.False(Validate(vm, model, asset).IsValid);
            Assert.Equal(1, Imports(vm));
            File.Delete(path);
            Assert.False(Validate(vm, model, asset).IsValid);
            Assert.Equal(1, Imports(vm));
            File.WriteAllBytes(path, bytes);
            Assert.True(Validate(vm, model, asset).IsValid);
            Assert.Equal(1, Imports(vm));
            typeof(MainWindowViewModel).GetMethod("SetProject", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [vm.CurrentProject, true, true, true]);
            Assert.True(Validate(vm, model, asset).IsValid);
            Assert.Equal(2, Imports(vm));
            File.Copy(path, Path.Combine(root, "moved.dlrmodel"));
            Assert.True(Validate(vm, model, asset with { RelativePath = "moved.dlrmodel" }).IsValid);
            Assert.Equal(3, Imports(vm));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    private static (ProjectAssetReference, ProjectModelEntry) Write(
        string root,
        CustomModelPackage package,
        string fileName)
    {
        byte[] bytes = CustomModelPackageSerializer.Serialize(package).ToArray();
        File.WriteAllBytes(Path.Combine(root, fileName), bytes);
        var asset = new ProjectAssetReference
        {
            Id = Guid.NewGuid(),
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = fileName,
            ResourceId = $"custom-model:{package.Document.ModelId:N}:generic",
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
        var model = new ProjectModelEntry
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            Name = "Generic model",
            RigSignature = package.Document.RigSignature,
        };
        return (asset, model);
    }

    private static DescriptorInventoryValidation Validate(
        MainWindowViewModel vm,
        ProjectModelEntry model,
        ProjectAssetReference asset) =>
        (DescriptorInventoryValidation)typeof(MainWindowViewModel)
            .GetMethod("ValidateProjectModelDl1DescriptorInventory",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [model, asset])!;

    private static int Imports(MainWindowViewModel vm) =>
        (int)typeof(MainWindowViewModel)
            .GetField("_descriptorReadinessImportCount",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm)!;

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public void ShowOperationFailure(string title, string summary, string details) => throw new InvalidOperationException(summary);
    }
}
