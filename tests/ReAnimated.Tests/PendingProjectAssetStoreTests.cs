using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class PendingProjectAssetStoreTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task UntitledAssetRecoveryMaterializesWithoutChangingIdentity()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string recoveryPath = Path.Combine(root, "recovery.json");
            var store = new PendingProjectAssetStore(recoveryPath);
            Guid assetId = Guid.NewGuid();
            const string relativePath =
                "Sources/Models/generic_character.dlrmodel";
            byte[] packageBytes = "generic portable model package"u8.ToArray();

            PendingProjectAssetReceipt receipt = await store.StageAsync(
                assetId,
                relativePath,
                packageBytes);
            string stagedPath = await store.ResolveAsync(receipt);
            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(stagedPath));
            Assert.Equal(assetId, receipt.AssetId);
            Assert.Equal(relativePath, receipt.RelativePath);

            var snapshotStore = new JsonWorkspaceStateStore(recoveryPath);
            snapshotStore.Save(new WorkspaceSnapshot(
                WorkspaceSnapshot.CurrentSchemaVersion,
                new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero),
                null,
                string.Empty,
                null,
                null,
                0,
                true,
                75.0f,
                0.01f,
                "Models",
                PendingAssets: [receipt]));
            WorkspaceSnapshot restored = Assert.IsType<WorkspaceSnapshot>(
                snapshotStore.Load());
            PendingProjectAssetReceipt restoredReceipt = Assert.Single(
                PendingProjectAssetStore.ValidateSnapshotReceipts(
                    restored.PendingAssets));
            Assert.Equal(assetId, restoredReceipt.AssetId);
            Assert.Equal(relativePath, restoredReceipt.RelativePath);

            string projectPath = Path.Combine(root, "project.dlraproj");
            string materialized = await store.MaterializeAsync(
                restoredReceipt,
                projectPath);
            Assert.Equal(
                Path.Combine(
                    root,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)),
                materialized);
            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(materialized));

            store.Delete(restoredReceipt);
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                store.ResolveAsync(restoredReceipt));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ConflictOrTamperRetainsFailClosedRecoveryReceipt()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var store = new PendingProjectAssetStore(
                Path.Combine(root, "recovery.json"));
            PendingProjectAssetReceipt receipt = await store.StageAsync(
                Guid.NewGuid(),
                "Sources/Models/generic_model.dlrmodel",
                "expected package bytes"u8.ToArray());
            string destination = Path.Combine(
                root,
                "Sources",
                "Models",
                "generic_model.dlrmodel");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, "different bytes");

            await Assert.ThrowsAsync<IOException>(() =>
                store.MaterializeAsync(
                    receipt,
                    Path.Combine(root, "project.dlraproj")));
            string stagedPath = await store.ResolveAsync(receipt);
            Assert.True(File.Exists(stagedPath));

            Assert.Throws<InvalidDataException>(() =>
                PendingProjectAssetStore.ValidateSnapshotReceipts(
                    [receipt, receipt]));
            await File.WriteAllTextAsync(stagedPath, "tampered");
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.ResolveAsync(receipt));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }
}
