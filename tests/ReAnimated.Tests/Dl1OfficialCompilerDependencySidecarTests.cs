using System.Security.Cryptography;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1OfficialCompilerDependencySidecarTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task GenuineCompilerSidecarIsPublishedAndHashValidated()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string compiler = Path.Combine(directory, "compiler");
            string output = Path.Combine(directory, "output");
            Directory.CreateDirectory(compiler);
            string objectPath = Path.Combine(compiler, "GenericModel.msh_obj");
            string sidecarPath = objectPath + "_dep";
            await File.WriteAllTextAsync(objectPath, "compiled model");
            byte[] dependency = "compiler-emitted dependency graph"u8.ToArray();
            await File.WriteAllBytesAsync(sidecarPath, dependency);

            Dl1OfficialCompilerDependencySidecar published = Assert.Single(
                await Dl1OfficialCompilerDependencySidecarCodec.PublishEmittedAsync(
                    [objectPath],
                    output));

            Assert.Equal("GenericModel.msh_obj", published.ObjectFileName);
            Assert.Equal("GenericModel.msh_obj_dep", published.SidecarFileName);
            Assert.Equal(dependency, await File.ReadAllBytesAsync(published.Path));
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData(dependency)),
                published.Sha256);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task MissingSidecarIsNotFabricatedAndEmptySidecarFailsClosed()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string objectPath = Path.Combine(directory, "generic.anm2_obj");
            string output = Path.Combine(directory, "output");
            await File.WriteAllTextAsync(objectPath, "compiled animation");

            Assert.Empty(await Dl1OfficialCompilerDependencySidecarCodec.PublishEmittedAsync(
                [objectPath],
                output));
            Assert.False(File.Exists(Path.Combine(output, "generic.anm2_obj_dep")));

            await File.WriteAllBytesAsync(objectPath + "_dep", []);
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Dl1OfficialCompilerDependencySidecarCodec.PublishEmittedAsync(
                    [objectPath],
                    output));
            Assert.Contains("between 1", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
