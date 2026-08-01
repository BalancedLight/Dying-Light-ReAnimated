namespace ReAnimated.DL1.Assets.Catalog;

public sealed class RetailAssetCatalog : IRetailAssetCatalog
{
    private readonly IReadOnlyDictionary<string, IRetailAssetProvider> _providers;
    private readonly IReadOnlyDictionary<RetailAssetLogicalId, RetailAssetRecord[]> _candidates;

    private RetailAssetCatalog(
        IReadOnlyDictionary<string, IRetailAssetProvider> providers,
        IReadOnlyDictionary<RetailAssetLogicalId, RetailAssetRecord[]> candidates,
        bool wasRestoredFromPersistentIndex)
    {
        _providers = providers;
        _candidates = candidates;
        WasRestoredFromPersistentIndex = wasRestoredFromPersistentIndex;
        Assets = candidates.Values
            .Select(static rows => rows[0])
            .OrderBy(static row => row.Id.StableKey, StringComparer.Ordinal)
            .ToArray();
        Conflicts = candidates
            .Where(static pair => pair.Value.Length > 1)
            .Select(static pair => new RetailAssetConflict(
                pair.Key,
                pair.Value[0],
                pair.Value[1..]))
            .OrderBy(static conflict =>
                conflict.Id.StableKey,
                StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<RetailAssetRecord> Assets { get; }

    public IReadOnlyList<RetailAssetConflict> Conflicts { get; }

    public bool WasRestoredFromPersistentIndex { get; }

    public static async Task<RetailAssetCatalog> BuildAsync(
        IEnumerable<IRetailAssetProvider> providers,
        RetailAssetSqliteIndex? persistentIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        IRetailAssetProvider[] providerArray = providers.ToArray();
        Dictionary<string, IRetailAssetProvider> providerLookup =
            new(StringComparer.Ordinal);
        foreach (IRetailAssetProvider provider in providerArray)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.ProviderId) ||
                !providerLookup.TryAdd(provider.ProviderId, provider))
            {
                throw new ArgumentException(
                    $"Retail provider ID '{provider.ProviderId}' is invalid or duplicated.",
                    nameof(providers));
            }
        }

