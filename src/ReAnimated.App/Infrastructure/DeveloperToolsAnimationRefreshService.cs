using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReAnimated.App.Infrastructure;

public enum DeveloperToolsAnimationRefreshRoute
{
    ProjectRPack,
    RawLoose,

    ProjectRPackFallback = ProjectRPack,
    LooseOnly = RawLoose,
}

public enum DeveloperToolsAnimationRefreshHost
{
    Editor,
    Player,
}

public sealed record DeveloperToolsAnimationRefreshChoice<T>(
    T Value,
    string Label,
    string Description)
    where T : struct, Enum;

public sealed record DeveloperToolsAnimationRefreshRequestResult(
    string RequestId,
    string DeploymentId,
    string ProjectRoot,
    string RequestPath,
    string ResultPath,
    string ManifestRelativePath,
    string ManifestSha256,
    DateTimeOffset ExpiresUtc,
    DeveloperToolsAnimationRefreshHost TargetHost,
    DeveloperToolsAnimationRefreshRoute Route);

public sealed record DeveloperToolsAnimationRefreshResultSummary(
    string Status,
    string? FailureStage,
    string Details,
    string ResultPath)
{
    public int SchemaVersion { get; init; }

    public string SelectedModelMatch { get; init; } = "NotReported";

    public string PackRegistration { get; init; } = "NotReported";

    public string ScriptBankResolution { get; init; } = "NotReported";

    public string BindCreation { get; init; } = "NotReported";

    public int? AnimationNameCount { get; init; }

    public string ClipResolution { get; init; } = "NotReported";

    public string InspectorRebuild { get; init; } = "NotReported";

    public string Rollback { get; init; } = "NotReported";

    public string? RefusalReason { get; init; }

    public string UiBridgeDiscovery { get; init; } = "NotReported";

    public string CommandInterception { get; init; } = "NotReported";

    public string ActiveDocumentResolution { get; init; } = "NotReported";

    public string ActiveProjectResolution { get; init; } = "NotReported";

    public string CommandSource { get; init; } = "NotReported";
}

public sealed record DeveloperToolsAnimationDiagnosticBundleResult(
    string OutputPath,
    int CollectedFileCount,
    long CollectedByteCount);

/// <summary>
/// Writes the bounded, project-local animation-refresh handshake and passively
/// collects its existing evidence. This service never discovers, launches,
/// attaches to, injects into, or controls a process.
/// </summary>
public static partial class DeveloperToolsAnimationRefreshService
{
    public const string RefreshRootRelativePath = ".dl-reanimated/animation-refresh";
    public const string RequestFormat = "dl-reanimated-animation-refresh-request";
    public const string ResultFormat = "dl-universal-loader-animation-refresh-result";
    public const string ManifestFormat = "dl-reanimated-animation-content-manifest";

    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumRequestBytes = 32 * 1024;
    private const int MaximumResultBytes = 32 * 1024;
    private const int MaximumRestorableRequests = 128;
    private const int MaximumBundleFiles = 128;
    private const int MaximumBundleFileBytes = 8 * 1024 * 1024;
    private const int MaximumBundleBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static DeveloperToolsAnimationRefreshRequestResult WriteRequest(
        string projectRoot,
        string deploymentId,
        DeveloperToolsAnimationRefreshHost targetHost,
        DeveloperToolsAnimationRefreshRoute route,
        DateTimeOffset? utcNow = null)
    {
        string root = ValidateProjectRoot(projectRoot);
        ValidateDeploymentId(deploymentId);
        string manifestRelativePath =
            $"{RefreshRootRelativePath}/manifests/{deploymentId}.json";
        string manifestPath = ResolveRegularProjectFile(
            root,
            manifestRelativePath,
            MaximumManifestBytes,
            "animation content manifest");
        byte[] manifestBytes = ReadBoundedFile(
            manifestPath,
            MaximumManifestBytes,
            "animation content manifest");
        ValidateManifestEnvelope(manifestBytes, deploymentId);
        string manifestSha256 = ComputeSha256(manifestBytes);

        DateTimeOffset createdUtc = (utcNow ?? DateTimeOffset.UtcNow).ToUniversalTime();
        DateTimeOffset expiresUtc = createdUtc.Add(RequestLifetime);
        string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var request = new AnimationRefreshRequestWire
        {
            Format = RequestFormat,
            SchemaVersion = 2,
            RequestId = requestId,
            DeploymentId = deploymentId,
            ManifestRelativePath = manifestRelativePath,
            ManifestSha256 = manifestSha256,
            TargetHost = targetHost,
            Route = GetRouteWireName(route),
            CreatedUtc = FormatUtc(createdUtc),
            ExpiresUtc = FormatUtc(expiresUtc),
        };
        string requestRelativePath =
            $"{RefreshRootRelativePath}/requests/{requestId}.json";
        string requestPath = ResolveProjectOutputPath(root, requestRelativePath);
        string resultPath = ResolveProjectOutputPath(
            root,
            $"{RefreshRootRelativePath}/results/{requestId}.json");
        EnsureOwnedOutputDirectory(root, requestPath);
        EnsureOwnedOutputDirectory(root, resultPath);
        AtomicFileWriter.WriteAllText(
            requestPath,
            JsonSerializer.Serialize(request, SerializerOptions));
        return new DeveloperToolsAnimationRefreshRequestResult(
            requestId,
            deploymentId,
            root,
            requestPath,
            resultPath,
            manifestRelativePath,
            manifestSha256,
            expiresUtc,
            targetHost,
            route);
    }

