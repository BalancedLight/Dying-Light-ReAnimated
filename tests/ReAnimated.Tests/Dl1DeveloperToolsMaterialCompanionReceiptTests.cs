using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1DeveloperToolsMaterialCompanionReceiptTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    [Theory]
    [InlineData("assets_pc/local_dx11.mp")]
    [InlineData("assets_pc/local_dx11_refs.mp")]
    [InlineData("assets_pc/local_dx11_debug.mp")]
    [InlineData("assets_pc/local_dx11_refs_debug.mp")]
    public void RecognizesExactNativeMaterialGraphSharedPaths(string relative)
    {
        WithReceipt(relative, new string('d', 64), receipt =>
            Assert.NotNull(Dl1DeveloperToolsProjectDeployer.LoadLatestActiveReceipt(receipt)));
    }

    [Theory]
    [InlineData("assets_pc/unrelated.mp")]
    [InlineData("assets_pc/local_dx11_refs.mp.other")]
    [InlineData("data/local_dx11_refs.mp")]
    public void RejectsUnrelatedSharedPaths(string relative)
    {
        WithReceipt(relative, new string('d', 64), receipt =>
            Assert.Null(Dl1DeveloperToolsProjectDeployer.LoadLatestActiveReceipt(receipt)));
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("")]
    public void CompanionAllowlistRetainsDeployedHashValidation(string hash)
    {
        WithReceipt("assets_pc/local_dx11_refs.mp", hash, receipt =>
            Assert.Null(Dl1DeveloperToolsProjectDeployer.LoadLatestActiveReceipt(receipt)));
    }

    private static void WithReceipt(string relative, string hash, Action<string> assert)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string id = new('a', 24);
            string receipts = Path.Combine(directory, ".dl-reanimated", "deployments");
            Directory.CreateDirectory(receipts);
            var receipt = new Dl1DeveloperToolsDeploymentReceipt
            {
                SchemaVersion = 1,
                DeploymentId = id,
                CharacterId = "generic_character",
                ModelResourceName = "generic_model",
                AnimationLibraryName = "generic_bank",
                AnimationScriptRelativePath = "data/characters/animations/animscripts/generic_bank.scr",
                ModelCompilerFingerprint = new string('b', 64),
                AnimationCompilerFingerprint = new string('c', 64),
                CompletedUtc = DateTimeOffset.UtcNow,
                Artifacts = [new(relative, Dl1DeploymentArtifactRole.Shared, hash, null, null, true, true)],
            };
            File.WriteAllText(Path.Combine(receipts, id + ".json"), JsonSerializer.Serialize(receipt, SerializerOptions));
            assert(directory);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
}