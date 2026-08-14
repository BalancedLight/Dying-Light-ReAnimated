using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReAnimated.Codecs.Models;

public enum Dl1DeveloperToolsBatchState
{
    Prepared,
    Committing,
    Committed,
    RollingBack,
    RolledBack,
    RollbackRequired,
}

public sealed record Dl1DeveloperToolsBatchRequest
{
    public required ImmutableArray<Dl1DeveloperToolsDeploymentRequest> Deployments { get; init; }

    internal Func<Dl1DeveloperToolsDeploymentRequest, CancellationToken, Task<Dl1DeveloperToolsDeploymentPlan>>?
        PreflightOverride { get; init; }

    internal Func<Dl1DeveloperToolsDeploymentRequest, CancellationToken, Task<Dl1DeveloperToolsDeploymentResult>>?
        DeploymentOverride { get; init; }

    internal Func<string, string, CancellationToken, Task>?
        RollbackOverride { get; init; }
}

public sealed record Dl1DeveloperToolsBatchConflict(
    string? ModelResourceName,
    string Message);

public sealed record Dl1DeveloperToolsBatchPreflight(
    string ProjectRoot,
    ImmutableArray<Dl1DeveloperToolsDeploymentPlan> Plans,
    ImmutableArray<Dl1DeveloperToolsBatchConflict> Conflicts)
{
    [JsonIgnore]
    public bool CanDeploy => Plans.All(static plan => plan.CanDeploy) && Conflicts.IsEmpty;
}

public sealed record Dl1DeveloperToolsBatchReceiptItem(
    string CharacterId,
    string ModelResourceName,
    string AnimationLibraryName,
    string DeploymentId,
    string ReceiptRelativePath);

public sealed record Dl1DeveloperToolsBatchReceipt
{
    public string Format { get; init; } = "dl-reanimated-developer-tools-batch";

    public int SchemaVersion { get; init; } = 1;

    public required string BatchId { get; init; }

    public required Dl1DeveloperToolsBatchState State { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    public DateTimeOffset? RolledBackUtc { get; init; }

    public ImmutableArray<Dl1DeveloperToolsBatchReceiptItem> Deployments { get; init; } = [];

    public ImmutableArray<string> ValidationResults { get; init; } = [];

    public ImmutableArray<string> Errors { get; init; } = [];

    [JsonIgnore]
    public string ProjectRoot { get; init; } = string.Empty;

    [JsonIgnore]
    public string ReceiptPath { get; init; } = string.Empty;
}

public sealed record Dl1DeveloperToolsBatchResult(
    Dl1DeveloperToolsBatchPreflight Preflight,
    Dl1DeveloperToolsBatchReceipt Receipt,
    string ReceiptPath,
    ImmutableArray<Dl1DeveloperToolsDeploymentResult> Deployments);

public sealed class Dl1DeveloperToolsBatchConflictException : InvalidOperationException
{
    public Dl1DeveloperToolsBatchConflictException(Dl1DeveloperToolsBatchPreflight preflight)
        : base(BuildMessage(preflight))
    {
        Preflight = preflight;
    }

    public Dl1DeveloperToolsBatchPreflight Preflight { get; }

    private static string BuildMessage(Dl1DeveloperToolsBatchPreflight preflight)
    {
        IEnumerable<string> messages = preflight.Conflicts.Select(static conflict => conflict.Message)
            .Concat(preflight.Plans.SelectMany(static plan => plan.Conflicts.Select(static conflict => conflict.Message)))
            .Take(6);
        return "Developer Tools batch preflight is blocked: " + string.Join("; ", messages);
    }
}

public sealed class Dl1DeveloperToolsBatchTransactionException : InvalidOperationException
{
    public Dl1DeveloperToolsBatchTransactionException(
        string message,
        Dl1DeveloperToolsBatchReceipt receipt,
        string receiptPath,
        Exception innerException)
        : base(message, innerException)
    {
        Receipt = receipt;
        ReceiptPath = receiptPath;
    }

    public Dl1DeveloperToolsBatchReceipt Receipt { get; }

