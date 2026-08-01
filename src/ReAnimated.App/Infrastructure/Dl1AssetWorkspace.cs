using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.App.Infrastructure;

public sealed record Dl1AssetIndexProgress(
    string Stage,
    double Percent,
    string Detail);

public sealed record Dl1AssetIndexResult(
    Dl1InstallLocation Install,
    RetailAssetCatalog Catalog,
    IReadOnlyList<Dl1RetailProviderDiagnostic> ProviderDiagnostics,
    IReadOnlyList<RpackProviderError> RpackSourceErrors);

public sealed record Dl1RetailAnimationTiming(
    FrameRate FrameRate,
    float StartFrame,
    float EndFrame,
    AnimationTimingProvenance Provenance,
    string Detail)
{
    public string SelectionLabel =>
        $"{FrameRate.Numerator}/{FrameRate.Denominator} FPS | " +
        $"frames {StartFrame:0.###}..{EndFrame:0.###} | {Detail}";
}

public sealed class Dl1AnimationTimingConflictException :
    IOException
{
    public Dl1AnimationTimingConflictException(
        string animationName,
        IEnumerable<Dl1RetailAnimationTiming> choices)
        : base(BuildMessage(animationName, choices, out var materialized))
    {
        AnimationName = animationName;
        Choices = materialized;
    }

    public string AnimationName { get; }

    public ImmutableArray<Dl1RetailAnimationTiming> Choices { get; }

    private static string BuildMessage(
        string animationName,
        IEnumerable<Dl1RetailAnimationTiming> choices,
        out ImmutableArray<Dl1RetailAnimationTiming> materialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationName);
        ArgumentNullException.ThrowIfNull(choices);
        materialized = choices.ToImmutableArray();
        if (materialized.Length < 2)
        {
            throw new ArgumentException(
                "A timing conflict requires at least two choices.",
                nameof(choices));
        }

        return
            $"Conflicting exact AnimationScr timing matches exist for '{animationName}'. Select one before playback: " +
            string.Join(
                "; ",
                materialized.Select(static value =>
                    value.SelectionLabel));
    }
}

public sealed record Dl1RetailAnimationPayload(
    RetailAssetRecord Asset,
    Anm2Clip Clip,
    string ResourceSha256,
    Dl1RetailAnimationTiming Timing);

public sealed class Dl1AssetWorkspace : IAsyncDisposable
{
    private const int MaximumAnimationScriptSectionBytes = 64 * 1024 * 1024;
    private readonly string _databasePath;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _indexSerial = new(1, 1);
    private readonly SemaphoreSlim _resourceGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly ConcurrentDictionary<string, Dl1RetailMeshProfile>
        _meshProfiles = new(StringComparer.Ordinal);
    private readonly Dl1RetailMeshClassificationService _meshClassifier =
        new Dl1RetailMeshClassificationService();
    private Dl1RetailProviderSet? _providers;
    private Rp6lChunkCache? _chunkCache;
    private RetailAssetSqliteIndex? _persistentIndex;
    private RetailAssetCatalog? _catalog;
    private Dl1InstallLocation? _install;
    private Dl1MaterialTextureResolver? _materialResolver;
    private bool _disposed;

