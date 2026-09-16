using System.Text.Json;
using System.Text.Json.Serialization;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Providers;

if (args.Length != 2) throw new ArgumentException("RigCorpusTool <private-request.json> <private-report.json>");
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};
CorpusRequest request = JsonSerializer.Deserialize<CorpusRequest>(await File.ReadAllTextAsync(args[0]), jsonOptions)
    ?? throw new InvalidDataException("Missing corpus request.");
string install = Path.GetFullPath(request.InstallPath);
string reportPath = Path.GetFullPath(args[1]);
string cacheDirectory = Path.GetFullPath(request.CacheDirectory);
foreach (string output in new[] { reportPath, cacheDirectory })
{
    string relative = Path.GetRelativePath(install, output);
    if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidDataException("Corpus output and cache must be outside the read-only installation.");
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
await using var cache = new Rp6lChunkCache(new()
{
    CacheDirectory = cacheDirectory, MaximumMemoryBytes = 128L * 1024 * 1024,
    MaximumMemoryEntryBytes = 32 * 1024 * 1024, MaximumDiskBytes = 2L * 1024 * 1024 * 1024,
});
await using var providers = Dl1RetailProviderSet.Create(install, cache,
    additionalSourceExtensions: [".scr", ".ascr", ".def", ".chr"]);
Console.WriteLine("Indexing configured installed resources without launching the game.");
RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(providers.Providers, cancellationToken: cancellation.Token);
var roots = request.Roots.Select(static r => r.ResourceType is { } type
    ? RetailAssetLogicalId.Rpack(type, r.Name) : RetailAssetLogicalId.VirtualFile(r.Name)).ToArray();
Dl1InstalledBuildFingerprint build = await new Dl1InstalledBuildFingerprintService().ReadAsync(install, cancellation.Token);
Console.WriteLine($"Capturing {roots.Length} roots from {catalog.Assets.Count} catalog entries.");
Dl1RigCorpusReport report = await Dl1RigCorpusCollector.CollectAsync(catalog, roots, build.BuildFingerprint,
    request.Limits, async (asset, digest, token) =>
    {
        Rp6lArchive archive = await providers.RpackProvider.GetArchiveAsync(asset.Source.ContainerPath, token);
        return await Dl1CompiledCorpusInspector.InspectAsync(asset, archive, cache, digest, token);
    }, cancellation.Token);
var envelope = new
{
    CapturedUtc = DateTimeOffset.UtcNow, Build = build, Report = report,
    ProviderDiagnostics = providers.Diagnostics, RpackSourceErrors = providers.RpackProvider.SourceErrors,
    SourceEnumerationMaximumBytes = 16 * 1024 * 1024,
};
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
string pendingPath = reportPath + ".pending";
await File.WriteAllTextAsync(pendingPath, JsonSerializer.Serialize(envelope, jsonOptions), cancellation.Token);
File.Move(pendingPath, reportPath, overwrite: true);
Console.WriteLine($"Captured {report.Resources.Length} resources; {report.Resources.Count(r => r.Status != Dl1CorpusReadStatus.Read)} unavailable or failed. Runtime binding remains unverified.");

internal sealed record CorpusRequest(string InstallPath, string CacheDirectory, CorpusRoot[] Roots, Dl1RigCorpusLimits? Limits);
internal sealed record CorpusRoot(string Name, short? ResourceType);
