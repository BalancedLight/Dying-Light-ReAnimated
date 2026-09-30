using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelBatchSetupTests : IDisposable
{
    private readonly string root = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public async Task ApprovedSetupIsAppliedToCandidateAndLeavesInputsUntouched()
    {
        RigSetupPreset setup = RigSetupWorkflowTests.Setup();
        FbxModelAuthoringImportResult target = StructuralHelperAuthoringTests.Source();
        string packagePath = SavePackage(target, "setup-target");
        string presetPath = SavePreset(setup, "portable-setup");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        byte[] packageBefore = File.ReadAllBytes(packagePath);
        byte[] presetBefore = File.ReadAllBytes(presetPath);
        var captured = new List<FbxModelAuthoringImportResult>();
        Dl1ModelBatchManifest manifest = Manifest(
            Item("setup-target", packagePath, setup, presetPath, target), compilerPath);

        Dl1ModelBatchResult result = await RunAsync(manifest, async (request, token) =>
        {
            captured.Add(request.Model);
            Assert.Equal(setup.Profile.Identity, request.Model.Package.Document.RiggingSession!.Recipe.Profile);
            Assert.Contains(request.Model.Package.Document.RiggingSession.Recipe.Assignments,
                assignment => assignment.RoleId == "marker");
            Assert.Equal(JsonSerializer.Serialize(target.Surfaces), JsonSerializer.Serialize(request.Model.Surfaces));
            return await WriteOutputAsync(request, "setup-output", token);
        });

        Assert.Single(captured);
        Dl1ModelBatchItemReceipt receipt = Assert.Single(result.Receipt.Items);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, receipt.State);
        Assert.Contains(receipt.Warnings, warning => warning.Contains("setup applied", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(packageBefore, File.ReadAllBytes(packagePath));
        Assert.Equal(presetBefore, File.ReadAllBytes(presetPath));
        string attempt = Path.Combine(result.Directory, receipt.PackageDirectory!);
        string attemptRoot = Directory.GetParent(Directory.GetParent(attempt)!.FullName)!.FullName;
        string setupApplied = Path.Combine(attemptRoot, "setup-applied.dlrmodel");
        Assert.True(File.Exists(setupApplied));
    }

    [Fact]
    public void LegacyItemWithoutSetupOmitsPropertyFromCanonicalJson()
    {
        string packagePath = SavePackage(StructuralHelperAuthoringTests.Source(), "legacy-item");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        Dl1ModelBatchManifest manifest = Manifest(Item("legacy-item", packagePath, null, null, null), compilerPath);
        string json = JsonSerializer.Serialize(manifest, Dl1ModelBatchJson.Options);
        Assert.DoesNotContain("\"setup\"", json, StringComparison.OrdinalIgnoreCase);
        Dl1ModelBatchManifest reopened = JsonSerializer.Deserialize<Dl1ModelBatchManifest>(json, Dl1ModelBatchJson.Options)!;
        Assert.Null(reopened.Items[0].Setup);
        Assert.Equal(json, JsonSerializer.Serialize(reopened, Dl1ModelBatchJson.Options));
    }

    [Fact]
    public async Task ChangedPresetBeforeRunNeedsReviewWithoutInvokingBuild()
    {
        RigSetupPreset setup = RigSetupWorkflowTests.Setup();
        string packagePath = SavePackage(StructuralHelperAuthoringTests.Source(), "changed-before");
        string presetPath = SavePreset(setup, "changed-before-setup");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        Dl1ModelBatchManifest manifest = Manifest(Item("changed-before", packagePath, setup, presetPath,
            StructuralHelperAuthoringTests.Source()), compilerPath);
        File.WriteAllBytes(presetPath, RigSetupPresetSerializer.Serialize(
            RigSetupPresetSerializer.Seal(setup with { Name = "changed setup" })));
        int calls = 0;
        Dl1ModelBatchResult result = await RunAsync(manifest, async (request, token) =>
        {
            calls++;
            return await WriteOutputAsync(request, "should-not-run", token);
        });
        Assert.Equal(0, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, Assert.Single(result.Receipt.Items).State);
    }

    [Fact]
    public async Task ChangedPresetDuringBuildIsNeedsReviewAndOutputIsNotAdmitted()
    {
        RigSetupPreset setup = RigSetupWorkflowTests.Setup();
        string packagePath = SavePackage(StructuralHelperAuthoringTests.Source(), "changed-during");
        string presetPath = SavePreset(setup, "changed-during-setup");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        Dl1ModelBatchManifest manifest = Manifest(Item("changed-during", packagePath, setup, presetPath,
            StructuralHelperAuthoringTests.Source()), compilerPath);
        int calls = 0;
        Dl1ModelBatchResult result = await RunAsync(manifest, async (request, token) =>
        {
            calls++;
            File.WriteAllBytes(presetPath, RigSetupPresetSerializer.Serialize(
                RigSetupPresetSerializer.Seal(setup with { Name = "changed during build" })));
            return await WriteOutputAsync(request, "not-admitted", token);
        });
        Dl1ModelBatchItemReceipt receipt = Assert.Single(result.Receipt.Items);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, receipt.State);
        Assert.NotEmpty(receipt.Outputs);
    }

    [Fact]
    public async Task MutatedPresetBeforeResumeDoesNotRebuildCompletedItem()
    {
        RigSetupPreset setup = RigSetupWorkflowTests.Setup();
        string packagePath = SavePackage(StructuralHelperAuthoringTests.Source(), "resume-mutation");
        string presetPath = SavePreset(setup, "resume-mutation-setup");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        Dl1ModelBatchManifest manifest = Manifest(Item("resume-mutation", packagePath, setup, presetPath,
            StructuralHelperAuthoringTests.Source()), compilerPath);
        int calls = 0;
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build = async (request, token) =>
        {
            calls++;
            return await WriteOutputAsync(request, "resume-output", token);
        };
        Dl1ModelBatchResult first = await RunAsync(manifest, build);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, Assert.Single(first.Receipt.Items).State);
        File.WriteAllBytes(presetPath, RigSetupPresetSerializer.Serialize(
            RigSetupPresetSerializer.Seal(setup with { Name = "mutated before resume" })));
        Dl1ModelBatchResult resumed = await RunAsync(manifest, build);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, Assert.Single(resumed.Receipt.Items).State);
    }

    [Fact]
    public async Task InvalidSetupMappingFailsWhileLaterValidItemContinues()
    {
        RigSetupPreset setup = RigSetupWorkflowTests.Setup();
        FbxModelAuthoringImportResult target = StructuralHelperAuthoringTests.Source();
        string validPackage = SavePackage(target, "valid-item");
        string invalidPackage = SavePackage(StructuralHelperAuthoringTests.Source(), "invalid-item");
        string presetPath = SavePreset(setup, "shared-setup");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        Dl1ModelBatchItem invalid = Item("invalid-item", invalidPackage, setup, presetPath, target) with
        {
            Setup = new Dl1ModelBatchSetup
            {
                Preset = new Dl1ModelBatchFile(presetPath, Hash(presetPath)),
                Bindings = setup.Nodes.Select(node => new Dl1ModelBatchSetupBinding(node.Key, Guid.NewGuid())).ToImmutableArray(),
            },
        };
        Dl1ModelBatchItem valid = Item("valid-item", validPackage, setup, presetPath, target);
        Dl1ModelBatchManifest manifest = Manifest([invalid, valid], compilerPath);
        int calls = 0;
        Dl1ModelBatchResult result = await RunAsync(manifest, async (request, token) =>
        {
            calls++;
            return await WriteOutputAsync(request, request.ResourceName, token);
        });
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.Failed, result.Receipt.Items[0].State);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, result.Receipt.Items[1].State);
    }

    [Fact]
    public async Task CancellationRetainsSetupSnapshotAndResumeCanComplete()
    {
        RigSetupPreset setup = RigSetupWorkflowTests.Setup();
        string packagePath = SavePackage(StructuralHelperAuthoringTests.Source(), "cancel-setup");
        string presetPath = SavePreset(setup, "cancel-setup-preset");
        string compilerPath = WriteFile("compiler.bin", "compiler");
        Dl1ModelBatchManifest manifest = Manifest(Item("cancel-setup", packagePath, setup, presetPath,
            StructuralHelperAuthoringTests.Source()), compilerPath);
        using CancellationTokenSource cancellation = new();
        int calls = 0;
        Dl1ModelBatchResult cancelled = await RunAsync(manifest, async (request, token) =>
        {
            calls++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return await WriteOutputAsync(request, "unreachable", token);
        }, cancellation.Token);
        Dl1ModelBatchItemReceipt cancelledItem = Assert.Single(cancelled.Receipt.Items);
        Assert.Equal(Dl1ModelBatchItemState.Cancelled, cancelledItem.State);
        Assert.NotEmpty(Directory.GetFiles(cancelled.Directory, "setup-applied.dlrmodel", SearchOption.AllDirectories));

        Dl1ModelBatchResult resumed = await RunAsync(manifest, async (request, token) =>
        {
            calls++;
            return await WriteOutputAsync(request, "resumed-output", token);
        });
        Assert.Equal(2, calls);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, Assert.Single(resumed.Receipt.Items).State);
    }

    [Fact]
    public async Task CompilerDataRejectionDoesNotAbortLaterItems()
    {
        string compiler = WriteFile("compiler.bin", "compiler");
        string package = SavePackage(StructuralHelperAuthoringTests.Source(), "shared-control");
        var first = Item("first", package, null, null);
        var second = Item("second", package, null, null);
        int calls = 0;
        var result = await RunAsync(Manifest([first, second], compiler), async (request, token) =>
        {
            if (++calls == 1) throw new InvalidDataException("Compiler rejected this model's data.");
            return await WriteOutputAsync(request, "valid-result", token);
        });
        Assert.Equal(2, calls);
        Assert.Equal(Dl1ModelBatchItemState.Failed, result.Receipt.Items[0].State);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, result.Receipt.Items[1].State);
    }

    private async Task<Dl1ModelBatchResult> RunAsync(
        Dl1ModelBatchManifest manifest,
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build,
        CancellationToken cancellationToken = default) =>
        await Dl1ModelBatchRunner.RunAsync(new() { Manifest = manifest, OutputDirectory = root, BuildOverride = build }, cancellationToken);

    private static Dl1ModelBatchManifest Manifest(Dl1ModelBatchItem item, string compiler) =>
        Manifest([item], compiler);

    private static Dl1ModelBatchManifest Manifest(ImmutableArray<Dl1ModelBatchItem> items, string compiler) => new()
    {
        Id = Guid.NewGuid(), Compiler = new Dl1ModelBatchFile(compiler, Hash(compiler)), Items = items,
    };

    private static Dl1ModelBatchItem Item(string name, string packagePath, RigSetupPreset? setup,
        string? presetPath, FbxModelAuthoringImportResult? target = null)
    {
        _ = target;
        FbxModelAuthoringImportResult? exactTarget = setup is null ? null :
            FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(packagePath));
        Dl1ModelBatchSetup? batchSetup = setup is null ? null : new()
        {
            Preset = new Dl1ModelBatchFile(presetPath!, Hash(presetPath!)),
            Bindings = setup.Nodes.Select(node => new Dl1ModelBatchSetupBinding(node.Key,
                exactTarget!.Package.Document.RiggingSession!.Recipe.Entities
                    .Single(entity => entity.NativeName == node.NativeName).EntityId)).ToImmutableArray(),
        };
        return new()
        {
            Id = Guid.NewGuid(), Name = name, Approved = true,
            Package = new Dl1ModelBatchFile(packagePath, Hash(packagePath)), Setup = batchSetup,
        };
    }

    private string SavePackage(FbxModelAuthoringImportResult model, string name)
    {
        string path = Path.Combine(root, name + ".dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(model.Package, path);
        return path;
    }

    private string SavePreset(RigSetupPreset setup, string name)
    {
        string path = Path.Combine(root, name + ".dlrsetup");
        File.WriteAllBytes(path, RigSetupPresetSerializer.Serialize(setup));
        return path;
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<Dl1ModelBatchBuildOutput> WriteOutputAsync(
        Dl1CustomModelPackageRequest request, string content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.Combine(request.ParentOutputDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "artifact.bin"), content, token);
        return new Dl1ModelBatchBuildOutput(directory, ["deterministic setup test output"]);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(root);
}
