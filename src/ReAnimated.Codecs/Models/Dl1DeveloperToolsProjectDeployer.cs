using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public enum Dl1DeploymentArtifactRole
{
    Source,
    Compiled,
    Shared,
    PortableOnly,
    ManifestOwned,
}

public enum Dl1DeploymentConflictResolution
{
    Block,
    BackUpAndReplace,
    Skip,
    Cancel,
}

public enum Dl1DeploymentArtifactDisposition
{
    Create,
    UpdateOwned,
    Unchanged,
    ReplaceUnowned,
    Skip,
    Conflict,
}

public sealed record Dl1DeveloperToolsDeploymentRequest
{
    public required FbxModelAuthoringImportResult Model { get; init; }

    public required string ProjectRoot { get; init; }

    public required string CompilerExecutablePath { get; init; }

    public required string RetailData0PakPath { get; init; }

    public required string CharacterId { get; init; }

    public required string ModelResourceName { get; init; }

    public string SurfaceName { get; init; } = "default";

    public required string AnimationLibraryName { get; init; }

    public ImmutableArray<CustomModelAnimationClip> AnimationSelections { get; init; } = [];

    public bool InstallLooseAnm2 { get; init; } = true;

    public bool ExportPortableAnimationRpack { get; init; } = true;

    public ImmutableDictionary<string, Dl1DeploymentConflictResolution> ConflictResolutions { get; init; } =
        ImmutableDictionary<string, Dl1DeploymentConflictResolution>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public TimeSpan CompilerTimeout { get; init; } = TimeSpan.FromMinutes(10);

    internal Func<Dl1SourceModelBuildRequest, CancellationToken, Task<Dl1SourceModelBuildResult>>?
        SourceWriterOverride { get; init; }

    internal Func<Dl1OfficialModelCompilerRequest, CancellationToken, Task<Dl1OfficialModelCompilerResult>>?
        ModelCompilerOverride { get; init; }

    internal Func<Dl1OfficialAnimationCompilerRequest, CancellationToken, Task<Dl1OfficialAnimationCompilerResult>>?
        AnimationCompilerOverride { get; init; }

    internal Func<string, CancellationToken, Task>? ArtifactPublishedObserver { get; init; }
}

public sealed record Dl1DeveloperToolsDeploymentArtifact(
    string RelativePath,
    Dl1DeploymentArtifactRole Role,
    Dl1DeploymentArtifactDisposition Disposition,
    bool Required,
    bool ExistingFileIsOwned,
    string? ExistingSha256,
    string? PreparedSha256,
    string Description);

public sealed record Dl1DeveloperToolsDeploymentConflict(
    string RelativePath,
    string Message,
    bool CanSkip);

public sealed record Dl1DeveloperToolsDeploymentAnimation(
    string SourceName,
    string AnimationName,
    string Anm2FileName,
    int StartFrame,
    int EndFrame,
    int FramesPerSecond,
    string ScrSequence);

public sealed record Dl1DeveloperToolsDeploymentPlan(
    string CharacterId,
    string ModelResourceName,
    string AnimationLibraryName,
    string AnimationScriptRelativePath,
    ImmutableArray<Dl1DeveloperToolsDeploymentArtifact> Artifacts,
    ImmutableArray<Dl1DeveloperToolsDeploymentConflict> Conflicts,
    ImmutableArray<string> StaleDuplicateResources,
    ImmutableArray<string> LegacyOutputWarnings,
    ImmutableArray<string> AnimationNames,
    bool InstallLooseAnm2,
    bool ExportPortableAnimationRpack)
{
    public bool CanDeploy => Conflicts.IsEmpty;

    public ImmutableArray<Dl1DeveloperToolsDeploymentAnimation> Animations { get; init; } = [];

    public ImmutableArray<string> LegacyOutputPaths { get; init; } = [];

    public string? PortableRpackRelativePath { get; init; }
}

public sealed record Dl1DeveloperToolsLegacyBackupResult(
    string BackupId,
    string BackupRootRelativePath,
    ImmutableArray<string> BackedUpRelativePaths);

public sealed record Dl1DeveloperToolsDeploymentReceiptArtifact(
    string RelativePath,
    Dl1DeploymentArtifactRole Role,
    string DeployedSha256,
    string? PreviousSha256,
    string? BackupRelativePath,
    bool CreatedByDeployment,
    bool ChangedByDeployment);

public sealed record Dl1DeveloperToolsDeploymentReceipt
{
    public string Format { get; init; } = "dl-reanimated-developer-tools-deployment";

    public int SchemaVersion { get; init; } = 1;

    public required string DeploymentId { get; init; }

    public required string CharacterId { get; init; }

    public required string ModelResourceName { get; init; }

    public required string AnimationLibraryName { get; init; }

    public required string AnimationScriptRelativePath { get; init; }

    public required string ModelCompilerFingerprint { get; init; }

    public required string AnimationCompilerFingerprint { get; init; }

    public required DateTimeOffset CompletedUtc { get; init; }

    public DateTimeOffset? RolledBackUtc { get; init; }

    public ImmutableArray<Dl1DeveloperToolsDeploymentReceiptArtifact> Artifacts { get; init; } = [];

    public ImmutableArray<string> ValidationResults { get; init; } = [];

    public ImmutableArray<string> Warnings { get; init; } = [];

    [JsonIgnore]
    public string ProjectRoot { get; init; } = string.Empty;

    [JsonIgnore]
    public string ManifestPath { get; init; } = string.Empty;
}

public sealed record Dl1DeveloperToolsDeploymentResult(
    Dl1DeveloperToolsDeploymentPlan Plan,
    Dl1DeveloperToolsDeploymentReceipt Receipt,
    string ReceiptPath);

public sealed class Dl1DeveloperToolsDeploymentConflictException : InvalidOperationException
{
    public Dl1DeveloperToolsDeploymentConflictException(Dl1DeveloperToolsDeploymentPlan plan)
        : base(BuildMessage(plan))
    {
        Plan = plan;
    }

    public Dl1DeveloperToolsDeploymentPlan Plan { get; }

    private static string BuildMessage(Dl1DeveloperToolsDeploymentPlan plan)
    {
        string conflicts = string.Join(
            "; ",
            plan.Conflicts.Take(4).Select(static conflict => conflict.Message));
        return $"Developer Tools deployment has {plan.Conflicts.Length:N0} unresolved conflict(s): {conflicts}";
    }
}

/// <summary>
/// Builds an entire Developer Tools deployment outside the selected project,
/// validates all compiler products and shared material state, then commits the
/// canonical data/assets_pc tree as one recoverable transaction.
/// </summary>
public static class Dl1DeveloperToolsProjectDeployer
{
    private const int MaximumReceiptCount = 4096;
    private const int MaximumReceiptArtifactCount = 16_384;
    private const int MaximumTransactionCount = 64;
    private const int MaximumDuplicateScanFiles = 250_000;
    private const string ReceiptFormat = "dl-reanimated-developer-tools-deployment";
    private const string TransactionFormat = "dl-reanimated-developer-tools-transaction";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Regex AnimationScriptAliasDirective = new(
        "^[ \\t]*AnimScriptAlias[ \\t]*\\([ \\t]*\"(?<alias>[^\"\\r\\n]*)\"[ \\t]*\\)[ \\t]*;?[ \\t]*(?://.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public static string NormalizeCharacterId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string candidate = value.Trim();
        if (candidate is "." or ".." ||
            Path.IsPathRooted(candidate) ||
            candidate.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ArgumentException(
                "Character ID must be one safe directory component.",
                nameof(value));
        }

        string normalized = Dl1SourceModelWriter.SanitizeName(candidate, 55).ToLowerInvariant();
        if (normalized is "." or ".." || normalized.Length == 0)
        {
            throw new ArgumentException(
                "Character ID must be one safe directory component.",
                nameof(value));
        }

        return normalized;
    }

    public static async Task<Dl1DeveloperToolsDeploymentPlan> PreflightAsync(
        Dl1DeveloperToolsDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedRequest validated = ValidateRequest(request);
        string jobDirectory = CreateJobDirectory("preflight");
        try
        {
            PreparedDeployment prepared = await PrepareSourceArtifactsAsync(
                request,
                validated,
                jobDirectory,
                includeCompiledPlaceholders: true,
                cancellationToken).ConfigureAwait(false);
            return CreatePlan(request, validated, prepared.Artifacts, prepared.Library, cancellationToken);
        }
        finally
        {
            DeleteOwnedDirectory(jobDirectory);
        }
    }

