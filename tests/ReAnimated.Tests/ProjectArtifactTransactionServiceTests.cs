using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using ReAnimated.Codecs.ProjectArtifacts;

namespace ReAnimated.Tests;

public sealed class ProjectArtifactTransactionServiceTests : IDisposable
{
    private readonly List<string> _roots = [];

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CommitAndRollbackPreserveOwnedArtifactsAndReceiptHashes()
    {
        string root = CreateProjectRoot();
        string replaced = ProjectPath(root, "data/content/existing.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(replaced)!);
        await File.WriteAllBytesAsync(replaced, Bytes("before"));

        Guid ownerProjectId = Guid.NewGuid();
        ProjectArtifactTransactionResult result =
            await ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = ownerProjectId,
                    Artifacts =
                    [
                        Artifact("data/content/existing.bin", "after"),
                        Artifact("data/content/new.bin", "created"),
                    ],
                });

        Assert.Equal(Bytes("after"), await File.ReadAllBytesAsync(replaced));
        Assert.Equal(
            Bytes("created"),
            await File.ReadAllBytesAsync(
                ProjectPath(root, "data/content/new.bin")));
        Assert.StartsWith(
            $".dl-reanimated/receipts/project-artifacts/{ownerProjectId:N}/",
            result.ReceiptRelativePath,
            StringComparison.Ordinal);
        Assert.Equal(
            result.Receipt.ReceiptContentSha256,
            ProjectArtifactTransactionService
                .ComputeReceiptContentSha256(result.Receipt));

        ProjectArtifactTransactionReceipt read =
            await ProjectArtifactTransactionService.ReadReceiptAsync(
                root,
                ownerProjectId,
                result.ReceiptRelativePath);
        AssertReceiptsEquivalent(result.Receipt, read);
        ProjectArtifactTransactionReceiptArtifact replacedReceipt =
            Assert.Single(
                read.Artifacts,
                static artifact => artifact.RelativePath ==
                    "data/content/existing.bin");
        Assert.False(replacedReceipt.CreatedByTransaction);
        Assert.True(replacedReceipt.ChangedByTransaction);
        Assert.NotNull(replacedReceipt.BackupRelativePath);
        Assert.Equal(
            Bytes("before"),
            await File.ReadAllBytesAsync(ProjectPath(
                root,
                replacedReceipt.BackupRelativePath!)));

        ProjectArtifactTransactionReceipt rolledBack =
            await ProjectArtifactTransactionService.RollbackAsync(
                root,
                ownerProjectId,
                result.ReceiptRelativePath);

        Assert.NotNull(rolledBack.RolledBackUtc);
        Assert.Equal(Bytes("before"), await File.ReadAllBytesAsync(replaced));
        Assert.False(File.Exists(ProjectPath(root, "data/content/new.bin")));
        Assert.True(File.Exists(ProjectPath(
            root,
            replacedReceipt.BackupRelativePath!)));
        Assert.Equal(
            rolledBack.ReceiptContentSha256,
            ProjectArtifactTransactionService
                .ComputeReceiptContentSha256(rolledBack));

