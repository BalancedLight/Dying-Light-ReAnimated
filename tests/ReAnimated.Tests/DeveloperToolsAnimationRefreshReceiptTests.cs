using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class DeveloperToolsAnimationRefreshReceiptTests
{
    private const string DeploymentId = "89abcdef0123456789abcdef";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "DeveloperToolsDeployment")]
    public void PublicRequestBindsSchema2ReceiptAndRejectsStaleOrChangedManifest()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] runtimePackBytes = "generic animation runtime pack"u8.ToArray();
            string runtimePackSha256 =
                Convert.ToHexStringLower(SHA256.HashData(runtimePackBytes));
            Dl1AnimationContentManifestBytes manifest =
                Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                    DeploymentId,
                    "generic_character",
                    "generic_model",
                    "generic_library",
                    new DateTimeOffset(2026, 8, 13, 20, 0, 0, TimeSpan.Zero),
                    CreateArtifacts(runtimePackSha256));
            string manifestPath = Path.Combine(
                root,
                manifest.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllBytes(manifestPath, manifest.Utf8Json.ToArray());
            string runtimePackPath = Path.Combine(
                root,
                manifest.Manifest.Artifacts.Single(static artifact =>
                        artifact.Role == Dl1AnimationContentArtifactRole.AnimationRuntimePack)
                    .RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePackPath)!);
            File.WriteAllBytes(runtimePackPath, runtimePackBytes);
            Dl1DeveloperToolsDeploymentReceipt receipt = CreateReceipt(manifest);

            DateTimeOffset now = new(2026, 8, 13, 20, 30, 40, TimeSpan.Zero);
            DeveloperToolsAnimationRefreshRequestResult request =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    receipt,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                    now);

            using JsonDocument requestJson = JsonDocument.Parse(
                File.ReadAllBytes(request.RequestPath));
            Assert.Equal(
                manifest.Sha256,
                requestJson.RootElement.GetProperty("manifestSha256").GetString());
            Assert.Equal(
                manifest.ManifestRelativePath,
                requestJson.RootElement.GetProperty("manifestRelativePath").GetString());
            Assert.Equal(
                "2026-08-13T20:40:40Z",
                requestJson.RootElement.GetProperty("expiresUtc").GetString());

            Dl1DeveloperToolsDeploymentReceipt legacyNamedReceipt = receipt with
            {
                AnimationRuntimePackRelativePath = null,
                AnimationRuntimePackSha256 = null,
                FallbackRpackRelativePath = receipt.AnimationRuntimePackRelativePath,
                FallbackRpackSha256 = receipt.AnimationRuntimePackSha256,
            };
            DeveloperToolsAnimationRefreshRequestResult migratedRequest =
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    legacyNamedReceipt,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.RawLoose,
                    now);
            Assert.True(File.Exists(migratedRequest.RequestPath));

            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    receipt with { SchemaVersion = 1 },
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack));
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    receipt with
                    {
                        AnimationContentManifestSha256 = new string('0', 64),
                    },
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack));
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    receipt with
                    {
                        StaleArtifactPaths =
                            ["data/characters/animations/generic_idle.anm2"],
                    },
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack));

            File.WriteAllText(runtimePackPath, "tampered runtime pack");
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    receipt,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    private static ImmutableArray<Dl1AnimationContentArtifact> CreateArtifacts(
        string runtimePackSha256) =>
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
                ".dl-reanimated/animation-refresh/packages/" +
                runtimePackSha256 +
                ".rpack",
                runtimePackSha256,
                "generic_library"),
        ];

    private static Dl1DeveloperToolsDeploymentReceipt CreateReceipt(
        Dl1AnimationContentManifestBytes manifest) =>
        new()
        {
            DeploymentId = DeploymentId,
            CharacterId = "generic_character",
            ModelResourceName = "generic_model",
            AnimationLibraryName = "generic_library",
            AnimationScriptRelativePath =
                "data/characters/animations/animscripts/generic_library.scr",
            AnimationContentManifestRelativePath = manifest.ManifestRelativePath,
            AnimationContentManifestSha256 = manifest.Sha256,
            AnimationRuntimePackRelativePath = manifest.Manifest.Artifacts
                .Single(static artifact =>
                    artifact.Role == Dl1AnimationContentArtifactRole.AnimationRuntimePack)
                .RelativePath,
            AnimationRuntimePackSha256 = manifest.Manifest.Artifacts
                .Single(static artifact =>
                    artifact.Role == Dl1AnimationContentArtifactRole.AnimationRuntimePack)
                .Sha256,
            ModelCompilerFingerprint = new string('1', 64),
            AnimationCompilerFingerprint = new string('2', 64),
            CompletedUtc = new DateTimeOffset(
                2026,
                8,
                13,
                20,
                0,
                0,
                TimeSpan.Zero),
        };
}
