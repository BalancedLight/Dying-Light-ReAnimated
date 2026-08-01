using System.IO;

namespace ReAnimated.Core.Storage;

/// <summary>
/// Canonical per-user storage locations shared by the WPF and in-process CLI
/// entry points. Retail bytes are never copied here; only indexes, bounded
/// caches, recovery data, and diagnostics are stored under LocalAppData.
/// </summary>
public sealed record LocalApplicationPaths(
    string RootDirectory,
    string AutosaveFile,
    string CrashDirectory,
    string LogDirectory,
    string AssetIndexFile,
    string RpackCacheDirectory)
{
    public const string ApplicationDirectoryName = "DLReAnimated";

    public static LocalApplicationPaths CreateDefault() =>
        Create(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData));

    public static LocalApplicationPaths Create(
        string localApplicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            localApplicationDataDirectory);

        string localData = Path.GetFullPath(
            localApplicationDataDirectory);
        string root = Path.Combine(
            localData,
            ApplicationDirectoryName);
        return new LocalApplicationPaths(
            root,
            Path.Combine(
                root,
                "Recovery",
                "workspace.autosave.json"),
            Path.Combine(root, "CrashReports"),
            Path.Combine(root, "Logs"),
            Path.Combine(
                root,
                "AssetCatalog",
                "dl1-assets.sqlite3"),
            Path.Combine(root, "AssetCache", "Rp6l"));
    }
}