    public static DeveloperToolsAnimationRefreshRequestResult? LoadLatestRequest(
        string projectRoot)
    {
        string root = ValidateProjectRoot(projectRoot);
        string requestDirectory = ResolveProjectOutputPath(
            root,
            $"{RefreshRootRelativePath}/requests");
        if (!Directory.Exists(requestDirectory))
        {
            return null;
        }

        RejectReparseComponents(root, requestDirectory);
        RejectReparsePoint(requestDirectory);
        FileInfo[] candidates = Directory
            .EnumerateFiles(requestDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(static path => new FileInfo(path))
            .OrderByDescending(static info => info.LastWriteTimeUtc)
            .ThenByDescending(static info => info.Name, StringComparer.Ordinal)
            .Take(MaximumRestorableRequests + 1)
            .ToArray();
        if (candidates.Length > MaximumRestorableRequests)
        {
            throw new InvalidDataException(
                $"The animation refresh request directory exceeds the {MaximumRestorableRequests:N0}-file restore bound.");
        }

        if (candidates.Length == 0)
        {
            return null;
        }

        FileInfo latest = candidates[0];
        RejectReparseComponents(root, latest.FullName);
        RejectReparsePoint(latest.FullName);
        byte[] requestBytes = ReadBoundedFile(
            latest.FullName,
            MaximumRequestBytes,
            "animation refresh request");
        using JsonDocument document = JsonDocument.Parse(
            requestBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        JsonElement request = document.RootElement;
        if (request.ValueKind != JsonValueKind.Object ||
            ReadRequiredString(request, "format") != RequestFormat ||
            ReadRequiredInt32(request, "schemaVersion") is not (1 or 2))
        {
            throw new InvalidDataException(
                "The latest animation refresh request has an unsupported envelope.");
        }

        string requestId = ReadRequiredString(request, "requestId");
        string deploymentId = ReadRequiredString(request, "deploymentId");
        string manifestRelativePath = ReadRequiredString(
            request,
            "manifestRelativePath");
        string manifestSha256 = ReadRequiredString(request, "manifestSha256");
        ValidateDeploymentId(deploymentId);
        if (!RequestIdPattern().IsMatch(requestId) ||
            !Sha256Pattern().IsMatch(manifestSha256) ||
            !string.Equals(
                Path.GetFileNameWithoutExtension(latest.Name),
                requestId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The latest animation refresh request has a non-canonical identity or fingerprint.");
        }

        string expectedManifestRelativePath =
            $"{RefreshRootRelativePath}/manifests/{deploymentId}.json";
        if (!string.Equals(
                manifestRelativePath,
                expectedManifestRelativePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The latest animation refresh request does not reference its deployment-owned manifest.");
        }

        string manifestPath = ResolveRegularProjectFile(
            root,
            manifestRelativePath,
            MaximumManifestBytes,
            "animation content manifest");
        byte[] manifestBytes = ReadBoundedFile(
            manifestPath,
            MaximumManifestBytes,
            "animation content manifest");
        ValidateManifestEnvelope(manifestBytes, deploymentId);
        if (!string.Equals(
                ComputeSha256(manifestBytes),
                manifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The latest animation refresh request manifest fingerprint is stale.");
        }

        string targetHostWireName = ReadRequiredString(request, "targetHost");
        if (!Enum.TryParse(
                targetHostWireName,
                ignoreCase: false,
                out DeveloperToolsAnimationRefreshHost targetHost) ||
            !Enum.IsDefined(targetHost))
        {
            throw new InvalidDataException(
                "The latest animation refresh request names an unsupported host.");
        }
        DeveloperToolsAnimationRefreshRoute route =
            ParseRouteWireName(ReadRequiredString(request, "route"));
        _ = ParseUtc(ReadRequiredString(request, "createdUtc"), "createdUtc");
        DateTimeOffset expiresUtc = ParseUtc(
            ReadRequiredString(request, "expiresUtc"),
            "expiresUtc");
        string resultPath = ResolveProjectOutputPath(
            root,
            $"{RefreshRootRelativePath}/results/{requestId}.json");
        return new DeveloperToolsAnimationRefreshRequestResult(
            requestId,
            deploymentId,
            root,
            latest.FullName,
            resultPath,
            manifestRelativePath,
            manifestSha256,
            expiresUtc,
            targetHost,
            route);
    }

    public static DeveloperToolsAnimationRefreshResultSummary ReadResult(
        DeveloperToolsAnimationRefreshRequestResult request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string projectRoot = ValidateProjectRoot(request.ProjectRoot);
        string expectedPath = ResolveProjectOutputPath(
            projectRoot,
            $"{RefreshRootRelativePath}/results/{request.RequestId}.json");
        string path = Path.GetFullPath(request.ResultPath);
        if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The animation refresh result path is not owned by the pending project request.");
        }

        path = ResolveRegularProjectFile(
            projectRoot,
            $"{RefreshRootRelativePath}/results/{request.RequestId}.json",
            MaximumResultBytes,
            "animation refresh result");
        byte[] bytes = ReadBoundedFile(path, MaximumResultBytes, "animation refresh result");
        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The animation refresh result root must be a JSON object.");
        }

        int schemaVersion = ReadRequiredInt32(root, "schemaVersion");
        if (ReadRequiredString(root, "format") != ResultFormat ||
            schemaVersion is not (1 or 2) ||
            !string.Equals(
                ReadRequiredString(root, "requestId"),
                request.RequestId,
                StringComparison.Ordinal) ||
            !string.Equals(
                ReadRequiredString(root, "deploymentId"),
                request.DeploymentId,
                StringComparison.Ordinal) ||
            !string.Equals(
                ReadRequiredString(root, "manifestSha256"),
                request.ManifestSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The animation refresh result does not match the pending request envelope.");
        }

        string status = BoundedDisplayValue(ReadRequiredString(root, "status"), 80);
        string? failureStage =
            ReadOptionalString(root, "firstFailedStage") ??
            ReadOptionalString(root, "failureStage");
        var details = new List<string>();
        string selectedModelMatch = "NotReported";
        string packRegistration = "NotReported";
        string scriptBankResolution = "NotReported";
        string bindCreation = "NotReported";
        int? animationNameCount = null;
        string clipResolution = "NotReported";
        string inspectorRebuild = "NotReported";
        string rollback = "NotReported";
        string? refusalReason = null;
        string uiBridgeDiscovery = "NotReported";
        string commandInterception = "NotReported";
        string activeDocumentResolution = "NotReported";
        string activeProjectResolution = "NotReported";
        string commandSource = "NotReported";
        if (schemaVersion == 2)
        {
            selectedModelMatch = ReadRequiredResultStage(root, "selectedModelMatch");
            packRegistration = ReadRequiredResultStage(root, "packRegistration");
            scriptBankResolution = ReadRequiredResultStage(root, "scriptBankResolution");
            bindCreation = ReadRequiredResultStage(root, "bindCreation");
            animationNameCount = ReadRequiredNonNegativeInt32(root, "animationNameCount");
            clipResolution = ReadRequiredResultStage(root, "clipResolution");
            inspectorRebuild = ReadRequiredResultStage(root, "inspectorRebuild");
            rollback = ReadRequiredResultStage(root, "rollback");
            refusalReason = ReadRequiredNullableString(root, "refusalReason");
            uiBridgeDiscovery = ReadOptionalResultStage(
                root, "uiBridgeDiscovery");
            commandInterception = ReadOptionalResultStage(
                root, "commandInterception");
            activeDocumentResolution = ReadOptionalResultStage(
                root, "activeDocumentResolution");
            activeProjectResolution = ReadOptionalResultStage(
                root, "activeProjectResolution");
            commandSource = ReadOptionalString(root, "commandSource") is { } source
                ? BoundedDisplayValue(source, 80)
                : "NotReported";
            details.Add($"Selected model match: {selectedModelMatch}");
            details.Add($"Runtime-pack registration: {packRegistration}");
            details.Add($"Script bank resolution: {scriptBankResolution}");
            details.Add($"Bind creation: {bindCreation}");
            details.Add($"Animation names: {animationNameCount.Value:N0}");
            details.Add($"Clip resolution: {clipResolution}");
            details.Add($"Inspector rebuild: {inspectorRebuild}");
            details.Add($"Rollback: {rollback}");
            details.Add($"UI bridge discovery: {uiBridgeDiscovery}");
            details.Add($"Command interception: {commandInterception}");
            details.Add($"Active document resolution: {activeDocumentResolution}");
            details.Add($"Active project resolution: {activeProjectResolution}");
            details.Add($"Command source: {commandSource}");
            details.Add($"Refusal reason: {refusalReason ?? "None"}");
        }
        else
        {
            details.Add(
                "Legacy schema-1 result: selected-model reload stages were not reported.");
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is "format" or "schemaVersion" or "requestId" or
                "deploymentId" or "manifestSha256" or "status" or "failureStage" or
                "firstFailedStage" or
                "selectedModelMatch" or "packRegistration" or "scriptBankResolution" or
                "bindCreation" or "animationNameCount" or "clipResolution" or
                "inspectorRebuild" or "rollback" or "refusalReason" or
                "uiBridgeDiscovery" or "commandInterception" or
                "activeDocumentResolution" or "activeProjectResolution" or
                "commandSource")
            {
                continue;
            }

            if (details.Count >= 16)
            {
                details.Add("Additional result fields omitted.");
                break;
            }

            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                    property.Value.GetRawText(),
                JsonValueKind.Null => "null",
                _ => "[structured detail]",
            };
            details.Add($"{BoundedDisplayValue(property.Name, 80)}: " +
                        BoundedDisplayValue(value ?? string.Empty, 200));
        }

        return new DeveloperToolsAnimationRefreshResultSummary(
            status,
            string.IsNullOrWhiteSpace(failureStage)
                ? null
                : BoundedDisplayValue(failureStage, 120),
            string.Join(Environment.NewLine, details),
            path)
        {
            SchemaVersion = schemaVersion,
            SelectedModelMatch = selectedModelMatch,
            PackRegistration = packRegistration,
            ScriptBankResolution = scriptBankResolution,
            BindCreation = bindCreation,
            AnimationNameCount = animationNameCount,
            ClipResolution = clipResolution,
            InspectorRebuild = inspectorRebuild,
            Rollback = rollback,
            RefusalReason = refusalReason,
            UiBridgeDiscovery = uiBridgeDiscovery,
            CommandInterception = commandInterception,
            ActiveDocumentResolution = activeDocumentResolution,
            ActiveProjectResolution = activeProjectResolution,
            CommandSource = commandSource,
        };
    }

    public static DeveloperToolsAnimationDiagnosticBundleResult CreateDiagnosticBundle(
        string projectRoot,
        string outputPath,
        string? loaderLogPath)
    {
        string root = ValidateProjectRoot(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException(
                "The diagnostic bundle output directory does not exist.");
        }

        string evidenceRoot = ResolveProjectOutputPath(root, ".dl-reanimated");
        if (IsPathWithin(fullOutputPath, evidenceRoot))
        {
            throw new InvalidOperationException(
                "Save the diagnostic bundle outside the project .dl-reanimated evidence directory.");
        }

        var candidates = new List<(string FullPath, string EntryPath, long Length)>();
        AddBundleCandidates(root, ".dl-reanimated/deployments", candidates);
        AddBundleCandidates(root, RefreshRootRelativePath, candidates);
        if (!string.IsNullOrWhiteSpace(loaderLogPath))
        {
            string logPath = Path.GetFullPath(loaderLogPath);
            if (!string.Equals(
                    Path.GetFileName(logPath),
                    "dl_universal_loader.log",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Only an existing dl_universal_loader.log may be added to the diagnostic bundle.");
            }

            if (!File.Exists(logPath))
            {
                throw new FileNotFoundException(
                    "The selected Universal Loader log no longer exists.",
                    logPath);
            }

            RejectReparsePoint(logPath);
            var logInfo = new FileInfo(logPath);
            if (logInfo.Length is <= 0 or > MaximumBundleFileBytes)
            {
                throw new InvalidDataException(
                    "The selected Universal Loader log is empty or exceeds the per-file limit.");
            }

            candidates.Add((logPath, "loader/dl_universal_loader.log", logInfo.Length));
        }

        candidates.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.EntryPath, right.EntryPath));
        if (candidates.Count > MaximumBundleFiles)
        {
            throw new InvalidDataException(
                $"The diagnostic evidence contains more than {MaximumBundleFiles:N0} files. Archive older evidence before collecting a bundle.");
        }

        long totalBytes = 0;
        foreach ((_, _, long length) in candidates)
        {
            totalBytes = checked(totalBytes + length);
            if (totalBytes > MaximumBundleBytes)
            {
                throw new InvalidDataException(
                    $"The diagnostic evidence exceeds the {MaximumBundleBytes / (1024 * 1024):N0} MiB bundle limit.");
            }
        }

        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       32 * 1024,
                       FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach ((string fullPath, string entryPath, long _) in candidates)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                    using Stream destination = entry.Open();
                    CopyBoundedFile(fullPath, destination, MaximumBundleFileBytes);
                }

                WriteArchiveText(
                    archive,
                    "bundle-manifest.txt",
                    $"Format: dl-reanimated-animation-diagnostic-bundle\n" +
                    $"Schema version: 1\n" +
                    $"Created UTC: {FormatUtc(DateTimeOffset.UtcNow)}\n" +
                    $"Collected files: {candidates.Count.ToString(CultureInfo.InvariantCulture)}\n" +
                    $"Collected bytes: {totalBytes.ToString(CultureInfo.InvariantCulture)}\n" +
                    "Scope: existing deployment receipts and animation-refresh evidence only.\n" +
                    "Live playback evidence: not claimed.\n");
                WriteArchiveText(
                    archive,
                    "MANUAL_ANIMATION_REFRESH_CHECKLIST.txt",
                    ManualAnimationRefreshChecklist.Content);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new DeveloperToolsAnimationDiagnosticBundleResult(
            fullOutputPath,
            candidates.Count,
            totalBytes);
    }

