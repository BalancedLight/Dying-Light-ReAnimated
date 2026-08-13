using System.Security.Cryptography;
using System.Text.Json;

namespace ReAnimated.Tests;

public sealed class ExternalCorpusManifestTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void MissingDefaultManifestReturnsNull()
    {
        Assert.Null(ExternalCorpusManifest.LoadOptional(_directory, null));
    }

    [Fact]
    public void ExplicitMissingManifestFailsActionably()
    {
        string path = Path.Combine(_directory, "missing.json");
        FileNotFoundException exception = Assert.Throws<FileNotFoundException>(
            () => ExternalCorpusManifest.LoadOptional(_directory, path));

        Assert.Contains(
            ExternalCorpusManifest.ManifestEnvironmentVariable,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedManifestFailsActionably()
    {
        string path = Path.Combine(_directory, "corpora.json");
        File.WriteAllText(path, "{");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ExternalCorpusManifest.LoadOptional(_directory, path));

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredControlsValidateFilesAndExpectations()
    {
        string sourcePath = Path.Combine(_directory, "control.fbx");
        await File.WriteAllBytesAsync(sourcePath, "external-control"u8.ToArray());
        string hash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)))
            .ToLowerInvariant();
        string manifestPath = Path.Combine(_directory, "corpora.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                format = ExternalCorpusManifest.Format,
                controls = new[]
                {
                    new
                    {
                        id = "animation-domain-control-01",
                        kind = "animation-domain",
                        filePath = "control.fbx",
                        sha256 = hash,
                        expectations = new
                        {
                            fileBytes = 16,
                            rigBoneCount = 1,
                            animationStackName = "synthetic",
                            frameCount = 1,
                            changingCurveCount = 0,
                        },
                    },
                },
            }));

        ExternalCorpusManifest manifest = Assert.IsType<ExternalCorpusManifest>(
            ExternalCorpusManifest.LoadOptional(_directory, manifestPath));
        ExternalCorpusControl control = Assert.Single(
            manifest.RequireControls("animation-domain"));

        Assert.Equal(Path.GetFullPath(sourcePath), control.RequireExistingFile());
        Assert.Equal(16, control.RequireInt64("fileBytes"));
        Assert.Equal("synthetic", control.RequireString("animationStackName"));
        await control.VerifySha256Async(control.RequireExistingFile());
    }

    [Fact]
    public async Task ConfiguredMissingControlFileFailsActionably()
    {
        string manifestPath = Path.Combine(_directory, "corpora.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                format = ExternalCorpusManifest.Format,
                controls = new[]
                {
                    new
                    {
                        id = "retarget-full-body",
                        kind = "retarget",
                        filePath = "missing.fbx",
                        sha256 = new string('a', 64),
                        expectations = new { sampleFrame = 1 },
                    },
                },
            }));
        ExternalCorpusManifest manifest = Assert.IsType<ExternalCorpusManifest>(
            ExternalCorpusManifest.LoadOptional(_directory, manifestPath));

        FileNotFoundException exception = Assert.Throws<FileNotFoundException>(
            () => manifest.RequireControl("retarget-full-body").RequireExistingFile());

        Assert.Contains("retarget-full-body", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        RpackTestData.DeleteTemporaryDirectory(_directory);
    }
}
