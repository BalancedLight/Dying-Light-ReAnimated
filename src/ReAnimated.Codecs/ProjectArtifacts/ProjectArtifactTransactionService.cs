using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReAnimated.Codecs.ProjectArtifacts;

public sealed record ProjectArtifactWrite(
    string RelativePath,
    ReadOnlyMemory<byte> Bytes);

public sealed record ProjectArtifactTransactionRequest
{
    public required string ProjectRoot { get; init; }

    public required Guid OwnerProjectId { get; init; }

    public ImmutableArray<ProjectArtifactWrite> Artifacts { get; init; } = [];

    internal Func<string, int, CancellationToken, Task>?
        ArtifactPublishedObserver { get; init; }
}

public sealed record ProjectArtifactTransactionReceiptArtifact
{
    public required string RelativePath { get; init; }

    public required string DeployedSha256 { get; init; }

    public string? PreviousSha256 { get; init; }

    public string? BackupRelativePath { get; init; }

    public bool CreatedByTransaction { get; init; }

    public bool ChangedByTransaction { get; init; }
}

public sealed record ProjectArtifactTransactionReceipt
{
    public string Format { get; init; } =
        ProjectArtifactTransactionService.ReceiptFormat;

    public int SchemaVersion { get; init; } = 1;

    public required string TransactionId { get; init; }

    public required Guid OwnerProjectId { get; init; }

    public required DateTimeOffset CompletedUtc { get; init; }

    public DateTimeOffset? RolledBackUtc { get; init; }

    public ImmutableArray<ProjectArtifactTransactionReceiptArtifact>
        Artifacts { get; init; } = [];

    public required string ReceiptContentSha256 { get; init; }
}

public sealed record ProjectArtifactTransactionResult(
    ProjectArtifactTransactionReceipt Receipt,
    string ReceiptRelativePath);

public sealed class ProjectArtifactTransactionException : IOException
{
    public ProjectArtifactTransactionException(
        string message,
        Exception innerException,
        IReadOnlyList<Exception> rollbackFailures)
        : base(message, innerException)
    {
        RollbackFailures = rollbackFailures ??
            throw new ArgumentNullException(nameof(rollbackFailures));
    }

    public IReadOnlyList<Exception> RollbackFailures { get; }
}

/// <summary>
/// Publishes a bounded set of project-owned byte artifacts as one recoverable
/// filesystem transaction. Destinations are always canonical paths beneath a
/// validated project root; transaction metadata and backups stay in the
/// project's private <c>.dl-reanimated</c> directory.
/// </summary>
public static class ProjectArtifactTransactionService
{
    public const string ReceiptFormat =
        "dl-reanimated-project-artifact-transaction";
    public const int MaximumArtifactCount = 4096;
    public const long MaximumArtifactBytes = 1024L * 1024L * 1024L;
    public const long MaximumTotalBytes = 2L * 1024L * 1024L * 1024L;
    public const int MaximumRelativePathLength = 512;
    public const long MaximumReceiptBytes = 8L * 1024L * 1024L;

    private const string InternalDirectory = ".dl-reanimated";
    private const string ReceiptDirectory =
        ".dl-reanimated/receipts/project-artifacts";
    private const string BackupDirectory =
        ".dl-reanimated/backups/project-artifacts";
    private const string TransactionDirectory =
        ".dl-reanimated/transactions/project-artifacts";
    private const string LockDirectory =
        ".dl-reanimated/locks/project-artifacts";

    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TransactionIdPattern = new(
        "^[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions ReceiptSerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
    private static readonly JsonSerializerOptions FingerprintSerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        ProjectLocks = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ProjectArtifactTransactionResult> CommitAsync(
        ProjectArtifactTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string projectRoot = ValidateProjectRoot(request.ProjectRoot);
        if (request.OwnerProjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The artifact transaction owner project ID cannot be empty.",
                nameof(request));
        }

        ImmutableArray<PreparedArtifact> artifacts =
            PrepareArtifacts(request.Artifacts);
        string lockKey = $"{projectRoot}|{request.OwnerProjectId:N}";
        SemaphoreSlim transactionLock = ProjectLocks.GetOrAdd(
            lockKey,
            static _ => new SemaphoreSlim(1, 1));
        await transactionLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await CommitLockedAsync(
                    projectRoot,
                    request,
                    artifacts,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            transactionLock.Release();
        }
    }