    public static async Task<Dl1DeveloperToolsDeploymentResult> DeployAsync(
        Dl1DeveloperToolsDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedRequest validated = ValidateRequest(request);
        await using ProjectOperationLock projectLock = await AcquireProjectOperationLockAsync(
            validated.ProjectRoot,
            cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedTransactionsLockedAsync(
            validated.ProjectRoot,
            cancellationToken).ConfigureAwait(false);
        string jobDirectory = CreateJobDirectory("deploy");
        try
        {
            PreparedDeployment source = await PrepareSourceArtifactsAsync(
                request,
                validated,
                jobDirectory,
                includeCompiledPlaceholders: false,
                cancellationToken).ConfigureAwait(false);

            string compilerOutput = Path.Combine(jobDirectory, "model-compiler");
            Directory.CreateDirectory(compilerOutput);
            string existingMaterialDatabase = Path.Combine(validated.ProjectRoot, "assets_pc", "local_dx11.mp");
            MaterialDatabaseSnapshot? materialDatabaseSnapshot =
                await SnapshotMaterialDatabaseAsync(
                    existingMaterialDatabase,
                    jobDirectory,
                    cancellationToken).ConfigureAwait(false);
            var modelCompilerRequest = new Dl1OfficialModelCompilerRequest
            {
                Model = request.Model,
                CompilerExecutablePath = validated.CompilerExecutablePath,
                RetailData0PakPath = validated.RetailData0PakPath,
                OutputRpackPath = Path.Combine(compilerOutput, $"{validated.ModelResourceName}_pc.rpack"),
                CharacterId = validated.CharacterId,
                ResourceName = validated.ModelResourceName,
                SurfaceName = validated.SurfaceName,
                AnimationScriptAlias = validated.AnimationLibraryName,
                ExistingMaterialDatabasePath = materialDatabaseSnapshot?.SnapshotPath,
                Timeout = request.CompilerTimeout,
            };
            Dl1OfficialModelCompilerResult modelCompiler = await (
                request.ModelCompilerOverride?.Invoke(modelCompilerRequest, cancellationToken) ??
                Dl1OfficialModelCompiler.CompileAsync(modelCompilerRequest, cancellationToken)).ConfigureAwait(false);

            string animationCompilerOutput = Path.Combine(jobDirectory, "animation-compiler");
            var animationCompilerRequest = new Dl1OfficialAnimationCompilerRequest
            {
                Library = source.Library,
                CompilerExecutablePath = validated.CompilerExecutablePath,
                RetailData0PakPath = validated.RetailData0PakPath,
                OutputDirectory = animationCompilerOutput,
                Timeout = request.CompilerTimeout,
            };
            Dl1OfficialAnimationCompilerResult animationCompiler = await (
                request.AnimationCompilerOverride?.Invoke(animationCompilerRequest, cancellationToken) ??
                Dl1OfficialModelCompiler.CompileAnimationsAsync(animationCompilerRequest, cancellationToken))
                .ConfigureAwait(false);

            await EnsureMaterialDatabaseSnapshotIsCurrentAsync(
                existingMaterialDatabase,
                materialDatabaseSnapshot,
                cancellationToken).ConfigureAwait(false);

            ImmutableArray<StagedArtifact> completeArtifacts = AddCompiledArtifacts(
                request,
                validated,
                source.Artifacts,
                modelCompiler,
                animationCompiler);
            string deploymentId = CreateDeploymentId(request, validated, source.Library);
            string receiptRelativePath = NormalizeRelativePath(
                $".dl-reanimated/deployments/{deploymentId}.json");
            Dl1DeveloperToolsDeploymentPlan plan = CreatePlan(
                request,
                validated,
                completeArtifacts,
                source.Library,
                cancellationToken,
                receiptRelativePath);
            if (!plan.CanDeploy)
            {
                throw new Dl1DeveloperToolsDeploymentConflictException(plan);
            }

            ValidatePreparedDeployment(validated, completeArtifacts, source.Library);
            return await CommitAsync(
                request,
                validated,
                plan,
                completeArtifacts,
                source.Library,
                modelCompiler.CompilerFingerprint,
                animationCompiler.CompilerFingerprint,
                deploymentId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteOwnedDirectory(jobDirectory);
        }
    }

    public static async Task RollbackAsync(
        string receiptPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        string fullReceiptPath = Path.GetFullPath(receiptPath);
        string? deploymentsDirectory = Path.GetDirectoryName(fullReceiptPath);
        string? metadataDirectory = deploymentsDirectory is null
            ? null
            : Path.GetDirectoryName(deploymentsDirectory);
        string? projectRoot = metadataDirectory is null
            ? null
            : Path.GetDirectoryName(metadataDirectory);
        if (projectRoot is null ||
            !string.Equals(Path.GetFileName(deploymentsDirectory), "deployments", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(metadataDirectory), ".dl-reanimated", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A deployment receipt must remain under <project>/.dl-reanimated/deployments.");
        }

        string receiptRelativePath = NormalizeRelativePath(
            Path.GetRelativePath(projectRoot, fullReceiptPath));
        string resolvedReceiptPath = ResolveProjectPath(projectRoot, receiptRelativePath);
        if (!string.Equals(resolvedReceiptPath, fullReceiptPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The deployment receipt resolves outside its canonical project location.");
        }

        await using ProjectOperationLock projectLock = await AcquireProjectOperationLockAsync(
            projectRoot,
            cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedTransactionsLockedAsync(projectRoot, cancellationToken).ConfigureAwait(false);

        Dl1DeveloperToolsDeploymentReceipt receipt = await ReadReceiptAsync(
            fullReceiptPath,
            projectRoot,
            cancellationToken).ConfigureAwait(false);
        if (receipt.RolledBackUtc is not null)
        {
            throw new InvalidOperationException("This Developer Tools deployment has already been rolled back.");
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
            if (!File.Exists(backup))
            {
                throw new InvalidDataException(
                    $"Rollback backup '{artifact.BackupRelativePath}' is missing.");
            }

            string backupHash = Sha256File(backup);
            if (!HashesEqual(backupHash, artifact.PreviousSha256))
            {
                throw new InvalidDataException(
                    $"Rollback backup '{artifact.BackupRelativePath}' does not match its recorded hash.");
            }
        }

        string rollbackDirectory = CreateJobDirectory("rollback");
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
                    string backup = ResolveProjectPath(projectRoot, artifact.BackupRelativePath);
                    if (!File.Exists(backup))
                    {
                        throw new InvalidDataException(
                            $"Rollback backup '{artifact.BackupRelativePath}' is missing.");
                    }

                    await PublishFileAtomicallyAsync(backup, destination, cancellationToken).ConfigureAwait(false);
                }
            }

            Dl1DeveloperToolsDeploymentReceipt rolledBack = receipt with
            {
                RolledBackUtc = DateTimeOffset.UtcNow,
                ProjectRoot = string.Empty,
                ManifestPath = string.Empty,
            };
            await WriteJsonDurablyAsync(rolledBack, fullReceiptPath, cancellationToken).ConfigureAwait(false);
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
                        string destination = ResolveProjectPath(projectRoot, artifact.RelativePath);
                        await PublishFileAtomicallyAsync(
                            snapshot,
                            destination,
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
                "Deployment rollback failed and one or more deployed files could not be restored to their pre-rollback state.",
                restoreErrors);
        }
        finally
        {
            DeleteOwnedDirectory(rollbackDirectory);
        }
    }

    public static async Task<Dl1DeveloperToolsLegacyBackupResult> BackUpLegacyOutputsAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string validatedRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(validatedRoot))
        {
            throw new DirectoryNotFoundException(
                $"Developer Tools project directory does not exist: {validatedRoot}");
        }

        RejectReparsePoint(validatedRoot);
        await using ProjectOperationLock projectLock = await AcquireProjectOperationLockAsync(
            validatedRoot,
            cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedTransactionsLockedAsync(validatedRoot, cancellationToken).ConfigureAwait(false);
        ImmutableArray<string> legacyPaths = FindLegacyOutputPaths(validatedRoot);
        if (legacyPaths.IsEmpty)
        {
            throw new InvalidOperationException(
                "No legacy nested Developer Tools output directories were found.");
        }

        string backupId = $"legacy-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        string backupRootRelative = NormalizeRelativePath(
            $".dl-reanimated/legacy-backups/{backupId}");
        var moved = new Stack<(string Source, string Destination)>();
        try
        {
            foreach (string relativePath in legacyPaths
                         .OrderByDescending(static path => path.Count(static character => character == '/'))
                         .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = ResolveProjectPath(validatedRoot, relativePath);
                if (!Directory.Exists(source))
                {
                    continue;
                }

                RejectReparsePoint(source);
                string destination = ResolveProjectPath(
                    validatedRoot,
                    $"{backupRootRelative}/{relativePath}");
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw new IOException(
                        $"Legacy backup destination already exists: {Path.GetRelativePath(validatedRoot, destination)}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(source, destination);
                moved.Push((source, destination));
            }

            string rootForRelativePaths = validatedRoot;
            return new Dl1DeveloperToolsLegacyBackupResult(
                backupId,
                backupRootRelative,
                moved.Select(item => NormalizeRelativePath(
                        Path.GetRelativePath(rootForRelativePaths, item.Source)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray());
        }
        catch
        {
            var restoreErrors = new List<Exception>();
            while (moved.TryPop(out (string Source, string Destination) item))
            {
                try
                {
                    if (Directory.Exists(item.Destination) && !Directory.Exists(item.Source))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(item.Source)!);
                        Directory.Move(item.Destination, item.Source);
                    }
                }
                catch (Exception exception)
                {
                    restoreErrors.Add(exception);
                }
            }

            if (restoreErrors.Count > 0)
            {
                throw new AggregateException(
                    "Legacy-output backup failed and one or more directories could not be restored.",
                    restoreErrors);
            }

            throw;
        }
    }

    public static Dl1DeveloperToolsDeploymentReceipt? LoadLatestActiveReceipt(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string validatedRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(validatedRoot))
        {
            return null;
        }

        RejectReparsePoint(validatedRoot);
        string directory = ResolveProjectPath(validatedRoot, ".dl-reanimated/deployments");
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string[] receiptPaths = EnumerateReceiptPaths(directory);
        Dl1DeveloperToolsDeploymentReceipt? latest = null;
        string? latestPath = null;
        foreach (string receiptPath in receiptPaths)
        {
            try
            {
                RejectReparsePoint(receiptPath);
                var info = new FileInfo(receiptPath);
                if (info.Length is <= 0 or > 16 * 1024 * 1024)
                {
                    continue;
                }

                Dl1DeveloperToolsDeploymentReceipt receipt = ReadAndValidateReceipt(
                    receiptPath,
                    validatedRoot);
                if (receipt.RolledBackUtc is not null ||
                    latest is not null && receipt.CompletedUtc <= latest.CompletedUtc)
                {
                    continue;
                }

                latest = receipt;
                latestPath = receiptPath;
            }
            catch (Exception exception) when (
                exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Invalid receipts never enable open or rollback actions.
            }
        }

        return latest is null
            ? null
            : latest with
            {
                ProjectRoot = validatedRoot,
                ManifestPath = latestPath!,
            };
    }

