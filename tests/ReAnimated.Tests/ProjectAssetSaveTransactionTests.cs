using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectAssetSaveTransactionTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task FailedCopyRollsBackEarlierCopiesWithoutChangingSources()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string sourceRoot = Path.Combine(directory, "source");
            Directory.CreateDirectory(sourceRoot);
            byte[] bytes = [1, 2, 3];
            string first = Path.Combine(sourceRoot, "first.bin");
            string second = Path.Combine(sourceRoot, "second.bin");
            await File.WriteAllBytesAsync(first, bytes);
            await File.WriteAllBytesAsync(second, bytes);
            var project = DlraProject.Create("Generic project") with
            {
                Assets = [Asset("first.bin", bytes), Asset("second.bin", bytes)],
            };
            string destinationRoot = Path.Combine(directory, "destination");
            var store = new PendingProjectAssetStore(Path.Combine(directory, "state.json"));
            await using (ProjectAssetSaveTransaction transaction = await ProjectAssetSaveTransaction.PrepareAsync(
                             project, Path.Combine(sourceRoot, "project.dlraproj"),
                             Path.Combine(destinationRoot, "project.dlraproj"), store,
                             new Dictionary<Guid, PendingProjectAssetReceipt>(), CancellationToken.None))
            {
                File.Delete(second);
                await Assert.ThrowsAsync<InvalidDataException>(() => transaction.MaterializeAsync(CancellationToken.None));
                Assert.True(File.Exists(Path.Combine(destinationRoot, "first.bin")));
            }
            Assert.False(File.Exists(Path.Combine(destinationRoot, "first.bin")));
            Assert.Equal(bytes, File.ReadAllBytes(first));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("Sources/../outside.bin")]
    [InlineData("Sources/file.bin:extra")]
    [Trait("ValidationTier", "Hermetic")]
    public async Task AssetPathsCannotEscapeProjectRoots(string relativePath)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var project = DlraProject.Create("Generic project") with { Assets = [Asset(relativePath, [1])] };
            await Assert.ThrowsAsync<InvalidDataException>(() => ProjectAssetSaveTransaction.PrepareAsync(project,
                Path.Combine(directory, "source", "project.dlraproj"),
                Path.Combine(directory, "destination", "project.dlraproj"),
                new PendingProjectAssetStore(Path.Combine(directory, "state.json")),
                new Dictionary<Guid, PendingProjectAssetReceipt>(), CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(directory, "destination")));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ChangedSourceIsRejectedBeforeCreatingDestinationFiles()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string sourceRoot = Path.Combine(directory, "source");
            Directory.CreateDirectory(sourceRoot);
            await File.WriteAllBytesAsync(Path.Combine(sourceRoot, "source.bin"), new byte[] { 2 });
            var project = DlraProject.Create("Generic project") with { Assets = [Asset("source.bin", [1])] };
            await Assert.ThrowsAsync<InvalidDataException>(() => ProjectAssetSaveTransaction.PrepareAsync(project,
                Path.Combine(sourceRoot, "project.dlraproj"), Path.Combine(directory, "destination", "project.dlraproj"),
                new PendingProjectAssetStore(Path.Combine(directory, "state.json")),
                new Dictionary<Guid, PendingProjectAssetReceipt>(), CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(directory, "destination")));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static ProjectAssetReference Asset(string path, byte[] bytes) => new()
    {
        Kind = ProjectAssetKind.SourceAnimation,
        RelativePath = path,
        ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
    };
}
