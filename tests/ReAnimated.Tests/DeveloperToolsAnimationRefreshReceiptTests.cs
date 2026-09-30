using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

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
            byte[] aliasScriptBytes =
                Encoding.UTF8.GetBytes("AnimScriptAlias(\"generic_library.scr\")\n");
            byte[] compiledMeshBytes = CreateCompiledMesh("generic_library.scr");
            Dl1AnimationContentManifestBytes manifest =
                Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                    DeploymentId,
                    "generic_character",
                    "generic_model",
                    "generic_library",
                    new DateTimeOffset(2026, 8, 13, 20, 0, 0, TimeSpan.Zero),
                    CreateArtifacts(
                        runtimePackSha256,
                        Convert.ToHexStringLower(SHA256.HashData(aliasScriptBytes))));
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
            string deployedAliasScriptPath = Path.Combine(
                root,
                "data",
                "characters",
                "generic_character",
                "generic_model.ascr");
            Directory.CreateDirectory(Path.GetDirectoryName(deployedAliasScriptPath)!);
            File.WriteAllBytes(deployedAliasScriptPath, aliasScriptBytes);
            string deployedCompiledMeshPath = Path.Combine(
                root,
                "assets_pc",
                "characters",
                "generic_character",
                "generic_model.msh_obj");
            Directory.CreateDirectory(Path.GetDirectoryName(deployedCompiledMeshPath)!);
            File.WriteAllBytes(deployedCompiledMeshPath, compiledMeshBytes);
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

            Dl1DeveloperToolsDeploymentReceipt compiledOutputDrift = receipt with
            {
                Artifacts =
                [
                    new("assets_pc/characters/generic_character/generic_model.msh_obj", Dl1DeploymentArtifactRole.Compiled, new string('d', 64), null, null, true, true),
                    new("assets_pc/characters/generic_character/generic_texture.dds_obj", Dl1DeploymentArtifactRole.Compiled, new string('e', 64), null, null, true, true),
                    new("assets_pc/local_dx11.mp", Dl1DeploymentArtifactRole.Shared, new string('f', 64), null, null, false, false),
                    new("assets_pc/characters/animations/generic_idle.anm2_obj", Dl1DeploymentArtifactRole.Compiled, new string('9', 64), null, null, true, true),
                    new("data/characters/generic_character/generic_model.ascr", Dl1DeploymentArtifactRole.Source, new string('a', 64), null, null, true, true),
                    new("data/characters/generic_character/generic_model.msh", Dl1DeploymentArtifactRole.Source, new string('1', 64), null, null, true, true),
                    new("data/characters/generic_character/generic_model.chr", Dl1DeploymentArtifactRole.Source, new string('2', 64), null, null, true, true),
                    new("data/characters/generic_character/generic_model.bscr", Dl1DeploymentArtifactRole.Source, new string('3', 64), null, null, true, true),
                    new("data/characters/animations/animscripts/generic_library.scr", Dl1DeploymentArtifactRole.Source, new string('b', 64), null, null, true, true),
                    new("data/characters/animations/generic_idle.anm2", Dl1DeploymentArtifactRole.Source, new string('c', 64), null, null, true, true),
                    new(".dl-reanimated/animation-refresh/packages/" + receipt.EffectiveAnimationRuntimePackSha256 + ".rpack", Dl1DeploymentArtifactRole.ManifestOwned, receipt.EffectiveAnimationRuntimePackSha256!, null, null, true, true),
                    new(".dl-reanimated/animation-refresh/manifests/" + DeploymentId + ".json", Dl1DeploymentArtifactRole.ManifestOwned, receipt.AnimationContentManifestSha256!, null, null, true, true),
                ],
                StaleArtifactPaths =
                [
                    "assets_pc/characters/generic_character/generic_model.msh_obj",
                    "assets_pc/characters/generic_character/generic_texture.dds_obj",
                    "assets_pc/local_dx11.mp",
                ],
            };
            Assert.True(File.Exists(DeveloperToolsAnimationRefreshService.WriteRequest(
                root,
                compiledOutputDrift,
                DeveloperToolsAnimationRefreshHost.Editor,
                DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                now).RequestPath));

            string requestDirectory = Path.Combine(
                root,
                DeveloperToolsAnimationRefreshService.RefreshRootRelativePath,
                "requests");
            byte[] wrongAliasScriptBytes =
                Encoding.UTF8.GetBytes("AnimScriptAlias(\"generic_other_library.scr\")\n");
            File.WriteAllBytes(deployedAliasScriptPath, wrongAliasScriptBytes);
            int requestsBeforeAliasFailure = Directory.GetFiles(requestDirectory, "*.json").Length;
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    compiledOutputDrift,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                    now));
            Assert.Equal(requestsBeforeAliasFailure, Directory.GetFiles(requestDirectory, "*.json").Length);
            File.WriteAllBytes(deployedAliasScriptPath, aliasScriptBytes);

            File.WriteAllBytes(
                deployedCompiledMeshPath,
                CreateCompiledMesh("generic_other_library.scr"));
            int requestsBeforeMeshFailure = Directory.GetFiles(requestDirectory, "*.json").Length;
            Assert.Throws<InvalidDataException>(() =>
                DeveloperToolsAnimationRefreshService.WriteRequest(
                    root,
                    compiledOutputDrift,
                    DeveloperToolsAnimationRefreshHost.Editor,
                    DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                    now));
            Assert.Equal(requestsBeforeMeshFailure, Directory.GetFiles(requestDirectory, "*.json").Length);
            File.WriteAllBytes(deployedCompiledMeshPath, compiledMeshBytes);

            foreach (string blockingPath in new[]
            {
                "assets_pc/characters/animations/generic_idle.anm2_obj",
                "data/characters/generic_character/generic_model.ascr",
                "data/characters/generic_character/generic_model.msh",
                "data/characters/generic_character/generic_model.chr",
                "data/characters/generic_character/generic_model.bscr",
                "data/characters/animations/animscripts/generic_library.scr",
                "data/characters/animations/generic_idle.anm2",
                ".dl-reanimated/animation-refresh/packages/" + receipt.EffectiveAnimationRuntimePackSha256 + ".rpack",
                ".dl-reanimated/animation-refresh/manifests/" + DeploymentId + ".json",
                "assets_pc/characters/unknown_model/unknown_model.msh_obj",
            })
            {
                Assert.Throws<InvalidDataException>(() =>
                    DeveloperToolsAnimationRefreshService.WriteRequest(
                        root,
                        compiledOutputDrift with { StaleArtifactPaths = [blockingPath] },
                        DeveloperToolsAnimationRefreshHost.Editor,
                        DeveloperToolsAnimationRefreshRoute.ProjectRPack,
                        now));
            }
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
        string runtimePackSha256,
        string aliasScriptSha256) =>
        [
            new(
                Dl1AnimationContentArtifactRole.AliasScript,
                "data/characters/generic_character/generic_model.ascr",
                aliasScriptSha256,
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

    private static byte[] CreateCompiledMesh(string animationScriptAlias)
    {
        byte[] original = RpackTestData.BuildCompactMeshPayload();
        byte[] metadata = [.. original, .. Encoding.UTF8.GetBytes(animationScriptAlias + '\0')];
        BinaryPrimitives.WriteUInt64LittleEndian(
            metadata.AsSpan(0x48),
            checked((ulong)original.Length + 1));
        return RpackTestData.BuildArchive(
            "generic_model",
            Rp6lResourceTypes.Mesh,
            [new(0, metadata)],
            RpackTestCompression.None);
    }

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