    public static async Task<ProjectArtifactTransactionReceipt> RollbackAsync(
        string projectRoot,
        Guid ownerProjectId,
        string receiptRelativePath,
        CancellationToken cancellationToken = default)
    {
        string validatedRoot = ValidateProjectRoot(projectRoot);
        if (ownerProjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The artifact transaction owner project ID cannot be empty.",
                nameof(ownerProjectId));
        }

        string canonicalReceiptPath = ValidateCanonicalRelativePath(
            receiptRelativePath,
            allowInternalPath: true,
            nameof(receiptRelativePath));
        string requiredReceiptPrefix =
            $"{ReceiptDirectory}/{ownerProjectId:N}/";
        if (!canonicalReceiptPath.StartsWith(
                requiredReceiptPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The artifact receipt is outside the requesting project's internal receipt directory.");
        }

        string lockKey = $"{validatedRoot}|{ownerProjectId:N}";
        SemaphoreSlim transactionLock = ProjectLocks.GetOrAdd(
            lockKey,
            static _ => new SemaphoreSlim(1, 1));
        await transactionLock.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await using FileStream processLock = AcquireProcessLock(
                validatedRoot,
                ownerProjectId);
            string receiptPath = ResolveProjectPath(
                validatedRoot,
                canonicalReceiptPath);
            ProjectArtifactTransactionReceipt receipt =
                await ReadAndValidateReceiptAsync(
                        validatedRoot,
                        ownerProjectId,
                        canonicalReceiptPath,
                        receiptPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (receipt.RolledBackUtc is not null)
            {
                return receipt;
            }

            ImmutableArray<RollbackArtifact> rollbackArtifacts =
                await PreflightRollbackAsync(
                        validatedRoot,
                        receipt,
                        cancellationToken)
                    .ConfigureAwait(false);
            foreach (RollbackArtifact artifact in rollbackArtifacts.Reverse())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!artifact.RequiresMutation)
                {
                    continue;
                }

                if (artifact.Receipt.CreatedByTransaction)
                {
                    File.Delete(artifact.DestinationPath);
                    continue;
                }

                await RestoreBackupAsync(
                        artifact.BackupPath!,
                        artifact.DestinationPath,
                        artifact.Receipt.PreviousSha256!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await VerifyRollbackAsync(
                    rollbackArtifacts,
                    cancellationToken)
                .ConfigureAwait(false);
            ProjectArtifactTransactionReceipt rolledBack =
                WithReceiptFingerprint(receipt with
                {
                    RolledBackUtc = DateTimeOffset.UtcNow,
                    ReceiptContentSha256 = string.Empty,
                });
            await WriteReceiptAtomicallyAsync(
                    receiptPath,
                    rolledBack,
                    cancellationToken)
                .ConfigureAwait(false);
            return rolledBack;
        }
        finally
        {
            transactionLock.Release();
        }
    }

    public static async Task<ProjectArtifactTransactionReceipt>
        ReadReceiptAsync(
            string projectRoot,
            Guid ownerProjectId,
            string receiptRelativePath,
            CancellationToken cancellationToken = default)
    {
        string validatedRoot = ValidateProjectRoot(projectRoot);
        if (ownerProjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The artifact transaction owner project ID cannot be empty.",
                nameof(ownerProjectId));
        }