    private static void ValidateManifestEnvelope(byte[] bytes, string deploymentId)
    {
        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "The animation content manifest root must be a JSON object.");
        }

        int schemaVersion = ReadRequiredInt32(root, "schemaVersion");
        if (ReadRequiredString(root, "format") != ManifestFormat ||
            schemaVersion is not (1 or 2) ||
            !string.Equals(
                ReadRequiredString(root, "deploymentId"),
                deploymentId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The animation content manifest has an unsupported or mismatched envelope.");
        }
    }

    private static string ValidateProjectRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                "The selected Developer Tools project does not exist.");
        }

        RejectReparsePoint(root);
        return root;
    }

    private static void ValidateDeploymentId(string deploymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);
        if (!DeploymentIdPattern().IsMatch(deploymentId))
        {
            throw new InvalidDataException(
                "The deployment identifier is not a canonical 24-character lowercase hexadecimal value.");
        }
    }

    private static string ResolveRegularProjectFile(
        string projectRoot,
        string relativePath,
        int maximumBytes,
        string description)
    {
        string path = ResolveProjectOutputPath(projectRoot, relativePath);
        RejectReparseComponents(projectRoot, path);
        RejectReparsePoint(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {description} must be a regular file between 1 and {maximumBytes:N0} bytes.");
        }

        return path;
    }

    private static string ResolveProjectOutputPath(string projectRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A project-relative path is required.");
        }

        string path = Path.GetFullPath(
            Path.Combine(
                projectRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathWithin(path, projectRoot))
        {
            throw new InvalidDataException("A refresh path escapes the selected project.");
        }

        return path;
    }

    private static bool IsPathWithin(string candidate, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
                                Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureOwnedOutputDirectory(
        string projectRoot,
        string outputPath)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            !IsPathWithin(directory, projectRoot))
        {
            throw new InvalidDataException(
                "Animation refresh output directory escapes the selected project.");
        }

        Directory.CreateDirectory(directory);
        RejectReparseComponents(projectRoot, directory);
        RejectReparsePoint(directory);
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes, string description)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {description} must be between 1 and {maximumBytes:N0} bytes.");
        }

        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void AddBundleCandidates(
        string projectRoot,
        string relativeDirectory,
        List<(string FullPath, string EntryPath, long Length)> candidates)
    {
        string directory = ResolveProjectOutputPath(projectRoot, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        RejectReparseComponents(projectRoot, directory);
        RejectReparsePoint(directory);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            RejectReparsePoint(current);
            foreach (string path in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparseComponents(projectRoot, path);
                RejectReparsePoint(path);
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".json" or ".jsonl" or ".log" or ".txt"))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumBundleFileBytes)
                {
                    throw new InvalidDataException(
                        $"Diagnostic evidence file '{Path.GetFileName(path)}' is empty or exceeds the per-file limit.");
                }

                string relative = Path.GetRelativePath(projectRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                candidates.Add((path, relative, info.Length));
                if (candidates.Count > MaximumBundleFiles)
                {
                    return;
                }
            }

            foreach (string child in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparseComponents(projectRoot, child);
                RejectReparsePoint(child);
                pending.Push(child);
            }
        }
    }

    private static void CopyBoundedFile(string sourcePath, Stream destination, int maximumBytes)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if (source.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Diagnostic evidence '{Path.GetFileName(sourcePath)}' changed beyond the per-file limit.");
        }

        source.CopyTo(destination);
    }

    private static void WriteArchiveText(ZipArchive archive, string path, string contents)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        byte[] bytes = Utf8WithoutBom.GetBytes(contents);
        stream.Write(bytes);
    }

    private static void RejectReparseComponents(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        string current = root;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparsePoint(current);
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Animation refresh evidence cannot traverse reparse point '{Path.GetFileName(path)}'.");
        }
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException($"Animation refresh JSON requires string '{name}'.");
        }

        return element.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Animation refresh JSON property '{name}' must be a string or null.");
        }

        return element.GetString();
    }

    private static string? ReadRequiredNullableString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            throw new InvalidDataException(
                $"Animation refresh JSON requires nullable string '{name}'.");
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException(
                $"Animation refresh JSON property '{name}' must be null or a non-empty string.");
        }

        return BoundedDisplayValue(element.GetString()!, 200);
    }

    private static string ReadRequiredResultStage(JsonElement root, string name)
    {
        string value = ReadRequiredString(root, name);
        if (value is not ("Passed" or "Failed" or "Unsupported" or "NotAttempted"))
        {
            throw new InvalidDataException(
                $"Animation refresh result stage '{name}' has unsupported value '{value}'.");
        }

        return value;
    }

    private static string ReadOptionalResultStage(
        JsonElement root,
        string name)
    {
        string? value = ReadOptionalString(root, name);
        if (value is null)
        {
            return "NotReported";
        }

        if (value is not ("Passed" or "Failed" or "Unsupported" or "NotAttempted"))
        {
            throw new InvalidDataException(
                $"Animation refresh result stage '{name}' has unsupported value '{value}'.");
        }

        return value;
    }

    private static int ReadRequiredNonNegativeInt32(JsonElement root, string name)
    {
        int value = ReadRequiredInt32(root, name);
        if (value is < 0 or > 1_000_000)
        {
            throw new InvalidDataException(
                $"Animation refresh JSON integer '{name}' is outside the supported range.");
        }

        return value;
    }

    private static int ReadRequiredInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element) ||
            !element.TryGetInt32(out int value))
        {
            throw new InvalidDataException($"Animation refresh JSON requires integer '{name}'.");
        }

        return value;
    }

    private static string BoundedDisplayValue(string value, int maximumLength)
    {
        string sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= maximumLength
            ? sanitized
            : string.Concat(sanitized.AsSpan(0, maximumLength), "...");
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture);

    private static string GetRouteWireName(DeveloperToolsAnimationRefreshRoute route) =>
        route switch
        {
            DeveloperToolsAnimationRefreshRoute.ProjectRPack => "ProjectRPack",
            DeveloperToolsAnimationRefreshRoute.RawLoose => "RawLoose",
            _ => throw new InvalidDataException(
                $"Animation refresh route '{route}' is unsupported."),
        };

    private static DeveloperToolsAnimationRefreshRoute ParseRouteWireName(string route) =>
        route switch
        {
            "ProjectRPack" or "ProjectRPackFallback" =>
                DeveloperToolsAnimationRefreshRoute.ProjectRPack,
            "RawLoose" or "LooseOnly" => DeveloperToolsAnimationRefreshRoute.RawLoose,
            _ => throw new InvalidDataException(
                $"Animation refresh route '{route}' is unsupported."),
        };

    private static DateTimeOffset ParseUtc(string value, string propertyName)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            throw new InvalidDataException(
                $"Animation refresh JSON property '{propertyName}' is not canonical UTC time.");
        }

        return parsed;
    }

    [GeneratedRegex("^[0-9a-f]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeploymentIdPattern();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    private sealed record AnimationRefreshRequestWire
    {
        public required string Format { get; init; }

        public required int SchemaVersion { get; init; }

        public required string RequestId { get; init; }

        public required string DeploymentId { get; init; }

        public required string ManifestRelativePath { get; init; }

        public required string ManifestSha256 { get; init; }

        public required DeveloperToolsAnimationRefreshHost TargetHost { get; init; }

        public required string Route { get; init; }

        public required string CreatedUtc { get; init; }

        public required string ExpiresUtc { get; init; }
    }
}

public static class ManualAnimationRefreshChecklist
{
    public const string Content = """
        DL1 manual animation refresh checklist

        This checklist is for manual testing after the offline-validated build is handed off.
        ReAnimated does not launch, attach to, inject into, or control a Dying Light process.

        Local loader switch
        [ ] Confirm [AnimationRefresh] Enabled=1; this is the shipped default.
        [ ] Keep [AnimationRefresh] Route=ProjectRPack for the normal test.
        [ ] Write the bounded refresh request from ReAnimated before or while the Editor is open.
        [ ] Set Enabled=0 only if you intentionally want to disable automatic refresh.

        Developer Tools Editor
        [ ] Start the Editor yourself and open the intended project.
        [ ] Check selected-model match, pack registration, script-bank resolution, bind creation, and inspector rebuild stages.
        [ ] Confirm the visible Refresh menu contains Reload Animations (Selected Model).
        [ ] Press Shift+Alt+A and confirm it uses the selected-model route without invoking the stock global reload.
        [ ] Record the visible animation row count.
        [ ] Select two clips and confirm that each visibly changes the pose.
        [ ] Deploy one new clip, use Refresh/retry, and confirm the row count increases.
        [ ] Optionally select RawLoose (advanced) and compare it with the normal ProjectRPack route.

        DyingLightPlayer (optional manual check)
        [ ] Start DyingLightPlayer yourself; do not use DyingLightGame for this checklist.
        [ ] Use ProjectRPack and record whether the intended clip visibly plays.
        [ ] RawLoose is Editor-only; a Player RawLoose request must report Unsupported.
        [ ] Confirm the tested valid animation reports no invalid-bind suppression.

        Handoff
        [ ] Collect the existing diagnostic bundle; collection does not inspect or control host processes.
        [ ] Include the visible row count, selected route, first failed step, and bundle when reporting a failure.

        Offline compiler, package, request, and loader receipts are not live playback proof.
        """;
}