        ProviderSnapshotCapture initialCapture =
            persistentIndex is null
                ? ProviderSnapshotCapture.NotRequested
                : await TryCaptureProviderSnapshotsAsync(
                    providerArray,
                    cancellationToken).ConfigureAwait(false);
        if (persistentIndex is not null &&
            initialCapture.Status == ProviderSnapshotCaptureStatus.Captured)
        {
            IReadOnlyList<RetailAssetRecord>? restored =
                await persistentIndex.TryLoadValidatedAsync(
                    initialCapture.Snapshots,
                    cancellationToken).ConfigureAwait(false);
            if (restored is not null)
            {
                ProviderSnapshotCapture confirmedCapture =
                    await TryCaptureProviderSnapshotsAsync(
                        providerArray,
                        cancellationToken).ConfigureAwait(false);
                if (confirmedCapture.Status ==
                    ProviderSnapshotCaptureStatus.Captured &&
                    ProviderSnapshotsMatch(
                        initialCapture.Snapshots,
                        confirmedCapture.Snapshots))
                {
                    foreach (RetailAssetRecord asset in restored)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!providerLookup.TryGetValue(
                                asset.Source.ProviderId,
                                out IRetailAssetProvider? provider))
                        {
                            throw new InvalidDataException(
                                $"Restored provider '{asset.Source.ProviderId}' is not registered.");
                        }

                        ValidateRecord(provider, asset);
                    }

                    return CreateCatalog(
                        providerLookup,
                        restored,
                        wasRestoredFromPersistentIndex: true);
                }
            }
        }

        List<RetailAssetRecord> allAssets = [];
        foreach (IRetailAssetProvider provider in providerArray)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (RetailAssetRecord asset in provider
                               .EnumerateAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRecord(provider, asset);
                allAssets.Add(asset);
            }
        }

        if (persistentIndex is not null &&
            initialCapture.Status ==
            ProviderSnapshotCaptureStatus.Unsupported)
        {
            await persistentIndex.ReplaceSnapshotAsync(
                [],
                allAssets,
                cancellationToken).ConfigureAwait(false);
        }
        else if (persistentIndex is not null)
        {
            ProviderSnapshotCapture finalCapture =
                await TryCaptureProviderSnapshotsAsync(
                    providerArray,
                    cancellationToken).ConfigureAwait(false);
            if (initialCapture.Status ==
                ProviderSnapshotCaptureStatus.Captured &&
                finalCapture.Status ==
                ProviderSnapshotCaptureStatus.Captured &&
                ProviderSnapshotsMatch(
                    initialCapture.Snapshots,
                    finalCapture.Snapshots))
            {
                await persistentIndex.ReplaceSnapshotAsync(
                    finalCapture.Snapshots,
                    allAssets,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return CreateCatalog(
            providerLookup,
            allAssets,
            wasRestoredFromPersistentIndex: false);
    }

    private static RetailAssetCatalog CreateCatalog(
        IReadOnlyDictionary<string, IRetailAssetProvider> providerLookup,
        IReadOnlyList<RetailAssetRecord> assets,
        bool wasRestoredFromPersistentIndex)
    {
        Dictionary<RetailAssetLogicalId, List<RetailAssetRecord>> candidates = [];
        foreach (RetailAssetRecord asset in assets)
        {
            if (!candidates.TryGetValue(
                    asset.Id.LogicalId,
                    out List<RetailAssetRecord>? rows))
            {
                rows = [];
                candidates.Add(asset.Id.LogicalId, rows);
            }

            rows.Add(asset);
        }

        Dictionary<RetailAssetLogicalId, RetailAssetRecord[]> ordered = [];
        foreach ((RetailAssetLogicalId id, List<RetailAssetRecord> rows) in candidates)
        {
            ordered.Add(
                id,
                rows
                    .OrderByDescending(static row => row.Source.Priority)
                    .ThenByDescending(static row => row.Source.Kind)
                    .ThenBy(static row => row.Source.ProviderId, StringComparer.Ordinal)
                    .ThenBy(static row => row.Source.ContainerPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static row => row.Source.EntryPath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static row => row.Id.SourceIndex)
                    .ThenBy(static row => row.Id.SourceFingerprint, StringComparer.Ordinal)
                    .ThenBy(
                        static row => row.Id.ContentFingerprint ?? string.Empty,
                        StringComparer.Ordinal)
                    .ToArray());
        }

        return new RetailAssetCatalog(
            providerLookup,
            ordered,
            wasRestoredFromPersistentIndex);
    }

    public RetailAssetRecord? Resolve(RetailAssetLogicalId id) =>
        _candidates.TryGetValue(id, out RetailAssetRecord[]? rows)
            ? rows[0]
            : null;

    public IReadOnlyList<RetailAssetRecord> GetCandidates(
        RetailAssetLogicalId id) =>
        _candidates.TryGetValue(id, out RetailAssetRecord[]? rows)
            ? rows
            : [];

    public IReadOnlyList<RetailAssetRecord> Search(
        string text,
        int maximumResults = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResults);
        return Assets
            .Where(asset =>
                asset.DisplayName.Contains(
                    text,
                    StringComparison.OrdinalIgnoreCase) ||
                asset.Id.Name.Contains(
                    text,
                    StringComparison.OrdinalIgnoreCase))
            .Take(maximumResults)
            .ToArray();
    }

    public ValueTask<Stream> OpenReadAsync(
        RetailAssetLogicalId id,
        CancellationToken cancellationToken = default)
    {
        RetailAssetRecord asset = Resolve(id)
            ?? throw new KeyNotFoundException(
                $"Retail asset '{id}' is not in the catalog.");
        return OpenReadAsync(asset, cancellationToken);
    }

    public ValueTask<Stream> OpenReadAsync(
        RetailAssetId id,
        CancellationToken cancellationToken = default)
    {
        RetailAssetRecord asset = GetCandidates(id.LogicalId)
            .FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException(
                $"Retail asset identity '{id}' is not in the catalog.");
        return OpenReadAsync(asset, cancellationToken);
    }

    public ValueTask<Stream> OpenReadAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!_providers.TryGetValue(
                asset.Source.ProviderId,
                out IRetailAssetProvider? provider))
        {
            throw new InvalidOperationException(
                $"Provider '{asset.Source.ProviderId}' is not registered.");
        }

        IReadOnlyList<RetailAssetRecord> rows =
            GetCandidates(asset.Id.LogicalId);
        if (!rows.Contains(asset))
        {
            throw new ArgumentException(
                "The asset does not belong to this catalog.",
                nameof(asset));
        }

        return provider.OpenReadAsync(asset, cancellationToken);
    }

    private static void ValidateRecord(
        IRetailAssetProvider provider,
        RetailAssetRecord asset) =>
        RetailAssetRecordValidator.Validate(
            provider.ProviderId,
            asset);

    private static async Task<ProviderSnapshotCapture>
        TryCaptureProviderSnapshotsAsync(
            IRetailAssetProvider[] providers,
            CancellationToken cancellationToken)
    {
        if (providers.Any(static provider =>
                provider is not IRetailAssetSnapshotProvider))
        {
            return ProviderSnapshotCapture.Unsupported;
        }

        try
        {
            List<RetailAssetProviderSnapshot> result =
                new(providers.Length);
            foreach (IRetailAssetProvider provider in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IRetailAssetSnapshotProvider snapshotProvider =
                    (IRetailAssetSnapshotProvider)provider;
                RetailAssetProviderSnapshot snapshot =
                    await snapshotProvider.CaptureSnapshotAsync(
                        cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        provider.ProviderId,
                        snapshot.ProviderId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Provider '{provider.ProviderId}' returned snapshot metadata for '{snapshot.ProviderId}'.");
                }

                result.Add(snapshot);
            }

            return ProviderSnapshotCapture.Captured(
                result.ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return ProviderSnapshotCapture.Failed;
        }
    }

    private static bool ProviderSnapshotsMatch(
        RetailAssetProviderSnapshot[] left,
        RetailAssetProviderSnapshot[] right) =>
        left.Length == right.Length &&
        left.Select(static provider => provider.StableFingerprint)
            .SequenceEqual(
                right.Select(static provider =>
                    provider.StableFingerprint),
                StringComparer.Ordinal);

    private enum ProviderSnapshotCaptureStatus
    {
        NotRequested,
        Unsupported,
        Captured,
        Failed,
    }

    private sealed record ProviderSnapshotCapture(
        ProviderSnapshotCaptureStatus Status,
        RetailAssetProviderSnapshot[] Snapshots)
    {
        public static ProviderSnapshotCapture NotRequested { get; } =
            new(ProviderSnapshotCaptureStatus.NotRequested, []);

        public static ProviderSnapshotCapture Unsupported { get; } =
            new(ProviderSnapshotCaptureStatus.Unsupported, []);

        public static ProviderSnapshotCapture Failed { get; } =
            new(ProviderSnapshotCaptureStatus.Failed, []);

        public static ProviderSnapshotCapture Captured(
            RetailAssetProviderSnapshot[] snapshots) =>
            new(
                ProviderSnapshotCaptureStatus.Captured,
                snapshots);
    }
}
