using ReAnimated.Cli;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class SourceModelInspectionCommandTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public async Task SourceInspectionDoesNotChangeInputAndDoesNotCreateCompiledProducts()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var source = await Dl1SourceModelWriter.WriteAsync(new Dl1SourceModelBuildRequest
            {
                Model = CustomModelSchema2MorphTests.CreateMorphModel(),
                OutputDirectory = root,
                ResourceName = "inspection_model",
            });
            byte[] before = await File.ReadAllBytesAsync(source.SourceMshPath);
            string[] beforeFiles = Directory.GetFiles(root).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(0, await CliApplication.RunAsync(["inspect-source-msh", source.SourceMshPath]));
            Assert.Equal(before, await File.ReadAllBytesAsync(source.SourceMshPath));
            Assert.Equal(beforeFiles, Directory.GetFiles(root).Order(StringComparer.Ordinal).ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public async Task MalformedSourceReturnsCliErrorAndPreservesBytes()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(root, "invalid.msh");
            byte[] bytes = "MSH\0"u8.ToArray();
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Equal(2, await CliApplication.RunAsync(["inspect-source-msh", path]));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public async Task CancellationReturnsTheStandardCliCancellationCode()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(root, "cancelled.msh");
            await File.WriteAllBytesAsync(path, "MSH\0"u8.ToArray());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Equal(130, await CliApplication.RunAsync(["inspect-source-msh", path], cancellation.Token));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(root); }
    }
}