    public string ReceiptPath { get; }
}

public static partial class Dl1DeveloperToolsProjectDeployer
{
    private const string BatchReceiptFormat = "dl-reanimated-developer-tools-batch";
    private const int MaximumBatchDeploymentCount = 128;

    public static Dl1DeveloperToolsBatchReceipt?
        LoadLatestActiveBatchReceipt(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string validatedRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(validatedRoot))
        {
            return null;
        }

        RejectReparsePoint(validatedRoot);
        string directory = ResolveProjectPath(
            validatedRoot,
            ".dl-reanimated/batches");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        Dl1DeveloperToolsBatchReceipt? latest = null;
        foreach (string path in Directory.EnumerateFiles(
                     directory,
                     "*.json",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                RejectReparsePoint(path);
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > 16 * 1024 * 1024)
                {
                    continue;
                }

                Dl1DeveloperToolsBatchReceipt? candidate =
                    JsonSerializer.Deserialize<
                        Dl1DeveloperToolsBatchReceipt>(
                        File.ReadAllBytes(path),
                        JsonOptions);
                if (candidate is null ||
                    candidate.Format != BatchReceiptFormat ||
                    candidate.SchemaVersion != 1 ||
                    candidate.State !=
                        Dl1DeveloperToolsBatchState.Committed ||
                    candidate.CompletedUtc is null ||
                    candidate.Deployments.IsDefaultOrEmpty ||
                    candidate.Deployments.Length >
                        MaximumBatchDeploymentCount ||
                    !string.Equals(
                        Path.GetFileNameWithoutExtension(path),
                        candidate.BatchId,
                        StringComparison.OrdinalIgnoreCase) ||
                    latest?.CompletedUtc >= candidate.CompletedUtc)
                {
                    continue;
                }

                foreach (Dl1DeveloperToolsBatchReceiptItem item in
                         candidate.Deployments)
                {
                    string relative = NormalizeRelativePath(
                        item.ReceiptRelativePath);
                    if (!relative.StartsWith(
                            ".dl-reanimated/deployments/",
                            StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(
                            Path.GetFileNameWithoutExtension(relative),
                            item.DeploymentId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Developer Tools batch contains an invalid child receipt reference.");
                    }

                    _ = ResolveProjectPath(validatedRoot, relative);
                }

                latest = candidate with
                {
                    ProjectRoot = validatedRoot,
                    ReceiptPath = path,
                };
            }
            catch (Exception exception) when (
                exception is JsonException or
                IOException or
                UnauthorizedAccessException or
                InvalidDataException)
            {
                // Invalid receipts never enable a rollback action.
            }
        }

        return latest;
    }

