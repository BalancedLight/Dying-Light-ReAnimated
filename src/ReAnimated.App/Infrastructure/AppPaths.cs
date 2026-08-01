using ReAnimated.Core.Storage;

namespace ReAnimated.App.Infrastructure;

public sealed record AppPaths(
    string RootDirectory,
    string AutosaveFile,
    string CrashDirectory,
    string LogDirectory,
    string AssetIndexFile,
    string RpackCacheDirectory)
{
    public static AppPaths CreateDefault()
    {
        LocalApplicationPaths paths =
            LocalApplicationPaths.CreateDefault();
        return new AppPaths(
            paths.RootDirectory,
            paths.AutosaveFile,
            paths.CrashDirectory,
            paths.LogDirectory,
            paths.AssetIndexFile,
            paths.RpackCacheDirectory);
    }
}
