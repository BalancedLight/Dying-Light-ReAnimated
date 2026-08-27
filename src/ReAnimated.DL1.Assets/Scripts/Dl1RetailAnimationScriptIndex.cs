using System.Collections.Immutable;
using System.IO.Compression;
using ReAnimated.Codecs.Models;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.DL1.Assets.Scripts;

/// <summary>
/// One stock animation script recovered from the shipped source tree.
/// </summary>
public sealed record Dl1RetailAnimationScript(
    string ResourceName,
    string RelativePath,
    string PakPath,
    ImmutableArray<string> ScriptIncludes,
    ImmutableArray<string> DefinitionIncludes,
    int SeqTrackCount,
    bool HasEventBlocks);

/// <summary>
/// The stock <c>data/characters/animations/animscripts</c> source tree, read
/// straight out of the retail <c>.pak</c> archives.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately separate from <c>RetailAssetCatalog</c>. The catalog's
/// asset inventory is pinned evidence (see
/// <c>docs/DL1_RPACK_CORPUS_EVIDENCE.md</c>) and widening the pak provider to
/// admit <c>.scr</c> would move that count, so this index reads the archives
/// directly and keeps its results to itself.
/// </para>
/// <para>
/// What this index proves and what it does not: the script names and the
/// <c>!include</c> graph below are exact retail data. The binding from a
/// character model to the script that animates it is <em>not</em> - no
/// <c>.ascr</c> ships in retail, retail <c>.chr</c> carry no script reference,
/// and the compiled type-322 resource stores neither a model reference nor an
/// include list. Any model-to-script default is therefore a heuristic and
/// belongs in <see cref="Dl1AnimationScriptFamilyDefaults"/>, labelled as one.
/// </para>
/// </remarks>
public sealed class Dl1RetailAnimationScriptIndex
{
    private const string AnimationScriptPrefix =
        "data/characters/animations/animscripts/";
    private const int MaximumEntryBytes = 4 * 1024 * 1024;
    private const int MaximumScripts = 20_000;

    private readonly ImmutableDictionary<string, Dl1RetailAnimationScript>
        _byResourceName;

    private Dl1RetailAnimationScriptIndex(
        string installPath,
        ImmutableArray<Dl1RetailAnimationScript> scripts,
        ImmutableArray<Dl1RetailAnimationScript> roots,
        ImmutableDictionary<string, Dl1RetailAnimationScript> byResourceName)
    {
        InstallPath = installPath;
        Scripts = scripts;
        Roots = roots;
        _byResourceName = byResourceName;
    }

    public string InstallPath { get; }

    /// <summary>Every stock script, ordered by resource name.</summary>
    public ImmutableArray<Dl1RetailAnimationScript> Scripts { get; }

    /// <summary>
    /// Scripts no other stock script includes - the entry points a character
    /// pack is registered against, such as <c>anims_player</c> and
    /// <c>anims_man_all</c>.
    /// </summary>
    public ImmutableArray<Dl1RetailAnimationScript> Roots { get; }

    public static Dl1RetailAnimationScriptIndex Build(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        string fullPath = Path.GetFullPath(installPath);
        if (!SteamInstallDiscovery.IsDyingLightInstall(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"'{fullPath}' is not a complete Dying Light installation.");
        }

        Dictionary<string, Dl1RetailAnimationScript> byResourceName =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string pakPath in EnumeratePakPaths(fullPath))
        {
            ReadPak(pakPath, byResourceName);
            if (byResourceName.Count > MaximumScripts)
            {
                throw new InvalidDataException(
                    "The installation declares an implausible number of animation scripts.");
            }
        }

        ImmutableArray<Dl1RetailAnimationScript> scripts = byResourceName
            .Values
            .OrderBy(
                static script => script.ResourceName,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);
        foreach (Dl1RetailAnimationScript script in scripts)
        {
            foreach (string include in script.ScriptIncludes)
            {
                included.Add(include);
            }
        }