    public static async Task<Dl1DeveloperToolsBatchPreflight> PreflightBatchAsync(
        Dl1DeveloperToolsBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        BatchValidation validation = ValidateBatchRequest(request);
        return await PreflightBatchCoreAsync(request, validation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Preflights every selected model and keeps the project operation lock for
    /// the complete commit. If any child commit fails, already-committed child
    /// receipts are rolled back in reverse order before the lock is released.
    /// </summary>
    public static async Task<Dl1DeveloperToolsBatchResult> DeployBatchAsync(
        Dl1DeveloperToolsBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        BatchValidation validation = ValidateBatchRequest(request);
        await using ProjectOperationLock projectLock = await AcquireProjectOperationLockAsync(
            validation.ProjectRoot,
            cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedTransactionsLockedAsync(
            validation.ProjectRoot,
            cancellationToken).ConfigureAwait(false);

        Dl1DeveloperToolsBatchPreflight preflight = await PreflightBatchCoreAsync(
            request,
            validation,
            cancellationToken).ConfigureAwait(false);
        if (!preflight.CanDeploy)
        {
            throw new Dl1DeveloperToolsBatchConflictException(preflight);
        }

        string batchId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        string receiptPath = ResolveProjectPath(
            validation.ProjectRoot,
            $".dl-reanimated/batches/{batchId}.json");
        Dl1DeveloperToolsBatchReceipt receipt = new()
        {
            BatchId = batchId,
            State = Dl1DeveloperToolsBatchState.Prepared,
            CreatedUtc = DateTimeOffset.UtcNow,
            ProjectRoot = validation.ProjectRoot,
            ReceiptPath = receiptPath,
            ValidationResults =
            [
                $"Preflight passed for {request.Deployments.Length:N0} selected model(s).",
                "All selected model/resource/script identities are unique within the batch.",
                "All commits use one Developer Tools project operation lock.",
            ],
        };
        await WriteBatchReceiptAsync(receipt, receiptPath, cancellationToken).ConfigureAwait(false);
        receipt = receipt with { State = Dl1DeveloperToolsBatchState.Committing };
        await WriteBatchReceiptAsync(receipt, receiptPath, cancellationToken).ConfigureAwait(false);

        var deployed = ImmutableArray.CreateBuilder<Dl1DeveloperToolsDeploymentResult>(
            request.Deployments.Length);
        try
        {
            for (int index = 0; index < request.Deployments.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dl1DeveloperToolsDeploymentRequest childRequest = request.Deployments[index];
                Dl1DeveloperToolsDeploymentResult result = request.DeploymentOverride is null
                    ? await DeployLockedAsync(
                        childRequest,
                        validation.ValidatedRequests[index],
                        cancellationToken).ConfigureAwait(false)
                    : await request.DeploymentOverride(childRequest, cancellationToken).ConfigureAwait(false);
                deployed.Add(result);
                receipt = receipt with
                {
                    Deployments = receipt.Deployments.Add(ToBatchReceiptItem(
                        validation.ProjectRoot,
                        result)),
                };
                await WriteBatchReceiptAsync(receipt, receiptPath, cancellationToken).ConfigureAwait(false);
            }

            receipt = receipt with
            {
                State = Dl1DeveloperToolsBatchState.Committed,
                CompletedUtc = DateTimeOffset.UtcNow,
            };
            await WriteBatchReceiptAsync(receipt, receiptPath, cancellationToken).ConfigureAwait(false);
            return new Dl1DeveloperToolsBatchResult(
                preflight,
                receipt,
                receiptPath,
                deployed.MoveToImmutable());
        }
        catch (Exception deploymentException)
        {
            receipt = receipt with
            {
                State = Dl1DeveloperToolsBatchState.RollingBack,
                Errors = [$"Commit failed: {deploymentException.Message}"],
            };
            string? receiptWriteError = await TryWriteBatchReceiptAsync(
                receipt,
                receiptPath).ConfigureAwait(false);
            if (receiptWriteError is not null)
            {
                receipt = receipt with
                {
                    Errors = receipt.Errors.Add(receiptWriteError),
                };
            }

            var rollbackErrorsBuilder = ImmutableArray.CreateBuilder<string>();
            try
            {
                await RecoverInterruptedTransactionsLockedAsync(
                    validation.ProjectRoot,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception recoveryException)
            {
                rollbackErrorsBuilder.Add(
                    $"Interrupted child transaction recovery failed: {recoveryException.Message}");
            }

            rollbackErrorsBuilder.AddRange(await RollBackBatchChildrenLockedAsync(
                request,
                validation.ProjectRoot,
                receipt.Deployments,
                CancellationToken.None).ConfigureAwait(false));
            ImmutableArray<string> rollbackErrors = rollbackErrorsBuilder.ToImmutable();
            receipt = receipt with
            {
                State = rollbackErrors.IsEmpty
                    ? Dl1DeveloperToolsBatchState.RolledBack
                    : Dl1DeveloperToolsBatchState.RollbackRequired,
                RolledBackUtc = rollbackErrors.IsEmpty ? DateTimeOffset.UtcNow : null,
                Errors = receipt.Errors.AddRange(rollbackErrors),
            };
            string? finalReceiptWriteError = await TryWriteBatchReceiptAsync(
                receipt,
                receiptPath).ConfigureAwait(false);
            if (finalReceiptWriteError is not null)
            {
                rollbackErrors = rollbackErrors.Add(finalReceiptWriteError);
                receipt = receipt with
                {
                    State = Dl1DeveloperToolsBatchState.RollbackRequired,
                    RolledBackUtc = null,
                    Errors = receipt.Errors.Add(finalReceiptWriteError),
                };
            }
            Exception failure = rollbackErrors.IsEmpty
                ? deploymentException
                : new AggregateException(
                    new Exception[] { deploymentException }
                        .Concat(rollbackErrors.Select(static message => new IOException(message))));
            throw new Dl1DeveloperToolsBatchTransactionException(
                rollbackErrors.IsEmpty
                    ? "Developer Tools batch commit failed; every completed model deployment was rolled back."
                    : "Developer Tools batch commit failed and automatic rollback requires review. See the batch receipt.",
                receipt,
                receiptPath,
                failure);
        }
    }

    public static async Task<Dl1DeveloperToolsBatchReceipt> RollbackBatchAsync(
        string receiptPath,
        CancellationToken cancellationToken = default)
    {
        (string fullReceiptPath, string projectRoot) = ValidateBatchReceiptPath(receiptPath);
        await using ProjectOperationLock projectLock = await AcquireProjectOperationLockAsync(
            projectRoot,
            cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedTransactionsLockedAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        Dl1DeveloperToolsBatchReceipt receipt = await ReadBatchReceiptAsync(
            fullReceiptPath,
            projectRoot,
            cancellationToken).ConfigureAwait(false);
        if (receipt.State == Dl1DeveloperToolsBatchState.RolledBack)
        {
            throw new InvalidOperationException("This Developer Tools batch has already been rolled back.");
        }

        if (receipt.State != Dl1DeveloperToolsBatchState.Committed &&
            receipt.State != Dl1DeveloperToolsBatchState.RollbackRequired)
        {
            throw new InvalidOperationException(
                $"Developer Tools batch state '{receipt.State}' cannot be rolled back manually.");
        }

        receipt = receipt with { State = Dl1DeveloperToolsBatchState.RollingBack };
        await WriteBatchReceiptAsync(receipt, fullReceiptPath, cancellationToken).ConfigureAwait(false);
        ImmutableArray<string> errors = await RollBackBatchChildrenLockedAsync(
            request: null,
            projectRoot,
            receipt.Deployments,
            CancellationToken.None).ConfigureAwait(false);
        receipt = receipt with
        {
            State = errors.IsEmpty
                ? Dl1DeveloperToolsBatchState.RolledBack
                : Dl1DeveloperToolsBatchState.RollbackRequired,
            RolledBackUtc = errors.IsEmpty ? DateTimeOffset.UtcNow : null,
            Errors = receipt.Errors.AddRange(errors),
        };
        await WriteBatchReceiptAsync(receipt, fullReceiptPath, CancellationToken.None).ConfigureAwait(false);
        if (!errors.IsEmpty)
        {
            throw new Dl1DeveloperToolsBatchTransactionException(
                "Developer Tools batch rollback requires review. See the batch receipt.",
                receipt,
                fullReceiptPath,
                new AggregateException(errors.Select(static message => new IOException(message))));
        }

        return receipt;
    }

    private static async Task<Dl1DeveloperToolsBatchPreflight> PreflightBatchCoreAsync(
        Dl1DeveloperToolsBatchRequest request,
        BatchValidation validation,
        CancellationToken cancellationToken)
    {
        var plans = ImmutableArray.CreateBuilder<Dl1DeveloperToolsDeploymentPlan>(
            request.Deployments.Length);
        foreach (Dl1DeveloperToolsDeploymentRequest child in request.Deployments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plans.Add(request.PreflightOverride is null
                ? await PreflightAsync(child, cancellationToken).ConfigureAwait(false)
                : await request.PreflightOverride(child, cancellationToken).ConfigureAwait(false));
        }

        ImmutableArray<Dl1DeveloperToolsDeploymentPlan> immutablePlans = plans.MoveToImmutable();
        var conflicts = ImmutableArray.CreateBuilder<Dl1DeveloperToolsBatchConflict>();
        AddDuplicateIdentityConflicts(immutablePlans, conflicts);
        AddCrossPlanArtifactConflicts(immutablePlans, conflicts);
        return new Dl1DeveloperToolsBatchPreflight(
            validation.ProjectRoot,
            immutablePlans,
            conflicts.ToImmutable());
    }

    private static BatchValidation ValidateBatchRequest(Dl1DeveloperToolsBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Deployments.IsDefaultOrEmpty ||
            request.Deployments.Length > MaximumBatchDeploymentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Select between 1 and {MaximumBatchDeploymentCount:N0} model deployments.");
        }

        var validated = ImmutableArray.CreateBuilder<ValidatedRequest>(request.Deployments.Length);
        foreach (Dl1DeveloperToolsDeploymentRequest child in request.Deployments)
        {
            ArgumentNullException.ThrowIfNull(child);
            if (request.PreflightOverride is not null && request.DeploymentOverride is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(child.ProjectRoot);
                validated.Add(new ValidatedRequest(
                    Path.GetFullPath(child.ProjectRoot),
                    child.CompilerExecutablePath,
                    child.RetailData0PakPath,
                    child.CharacterId,
                    child.ModelResourceName,
                    child.SurfaceName,
                    child.AnimationLibraryName));
            }
            else
            {
                validated.Add(ValidateRequest(child));
            }
        }

        ImmutableArray<ValidatedRequest> rows = validated.MoveToImmutable();
        string projectRoot = rows[0].ProjectRoot;
        if (rows.Any(row => !string.Equals(
                projectRoot,
                row.ProjectRoot,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Every checked model must target the same chosen Developer Tools project.");
        }

        return new BatchValidation(projectRoot, rows);
    }

    private static void AddDuplicateIdentityConflicts(
        ImmutableArray<Dl1DeveloperToolsDeploymentPlan> plans,
        ImmutableArray<Dl1DeveloperToolsBatchConflict>.Builder conflicts)
    {
        foreach (IGrouping<string, Dl1DeveloperToolsDeploymentPlan> group in plans.GroupBy(
                     static plan => $"{plan.CharacterId}/{plan.ModelResourceName}",
                     StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1))
        {
            conflicts.Add(new Dl1DeveloperToolsBatchConflict(
                group.First().ModelResourceName,
                $"Model resource identity '{group.Key}' is selected more than once."));
        }

        foreach (IGrouping<string, Dl1DeveloperToolsDeploymentPlan> group in plans.GroupBy(
                     static plan => plan.AnimationLibraryName,
                     StringComparer.OrdinalIgnoreCase).Where(static group => group.Count() > 1))
        {
            conflicts.Add(new Dl1DeveloperToolsBatchConflict(
                group.First().ModelResourceName,
                $"Animation library identity '{group.Key}' is selected more than once."));
        }
    }

    private static void AddCrossPlanArtifactConflicts(
        ImmutableArray<Dl1DeveloperToolsDeploymentPlan> plans,
        ImmutableArray<Dl1DeveloperToolsBatchConflict>.Builder conflicts)
    {
        var owners = new Dictionary<string, (string Model, Dl1DeploymentArtifactRole Role)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Dl1DeveloperToolsDeploymentPlan plan in plans)
        {
            foreach (Dl1DeveloperToolsDeploymentArtifact artifact in plan.Artifacts)
            {
                if (artifact.Role == Dl1DeploymentArtifactRole.ManifestOwned ||
                    (artifact.Role == Dl1DeploymentArtifactRole.Shared &&
                     string.Equals(
                         artifact.RelativePath,
                         "assets_pc/local_dx11.mp",
                         StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (owners.TryGetValue(artifact.RelativePath, out var owner))
                {
                    conflicts.Add(new Dl1DeveloperToolsBatchConflict(
                        plan.ModelResourceName,
                        $"Models '{owner.Model}' and '{plan.ModelResourceName}' both publish '{artifact.RelativePath}'. Rename or deselect one; batch output is never silently skipped."));
                }
                else
                {
                    owners.Add(artifact.RelativePath, (plan.ModelResourceName, artifact.Role));
                }
            }
        }
    }

    private static Dl1DeveloperToolsBatchReceiptItem ToBatchReceiptItem(
        string projectRoot,
        Dl1DeveloperToolsDeploymentResult result) => new(
            result.Receipt.CharacterId,
            result.Receipt.ModelResourceName,
            result.Receipt.AnimationLibraryName,
            result.Receipt.DeploymentId,
            NormalizeRelativePath(Path.GetRelativePath(projectRoot, result.ReceiptPath)));

    private static async Task<ImmutableArray<string>> RollBackBatchChildrenLockedAsync(
        Dl1DeveloperToolsBatchRequest? request,
        string projectRoot,
        ImmutableArray<Dl1DeveloperToolsBatchReceiptItem> deployments,
        CancellationToken cancellationToken)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        foreach (Dl1DeveloperToolsBatchReceiptItem item in deployments.Reverse())
        {
            try
            {
                string childReceipt = ResolveProjectPath(projectRoot, item.ReceiptRelativePath);
                if (request?.RollbackOverride is not null)
                {
                    await request.RollbackOverride(
                        childReceipt,
                        projectRoot,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await RollbackDeploymentLockedAsync(
                        childReceipt,
                        projectRoot,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"Rollback for model '{item.ModelResourceName}' failed: {exception.Message}");
            }
        }

        return errors.ToImmutable();
    }

    private static async Task RollbackDeploymentLockedAsync(
        string fullReceiptPath,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        Dl1DeveloperToolsDeploymentReceipt receipt = await ReadReceiptAsync(
            fullReceiptPath,
            projectRoot,
            cancellationToken).ConfigureAwait(false);
        if (receipt.RolledBackUtc is not null)
        {
            return;
        }

        EnsureNoLaterDeploymentOwnsChangedArtifacts(
            projectRoot,
            fullReceiptPath,
            receipt,
            cancellationToken);
        ImmutableArray<Dl1DeveloperToolsDeploymentReceiptArtifact> changedArtifacts = receipt.Artifacts
            .Where(static artifact => artifact.ChangedByDeployment)
            .ToImmutableArray();
        foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in changedArtifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = ResolveProjectPath(projectRoot, artifact.RelativePath);
            if (!File.Exists(destination))
            {
                throw new InvalidOperationException(
                    $"Cannot roll back because deployed file '{artifact.RelativePath}' is missing.");
            }

            string currentHash = await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, artifact.DeployedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot roll back because deployed file '{artifact.RelativePath}' was modified after deployment.");
            }

            if (artifact.BackupRelativePath is null)
            {
                if (artifact.PreviousSha256 is not null)
                {
                    throw new InvalidDataException(
                        $"Rollback metadata for '{artifact.RelativePath}' omits its required backup.");
                }

                continue;
            }

            if (artifact.PreviousSha256 is null)
            {
                throw new InvalidDataException(
                    $"Rollback metadata for '{artifact.RelativePath}' omits the previous content hash.");
            }

            string backup = ResolveProjectPath(projectRoot, artifact.BackupRelativePath);
            if (!File.Exists(backup) ||
                !HashesEqual(Sha256File(backup), artifact.PreviousSha256))
            {
                throw new InvalidDataException(
                    $"Rollback backup '{artifact.BackupRelativePath}' is missing or does not match its recorded hash.");
            }
        }

        string rollbackDirectory = CreateJobDirectory("batch-rollback");
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in changedArtifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = ResolveProjectPath(projectRoot, artifact.RelativePath);
                string snapshot = Path.Combine(
                    rollbackDirectory,
                    $"{snapshots.Count:D6}-{Path.GetFileName(destination)}");
                await PublishFileAtomicallyAsync(destination, snapshot, cancellationToken).ConfigureAwait(false);
                snapshots.Add(artifact.RelativePath, snapshot);
            }

            foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in changedArtifacts.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = ResolveProjectPath(projectRoot, artifact.RelativePath);
                if (artifact.BackupRelativePath is null)
                {
                    File.Delete(destination);
                }
                else
                {
                    await PublishFileAtomicallyAsync(
                        ResolveProjectPath(projectRoot, artifact.BackupRelativePath),
                        destination,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await WriteJsonDurablyAsync(
                receipt with
                {
                    RolledBackUtc = DateTimeOffset.UtcNow,
                    ProjectRoot = string.Empty,
                    ManifestPath = string.Empty,
                },
                fullReceiptPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception originalException)
        {
            var restoreErrors = new List<Exception>();
            foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in changedArtifacts)
            {
                try
                {
                    if (snapshots.TryGetValue(artifact.RelativePath, out string? snapshot) && File.Exists(snapshot))
                    {
                        await PublishFileAtomicallyAsync(
                            snapshot,
                            ResolveProjectPath(projectRoot, artifact.RelativePath),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception restoreException)
                {
                    restoreErrors.Add(restoreException);
                }
            }

            if (restoreErrors.Count == 0)
            {
                throw;
            }

            restoreErrors.Insert(0, originalException);
            throw new AggregateException(
                "Batch rollback failed and deployed files could not all be restored.",
                restoreErrors);
        }
        finally
        {
            DeleteOwnedDirectory(rollbackDirectory);
        }
    }

    private static async Task WriteBatchReceiptAsync(
        Dl1DeveloperToolsBatchReceipt receipt,
        string path,
        CancellationToken cancellationToken) =>
        await WriteJsonDurablyAsync(
            receipt with { ProjectRoot = string.Empty, ReceiptPath = string.Empty },
            path,
            cancellationToken).ConfigureAwait(false);

    private static async Task<string?> TryWriteBatchReceiptAsync(
        Dl1DeveloperToolsBatchReceipt receipt,
        string path)
    {
        try
        {
            await WriteBatchReceiptAsync(
                receipt,
                path,
                CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return $"Batch receipt update failed: {exception.Message}";
        }
    }

    private static async Task<Dl1DeveloperToolsBatchReceipt> ReadBatchReceiptAsync(
        string path,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Developer Tools batch receipt is empty or exceeds the supported size.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Dl1DeveloperToolsBatchReceipt receipt =
            await JsonSerializer.DeserializeAsync<Dl1DeveloperToolsBatchReceipt>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Developer Tools batch receipt is empty.");
        if (receipt.Format != BatchReceiptFormat ||
            receipt.SchemaVersion != 1 ||
            receipt.Deployments.Length > MaximumBatchDeploymentCount ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                receipt.BatchId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Developer Tools batch receipt format or identity is invalid.");
        }

        foreach (Dl1DeveloperToolsBatchReceiptItem item in receipt.Deployments)
        {
            string expectedPrefix = ".dl-reanimated/deployments/";
            string relative = NormalizeRelativePath(item.ReceiptRelativePath);
            if (!relative.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetFileNameWithoutExtension(relative),
                    item.DeploymentId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Developer Tools batch contains an invalid child receipt reference.");
            }

            _ = ResolveProjectPath(projectRoot, relative);
        }

        return receipt with { ProjectRoot = projectRoot, ReceiptPath = path };
    }

    private static (string ReceiptPath, string ProjectRoot) ValidateBatchReceiptPath(string receiptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        string fullPath = Path.GetFullPath(receiptPath);
        string? batchesDirectory = Path.GetDirectoryName(fullPath);
        string? metadataDirectory = batchesDirectory is null ? null : Path.GetDirectoryName(batchesDirectory);
        string? projectRoot = metadataDirectory is null ? null : Path.GetDirectoryName(metadataDirectory);
        if (projectRoot is null ||
            !string.Equals(Path.GetFileName(batchesDirectory), "batches", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(metadataDirectory), ".dl-reanimated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A batch receipt must remain under <project>/.dl-reanimated/batches.");
        }

        string canonical = ResolveProjectPath(
            projectRoot,
            NormalizeRelativePath(Path.GetRelativePath(projectRoot, fullPath)));
        if (!string.Equals(canonical, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The batch receipt resolves outside its canonical project location.");
        }

        return (fullPath, projectRoot);
    }

    private sealed record BatchValidation(
        string ProjectRoot,
        ImmutableArray<ValidatedRequest> ValidatedRequests);
}
