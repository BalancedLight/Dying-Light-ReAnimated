using System.Runtime.CompilerServices;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class RetailAssetCatalogPersistenceTests
{
    [Fact]
    public async Task UnchangedBaseAndUserPacksRestoreWithoutEnumeration()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string basePack = await WritePackAsync(
                Path.Combine(directory, "base"),
                "shared_mesh",
                1);
            string userPack = await WritePackAsync(
                Path.Combine(directory, "user"),
                "shared_mesh",
                2);
            string database = Path.Combine(directory, "assets.sqlite");
            RpackSource[] sources =
            [
                new(basePack, 10),
                new(userPack, 100),
            ];

            await using (CountingRpackProvider first =
                         CreateProvider(sources))
            await using (RetailAssetSqliteIndex firstIndex =
                         new(database))
            {
                RetailAssetCatalog catalog =
                    await RetailAssetCatalog.BuildAsync(
                        [first],
                        firstIndex);
                Assert.False(catalog.WasRestoredFromPersistentIndex);
                Assert.Equal(1, first.EnumerationCount);
                Assert.Equal(
                    userPack,
                    Assert.IsType<RetailAssetRecord>(
                            catalog.Resolve(RetailAssetLogicalId.Rpack(
                                Rp6lResourceTypes.Mesh,
                                "shared_mesh")))
                        .Source.ContainerPath,
                    ignoreCase: true);
                Assert.Equal(2, Assert.Single(catalog.Conflicts)
                    .Shadowed.Count + 1);
            }

            await using CountingRpackProvider restoredProvider =
                CreateProvider(sources);
            await using RetailAssetSqliteIndex restoredIndex =
                new(database);
            RetailAssetCatalog restored =
                await RetailAssetCatalog.BuildAsync(
                    [restoredProvider],
                    restoredIndex);

            Assert.True(restored.WasRestoredFromPersistentIndex);
            Assert.Equal(0, restoredProvider.EnumerationCount);
            RetailAssetRecord winner = Assert.IsType<RetailAssetRecord>(
                restored.Resolve(RetailAssetLogicalId.Rpack(
                    Rp6lResourceTypes.Mesh,
                    "shared_mesh")));
            Assert.Equal(userPack, winner.Source.ContainerPath, ignoreCase: true);
            Assert.Equal(2, restored.GetCandidates(winner.Id.LogicalId).Count);
            await using Stream payload = await restored.OpenReadAsync(winner);
            Assert.Equal(2, payload.ReadByte());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task TimestampPreservingPackChangeInvalidatesSavedCatalog()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = await WritePackAsync(
                directory,
                "mesh_old",
                1);
            string database = Path.Combine(directory, "assets.sqlite");
            await BuildAndDisposeAsync(
                database,
                [new RpackSource(pack, 10)]);
            DateTime originalWriteTime =
                File.GetLastWriteTimeUtc(pack);
            long originalLength = new FileInfo(pack).Length;
            byte[] replacement = RpackTestData.BuildArchive(
                "mesh_new",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, [2])],
                RpackTestCompression.None);
            Assert.Equal(originalLength, replacement.LongLength);
            await File.WriteAllBytesAsync(pack, replacement);
            File.SetLastWriteTimeUtc(pack, originalWriteTime);

            await using CountingRpackProvider provider =
                CreateProvider([new RpackSource(pack, 10)]);
            await using RetailAssetSqliteIndex index =
                new(database);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    [provider],
                    index);

            Assert.False(catalog.WasRestoredFromPersistentIndex);
            Assert.Equal(1, provider.EnumerationCount);
            Assert.Null(catalog.Resolve(RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                "mesh_old")));
            Assert.NotNull(catalog.Resolve(RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                "mesh_new")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task SmallSourceFingerprintCoversBytesAfterFirstSampleWindow()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = Path.Combine(directory, "small-source.rpack");
            byte[] bytes = RpackTestData.BuildArchive(
                "mesh",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, [1])],
                RpackTestCompression.None);
            Array.Resize(ref bytes, 48 * 1024);
            await File.WriteAllBytesAsync(pack, bytes);
            DateTime originalWriteTime =
                File.GetLastWriteTimeUtc(pack);
            await using CountingRpackProvider provider =
                CreateProvider([new RpackSource(pack, 10)]);
            RetailAssetProviderSnapshot before =
                await provider.CaptureSnapshotAsync();

            bytes[32 * 1024] = 0x7F;
            await File.WriteAllBytesAsync(pack, bytes);
            File.SetLastWriteTimeUtc(pack, originalWriteTime);
            RetailAssetProviderSnapshot after =
                await provider.CaptureSnapshotAsync();

            Assert.NotEqual(
                Assert.Single(before.Sources).BoundedFingerprint,
                Assert.Single(after.Sources).BoundedFingerprint);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task LargeSourceFingerprintRemainsFiveBoundedWindows()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = Path.Combine(directory, "large-source.rpack");
            byte[] bytes = RpackTestData.BuildArchive(
                "mesh",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, [1])],
                RpackTestCompression.None);
            Array.Resize(ref bytes, 128 * 1024);
            await File.WriteAllBytesAsync(pack, bytes);
            DateTime originalWriteTime =
                File.GetLastWriteTimeUtc(pack);
            await using CountingRpackProvider provider =
                CreateProvider([new RpackSource(pack, 10)]);
            RetailAssetProviderSnapshot before =
                await provider.CaptureSnapshotAsync();

            bytes[20 * 1024] = 0x41;
            await File.WriteAllBytesAsync(pack, bytes);
            File.SetLastWriteTimeUtc(pack, originalWriteTime);
            RetailAssetProviderSnapshot outsideWindows =
                await provider.CaptureSnapshotAsync();
            Assert.Equal(
                Assert.Single(before.Sources).BoundedFingerprint,
                Assert.Single(outsideWindows.Sources).BoundedFingerprint);

            bytes[40 * 1024] = 0x42;
            await File.WriteAllBytesAsync(pack, bytes);
            File.SetLastWriteTimeUtc(pack, originalWriteTime);
            RetailAssetProviderSnapshot insideWindow =
                await provider.CaptureSnapshotAsync();
            Assert.NotEqual(
                Assert.Single(outsideWindows.Sources).BoundedFingerprint,
                Assert.Single(insideWindow.Sources).BoundedFingerprint);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task MissingUserPackInvalidatesProviderInventory()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string basePack = await WritePackAsync(
                Path.Combine(directory, "base"),
                "base_mesh",
                1);
            string userPack = await WritePackAsync(
                Path.Combine(directory, "user"),
                "user_mesh",
                2);
            string database = Path.Combine(directory, "assets.sqlite");
            await BuildAndDisposeAsync(
                database,
                [
                    new RpackSource(basePack, 10),
                    new RpackSource(userPack, 100),
                ]);
            File.Delete(userPack);

            await using CountingRpackProvider provider =
                CreateProvider([new RpackSource(basePack, 10)]);
            await using RetailAssetSqliteIndex index =
                new(database);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    [provider],
                    index);

            Assert.False(catalog.WasRestoredFromPersistentIndex);
            Assert.Equal(1, provider.EnumerationCount);
            Assert.Single(catalog.Assets);
            Assert.NotNull(catalog.Resolve(RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                "base_mesh")));
            Assert.Null(catalog.Resolve(RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                "user_mesh")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ConfiguredUserRootInventoryParticipatesInRestoreValidation()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string baseData = Path.Combine(directory, "DW", "Data");
            Directory.CreateDirectory(baseData);
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "DyingLightGame.exe"),
                []);
            using (ZipArchive archive = ZipFile.Open(
                       Path.Combine(directory, "DW", "Data0.pak"),
                       ZipArchiveMode.Create))
            {
            }

            await WriteNamedPackAsync(
                Path.Combine(baseData, "base.rpack"),
                "base_mesh",
                1);
            string userRoot = Path.Combine(directory, "authoring-packs");
            Directory.CreateDirectory(userRoot);
            await WriteNamedPackAsync(
                Path.Combine(userRoot, "user-a.rpack"),
                "user_mesh_a",
                2);
            string database = Path.Combine(directory, "assets.sqlite");

            await using (Dl1RetailProviderSet first =
                         Dl1RetailProviderSet.Create(
                             directory,
                             additionalRpackRoots: [userRoot]))
            await using (RetailAssetSqliteIndex firstIndex =
                         new(database))
            {
                RetailAssetCatalog catalog =
                    await RetailAssetCatalog.BuildAsync(
                        first.Providers,
                        firstIndex);
                Assert.False(catalog.WasRestoredFromPersistentIndex);
                Assert.NotNull(catalog.Resolve(
                    RetailAssetLogicalId.Rpack(
                        Rp6lResourceTypes.Mesh,
                        "user_mesh_a")));
            }

            await using (Dl1RetailProviderSet second =
                         Dl1RetailProviderSet.Create(
                             directory,
                             additionalRpackRoots: [userRoot]))
            await using (RetailAssetSqliteIndex secondIndex =
                         new(database))
            {
                RetailAssetCatalog restored =
                    await RetailAssetCatalog.BuildAsync(
                        second.Providers,
                        secondIndex);
                Assert.True(restored.WasRestoredFromPersistentIndex);
            }

            await WriteNamedPackAsync(
                Path.Combine(userRoot, "user-b.rpack"),
                "user_mesh_b",
                3);
            await using Dl1RetailProviderSet third =
                Dl1RetailProviderSet.Create(
                    directory,
                    additionalRpackRoots: [userRoot]);
            await using RetailAssetSqliteIndex thirdIndex =
                new(database);
            RetailAssetCatalog rebuilt =
                await RetailAssetCatalog.BuildAsync(
                    third.Providers,
                    thirdIndex);
            Assert.False(rebuilt.WasRestoredFromPersistentIndex);
            Assert.NotNull(rebuilt.Resolve(
                RetailAssetLogicalId.Rpack(
                    Rp6lResourceTypes.Mesh,
                    "user_mesh_b")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CorruptDatabaseFailsClosedRescansAndIsReplaced()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = await WritePackAsync(
                directory,
                "recoverable_mesh",
                7);
            string database = Path.Combine(directory, "assets.sqlite");
            RpackSource[] sources = [new(pack, 10)];
            await BuildAndDisposeAsync(database, sources);
            await File.WriteAllBytesAsync(
                database,
                "not sqlite"u8.ToArray());

            await using (CountingRpackProvider recoveringProvider =
                         CreateProvider(sources))
            await using (RetailAssetSqliteIndex recoveringIndex =
                         new(database))
            {
                RetailAssetCatalog recovered =
                    await RetailAssetCatalog.BuildAsync(
                        [recoveringProvider],
                        recoveringIndex);
                Assert.False(recovered.WasRestoredFromPersistentIndex);
                Assert.Equal(1, recoveringProvider.EnumerationCount);
                Assert.Single(recovered.Assets);
            }

            await using CountingRpackProvider restoredProvider =
                CreateProvider(sources);
            await using RetailAssetSqliteIndex restoredIndex =
                new(database);
            RetailAssetCatalog restored =
                await RetailAssetCatalog.BuildAsync(
                    [restoredProvider],
                    restoredIndex);
            Assert.True(restored.WasRestoredFromPersistentIndex);
            Assert.Equal(0, restoredProvider.EnumerationCount);
            Assert.Single(restored.Assets);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task MissingPersistedAssetRowFailsManifestAndRescans()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string basePack = await WritePackAsync(
                Path.Combine(directory, "base"),
                "base_mesh",
                1);
            string userPack = await WritePackAsync(
                Path.Combine(directory, "user"),
                "user_mesh",
                2);
            RpackSource[] sources =
            [
                new(basePack, 10),
                new(userPack, 100),
            ];
            string database = Path.Combine(directory, "assets.sqlite");
            await BuildAndDisposeAsync(database, sources);
            await ExecuteSqlAsync(
                database,
                "DELETE FROM retail_asset_identities WHERE precedence = 100;");

            await using CountingRpackProvider provider =
                CreateProvider(sources);
            await using RetailAssetSqliteIndex index =
                new(database);
            RetailAssetCatalog rebuilt =
                await RetailAssetCatalog.BuildAsync(
                    [provider],
                    index);

            Assert.False(rebuilt.WasRestoredFromPersistentIndex);
            Assert.Equal(1, provider.EnumerationCount);
            Assert.Equal(2, rebuilt.Assets.Count);
            Assert.NotNull(rebuilt.Resolve(RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                "user_mesh")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task EmbeddedNullPersistedTextFailsClosedAndRescans()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = await WritePackAsync(
                directory,
                "bounded_mesh",
                1);
            RpackSource[] sources = [new(pack, 10)];
            string database = Path.Combine(directory, "assets.sqlite");
            await BuildAndDisposeAsync(database, sources);
            await using (SqliteConnection connection =
                         CreateTestSqliteConnection(database))
            {
                await connection.OpenAsync();
                await using SqliteCommand command =
                    connection.CreateCommand();
                command.CommandText = """
                    UPDATE retail_asset_identities
                    SET display_name = $display;
                    """;
                command.Parameters.AddWithValue(
                    "$display",
                    "bounded\0suffix");
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using CountingRpackProvider provider =
                CreateProvider(sources);
            await using RetailAssetSqliteIndex index =
                new(database);
            RetailAssetCatalog rebuilt =
                await RetailAssetCatalog.BuildAsync(
                    [provider],
                    index);
            Assert.False(rebuilt.WasRestoredFromPersistentIndex);
            Assert.Equal(1, provider.EnumerationCount);
            Assert.Equal(
                "bounded_mesh",
                Assert.Single(rebuilt.Assets).DisplayName);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task OversizedPersistedTextBytesFailClosedBeforeRead()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = await WritePackAsync(
                directory,
                "oversized_mesh",
                1);
            RpackSource[] sources = [new(pack, 10)];
            string database = Path.Combine(directory, "assets.sqlite");
            await BuildAndDisposeAsync(database, sources);
            await using (SqliteConnection connection =
                         CreateTestSqliteConnection(database))
            {
                await connection.OpenAsync();
                await using SqliteCommand command =
                    connection.CreateCommand();
                command.CommandText = """
                    UPDATE retail_asset_identities
                    SET display_name = $display;
                    """;
                command.Parameters.AddWithValue(
                    "$display",
                    string.Concat(
                        "\0",
                        new string('x', 40_000)));
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using CountingRpackProvider provider =
                CreateProvider(sources);
            await using RetailAssetSqliteIndex index =
                new(database);
            RetailAssetCatalog rebuilt =
                await RetailAssetCatalog.BuildAsync(
                    [provider],
                    index);
            Assert.False(rebuilt.WasRestoredFromPersistentIndex);
            Assert.Equal(1, provider.EnumerationCount);
            Assert.Equal(
                "oversized_mesh",
                Assert.Single(rebuilt.Assets).DisplayName);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task TransientSnapshotFailureRetainsRestorableDatabase()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string pack = await WritePackAsync(
                directory,
                "retained_mesh",
                1);
            RpackSource[] sources = [new(pack, 10)];
            string database = Path.Combine(directory, "assets.sqlite");
            await BuildAndDisposeAsync(database, sources);

            await using FailingSnapshotRpackProvider failing =
                new(CreateProvider(sources), failures: 1);
            await using (RetailAssetSqliteIndex index =
                         new(database))
            {
                RetailAssetCatalog scanned =
                    await RetailAssetCatalog.BuildAsync(
                        [failing],
                        index);
                Assert.False(scanned.WasRestoredFromPersistentIndex);
                Assert.Equal(1, failing.EnumerationCount);
                Assert.Equal(2, failing.SnapshotCount);
            }

            await using CountingRpackProvider restoredProvider =
                CreateProvider(sources);
            await using RetailAssetSqliteIndex restoredIndex =
                new(database);
            RetailAssetCatalog restored =
                await RetailAssetCatalog.BuildAsync(
                    [restoredProvider],
                    restoredIndex);
            Assert.True(restored.WasRestoredFromPersistentIndex);
            Assert.Equal(0, restoredProvider.EnumerationCount);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task LockedDestinationSidecarAbortsBeforeReplacement()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string database = Path.Combine(directory, "assets.sqlite");
            RetailAssetRecord original =
                CreateStandaloneRecord("original");
            RetailAssetRecord replacement =
                CreateStandaloneRecord("replacement");
            await using RetailAssetSqliteIndex index = new(database);
            await index.ReplaceSnapshotAsync([], [original]);
            string sidecar = string.Concat(database, "-wal");
            await File.WriteAllTextAsync(sidecar, "locked");
            await using (FileStream locked = new(
                             sidecar,
                             FileMode.Open,
                             FileAccess.ReadWrite,
                             FileShare.Read))
            {
                await Assert.ThrowsAnyAsync<IOException>(
                    () => index.ReplaceSnapshotAsync(
                        [],
                        [replacement]));
            }

            File.Delete(sidecar);
            RetailAssetRecord saved = Assert.Single(
                await index.LoadAsync());
            Assert.Equal(original.Id.LogicalId, saved.Id.LogicalId);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExactPrecedenceTiesUseStablePhysicalIdentityOrder()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(directory, "tie.source");
            await File.WriteAllBytesAsync(sourcePath, [1]);
            string database = Path.Combine(directory, "assets.sqlite");
            await using (TieSnapshotProvider first =
                         new(sourcePath))
            await using (RetailAssetSqliteIndex firstIndex =
                         new(database))
            {
                RetailAssetCatalog scanned =
                    await RetailAssetCatalog.BuildAsync(
                        [first],
                        firstIndex);
                Assert.Equal(
                    1,
                    Assert.IsType<RetailAssetRecord>(
                            scanned.Resolve(first.LogicalId))
                        .Id.SourceIndex);
            }

            await using TieSnapshotProvider second =
                new(sourcePath);
            await using RetailAssetSqliteIndex secondIndex =
                new(database);
            RetailAssetCatalog restored =
                await RetailAssetCatalog.BuildAsync(
                    [second],
                    secondIndex);
            Assert.True(restored.WasRestoredFromPersistentIndex);
            Assert.Equal(0, second.EnumerationCount);
            Assert.Equal(
                1,
                Assert.IsType<RetailAssetRecord>(
                        restored.Resolve(second.LogicalId))
                    .Id.SourceIndex);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CancellationDuringValidationPreservesPublishedDatabase()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string database = Path.Combine(directory, "assets.sqlite");
            RetailAssetRecord original =
                CreateStandaloneRecord("original");
            RetailAssetRecord replacement =
                CreateStandaloneRecord("replacement");
            await using RetailAssetSqliteIndex index = new(database);
            await index.ReplaceSnapshotAsync([], [original]);
            using CancellationTokenSource cancellation = new();
            CancelOnEnumerationCollection records =
                new(replacement, cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => index.ReplaceSnapshotAsync(
                    [],
                    records,
                    cancellation.Token));

            RetailAssetRecord saved = Assert.Single(
                await index.LoadAsync());
            Assert.Equal(original.Id.LogicalId, saved.Id.LogicalId);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task SemanticRecordValidationRejectsInvalidReplacement()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string database = Path.Combine(directory, "assets.sqlite");
            RetailAssetRecord original =
                CreateStandaloneRecord("original");
            RetailAssetRecord invalid = original with
            {
                Source = original.Source with
                {
                    ResourceIndex = 1,
                },
            };
            await using RetailAssetSqliteIndex index = new(database);
            await index.ReplaceSnapshotAsync([], [original]);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => index.ReplaceSnapshotAsync(
                    [],
                    [invalid]));

            RetailAssetRecord saved = Assert.Single(
                await index.LoadAsync());
            Assert.Equal(original, saved);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task CancelledReplacementLeavesPublishedDatabaseUntouched()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string database = Path.Combine(directory, "assets.sqlite");
            RetailAssetRecord original = CreateStandaloneRecord("original");
            RetailAssetRecord replacement = CreateStandaloneRecord("replacement");
            await using RetailAssetSqliteIndex index = new(database);
            await index.ReplaceSnapshotAsync([], [original]);
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => index.ReplaceSnapshotAsync(
                    [],
                    [replacement],
                    cancellation.Token));

            RetailAssetRecord saved = Assert.Single(
                await index.LoadAsync());
            Assert.Equal(original.Id.LogicalId, saved.Id.LogicalId);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task<string> WritePackAsync(
        string directory,
        string resourceName,
        byte payload) =>
        await RpackTestData.WriteArchiveAsync(
            directory,
            resourceName,
            Rp6lResourceTypes.Mesh,
            [new RpackTestItem(16, [payload])],
            RpackTestCompression.None);

    private static Task WriteNamedPackAsync(
        string path,
        string resourceName,
        byte payload) =>
        File.WriteAllBytesAsync(
            path,
            RpackTestData.BuildArchive(
                resourceName,
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, [payload])],
                RpackTestCompression.None));

    private static CountingRpackProvider CreateProvider(
        IEnumerable<RpackSource> sources) =>
        new(new RpackAssetProvider(
            "dl1-rpacks",
            sources,
            installId: "test-install"));

    private static async Task BuildAndDisposeAsync(
        string database,
        IReadOnlyList<RpackSource> sources)
    {
        await using CountingRpackProvider provider =
            CreateProvider(sources);
        await using RetailAssetSqliteIndex index =
            new(database);
        RetailAssetCatalog catalog =
            await RetailAssetCatalog.BuildAsync(
                [provider],
                index);
        Assert.False(catalog.WasRestoredFromPersistentIndex);
        Assert.Equal(1, provider.EnumerationCount);
    }

    private static async Task ExecuteSqlAsync(
        string database,
        string commandText)
    {
        await using SqliteConnection connection =
            CreateTestSqliteConnection(database);
        await connection.OpenAsync();
        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteConnection CreateTestSqliteConnection(
        string database) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false,
        }.ToString());

    private static RetailAssetRecord CreateStandaloneRecord(string name)
    {
        RetailAssetLogicalId logicalId =
            RetailAssetLogicalId.VirtualFile(
                string.Concat(name, ".fed"));
        return new RetailAssetRecord(
            RetailAssetId.Create(
                logicalId,
                "standalone-install",
                "standalone",
                0,
                1,
                string.Concat(name, "-snapshot")),
            logicalId.Name,
            new RetailAssetSource(
                "standalone",
                RetailAssetSourceKind.LooseFile,
                1,
                string.Concat(name, ".fed"),
                logicalId.Name,
                null,
                1,
                1,
                DateTime.UnixEpoch));
    }

    private sealed class CountingRpackProvider :
        IRetailAssetProvider,
        IRetailAssetSnapshotProvider,
        IAsyncDisposable
    {
        private readonly RpackAssetProvider _inner;

        public CountingRpackProvider(RpackAssetProvider inner)
        {
            _inner = inner;
        }

        public string ProviderId => _inner.ProviderId;

        public int EnumerationCount { get; private set; }

        public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnumerationCount++;
            await foreach (RetailAssetRecord asset in _inner
                               .EnumerateAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return asset;
            }
        }

        public ValueTask<Stream> OpenReadAsync(
            RetailAssetRecord asset,
            CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(asset, cancellationToken);

        public ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            _inner.CaptureSnapshotAsync(cancellationToken);

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();
    }

    private sealed class FailingSnapshotRpackProvider :
        IRetailAssetProvider,
        IRetailAssetSnapshotProvider,
        IAsyncDisposable
    {
        private readonly CountingRpackProvider _inner;
        private int _remainingFailures;

        public FailingSnapshotRpackProvider(
            CountingRpackProvider inner,
            int failures)
        {
            _inner = inner;
            _remainingFailures = failures;
        }

        public string ProviderId => _inner.ProviderId;

        public int EnumerationCount => _inner.EnumerationCount;

        public int SnapshotCount { get; private set; }

        public IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
            CancellationToken cancellationToken = default) =>
            _inner.EnumerateAsync(cancellationToken);

        public ValueTask<Stream> OpenReadAsync(
            RetailAssetRecord asset,
            CancellationToken cancellationToken = default) =>
            _inner.OpenReadAsync(asset, cancellationToken);

        public ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            SnapshotCount++;
            if (_remainingFailures-- > 0)
            {
                throw new IOException(
                    "Transient snapshot failure.");
            }

            return _inner.CaptureSnapshotAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();
    }

    private sealed class TieSnapshotProvider :
        IRetailAssetProvider,
        IRetailAssetSnapshotProvider,
        IAsyncDisposable
    {
        private readonly string _sourcePath;
        private readonly RetailAssetRecord[] _records;

        public TieSnapshotProvider(string sourcePath)
        {
            _sourcePath = Path.GetFullPath(sourcePath);
            LogicalId = RetailAssetLogicalId.VirtualFile(
                "tie.asset");
            FileInfo file = new(_sourcePath);
            _records =
            [
                CreateRecord(2, "fingerprint-b", file),
                CreateRecord(1, "fingerprint-a", file),
            ];
        }

        public string ProviderId => "tie-provider";

        public RetailAssetLogicalId LogicalId { get; }

        public int EnumerationCount { get; private set; }

        public async IAsyncEnumerable<RetailAssetRecord> EnumerateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            EnumerationCount++;
            await Task.CompletedTask.ConfigureAwait(false);
            foreach (RetailAssetRecord record in _records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }

        public ValueTask<Stream> OpenReadAsync(
            RetailAssetRecord asset,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(
                File.OpenRead(_sourcePath));

        public ValueTask<RetailAssetProviderSnapshot> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = new(_sourcePath);
            RetailAssetProviderSnapshot snapshot = new(
                ProviderId,
                nameof(TieSnapshotProvider),
                "tie-install",
                "tie-configuration",
                [
                    new(
                        0,
                        "tie-root",
                        Path.GetDirectoryName(_sourcePath)
                        ?? throw new InvalidOperationException(
                            "Tie source has no parent directory."),
                        true),
                ],
                [
                    new(
                        0,
                        RetailAssetSourceKind.GeneratedOverride,
                        5,
                        _sourcePath,
                        file.Length,
                        file.LastWriteTimeUtc.Ticks,
                        "tie-bounded-fingerprint"),
                ]);
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;

        private RetailAssetRecord CreateRecord(
            long sourceIndex,
            string fingerprint,
            FileInfo file) =>
            new(
                RetailAssetId.Create(
                    LogicalId,
                    "tie-install",
                    ProviderId,
                    sourceIndex,
                    5,
                    fingerprint),
                "tie.asset",
                new RetailAssetSource(
                    ProviderId,
                    RetailAssetSourceKind.GeneratedOverride,
                    5,
                    _sourcePath,
                    "tie.asset",
                    null,
                    file.Length,
                    file.Length,
                    file.LastWriteTimeUtc));
    }

    private sealed class CancelOnEnumerationCollection :
        IReadOnlyCollection<RetailAssetRecord>
    {
        private readonly RetailAssetRecord _record;
        private readonly CancellationTokenSource _cancellation;

        public CancelOnEnumerationCollection(
            RetailAssetRecord record,
            CancellationTokenSource cancellation)
        {
            _record = record;
            _cancellation = cancellation;
        }

        public int Count => 1;

        public IEnumerator<RetailAssetRecord> GetEnumerator()
        {
            _cancellation.Cancel();
            yield return _record;
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