    public Dl1AssetWorkspace(
        string databasePath,
        string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _databasePath = Path.GetFullPath(databasePath);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public RetailAssetCatalog? Catalog => Volatile.Read(ref _catalog);

    public Dl1InstallLocation? Install => Volatile.Read(ref _install);

    public bool TryGetCachedMeshProfile(
        RetailAssetId assetId,
        out Dl1RetailMeshProfile? profile) =>
        _meshProfiles.TryGetValue(assetId.StableKey, out profile);

    public Task<Dl1AssetIndexResult> IndexSteamInstallAsync(
        IProgress<Dl1AssetIndexProgress>? progress,
        CancellationToken cancellationToken) =>
        IndexSteamInstallAsync(
            progress,
            additionalRpackRoots: null,
            cancellationToken);

    public async Task<Dl1AssetIndexResult> IndexSteamInstallAsync(
        IProgress<Dl1AssetIndexProgress>? progress = null,
        IReadOnlyList<string>? additionalRpackRoots = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeSource.Token);
        CancellationToken token = linkedSource.Token;
        await _indexSerial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            progress?.Report(new Dl1AssetIndexProgress(
                "Discovery",
                4.0,
                "Searching Steam libraries for Dying Light 1"));
            IReadOnlyList<Dl1InstallLocation> locations = await Task.Run(
                () => SteamInstallDiscovery.Discover(),
                token).ConfigureAwait(false);
            Dl1InstallLocation install = locations.FirstOrDefault(
                static location => location.IsValid)
                ?? throw new DirectoryNotFoundException(
                    "No complete Steam installation of Dying Light 1 was found.");
            token.ThrowIfCancellationRequested();

            progress?.Report(new Dl1AssetIndexProgress(
                "Providers",
                12.0,
                $"Opening retail packs under {install.InstallPath}"));
            Rp6lChunkCache? pendingCache = null;
            Dl1RetailProviderSet? pendingProviders = null;
            RetailAssetSqliteIndex? pendingIndex = null;
            try
            {
                pendingCache = new Rp6lChunkCache(
                    new Rp6lChunkCacheOptions
                    {
                        CacheDirectory = _cacheDirectory,
                    });
                pendingProviders = await Task.Run(
                    () => Dl1RetailProviderSet.Create(
                        install.InstallPath,
                        pendingCache,
                        additionalRpackRoots:
                            additionalRpackRoots),
                    token).ConfigureAwait(false);
                pendingIndex = new RetailAssetSqliteIndex(
                    _databasePath);

                progress?.Report(new Dl1AssetIndexProgress(
                    "Catalog",
                    24.0,
                    "Validating the saved catalog against the current DL1 pack fingerprints"));
                RetailAssetCatalog pendingCatalog =
                    await RetailAssetCatalog.BuildAsync(
                        pendingProviders.Providers,
                        pendingIndex,
                        token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Dl1RetailProviderDiagnostic[] providerDiagnostics =
                    pendingProviders.Diagnostics.ToArray();
                RpackProviderError[] rpackSourceErrors =
                    pendingProviders.RpackProvider.SourceErrors.ToArray();

                progress?.Report(new Dl1AssetIndexProgress(
                    "Commit",
                    94.0,
                    pendingCatalog.WasRestoredFromPersistentIndex
                        ? $"Publishing {pendingCatalog.Assets.Count:N0} restored assets"
                        : $"Publishing {pendingCatalog.Assets.Count:N0} newly scanned assets"));
                await ReplaceResourcesAsync(
                    install,
                    pendingCatalog,
                    pendingProviders,
                    pendingCache,
                    pendingIndex,
                    token).ConfigureAwait(false);
                pendingProviders = null;
                pendingCache = null;
                pendingIndex = null;
                progress?.Report(new Dl1AssetIndexProgress(
                    "Ready",
                    100.0,
                    pendingCatalog.WasRestoredFromPersistentIndex
                        ? $"Loaded {pendingCatalog.Assets.Count:N0} DL1 assets from the validated local catalog"
                        : $"Scanned and cached {pendingCatalog.Assets.Count:N0} DL1 assets"));
                return new Dl1AssetIndexResult(
                    install,
                    pendingCatalog,
                    providerDiagnostics,
                    rpackSourceErrors);
            }
            finally
            {
                if (pendingProviders is not null)
                {
                    await pendingProviders.DisposeAsync()
                        .ConfigureAwait(false);
                }

                if (pendingCache is not null)
                {
                    await pendingCache.DisposeAsync()
                        .ConfigureAwait(false);
                }

                if (pendingIndex is not null)
                {
                    await pendingIndex.DisposeAsync()
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _indexSerial.Release();
        }
    }

    public async Task<Dl1MeshPreviewPayload> DecodeMeshAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id.Namespace != RetailAssetNamespace.RpackResource
            || asset.Id.ResourceType != Rp6lResourceTypes.Mesh)
        {
            throw new ArgumentException(
                $"Asset '{asset.Id}' is not a type-{Rp6lResourceTypes.Mesh} DL1 mesh.",
                nameof(asset));
        }

        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeSource.Token);
        CancellationToken token = linkedSource.Token;
        await _resourceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Dl1RetailProviderSet providers = _providers
                ?? throw new InvalidOperationException(
                    "The DL1 asset catalog has not been indexed.");
            Rp6lChunkCache cache = _chunkCache
                ?? throw new InvalidOperationException(
                    "The DL1 RP6L cache is unavailable.");
            Dl1MaterialTextureResolver materialResolver =
                _materialResolver
                ?? throw new InvalidOperationException(
                    "The DL1 retail material resolver is unavailable.");
            Dl1MeshData decoded =
                await DecodeMeshResourceAsync(
                    asset,
                    providers,
                    cache,
                    token).ConfigureAwait(false);
            Dl1RetailMeshProfile profile =
                _meshClassifier.Classify(asset, decoded);
            _meshProfiles[asset.Id.StableKey] = profile;
            decoded = await materialResolver.ResolveAsync(
                decoded,
                token).ConfigureAwait(false);
            Rp6lArchive archive =
                await providers.RpackProvider.GetArchiveAsync(
                    asset.Source.ContainerPath,
                    token).ConfigureAwait(false);
            Rp6lResourceDescriptor resource = GetMeshResource(
                asset,
                archive);
            await using Stream resourceStream =
                await archive.OpenResourceStreamAsync(
                    resource,
                    cache,
                    token).ConfigureAwait(false);
            byte[] contentHash = await SHA256.HashDataAsync(
                resourceStream,
                token).ConfigureAwait(false);
            string resourceSha256 =
                Convert.ToHexString(contentHash)
                    .ToLowerInvariant();
            return Dl1MeshPreviewAdapter.Convert(
                    decoded,
                    resourceSha256) with
            {
                Profile = profile,
            };
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    public async Task<Dl1RetailMeshProfile> ClassifyMeshAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateMeshAsset(asset);
        if (_meshProfiles.TryGetValue(
                asset.Id.StableKey,
                out Dl1RetailMeshProfile? cached))
        {
            return cached;
        }

        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeSource.Token);
        CancellationToken token = linkedSource.Token;
        await _resourceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_meshProfiles.TryGetValue(
                    asset.Id.StableKey,
                    out cached))
            {
                return cached;
            }

            Dl1RetailProviderSet providers = _providers
                ?? throw new InvalidOperationException(
                    "The DL1 asset catalog has not been indexed.");
            Rp6lChunkCache cache = _chunkCache
                ?? throw new InvalidOperationException(
                    "The DL1 RP6L cache is unavailable.");
            Dl1MeshData decoded =
                await DecodeMeshResourceAsync(
                    asset,
                    providers,
                    cache,
                    token).ConfigureAwait(false);
            Dl1RetailMeshProfile profile =
                _meshClassifier.Classify(asset, decoded);
            _meshProfiles[asset.Id.StableKey] = profile;
            return profile;
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    public async Task<Dl1RetailAnimationPayload> DecodeAnimationAsync(
        RetailAssetRecord asset,
        CancellationToken cancellationToken = default)
    {
        return await DecodeAnimationAsync(
            asset,
            selectedTiming: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Dl1RetailAnimationPayload> DecodeAnimationAsync(
        RetailAssetRecord asset,
        Dl1RetailAnimationTiming? selectedTiming,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRpackAsset(asset, Rp6lResourceTypes.Animation);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeSource.Token);
        CancellationToken token = linkedSource.Token;
        await _resourceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Dl1RetailProviderSet providers = _providers
                ?? throw new InvalidOperationException(
                    "The DL1 asset catalog has not been indexed.");
            Rp6lChunkCache cache = _chunkCache
                ?? throw new InvalidOperationException(
                    "The DL1 RP6L cache is unavailable.");
            Rp6lArchive archive =
                await providers.RpackProvider.GetArchiveAsync(
                    asset.Source.ContainerPath,
                    token).ConfigureAwait(false);
            Rp6lResourceDescriptor resource = GetRpackResource(
                asset,
                archive,
                Rp6lResourceTypes.Animation);
            await using Stream stream =
                await archive.OpenResourceStreamAsync(
                    resource,
                    cache,
                    token).ConfigureAwait(false);
            byte[] payload = await ReadBoundedAsync(
                stream,
                Anm2Reader.DefaultMaximumPayloadBytes,
                "retail ANM2",
                token).ConfigureAwait(false);
            string sha256 = Convert.ToHexString(SHA256.HashData(payload))
                .ToLowerInvariant();
            Anm2Clip clip = new Anm2Decoder().Decode(
                payload,
                asset.DisplayName);
            Dl1RetailAnimationTiming timing =
                await ResolveAnimationTimingAsync(
                    asset,
                    providers,
                    cache,
                    selectedTiming,
                    token).ConfigureAwait(false);
            return new Dl1RetailAnimationPayload(
                asset,
                clip,
                sha256,
                timing);
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeSource.Cancel();
        await _indexSerial.WaitAsync().ConfigureAwait(false);
        try
        {
            await _resourceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisposeCurrentResourcesAsync().ConfigureAwait(false);
            }
            finally
            {
                _resourceGate.Release();
            }
        }
        finally
        {
            _indexSerial.Release();
            _lifetimeSource.Dispose();
            _resourceGate.Dispose();
            _indexSerial.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private async Task ReplaceResourcesAsync(
        Dl1InstallLocation install,
        RetailAssetCatalog catalog,
        Dl1RetailProviderSet providers,
        Rp6lChunkCache cache,
        RetailAssetSqliteIndex persistentIndex,
        CancellationToken cancellationToken)
    {
        await _resourceGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await DisposeCurrentResourcesAsync().ConfigureAwait(false);
            _install = install;
            _catalog = catalog;
            _providers = providers;
            _chunkCache = cache;
            _persistentIndex = persistentIndex;
            _meshProfiles.Clear();
            _materialResolver = new Dl1MaterialTextureResolver(
                catalog,
                providers.RpackProvider,
                cache,
                Path.Combine(
                    install.DataPath,
                    "optimized_dx11.mp"));
        }
        finally
        {
            _resourceGate.Release();
        }
    }

    private async ValueTask DisposeCurrentResourcesAsync()
    {
        Dl1RetailProviderSet? providers = _providers;
        Rp6lChunkCache? cache = _chunkCache;
        RetailAssetSqliteIndex? persistentIndex = _persistentIndex;
        _providers = null;
        _chunkCache = null;
        _persistentIndex = null;
        _catalog = null;
        _install = null;
        _materialResolver = null;
        _meshProfiles.Clear();

        if (providers is not null)
        {
            await providers.DisposeAsync().ConfigureAwait(false);
        }

        if (cache is not null)
        {
            await cache.DisposeAsync().ConfigureAwait(false);
        }

        if (persistentIndex is not null)
        {
            await persistentIndex.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Dl1MeshData> DecodeMeshResourceAsync(
        RetailAssetRecord asset,
        Dl1RetailProviderSet providers,
        Rp6lChunkCache cache,
        CancellationToken cancellationToken)
    {
        ValidateMeshAsset(asset);
        if (!string.Equals(
                asset.Source.ProviderId,
                providers.RpackProvider.ProviderId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The selected mesh is not owned by the active RPACK provider.",
                nameof(asset));
        }

        Rp6lArchive archive =
            await providers.RpackProvider.GetArchiveAsync(
                asset.Source.ContainerPath,
                cancellationToken).ConfigureAwait(false);
        Rp6lResourceDescriptor resource = GetMeshResource(
            asset,
            archive);
        return await Dl1MeshResourceDecoder.DecodeAsync(
            archive,
            resource,
            cache,
            cancellationToken).ConfigureAwait(false);
    }

    private static Rp6lResourceDescriptor GetMeshResource(
        RetailAssetRecord asset,
        Rp6lArchive archive)
    {
        int resourceIndex = asset.Source.ResourceIndex
            ?? throw new InvalidDataException(
                "The catalog entry has no RP6L resource index.");
        if (resourceIndex < 0 ||
            resourceIndex >= archive.Resources.Count)
        {
            throw new InvalidDataException(
                $"Resource index {resourceIndex} is outside '{archive.Path}'.");
        }

        Rp6lResourceDescriptor resource =
            archive.Resources[resourceIndex];
        if (resource.ResourceType != Rp6lResourceTypes.Mesh ||
            !string.Equals(
                resource.Name,
                asset.DisplayName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The selected RP6L resource changed after indexing.");
        }

        return resource;
    }

    private async Task<Dl1RetailAnimationTiming> ResolveAnimationTimingAsync(
        RetailAssetRecord animation,
        Dl1RetailProviderSet providers,
        Rp6lChunkCache cache,
        Dl1RetailAnimationTiming? selectedTiming,
        CancellationToken cancellationToken)
    {
        RetailAssetCatalog catalog = _catalog
            ?? throw new InvalidOperationException(
                "The DL1 asset catalog has not been indexed.");
        RetailAssetRecord[] scriptCandidates = catalog.Assets
            .Where(candidate =>
                candidate.Id.Namespace == RetailAssetNamespace.RpackResource &&
                candidate.Id.ResourceType == Rp6lResourceTypes.AnimationScript &&
                string.Equals(
                    candidate.Source.ProviderId,
                    animation.Source.ProviderId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    candidate.Source.ContainerPath,
                    animation.Source.ContainerPath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matches = ImmutableArray.CreateBuilder<Dl1RetailAnimationTiming>();
        foreach (RetailAssetRecord scriptAsset in scriptCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lArchive archive =
                await providers.RpackProvider.GetArchiveAsync(
                    scriptAsset.Source.ContainerPath,
                    cancellationToken).ConfigureAwait(false);
            Rp6lResourceDescriptor resource = GetRpackResource(
                scriptAsset,
                archive,
                Rp6lResourceTypes.AnimationScript);
            Rp6lItemDescriptor[] readable = resource.Items
                .Where(static item => item.HasReadableSize)
                .ToArray();
            if (readable.Length < 2)
            {
                continue;
            }

            await using Stream recordsStream =
                await archive.OpenItemStreamAsync(
                    readable[0],
                    cache,
                    cancellationToken).ConfigureAwait(false);
            await using Stream indexStream =
                await archive.OpenItemStreamAsync(
                    readable[1],
                    cache,
                    cancellationToken).ConfigureAwait(false);
            byte[] records = await ReadBoundedAsync(
                recordsStream,
                MaximumAnimationScriptSectionBytes,
                "AnimationScr records section",
                cancellationToken).ConfigureAwait(false);
            byte[] index = await ReadBoundedAsync(
                indexStream,
                MaximumAnimationScriptSectionBytes,
                "AnimationScr index section",
                cancellationToken).ConfigureAwait(false);
            ParsedAnimationScr parsed = AnimationScrCodec.Parse(
                new AnimationScrSections(records, index));
            foreach (ParsedAnimationScrSequence sequence in parsed.Sequences.Where(
                         sequence => string.Equals(
                             sequence.Name,
                             animation.DisplayName,
                             StringComparison.OrdinalIgnoreCase)))
            {
                matches.Add(new Dl1RetailAnimationTiming(
                    ToFrameRate(sequence.FramesPerSecond),
                    sequence.StartFrame,
                    sequence.EndFrame,
                    AnimationTimingProvenance.ExactRetailAnimationScript,
                    $"Exact type-{Rp6lResourceTypes.AnimationScript} match '{scriptAsset.DisplayName}' from provider {scriptAsset.Source.ProviderId}."));
            }
        }

        Dl1RetailAnimationTiming[] distinct = matches
            .DistinctBy(static value => new
            {
                value.FrameRate,
                value.StartFrame,
                value.EndFrame,
            })
            .ToArray();
        if (distinct.Length > 1 && selectedTiming is not null)
        {
            Dl1RetailAnimationTiming? selected = distinct.FirstOrDefault(
                candidate =>
                    candidate.FrameRate == selectedTiming.FrameRate &&
                    candidate.StartFrame == selectedTiming.StartFrame &&
                    candidate.EndFrame == selectedTiming.EndFrame);
            if (selected is null)
            {
                throw new InvalidDataException(
                    "The chosen AnimationScr timing is no longer one of the exact same-provider/pack matches.");
            }
            return selected;
        }

        return distinct.Length switch
        {
            0 => new Dl1RetailAnimationTiming(
                new FrameRate(30, 1),
                0,
                0,
                AnimationTimingProvenance.Manual30FpsFallback,
                "No exact same-name/provider AnimationScr timing was found; manual 30 FPS is active."),
            1 => distinct[0],
            _ => throw new Dl1AnimationTimingConflictException(
                animation.DisplayName,
                distinct),
        };
    }

    private static FrameRate ToFrameRate(float framesPerSecond)
    {
        if (!float.IsFinite(framesPerSecond) || framesPerSecond <= 0.0f)
        {
            throw new InvalidDataException(
                "AnimationScr contains a non-positive or non-finite cadence.");
        }

        const int denominator = 1000;
        int numerator = checked((int)Math.Round(
            framesPerSecond * denominator,
            MidpointRounding.AwayFromZero));
        return new FrameRate(numerator, denominator);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        await using var output = new MemoryStream();
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length > maximumBytes - read)
            {
                throw new InvalidDataException(
                    $"The {description} exceeds the bounded {maximumBytes:N0}-byte limit.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static Rp6lResourceDescriptor GetRpackResource(
        RetailAssetRecord asset,
        Rp6lArchive archive,
        short expectedType)
    {
        int resourceIndex = asset.Source.ResourceIndex
            ?? throw new InvalidDataException(
                "The catalog entry has no RP6L resource index.");
        if ((uint)resourceIndex >= (uint)archive.Resources.Count)
        {
            throw new InvalidDataException(
                $"Resource index {resourceIndex} is outside '{archive.Path}'.");
        }

        Rp6lResourceDescriptor resource = archive.Resources[resourceIndex];
        if (resource.ResourceType != expectedType ||
            !string.Equals(
                resource.Name,
                asset.DisplayName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The selected RP6L resource changed after indexing.");
        }

        return resource;
    }

    private static void ValidateRpackAsset(
        RetailAssetRecord asset,
        short expectedType)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id.Namespace != RetailAssetNamespace.RpackResource ||
            asset.Id.ResourceType != expectedType)
        {
            throw new ArgumentException(
                $"Asset '{asset.Id}' is not a type-{expectedType} DL1 resource.",
                nameof(asset));
        }
    }

    private static void ValidateMeshAsset(RetailAssetRecord asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id.Namespace != RetailAssetNamespace.RpackResource ||
            asset.Id.ResourceType != Rp6lResourceTypes.Mesh)
        {
            throw new ArgumentException(
                $"Asset '{asset.Id}' is not a type-{Rp6lResourceTypes.Mesh} DL1 mesh.",
                nameof(asset));
        }
    }
}
