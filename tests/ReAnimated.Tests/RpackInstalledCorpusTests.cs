using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class RpackInstalledCorpusTests
{
    [Fact(Timeout = 60_000)]
    public async Task InstalledConfiguredPacksOpenWithoutMaterializingLogicalChunksWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        FileInfo[] packs = new DirectoryInfo(install.InstallPath)
            .EnumerateDirectories("DW*")
            .Where(static directory =>
                directory.Name.Equals(
                    "DW",
                    StringComparison.OrdinalIgnoreCase) ||
                directory.Name.StartsWith(
                    "DW_DLC",
                    StringComparison.OrdinalIgnoreCase))
            .Select(static directory => new DirectoryInfo(
                Path.Combine(directory.FullName, "Data")))
            .Where(static directory => directory.Exists)
            .SelectMany(static directory =>
                directory.EnumerateFiles("*.rpack"))
            .OrderBy(static file =>
                file.FullName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packs.Length == 0)
        {
            return;
        }

        long resourceCount = 0;
        bool sawLogicalChunkLargerThanInt32 = false;
        bool sawArchiveRelativeTailChunk = false;
        foreach (FileInfo pack in packs)
        {
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(pack.FullName);
            resourceCount = checked(resourceCount + archive.Resources.Count);
            sawLogicalChunkLargerThanInt32 |= archive.Chunks.Any(
                static chunk => chunk.LogicalSize > int.MaxValue);
            sawArchiveRelativeTailChunk |= archive.Chunks.Any(
                static chunk => chunk.ItemOffsetBias > 0);
        }

        Assert.True(packs.Length >= 50);
        Assert.True(resourceCount >= 30_000);
        Assert.True(sawLogicalChunkLargerThanInt32);
        Assert.True(sawArchiveRelativeTailChunk);
    }
}
