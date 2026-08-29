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

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task MaterializeReplacesExistingBytesWhenReplacementIsRequested()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var store = new PendingProjectAssetStore(
                Path.Combine(root, "recovery.json"));
            Guid assetId = Guid.NewGuid();
            const string relativePath = "Sources/qiqinew-model.dlrmodel";
            string projectPath = Path.Combine(root, "project.dlraproj");
            string destination = Path.Combine(
                root,
                "Sources",
                "qiqinew-model.dlrmodel");

            PendingProjectAssetReceipt first = await store.StageAsync(
                assetId,
                relativePath,
                "first authored package"u8.ToArray());
            Assert.Equal(
                destination,
                await store.MaterializeAsync(first, projectPath));

            // Editing the model keeps the same owned path and changes the bytes.
            byte[] edited = "edited authored package with a texture"u8.ToArray();
            PendingProjectAssetReceipt second = await store.StageAsync(
                assetId,
                relativePath,
                edited);
            string materialized = await store.MaterializeAsync(
                second,
                projectPath,
                replaceExisting: true);

            Assert.Equal(destination, materialized);
            Assert.Equal(edited, await File.ReadAllBytesAsync(materialized));
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(destination)!,
                "*.tmp",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task MaterializeLeavesIdenticalBytesUntouchedInBothModes()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var store = new PendingProjectAssetStore(
                Path.Combine(root, "recovery.json"));
            const string relativePath = "Sources/unchanged.dlrmodel";
            string projectPath = Path.Combine(root, "project.dlraproj");
            PendingProjectAssetReceipt receipt = await store.StageAsync(
                Guid.NewGuid(),
                relativePath,
                "stable package bytes"u8.ToArray());

            string materialized = await store.MaterializeAsync(
                receipt,
                projectPath);
            DateTime firstWriteUtc = File.GetLastWriteTimeUtc(materialized);

            Assert.Equal(
                materialized,
                await store.MaterializeAsync(receipt, projectPath));
            Assert.Equal(
                materialized,
                await store.MaterializeAsync(
                    receipt,
                    projectPath,
                    replaceExisting: true));
            Assert.Equal(
                firstWriteUtc,
                File.GetLastWriteTimeUtc(materialized));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ConflictMessageNamesTheDestinationAndBothHashes()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var store = new PendingProjectAssetStore(
                Path.Combine(root, "recovery.json"));
            PendingProjectAssetReceipt receipt = await store.StageAsync(
                Guid.NewGuid(),
                "Sources/foreign.dlrmodel",
                "staged package"u8.ToArray());
            string destination = Path.Combine(
                root,
                "Sources",
                "foreign.dlrmodel");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, "unrelated bytes");

            IOException failure = await Assert.ThrowsAsync<IOException>(() =>
                store.MaterializeAsync(
                    receipt,
                    Path.Combine(root, "project.dlraproj")));

            Assert.Contains(destination, failure.Message, StringComparison.Ordinal);
            Assert.Contains(
                receipt.ContentSha256,
                failure.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void StaleTemporaryFilesAreSweptAndActiveOnesAreNot()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string sources = Path.Combine(root, "Sources");
            Directory.CreateDirectory(sources);
            string stale = Path.Combine(sources, ".model.fbx.abandoned.tmp");
            string recent = Path.Combine(sources, ".model.fbx.inflight.tmp");
            string real = Path.Combine(sources, "model.fbx");
            File.WriteAllText(stale, "abandoned");
            File.WriteAllText(recent, "in flight");
            File.WriteAllText(real, "a real project source");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-48));

            // A scratch file still held open by a concurrent write must not
            // fail the sweep.
            string locked = Path.Combine(sources, ".model.fbx.locked.tmp");
            using (var handle = new FileStream(
                       locked,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                handle.WriteByte(0x42);
                handle.Flush();
                File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddHours(-48));

                int removed = ProjectSourceImporter
                    .SweepStaleProjectSourceTemporaryFiles(
                        Path.Combine(root, "project.dlraproj"));

                Assert.Equal(1, removed);
            }

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(locked));
            Assert.True(File.Exists(real));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void SweepIgnoresMissingDirectoriesAndUntitledProjects()
    {
        string root = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Assert.Equal(
                0,
                ProjectSourceImporter.SweepStaleProjectSourceTemporaryFiles(
                    Path.Combine(root, "project.dlraproj")));
            Assert.Equal(
                0,
                ProjectSourceImporter.SweepStaleProjectSourceTemporaryFiles(
                    string.Empty));
            Assert.Equal(
                0,
                ProjectSourceImporter.SweepStaleTemporaryFiles(
                    Path.Combine(root, "does-not-exist")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(root);
        }
    }
}
