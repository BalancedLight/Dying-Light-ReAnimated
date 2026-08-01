using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.DL1.Assets.Providers;

public sealed record Dl1RetailProviderDiagnostic(
    string Code,
    string Path,
    string Message);

public sealed class Dl1RetailProviderSet : IAsyncDisposable
{
    public const int FirstAdditionalRpackPriority = 100_000_000;

    private readonly Rp6lChunkCache _chunkCache;
    private readonly bool _ownsChunkCache;

    private Dl1RetailProviderSet(
        Rp6lChunkCache chunkCache,
        bool ownsChunkCache,
        IReadOnlyList<IRetailAssetProvider> providers,
        RpackAssetProvider rpackProvider,
        IReadOnlyList<Dl1RetailProviderDiagnostic> diagnostics)
    {
        _chunkCache = chunkCache;
        _ownsChunkCache = ownsChunkCache;
        Providers = providers;
        RpackProvider = rpackProvider;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<IRetailAssetProvider> Providers { get; }

    public RpackAssetProvider RpackProvider { get; }

    public IReadOnlyList<Dl1RetailProviderDiagnostic> Diagnostics { get; }

    public static Dl1RetailProviderSet Create(
        string installPath,
        Rp6lChunkCache? chunkCache = null,
        Rp6lLimits? limits = null,
        IEnumerable<string>? additionalRpackRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        string fullPath = Path.GetFullPath(installPath);
        if (!SteamInstallDiscovery.IsDyingLightInstall(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"'{fullPath}' is not a complete Dying Light installation.");
        }

        Rp6lChunkCache effectiveCache =
            chunkCache ?? new Rp6lChunkCache();
        string installId =
            RetailAssetIdentity.CreateInstallId(fullPath);
        bool ownsCache = chunkCache is null;
        DirectoryInfo root = new(fullPath);
        DirectoryInfo[] dataRoots = root
            .EnumerateDirectories("DW*")
            .Where(static directory =>
                directory.Name.Equals(
                    "DW",
                    StringComparison.OrdinalIgnoreCase) ||
                directory.Name.StartsWith(
                    "DW_DLC",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(static directory =>
                directory.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<RpackSource> rpacks = [];
        List<ZipPakSource> paks = [];
        List<IRetailAssetProvider> providers = [];
        List<Dl1RetailProviderDiagnostic> diagnostics = [];
        HashSet<string> knownRpackPaths =
            new(StringComparer.OrdinalIgnoreCase);
        for (int rootIndex = 0; rootIndex < dataRoots.Length; rootIndex++)
        {
            DirectoryInfo dataRoot = dataRoots[rootIndex];
            bool isBase = dataRoot.Name.Equals(
                "DW",
                StringComparison.OrdinalIgnoreCase);
            int rootPriority = (isBase ? 10_000 : 20_000) + rootIndex * 100;
            DirectoryInfo compiled = new(Path.Combine(
                dataRoot.FullName,
                "Data"));
            if (compiled.Exists)
            {
                int packPriority = rootPriority;
                foreach (FileInfo pack in compiled
                             .EnumerateFiles("*.rpack")
                             .OrderBy(static file =>
                                 file.Name,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    if (knownRpackPaths.Add(pack.FullName))
                    {
                        rpacks.Add(new RpackSource(
                            pack.FullName,
                            packPriority++));
                    }
                }
            }

            int pakPriority = rootPriority + 2_000;
            foreach (FileInfo pak in dataRoot
                         .EnumerateFiles("*.pak")
                         .OrderBy(static file =>
                             file.Name,
                             StringComparer.OrdinalIgnoreCase))
            {
                paks.Add(new ZipPakSource(
                    pak.FullName,
                    pakPriority++));
            }

            providers.Add(new LooseFileAssetProvider(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"dl1-loose-{rootIndex}"),
                dataRoot.FullName,
                [".fed"],
                rootPriority + 5_000,
                maximumFileBytes: 16 * 1024 * 1024,
                installId: installId));
        }

        AddAdditionalRpackRoots(
            additionalRpackRoots,
            rpacks,
            knownRpackPaths,
            diagnostics);

        if (rpacks.Count == 0)
        {
            if (ownsCache)
            {
                effectiveCache.Dispose();
            }

            throw new InvalidDataException(
                "The Dying Light installation contains no retail RP6L packs.");
        }

        RpackAssetProvider rpackProvider = new(
            "dl1-rpacks",
            rpacks,
            effectiveCache,
            limits,
            installId);
        providers.Add(rpackProvider);
        if (paks.Count > 0)
        {
            providers.Add(new ZipPakAssetProvider(
                "dl1-fed-paks",
                paks,
                [".fed"],
                maximumEntryBytes: 16 * 1024 * 1024,
                installId: installId));
        }

        return new Dl1RetailProviderSet(
            effectiveCache,
            ownsCache,
            providers,
            rpackProvider,
            diagnostics);
    }

    public async ValueTask DisposeAsync()
    {
        await RpackProvider.DisposeAsync().ConfigureAwait(false);
        if (_ownsChunkCache)
        {
            await _chunkCache.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void AddAdditionalRpackRoots(
        IEnumerable<string>? configuredRoots,
        List<RpackSource> sources,
        HashSet<string> knownPaths,
        List<Dl1RetailProviderDiagnostic> diagnostics)
    {
        if (configuredRoots is null)
        {
            return;
        }

        int priority = FirstAdditionalRpackPriority;
        foreach (string configuredRoot in configuredRoots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                diagnostics.Add(new Dl1RetailProviderDiagnostic(
                    "additional-rpack-root-empty",
                    string.Empty,
                    "An empty additional RPack root was ignored."));
                continue;
            }

            string fullPath = Path.GetFullPath(configuredRoot);
            FileInfo configuredFile = new(fullPath);
            FileInfo[] packs;
            if (configuredFile.Exists)
            {
                if (!configuredFile.Extension.Equals(
                        ".rpack",
                        StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new Dl1RetailProviderDiagnostic(
                        "additional-rpack-file-unsupported",
                        fullPath,
                        "Only RP6L .rpack files can be added as retail pack sources."));
                    continue;
                }

                packs = [configuredFile];
            }
            else
            {
                DirectoryInfo directory = new(fullPath);
                if (!directory.Exists)
                {
                    diagnostics.Add(new Dl1RetailProviderDiagnostic(
                        "additional-rpack-root-missing",
                        fullPath,
                        "The additional RPack root does not exist and was skipped."));
                    continue;
                }

                try
                {
                    packs = directory
                        .EnumerateFiles("*.rpack", SearchOption.TopDirectoryOnly)
                        .OrderBy(
                            static pack => pack.Name,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new Dl1RetailProviderDiagnostic(
                        "additional-rpack-root-unreadable",
                        fullPath,
                        exception.Message));
                    continue;
                }
            }

            if (packs.Length == 0)
            {
                diagnostics.Add(new Dl1RetailProviderDiagnostic(
                    "additional-rpack-root-empty",
                    fullPath,
                    "The additional root contains no top-level .rpack files."));
                continue;
            }

            foreach (FileInfo pack in packs)
            {
                if (!knownPaths.Add(pack.FullName))
                {
                    diagnostics.Add(new Dl1RetailProviderDiagnostic(
                        "additional-rpack-duplicate",
                        pack.FullName,
                        "The same RPack is already present in the configured retail sources."));
                    continue;
                }

                sources.Add(new RpackSource(
                    pack.FullName,
                    priority));
                if (priority == int.MinValue)
                {
                    throw new InvalidOperationException(
                        "Too many additional RPack sources were configured.");
                }

                priority--;
            }
        }
    }
}
