using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Storage;

namespace ReAnimated.Tests;

public sealed class LocalApplicationPathsTests
{
    [Fact]
    public void CanonicalPathsStayInsideOneApplicationRoot()
    {
        string localData = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                "dl-reanimated-path-contract"));

        LocalApplicationPaths paths =
            LocalApplicationPaths.Create(localData);
        string expectedRoot = Path.Combine(
            localData,
            LocalApplicationPaths.ApplicationDirectoryName);

        Assert.Equal(expectedRoot, paths.RootDirectory);
        Assert.Equal(
            Path.Combine(
                expectedRoot,
                "Recovery",
                "workspace.autosave.json"),
            paths.AutosaveFile);
        Assert.Equal(
            Path.Combine(expectedRoot, "CrashReports"),
            paths.CrashDirectory);
        Assert.Equal(
            Path.Combine(expectedRoot, "Logs"),
            paths.LogDirectory);
        Assert.Equal(
            Path.Combine(
                expectedRoot,
                "AssetCatalog",
                "dl1-assets.sqlite3"),
            paths.AssetIndexFile);
        Assert.Equal(
            Path.Combine(expectedRoot, "AssetCache", "Rp6l"),
            paths.RpackCacheDirectory);

        string rootedPrefix =
            expectedRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (string path in new[]
                 {
                     paths.AutosaveFile,
                     paths.CrashDirectory,
                     paths.LogDirectory,
                     paths.AssetIndexFile,
                     paths.RpackCacheDirectory,
                 })
        {
            Assert.StartsWith(
                rootedPrefix,
                path,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WpfPathsUseTheSharedLocalAppDataContract()
    {
        LocalApplicationPaths shared =
            LocalApplicationPaths.CreateDefault();
        AppPaths app = AppPaths.CreateDefault();

        Assert.Equal(shared.RootDirectory, app.RootDirectory);
        Assert.Equal(shared.AutosaveFile, app.AutosaveFile);
        Assert.Equal(shared.CrashDirectory, app.CrashDirectory);
        Assert.Equal(shared.LogDirectory, app.LogDirectory);
        Assert.Equal(shared.AssetIndexFile, app.AssetIndexFile);
        Assert.Equal(
            shared.RpackCacheDirectory,
            app.RpackCacheDirectory);
    }
}
