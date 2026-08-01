using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReAnimated.DL1.Assets.Catalog;

public sealed class RetailAssetSqliteIndex : IAsyncDisposable
{
    private const int SchemaVersion = 3;
    private const int MaximumPersistedAssetCount = 2_000_000;
    private const int MaximumPersistedProviderCount = 4_096;
    private const int MaximumPersistedSourceCount = 65_536;
    private const int MaximumPersistedNameCharacters = 8_192;
    private const int MaximumPersistedPathCharacters = 32_768;
    private const int MaximumPersistedIdentityCharacters = 1_024;
    private const int MaximumPersistedNameBytes =
        MaximumPersistedNameCharacters * 4;
    private const int MaximumPersistedPathBytes =
        MaximumPersistedPathCharacters * 4;
    private const int MaximumPersistedIdentityBytes =
        MaximumPersistedIdentityCharacters * 4;
    private const string CatalogFormat = "dl-reanimated-retail-catalog";
    private const string Schema = """
        PRAGMA journal_mode=DELETE;
        PRAGMA synchronous=FULL;
        PRAGMA user_version=3;
        CREATE TABLE catalog_metadata (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            catalog_format TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            is_complete INTEGER NOT NULL CHECK (is_complete IN (0, 1)),
            provider_set_fingerprint TEXT NOT NULL,
            asset_count INTEGER NOT NULL,
            asset_manifest_sha256 TEXT NOT NULL
        );
        CREATE TABLE provider_snapshots (
            provider_ordinal INTEGER NOT NULL PRIMARY KEY,
            provider_id TEXT NOT NULL UNIQUE,
            provider_kind TEXT NOT NULL,
            install_id TEXT NOT NULL,
            configuration_fingerprint TEXT NOT NULL,
            stable_fingerprint TEXT NOT NULL
        );
        CREATE TABLE provider_roots (
            provider_id TEXT NOT NULL,
            root_ordinal INTEGER NOT NULL,
            role TEXT NOT NULL,
            root_path TEXT NOT NULL,
            root_exists INTEGER NOT NULL CHECK (root_exists IN (0, 1)),
            PRIMARY KEY (provider_id, root_ordinal)
        );
        CREATE TABLE source_snapshots (
            provider_id TEXT NOT NULL,
            source_ordinal INTEGER NOT NULL,
            source_kind INTEGER NOT NULL,
            priority INTEGER NOT NULL,
            source_path TEXT NOT NULL,
            source_length INTEGER NOT NULL,
            source_mtime_ticks INTEGER NOT NULL,
            bounded_fingerprint TEXT NOT NULL,
            PRIMARY KEY (provider_id, source_ordinal)
        );
        CREATE TABLE retail_asset_identities (
            asset_namespace INTEGER NOT NULL,
            resource_type INTEGER NOT NULL,
            normalized_name TEXT NOT NULL,
            display_name TEXT NOT NULL,
            install_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            source_index INTEGER NOT NULL,
            precedence INTEGER NOT NULL,
            source_fingerprint TEXT NOT NULL,
            content_fingerprint TEXT NULL,
            source_kind INTEGER NOT NULL,
            priority INTEGER NOT NULL,
            container_path TEXT NOT NULL,
            entry_path TEXT NOT NULL,
            resource_index INTEGER NULL,
            asset_length INTEGER NOT NULL,
            source_length INTEGER NOT NULL,
            source_mtime_ticks INTEGER NOT NULL,
            PRIMARY KEY (
                install_id,
                provider_id,
                source_index,
                source_fingerprint,
                asset_namespace,
                resource_type,
                normalized_name
            )
        );
        CREATE INDEX ix_retail_asset_identities_lookup
            ON retail_asset_identities (
                asset_namespace,
                resource_type,
                normalized_name,
                precedence DESC
            );
        CREATE INDEX ix_retail_asset_identities_provider
            ON retail_asset_identities (provider_id);
        """;

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public RetailAssetSqliteIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (File.Exists(_databasePath))
        {
            return;
        }

        await ReplaceSnapshotAsync(
            [],
            [],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a complete saved catalog only when its schema and every current
    /// provider/root/source snapshot match. Corrupt and stale cache databases
    /// are treated as cache misses; cancellation is always propagated.
    /// </summary>
    public async Task<IReadOnlyList<RetailAssetRecord>?> TryLoadValidatedAsync(
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(providers);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_databasePath))
            {
                return null;
            }

