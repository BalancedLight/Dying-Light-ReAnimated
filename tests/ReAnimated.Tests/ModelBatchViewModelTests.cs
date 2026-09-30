using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelBatchViewModelTests : IDisposable
{
    private readonly string root = RpackTestData.CreateTemporaryDirectory();
    private readonly NullDialogs dialogs = new();

    [Fact]
    public async Task AddingPackageStartsUnapprovedAndSaveLoadRetainsHashAndJobIdentity()
    {
        string package = CreatePackage("queue_item");
        string compiler = CreateFile("compiler.bin", "compiler");
        string output = Path.Combine(root, "output");
        var vm = CreateViewModel();
        await vm.AddPackageFromPathAsync(package);
        ModelBatchQueueItemViewModel row = Assert.Single(vm.Items);
        Assert.False(row.Approved);
        Assert.True(row.IsEditable);
        row.Approved = true;
        vm.CompilerPath = compiler;
        vm.OutputDirectory = output;
        await vm.SaveQueueCommand.ExecuteAsync(null);
        string manifestPath = vm.ManifestPath;
        Dl1ModelBatchManifest saved = Dl1ModelBatchJson.LoadManifest(manifestPath);
        Assert.Equal(row.Sha256, saved.Items[0].Package.Sha256);
        Guid jobId = saved.Id;

        var reopened = CreateViewModel();
        await reopened.LoadQueueFromPathAsync(manifestPath);
        Assert.Equal(jobId, Dl1ModelBatchJson.LoadManifest(manifestPath).Id);
        Assert.Equal(row.Sha256, Assert.Single(reopened.Items).Sha256);
        Assert.True(Assert.Single(reopened.Items).Approved);

        string movedOutput = Path.Combine(root, "moved-output");
        reopened.OutputDirectory = movedOutput;
        await reopened.SaveQueueCommand.ExecuteAsync(null);
        Dl1ModelBatchManifest moved = Dl1ModelBatchJson.LoadManifest(reopened.ManifestPath);
        Assert.Equal(jobId, moved.Id);
        Assert.Equal(movedOutput, reopened.OutputDirectory);
        reopened.Dispose();
        vm.Dispose();
    }

    [Fact]
    public async Task RefreshingSelectedPackageClearsApprovalAndPublishesNewHash()
    {
        string package = CreatePackage("refresh_before");
        var vm = CreateViewModel();
        await vm.AddPackageFromPathAsync(package);
        ModelBatchQueueItemViewModel row = Assert.Single(vm.Items);
        row.Approved = true;
        string oldHash = row.Sha256;
        SavePackage(package, "refresh_after");

        await vm.RefreshSelectedCommand.ExecuteAsync(null);
        ModelBatchQueueItemViewModel refreshed = Assert.Single(vm.Items);
        Assert.False(refreshed.Approved);
        Assert.NotEqual(oldHash, refreshed.Sha256);
        Assert.Contains("approve", vm.Status, StringComparison.OrdinalIgnoreCase);
        vm.Dispose();
    }

    [Fact]
    public async Task RunningLocksEditorsAndCancellationReachesInjectedRunner()
    {
        string package = CreatePackage("cancelled");
        string compiler = CreateFile("compiler.bin", "compiler");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new ModelBatchViewModel(dialogs, null, async (request, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            throw new OperationCanceledException(token);
        });
        await vm.AddPackageFromPathAsync(package);
        Assert.Single(vm.Items).Approved = true;
        vm.CompilerPath = compiler;
        vm.OutputDirectory = Path.Combine(root, "cancel-output");
        Task run = vm.RunCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanEdit);
        Assert.False(Assert.Single(vm.Items).IsEditable);
        string compilerBeforeEdit = vm.CompilerPath;
        vm.CompilerPath = compiler + ".changed";
        Assert.Equal(compilerBeforeEdit, vm.CompilerPath);
        Assert.Single(vm.Items).Approved = false;
        Assert.True(Assert.Single(vm.Items).Approved);
        vm.CancelCommand.Execute(null);
        release.SetResult();
        await run;
        Assert.False(vm.IsBusy);
        Assert.Contains("Cancelled", vm.Status, StringComparison.OrdinalIgnoreCase);
        vm.Dispose();
    }

    [Fact]
    public async Task PartialCompilerResultIsDisplayedAsRuntimeUnverified()
    {
        string package = CreatePackage("partial");
        string compiler = CreateFile("compiler.bin", "compiler");
        var vm = new ModelBatchViewModel(dialogs, null, (request, token) =>
        {
            Guid id = request.Manifest.Items[0].Id;
            var receipt = new Dl1ModelBatchReceipt
            {
                Id = request.Manifest.Id,
                ManifestSha256 = "manifest",
                ToolFingerprint = "tool",
                CreatedUtc = DateTimeOffset.UtcNow,
                Items = [new Dl1ModelBatchItemReceipt { Id = id, State = Dl1ModelBatchItemState.CompilerValidated,
                    Message = "compiler output recorded", UpdatedUtc = DateTimeOffset.UtcNow }],
            };
            return Task.FromResult(new Dl1ModelBatchResult(root, Path.Combine(root, "receipt.json"), receipt));
        });
        await vm.AddPackageFromPathAsync(package);
        Assert.Single(vm.Items).Approved = true;
        vm.CompilerPath = compiler;
        vm.OutputDirectory = Path.Combine(root, "partial-output");
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("Compiled; runtime unverified", Assert.Single(vm.Items).StateLabel);
        Assert.Contains("runtime", vm.Status, StringComparison.OrdinalIgnoreCase);
        vm.Dispose();
    }

    [Fact]
    public async Task DisposedViewModelIgnoresStaleProgressAndResultUpdates()
    {
        string package = CreatePackage("stale_progress");
        string compiler = CreateFile("compiler.bin", "compiler");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<Dl1ModelBatchItemReceipt>? progress = null;
        var vm = new ModelBatchViewModel(dialogs, null, async (request, token) =>
        {
            progress = request.Progress;
            entered.SetResult();
            await release.Task;
            progress?.Report(new Dl1ModelBatchItemReceipt { Id = request.Manifest.Items[0].Id,
                State = Dl1ModelBatchItemState.CompilerValidated, Message = "stale result" });
            var receipt = new Dl1ModelBatchReceipt
            {
                Id = request.Manifest.Id,
                ManifestSha256 = "manifest",
                ToolFingerprint = "tool",
                CreatedUtc = DateTimeOffset.UtcNow,
                Items = [new Dl1ModelBatchItemReceipt { Id = request.Manifest.Items[0].Id,
                    State = Dl1ModelBatchItemState.CompilerValidated, Message = "stale result" }],
            };
            return new Dl1ModelBatchResult(root, Path.Combine(root, "receipt.json"), receipt);
        });
        await vm.AddPackageFromPathAsync(package);
        ModelBatchQueueItemViewModel row = Assert.Single(vm.Items);
        row.Approved = true;
        vm.CompilerPath = compiler;
        vm.OutputDirectory = Path.Combine(root, "stale-output");
        Task run = vm.RunCommand.ExecuteAsync(null);
        await entered.Task;
        vm.Dispose();
        release.SetResult();
        await run;
        Assert.Equal("Queued", row.StateLabel);
        Assert.DoesNotContain("stale result", row.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspaceDisablesConflictingCommandsDuringQueueOperations()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var workspace = new ModelsWorkspaceViewModel(dialogs, _ => { }, _ => Task.CompletedTask, () => null,
            resolveRigTemplate: async (_, token) =>
            {
                entered.SetResult(); await release.Task.WaitAsync(token);
                return Dl1RigTemplateResolution.Failed("control", "No template needed for this coordination check");
            });
        workspace.CommitProjectRestore(new(CompilerRetentionAuthoringTests.Source(), "control.dlrmodel",
            new ReAnimated.Core.Project.ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        // A Conform job must lock the queue, without making queue CanEdit depend on itself.
        Task resolve = workspace.Conformance.ResolveTemplateCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(workspace.ModelBatch.CanEdit);
        release.SetResult(); await resolve;
        Assert.True(workspace.ModelBatch.CanEdit);
        // Inspect changes IsBusy synchronously until its file read completes.
        string package = CreatePackage("workspace_queue");
        workspace.ModelBatch.CompilerPath = CreateFile("compiler.bin", "compiler");
        workspace.ModelBatch.OutputDirectory = Path.Combine(root, "workspace-output");
        await workspace.ModelBatch.AddPackageFromPathAsync(package);
        await workspace.ModelBatch.SaveQueueCommand.ExecuteAsync(null);
        bool sawBusy = false;
        workspace.ModelBatch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ModelBatchViewModel.IsBusy) || !workspace.ModelBatch.IsBusy) return;
            sawBusy = true;
            Assert.True(workspace.IsBusy);
            Assert.False(workspace.BuildCompletePackageCommand.CanExecute(null));
            Assert.False(workspace.OpenPackageCommand.CanExecute(null));
            Assert.True(workspace.CancelCommand.CanExecute(null));
        };
        await workspace.ModelBatch.InspectReceiptCommand.ExecuteAsync(null);
        Assert.True(sawBusy); Assert.False(workspace.IsBusy);
        Assert.Contains("did not complete", workspace.ModelBatch.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidSavedQueueDoesNotReplaceCurrentSelectionOrApproval()
    {
        using var vm = CreateViewModel();
        await vm.AddPackageFromPathAsync(CreatePackage("retained"));
        var original = Assert.Single(vm.Items); original.Approved = true;
        await vm.LoadQueueFromPathAsync(CreateFile("invalid.json", "{}"));
        Assert.Same(original, Assert.Single(vm.Items)); Assert.True(original.Approved);
        Assert.Contains("did not complete", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    private ModelBatchViewModel CreateViewModel() => new(dialogs);

    private string CreatePackage(string name)
    {
        string path = Path.Combine(root, name + ".dlrmodel");
        SavePackage(path, name);
        return path;
    }

    private static void SavePackage(string path, string name)
    {
        var source = CompilerRetentionAuthoringTests.Source();
        CustomModelDocument document = source.Package.Document with
        {
            Name = name,
            BuildSettings = source.Package.Document.BuildSettings with { ResourceName = name },
        };
        CustomModelPackageSerializer.SaveAtomic(source.Package with { Document = document }, path);
    }

    private string CreateFile(string name, string contents)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private sealed class NullDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(root);
}
