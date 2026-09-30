using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Sequential, recoverable package builds over exact approved source snapshots. Never deploys or awards runtime acceptance.</summary>
public static class Dl1ModelBatchRunner
{
    private const string Marker = ".dl-reanimated-batch-owned";
    private const string ReceiptName = "receipt.json";
    private const int MaximumItems = 64;
    private const int MaximumOutputs = 16384;

    public static string ResolveRunDirectory(string outputDirectory, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A batch requires a stable nonempty identity.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        return Path.Combine(Path.GetFullPath(outputDirectory), "batch-" + id.ToString("N"));
    }

    public static Dl1ModelBatchReceipt ReadReceipt(string runDirectory)
    {
        string path = Path.Combine(Path.GetFullPath(runDirectory), ReceiptName);
        RejectLinks(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("Batch receipt exceeds its size limit.");
        var receipt = JsonSerializer.Deserialize<Dl1ModelBatchReceipt>(File.ReadAllBytes(path), Dl1ModelBatchJson.Options)
            ?? throw new InvalidDataException("Batch receipt cannot be null.");
        if (receipt.Format != "dl-reanimated-model-batch-receipt" || receipt.Version != 1 || receipt.Id == Guid.Empty ||
            receipt.Items.IsDefault || receipt.Items.Length > MaximumItems || receipt.Items.Any(i => i is null) || receipt.Items.Select(i => i.Id).Distinct().Count() != receipt.Items.Length ||
            receipt.Items.Any(i => i.Id == Guid.Empty || !Enum.IsDefined(i.State) || i.Attempts < 0 || i.Outputs is null || i.Outputs.Count > MaximumOutputs))
            throw new InvalidDataException("Invalid batch receipt contract.");
        if (!IsHash(receipt.ManifestSha256) || !IsHash(receipt.ToolFingerprint)) throw new InvalidDataException("Batch receipt fingerprints are invalid.");
        foreach (var item in receipt.Items)
        {
            if (item.State == Dl1ModelBatchItemState.CompilerValidated && (item.PackageDirectory is null || item.Outputs.Count == 0))
                throw new InvalidDataException("A completed batch item has no recorded artifact inventory.");
            if (item.PackageDirectory is { } directory)
            {
                if (!directory.StartsWith($"items/{item.Id:N}/attempt-", StringComparison.Ordinal))
                    throw new InvalidDataException("A batch artifact belongs to another item's attempt.");
                _ = Child(runDirectory, directory);
            }
            foreach (var pair in item.Outputs)
            {
                string pathValue = Child(runDirectory, pair.Key);
                if (!IsHash(pair.Value) || item.PackageDirectory is null || !Within(Child(runDirectory, item.PackageDirectory), pathValue))
                    throw new InvalidDataException("A batch artifact hash or ownership path is invalid.");
            }
        }
        return receipt;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static async Task<Dl1ModelBatchResult> RunAsync(Dl1ModelBatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ValidateManifest(request.Manifest);
        if (request.CompilerTimeout <= TimeSpan.Zero || request.CompilerTimeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(request), "Compiler timeout must be positive and at most 30 minutes.");
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = request.Manifest;
        string manifestHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, Dl1ModelBatchJson.Options)));
        string root = ResolveRunDirectory(request.OutputDirectory, manifest.Id);
        RejectLinks(root);
        foreach (var item in manifest.Items)
            if (Within(root, item.Package.Path) || item.Setup is { } setup && Within(root, setup.Preset.Path)) throw new InvalidDataException("Batch source packages and setups cannot reside inside their output job.");
        await VerifyTools(manifest, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(root);
        string markerPath = Path.Combine(root, Marker);
        string identity = manifest.Id.ToString("N") + "\n" + manifestHash;
        RejectLinks(markerPath); RejectLinks(Path.Combine(root, ".lock"));
        if (!File.Exists(markerPath) && Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".lock"))
            throw new InvalidDataException("The output job is not an empty owned batch directory.");
        await using var lease = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(markerPath))
        {
            if (new FileInfo(markerPath).Length > 4096 || await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false) != identity)
                throw new InvalidDataException("The batch manifest changed. Create a new reviewed batch identity; existing results were not replaced.");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".lock"))
                throw new InvalidDataException("The output job is not an empty owned batch directory.");
            await File.WriteAllTextAsync(markerPath, identity, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        string receiptPath = Path.Combine(root, ReceiptName);
        var receipt = File.Exists(receiptPath) ? ReadReceipt(root) : new Dl1ModelBatchReceipt
        {
            Id = manifest.Id, ManifestSha256 = manifestHash, ToolFingerprint = Dl1OfficialModelCompiler.CurrentToolFingerprint,
            CreatedUtc = DateTimeOffset.UtcNow, Items = manifest.Items.Select(i => new Dl1ModelBatchItemReceipt { Id = i.Id }).ToImmutableArray(),
        };
        if (receipt.Id != manifest.Id || receipt.ManifestSha256 != manifestHash || receipt.ToolFingerprint != Dl1OfficialModelCompiler.CurrentToolFingerprint ||
            !receipt.Items.Select(i => i.Id).SequenceEqual(manifest.Items.Select(i => i.Id)))
            throw new InvalidDataException("Batch receipt inputs or exporter contract differ. Start a new reviewed batch.");
        receipt = receipt with { Interrupted = false };
        await Save(receiptPath, receipt).ConfigureAwait(false);
        for (int index = 0; index < manifest.Items.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var item = manifest.Items[index]; var prior = receipt.Items[index];
            try
            {
                if (!item.Approved)
                { await Update(prior with { State = Dl1ModelBatchItemState.NeedsReview, Message = "This package revision has not been approved." }).ConfigureAwait(false); continue; }
                if (!await InputsMatch(item, cancellationToken).ConfigureAwait(false))
                { await Update(prior with { State = Dl1ModelBatchItemState.NeedsReview, Message = "Source package or setup is missing or changed; review its new hash in a new batch." }).ConfigureAwait(false); continue; }
                if (prior.State == Dl1ModelBatchItemState.NeedsReview && prior.PackageDirectory is not null)
                    continue; // A materialized result requiring review is never silently retried.
                if (prior.State == Dl1ModelBatchItemState.CompilerValidated)
                {
                    bool valid = prior.PackageDirectory is not null && prior.Outputs.Count > 0 &&
                        await VerifyOutputs(root, prior, cancellationToken).ConfigureAwait(false);
                    if (!valid) await Update(prior with { State = Dl1ModelBatchItemState.NeedsReview, Message = "Completed output changed or disappeared; it was not overwritten." }).ConfigureAwait(false);
                    continue;
                }
                string attempt = Child(root, $"items/{item.Id:N}/attempt-{Guid.NewGuid():N}");
                Directory.CreateDirectory(attempt);
                prior = prior with { State = Dl1ModelBatchItemState.Running, Attempts = checked(prior.Attempts + 1),
                    Message = "Building the approved source snapshot.", PackageDirectory = null,
                    Outputs = ImmutableDictionary<string, string>.Empty, Warnings = [] };
                await Update(prior).ConfigureAwait(false);
                string snapshot = Path.Combine(attempt, "input.dlrmodel");
                await Snapshot(item.Package, snapshot, CustomModelPackageSerializer.MaximumPackageBytes, cancellationToken).ConfigureAwait(false);
                var package = CustomModelPackageSerializer.Load(snapshot);
                var model = FbxModelAuthoringImporter.ImportPackage(package, cancellationToken);
                ImmutableArray<string> setupWarnings = [];
                if (item.Setup is { } appliedSetup)
                {
                    string setupSnapshot = Path.Combine(attempt, "input.dlrsetup");
                    await Snapshot(appliedSetup.Preset, setupSnapshot, RigSetupPresetSerializer.MaximumBytes, cancellationToken).ConfigureAwait(false);
                    var preset = RigSetupPresetSerializer.Deserialize(await File.ReadAllBytesAsync(setupSnapshot, cancellationToken).ConfigureAwait(false));
                    var mapping = appliedSetup.Bindings.ToDictionary(b => b.Key, b => b.DestinationEntityId, StringComparer.Ordinal);
                    var preview = FbxRigSetupTransfer.Preview(model, preset, mapping, cancellationToken);
                    model = preview.Candidate;
                    setupWarnings = preview.Review.Diagnostics.Select(d => $"{d.Status}: {d.Message} {d.CorrectiveOperation}")
                        .Prepend("Reviewed setup applied to the approved destination package; fitted frames, source clips and native acceptance remain destination-owned.").ToImmutableArray();
                    CustomModelPackageSerializer.SaveAtomic(model.Package, Path.Combine(attempt, "setup-applied.dlrmodel"));
                }
                var settings = model.Package.Document.BuildSettings;
                var buildRequest = new Dl1CustomModelPackageRequest
                {
                    Model = model, ParentOutputDirectory = Path.Combine(attempt, "output"), CompilerExecutablePath = manifest.Compiler.Path,
                    RetailData0PakPath = manifest.RetailData0?.Path, ResourceName = settings.ResourceName, SurfaceName = settings.SurfaceName,
                    AnimationScriptAlias = settings.AnimationScriptAlias, AnimationSelections = model.Package.Document.AnimationClips,
                    CompilerTimeout = request.CompilerTimeout, CompilerWorkingDirectoryRoot = manifest.CompilerWorkingDirectory,
                };
                var output = request.BuildOverride is { } build
                    ? await build(buildRequest, cancellationToken).ConfigureAwait(false)
                    : await Build(buildRequest, cancellationToken).ConfigureAwait(false);
                if (!Within(attempt, output.PackageDirectory)) throw new InvalidDataException("The build result escaped its owned attempt directory.");
                var hashes = await OutputHashes(root, output.PackageDirectory, cancellationToken).ConfigureAwait(false);
                if (hashes.IsEmpty) throw new InvalidDataException("The builder produced no output files.");
                // The package builder owns its local transaction; only this durable
                // receipt admits an attempt as the current approved batch result.
                await VerifyTools(manifest, cancellationToken).ConfigureAwait(false);
                bool sourceCurrent = await InputsMatch(item, cancellationToken).ConfigureAwait(false);
                await Update(prior with { State = sourceCurrent ? Dl1ModelBatchItemState.CompilerValidated : Dl1ModelBatchItemState.NeedsReview,
                    Message = sourceCurrent ? "Official package pipeline completed. Runtime binding and behavior remain unverified." : "Source package or setup changed during the build; the snapshot output requires a new review.",
                    PackageDirectory = Relative(root, output.PackageDirectory), Outputs = hashes, Warnings = output.Warnings.Concat(setupWarnings).Distinct(StringComparer.Ordinal).ToImmutableArray() }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { await Update(prior with { State = Dl1ModelBatchItemState.Cancelled, Message = "Cancelled; completed attempts and source snapshots were retained." }).ConfigureAwait(false); break; }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or FormatException or JsonException or NotSupportedException or TimeoutException)
            { await Update(prior with { State = Dl1ModelBatchItemState.Failed, Message = error.Message }).ConfigureAwait(false); }

            async Task Update(Dl1ModelBatchItemReceipt itemReceipt)
            {
                itemReceipt = itemReceipt with { UpdatedUtc = DateTimeOffset.UtcNow };
                receipt = receipt with { Items = receipt.Items.SetItem(index, itemReceipt) };
                await Save(receiptPath, receipt).ConfigureAwait(false);
                request.Progress?.Report(itemReceipt);
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            receipt = receipt with { Interrupted = true };
            await Save(receiptPath, receipt).ConfigureAwait(false);
        }
        return new(root, receiptPath, receipt);
    }

    private static async Task<Dl1ModelBatchBuildOutput> Build(Dl1CustomModelPackageRequest request, CancellationToken token)
    {
        var result = await Dl1CustomModelPackageBuilder.BuildAsync(request, token).ConfigureAwait(false);
        if (result.CompiledModel.BuildReceipt.State != CustomModelBuildState.CompilerValidated)
            throw new InvalidDataException("The shared package pipeline did not return compiler-validated output.");
        return new(result.PackageDirectory, result.CompiledModel.Warnings.Concat(result.SourceModel.NativeCompanionNotes).Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    public static void ValidateManifest(Dl1ModelBatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Format != "dl-reanimated-model-batch" || manifest.Version != 1 || manifest.Id == Guid.Empty || manifest.Items.IsDefaultOrEmpty ||
            manifest.Items.Length > MaximumItems || manifest.Items.Any(i => i is null) || manifest.Items.Select(i => i.Id).Distinct().Count() != manifest.Items.Length ||
            manifest.AdditionalToolInputs.IsDefault || manifest.AdditionalToolInputs.Length > 64)
            throw new ArgumentException("A batch requires 1–64 distinct reviewed package entries and a versioned identity.");
        ValidateFile(manifest.Compiler); if (manifest.RetailData0 is { } data) ValidateFile(data);
        foreach (var file in manifest.AdditionalToolInputs) ValidateFile(file);
        foreach (var item in manifest.Items)
        {
            if (item is null || item.Id == Guid.Empty || item.Name is null || item.Name.Length > 256) throw new ArgumentException("Invalid batch item identity or label.");
            ValidateFile(item.Package);
            if (item.Setup is { } setup)
            {
                ValidateFile(setup.Preset);
                if (setup.Bindings.IsDefault || setup.Bindings.Length > 4096 || setup.Bindings.Any(b => b is null || string.IsNullOrWhiteSpace(b.Key) || b.Key.Length > 512 || b.DestinationEntityId == Guid.Empty) ||
                    setup.Bindings.Select(b => b.Key).Distinct(StringComparer.Ordinal).Count() != setup.Bindings.Length ||
                    setup.Bindings.Select(b => b.DestinationEntityId).Distinct().Count() != setup.Bindings.Length)
                    throw new ArgumentException("Batch setup bindings require distinct preset keys and destination-owned identities.");
            }
        }
        if (manifest.CompilerWorkingDirectory is { } work && !Path.IsPathFullyQualified(work))
            throw new ArgumentException("The compiler working directory must be resolved before running a batch.");
        if (JsonSerializer.SerializeToUtf8Bytes(manifest, Dl1ModelBatchJson.Options).Length > 1024 * 1024)
            throw new ArgumentException("Batch manifest exceeds 1 MiB; split it into smaller reviewed batches.");
    }

    private static void ValidateFile(Dl1ModelBatchFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!Path.IsPathFullyQualified(file.Path) || file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Batch files require resolved paths and exact SHA-256 values.");
    }

    private static async Task VerifyTools(Dl1ModelBatchManifest manifest, CancellationToken token)
    {
        foreach (var file in manifest.AdditionalToolInputs.Prepend(manifest.Compiler).Concat(manifest.RetailData0 is { } data ? [data] : []))
            if (!await Matches(file, token).ConfigureAwait(false)) throw new InvalidDataException("A declared compiler input changed or is missing: " + file.Path);
    }

    private static async Task<bool> Matches(Dl1ModelBatchFile file, CancellationToken token)
    {
        RejectLinks(file.Path);
        return File.Exists(file.Path) && string.Equals(await Hash(file.Path, token).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> InputsMatch(Dl1ModelBatchItem item, CancellationToken token)
    {
        if (!await Matches(item.Package, token).ConfigureAwait(false)) return false;
        if (item.Setup is not { } setup) return true;
        if (!File.Exists(setup.Preset.Path) || new FileInfo(setup.Preset.Path).Length > RigSetupPresetSerializer.MaximumBytes) return false;
        return await Matches(setup.Preset, token).ConfigureAwait(false);
    }

    private static async Task Snapshot(Dl1ModelBatchFile source, string destination, long maximumBytes, CancellationToken token)
    {
        await using var input = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximumBytes) throw new InvalidDataException("Source input exceeds its size limit.");
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        if (!string.Equals(await Hash(destination, token).ConfigureAwait(false), source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source changed while creating its approved snapshot.");
    }

    private static async Task<ImmutableDictionary<string, string>> OutputHashes(string root, string directory, CancellationToken token)
    {
        var outputs = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(directory)); int entries = 0;
        while (pending.Count > 0)
        {
            string current = pending.Pop(); RejectLinks(current);
            foreach (string path in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); RejectLinks(path);
                if (++entries > MaximumOutputs) throw new InvalidDataException("Batch output inventory exceeds its bound.");
                if (Directory.Exists(path)) pending.Push(path);
                else outputs.Add(Relative(root, path), await Hash(path, token).ConfigureAwait(false));
            }
        }
        return outputs.ToImmutable();
    }

    private static async Task<bool> VerifyOutputs(string root, Dl1ModelBatchItemReceipt item, CancellationToken token)
    {
        if (!item.PackageDirectory!.StartsWith($"items/{item.Id:N}/attempt-", StringComparison.Ordinal))
            throw new InvalidDataException("A batch result belongs to another item's attempt.");
        string package = Child(root, item.PackageDirectory);
        if (!Directory.Exists(package)) return false;
        foreach (var pair in item.Outputs)
        {
            string path = Child(root, pair.Key);
            if (!Within(package, path) || !File.Exists(path) || await Hash(path, token).ConfigureAwait(false) != pair.Value) return false;
        }
        var current = await OutputHashes(root, package, token).ConfigureAwait(false);
        return current.Count == item.Outputs.Count && current.All(p => item.Outputs.TryGetValue(p.Key, out var hash) && hash == p.Value);
    }

    private static async Task<string> Hash(string path, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static async Task Save(string path, Dl1ModelBatchReceipt receipt)
    {
        RejectLinks(path); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Dl1ModelBatchJson.Options);
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }

    private static string Relative(string root, string path) => Within(root, path)
        ? Path.GetRelativePath(root, path).Replace('\\', '/') : throw new InvalidDataException("Batch artifact escaped its owned directory.");
    private static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(p => p is "." or ".." or ""))
            throw new InvalidDataException("Invalid relative batch artifact path.");
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(root, result)) throw new InvalidDataException("Batch artifact escaped its directory.");
        RejectLinks(result); return result;
    }
    private static bool Within(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Batch paths cannot traverse filesystem links: " + current);
        }
    }
}