            try
            {
                await using SqliteConnection connection =
                    CreateConnection(_databasePath, SqliteOpenMode.ReadOnly);
                await connection.OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
                AssetManifest expectedManifest =
                    await ValidateDatabaseAsync(
                    connection,
                    providers,
                    cancellationToken).ConfigureAwait(false);
                AssetManifest actualManifest =
                    await ComputeAssetManifestAsync(
                        connection,
                        transaction: null,
                        cancellationToken).ConfigureAwait(false);
                if (actualManifest != expectedManifest)
                {
                    throw new InvalidDataException(
                        "The retail catalog asset manifest is incomplete or corrupt.");
                }

                IReadOnlyList<RetailAssetRecord> assets =
                    await LoadRecordsAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                ValidateLoadedRecords(
                    assets,
                    providers,
                    cancellationToken);
                return assets;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException
                or FormatException
                or InvalidCastException
                or OverflowException)
            {
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces the complete cache as one same-directory file operation. A
    /// cancelled or failed write never publishes the partial temporary DB.
    /// </summary>
    public async Task ReplaceSnapshotAsync(
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        IReadOnlyCollection<RetailAssetRecord> assets,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(assets);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplaceSnapshotUnderGateAsync(
                providers,
                assets,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceProviderAsync(
        string providerId,
        IReadOnlyCollection<RetailAssetRecord> assets,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(assets);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<RetailAssetRecord> existing;
            try
            {
                if (!File.Exists(_databasePath))
                {
                    existing = [];
                }
                else
                {
                    await using SqliteConnection connection =
                        CreateConnection(
                            _databasePath,
                            SqliteOpenMode.ReadOnly);
                    await connection.OpenAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await ValidateDatabaseBoundsAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                    AssetManifest expectedManifest =
                        await ReadStoredAssetManifestAsync(
                            connection,
                            cancellationToken).ConfigureAwait(false);
                    AssetManifest actualManifest =
                        await ComputeAssetManifestAsync(
                            connection,
                            transaction: null,
                            cancellationToken).ConfigureAwait(false);
                    if (expectedManifest != actualManifest)
                    {
                        throw new InvalidDataException(
                            "The retail catalog asset manifest is incomplete or corrupt.");
                    }

                    existing = await LoadRecordsAsync(
                        connection,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (
                exception is SqliteException
                or InvalidDataException
                or IOException
                or FormatException
                or InvalidCastException)
            {
                existing = [];
            }

            RetailAssetRecord[] combined = existing
                .Where(asset => !string.Equals(
                    asset.Source.ProviderId,
                    providerId,
                    StringComparison.Ordinal))
                .Concat(assets)
                .ToArray();
            await ReplaceSnapshotUnderGateAsync(
                [],
                combined,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReplaceSnapshotUnderGateAsync(
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        IReadOnlyCollection<RetailAssetRecord> assets,
        CancellationToken cancellationToken)
    {
        string temporaryPath = string.Concat(
            _databasePath,
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        try
        {
            ValidateSnapshotForWrite(
                providers,
                assets,
                cancellationToken);
            await WriteDatabaseAsync(
                temporaryPath,
                providers,
                assets,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            DeleteDestinationSidecar(
                string.Concat(_databasePath, "-wal"));
            DeleteDestinationSidecar(
                string.Concat(_databasePath, "-shm"));
            File.Move(
                temporaryPath,
                _databasePath,
                overwrite: true);
        }
        finally
        {
            DeleteSidecarIfPresent(temporaryPath);
            DeleteSidecarIfPresent(string.Concat(temporaryPath, "-journal"));
            DeleteSidecarIfPresent(string.Concat(temporaryPath, "-wal"));
            DeleteSidecarIfPresent(string.Concat(temporaryPath, "-shm"));
        }
    }

    public async Task<IReadOnlyList<RetailAssetRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection =
                CreateConnection(_databasePath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await ValidateDatabaseBoundsAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            AssetManifest expectedManifest =
                await ReadStoredAssetManifestAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
            AssetManifest actualManifest =
                await ComputeAssetManifestAsync(
                    connection,
                    transaction: null,
                    cancellationToken).ConfigureAwait(false);
            if (expectedManifest != actualManifest)
            {
                throw new InvalidDataException(
                    "The retail catalog asset manifest is incomplete or corrupt.");
            }

            return await LoadRecordsAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<AssetManifest> ValidateDatabaseAsync(
        SqliteConnection connection,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check;";
            object? result = await integrity.ExecuteScalarAsync(
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    result as string,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The retail catalog SQLite integrity check failed.");
            }
        }

        await using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            object? result = await version.ExecuteScalarAsync(
                cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt32(
                    result,
                    System.Globalization.CultureInfo.InvariantCulture) !=
                SchemaVersion)
            {
                throw new InvalidDataException(
                    "The retail catalog SQLite schema version is stale.");
            }
        }

        await ValidateDatabaseBoundsAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        string expectedProviderSetFingerprint =
            CreateProviderSetFingerprint(providers);
        AssetManifest expectedManifest;
        await using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.CommandText = """
                SELECT
                    catalog_format,
                    schema_version,
                    is_complete,
                    provider_set_fingerprint,
                    asset_count,
                    asset_manifest_sha256
                FROM catalog_metadata
                WHERE singleton = 1;
                """;
            await using SqliteDataReader reader =
                await metadata.ExecuteReaderAsync(
                    cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        0,
                        MaximumPersistedIdentityCharacters,
                        "catalog format"),
                    CatalogFormat,
                    StringComparison.Ordinal) ||
                reader.GetInt32(1) != SchemaVersion ||
                reader.GetInt32(2) != 1 ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        3,
                        MaximumPersistedIdentityCharacters,
                        "provider set fingerprint"),
                    expectedProviderSetFingerprint,
                    StringComparison.Ordinal) ||
                reader.GetInt64(4) < 0 ||
                reader.GetInt64(4) > MaximumPersistedAssetCount)
            {
                throw new InvalidDataException(
                    "The retail catalog schema or provider set is stale.");
            }

            string manifestSha256 = ReadBoundedString(
                reader,
                5,
                MaximumPersistedIdentityCharacters,
                "asset manifest fingerprint");
            if (manifestSha256.Length != 64 ||
                manifestSha256.Any(static character =>
                    !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException(
                    "The retail catalog asset manifest fingerprint is invalid.");
            }

            expectedManifest = new AssetManifest(
                reader.GetInt64(4),
                manifestSha256);
            if (await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    "The retail catalog has duplicate metadata.");
            }
        }

        await ValidateProvidersAsync(
            connection,
            providers,
            cancellationToken).ConfigureAwait(false);
        return expectedManifest;
    }

    private static async Task ValidateDatabaseBoundsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateTableCountAsync(
            connection,
            "provider_snapshots",
            MaximumPersistedProviderCount,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableCountAsync(
            connection,
            "provider_roots",
            MaximumPersistedSourceCount,
            cancellationToken).ConfigureAwait(false);
        await ValidateTableCountAsync(
            connection,
            "source_snapshots",
            MaximumPersistedSourceCount,
            cancellationToken).ConfigureAwait(false);
        await ValidateStringBoundsAsync(
            connection,
            """
            SELECT
                COALESCE(MAX(LENGTH(CAST(catalog_format AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(provider_set_fingerprint AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(asset_manifest_sha256 AS BLOB))), 0)
            FROM catalog_metadata;
            """,
            [
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
            ],
            cancellationToken).ConfigureAwait(false);
        await ValidateStringBoundsAsync(
            connection,
            """
            SELECT
                COALESCE(MAX(LENGTH(CAST(provider_id AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(provider_kind AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(install_id AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(configuration_fingerprint AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(stable_fingerprint AS BLOB))), 0)
            FROM provider_snapshots;
            """,
            [
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
            ],
            cancellationToken).ConfigureAwait(false);
        await ValidateStringBoundsAsync(
            connection,
            """
            SELECT
                COALESCE(MAX(LENGTH(CAST(provider_id AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(role AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(root_path AS BLOB))), 0)
            FROM provider_roots;
            """,
            [
                MaximumPersistedIdentityBytes,
                MaximumPersistedIdentityBytes,
                MaximumPersistedPathBytes,
            ],
            cancellationToken).ConfigureAwait(false);
        await ValidateStringBoundsAsync(
            connection,
            """
            SELECT
                COALESCE(MAX(LENGTH(CAST(provider_id AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(source_path AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(bounded_fingerprint AS BLOB))), 0)
            FROM source_snapshots;
            """,
            [
                MaximumPersistedIdentityBytes,
                MaximumPersistedPathBytes,
                MaximumPersistedIdentityBytes,
            ],
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(MAX(LENGTH(CAST(normalized_name AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(display_name AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(install_id AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(provider_id AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(source_fingerprint AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(content_fingerprint AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(container_path AS BLOB))), 0),
                COALESCE(MAX(LENGTH(CAST(entry_path AS BLOB))), 0)
            FROM retail_asset_identities;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false) ||
            reader.GetInt64(0) > MaximumPersistedAssetCount ||
            reader.GetInt64(1) > MaximumPersistedNameBytes ||
            reader.GetInt64(2) > MaximumPersistedNameBytes ||
            reader.GetInt64(3) > MaximumPersistedIdentityBytes ||
            reader.GetInt64(4) > MaximumPersistedIdentityBytes ||
            reader.GetInt64(5) > MaximumPersistedIdentityBytes ||
            reader.GetInt64(6) > MaximumPersistedIdentityBytes ||
            reader.GetInt64(7) > MaximumPersistedPathBytes ||
            reader.GetInt64(8) > MaximumPersistedPathBytes)
        {
            throw new InvalidDataException(
                "The retail catalog exceeds its bounded restore limits.");
        }
    }

    private static async Task<AssetManifest> ReadStoredAssetManifestAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                catalog_format,
                schema_version,
                is_complete,
                asset_count,
                asset_manifest_sha256
            FROM catalog_metadata
            WHERE singleton = 1;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false) ||
            !string.Equals(
                ReadBoundedString(
                    reader,
                    0,
                    MaximumPersistedIdentityCharacters,
                    "catalog format"),
                CatalogFormat,
                StringComparison.Ordinal) ||
            reader.GetInt32(1) != SchemaVersion ||
            reader.GetInt32(2) != 1)
        {
            throw new InvalidDataException(
                "The retail catalog metadata is stale or incomplete.");
        }

        long count = reader.GetInt64(3);
        string sha256 = ReadBoundedString(
            reader,
            4,
            MaximumPersistedIdentityCharacters,
            "asset manifest fingerprint");
        if (count < 0 ||
            count > MaximumPersistedAssetCount ||
            sha256.Length != 64 ||
            sha256.Any(static character =>
                !Uri.IsHexDigit(character)) ||
            await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidDataException(
                "The retail catalog asset manifest metadata is invalid.");
        }

        return new AssetManifest(count, sha256);
    }

    private static async Task ValidateStringBoundsAsync(
        SqliteConnection connection,
        string commandText,
        IReadOnlyList<int> maximumLengths,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false) ||
            reader.FieldCount != maximumLengths.Count)
        {
            throw new InvalidDataException(
                "The retail catalog string-bound query is invalid.");
        }

        for (int index = 0; index < maximumLengths.Count; index++)
        {
            long length = reader.GetInt64(index);
            if (length < 0 || length > maximumLengths[index])
            {
                throw new InvalidDataException(
                    "The retail catalog exceeds its bounded restore string limits.");
            }
        }
    }

    private static async Task ValidateTableCountAsync(
        SqliteConnection connection,
        string tableName,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        long count = (long)(await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"The retail catalog table '{tableName}' has no row count."));
        if (count < 0 || count > maximumCount)
        {
            throw new InvalidDataException(
                $"The retail catalog table '{tableName}' exceeds its bounded restore limit.");
        }
    }

    private static async Task ValidateProvidersAsync(
        SqliteConnection connection,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                provider_ordinal,
                provider_id,
                provider_kind,
                install_id,
                configuration_fingerprint,
                stable_fingerprint
            FROM provider_snapshots
            ORDER BY provider_ordinal;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        int ordinal = 0;
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            if (ordinal >= providers.Count)
            {
                throw new InvalidDataException(
                    "The retail catalog contains an unexpected provider.");
            }

            RetailAssetProviderSnapshot expected = providers[ordinal];
            if (reader.GetInt32(0) != ordinal ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        1,
                        MaximumPersistedIdentityCharacters,
                        "provider ID"),
                    expected.ProviderId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        2,
                        MaximumPersistedIdentityCharacters,
                        "provider kind"),
                    expected.ProviderKind,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        3,
                        MaximumPersistedIdentityCharacters,
                        "provider install ID"),
                    expected.InstallId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        4,
                        MaximumPersistedIdentityCharacters,
                        "provider configuration fingerprint"),
                    expected.ConfigurationFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        5,
                        MaximumPersistedIdentityCharacters,
                        "provider stable fingerprint"),
                    expected.StableFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The retail catalog provider snapshot at ordinal {ordinal} is stale.");
            }

            ordinal++;
        }

        if (ordinal != providers.Count)
        {
            throw new InvalidDataException(
                "The retail catalog provider snapshot is incomplete.");
        }

        await ValidateRootsAsync(
            connection,
            providers,
            cancellationToken).ConfigureAwait(false);
        await ValidateSourcesAsync(
            connection,
            providers,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateRootsAsync(
        SqliteConnection connection,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken)
    {
        Dictionary<string, RetailAssetRootSnapshot> expected =
            providers
                .SelectMany(provider => provider.Roots.Select(root =>
                    new KeyValuePair<string, RetailAssetRootSnapshot>(
                        CreateSnapshotKey(provider.ProviderId, root.Ordinal),
                        root)))
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_id, root_ordinal, role, root_path, root_exists
            FROM provider_roots;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            string key = CreateSnapshotKey(
                ReadBoundedString(
                    reader,
                    0,
                    MaximumPersistedIdentityCharacters,
                    "root provider ID"),
                reader.GetInt32(1));
            if (!expected.TryGetValue(
                    key,
                    out RetailAssetRootSnapshot? root) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        2,
                        MaximumPersistedIdentityCharacters,
                        "root role"),
                    root.Role,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        3,
                        MaximumPersistedPathCharacters,
                        "root path"),
                    root.Path,
                    StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(4) != (root.Exists ? 1 : 0))
            {
                throw new InvalidDataException(
                    "The retail catalog root metadata is stale.");
            }

            count++;
        }

        if (count != expected.Count)
        {
            throw new InvalidDataException(
                "The retail catalog root metadata is incomplete.");
        }
    }

    private static async Task ValidateSourcesAsync(
        SqliteConnection connection,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken)
    {
        Dictionary<string, RetailAssetSourceSnapshot> expected =
            providers
                .SelectMany(provider => provider.Sources.Select(source =>
                    new KeyValuePair<string, RetailAssetSourceSnapshot>(
                        CreateSnapshotKey(provider.ProviderId, source.Ordinal),
                        source)))
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value,
                    StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                provider_id,
                source_ordinal,
                source_kind,
                priority,
                source_path,
                source_length,
                source_mtime_ticks,
                bounded_fingerprint
            FROM source_snapshots;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            string key = CreateSnapshotKey(
                ReadBoundedString(
                    reader,
                    0,
                    MaximumPersistedIdentityCharacters,
                    "source provider ID"),
                reader.GetInt32(1));
            if (!expected.TryGetValue(
                    key,
                    out RetailAssetSourceSnapshot? source) ||
                reader.GetInt32(2) != (int)source.Kind ||
                reader.GetInt32(3) != source.Priority ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        4,
                        MaximumPersistedPathCharacters,
                        "source path"),
                    source.Path,
                    StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt64(5) != source.Length ||
                reader.GetInt64(6) != source.LastWriteTimeUtcTicks ||
                !string.Equals(
                    ReadBoundedString(
                        reader,
                        7,
                        MaximumPersistedIdentityCharacters,
                        "source bounded fingerprint"),
                    source.BoundedFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The retail catalog source metadata is stale.");
            }

            count++;
        }

        if (count != expected.Count)
        {
            throw new InvalidDataException(
                "The retail catalog source metadata is incomplete.");
        }
    }

    private static async Task<IReadOnlyList<RetailAssetRecord>> LoadRecordsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                asset_namespace,
                resource_type,
                normalized_name,
                display_name,
                install_id,
                provider_id,
                source_index,
                precedence,
                source_fingerprint,
                content_fingerprint,
                source_kind,
                priority,
                container_path,
                entry_path,
                resource_index,
                asset_length,
                source_length,
                source_mtime_ticks
            FROM retail_asset_identities
            ORDER BY normalized_name, precedence DESC, provider_id;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        List<RetailAssetRecord> result = [];
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            int namespaceValue = reader.GetInt32(0);
            if (!Enum.IsDefined((RetailAssetNamespace)namespaceValue))
            {
                throw new InvalidDataException(
                    "The retail catalog contains an invalid asset namespace.");
            }

            RetailAssetNamespace assetNamespace =
                (RetailAssetNamespace)namespaceValue;
            int type = reader.GetInt32(1);
            string normalizedName = ReadBoundedString(
                reader,
                2,
                MaximumPersistedNameCharacters,
                "asset normalized name");
            string displayName = ReadBoundedString(
                reader,
                3,
                MaximumPersistedNameCharacters,
                "asset display name");
            string installId = ReadBoundedString(
                reader,
                4,
                MaximumPersistedIdentityCharacters,
                "asset install ID");
            string providerId = ReadBoundedString(
                reader,
                5,
                MaximumPersistedIdentityCharacters,
                "asset provider ID");
            string sourceFingerprint = ReadBoundedString(
                reader,
                8,
                MaximumPersistedIdentityCharacters,
                "asset source fingerprint");
            string? contentFingerprint =
                ReadOptionalBoundedString(
                    reader,
                    9,
                    MaximumPersistedIdentityCharacters,
                    "asset content fingerprint");
            RetailAssetLogicalId logicalId =
                assetNamespace == RetailAssetNamespace.RpackResource
                    ? RetailAssetLogicalId.Rpack(
                        checked((short)type),
                        normalizedName)
                    : RetailAssetLogicalId.VirtualFile(normalizedName);
            RetailAssetId id = RetailAssetId.Create(
                logicalId,
                installId,
                providerId,
                reader.GetInt64(6),
                reader.GetInt32(7),
                sourceFingerprint,
                contentFingerprint);
            int sourceKindValue = reader.GetInt32(10);
            if (!Enum.IsDefined((RetailAssetSourceKind)sourceKindValue))
            {
                throw new InvalidDataException(
                    "The retail catalog contains an invalid source kind.");
            }

            RetailAssetSource source = new(
                providerId,
                (RetailAssetSourceKind)sourceKindValue,
                reader.GetInt32(11),
                ReadBoundedString(
                    reader,
                    12,
                    MaximumPersistedPathCharacters,
                    "asset container path"),
                ReadBoundedString(
                    reader,
                    13,
                    MaximumPersistedPathCharacters,
                    "asset entry path"),
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                reader.GetInt64(15),
                reader.GetInt64(16),
                new DateTime(reader.GetInt64(17), DateTimeKind.Utc));
            result.Add(new RetailAssetRecord(
                id,
                displayName,
                source));
        }

        return result;
    }

    private static void ValidateLoadedRecords(
        IEnumerable<RetailAssetRecord> assets,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken)
    {
        Dictionary<string, RetailAssetProviderSnapshot> providerLookup =
            providers.ToDictionary(
                static provider => provider.ProviderId,
                StringComparer.Ordinal);
        Dictionary<string, RetailAssetSourceSnapshot> sourceLookup =
            new(StringComparer.Ordinal);
        foreach (RetailAssetProviderSnapshot provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (RetailAssetSourceSnapshot source in provider.Sources)
            {
                string key = CreateSourceValidationKey(
                    provider.ProviderId,
                    source.Kind,
                    source.Priority,
                    source.Path);
                if (!sourceLookup.TryAdd(key, source))
                {
                    throw new InvalidDataException(
                        "The retail catalog provider snapshot contains duplicate physical sources.");
                }
            }
        }

        foreach (RetailAssetRecord asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!providerLookup.TryGetValue(
                    asset.Source.ProviderId,
                    out RetailAssetProviderSnapshot? provider) ||
                !string.Equals(
                    asset.Id.ProviderId,
                    provider.ProviderId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    asset.Id.InstallId,
                    provider.InstallId,
                    StringComparison.Ordinal) ||
                asset.Id.Precedence != asset.Source.Priority)
            {
                throw new InvalidDataException(
                    "The retail catalog contains an asset owned by a stale provider.");
            }

            RetailAssetRecordValidator.Validate(
                provider.ProviderId,
                asset);
            if (!sourceLookup.TryGetValue(
                    CreateSourceValidationKey(
                        provider.ProviderId,
                        asset.Source.Kind,
                        asset.Source.Priority,
                        asset.Source.ContainerPath),
                    out RetailAssetSourceSnapshot? source) ||
                source.Length != asset.Source.SourceLength ||
                source.LastWriteTimeUtcTicks !=
                asset.Source.SourceLastWriteTimeUtc.Ticks ||
                asset.Source.Length < 0)
            {
                throw new InvalidDataException(
                    "The retail catalog asset source snapshot is stale.");
            }
        }
    }

    private static void ValidateSnapshotForWrite(
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        IReadOnlyCollection<RetailAssetRecord> assets,
        CancellationToken cancellationToken)
    {
        if (providers.Count > MaximumPersistedProviderCount ||
            providers.Sum(static provider => (long)provider.Roots.Count) >
            MaximumPersistedSourceCount ||
            providers.Sum(static provider => (long)provider.Sources.Count) >
            MaximumPersistedSourceCount ||
            assets.Count > MaximumPersistedAssetCount)
        {
            throw new InvalidDataException(
                "The retail catalog exceeds its bounded persistence limits.");
        }

        if (providers
            .Select(static provider => provider.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Count() != providers.Count)
        {
            throw new InvalidDataException(
                "Retail catalog provider IDs must be unique.");
        }

        foreach (RetailAssetProviderSnapshot provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePersistedString(
                provider.ProviderId,
                MaximumPersistedIdentityCharacters,
                "provider ID");
            ValidatePersistedString(
                provider.ProviderKind,
                MaximumPersistedIdentityCharacters,
                "provider kind");
            ValidatePersistedString(
                provider.InstallId,
                MaximumPersistedIdentityCharacters,
                "install ID");
            ValidatePersistedString(
                provider.ConfigurationFingerprint,
                MaximumPersistedIdentityCharacters,
                "configuration fingerprint");
            ValidatePersistedString(
                provider.StableFingerprint,
                MaximumPersistedIdentityCharacters,
                "provider fingerprint");
            foreach (RetailAssetRootSnapshot root in provider.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePersistedString(
                    root.Role,
                    MaximumPersistedIdentityCharacters,
                    "root role");
                ValidatePersistedString(
                    root.Path,
                    MaximumPersistedPathCharacters,
                    "root path");
            }

            foreach (RetailAssetSourceSnapshot source in provider.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePersistedString(
                    source.Path,
                    MaximumPersistedPathCharacters,
                    "source path");
                ValidatePersistedString(
                    source.BoundedFingerprint,
                    MaximumPersistedIdentityCharacters,
                    "source fingerprint");
            }
        }

        foreach (RetailAssetRecord asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetailAssetRecordValidator.Validate(
                asset.Source.ProviderId,
                asset);
            ValidatePersistedString(
                asset.Id.Name,
                MaximumPersistedNameCharacters,
                "asset name");
            ValidatePersistedString(
                asset.DisplayName,
                MaximumPersistedNameCharacters,
                "asset display name");
            ValidatePersistedString(
                asset.Id.InstallId,
                MaximumPersistedIdentityCharacters,
                "asset install ID");
            ValidatePersistedString(
                asset.Id.ProviderId,
                MaximumPersistedIdentityCharacters,
                "asset provider ID");
            ValidatePersistedString(
                asset.Id.SourceFingerprint,
                MaximumPersistedIdentityCharacters,
                "asset source fingerprint");
            if (asset.Id.ContentFingerprint is { } contentFingerprint)
            {
                ValidatePersistedString(
                    contentFingerprint,
                    MaximumPersistedIdentityCharacters,
                    "asset content fingerprint");
            }

            ValidatePersistedString(
                asset.Source.ContainerPath,
                MaximumPersistedPathCharacters,
                "asset container path");
            ValidatePersistedString(
                asset.Source.EntryPath,
                MaximumPersistedPathCharacters,
                "asset entry path");
        }

        if (providers.Count > 0)
        {
            ValidateLoadedRecords(
                assets,
                providers,
                cancellationToken);
        }
    }

    private static void ValidatePersistedString(
        string value,
        int maximumCharacters,
        string fieldName)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumCharacters ||
            value.Contains('\0'))
        {
            throw new InvalidDataException(
                $"The retail catalog {fieldName} exceeds its bounded persistence limit.");
        }
    }

    private static async Task WriteDatabaseAsync(
        string path,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        IReadOnlyCollection<RetailAssetRecord> assets,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            CreateConnection(path, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText = Schema;
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken).ConfigureAwait(false);
        await InsertMetadataAsync(
            connection,
            transaction,
            providers,
            cancellationToken).ConfigureAwait(false);
        await InsertAssetsAsync(
            connection,
            transaction,
            assets,
            cancellationToken).ConfigureAwait(false);
        AssetManifest manifest = await ComputeAssetManifestAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await CompleteMetadataAsync(
            connection,
            transaction,
            manifest,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task InsertMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetailAssetProviderSnapshot> providers,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = """
                INSERT INTO catalog_metadata (
                    singleton,
                    catalog_format,
                    schema_version,
                    is_complete,
                    provider_set_fingerprint,
                    asset_count,
                    asset_manifest_sha256
                ) VALUES (
                    1,
                    $format,
                    $version,
                    0,
                    $providers,
                    0,
                    ''
                );
                """;
            metadata.Parameters.AddWithValue("$format", CatalogFormat);
            metadata.Parameters.AddWithValue("$version", SchemaVersion);
            metadata.Parameters.AddWithValue(
                "$providers",
                CreateProviderSetFingerprint(providers));
            await metadata.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        for (int providerOrdinal = 0;
             providerOrdinal < providers.Count;
             providerOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetailAssetProviderSnapshot provider =
                providers[providerOrdinal];
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO provider_snapshots (
                        provider_ordinal,
                        provider_id,
                        provider_kind,
                        install_id,
                        configuration_fingerprint,
                        stable_fingerprint
                    ) VALUES (
                        $ordinal,
                        $provider,
                        $kind,
                        $install,
                        $configuration,
                        $stable
                    );
                    """;
                command.Parameters.AddWithValue(
                    "$ordinal",
                    providerOrdinal);
                command.Parameters.AddWithValue(
                    "$provider",
                    provider.ProviderId);
                command.Parameters.AddWithValue(
                    "$kind",
                    provider.ProviderKind);
                command.Parameters.AddWithValue(
                    "$install",
                    provider.InstallId);
                command.Parameters.AddWithValue(
                    "$configuration",
                    provider.ConfigurationFingerprint);
                command.Parameters.AddWithValue(
                    "$stable",
                    provider.StableFingerprint);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (RetailAssetRootSnapshot root in provider.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using SqliteCommand command =
                    connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO provider_roots (
                        provider_id,
                        root_ordinal,
                        role,
                        root_path,
                        root_exists
                    ) VALUES (
                        $provider,
                        $ordinal,
                        $role,
                        $path,
                        $exists
                    );
                    """;
                command.Parameters.AddWithValue(
                    "$provider",
                    provider.ProviderId);
                command.Parameters.AddWithValue(
                    "$ordinal",
                    root.Ordinal);
                command.Parameters.AddWithValue("$role", root.Role);
                command.Parameters.AddWithValue("$path", root.Path);
                command.Parameters.AddWithValue(
                    "$exists",
                    root.Exists ? 1 : 0);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (RetailAssetSourceSnapshot source in provider.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using SqliteCommand command =
                    connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO source_snapshots (
                        provider_id,
                        source_ordinal,
                        source_kind,
                        priority,
                        source_path,
                        source_length,
                        source_mtime_ticks,
                        bounded_fingerprint
                    ) VALUES (
                        $provider,
                        $ordinal,
                        $kind,
                        $priority,
                        $path,
                        $length,
                        $mtime,
                        $fingerprint
                    );
                    """;
                command.Parameters.AddWithValue(
                    "$provider",
                    provider.ProviderId);
                command.Parameters.AddWithValue(
                    "$ordinal",
                    source.Ordinal);
                command.Parameters.AddWithValue(
                    "$kind",
                    (int)source.Kind);
                command.Parameters.AddWithValue(
                    "$priority",
                    source.Priority);
                command.Parameters.AddWithValue("$path", source.Path);
                command.Parameters.AddWithValue(
                    "$length",
                    source.Length);
                command.Parameters.AddWithValue(
                    "$mtime",
                    source.LastWriteTimeUtcTicks);
                command.Parameters.AddWithValue(
                    "$fingerprint",
                    source.BoundedFingerprint);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task CompleteMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetManifest manifest,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE catalog_metadata
            SET
                asset_count = $count,
                asset_manifest_sha256 = $manifest,
                is_complete = 1
            WHERE singleton = 1 AND is_complete = 0;
            """;
        command.Parameters.AddWithValue("$count", manifest.Count);
        command.Parameters.AddWithValue("$manifest", manifest.Sha256);
        int updated = await command.ExecuteNonQueryAsync(
            cancellationToken).ConfigureAwait(false);
        if (updated != 1)
        {
            throw new InvalidDataException(
                "The retail catalog metadata could not be completed atomically.");
        }
    }

    private static async Task InsertAssetsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<RetailAssetRecord> assets,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO retail_asset_identities (
                asset_namespace,
                resource_type,
                normalized_name,
                display_name,
                install_id,
                provider_id,
                source_index,
                precedence,
                source_fingerprint,
                content_fingerprint,
                source_kind,
                priority,
                container_path,
                entry_path,
                resource_index,
                asset_length,
                source_length,
                source_mtime_ticks
            ) VALUES (
                $namespace,
                $resourceType,
                $name,
                $displayName,
                $install,
                $provider,
                $sourceIndex,
                $precedence,
                $sourceFingerprint,
                $contentFingerprint,
                $sourceKind,
                $priority,
                $container,
                $entry,
                $resourceIndex,
                $assetLength,
                $sourceLength,
                $sourceMtime
            );
            """;
        SqliteParameter assetNamespace =
            insert.Parameters.Add("$namespace", SqliteType.Integer);
        SqliteParameter resourceType =
            insert.Parameters.Add("$resourceType", SqliteType.Integer);
        SqliteParameter name =
            insert.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter displayName =
            insert.Parameters.Add("$displayName", SqliteType.Text);
        SqliteParameter install =
            insert.Parameters.Add("$install", SqliteType.Text);
        SqliteParameter provider =
            insert.Parameters.Add("$provider", SqliteType.Text);
        SqliteParameter sourceIndex =
            insert.Parameters.Add("$sourceIndex", SqliteType.Integer);
        SqliteParameter precedence =
            insert.Parameters.Add("$precedence", SqliteType.Integer);
        SqliteParameter sourceFingerprint =
            insert.Parameters.Add("$sourceFingerprint", SqliteType.Text);
        SqliteParameter contentFingerprint =
            insert.Parameters.Add("$contentFingerprint", SqliteType.Text);
        SqliteParameter sourceKind =
            insert.Parameters.Add("$sourceKind", SqliteType.Integer);
        SqliteParameter priority =
            insert.Parameters.Add("$priority", SqliteType.Integer);
        SqliteParameter container =
            insert.Parameters.Add("$container", SqliteType.Text);
        SqliteParameter entry =
            insert.Parameters.Add("$entry", SqliteType.Text);
        SqliteParameter resourceIndex =
            insert.Parameters.Add("$resourceIndex", SqliteType.Integer);
        SqliteParameter assetLength =
            insert.Parameters.Add("$assetLength", SqliteType.Integer);
        SqliteParameter sourceLength =
            insert.Parameters.Add("$sourceLength", SqliteType.Integer);
        SqliteParameter sourceMtime =
            insert.Parameters.Add("$sourceMtime", SqliteType.Integer);

        foreach (RetailAssetRecord asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            assetNamespace.Value = (int)asset.Id.Namespace;
            resourceType.Value = asset.Id.ResourceType;
            name.Value = asset.Id.Name;
            displayName.Value = asset.DisplayName;
            install.Value = asset.Id.InstallId;
            provider.Value = asset.Id.ProviderId;
            sourceIndex.Value = asset.Id.SourceIndex;
            precedence.Value = asset.Id.Precedence;
            sourceFingerprint.Value = asset.Id.SourceFingerprint;
            contentFingerprint.Value =
                asset.Id.ContentFingerprint is { } fingerprint
                    ? fingerprint
                    : DBNull.Value;
            sourceKind.Value = (int)asset.Source.Kind;
            priority.Value = asset.Source.Priority;
            container.Value = asset.Source.ContainerPath;
            entry.Value = asset.Source.EntryPath;
            resourceIndex.Value =
                asset.Source.ResourceIndex is { } index
                    ? index
                    : DBNull.Value;
            assetLength.Value = asset.Source.Length;
            sourceLength.Value = asset.Source.SourceLength;
            sourceMtime.Value =
                asset.Source.SourceLastWriteTimeUtc.Ticks;
            await insert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<AssetManifest> ComputeAssetManifestAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                asset_namespace,
                resource_type,
                normalized_name,
                display_name,
                install_id,
                provider_id,
                source_index,
                precedence,
                source_fingerprint,
                content_fingerprint,
                source_kind,
                priority,
                container_path,
                entry_path,
                resource_index,
                asset_length,
                source_length,
                source_mtime_ticks
            FROM retail_asset_identities
            ORDER BY
                asset_namespace,
                resource_type,
                normalized_name COLLATE BINARY,
                display_name COLLATE BINARY,
                install_id COLLATE BINARY,
                provider_id COLLATE BINARY,
                source_index,
                precedence,
                source_fingerprint COLLATE BINARY,
                content_fingerprint COLLATE BINARY,
                source_kind,
                priority,
                container_path COLLATE BINARY,
                entry_path COLLATE BINARY,
                resource_index,
                asset_length,
                source_length,
                source_mtime_ticks;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        while (await reader.ReadAsync(cancellationToken)
                   .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (count > MaximumPersistedAssetCount)
            {
                throw new InvalidDataException(
                    "The retail catalog asset manifest exceeds its bounded row limit.");
            }

            AppendInt32(hash, reader.GetInt32(0));
            AppendInt32(hash, reader.GetInt32(1));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    2,
                    MaximumPersistedNameCharacters,
                    "manifest asset name"));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    3,
                    MaximumPersistedNameCharacters,
                    "manifest display name"));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    4,
                    MaximumPersistedIdentityCharacters,
                    "manifest install ID"));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    5,
                    MaximumPersistedIdentityCharacters,
                    "manifest provider ID"));
            AppendInt64(hash, reader.GetInt64(6));
            AppendInt32(hash, reader.GetInt32(7));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    8,
                    MaximumPersistedIdentityCharacters,
                    "manifest source fingerprint"));
            AppendOptionalString(
                hash,
                ReadOptionalBoundedString(
                    reader,
                    9,
                    MaximumPersistedIdentityCharacters,
                    "manifest content fingerprint"));
            AppendInt32(hash, reader.GetInt32(10));
            AppendInt32(hash, reader.GetInt32(11));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    12,
                    MaximumPersistedPathCharacters,
                    "manifest container path"));
            AppendString(
                hash,
                ReadBoundedString(
                    reader,
                    13,
                    MaximumPersistedPathCharacters,
                    "manifest entry path"));
            AppendOptionalInt32(
                hash,
                reader.IsDBNull(14)
                    ? null
                    : reader.GetInt32(14));
            AppendInt64(hash, reader.GetInt64(15));
            AppendInt64(hash, reader.GetInt64(16));
            AppendInt64(hash, reader.GetInt64(17));
        }

        return new AssetManifest(
            count,
            Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant());
    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(
        IncrementalHash hash,
        long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendOptionalString(
        IncrementalHash hash,
        string? value)
    {
        if (value is null)
        {
            AppendInt32(hash, -1);
            return;
        }

        AppendString(hash, value);
    }

    private static void AppendOptionalInt32(
        IncrementalHash hash,
        int? value)
    {
        AppendInt32(hash, value.HasValue ? 1 : 0);
        if (value is { } actual)
        {
            AppendInt32(hash, actual);
        }
    }

    private static string CreateProviderSetFingerprint(
        IReadOnlyList<RetailAssetProviderSnapshot> providers) =>
        RetailAssetIdentity.CreateSourceFingerprint(
            CatalogFormat,
            SchemaVersion,
            string.Join(
                "\n",
                providers.Select(static (provider, ordinal) =>
                    $"{ordinal}|{provider.StableFingerprint}")));

    private static SqliteConnection CreateConnection(
        string path,
        SqliteOpenMode mode) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());

    private static string CreateSnapshotKey(
        string providerId,
        int ordinal) =>
        string.Concat(
            providerId,
            "\0",
            ordinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private static string CreateSourceValidationKey(
        string providerId,
        RetailAssetSourceKind kind,
        int priority,
        string path) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{providerId}\0{(int)kind}\0{priority}\0{Path.GetFullPath(path).ToUpperInvariant()}");

    private static string ReadBoundedString(
        SqliteDataReader reader,
        int ordinal,
        int maximumCharacters,
        string fieldName)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidDataException(
                $"The retail catalog {fieldName} is null.");
        }

        string value = reader.GetString(ordinal);
        ValidatePersistedString(
            value,
            maximumCharacters,
            fieldName);
        return value;
    }

    private static string? ReadOptionalBoundedString(
        SqliteDataReader reader,
        int ordinal,
        int maximumCharacters,
        string fieldName)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ReadBoundedString(
            reader,
            ordinal,
            maximumCharacters,
            fieldName);
    }

    private static void DeleteDestinationSidecar(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException(
                $"SQLite sidecar '{path}' could not be removed before atomic catalog replacement.");
        }
    }

    private static void DeleteSidecarIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct AssetManifest(
        long Count,
        string Sha256);
}
