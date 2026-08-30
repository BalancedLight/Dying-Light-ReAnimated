using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class DeveloperToolsAnimationRefreshTests
{
    private const string DeploymentId = "0123456789abcdef01234567";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void RequestIsShortLivedAndBindsExactManifestBytes()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string manifestPath = WriteManifest(root);
            DateTimeOffset now = new(2026, 8, 13, 20, 30, 40, TimeSpan.Zero);

            DeveloperToolsAnimationRefreshRequestResult result =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    DeploymentId,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                    now);

            Assert.True(File.Exists(result.RequestPath));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)))
                    .ToLowerInvariant(),
                result.ManifestSha256);
            Assert.Equal(now.AddMinutes(10), result.ExpiresUtc);
            using JsonDocument request = JsonDocument.Parse(
                File.ReadAllBytes(result.RequestPath));
            JsonElement rootElement = request.RootElement;
            Assert.Equal(
                DeveloperToolsAnimationRefreshService.RequestFormat,
                rootElement.GetProperty("format").GetString());
            Assert.Equal(2, rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(DeploymentId, rootElement.GetProperty("deploymentId").GetString());
            Assert.Equal("Editor", rootElement.GetProperty("targetHost").GetString());
            Assert.Equal("ProjectRPack", rootElement.GetProperty("route").GetString());
            Assert.Equal("2026-08-13T20:30:40Z", rootElement.GetProperty("createdUtc").GetString());
            Assert.Equal("2026-08-13T20:40:40Z", rootElement.GetProperty("expiresUtc").GetString());
            Assert.DoesNotContain(
                rootElement.EnumerateObject(),
                static property => string.Equals(
                    property.Name,
                    "projectRoot",
                    StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void LatestRequestRestoreRevalidatesHostAndManifestFingerprint()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string manifestPath = WriteManifest(root);
            DeveloperToolsAnimationRefreshRequestResult written =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    DeploymentId,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                    new DateTimeOffset(
                        2026,
                        8,
                        14,
                        12,
                        0,
                        0,
                        TimeSpan.Zero));

            DeveloperToolsAnimationRefreshRequestResult restored =
                Assert.IsType<DeveloperToolsAnimationRefreshRequestResult>(
                    DeveloperToolsAnimationRefreshService.LoadLatestRequest(root));
            Assert.Equal(written.RequestId, restored.RequestId);
            Assert.Equal(written.ManifestSha256, restored.ManifestSha256);
            Assert.Equal(DeveloperToolsAnimationRefreshHost.Editor, restored.TargetHost);
            Assert.Equal(DeveloperToolsAnimationRefreshRoute.ProjectRPack, restored.Route);

            string requestJson = File.ReadAllText(written.RequestPath);
            File.WriteAllText(
                written.RequestPath,
                requestJson.Replace(
                    "\"targetHost\": \"Editor\"",
                    "\"targetHost\": \"Unsupported\"",
                    StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.LoadLatestRequest(root));

            File.WriteAllText(written.RequestPath, requestJson);
            File.AppendAllText(manifestPath, " ");
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.LoadLatestRequest(root));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void Schema1ManifestAndLegacyRouteAliasMigrateToSchema2RawLooseRequest()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            WriteManifest(root, schemaVersion: 1);
            DeveloperToolsAnimationRefreshRequestResult request =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    DeploymentId,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.LooseOnly);

            using JsonDocument json = JsonDocument.Parse(
                File.ReadAllBytes(request.RequestPath));
            Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("RawLoose", json.RootElement.GetProperty("route").GetString());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void ResultMustMatchPendingRequestAndRemainsAnOfflineReceipt()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            WriteManifest(root);
            DeveloperToolsAnimationRefreshRequestResult request =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    DeploymentId,
                    DeveloperToolsAnimationRefreshHost.Player,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack);
            Directory.CreateDirectory(Path.GetDirectoryName(request.ResultPath)!);
            File.WriteAllText(
                request.ResultPath,
                JsonSerializer.Serialize(new
                {
                    format = DeveloperToolsAnimationRefreshService.ResultFormat,
                    schemaVersion = 2,
                    requestId = request.RequestId,
                    deploymentId = DeploymentId,
                    manifestSha256 = request.ManifestSha256,
                    status = "Completed",
                    firstFailedStage = (string?)null,
                    failureStage = (string?)null,
                    uiBridgeDiscovery = "Passed",
                    commandInterception = "Passed",
                    activeDocumentResolution = "Passed",
                    activeProjectResolution = "Passed",
                    commandSource = "refresh-menu",
                    selectedModelMatch = "Passed",
                    packRegistration = "Passed",
                    scriptBankResolution = "Passed",
                    bindCreation = "Passed",
                    animationNameCount = 2,
                    clipResolution = "Passed",
                    inspectorRebuild = "Passed",
                    rollback = "NotAttempted",
                    refusalReason = (string?)null,
                    visibleAnimationCount = 2,
                    liveEvidence = false,
                }));

            DeveloperToolsAnimationRefreshResultSummary summary =
                DeveloperToolsAnimationRefreshService.ReadResult(request);

            Assert.Equal("Completed", summary.Status);
            Assert.Equal(2, summary.SchemaVersion);
            Assert.Null(summary.FailureStage);
            Assert.Equal("Passed", summary.UiBridgeDiscovery);
            Assert.Equal("Passed", summary.CommandInterception);
            Assert.Equal("Passed", summary.ActiveDocumentResolution);
            Assert.Equal("Passed", summary.ActiveProjectResolution);
            Assert.Equal("refresh-menu", summary.CommandSource);
            Assert.Equal("Passed", summary.PackRegistration);
            Assert.Equal(2, summary.AnimationNameCount);
            Assert.Contains("Selected model match: Passed", summary.Details, StringComparison.Ordinal);
            Assert.Contains("Inspector rebuild: Passed", summary.Details, StringComparison.Ordinal);
            Assert.Contains("Command source: refresh-menu", summary.Details, StringComparison.Ordinal);
            Assert.Contains("visibleAnimationCount: 2", summary.Details, StringComparison.Ordinal);
            Assert.Contains("liveEvidence: false", summary.Details, StringComparison.Ordinal);

            string tampered = File.ReadAllText(request.ResultPath)
                .Replace(request.ManifestSha256, new string('0', 64), StringComparison.Ordinal);
            File.WriteAllText(request.ResultPath, tampered);
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.ReadResult(request));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void Schema1LoaderResultRemainsReadableWithoutInventingSchema2Stages()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            WriteManifest(root, schemaVersion: 1);
            DeveloperToolsAnimationRefreshRequestResult request =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    DeploymentId,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.RawLoose);
            Directory.CreateDirectory(Path.GetDirectoryName(request.ResultPath)!);
            File.WriteAllText(
                request.ResultPath,
                JsonSerializer.Serialize(new
                {
                    format = DeveloperToolsAnimationRefreshService.ResultFormat,
                    schemaVersion = 1,
                    requestId = request.RequestId,
                    deploymentId = DeploymentId,
                    manifestSha256 = request.ManifestSha256,
                    status = "Refused",
                    failureStage = "reload_unsupported",
                    animationCount = 0,
                    rebind = "NotAttempted",
                }));

            DeveloperToolsAnimationRefreshResultSummary summary =
                DeveloperToolsAnimationRefreshService.ReadResult(request);

            Assert.Equal(1, summary.SchemaVersion);
            Assert.Equal("NotReported", summary.SelectedModelMatch);
            Assert.Null(summary.AnimationNameCount);
            Assert.Contains(
                "Legacy schema-1 result",
                summary.Details,
                StringComparison.Ordinal);
            Assert.Contains("animationCount: 0", summary.Details, StringComparison.Ordinal);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void SuccessfulDeploymentQueuesDefaultEditorProjectRpackRequestAutomatically()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] runtimePackBytes = "generic runtime pack"u8.ToArray();
            string runtimePackSha256 =
                Convert.ToHexStringLower(SHA256.HashData(runtimePackBytes));
            ImmutableArray<Dl1AnimationContentArtifact> artifacts =
            [
                new(
                    Dl1AnimationContentArtifactRole.AliasScript,
                    "data/characters/generic_character/generic_model.ascr",
                    new string('a', 64),
                    "generic_model"),
                new(
                    Dl1AnimationContentArtifactRole.AnimationScript,
                    "data/characters/animations/animscripts/generic_library.scr",
                    new string('b', 64),
                    "generic_library"),
                new(
                    Dl1AnimationContentArtifactRole.Animation,
                    "data/characters/animations/generic_idle.anm2",
                    new string('c', 64),
                    "generic_idle"),
                new(
                    Dl1AnimationContentArtifactRole.AnimationRuntimePack,
                    $".dl-reanimated/animation-refresh/packages/{runtimePackSha256}.rpack",
                    runtimePackSha256,
                    "generic_library"),
            ];
            Dl1AnimationContentManifestBytes manifest =
                Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                    DeploymentId,
                    "generic_character",
                    "generic_model",
                    "generic_library",
                    new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
                    artifacts);
            string manifestPath = Path.Combine(
                root,
                manifest.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllBytes(manifestPath, manifest.Utf8Json.ToArray());
            string runtimePackPath = Path.Combine(
                root,
                artifacts[^1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePackPath)!);
            File.WriteAllBytes(runtimePackPath, runtimePackBytes);
            var receipt = new Dl1DeveloperToolsDeploymentReceipt
            {
                DeploymentId = DeploymentId,
                CharacterId = "generic_character",
                ModelResourceName = "generic_model",
                AnimationLibraryName = "generic_library",
                AnimationScriptRelativePath =
                    "data/characters/animations/animscripts/generic_library.scr",
                AnimationContentManifestRelativePath = manifest.ManifestRelativePath,
                AnimationContentManifestSha256 = manifest.Sha256,
                AnimationRuntimePackRelativePath = artifacts[^1].RelativePath,
                AnimationRuntimePackSha256 = runtimePackSha256,
                ModelCompilerFingerprint = new string('1', 64),
                AnimationCompilerFingerprint = new string('2', 64),
                CompletedUtc = DateTimeOffset.UtcNow,
            };
            var settings = new CustomModelDeveloperToolsSettings(
                Path.Combine(root, "settings", "developer-tools.json"));
            using var viewModel = new ModelsWorkspaceViewModel(
                new NoOpProjectFileDialogs(),
                static _ => { },
                static _ => Task.CompletedTask,
                static () => null,
                settings);

            Assert.Equal(
                DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                viewModel.SelectedAnimationRefreshRoute.Value);
            viewModel.QueueAutomaticDeveloperToolsAnimationRefresh(root, receipt);

            string requestPath = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, ".dl-reanimated", "animation-refresh", "requests"),
                "*.json"));
            using JsonDocument request = JsonDocument.Parse(File.ReadAllBytes(requestPath));
            Assert.Equal("Editor", request.RootElement.GetProperty("targetHost").GetString());
            Assert.Equal("ProjectRPack", request.RootElement.GetProperty("route").GetString());
            Assert.Contains(
                "Automatic post-deployment refresh",
                viewModel.AnimationRefreshStatus,
                StringComparison.Ordinal);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void DiagnosticBundleIsPassiveBoundedAndIncludesSelectedLoaderLog()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            WriteManifest(root);
            string deployments = Path.Combine(root, ".dl-reanimated", "deployments");
            Directory.CreateDirectory(deployments);
            File.WriteAllText(Path.Combine(deployments, $"{DeploymentId}.json"), "{}");
            string loaderLog = Path.Combine(root, "dl_universal_loader.log");
            File.WriteAllText(loaderLog, "existing offline loader evidence");
            string output = Path.Combine(root, "animation-diagnostics.zip");

            string wrongLog = Path.Combine(root, "other.log");
            File.WriteAllText(wrongLog, "not the requested loader log");
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.CreateDiagnosticBundle(
                    root,
                    output,
                    wrongLog));

            DeveloperToolsAnimationDiagnosticBundleResult result =
                DeveloperToolsAnimationRefreshService.CreateDiagnosticBundle(
                    root,
                    output,
                    loaderLog);

            Assert.True(File.Exists(result.OutputPath));
            Assert.True(result.CollectedFileCount >= 3);
            using ZipArchive archive = ZipFile.OpenRead(result.OutputPath);
            string[] names = archive.Entries.Select(static entry => entry.FullName).ToArray();
            Assert.Contains("loader/dl_universal_loader.log", names);
            Assert.Contains("bundle-manifest.txt", names);
            Assert.Contains("MANUAL_ANIMATION_REFRESH_CHECKLIST.txt", names);
            Assert.Contains(
                names,
                static name => name.StartsWith(
                    ".dl-reanimated/animation-refresh/manifests/",
                    StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ExportWorkspaceKeepsDeploymentSimpleAndDiagnosticsSecondary()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "MainWindow.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.DoesNotContain(
            document.Descendants().Attributes(),
            static attribute => attribute.Value.Contains(
                "HostsClosedForDiagnostics",
                StringComparison.Ordinal));
        Assert.Equal(
            ["Files", "Developer Tools"],
            document.Descendants(presentation + "TabItem")
                .Select(static element => (string?)element.Attribute("Header"))
                .Where(static header => header is
                    "Files" or "Developer Tools")
                .Cast<string>()
                .ToArray());
        Assert.DoesNotContain(
            document.Descendants(presentation + "TabItem"),
            static element => string.Equals(
                (string?)element.Attribute("Header"),
                "Receipts",
                StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(presentation + "Expander"),
            static element => string.Equals(
                (string?)element.Attribute("Header"),
                "Failure details and diagnostics",
                StringComparison.Ordinal));
        foreach (string mode in new[]
                 {
                     "Animations only",
                     // Source staging and the prebuilt pack are separate
                     // modes now, and each label says which one it is.
                     "Characters (source)",
                     "Characters (prebuilt RPack)",
                     "ANM2 only",
                     "Initial / full export",
                 })
        {
            Assert.Contains(
                document.Descendants().Where(static element =>
                    element.Name.LocalName is "Button" or "ToggleButton"),
                element => string.Equals(
                    (string?)element.Attribute("Content"),
                    mode,
                    StringComparison.Ordinal));
        }
        XElement[] modeToggles = document
            .Descendants(presentation + "ToggleButton")
            .Where(static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding SelectDeveloperToolsExportModeCommand}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(5, modeToggles.Length);
        Assert.Equal(
            [
                "AnimationsOnly",
                "CharactersOnly",
                "CharacterRpack",
                "Anm2Only",
                "Full",
            ],
            modeToggles.Select(static element =>
                (string?)element.Attribute("CommandParameter") ?? string.Empty));
        XElement compiler = Assert.Single(
            document.Descendants(presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding Models.SelectModelCompilerCommand}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding IsCharacterCompilerRequired, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)compiler.Attribute("Visibility"));
    }

    private static string WriteManifest(string projectRoot, int schemaVersion = 2)
    {
        string path = Path.Combine(
            projectRoot,
            ".dl-reanimated",
            "animation-refresh",
            "manifests",
            $"{DeploymentId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new
            {
                format = DeveloperToolsAnimationRefreshService.ManifestFormat,
                schemaVersion,
                deploymentId = DeploymentId,
                characterId = "generic_character",
                modelResourceName = "generic_model",
                animationLibraryName = "generic_library",
                createdUtc = "2026-08-13T20:00:00Z",
                artifacts = Array.Empty<object>(),
            }));
        return path;
    }

    private static string FindRepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{Path.Combine(relativeSegments)}' above '{AppContext.BaseDirectory}'.");
    }

    private sealed class NoOpProjectFileDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}
