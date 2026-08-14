using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1DeveloperToolsBatchDeploymentTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task CrossModelArtifactCollisionBlocksBeforeAnyCommit()
    {
        string projectRoot = RpackTestData.CreateTemporaryDirectory();
        int deploymentCalls = 0;
        try
        {
            Dl1DeveloperToolsDeploymentRequest first = CreateChildRequest(
                projectRoot,
                "character_a",
                "ModelA",
                "LibraryA");
            Dl1DeveloperToolsDeploymentRequest second = CreateChildRequest(
                projectRoot,
                "character_b",
                "ModelB",
                "LibraryB");
            var request = new Dl1DeveloperToolsBatchRequest
            {
                Deployments = [first, second],
                PreflightOverride = (child, _) => Task.FromResult(CreatePlan(
                    child,
                    "data/characters/animations/shared_clip.anm2")),
                DeploymentOverride = (child, _) =>
                {
                    deploymentCalls++;
                    return Task.FromResult(CreateResult(child));
                },
            };

            Dl1DeveloperToolsBatchConflictException error = await Assert.ThrowsAsync<
                Dl1DeveloperToolsBatchConflictException>(() =>
                Dl1DeveloperToolsProjectDeployer.DeployBatchAsync(request));

            Assert.Equal(0, deploymentCalls);
            Assert.Contains(
                error.Preflight.Conflicts,
                static conflict => conflict.Message.Contains(
                    "shared_clip.anm2",
                    StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(projectRoot, ".dl-reanimated", "batches")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(projectRoot);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task FailedChildCommitRollsBackEarlierChildrenAndPersistsBatchReceipt()
    {
        string projectRoot = RpackTestData.CreateTemporaryDirectory();
        var rollbackOrder = new List<string>();
        int deploymentCalls = 0;
        try
        {
            Dl1DeveloperToolsDeploymentRequest first = CreateChildRequest(
                projectRoot,
                "character_a",
                "ModelA",
                "LibraryA");
            Dl1DeveloperToolsDeploymentRequest second = CreateChildRequest(
                projectRoot,
                "character_b",
                "ModelB",
                "LibraryB");
            var request = new Dl1DeveloperToolsBatchRequest
            {
                Deployments = [first, second],
                PreflightOverride = (child, _) => Task.FromResult(CreatePlan(
                    child,
                    $"data/characters/animations/{child.AnimationLibraryName}.anm2")),
                DeploymentOverride = (child, _) =>
                {
                    deploymentCalls++;
                    return deploymentCalls == 1
                        ? Task.FromResult(CreateResult(child))
                        : Task.FromException<Dl1DeveloperToolsDeploymentResult>(
                            new IOException("synthetic second-model failure"));
                },
                RollbackOverride = (receiptPath, _, _) =>
                {
                    rollbackOrder.Add(Path.GetFileNameWithoutExtension(receiptPath));
                    return Task.CompletedTask;
                },
            };

            Dl1DeveloperToolsBatchTransactionException error = await Assert.ThrowsAsync<
                Dl1DeveloperToolsBatchTransactionException>(() =>
                Dl1DeveloperToolsProjectDeployer.DeployBatchAsync(request));

            Assert.Equal([new string('a', 24)], rollbackOrder);
            Assert.Equal(Dl1DeveloperToolsBatchState.RolledBack, error.Receipt.State);
            Assert.NotNull(error.Receipt.RolledBackUtc);
            Assert.True(File.Exists(error.ReceiptPath));
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllBytesAsync(error.ReceiptPath));
            Assert.Equal(
                "RolledBack",
                json.RootElement.GetProperty("state").GetString());
            Assert.Contains(
                "synthetic second-model failure",
                json.RootElement.GetProperty("errors")[0].GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(projectRoot);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task SuccessfulBatchUsesOneProjectReceiptWithEveryModel()
    {
        string projectRoot = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1DeveloperToolsDeploymentRequest first = CreateChildRequest(
                projectRoot,
                "character_a",
                "ModelA",
                "LibraryA");
            Dl1DeveloperToolsDeploymentRequest second = CreateChildRequest(
                projectRoot,
                "character_b",
                "ModelB",
                "LibraryB");
            var request = new Dl1DeveloperToolsBatchRequest
            {
                Deployments = [first, second],
                PreflightOverride = (child, _) => Task.FromResult(CreatePlan(
                    child,
                    $"data/characters/animations/{child.AnimationLibraryName}.anm2")),
                DeploymentOverride = (child, _) => Task.FromResult(CreateResult(
                    child,
                    child.ModelResourceName == "ModelA"
                        ? new string('a', 24)
                        : new string('b', 24))),
            };

            Dl1DeveloperToolsBatchResult result =
                await Dl1DeveloperToolsProjectDeployer.DeployBatchAsync(request);

            Assert.True(result.Preflight.CanDeploy);
            Assert.Equal(Dl1DeveloperToolsBatchState.Committed, result.Receipt.State);
            Assert.Equal(2, result.Deployments.Length);
            Assert.Equal(2, result.Receipt.Deployments.Length);
            Assert.True(File.Exists(result.ReceiptPath));
            Assert.All(
                result.Receipt.Deployments,
                static item => Assert.StartsWith(
                    ".dl-reanimated/deployments/",
                    item.ReceiptRelativePath,
                    StringComparison.Ordinal));
            Dl1DeveloperToolsBatchReceipt restored =
                Dl1DeveloperToolsProjectDeployer
                    .LoadLatestActiveBatchReceipt(projectRoot)
                ?? throw new Xunit.Sdk.XunitException(
                    "The committed batch receipt was not restored.");
            Assert.Equal(result.Receipt.BatchId, restored.BatchId);
            Assert.Equal(result.ReceiptPath, restored.ReceiptPath);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(projectRoot);
        }
    }

    private static Dl1DeveloperToolsDeploymentRequest CreateChildRequest(
        string projectRoot,
        string characterId,
        string modelResourceName,
        string animationLibraryName) => new()
    {
        Model = null!,
        ProjectRoot = projectRoot,
        CompilerExecutablePath = "compiler.exe",
        RetailData0PakPath = "data0.pak",
        CharacterId = characterId,
        ModelResourceName = modelResourceName,
        AnimationLibraryName = animationLibraryName,
    };

    private static Dl1DeveloperToolsDeploymentPlan CreatePlan(
        Dl1DeveloperToolsDeploymentRequest request,
        string artifactPath) => new(
            request.CharacterId,
            request.ModelResourceName,
            request.AnimationLibraryName,
            $"data/characters/animations/animscripts/{request.AnimationLibraryName}.scr",
            [
                new Dl1DeveloperToolsDeploymentArtifact(
                    artifactPath,
                    Dl1DeploymentArtifactRole.Source,
                    Dl1DeploymentArtifactDisposition.Create,
                    Required: true,
                    ExistingFileIsOwned: false,
                    ExistingSha256: null,
                    PreparedSha256: new string('0', 64),
                    Description: "Synthetic batch artifact"),
            ],
            [],
            [],
            [],
            [],
            InstallLooseAnm2: true,
            ExportPortableAnimationRpack: true);

    private static Dl1DeveloperToolsDeploymentResult CreateResult(
        Dl1DeveloperToolsDeploymentRequest request,
        string? deploymentId = null)
    {
        deploymentId ??= new string('a', 24);
        string receiptPath = Path.Combine(
            request.ProjectRoot,
            ".dl-reanimated",
            "deployments",
            deploymentId + ".json");
        return new Dl1DeveloperToolsDeploymentResult(
            CreatePlan(
                request,
                $"data/characters/animations/{request.AnimationLibraryName}.anm2"),
            new Dl1DeveloperToolsDeploymentReceipt
            {
                DeploymentId = deploymentId,
                CharacterId = request.CharacterId,
                ModelResourceName = request.ModelResourceName,
                AnimationLibraryName = request.AnimationLibraryName,
                AnimationScriptRelativePath =
                    $"data/characters/animations/animscripts/{request.AnimationLibraryName}.scr",
                ModelCompilerFingerprint = new string('1', 64),
                AnimationCompilerFingerprint = new string('2', 64),
                CompletedUtc = DateTimeOffset.UtcNow,
            },
            receiptPath);
    }
}
