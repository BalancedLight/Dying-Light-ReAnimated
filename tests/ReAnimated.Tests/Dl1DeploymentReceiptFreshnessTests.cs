using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1DeploymentReceiptFreshnessTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task ReceiptFreshnessReportsChangedAndMissingOwnedArtifacts()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string project = Path.Combine(directory, "project");
            string relative = "data/characters/animations/generic_clip.anm2";
            string path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] deployed = "generic deployed animation"u8.ToArray();
            await File.WriteAllBytesAsync(path, deployed);
            string hash = Convert.ToHexStringLower(SHA256.HashData(deployed));
            var receipt = new Dl1DeveloperToolsDeploymentReceipt
            {
                DeploymentId = "0123456789abcdef01234567",
                CharacterId = "generic_character",
                ModelResourceName = "GenericModel",
                AnimationLibraryName = "GenericLibrary",
                AnimationScriptRelativePath =
                    "data/characters/animations/animscripts/GenericLibrary.scr",
                ModelCompilerFingerprint = new string('a', 64),
                AnimationCompilerFingerprint = new string('b', 64),
                CompletedUtc = DateTimeOffset.UnixEpoch,
                Artifacts =
                [
                    new Dl1DeveloperToolsDeploymentReceiptArtifact(
                        relative,
                        Dl1DeploymentArtifactRole.Source,
                        hash,
                        null,
                        null,
                        CreatedByDeployment: true,
                        ChangedByDeployment: true),
                ],
            };

            Dl1DeploymentReceiptFreshness fresh =
                Dl1DeveloperToolsProjectDeployer.InspectDeploymentReceiptFreshness(
                    receipt,
                    project);
            Assert.False(fresh.IsStale);
            Assert.Empty(fresh.StaleArtifactPaths);

            await File.WriteAllTextAsync(path, "changed outside deployment");
            Dl1DeploymentReceiptFreshness changed =
                Dl1DeveloperToolsProjectDeployer.InspectDeploymentReceiptFreshness(
                    receipt,
                    project);
            Assert.True(changed.IsStale);
            Assert.Equal([relative], changed.StaleArtifactPaths.ToArray());

            File.Delete(path);
            Dl1DeploymentReceiptFreshness missing =
                Dl1DeveloperToolsProjectDeployer.InspectDeploymentReceiptFreshness(
                    receipt,
                    project);
            Assert.True(missing.IsStale);
            Assert.Equal([relative], missing.StaleArtifactPaths.ToArray());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
