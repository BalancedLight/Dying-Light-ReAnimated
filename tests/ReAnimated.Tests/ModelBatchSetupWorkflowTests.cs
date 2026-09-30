using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelBatchSetupWorkflowTests : IDisposable
{
    private readonly string root = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public async Task AttachingRequiresReviewClearsApprovalAndRoundTripsExactBindings()
    {
        var (vm, preset, package) = await CreateQueue();
        using (vm)
        {
            var before = SHA256.HashData(File.ReadAllBytes(package));
            vm.SelectedItem!.Approved = true;
            await vm.LoadSetupForSelectedAsync(preset);
            Assert.True(vm.CanPreviewSetup);
            await vm.PreviewSetupCommand.ExecuteAsync(null);
            Assert.False(vm.CanAttachSetup);
            await vm.AttachSetupCommand.ExecuteAsync(null);
            Assert.Null(vm.SelectedItem.Item.Setup);
            vm.SetupReviewed = true;
            Assert.True(vm.CanAttachSetup);
            await vm.AttachSetupCommand.ExecuteAsync(null);
            var attached = vm.SelectedItem!.Item.Setup;
            Assert.NotNull(attached); Assert.False(vm.SelectedItem.Approved);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(package)));
            vm.SelectedItem.Approved = true;
            await vm.SaveQueueCommand.ExecuteAsync(null);
            Assert.True(File.Exists(vm.ManifestPath));
            using var restored = new ModelBatchViewModel(new Dialogs());
            await restored.LoadQueueFromPathAsync(vm.ManifestPath);
            Assert.Equal(attached.Preset, restored.SelectedItem!.Item.Setup!.Preset);
            Assert.Equal<Dl1ModelBatchSetupBinding>(attached.Bindings, restored.SelectedItem.Item.Setup.Bindings);
            Assert.True(restored.SelectedItem.Approved);
            await restored.ReviewLinkedSetupCommand.ExecuteAsync(null);
            Assert.NotEmpty(restored.SetupMappings);
            Assert.All(restored.SetupMappings, m => Assert.Equal(attached.Bindings.Single(b => b.Key == m.Node.Key).DestinationEntityId, m.Destination!.EntityId));
        }
    }

    [Fact]
    public async Task ChangedSetupAfterPreviewCannotBeAttached()
    {
        var (vm, preset, _) = await CreateQueue();
        using (vm)
        {
            await vm.LoadSetupForSelectedAsync(preset); await vm.PreviewSetupCommand.ExecuteAsync(null);
            vm.SetupReviewed = true;
            var changed = RigSetupPresetSerializer.Seal(RigSetupWorkflowTests.Setup() with { Name = "New setup revision" });
            File.WriteAllBytes(preset, RigSetupPresetSerializer.Serialize(changed));
            await vm.AttachSetupCommand.ExecuteAsync(null);
            Assert.Null(vm.SelectedItem!.Item.Setup);
            Assert.Contains("changed", vm.Status, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RemovingOrRefreshingSetupRequiresNewPackageApproval()
    {
        var (vm, preset, _) = await CreateQueue();
        using (vm)
        {
            await Attach(); vm.SelectedItem!.Approved = true;
            vm.RemoveSetupCommand.Execute(null);
            Assert.Null(vm.SelectedItem.Item.Setup); Assert.False(vm.SelectedItem.Approved);
            await Attach(); vm.SelectedItem.Approved = true;
            await vm.RefreshSelectedCommand.ExecuteAsync(null);
            Assert.Null(vm.SelectedItem!.Item.Setup); Assert.False(vm.SelectedItem.Approved);
            Assert.Empty(vm.SetupMappings); Assert.False(vm.CanAttachSetup);
            async Task Attach()
            {
                await vm.LoadSetupForSelectedAsync(preset); await vm.PreviewSetupCommand.ExecuteAsync(null);
                vm.SetupReviewed = true; await vm.AttachSetupCommand.ExecuteAsync(null);
                Assert.NotNull(vm.SelectedItem!.Item.Setup);
            }
        }
    }

    private async Task<(ModelBatchViewModel Vm, string Preset, string Package)> CreateQueue()
    {
        string package = Path.Combine(root, "target.dlrmodel"), preset = Path.Combine(root, "setup.dlrsetup"), compiler = Path.Combine(root, "compiler.bin");
        CustomModelPackageSerializer.SaveAtomic(StructuralHelperAuthoringTests.Source().Package, package);
        File.WriteAllBytes(preset, RigSetupPresetSerializer.Serialize(RigSetupWorkflowTests.Setup()));
        File.WriteAllText(compiler, "test compiler identity");
        var vm = new ModelBatchViewModel(new Dialogs()) { CompilerPath = compiler, OutputDirectory = Path.Combine(root, "output") };
        await vm.AddPackageFromPathAsync(package);
        return (vm, preset, package);
    }

    private sealed class Dialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(root);
}