        return new Dl1RetailAnimationScriptIndex(
            fullPath,
            scripts,
            [.. scripts.Where(script =>
                !included.Contains(script.ResourceName))],
            byResourceName.ToImmutableDictionary(
                StringComparer.OrdinalIgnoreCase));
    }

    public bool TryGet(
        string resourceName,
        out Dl1RetailAnimationScript script)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            script = null!;
            return false;
        }

        return _byResourceName.TryGetValue(resourceName.Trim(), out script!);
    }

    public bool Contains(string resourceName) =>
        TryGet(resourceName, out _);

    /// <summary>
    /// Every script reachable from <paramref name="resourceName"/> through
    /// <c>!include</c>, itself included. Cycles are tolerated; stock scripts do
    /// include each other in both directions in places.
    /// </summary>
    public ImmutableArray<string> ResolveIncludeClosure(string resourceName)
    {
        if (!TryGet(resourceName, out Dl1RetailAnimationScript? root))
        {
            return [];
        }

        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        Queue<Dl1RetailAnimationScript> pending = new();
        pending.Enqueue(root);
        visited.Add(root.ResourceName);
        List<string> ordered = [root.ResourceName];
        while (pending.Count > 0)
        {
            Dl1RetailAnimationScript current = pending.Dequeue();
            foreach (string include in current.ScriptIncludes)
            {
                if (!TryGet(include, out Dl1RetailAnimationScript? next) ||
                    !visited.Add(next.ResourceName))
                {
                    continue;
                }

                ordered.Add(next.ResourceName);
                pending.Enqueue(next);
            }
        }

        return [.. ordered];
    }

    private static IEnumerable<string> EnumeratePakPaths(string installPath)
    {
        DirectoryInfo root = new(installPath);
        IEnumerable<DirectoryInfo> dataRoots = root
            .EnumerateDirectories("DW*")
            .Where(static directory =>
                directory.Name.Equals(
                    "DW",
                    StringComparison.OrdinalIgnoreCase) ||
                directory.Name.StartsWith(
                    "DW_DLC",
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(
                static directory => directory.Name,
                StringComparer.OrdinalIgnoreCase);
        foreach (DirectoryInfo dataRoot in dataRoots)
        {
            foreach (FileInfo pak in dataRoot
                         .EnumerateFiles("*.pak")
                         .OrderBy(
                             static file => file.Name,
                             StringComparer.OrdinalIgnoreCase))
            {
                yield return pak.FullName;
            }
        }
    }

    private static void ReadPak(
        string pakPath,
        Dictionary<string, Dl1RetailAnimationScript> byResourceName)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(pakPath);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
            UnauthorizedAccessException)
        {
            return;
        }

        using (archive)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsAnimationScriptEntry(entry) ||
                    entry.Length > MaximumEntryBytes)
                {
                    continue;
                }

                string resourceName = Path.GetFileNameWithoutExtension(
                    entry.FullName);
                if (resourceName.Length == 0 ||
                    byResourceName.ContainsKey(resourceName))
                {
                    // Earlier roots win, matching provider priority: the base
                    // DW install is enumerated before any DW_DLC root.
                    continue;
                }

                Dl1RetailAnimationScript? script = TryReadScript(
                    entry,
                    pakPath,
                    resourceName);
                if (script is not null)
                {
                    byResourceName[resourceName] = script;
                }
            }
        }
    }

    private static Dl1RetailAnimationScript? TryReadScript(
        ZipArchiveEntry entry,
        string pakPath,
        string resourceName)
    {
        string text;
        try
        {
            using Stream stream = entry.Open();
            using StreamReader reader = new(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException)
        {
            return null;
        }

        ImmutableArray<AnimationScriptInclude> includes;
        ImmutableArray<AnimationScriptSeqTrack> tracks;
        try
        {
            includes = AnimationScriptSourceParser.ParseIncludes(text);
            tracks = AnimationScriptSourceParser.ParseSeqTracks(text);
        }
        catch (InvalidDataException)
        {
            // A stock script this parser cannot read is reported as present
            // with no derived detail rather than failing the whole index.
            return new Dl1RetailAnimationScript(
                resourceName,
                entry.FullName,
                pakPath,
                [],
                [],
                0,
                false);
        }

        return new Dl1RetailAnimationScript(
            resourceName,
            entry.FullName,
            pakPath,
            [.. includes
                .Where(static include => include.IsAnimationScript)
                .Select(static include => include.ResourceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. includes
                .Where(static include => !include.IsAnimationScript)
                .Select(static include => include.ResourceName)
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            tracks.Length,
            tracks.Any(static track => track.HasEventBlock));
    }

    private static bool IsAnimationScriptEntry(ZipArchiveEntry entry) =>
        entry.FullName.StartsWith(
            AnimationScriptPrefix,
            StringComparison.OrdinalIgnoreCase) &&
        entry.FullName.EndsWith(".scr", StringComparison.OrdinalIgnoreCase);
}
