using System.Text.Json;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class ModelBatchRunnerTests : IDisposable
{
    private readonly string root = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public async Task MixedItemsPersistIndependentCompilerValidatedAndFailedStates()
    {
        (Dl1ModelBatchManifest manifest, _) = CreateManifest(("validated", true), ("failed", true), ("later", true));
        int calls = 0;
        Dl1ModelBatchResult result = await RunAsync(manifest, async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (request.ResourceName == "failed") throw new InvalidOperationException("deterministic test failure");
            return await WriteOutputAsync(request, "validated-output", token);
        });

        Assert.Equal(3, calls);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, result.Receipt.Items[2].State);
        Assert.Equal(manifest.Id, result.Receipt.Id);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated,
            result.Receipt.Items.Single(item => item.Id == manifest.Items[0].Id).State);
        Assert.Equal(Dl1ModelBatchItemState.Failed,
            result.Receipt.Items.Single(item => item.Id == manifest.Items[1].Id).State);
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.Equal(JsonSerializer.Serialize(result.Receipt, Dl1ModelBatchJson.Options),
            JsonSerializer.Serialize(Dl1ModelBatchRunner.ReadReceipt(result.Directory), Dl1ModelBatchJson.Options));
        Assert.Equal(result.Directory, Dl1ModelBatchRunner.ResolveRunDirectory(root, manifest.Id));
        Assert.StartsWith("batch-", Path.GetFileName(result.Directory), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidResumeSkipsCompletedBuildAndOutputHashesAreRecorded()
    {
        (Dl1ModelBatchManifest manifest, _) = CreateManifest(("stable", true));
        int calls = 0;
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build = async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            return await WriteOutputAsync(request, "stable-output", token);
        };
        Dl1ModelBatchResult first = await RunAsync(manifest, build);
        Assert.Equal(1, calls);
        Dl1ModelBatchResult resumed = await RunAsync(manifest, build);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, Assert.Single(resumed.Receipt.Items).State);
        Assert.NotEmpty(Assert.Single(resumed.Receipt.Items).Outputs);
    }

    [Fact]
    public async Task SourceOrOutputTamperingNeedsReviewWithoutRebuilding()
    {
        (Dl1ModelBatchManifest manifest, string[] packagePaths) = CreateManifest(("tamper", true));
        int calls = 0;
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build = async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            return await WriteOutputAsync(request, "tamper-output", token);
        };
        Dl1ModelBatchResult first = await RunAsync(manifest, build);
        Assert.Equal(1, calls);

        Dl1ModelBatchItemReceipt item = Assert.Single(first.Receipt.Items);
        string output = Path.Combine(first.Directory, item.PackageDirectory!, "artifact.bin");
        File.AppendAllText(output, "tampered");
        Dl1ModelBatchResult outputReview = await RunAsync(manifest, build);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, Assert.Single(outputReview.Receipt.Items).State);

        File.WriteAllText(packagePaths[0], "changed source bytes");
        Dl1ModelBatchResult sourceReview = await RunAsync(manifest, build);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, Assert.Single(sourceReview.Receipt.Items).State);
    }

    [Fact]
    public async Task UnapprovedItemAndChangedManifestAreRejectedWithoutOverwrite()
    {
        (Dl1ModelBatchManifest manifest, _) = CreateManifest(("approval", false));
        int calls = 0;
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build = async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            return await WriteOutputAsync(request, "should-not-build", token);
        };
        Dl1ModelBatchResult unapproved = await RunAsync(manifest, build);
        Assert.Equal(0, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, Assert.Single(unapproved.Receipt.Items).State);
        string receiptBefore = File.ReadAllText(unapproved.ReceiptPath);

        Dl1ModelBatchManifest changed = manifest with
        {
            Items = [manifest.Items[0] with { Name = "changed identity" }],
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(changed, build));
        Assert.Equal(receiptBefore, File.ReadAllText(unapproved.ReceiptPath));
    }

    [Fact]
    public async Task CancellationLeavesLaterItemsPendingAndExactResumeCompletesThem()
    {
        (Dl1ModelBatchManifest manifest, _) = CreateManifest(("first", true), ("second", true));
        using CancellationTokenSource cancellation = new();
        int calls = 0;
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build = async (request, token) =>
        {
            Interlocked.Increment(ref calls);
            if (request.ResourceName == "first") cancellation.Cancel();
            return await WriteOutputAsync(request, request.ResourceName, token);
        };
        Dl1ModelBatchResult cancelled = await RunAsync(manifest, build, cancellation.Token);
        Assert.Equal(Dl1ModelBatchItemState.Cancelled,
            cancelled.Receipt.Items.Single(item => item.Id == manifest.Items[0].Id).State);
        Assert.Equal(Dl1ModelBatchItemState.Pending,
            cancelled.Receipt.Items.Single(item => item.Id == manifest.Items[1].Id).State);
        Assert.Equal(1, calls);

        Dl1ModelBatchResult resumed = await RunAsync(manifest, build);
        Assert.Equal(3, calls);
        Assert.All(resumed.Receipt.Items, item => Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, item.State));
    }

    [Fact]
    public async Task ConcurrentRunForTheSameOutputParentIsRejected()
    {
        (Dl1ModelBatchManifest manifest, _) = CreateManifest(("locked", true));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build = async (request, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return await WriteOutputAsync(request, "locked-output", token);
        };
        Task<Dl1ModelBatchResult> first = RunAsync(manifest, build);
        await entered.Task;
        try { await Assert.ThrowsAsync<IOException>(() => RunAsync(manifest, build)); }
        finally { release.SetResult(); }
        Dl1ModelBatchResult result = await first;
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, Assert.Single(result.Receipt.Items).State);
    }

    [Fact]
    public async Task SourceChangedDuringBuildNeedsReviewEvenAfterRestoringTheOriginal()
    {
        var (manifest, paths) = CreateManifest(("changed_during_build", true));
        byte[] original = File.ReadAllBytes(paths[0]); int calls = 0;
        async Task<Dl1ModelBatchBuildOutput> Build(Dl1CustomModelPackageRequest request, CancellationToken token)
        {
            calls++;
            await File.AppendAllTextAsync(paths[0], "changed", token);
            return await WriteOutputAsync(request, "snapshot-output", token);
        }
        var first = await RunAsync(manifest, Build);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, first.Receipt.Items[0].State);
        Assert.NotEmpty(first.Receipt.Items[0].Outputs);
        File.WriteAllBytes(paths[0], original);
        var resumed = await RunAsync(manifest, Build);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, resumed.Receipt.Items[0].State);
        Assert.False(resumed.Receipt.RuntimeVerified);
    }

    [Fact]
    public async Task CorruptedCompletedOutputStaysReviewRequiredOnFurtherResume()
    {
        var (manifest, _) = CreateManifest(("stable_review", true)); int calls = 0;
        async Task<Dl1ModelBatchBuildOutput> Build(Dl1CustomModelPackageRequest request, CancellationToken token)
        { calls++; return await WriteOutputAsync(request, "artifact", token); }
        var result = await RunAsync(manifest, Build);
        string path = Path.Combine(result.Directory, result.Receipt.Items[0].PackageDirectory!, "artifact.bin");
        File.AppendAllText(path, "changed");
        await RunAsync(manifest, Build);
        var next = await RunAsync(manifest, Build);
        Assert.Equal(1, calls);
        Assert.Equal(Dl1ModelBatchItemState.NeedsReview, next.Receipt.Items[0].State);
    }

    [Fact]
    public async Task RunningReceiptRecoversOnlyAfterThePriorLeaseHasEnded()
    {
        var (manifest, _) = CreateManifest(("interrupted", true)); int calls = 0;
        async Task<Dl1ModelBatchBuildOutput> Build(Dl1CustomModelPackageRequest request, CancellationToken token)
        { calls++; return await WriteOutputAsync(request, "artifact", token); }
        var first = await RunAsync(manifest, Build);
        var interrupted = first.Receipt with { Items = first.Receipt.Items.SetItem(0, first.Receipt.Items[0] with
            { State = Dl1ModelBatchItemState.Running, Outputs = ImmutableDictionary<string,string>.Empty, PackageDirectory = null }) };
        File.WriteAllBytes(first.ReceiptPath, JsonSerializer.SerializeToUtf8Bytes(interrupted, Dl1ModelBatchJson.Options));
        var next = await RunAsync(manifest, Build);
        Assert.Equal(2, calls);
        Assert.Equal(2, next.Receipt.Items[0].Attempts);
        Assert.Equal(Dl1ModelBatchItemState.CompilerValidated, next.Receipt.Items[0].State);
        Assert.True(Directory.Exists(Path.Combine(first.Directory, first.Receipt.Items[0].PackageDirectory!)));
    }

    [Fact]
    public async Task EscapingRecordedArtifactPathsFailBeforeResumeCanBuild()
    {
        var (manifest, _) = CreateManifest(("path_guard", true)); int calls = 0;
        async Task<Dl1ModelBatchBuildOutput> Build(Dl1CustomModelPackageRequest request, CancellationToken token)
        { calls++; return await WriteOutputAsync(request, "artifact", token); }
        var first = await RunAsync(manifest, Build);
        var bad = first.Receipt with { Items = first.Receipt.Items.SetItem(0, first.Receipt.Items[0] with
            { Outputs = ImmutableDictionary<string,string>.Empty.Add("../outside.bin", new string('a',64)) }) };
        File.WriteAllBytes(first.ReceiptPath, JsonSerializer.SerializeToUtf8Bytes(bad, Dl1ModelBatchJson.Options));
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(manifest, Build));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ManifestResolvesRelativePathsAndRejectsUnknownOrMissingDeclarations()
    {
        var (manifest, _) = CreateManifest(("relative", true));
        string path = Path.Combine(root, "batch.json");
        var relative = manifest with { Compiler = manifest.Compiler with { Path = "compiler.bin" },
            Items = [manifest.Items[0] with { Package = manifest.Items[0].Package with { Path = "relative.dlrmodel" } }] };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(relative, Dl1ModelBatchJson.Options));
        var reopened = Dl1ModelBatchJson.LoadManifest(path);
        Assert.Equal(manifest.Compiler.Path, reopened.Compiler.Path);
        Assert.Equal(manifest.Items[0].Package.Path, reopened.Items[0].Package.Path);
        string json = File.ReadAllText(path);
        File.WriteAllText(path, json.Insert(json.IndexOf('{')+1, "\"unknownOption\":true,"));
        Assert.Throws<JsonException>(() => Dl1ModelBatchJson.LoadManifest(path));
    }

    private async Task<Dl1ModelBatchResult> RunAsync(
        Dl1ModelBatchManifest manifest,
        Func<Dl1CustomModelPackageRequest, CancellationToken, Task<Dl1ModelBatchBuildOutput>> build,
        CancellationToken cancellationToken = default) =>
        await Dl1ModelBatchRunner.RunAsync(new Dl1ModelBatchRequest
        {
            Manifest = manifest,
            OutputDirectory = root,
            BuildOverride = build,
        }, cancellationToken);

    private (Dl1ModelBatchManifest Manifest, string[] PackagePaths) CreateManifest(
        params (string Name, bool Approved)[] entries)
    {
        string compiler = Path.Combine(root, "compiler.bin");
        File.WriteAllText(compiler, "deterministic compiler input");
        var items = ImmutableArray.CreateBuilder<Dl1ModelBatchItem>(entries.Length);
        string[] packagePaths = new string[entries.Length];
        foreach ((string name, bool approved) in entries)
        {
            FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
            CustomModelDocument document = source.Package.Document with
            {
                Name = name,
                BuildSettings = source.Package.Document.BuildSettings with { ResourceName = name },
            };
            source = source with { Package = source.Package with { Document = document } };
            string packagePath = Path.Combine(root, name + ".dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(source.Package, packagePath);
            packagePaths[items.Count] = packagePath;
            items.Add(new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                Approved = approved,
                Package = new Dl1ModelBatchFile(packagePath, Hash(packagePath)),
            });
        }
        return (new Dl1ModelBatchManifest
        {
            Id = Guid.NewGuid(),
            Compiler = new Dl1ModelBatchFile(compiler, Hash(compiler)),
            Items = items.MoveToImmutable(),
        }, packagePaths);
    }

    private static async Task<Dl1ModelBatchBuildOutput> WriteOutputAsync(
        Dl1CustomModelPackageRequest request, string content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string packageDirectory = Path.Combine(request.ParentOutputDirectory,
            "attempt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "artifact.bin"), content, token);
        return new Dl1ModelBatchBuildOutput(packageDirectory, ["deterministic test output"]);
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(root);
}