    private static async Task<PreparedDeployment> PrepareSourceArtifactsAsync(
        Dl1DeveloperToolsDeploymentRequest request,
        ValidatedRequest validated,
        string jobDirectory,
        bool includeCompiledPlaceholders,
        CancellationToken cancellationToken)
    {
        string modelSourceDirectory = Path.Combine(jobDirectory, "model-source");
        var sourceRequest = new Dl1SourceModelBuildRequest
        {
            Model = request.Model,
            OutputDirectory = modelSourceDirectory,
            ResourceName = validated.ModelResourceName,
            SurfaceName = validated.SurfaceName,
            AnimationScriptAlias = validated.AnimationLibraryName,
        };
        Dl1SourceModelBuildResult source = await (
            request.SourceWriterOverride?.Invoke(sourceRequest, cancellationToken) ??
            Dl1SourceModelWriter.WriteAsync(sourceRequest, cancellationToken)).ConfigureAwait(false);
        if (source.AnimationScriptPath is null || !File.Exists(source.AnimationScriptPath))
        {
            throw new InvalidDataException("The source writer did not produce the required model ASCR redirect.");
        }

        PreparedCustomModelAnimationLibrary library = await CustomModelAnimationLibraryExporter.PrepareAsync(
            new CustomModelAnimationLibraryRequest
            {
                Model = request.Model,
                OutputPath = Path.Combine(jobDirectory, "unused.rpack"),
                AnimationScriptAlias = validated.AnimationLibraryName,
                Selections = request.AnimationSelections,
            },
            cancellationToken).ConfigureAwait(false);

        var artifacts = ImmutableArray.CreateBuilder<StagedArtifact>();
        foreach (string sourcePath in Directory.EnumerateFiles(modelSourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(sourcePath);
            if (fileName is "model-build.json" or "BLOCKED_OUTPUTS.txt")
            {
                continue;
            }

            artifacts.Add(new StagedArtifact(
                NormalizeRelativePath($"data/characters/{validated.CharacterId}/{fileName}"),
                Dl1DeploymentArtifactRole.Source,
                sourcePath,
                Required: true,
                $"Model source {fileName}"));
        }

        string animationSourceDirectory = Path.Combine(jobDirectory, "animation-source");
        Directory.CreateDirectory(animationSourceDirectory);
        string scriptPath = Path.Combine(animationSourceDirectory, $"{validated.AnimationLibraryName}.scr");
        await File.WriteAllTextAsync(
            scriptPath,
            library.LooseScriptText,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        artifacts.Add(new StagedArtifact(
            NormalizeRelativePath($"data/characters/animations/animscripts/{validated.AnimationLibraryName}.scr"),
            Dl1DeploymentArtifactRole.Source,
            scriptPath,
            Required: true,
            "Loose animation script"));

        foreach (PreparedCustomModelAnimation animation in library.Animations)
        {
            string animationPath = Path.Combine(animationSourceDirectory, animation.Anm2FileName);
            await File.WriteAllBytesAsync(animationPath, animation.Payload, cancellationToken).ConfigureAwait(false);
            if (request.InstallLooseAnm2)
            {
                artifacts.Add(new StagedArtifact(
                    NormalizeRelativePath($"data/characters/animations/{animation.Anm2FileName}"),
                    Dl1DeploymentArtifactRole.Source,
                    animationPath,
                    Required: false,
                    "Optional loose ANM2 source"));
            }
        }

        if (request.ExportPortableAnimationRpack)
        {
            string portablePath = Path.Combine(
                jobDirectory,
                $"{validated.AnimationLibraryName}_pc.rpack");
            byte[] portableBytes = library.BuildPortableRpack();
            await File.WriteAllBytesAsync(portablePath, portableBytes, cancellationToken).ConfigureAwait(false);
            await ValidatePortableRpackAsync(portablePath, library, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new StagedArtifact(
                NormalizeRelativePath(
                    $"out/ReAnimated/{validated.ModelResourceName}/{validated.AnimationLibraryName}_pc.rpack"),
                Dl1DeploymentArtifactRole.PortableOnly,
                portablePath,
                Required: false,
                "Portable animation RPack (not automatically mounted by Developer Tools)"));
        }

        if (includeCompiledPlaceholders)
        {
            artifacts.Add(StagedArtifact.Placeholder(
                NormalizeRelativePath($"assets_pc/characters/{validated.CharacterId}/{validated.ModelResourceName}.msh_obj"),
                Dl1DeploymentArtifactRole.Compiled,
                required: true,
                "Official compiler model object"));
            foreach (string textureFile in source.TextureSourceFiles)
            {
                artifacts.Add(StagedArtifact.Placeholder(
                    NormalizeRelativePath($"assets_pc/characters/{validated.CharacterId}/{Path.GetFileName(textureFile)}_obj"),
                    Dl1DeploymentArtifactRole.Compiled,
                    required: true,
                    "Official compiler texture object"));
            }

            string existingMaterialDatabase = Path.Combine(
                validated.ProjectRoot,
                "assets_pc",
                "local_dx11.mp");
            if (source.CustomMaterialReferences.Length > 0 || File.Exists(existingMaterialDatabase))
            {
                artifacts.Add(StagedArtifact.Placeholder(
                    "assets_pc/local_dx11.mp",
                    Dl1DeploymentArtifactRole.Shared,
                    required: true,
                    "Shared material database updated or preserved by the official compiler"));
            }
            foreach (PreparedCustomModelAnimation animation in library.Animations)
            {
                artifacts.Add(StagedArtifact.Placeholder(
                    NormalizeRelativePath($"assets_pc/characters/animations/{animation.Name}.anm2_obj"),
                    Dl1DeploymentArtifactRole.Compiled,
                    required: true,
                    "Official compiler animation object"));
            }

            artifacts.Add(StagedArtifact.Placeholder(
                NormalizeRelativePath($".dl-reanimated/deployments/<deployment-id>.json"),
                Dl1DeploymentArtifactRole.ManifestOwned,
                required: true,
                "Deployment receipt"));
        }

        return new PreparedDeployment(source, library, artifacts.ToImmutable());
    }

    private static ImmutableArray<StagedArtifact> AddCompiledArtifacts(
        Dl1DeveloperToolsDeploymentRequest request,
        ValidatedRequest validated,
        ImmutableArray<StagedArtifact> sourceArtifacts,
        Dl1OfficialModelCompilerResult modelCompiler,
        Dl1OfficialAnimationCompilerResult animationCompiler)
    {
        var artifacts = sourceArtifacts.ToBuilder();
        artifacts.Add(new StagedArtifact(
            NormalizeRelativePath($"assets_pc/characters/{validated.CharacterId}/{validated.ModelResourceName}.msh_obj"),
            Dl1DeploymentArtifactRole.Compiled,
            modelCompiler.CompiledMeshObjectPath,
            Required: true,
            "Official compiler model object"));
        foreach (string textureObjectPath in modelCompiler.CompiledTextureObjectPaths)
        {
            artifacts.Add(new StagedArtifact(
                NormalizeRelativePath($"assets_pc/characters/{validated.CharacterId}/{Path.GetFileName(textureObjectPath)}"),
                Dl1DeploymentArtifactRole.Compiled,
                textureObjectPath,
                Required: true,
                "Official compiler texture object"));
        }

        bool materialDatabaseRequired = sourceArtifacts.Any(static artifact =>
                artifact.RelativePath.EndsWith(".dmt", StringComparison.OrdinalIgnoreCase)) ||
            File.Exists(Path.Combine(validated.ProjectRoot, "assets_pc", "local_dx11.mp"));
        if (materialDatabaseRequired &&
            (modelCompiler.MaterialDatabasePath is null || !File.Exists(modelCompiler.MaterialDatabasePath)))
        {
            throw new InvalidDataException(
                "The official compiler did not return the required staged local_dx11.mp material database.");
        }

        if (modelCompiler.MaterialDatabasePath is not null && File.Exists(modelCompiler.MaterialDatabasePath))
        {
            artifacts.Add(new StagedArtifact(
                "assets_pc/local_dx11.mp",
                Dl1DeploymentArtifactRole.Shared,
                modelCompiler.MaterialDatabasePath,
                Required: true,
                "Shared material database updated or preserved by the official compiler"));
        }
        foreach ((string name, string path) in animationCompiler.CompiledObjectPaths)
        {
            artifacts.Add(new StagedArtifact(
                NormalizeRelativePath($"assets_pc/characters/animations/{name}.anm2_obj"),
                Dl1DeploymentArtifactRole.Compiled,
                path,
                Required: true,
                "Official compiler animation object"));
        }

        return artifacts.ToImmutable();
    }

    private static Dl1DeveloperToolsDeploymentPlan CreatePlan(
        Dl1DeveloperToolsDeploymentRequest request,
        ValidatedRequest validated,
        ImmutableArray<StagedArtifact> stagedArtifacts,
        PreparedCustomModelAnimationLibrary library,
        CancellationToken cancellationToken,
        string? receiptRelativePath = null)
    {
        ImmutableArray<StagedArtifact> effectiveArtifacts = receiptRelativePath is null
            ? stagedArtifacts
            : stagedArtifacts.Add(StagedArtifact.Placeholder(
                receiptRelativePath,
                Dl1DeploymentArtifactRole.ManifestOwned,
                required: true,
                "Deployment receipt"));
        string? duplicateDestination = effectiveArtifacts
            .GroupBy(static artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .FirstOrDefault();
        if (duplicateDestination is not null)
        {
            throw new InvalidDataException(
                $"Deployment preparation produced duplicate destination '{duplicateDestination}'.");
        }

        Dictionary<string, string> owned = LoadOwnedHashes(validated.ProjectRoot, cancellationToken);
        var artifacts = ImmutableArray.CreateBuilder<Dl1DeveloperToolsDeploymentArtifact>();
        var conflicts = ImmutableArray.CreateBuilder<Dl1DeveloperToolsDeploymentConflict>();
        foreach (StagedArtifact staged in effectiveArtifacts
                     .OrderBy(static artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = ResolveProjectPath(validated.ProjectRoot, staged.RelativePath);
            string? existingHash = File.Exists(destination) ? Sha256File(destination) : null;
            string? stagedHash = staged.StagedPath is not null && File.Exists(staged.StagedPath)
                ? Sha256File(staged.StagedPath)
                : null;
            bool isOwned = existingHash is not null &&
                owned.TryGetValue(staged.RelativePath, out string? ownedHash) &&
                string.Equals(existingHash, ownedHash, StringComparison.OrdinalIgnoreCase);
            Dl1DeploymentArtifactDisposition disposition;
            if (existingHash is null)
            {
                disposition = Dl1DeploymentArtifactDisposition.Create;
            }
            else if (stagedHash is not null &&
                     string.Equals(existingHash, stagedHash, StringComparison.OrdinalIgnoreCase))
            {
                disposition = Dl1DeploymentArtifactDisposition.Unchanged;
            }
            else if (isOwned)
            {
                disposition = Dl1DeploymentArtifactDisposition.UpdateOwned;
            }
            else
            {
                request.ConflictResolutions.TryGetValue(staged.RelativePath, out Dl1DeploymentConflictResolution resolution);
                disposition = resolution switch
                {
                    Dl1DeploymentConflictResolution.BackUpAndReplace =>
                        Dl1DeploymentArtifactDisposition.ReplaceUnowned,
                    Dl1DeploymentConflictResolution.Skip when !staged.Required =>
                        Dl1DeploymentArtifactDisposition.Skip,
                    Dl1DeploymentConflictResolution.Cancel => throw new OperationCanceledException(
                        $"Deployment was canceled at conflict '{staged.RelativePath}'."),
                    _ => Dl1DeploymentArtifactDisposition.Conflict,
                };
                if (disposition == Dl1DeploymentArtifactDisposition.Conflict)
                {
                    string reason = resolution == Dl1DeploymentConflictResolution.Skip && staged.Required
                        ? "is required and cannot be skipped"
                        : "is not owned by this deployment";
                    conflicts.Add(new Dl1DeveloperToolsDeploymentConflict(
                        staged.RelativePath,
                        $"Existing file '{staged.RelativePath}' {reason}.",
                        CanSkip: !staged.Required));
                }
            }

            artifacts.Add(new Dl1DeveloperToolsDeploymentArtifact(
                staged.RelativePath,
                staged.Role,
                disposition,
                staged.Required,
                isOwned,
                existingHash,
                stagedHash,
                staged.Description));
        }

        ImmutableArray<string> legacyPaths = FindLegacyOutputPaths(validated.ProjectRoot);
        ImmutableArray<string> legacy = legacyPaths
            .Select(relative =>
                $"Legacy or unmounted output directory '{relative}' was detected. Use Back up legacy output before relying on the canonical deployment.")
            .ToImmutableArray();
        ImmutableArray<string> duplicates = FindDuplicateResources(
            validated.ProjectRoot,
            effectiveArtifacts.Select(static artifact => artifact.RelativePath).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        ImmutableArray<Dl1DeveloperToolsDeploymentAnimation> animations = library.Animations
            .Select(animation =>
            {
                AnimationScrSequence sequence = library.Sequences.Single(candidate =>
                    string.Equals(candidate.Name, animation.Name, StringComparison.OrdinalIgnoreCase));
                return new Dl1DeveloperToolsDeploymentAnimation(
                    animation.SourceName,
                    animation.Name,
                    animation.Anm2FileName,
                    checked((int)sequence.StartFrame),
                    checked((int)sequence.EndFrame),
                    checked((int)sequence.FramesPerSecond),
                    BuildSeqTrackPreview(sequence));
            })
            .ToImmutableArray();
        return new Dl1DeveloperToolsDeploymentPlan(
            validated.CharacterId,
            validated.ModelResourceName,
            validated.AnimationLibraryName,
            NormalizeRelativePath($"data/characters/animations/animscripts/{validated.AnimationLibraryName}.scr"),
            artifacts.ToImmutable(),
            conflicts.ToImmutable(),
            duplicates,
            legacy,
            library.Animations.Select(static animation => animation.Name).ToImmutableArray(),
            request.InstallLooseAnm2,
            request.ExportPortableAnimationRpack)
        {
            Animations = animations,
            LegacyOutputPaths = legacyPaths,
            PortableRpackRelativePath = request.ExportPortableAnimationRpack
                ? NormalizeRelativePath(
                    $"out/ReAnimated/{validated.ModelResourceName}/{validated.AnimationLibraryName}_pc.rpack")
                : null,
        };
    }

    private static async Task<Dl1DeveloperToolsDeploymentResult> CommitAsync(
        Dl1DeveloperToolsDeploymentRequest request,
        ValidatedRequest validated,
        Dl1DeveloperToolsDeploymentPlan plan,
        ImmutableArray<StagedArtifact> stagedArtifacts,
        PreparedCustomModelAnimationLibrary library,
        string modelCompilerFingerprint,
        string animationCompilerFingerprint,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        string backupRootRelative = NormalizeRelativePath(
            $".dl-reanimated/backups/{deploymentId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
        string backupDeploymentRootRelative = NormalizeRelativePath(
            $".dl-reanimated/backups/{deploymentId}");
        string receiptRelative = NormalizeRelativePath(
            $".dl-reanimated/deployments/{deploymentId}.json");
        string transactionRelative = NormalizeRelativePath(
            $".dl-reanimated/transactions/{deploymentId}.json");
        string transactionPath = ResolveProjectPath(validated.ProjectRoot, transactionRelative);
        var committed = new Stack<CommittedArtifact>();
        var transactionArtifacts = ImmutableArray.CreateBuilder<DeploymentTransactionArtifact>();
        try
        {
            foreach (StagedArtifact staged in stagedArtifacts
                         .OrderBy(static artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                Dl1DeveloperToolsDeploymentArtifact planned = plan.Artifacts.First(
                    artifact => string.Equals(
                        artifact.RelativePath,
                        staged.RelativePath,
                        StringComparison.OrdinalIgnoreCase));
                if (planned.Disposition is Dl1DeploymentArtifactDisposition.Skip or
                    Dl1DeploymentArtifactDisposition.Unchanged)
                {
                    continue;
                }

                string destination = ResolveProjectPath(validated.ProjectRoot, staged.RelativePath);
                string? currentDestinationHash = File.Exists(destination)
                    ? await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false)
                    : null;
                if (!HashesEqual(currentDestinationHash, planned.ExistingSha256))
                {
                    throw new InvalidOperationException(
                        $"Deployment destination '{staged.RelativePath}' changed after preflight. " +
                        "The selected project was not modified by this deployment.");
                }

                if (staged.StagedPath is null || !File.Exists(staged.StagedPath))
                {
                    throw new InvalidDataException(
                        $"Prepared deployment artifact '{staged.RelativePath}' is missing.");
                }

                string currentPreparedHash = await Sha256FileAsync(
                    staged.StagedPath,
                    cancellationToken).ConfigureAwait(false);
                if (!HashesEqual(currentPreparedHash, planned.PreparedSha256))
                {
                    throw new InvalidOperationException(
                        $"Prepared deployment artifact '{staged.RelativePath}' changed before commit.");
                }

                string? backupRelative = currentDestinationHash is null
                    ? null
                    : NormalizeRelativePath($"{backupRootRelative}/{staged.RelativePath}");
                transactionArtifacts.Add(new DeploymentTransactionArtifact(
                    staged.RelativePath,
                    currentDestinationHash,
                    currentPreparedHash,
                    backupRelative,
                    DeploymentTransactionArtifactState.Pending));
            }

            DeploymentTransactionJournal transaction = new()
            {
                DeploymentId = deploymentId,
                ReceiptRelativePath = receiptRelative,
                CreatedUtc = DateTimeOffset.UtcNow,
                Artifacts = transactionArtifacts.ToImmutable(),
            };
            await WriteJsonDurablyAsync(transaction, transactionPath, cancellationToken).ConfigureAwait(false);

            foreach (StagedArtifact staged in stagedArtifacts
                         .OrderBy(static artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                Dl1DeveloperToolsDeploymentArtifact planned = plan.Artifacts.First(
                    artifact => string.Equals(
                        artifact.RelativePath,
                        staged.RelativePath,
                        StringComparison.OrdinalIgnoreCase));
                string destination = ResolveProjectPath(validated.ProjectRoot, staged.RelativePath);
                string? currentDestinationHash = File.Exists(destination)
                    ? await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false)
                    : null;
                if (planned.Disposition != Dl1DeploymentArtifactDisposition.Skip &&
                    !HashesEqual(currentDestinationHash, planned.ExistingSha256))
                {
                    throw new InvalidOperationException(
                        $"Deployment destination '{staged.RelativePath}' changed after preflight. " +
                        "The selected project was not modified by this deployment.");
                }

                if (staged.StagedPath is not null && File.Exists(staged.StagedPath))
                {
                    string currentPreparedHash = await Sha256FileAsync(
                        staged.StagedPath,
                        cancellationToken).ConfigureAwait(false);
                    if (!HashesEqual(currentPreparedHash, planned.PreparedSha256))
                    {
                        throw new InvalidOperationException(
                            $"Prepared deployment artifact '{staged.RelativePath}' changed before commit.");
                    }
                }

                if (planned.Disposition is Dl1DeploymentArtifactDisposition.Skip or
                    Dl1DeploymentArtifactDisposition.Unchanged)
                {
                    continue;
                }

                if (staged.StagedPath is null || !File.Exists(staged.StagedPath))
                {
                    throw new InvalidDataException(
                        $"Prepared deployment artifact '{staged.RelativePath}' is missing.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                string? previousHash = currentDestinationHash;
                string? backupRelative = null;
                if (previousHash is not null)
                {
                    backupRelative = NormalizeRelativePath($"{backupRootRelative}/{staged.RelativePath}");
                    string backup = ResolveProjectPath(validated.ProjectRoot, backupRelative);
                    await PublishFileAtomicallyAsync(destination, backup, cancellationToken).ConfigureAwait(false);
                    string backupHash = await Sha256FileAsync(backup, cancellationToken).ConfigureAwait(false);
                    if (!HashesEqual(backupHash, previousHash))
                    {
                        throw new InvalidDataException(
                            $"Deployment backup for '{staged.RelativePath}' does not match the preflight content.");
                    }
                }

                transaction = UpdateTransactionArtifactState(
                    transaction,
                    staged.RelativePath,
                    DeploymentTransactionArtifactState.BackupReady);
                await WriteJsonDurablyAsync(transaction, transactionPath, cancellationToken).ConfigureAwait(false);

                string? beforePublishHash = File.Exists(destination)
                    ? await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false)
                    : null;
                if (!HashesEqual(beforePublishHash, previousHash))
                {
                    throw new InvalidOperationException(
                        $"Deployment destination '{staged.RelativePath}' changed while the transaction was being committed.");
                }

                await PublishFileAtomicallyAsync(staged.StagedPath, destination, cancellationToken).ConfigureAwait(false);
                committed.Push(new CommittedArtifact(staged, previousHash, backupRelative));
                string publishedHash = await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!HashesEqual(publishedHash, planned.PreparedSha256))
                {
                    throw new InvalidDataException(
                        $"Published deployment artifact '{staged.RelativePath}' failed hash validation.");
                }
                transaction = UpdateTransactionArtifactState(
                    transaction,
                    staged.RelativePath,
                    DeploymentTransactionArtifactState.Published);
                await WriteJsonDurablyAsync(transaction, transactionPath, cancellationToken).ConfigureAwait(false);
                if (request.ArtifactPublishedObserver is not null)
                {
                    await request.ArtifactPublishedObserver(
                        staged.RelativePath,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var receiptArtifacts = ImmutableArray.CreateBuilder<Dl1DeveloperToolsDeploymentReceiptArtifact>();
            foreach (StagedArtifact staged in stagedArtifacts
                         .OrderBy(static artifact => artifact.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                Dl1DeveloperToolsDeploymentArtifact planned = plan.Artifacts.First(
                    artifact => string.Equals(
                        artifact.RelativePath,
                        staged.RelativePath,
                        StringComparison.OrdinalIgnoreCase));
                if (planned.Disposition == Dl1DeploymentArtifactDisposition.Skip)
                {
                    continue;
                }

                string destination = ResolveProjectPath(validated.ProjectRoot, staged.RelativePath);
                string deployedHash = await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false);
                CommittedArtifact? changed = committed.FirstOrDefault(item =>
                    string.Equals(item.Artifact.RelativePath, staged.RelativePath, StringComparison.OrdinalIgnoreCase));
                receiptArtifacts.Add(new Dl1DeveloperToolsDeploymentReceiptArtifact(
                    staged.RelativePath,
                    staged.Role,
                    deployedHash,
                    changed?.PreviousHash ?? planned.ExistingSha256,
                    changed?.BackupRelativePath,
                    CreatedByDeployment: changed is not null && planned.ExistingSha256 is null,
                    ChangedByDeployment: changed is not null));
            }

            string receiptPath = ResolveProjectPath(validated.ProjectRoot, receiptRelative);
            Dl1DeveloperToolsDeploymentArtifact receiptPlan = plan.Artifacts.Single(artifact =>
                string.Equals(
                    artifact.RelativePath,
                    receiptRelative,
                    StringComparison.OrdinalIgnoreCase));
            string? currentReceiptHash = File.Exists(receiptPath)
                ? await Sha256FileAsync(receiptPath, cancellationToken).ConfigureAwait(false)
                : null;
            if (!HashesEqual(currentReceiptHash, receiptPlan.ExistingSha256))
            {
                throw new InvalidOperationException(
                    $"Deployment receipt destination '{receiptRelative}' changed after preflight.");
            }

            Dl1DeveloperToolsDeploymentReceipt receipt = new()
            {
                DeploymentId = deploymentId,
                CharacterId = validated.CharacterId,
                ModelResourceName = validated.ModelResourceName,
                AnimationLibraryName = validated.AnimationLibraryName,
                AnimationScriptRelativePath = plan.AnimationScriptRelativePath,
                ModelCompilerFingerprint = modelCompilerFingerprint,
                AnimationCompilerFingerprint = animationCompilerFingerprint,
                CompletedUtc = DateTimeOffset.UtcNow,
                Artifacts = receiptArtifacts.ToImmutable(),
                ValidationResults = ImmutableArray.Create(
                    $"Validated model ASCR redirects to {validated.AnimationLibraryName}.scr.",
                    $"Validated {library.Sequences.Length:N0} loose SCR sequence(s) against the same prepared ANM2 inventory.",
                    $"Validated {library.Animations.Length:N0} compiled ANM2 object(s).")
                    .AddRange(plan.Artifacts.Any(static artifact =>
                        artifact.Role == Dl1DeploymentArtifactRole.Shared)
                    ? ImmutableArray.Create(
                        "Validated shared local_dx11.mp preservation through the official material compiler.")
                    : []),
                Warnings = plan.LegacyOutputWarnings
                    .AddRange(plan.StaleDuplicateResources)
                    .AddRange(library.Warnings)
                    .Add(request.ExportPortableAnimationRpack
                        ? "The exported animation RPack is portable-only and is not automatically mounted by Developer Tools."
                        : "Portable animation RPack export was disabled."),
                ProjectRoot = validated.ProjectRoot,
                ManifestPath = receiptPath,
            };
            await WriteJsonDurablyAsync(receipt, receiptPath, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Delete(transactionPath);
            }
            catch (IOException)
            {
                // The durable receipt proves the commit. Startup recovery will remove the stale journal.
            }
            catch (UnauthorizedAccessException)
            {
                // The durable receipt proves the commit. Startup recovery will remove the stale journal.
            }
            return new Dl1DeveloperToolsDeploymentResult(plan, receipt, receiptPath);
        }
        catch (Exception originalException)
        {
            var rollbackErrors = new List<Exception>();
            while (committed.TryPop(out CommittedArtifact? item))
            {
                try
                {
                    string destination = ResolveProjectPath(validated.ProjectRoot, item.Artifact.RelativePath);
                    if (item.BackupRelativePath is null)
                    {
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }
                    }
                    else
                    {
                        string backup = ResolveProjectPath(validated.ProjectRoot, item.BackupRelativePath);
                        if (!File.Exists(backup))
                        {
                            throw new InvalidDataException(
                                $"Deployment rollback backup '{item.BackupRelativePath}' is missing.");
                        }

                        await PublishFileAtomicallyAsync(
                            backup,
                            destination,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException);
                }
            }

            if (rollbackErrors.Count == 0)
            {
                if (File.Exists(transactionPath))
                {
                    File.Delete(transactionPath);
                }
                DeleteOwnedProjectDirectory(validated.ProjectRoot, backupDeploymentRootRelative);
                throw;
            }

            rollbackErrors.Insert(0, originalException);
            throw new AggregateException(
                "Developer Tools deployment failed and one or more prior project files could not be restored.",
                rollbackErrors);
        }
    }

    private static ValidatedRequest ValidateRequest(Dl1DeveloperToolsDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Model);
        request.Model.Package.Document.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        string projectRoot = Path.GetFullPath(request.ProjectRoot);
        if (!Directory.Exists(projectRoot))
        {
            throw new DirectoryNotFoundException(
                $"Developer Tools project directory does not exist: {projectRoot}");
        }

        RejectReparsePoint(projectRoot);

        string compiler = Path.GetFullPath(request.CompilerExecutablePath);
        if (!File.Exists(compiler))
        {
            throw new FileNotFoundException("Developer Tools compiler was not found.", compiler);
        }

        string retailData = Path.GetFullPath(request.RetailData0PakPath);
        if (!File.Exists(retailData))
        {
            throw new FileNotFoundException("Retail Data0.pak was not found.", retailData);
        }

        string characterId = NormalizeCharacterId(request.CharacterId);
        string model = Dl1SourceModelWriter.RequireExactResourceName(
            request.ModelResourceName,
            55,
            "model resource name");
        string surface = Dl1SourceModelWriter.SanitizeName(request.SurfaceName, 63);
        string library = Dl1SourceModelWriter.RequireExactResourceName(
            request.AnimationLibraryName,
            63,
            "animation library/script name");
        if (request.CompilerTimeout <= TimeSpan.Zero || request.CompilerTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Compiler timeout must be between zero and one hour.");
        }

        return new ValidatedRequest(projectRoot, compiler, retailData, characterId, model, surface, library);
    }

    private static void ValidatePreparedDeployment(
        ValidatedRequest request,
        ImmutableArray<StagedArtifact> artifacts,
        PreparedCustomModelAnimationLibrary library)
    {
        string[] requiredPaths =
        [
            $"data/characters/{request.CharacterId}/{request.ModelResourceName}.msh",
            $"data/characters/{request.CharacterId}/{request.ModelResourceName}.chr",
            $"data/characters/{request.CharacterId}/{request.ModelResourceName}.bscr",
            $"data/characters/{request.CharacterId}/{request.ModelResourceName}.ascr",
            $"data/characters/animations/animscripts/{request.AnimationLibraryName}.scr",
            $"assets_pc/characters/{request.CharacterId}/{request.ModelResourceName}.msh_obj",
        ];
        foreach (string relativePath in requiredPaths)
        {
            StagedArtifact? artifact = artifacts.FirstOrDefault(item =>
                string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (artifact?.StagedPath is null || !File.Exists(artifact.StagedPath))
            {
                throw new InvalidDataException(
                    $"Required staged deployment artifact '{relativePath}' is missing.");
            }
        }

        StagedArtifact? materialDatabase = artifacts.FirstOrDefault(static artifact =>
            string.Equals(
                artifact.RelativePath,
                "assets_pc/local_dx11.mp",
                StringComparison.OrdinalIgnoreCase));
        if (materialDatabase is not null &&
            (materialDatabase.StagedPath is null || !File.Exists(materialDatabase.StagedPath)))
        {
            throw new InvalidDataException(
                "The staged shared local_dx11.mp material database is missing.");
        }

        foreach (PreparedCustomModelAnimation animation in library.Animations)
        {
            string relativePath = $"assets_pc/characters/animations/{animation.Name}.anm2_obj";
            if (!artifacts.Any(item =>
                    string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase) &&
                    item.StagedPath is not null &&
                    File.Exists(item.StagedPath)))
            {
                throw new InvalidDataException(
                    $"Required compiled animation object '{relativePath}' is missing.");
            }
        }

        string ascrArtifactPath = artifacts.First(item =>
            string.Equals(
                item.RelativePath,
                $"data/characters/{request.CharacterId}/{request.ModelResourceName}.ascr",
                StringComparison.OrdinalIgnoreCase)).StagedPath!;
        string ascr = File.ReadAllText(ascrArtifactPath);
        string expectedAlias = $"{request.AnimationLibraryName}.scr";
        MatchCollection directives = AnimationScriptAliasDirective.Matches(ascr);
        if (directives.Count != 1 ||
            !string.Equals(
                directives[0].Groups["alias"].Value,
                expectedAlias,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Model ASCR must contain exactly one effective AnimScriptAlias directive " +
                $"redirecting to '{expectedAlias}'.");
        }


        string scrArtifactPath = artifacts.First(item =>
            string.Equals(
                item.RelativePath,
                $"data/characters/animations/animscripts/{request.AnimationLibraryName}.scr",
                StringComparison.OrdinalIgnoreCase)).StagedPath!;
        string actualScript = File.ReadAllText(scrArtifactPath);
        string expectedScript = CustomModelAnimationLibraryExporter.BuildLooseAnimationScript(library.Sequences);
        if (!string.Equals(actualScript, expectedScript, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The staged loose SCR sequence inventory differs from the prepared ANM2 timing inventory.");
        }
    }

    private static async Task ValidatePortableRpackAsync(
        string path,
        PreparedCustomModelAnimationLibrary library,
        CancellationToken cancellationToken)
    {
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(
            path,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (PreparedCustomModelAnimation animation in library.Animations)
        {
            if (!archive.Resources.Any(resource =>
                    resource.ResourceType == Rp6lResourceTypes.Animation &&
                    string.Equals(resource.Name, animation.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    $"Portable animation RPack is missing type-{Rp6lResourceTypes.Animation} '{animation.Name}'.");
            }
        }

        if (!archive.Resources.Any(resource =>
                resource.ResourceType == Rp6lResourceTypes.AnimationScript &&
                string.Equals(resource.Name, library.AnimationScriptName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Portable animation RPack is missing type-{Rp6lResourceTypes.AnimationScript} '{library.AnimationScriptName}'.");
        }
    }

    private static Dictionary<string, string> LoadOwnedHashes(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string directory = ResolveProjectPath(projectRoot, ".dl-reanimated/deployments");
        if (!Directory.Exists(directory))
        {
            return owned;
        }

        var receipts = new List<Dl1DeveloperToolsDeploymentReceipt>();
        foreach (string receiptPath in EnumerateReceiptPaths(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectReparsePoint(receiptPath);
                Dl1DeveloperToolsDeploymentReceipt receipt = ReadAndValidateReceipt(
                    receiptPath,
                    projectRoot);
                if (receipt.RolledBackUtc is not null)
                {
                    continue;
                }

                receipts.Add(receipt);
            }
            catch (JsonException)
            {
                // Malformed receipts never confer ownership.
            }
            catch (IOException)
            {
                // A concurrently replaced receipt never confers ownership.
            }
            catch (InvalidDataException)
            {
                // A receipt that fails strict validation never confers ownership.
            }
            catch (UnauthorizedAccessException)
            {
                // An unreadable receipt never confers ownership.
            }
        }

        foreach (Dl1DeveloperToolsDeploymentReceipt receipt in receipts
                     .OrderBy(static receipt => receipt.CompletedUtc))
        {
            foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in receipt.Artifacts)
            {
                owned[NormalizeRelativePath(artifact.RelativePath)] = artifact.DeployedSha256;
            }
        }

        return owned;
    }

    private static void EnsureNoLaterDeploymentOwnsChangedArtifacts(
        string projectRoot,
        string selectedReceiptPath,
        Dl1DeveloperToolsDeploymentReceipt selectedReceipt,
        CancellationToken cancellationToken)
    {
        ImmutableHashSet<string> selectedPaths = selectedReceipt.Artifacts
            .Where(static artifact => artifact.ChangedByDeployment)
            .Select(static artifact => NormalizeRelativePath(artifact.RelativePath))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        string directory = ResolveProjectPath(projectRoot, ".dl-reanimated/deployments");
        if (!Directory.Exists(directory) || selectedPaths.IsEmpty)
        {
            return;
        }

        foreach (string path in EnumerateReceiptPaths(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(Path.GetFullPath(path), selectedReceiptPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Dl1DeveloperToolsDeploymentReceipt later;
            try
            {
                RejectReparsePoint(path);
                later = ReadAndValidateReceipt(path, projectRoot);
            }
            catch (JsonException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }
            catch (InvalidDataException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (later.RolledBackUtc is not null ||
                later.CompletedUtc <= selectedReceipt.CompletedUtc)
            {
                continue;
            }

            string? overlap = later.Artifacts
                .Select(static artifact => NormalizeRelativePath(artifact.RelativePath))
                .FirstOrDefault(selectedPaths.Contains);
            if (overlap is not null)
            {
                throw new InvalidOperationException(
                    $"Cannot roll back deployment '{selectedReceipt.DeploymentId}' because newer deployment " +
                    $"'{later.DeploymentId}' also owns '{overlap}'. Roll back the newer deployment first.");
            }
        }
    }

    private static ImmutableArray<string> FindLegacyOutputPaths(string projectRoot)
    {
        string[] suspicious =
        [
            "compiled",
            "loose",
            "animations",
            "data/compiled",
            "data/loose",
            "data/animations",
            "compiled/loose/animations",
        ];
        return suspicious
            .Where(relative => Directory.Exists(ResolveProjectPath(projectRoot, relative)))
            .Select(NormalizeRelativePath)
            .Where(relative => !suspicious.Any(other =>
                !string.Equals(relative, NormalizeRelativePath(other), StringComparison.OrdinalIgnoreCase) &&
                relative.StartsWith(NormalizeRelativePath(other) + "/", StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(ResolveProjectPath(projectRoot, other))))
            .ToImmutableArray();
    }

    private static string BuildSeqTrackPreview(AnimationScrSequence sequence) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"SeqTrack( \"{sequence.Name}\", \"{sequence.Anm2Name}\", " +
            $"{sequence.StartFrame:0}, {sequence.EndFrame:0}, {sequence.FramesPerSecond:0}, " +
            $"{sequence.Enabled}, {sequence.Blend:0.###} )");

    private static ImmutableArray<string> FindDuplicateResources(
        string projectRoot,
        ImmutableHashSet<string> canonicalPaths,
        CancellationToken cancellationToken)
    {
        var targetNames = canonicalPaths
            .Where(static path => !path.Contains("<deployment-id>", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicates = ImmutableArray.CreateBuilder<string>();
        int scanned = 0;
        foreach (string rootName in new[] { "data", "assets_pc" })
        {
            string root = Path.Combine(projectRoot, rootName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string path in EnumerateFilesWithoutReparsePoints(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaximumDuplicateScanFiles)
                {
                    duplicates.Add(
                        $"Duplicate-resource scan stopped after {MaximumDuplicateScanFiles:N0} files; review the project manually.");
                    return duplicates.ToImmutable();
                }

                string relative = NormalizeRelativePath(Path.GetRelativePath(projectRoot, path));
                if (targetNames.Contains(Path.GetFileName(path)) && !canonicalPaths.Contains(relative))
                {
                    duplicates.Add($"Duplicate resource outside the canonical destination: {relative}");
                }
            }
        }

        return duplicates.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            RejectReparsePoint(directory);
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                RejectReparsePoint(file);
                yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                RejectReparsePoint(child);
                pending.Push(child);
            }
        }
    }

    private static string CreateDeploymentId(
        Dl1DeveloperToolsDeploymentRequest request,
        ValidatedRequest validated,
        PreparedCustomModelAnimationLibrary library)
    {
        string input = string.Join(
            '\n',
            "dl-reanimated-deployment-v1",
            request.Model.Package.Document.Source.ContentSha256,
            request.Model.Package.Document.RigSignature,
            validated.CharacterId,
            validated.ModelResourceName,
            validated.AnimationLibraryName,
            string.Join(',', library.Animations.Select(static animation => animation.SourceFingerprint)),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("N"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..24];
    }

    private static string CreateJobDirectory(string operation)
    {
        string container = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLReAnimated",
            "DeveloperToolsDeploymentJobs");
        Directory.CreateDirectory(container);
        string directory = Path.Combine(container, $"{operation}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ResolveProjectPath(string projectRoot, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw new InvalidDataException($"Deployment path '{relativePath}' is not project-relative.");
        }

        string fullRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Deployment path '{relativePath}' escapes the selected project.");
        }

        RejectExistingReparsePoints(fullRoot.TrimEnd(Path.DirectorySeparatorChar), fullPath);

        return fullPath;
    }

    private static void RejectExistingReparsePoints(string projectRoot, string fullPath)
    {
        string current = Path.GetFullPath(projectRoot);
        RejectReparsePoint(current);
        string relative = Path.GetRelativePath(current, fullPath);
        foreach (string component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Developer Tools deployment refuses to traverse reparse point '{path}'.");
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<MaterialDatabaseSnapshot?> SnapshotMaterialDatabaseAsync(
        string livePath,
        string jobDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(livePath))
        {
            return null;
        }

        RejectReparsePoint(livePath);
        string originalHash = await Sha256FileAsync(livePath, cancellationToken).ConfigureAwait(false);
        string snapshotPath = Path.Combine(jobDirectory, "existing-local_dx11.mp");
        await PublishFileAtomicallyAsync(livePath, snapshotPath, cancellationToken).ConfigureAwait(false);
        string snapshotHash = await Sha256FileAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        if (!HashesEqual(originalHash, snapshotHash))
        {
            throw new InvalidDataException(
                "The existing Developer Tools material database changed while it was being snapshotted.");
        }

        return new MaterialDatabaseSnapshot(snapshotPath, originalHash);
    }

    private static async Task EnsureMaterialDatabaseSnapshotIsCurrentAsync(
        string livePath,
        MaterialDatabaseSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        string? currentHash = File.Exists(livePath)
            ? await Sha256FileAsync(livePath, cancellationToken).ConfigureAwait(false)
            : null;
        if (!HashesEqual(currentHash, snapshot?.OriginalSha256))
        {
            throw new InvalidOperationException(
                "assets_pc/local_dx11.mp changed while the deployment was being prepared. " +
                "The staged compiler result was discarded; run the deployment again.");
        }
    }

    private static async Task<ProjectOperationLock> AcquireProjectOperationLockAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string lockRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLReAnimated",
            "DeveloperToolsProjectLocks");
        Directory.CreateDirectory(lockRoot);
        string canonicalProject = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalProject)));
        string lockPath = Path.Combine(lockRoot, key + ".lock");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileStream stream = new(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                return new ProjectOperationLock(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static DeploymentTransactionJournal UpdateTransactionArtifactState(
        DeploymentTransactionJournal transaction,
        string relativePath,
        DeploymentTransactionArtifactState state) =>
        transaction with
        {
            Artifacts = transaction.Artifacts
                .Select(artifact => string.Equals(
                    artifact.RelativePath,
                    relativePath,
                    StringComparison.OrdinalIgnoreCase)
                    ? artifact with { State = state }
                    : artifact)
                .ToImmutableArray(),
        };

    private static async Task RecoverInterruptedTransactionsLockedAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string directory = ResolveProjectPath(projectRoot, ".dl-reanimated/transactions");
        if (!Directory.Exists(directory))
        {
            return;
        }

        RejectReparsePoint(directory);
        string[] paths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length > MaximumTransactionCount)
        {
            throw new InvalidDataException(
                $"The project contains more than {MaximumTransactionCount:N0} pending deployment transactions.");
        }

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectReparsePoint(path);
            DeploymentTransactionJournal transaction = await ReadTransactionAsync(
                path,
                projectRoot,
                cancellationToken).ConfigureAwait(false);
            string receiptPath = ResolveProjectPath(projectRoot, transaction.ReceiptRelativePath);
            if (File.Exists(receiptPath))
            {
                Dl1DeveloperToolsDeploymentReceipt receipt = await ReadReceiptAsync(
                    receiptPath,
                    projectRoot,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        receipt.DeploymentId,
                        transaction.DeploymentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Transaction '{transaction.DeploymentId}' does not match its deployment receipt.");
                }

                File.Delete(path);
                continue;
            }

            foreach (DeploymentTransactionArtifact artifact in transaction.Artifacts.Reverse())
            {
                if (artifact.State == DeploymentTransactionArtifactState.Pending)
                {
                    continue;
                }

                string destination = ResolveProjectPath(projectRoot, artifact.RelativePath);
                string? currentHash = File.Exists(destination)
                    ? await Sha256FileAsync(destination, cancellationToken).ConfigureAwait(false)
                    : null;
                if (artifact.PreviousSha256 is null)
                {
                    if (currentHash is null)
                    {
                        continue;
                    }

                    if (!HashesEqual(currentHash, artifact.PreparedSha256))
                    {
                        throw new InvalidOperationException(
                            $"Interrupted deployment recovery stopped because '{artifact.RelativePath}' " +
                            "was modified after the interrupted publish.");
                    }

                    File.Delete(destination);
                    continue;
                }

                if (HashesEqual(currentHash, artifact.PreviousSha256))
                {
                    continue;
                }

                if (!HashesEqual(currentHash, artifact.PreparedSha256))
                {
                    throw new InvalidOperationException(
                        $"Interrupted deployment recovery stopped because '{artifact.RelativePath}' " +
                        "does not match either the old or staged content.");
                }

                if (artifact.BackupRelativePath is null)
                {
                    throw new InvalidDataException(
                        $"Interrupted deployment metadata omits the backup for '{artifact.RelativePath}'.");
                }

                string backup = ResolveProjectPath(projectRoot, artifact.BackupRelativePath);
                if (!File.Exists(backup) ||
                    !HashesEqual(await Sha256FileAsync(backup, cancellationToken).ConfigureAwait(false),
                        artifact.PreviousSha256))
                {
                    throw new InvalidDataException(
                        $"Interrupted deployment backup for '{artifact.RelativePath}' is missing or corrupt.");
                }

                await PublishFileAtomicallyAsync(backup, destination, cancellationToken).ConfigureAwait(false);
            }

            File.Delete(path);
            DeleteOwnedProjectDirectory(
                projectRoot,
                NormalizeRelativePath($".dl-reanimated/backups/{transaction.DeploymentId}"));
        }
    }

    private static async Task PublishFileAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream input = new(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool HashesEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void DeleteOwnedProjectDirectory(string projectRoot, string relativePath)
    {
        string directory = ResolveProjectPath(projectRoot, relativePath);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        T value,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteJsonDurablyAsync<T>(
        T value,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + $".dlr-{Guid.NewGuid():N}.tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string[] EnumerateReceiptPaths(string directory)
    {
        RejectReparsePoint(directory);
        string[] paths = Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length > MaximumReceiptCount)
        {
            throw new InvalidDataException(
                $"The project contains more than {MaximumReceiptCount:N0} deployment receipts.");
        }

        return paths;
    }

    private static Dl1DeveloperToolsDeploymentReceipt ReadAndValidateReceipt(
        string path,
        string projectRoot)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Deployment receipt is empty or exceeds the supported size.");
        }

        Dl1DeveloperToolsDeploymentReceipt receipt = JsonSerializer.Deserialize<Dl1DeveloperToolsDeploymentReceipt>(
            File.ReadAllBytes(path),
            JsonOptions) ?? throw new InvalidDataException("Deployment receipt is empty.");
        ValidateReceipt(receipt, path, projectRoot);
        return receipt;
    }

    private static async Task<Dl1DeveloperToolsDeploymentReceipt> ReadReceiptAsync(
        string path,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Deployment receipt is empty or exceeds the supported size.");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        Dl1DeveloperToolsDeploymentReceipt receipt =
            await JsonSerializer.DeserializeAsync<Dl1DeveloperToolsDeploymentReceipt>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Deployment receipt is empty.");
        ValidateReceipt(receipt, path, projectRoot);
        return receipt;
    }

    private static void ValidateReceipt(
        Dl1DeveloperToolsDeploymentReceipt receipt,
        string path,
        string projectRoot)
    {
        if (receipt.Format != ReceiptFormat || receipt.SchemaVersion != 1)
        {
            throw new InvalidDataException("Deployment receipt format or schema is unsupported.");
        }

        if (!Regex.IsMatch(receipt.DeploymentId ?? string.Empty, "^[0-9a-f]{24}$", RegexOptions.CultureInvariant) ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                receipt.DeploymentId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Deployment receipt ID does not match its canonical receipt filename.");
        }

        string character;
        string model;
        string library;
        try
        {
            character = NormalizeCharacterId(receipt.CharacterId);
            model = Dl1SourceModelWriter.RequireExactResourceName(
                receipt.ModelResourceName,
                55,
                "model resource name");
            library = Dl1SourceModelWriter.RequireExactResourceName(
                receipt.AnimationLibraryName,
                63,
                "animation library/script name");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Deployment receipt contains an invalid resource identity.", exception);
        }

        if (!string.Equals(character, receipt.CharacterId, StringComparison.Ordinal) ||
            !string.Equals(model, receipt.ModelResourceName, StringComparison.Ordinal) ||
            !string.Equals(library, receipt.AnimationLibraryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Deployment receipt resource identities are not canonical.");
        }

        string expectedScript = NormalizeRelativePath(
            $"data/characters/animations/animscripts/{library}.scr");
        if (!string.Equals(
                receipt.AnimationScriptRelativePath,
                expectedScript,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Deployment receipt contains a non-canonical animation script path.");
        }

        if (receipt.Artifacts.IsDefault || receipt.Artifacts.Length > MaximumReceiptArtifactCount)
        {
            throw new InvalidDataException("Deployment receipt has an invalid artifact inventory size.");
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in receipt.Artifacts)
        {
            string relative = ValidateCanonicalProjectRelativePath(
                projectRoot,
                artifact.RelativePath,
                "deployment artifact");
            if (!unique.Add(relative))
            {
                throw new InvalidDataException(
                    $"Deployment receipt contains duplicate artifact '{relative}'.");
            }

            ValidateReceiptArtifactRolePath(artifact.Role, relative, character, model, library);
            RequireSha256(artifact.DeployedSha256, $"deployed hash for '{relative}'");
            if (artifact.PreviousSha256 is not null)
            {
                RequireSha256(artifact.PreviousSha256, $"previous hash for '{relative}'");
            }

            if (!artifact.ChangedByDeployment)
            {
                if (artifact.CreatedByDeployment || artifact.BackupRelativePath is not null ||
                    !HashesEqual(artifact.PreviousSha256, artifact.DeployedSha256))
                {
                    throw new InvalidDataException(
                        $"Unchanged artifact '{relative}' has inconsistent rollback metadata.");
                }

                continue;
            }

            if (artifact.CreatedByDeployment)
            {
                if (artifact.PreviousSha256 is not null || artifact.BackupRelativePath is not null)
                {
                    throw new InvalidDataException(
                        $"Created artifact '{relative}' has inconsistent rollback metadata.");
                }

                continue;
            }

            if (artifact.PreviousSha256 is null || artifact.BackupRelativePath is null)
            {
                throw new InvalidDataException(
                    $"Replaced artifact '{relative}' omits its previous hash or backup.");
            }

            string backup = ValidateCanonicalProjectRelativePath(
                projectRoot,
                artifact.BackupRelativePath,
                "deployment backup");
            string requiredPrefix = NormalizeRelativePath(
                $".dl-reanimated/backups/{receipt.DeploymentId}/");
            if (!backup.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase) ||
                !backup.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Deployment backup for '{relative}' is outside its deployment backup directory.");
            }
        }
    }

    private static void ValidateReceiptArtifactRolePath(
        Dl1DeploymentArtifactRole role,
        string relative,
        string character,
        string model,
        string library)
    {
        bool valid = role switch
        {
            Dl1DeploymentArtifactRole.Source =>
                relative.StartsWith($"data/characters/{character}/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("data/characters/animations/", StringComparison.OrdinalIgnoreCase),
            Dl1DeploymentArtifactRole.Compiled =>
                relative.StartsWith($"assets_pc/characters/{character}/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("assets_pc/characters/animations/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("assets_pc/", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(relative, "assets_pc/local_dx11.mp", StringComparison.OrdinalIgnoreCase),
            Dl1DeploymentArtifactRole.Shared =>
                string.Equals(relative, "assets_pc/local_dx11.mp", StringComparison.OrdinalIgnoreCase),
            Dl1DeploymentArtifactRole.PortableOnly =>
                string.Equals(
                    relative,
                    NormalizeRelativePath($"out/ReAnimated/{model}/{library}_pc.rpack"),
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Deployment artifact '{relative}' is not valid for its recorded role '{role}'.");
        }
    }

    private static string ValidateCanonicalProjectRelativePath(
        string projectRoot,
        string relativePath,
        string purpose)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !string.Equals(relativePath, NormalizeRelativePath(relativePath), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The {purpose} path is not canonical.");
        }

        string resolved = ResolveProjectPath(projectRoot, relativePath);
        string canonical = NormalizeRelativePath(Path.GetRelativePath(projectRoot, resolved));
        if (!string.Equals(canonical, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The {purpose} path '{relativePath}' is not canonical.");
        }

        return relativePath;
    }

    private static void RequireSha256(string value, string purpose)
    {
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Deployment receipt contains an invalid {purpose}.");
        }
    }

    private static async Task<DeploymentTransactionJournal> ReadTransactionAsync(
        string path,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Deployment transaction is empty or exceeds the supported size.");
        }

        await using FileStream stream = File.OpenRead(path);
        DeploymentTransactionJournal transaction =
            await JsonSerializer.DeserializeAsync<DeploymentTransactionJournal>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Deployment transaction is empty.");
        if (transaction.Format != TransactionFormat || transaction.SchemaVersion != 1 ||
            !Regex.IsMatch(transaction.DeploymentId ?? string.Empty, "^[0-9a-f]{24}$", RegexOptions.CultureInvariant) ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(path),
                transaction.DeploymentId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Deployment transaction identity or schema is invalid.");
        }

        string expectedReceipt = NormalizeRelativePath(
            $".dl-reanimated/deployments/{transaction.DeploymentId}.json");
        if (!string.Equals(transaction.ReceiptRelativePath, expectedReceipt, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Deployment transaction receipt path is invalid.");
        }

        if (transaction.Artifacts.IsDefault || transaction.Artifacts.Length > MaximumReceiptArtifactCount)
        {
            throw new InvalidDataException("Deployment transaction artifact inventory is invalid.");
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DeploymentTransactionArtifact artifact in transaction.Artifacts)
        {
            string relative = ValidateCanonicalProjectRelativePath(
                projectRoot,
                artifact.RelativePath,
                "transaction artifact");
            if (!unique.Add(relative))
            {
                throw new InvalidDataException($"Deployment transaction repeats '{relative}'.");
            }

            RequireSha256(artifact.PreparedSha256, $"prepared hash for '{relative}'");
            if (artifact.PreviousSha256 is not null)
            {
                RequireSha256(artifact.PreviousSha256, $"previous hash for '{relative}'");
                if (artifact.BackupRelativePath is null)
                {
                    throw new InvalidDataException(
                        $"Deployment transaction omits the backup for '{relative}'.");
                }

                string backup = ValidateCanonicalProjectRelativePath(
                    projectRoot,
                    artifact.BackupRelativePath,
                    "transaction backup");
                string prefix = NormalizeRelativePath(
                    $".dl-reanimated/backups/{transaction.DeploymentId}/");
                if (!backup.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !backup.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Deployment transaction backup for '{relative}' is outside its backup directory.");
                }
            }
            else if (artifact.BackupRelativePath is not null)
            {
                throw new InvalidDataException(
                    $"Deployment transaction has a backup without previous content for '{relative}'.");
            }
        }

        return transaction;
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ValidatedRequest(
        string ProjectRoot,
        string CompilerExecutablePath,
        string RetailData0PakPath,
        string CharacterId,
        string ModelResourceName,
        string SurfaceName,
        string AnimationLibraryName);

    private sealed record StagedArtifact(
        string RelativePath,
        Dl1DeploymentArtifactRole Role,
        string? StagedPath,
        bool Required,
        string Description)
    {
        public static StagedArtifact Placeholder(
            string relativePath,
            Dl1DeploymentArtifactRole role,
            bool required,
            string description) =>
            new(relativePath, role, null, required, description);
    }

    private sealed record PreparedDeployment(
        Dl1SourceModelBuildResult Source,
        PreparedCustomModelAnimationLibrary Library,
        ImmutableArray<StagedArtifact> Artifacts);

    private sealed record CommittedArtifact(
        StagedArtifact Artifact,
        string? PreviousHash,
        string? BackupRelativePath);

    private sealed record MaterialDatabaseSnapshot(
        string SnapshotPath,
        string OriginalSha256);

    private enum DeploymentTransactionArtifactState
    {
        Pending,
        BackupReady,
        Published,
    }

    private sealed record DeploymentTransactionArtifact(
        string RelativePath,
        string? PreviousSha256,
        string PreparedSha256,
        string? BackupRelativePath,
        DeploymentTransactionArtifactState State);

    private sealed record DeploymentTransactionJournal
    {
        public string Format { get; init; } = TransactionFormat;

        public int SchemaVersion { get; init; } = 1;

        public required string DeploymentId { get; init; }

        public required string ReceiptRelativePath { get; init; }

        public required DateTimeOffset CreatedUtc { get; init; }

        public ImmutableArray<DeploymentTransactionArtifact> Artifacts { get; init; } = [];
    }

    private sealed class ProjectOperationLock : IAsyncDisposable
    {
        private readonly FileStream _stream;

        public ProjectOperationLock(FileStream stream)
        {
            _stream = stream;
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