        string canonical = ValidateCanonicalRelativePath(
            receiptRelativePath,
            allowInternalPath: true,
            nameof(receiptRelativePath));
        string fullPath = ResolveProjectPath(validatedRoot, canonical);
        return await ReadAndValidateReceiptAsync(
                validatedRoot,
                ownerProjectId,
                canonical,
                fullPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static string ComputeReceiptContentSha256(
        ProjectArtifactTransactionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ProjectArtifactTransactionReceipt fingerprintSource = receipt with
        {
            ReceiptContentSha256 = string.Empty,
        };
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            fingerprintSource,
            FingerprintSerializerOptions);
        return Sha256(canonical);
    }

    private static async Task<ProjectArtifactTransactionResult>
        CommitLockedAsync(
            string projectRoot,
            ProjectArtifactTransactionRequest request,
            ImmutableArray<PreparedArtifact> artifacts,
            CancellationToken cancellationToken)
    {
        await using FileStream processLock = AcquireProcessLock(
            projectRoot,
            request.OwnerProjectId);
        string transactionId = Guid.NewGuid().ToString("N");
        string owner = request.OwnerProjectId.ToString("N");
        string transactionRootRelative =
            $"{TransactionDirectory}/{owner}/{transactionId}";
        string stagingRootRelative = $"{transactionRootRelative}/staging";
        string backupRootRelative =
            $"{BackupDirectory}/{owner}/{transactionId}";
        string receiptRelative =
            $"{ReceiptDirectory}/{owner}/{transactionId}.json";
        string transactionRoot = EnsureOwnedDirectory(
            projectRoot,
            transactionRootRelative);
        _ = EnsureOwnedDirectory(projectRoot, stagingRootRelative);
        _ = EnsureOwnedDirectory(
            projectRoot,
            $"{ReceiptDirectory}/{owner}");

        var preparedReceipts =
            ImmutableArray.CreateBuilder<
                ProjectArtifactTransactionReceiptArtifact>(artifacts.Length);
        try
        {
            foreach (PreparedArtifact artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = ResolveProjectPath(
                    projectRoot,
                    artifact.RelativePath);
                EnsureNoReparseComponents(projectRoot, artifact.RelativePath);
                if (Directory.Exists(destination))
                {
                    throw new IOException(
                        $"The artifact destination is a directory: '{artifact.RelativePath}'.");
                }

                string? previousSha256 = File.Exists(destination)
                    ? await Sha256FileAsync(destination, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                bool changed = !HashesEqual(
                    previousSha256,
                    artifact.DeployedSha256);
                string? backupRelative = previousSha256 is not null && changed
                    ? $"{backupRootRelative}/{artifact.RelativePath}"
                    : null;
                preparedReceipts.Add(
                    new ProjectArtifactTransactionReceiptArtifact
                    {
                        RelativePath = artifact.RelativePath,
                        DeployedSha256 = artifact.DeployedSha256,
                        PreviousSha256 = previousSha256,
                        BackupRelativePath = backupRelative,
                        CreatedByTransaction = previousSha256 is null,
                        ChangedByTransaction = changed,
                    });

                string staged = ResolveProjectPath(
                    projectRoot,
                    $"{stagingRootRelative}/{artifact.RelativePath}");
                EnsureOwnedParentDirectory(
                    projectRoot,
                    $"{stagingRootRelative}/{artifact.RelativePath}");
                await WriteBytesDurablyAsync(
                        staged,
                        artifact.Bytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            TryDeleteOwnedDirectory(projectRoot, transactionRoot);
            TryDeleteOwnedDirectory(
                projectRoot,
                ResolveProjectPath(projectRoot, backupRootRelative));
            throw;
        }

        ImmutableArray<ProjectArtifactTransactionReceiptArtifact>
            receiptArtifacts = preparedReceipts.MoveToImmutable();
        var committed = new Stack<CommittedArtifact>();
        bool rollbackSucceeded = false;
        try
        {
            for (int index = 0; index < artifacts.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreparedArtifact artifact = artifacts[index];
                ProjectArtifactTransactionReceiptArtifact metadata =
                    receiptArtifacts[index];
                if (!metadata.ChangedByTransaction)
                {
                    continue;
                }

                string destination = ResolveProjectPath(
                    projectRoot,
                    artifact.RelativePath);
                string staged = ResolveProjectPath(
                    projectRoot,
                    $"{stagingRootRelative}/{artifact.RelativePath}");
                EnsureOwnedParentDirectory(projectRoot, artifact.RelativePath);
                string? currentSha256 = File.Exists(destination)
                    ? await Sha256FileAsync(destination, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                if (!HashesEqual(
                        currentSha256,
                        metadata.PreviousSha256))
                {
                    throw new IOException(
                        $"The artifact destination changed while the transaction was staging: '{artifact.RelativePath}'.");
                }

                string? backupPath = null;
                if (metadata.PreviousSha256 is not null)
                {
                    backupPath = ResolveProjectPath(
                        projectRoot,
                        metadata.BackupRelativePath!);
                    EnsureOwnedParentDirectory(
                        projectRoot,
                        metadata.BackupRelativePath!);
                    File.Replace(
                        staged,
                        destination,
                        backupPath,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(staged, destination);
                }

                committed.Push(new CommittedArtifact(
                    metadata,
                    destination,
                    backupPath));
                string deployedSha256 = await Sha256FileAsync(
                        destination,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!HashesEqual(
                        deployedSha256,
                        metadata.DeployedSha256))
                {
                    throw new IOException(
                        $"The published artifact failed hash verification: '{artifact.RelativePath}'.");
                }

                if (request.ArtifactPublishedObserver is not null)
                {
                    await request.ArtifactPublishedObserver(
                            artifact.RelativePath,
                            index,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            ProjectArtifactTransactionReceipt receipt =
                WithReceiptFingerprint(
                    new ProjectArtifactTransactionReceipt
                    {
                        TransactionId = transactionId,
                        OwnerProjectId = request.OwnerProjectId,
                        CompletedUtc = DateTimeOffset.UtcNow,
                        Artifacts = receiptArtifacts,
                        ReceiptContentSha256 = string.Empty,
                    });
            string receiptPath = ResolveProjectPath(
                projectRoot,
                receiptRelative);
            await WriteReceiptAtomicallyAsync(
                    receiptPath,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ProjectArtifactTransactionResult(
                receipt,
                receiptRelative);
        }
        catch (Exception exception)
        {
            List<Exception> rollbackFailures =
                await RollbackCommittedAsync(committed)
                    .ConfigureAwait(false);
            rollbackSucceeded = rollbackFailures.Count == 0;
            if (!rollbackSucceeded)
            {
                throw new ProjectArtifactTransactionException(
                    "The project artifact transaction failed and could not completely restore its prior files.",
                    exception,
                    rollbackFailures);
            }

            throw;
        }
        finally
        {
            TryDeleteOwnedDirectory(projectRoot, transactionRoot);
            if (rollbackSucceeded)
            {
                string backupRoot = ResolveProjectPath(
                    projectRoot,
                    backupRootRelative);
                TryDeleteOwnedDirectory(projectRoot, backupRoot);
            }
        }
    }

    private static ImmutableArray<PreparedArtifact> PrepareArtifacts(
        ImmutableArray<ProjectArtifactWrite> artifacts)
    {
        if (artifacts.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A project artifact transaction must contain at least one artifact.",
                nameof(artifacts));
        }

        if (artifacts.Length > MaximumArtifactCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(artifacts),
                $"A project artifact transaction cannot contain more than {MaximumArtifactCount:N0} artifacts.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prepared = ImmutableArray.CreateBuilder<PreparedArtifact>(
            artifacts.Length);
        long totalBytes = 0;
        foreach (ProjectArtifactWrite artifact in artifacts)
        {
            if (artifact is null)
            {
                throw new ArgumentException(
                    "A project artifact transaction contains a null artifact.",
                    nameof(artifacts));
            }

            string relativePath = ValidateCanonicalRelativePath(
                artifact.RelativePath,
                allowInternalPath: false,
                nameof(artifacts));
            if (!paths.Add(relativePath))
            {
                throw new ArgumentException(
                    $"A project artifact destination is duplicated: '{relativePath}'.",
                    nameof(artifacts));
            }

            if (artifact.Bytes.Length > MaximumArtifactBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(artifacts),
                    $"Artifact '{relativePath}' exceeds the {MaximumArtifactBytes:N0}-byte limit.");
            }

            totalBytes = checked(totalBytes + artifact.Bytes.Length);
            if (totalBytes > MaximumTotalBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(artifacts),
                    $"The project artifact transaction exceeds the {MaximumTotalBytes:N0}-byte limit.");
            }

            byte[] bytes = artifact.Bytes.ToArray();
            prepared.Add(new PreparedArtifact(
                relativePath,
                bytes,
                Sha256(bytes)));
        }

        return prepared
            .OrderBy(static artifact => artifact.RelativePath,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static string ValidateProjectRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string fullPath = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The validated project root does not exist: '{fullPath}'.");
        }

        string? volumeRoot = Path.GetPathRoot(fullPath)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(
                fullPath,
                volumeRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A filesystem volume root cannot be used as a project root.",
                nameof(projectRoot));
        }

        return fullPath;
    }

    private static string ValidateCanonicalRelativePath(
        string relativePath,
        bool allowInternalPath,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, parameterName);
        if (relativePath.Length > MaximumRelativePathLength ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.StartsWith('/') ||
            relativePath.EndsWith('/') ||
            !relativePath.IsNormalized(NormalizationForm.FormC))
        {
            throw new ArgumentException(
                $"The project artifact path is not a canonical project-relative path: '{relativePath}'.",
                parameterName);
        }

        string[] segments = relativePath.Split('/');
        if (segments.Length == 0 ||
            segments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Length > 255 ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                segment.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
                segment.Any(char.IsControl) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException(
                $"The project artifact path is not a canonical project-relative path: '{relativePath}'.",
                parameterName);
        }

        if (!allowInternalPath &&
            string.Equals(
                segments[0],
                InternalDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Project artifacts cannot target the internal transaction directory.",
                parameterName);
        }

        return relativePath;
    }

    private static string ResolveProjectPath(
        string projectRoot,
        string canonicalRelativePath)
    {
        string candidate = Path.GetFullPath(Path.Combine(
            projectRoot,
            canonicalRelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        string prefix = projectRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A project artifact path escapes the validated project root.");
        }

        return candidate;
    }

    private static void EnsureNoReparseComponents(
        string projectRoot,
        string canonicalRelativePath)
    {
        string current = projectRoot;
        foreach (string segment in canonicalRelativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"A project artifact path crosses a reparse point: '{canonicalRelativePath}'.");
            }
        }
    }

    private static string EnsureOwnedDirectory(
        string projectRoot,
        string canonicalRelativePath)
    {
        EnsureNoReparseComponents(projectRoot, canonicalRelativePath);
        string directory = ResolveProjectPath(
            projectRoot,
            canonicalRelativePath);
        Directory.CreateDirectory(directory);
        EnsureNoReparseComponents(projectRoot, canonicalRelativePath);
        return directory;
    }

    private static void EnsureOwnedParentDirectory(
        string projectRoot,
        string canonicalRelativePath)
    {
        int separator = canonicalRelativePath.LastIndexOf('/');
        if (separator <= 0)
        {
            return;
        }

        _ = EnsureOwnedDirectory(
            projectRoot,
            canonicalRelativePath[..separator]);
    }

    private static FileStream AcquireProcessLock(
        string projectRoot,
        Guid ownerProjectId)
    {
        string lockDirectory = EnsureOwnedDirectory(
            projectRoot,
            LockDirectory);
        string lockPath = Path.Combine(
            lockDirectory,
            $"{ownerProjectId:N}.lock");
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.WriteThrough);
    }

    private static async Task WriteBytesDurablyAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteReceiptAtomicallyAsync(
        string path,
        ProjectArtifactTransactionReceipt receipt,
        CancellationToken cancellationToken)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            receipt,
            ReceiptSerializerOptions);
        if (json.LongLength > MaximumReceiptBytes)
        {
            throw new InvalidDataException(
                "The project artifact receipt exceeds its bounded size.");
        }

        try
        {
            await WriteBytesDurablyAsync(
                    temporary,
                    json,
                    cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Replace(
                    temporary,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<ProjectArtifactTransactionReceipt>
        ReadAndValidateReceiptAsync(
            string projectRoot,
            Guid ownerProjectId,
            string receiptRelativePath,
            string receiptPath,
            CancellationToken cancellationToken)
    {
        string requiredPrefix =
            $"{ReceiptDirectory}/{ownerProjectId:N}/";
        if (!receiptRelativePath.StartsWith(
                requiredPrefix,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The project artifact receipt is outside the owner's receipt directory.");
        }

        EnsureNoReparseComponents(projectRoot, receiptRelativePath);
        var file = new FileInfo(receiptPath);
        if (!file.Exists || file.Length is <= 0 or > MaximumReceiptBytes)
        {
            throw new InvalidDataException(
                "The project artifact receipt is missing, empty, or exceeds its bounded size.");
        }

        byte[] json = await File.ReadAllBytesAsync(
                receiptPath,
                cancellationToken)
            .ConfigureAwait(false);
        ProjectArtifactTransactionReceipt receipt =
            JsonSerializer.Deserialize<
                ProjectArtifactTransactionReceipt>(
                    json,
                    ReceiptSerializerOptions)
            ?? throw new InvalidDataException(
                "The project artifact receipt is empty.");
        ValidateReceipt(
            receipt,
            ownerProjectId,
            receiptRelativePath);
        return receipt;
    }

    private static void ValidateReceipt(
        ProjectArtifactTransactionReceipt receipt,
        Guid ownerProjectId,
        string receiptRelativePath)
    {
        if (!string.Equals(
                receipt.Format,
                ReceiptFormat,
                StringComparison.Ordinal) ||
            receipt.SchemaVersion != 1 ||
            receipt.OwnerProjectId != ownerProjectId ||
            string.IsNullOrWhiteSpace(receipt.TransactionId) ||
            !TransactionIdPattern.IsMatch(receipt.TransactionId) ||
            receipt.CompletedUtc == default ||
            receipt.RolledBackUtc < receipt.CompletedUtc ||
            receipt.Artifacts.IsDefaultOrEmpty ||
            receipt.Artifacts.Length > MaximumArtifactCount)
        {
            throw new InvalidDataException(
                "The project artifact receipt identity or schema is invalid.");
        }

        string expectedReceipt =
            $"{ReceiptDirectory}/{ownerProjectId:N}/{receipt.TransactionId}.json";
        if (!string.Equals(
                expectedReceipt,
                receiptRelativePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The project artifact receipt path does not match its transaction identity.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectArtifactTransactionReceiptArtifact artifact in
                 receipt.Artifacts)
        {
            if (artifact is null)
            {
                throw new InvalidDataException(
                    "The project artifact receipt contains a null artifact.");
            }

            string relativePath = ValidateCanonicalRelativePath(
                artifact.RelativePath,
                allowInternalPath: false,
                nameof(receipt));
            if (!paths.Add(relativePath) ||
                !Sha256Pattern.IsMatch(artifact.DeployedSha256) ||
                artifact.PreviousSha256 is not null &&
                !Sha256Pattern.IsMatch(artifact.PreviousSha256))
            {
                throw new InvalidDataException(
                    "The project artifact receipt contains a duplicate path or invalid hash.");
            }

            if (!artifact.ChangedByTransaction)
            {
                if (artifact.CreatedByTransaction ||
                    artifact.BackupRelativePath is not null ||
                    !HashesEqual(
                        artifact.PreviousSha256,
                        artifact.DeployedSha256))
                {
                    throw new InvalidDataException(
                        "An unchanged receipt artifact has inconsistent ownership metadata.");
                }

                continue;
            }

            if (artifact.CreatedByTransaction)
            {
                if (artifact.PreviousSha256 is not null ||
                    artifact.BackupRelativePath is not null)
                {
                    throw new InvalidDataException(
                        "A created receipt artifact has unexpected prior-content metadata.");
                }

                continue;
            }

            if (artifact.PreviousSha256 is null ||
                artifact.BackupRelativePath is null)
            {
                throw new InvalidDataException(
                    "A replaced receipt artifact omits its prior content or backup.");
            }

            string expectedBackup =
                $"{BackupDirectory}/{ownerProjectId:N}/{receipt.TransactionId}/{relativePath}";
            string backup = ValidateCanonicalRelativePath(
                artifact.BackupRelativePath,
                allowInternalPath: true,
                nameof(receipt));
            if (!string.Equals(
                    expectedBackup,
                    backup,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A receipt backup is outside its transaction directory.");
            }
        }

        if (string.IsNullOrWhiteSpace(receipt.ReceiptContentSha256) ||
            !Sha256Pattern.IsMatch(receipt.ReceiptContentSha256) ||
            !HashesEqual(
                receipt.ReceiptContentSha256,
                ComputeReceiptContentSha256(receipt)))
        {
            throw new InvalidDataException(
                "The project artifact receipt content fingerprint is invalid.");
        }
    }

    private static async Task<ImmutableArray<RollbackArtifact>>
        PreflightRollbackAsync(
            string projectRoot,
            ProjectArtifactTransactionReceipt receipt,
            CancellationToken cancellationToken)
    {
        var artifacts = ImmutableArray.CreateBuilder<RollbackArtifact>(
            receipt.Artifacts.Length);
        foreach (ProjectArtifactTransactionReceiptArtifact metadata in
                 receipt.Artifacts)
        {
            string destination = ResolveProjectPath(
                projectRoot,
                metadata.RelativePath);
            EnsureNoReparseComponents(projectRoot, metadata.RelativePath);
            if (Directory.Exists(destination))
            {
                throw new InvalidDataException(
                    $"Artifact '{metadata.RelativePath}' became a directory; rollback was not started.");
            }

            if (!metadata.ChangedByTransaction)
            {
                artifacts.Add(new RollbackArtifact(
                    metadata,
                    destination,
                    BackupPath: null,
                    RequiresMutation: false));
                continue;
            }

            string? currentSha256 = File.Exists(destination)
                ? await Sha256FileAsync(destination, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            bool alreadyRestored = metadata.CreatedByTransaction
                ? currentSha256 is null
                : HashesEqual(currentSha256, metadata.PreviousSha256);
            if (!alreadyRestored &&
                !HashesEqual(currentSha256, metadata.DeployedSha256))
            {
                throw new InvalidDataException(
                    $"Artifact '{metadata.RelativePath}' changed after export; rollback was not started.");
            }

            string? backupPath = null;
            if (metadata.BackupRelativePath is not null)
            {
                backupPath = ResolveProjectPath(
                    projectRoot,
                    metadata.BackupRelativePath);
                EnsureNoReparseComponents(
                    projectRoot,
                    metadata.BackupRelativePath);
                if (!File.Exists(backupPath) ||
                    !HashesEqual(
                        await Sha256FileAsync(
                                backupPath,
                                cancellationToken)
                            .ConfigureAwait(false),
                        metadata.PreviousSha256))
                {
                    throw new InvalidDataException(
                        $"The rollback backup for '{metadata.RelativePath}' is missing or corrupt.");
                }
            }

            artifacts.Add(new RollbackArtifact(
                metadata,
                destination,
                backupPath,
                RequiresMutation:
                    metadata.ChangedByTransaction && !alreadyRestored));
        }

        return artifacts.MoveToImmutable();
    }

    private static async Task RestoreBackupAsync(
        string backupPath,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        string temporary =
            $"{destinationPath}.{Guid.NewGuid():N}.rollback";
        try
        {
            File.Copy(backupPath, temporary, overwrite: false);
            string copiedSha256 = await Sha256FileAsync(
                    temporary,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!HashesEqual(copiedSha256, expectedSha256))
            {
                throw new InvalidDataException(
                    "A copied rollback backup failed hash verification.");
            }

            File.Replace(
                temporary,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task VerifyRollbackAsync(
        ImmutableArray<RollbackArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        foreach (RollbackArtifact artifact in artifacts)
        {
            if (!artifact.Receipt.ChangedByTransaction)
            {
                continue;
            }

            if (artifact.Receipt.CreatedByTransaction)
            {
                if (File.Exists(artifact.DestinationPath) ||
                    Directory.Exists(artifact.DestinationPath))
                {
                    throw new IOException(
                        $"Created artifact '{artifact.Receipt.RelativePath}' remained after rollback.");
                }

                continue;
            }

            if (!File.Exists(artifact.DestinationPath) ||
                !HashesEqual(
                    await Sha256FileAsync(
                            artifact.DestinationPath,
                            cancellationToken)
                        .ConfigureAwait(false),
                    artifact.Receipt.PreviousSha256))
            {
                throw new IOException(
                    $"Replaced artifact '{artifact.Receipt.RelativePath}' was not restored by rollback.");
            }
        }
    }

    private static async Task<List<Exception>> RollbackCommittedAsync(
        Stack<CommittedArtifact> committed)
    {
        var failures = new List<Exception>();
        while (committed.TryPop(out CommittedArtifact? artifact))
        {
            try
            {
                if (artifact.Receipt.CreatedByTransaction)
                {
                    if (File.Exists(artifact.DestinationPath))
                    {
                        File.Delete(artifact.DestinationPath);
                    }

                    continue;
                }

                if (artifact.BackupPath is null ||
                    !File.Exists(artifact.BackupPath))
                {
                    throw new IOException(
                        $"The transaction backup for '{artifact.Receipt.RelativePath}' is missing.");
                }

                await RestoreBackupAsync(
                        artifact.BackupPath,
                        artifact.DestinationPath,
                        artifact.Receipt.PreviousSha256!,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static ProjectArtifactTransactionReceipt WithReceiptFingerprint(
        ProjectArtifactTransactionReceipt receipt) =>
        receipt with
        {
            ReceiptContentSha256 = ComputeReceiptContentSha256(receipt),
        };

    private static async Task<string> Sha256FileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool HashesEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteOwnedDirectory(
        string projectRoot,
        string directory)
    {
        string transactions = ResolveProjectPath(
            projectRoot,
            TransactionDirectory);
        string backups = ResolveProjectPath(
            projectRoot,
            BackupDirectory);
        bool owned = directory.StartsWith(
                transactions + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            directory.StartsWith(
                backups + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        if (!owned || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup leaves only project-private staging or backup
            // data; the user-owned destination has already been resolved.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }

    private sealed record PreparedArtifact(
        string RelativePath,
        byte[] Bytes,
        string DeployedSha256);

    private sealed record CommittedArtifact(
        ProjectArtifactTransactionReceiptArtifact Receipt,
        string DestinationPath,
        string? BackupPath);

    private sealed record RollbackArtifact(
        ProjectArtifactTransactionReceiptArtifact Receipt,
        string DestinationPath,
        string? BackupPath,
        bool RequiresMutation);
}
