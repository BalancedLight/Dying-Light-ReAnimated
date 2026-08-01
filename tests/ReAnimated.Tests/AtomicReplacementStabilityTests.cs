using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AtomicReplacementStabilityTests
{
    [Fact]
    public void BlockedProjectReplacementPreservesOriginalAndRemovesTemporaryFile()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "locked.dlraproj");
            ProjectSerializer.SaveAtomic(
                DlraProject.Create("Original"),
                path);

            using (var blocker = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                Exception exception = Assert.ThrowsAny<Exception>(
                    () => ProjectSerializer.SaveAtomic(
                        DlraProject.Create("Replacement"),
                        path));
                Assert.True(
                    exception is IOException or UnauthorizedAccessException,
                    $"Expected a filesystem replacement failure, received {exception.GetType().Name}.");
            }

            Assert.Equal("Original", ProjectSerializer.Load(path).Name);
            Assert.Empty(
                Directory.EnumerateFiles(
                    directory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task BlockedOutputReplacementPreservesOriginalAndRemovesTemporaryFile()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "locked.rpack");
            byte[] original = "existing-output"u8.ToArray();
            await File.WriteAllBytesAsync(path, original);
            byte[] replacement = Enumerable.Repeat(
                    (byte)0x5A,
                    1024 * 1024)
                .ToArray();

            using (var blocker = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                Exception exception = await Assert.ThrowsAnyAsync<Exception>(
                    async () => await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                        path,
                        replacement));
                Assert.True(
                    exception is IOException or UnauthorizedAccessException,
                    $"Expected a filesystem replacement failure, received {exception.GetType().Name}.");
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Empty(
                Directory.EnumerateFiles(
                    directory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CanceledOutputWritePreservesExistingDestinationAndCleansTemporaryFile()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "cancelled.rpack");
            byte[] original = "retained-output"u8.ToArray();
            await File.WriteAllBytesAsync(path, original);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                    path,
                    new byte[1024 * 1024],
                    cancellation.Token));

            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Empty(
                Directory.EnumerateFiles(
                    directory,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