        ProjectArtifactTransactionReceipt repeated =
            await ProjectArtifactTransactionService.RollbackAsync(
                root,
                ownerProjectId,
                result.ReceiptRelativePath);
        AssertReceiptsEquivalent(rolledBack, repeated);
    }

    [Theory]
    [Trait("ValidationTier", "Hermetic")]
    [InlineData("../escape.bin")]
    [InlineData("/rooted.bin")]
    [InlineData("data//duplicate-separator.bin")]
    [InlineData("data/./dot.bin")]
    [InlineData("data/../parent.bin")]
    [InlineData("data\\backslash.bin")]
    [InlineData("data/trailing./file.bin")]
    [InlineData(".dl-reanimated/receipts/overwrite.json")]
    public async Task CommitRejectsNoncanonicalOrInternalPaths(
        string relativePath)
    {
        string root = CreateProjectRoot();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = Guid.NewGuid(),
                    Artifacts = [Artifact(relativePath, "content")],
                }));

        Assert.Empty(Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CommitRejectsRootedAndCaseInsensitiveDuplicatePaths()
    {
        string root = CreateProjectRoot();
        string rooted = Path.Combine(root, "outside.bin");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = Guid.NewGuid(),
                    Artifacts = [Artifact(rooted, "content")],
                }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = Guid.NewGuid(),
                    Artifacts =
                    [
                        Artifact("data/content/clip.anm2", "one"),
                        Artifact("DATA/CONTENT/CLIP.ANM2", "two"),
                    ],
                }));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CommitRejectsArtifactCountBeyondBound()
    {
        string root = CreateProjectRoot();
        ImmutableArray<ProjectArtifactWrite> artifacts = Enumerable
            .Range(
                0,
                ProjectArtifactTransactionService.MaximumArtifactCount + 1)
            .Select(index => Artifact(
                $"data/generated/{index:D5}.bin",
                string.Empty))
            .ToImmutableArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = Guid.NewGuid(),
                    Artifacts = artifacts,
                }));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MidCommitFailureRestoresEveryPublishedDestination()
    {
        string root = CreateProjectRoot();
        string existing = ProjectPath(root, "data/generated/a.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllBytesAsync(existing, Bytes("original"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = Guid.NewGuid(),
                    Artifacts =
                    [
                        Artifact("data/generated/a.bin", "replacement"),
                        Artifact("data/generated/b.bin", "created"),
                    ],
                    ArtifactPublishedObserver = static (
                        _,
                        index,
                        _) => index == 0
                            ? Task.FromException(
                                new InvalidOperationException(
                                    "Injected publish failure."))
                            : Task.CompletedTask,
                }));

        Assert.Equal(
            Bytes("original"),
            await File.ReadAllBytesAsync(existing));
        Assert.False(File.Exists(ProjectPath(root, "data/generated/b.bin")));
        string receiptRoot = ProjectPath(
            root,
            ".dl-reanimated/receipts/project-artifacts");
        Assert.Empty(Directory.EnumerateFiles(
            receiptRoot,
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RollbackRefusesTamperedReceiptBeforeChangingArtifacts()
    {
        string root = CreateProjectRoot();
        Guid ownerProjectId = Guid.NewGuid();
        ProjectArtifactTransactionResult result =
            await ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = ownerProjectId,
                    Artifacts =
                    [
                        Artifact("data/generated/a.bin", "deployed-a"),
                        Artifact("data/generated/b.bin", "deployed-b"),
                    ],
                });
        string receiptPath = ProjectPath(root, result.ReceiptRelativePath);
        JsonObject receipt = JsonNode.Parse(
                await File.ReadAllTextAsync(receiptPath))!
            .AsObject();
        receipt["receiptContentSha256"] = new string('0', 64);
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ProjectArtifactTransactionService.RollbackAsync(
                root,
                ownerProjectId,
                result.ReceiptRelativePath));

        Assert.Equal(
            Bytes("deployed-a"),
            await File.ReadAllBytesAsync(
                ProjectPath(root, "data/generated/a.bin")));
        Assert.Equal(
            Bytes("deployed-b"),
            await File.ReadAllBytesAsync(
                ProjectPath(root, "data/generated/b.bin")));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RollbackPreflightsAllArtifactsBeforeAnyMutation()
    {
        string root = CreateProjectRoot();
        Guid ownerProjectId = Guid.NewGuid();
        ProjectArtifactTransactionResult result =
            await ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = ownerProjectId,
                    Artifacts =
                    [
                        Artifact("data/generated/a.bin", "deployed-a"),
                        Artifact("data/generated/b.bin", "deployed-b"),
                    ],
                });
        string first = ProjectPath(root, "data/generated/a.bin");
        string second = ProjectPath(root, "data/generated/b.bin");
        await File.WriteAllBytesAsync(second, Bytes("external-change"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ProjectArtifactTransactionService.RollbackAsync(
                root,
                ownerProjectId,
                result.ReceiptRelativePath));

        Assert.Equal(Bytes("deployed-a"), await File.ReadAllBytesAsync(first));
        Assert.Equal(
            Bytes("external-change"),
            await File.ReadAllBytesAsync(second));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task RollbackRefusesDirectoryCollisionBeforeAnyMutation()
    {
        string root = CreateProjectRoot();
        Guid ownerProjectId = Guid.NewGuid();
        ProjectArtifactTransactionResult result =
            await ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = ownerProjectId,
                    Artifacts =
                    [
                        Artifact("data/generated/a.bin", "deployed-a"),
                        Artifact("data/generated/b.bin", "deployed-b"),
                    ],
                });
        string first = ProjectPath(root, "data/generated/a.bin");
        string second = ProjectPath(root, "data/generated/b.bin");
        File.Delete(second);
        Directory.CreateDirectory(second);

        InvalidDataException error = await Assert.ThrowsAsync<
            InvalidDataException>(
            () => ProjectArtifactTransactionService.RollbackAsync(
                root,
                ownerProjectId,
                result.ReceiptRelativePath));

        Assert.Contains(
            "became a directory",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Bytes("deployed-a"), await File.ReadAllBytesAsync(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task UnchangedArtifactDoesNotCreateBackupOrChangeOnRollback()
    {
        string root = CreateProjectRoot();
        string path = ProjectPath(root, "data/generated/stable.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Bytes("same"));
        Guid ownerProjectId = Guid.NewGuid();

        ProjectArtifactTransactionResult result =
            await ProjectArtifactTransactionService.CommitAsync(
                new ProjectArtifactTransactionRequest
                {
                    ProjectRoot = root,
                    OwnerProjectId = ownerProjectId,
                    Artifacts =
                    [
                        Artifact("data/generated/stable.bin", "same"),
                    ],
                });

        ProjectArtifactTransactionReceiptArtifact artifact =
            Assert.Single(result.Receipt.Artifacts);
        Assert.False(artifact.CreatedByTransaction);
        Assert.False(artifact.ChangedByTransaction);
        Assert.Null(artifact.BackupRelativePath);
        await File.WriteAllBytesAsync(path, Bytes("external-after-export"));
        await ProjectArtifactTransactionService.RollbackAsync(
            root,
            ownerProjectId,
            result.ReceiptRelativePath);
        Assert.Equal(
            Bytes("external-after-export"),
            await File.ReadAllBytesAsync(path));
    }

    public void Dispose()
    {
        foreach (string root in _roots)
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private string CreateProjectRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dl-reanimated-project-artifact-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static ProjectArtifactWrite Artifact(
        string relativePath,
        string content) =>
        new(relativePath, Bytes(content));

    private static byte[] Bytes(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static string ProjectPath(
        string root,
        string relativePath) =>
        Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void AssertReceiptsEquivalent(
        ProjectArtifactTransactionReceipt expected,
        ProjectArtifactTransactionReceipt actual)
    {
        Assert.Equal(expected.Format, actual.Format);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.OwnerProjectId, actual.OwnerProjectId);
        Assert.Equal(expected.CompletedUtc, actual.CompletedUtc);
        Assert.Equal(expected.RolledBackUtc, actual.RolledBackUtc);
        Assert.Equal(
            expected.ReceiptContentSha256,
            actual.ReceiptContentSha256);
        Assert.Equal(expected.Artifacts.Length, actual.Artifacts.Length);
        for (int index = 0; index < expected.Artifacts.Length; index++)
        {
            Assert.Equal(expected.Artifacts[index], actual.Artifacts[index]);
        }
    }
}
