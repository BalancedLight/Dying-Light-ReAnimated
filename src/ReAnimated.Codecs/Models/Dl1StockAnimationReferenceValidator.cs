using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1StockAnimationSource(string RelativePath, string Sha256)
{
    public string? ArchiveName { get; init; }
}
public sealed record Dl1StockAnimationReference(string BankName, int SequenceCount,
    ImmutableArray<Dl1StockAnimationSource> Sources)
{
    public required Dl1CompiledAnimationBankReference CompiledBank { get; init; }
}

/// <summary>Resolves a nonempty stock source graph without publishing or shadowing it.</summary>
public static class Dl1StockAnimationReferenceValidator
{
    public static Dl1StockAnimationReference Validate(string dataArchivePath, string bankName,
        string? projectRoot = null, CancellationToken cancellationToken = default)
    {
        string bank = Dl1SourceModelWriter.RequireExactResourceName(bankName, 63, "stock animation bank");
        using ZipArchive archive = ZipFile.OpenRead(dataArchivePath);
        var entries = archive.Entries.ToDictionary(e => e.FullName.Replace('\\','/'), StringComparer.OrdinalIgnoreCase);
        var owners = entries.Values.ToDictionary(e => e, _ => Path.GetFileName(dataArchivePath));
        var dependencyArchives = new List<ZipArchive>();
        var dependencyEntries = new List<(ZipArchiveEntry Entry, string Archive)>();
        bool dependenciesLoaded = false;
        string rootName = $"data/characters/animations/animscripts/{bank}.scr";
        if (!entries.ContainsKey(rootName)) throw new InvalidDataException($"Stock animation bank '{bank}.scr' is missing from the selected retail archive.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = ImmutableArray.CreateBuilder<Dl1StockAnimationSource>();
        int sequences = 0;
        long totalBytes = 0;
        try
        {
            Visit(rootName);
            if (sequences == 0) throw new InvalidDataException($"Stock animation bank '{bank}' has no reachable SeqTrack declarations.");
            return new(bank, sequences, sources.ToImmutable())
            {
                CompiledBank = Dl1CompiledAnimationBankValidator.ValidateAsync(dataArchivePath, bank, cancellationToken)
                    .GetAwaiter().GetResult(),
            };
        }
        finally { foreach (ZipArchive dependency in dependencyArchives) dependency.Dispose(); }

        void Visit(string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inProgress.Add(name))
                throw new InvalidDataException($"Stock animation graph contains an include cycle at '{name}'.");
            if (!seen.Add(name))
            {
                inProgress.Remove(name);
                return;
            }
            if (seen.Count > 2048) throw new InvalidDataException("Stock animation graph exceeds the source-count limit.");
            ZipArchiveEntry entry = entries[name];
            totalBytes += entry.Length;
            if (entry.Length > 8 * 1024 * 1024 || totalBytes > 128 * 1024 * 1024)
                throw new InvalidDataException("Stock animation graph exceeds its bounded source budget.");
            using Stream input = entry.Open();
            using MemoryStream bytes = new();
            input.CopyTo(bytes);
            byte[] content = bytes.ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            if (projectRoot is not null)
            {
                string local = Path.GetFullPath(Path.Combine(projectRoot, name.Replace('/',Path.DirectorySeparatorChar)));
                if (File.Exists(local) && !string.Equals(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(local))), hash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Project file '{name}' shadows the selected stock animation graph. Remove or explicitly reconcile the local override before using stock-reference mode.");
            }
            sources.Add(new(name,hash) { ArchiveName = owners[entry] });
            string text = Encoding.UTF8.GetString(content);
            if (Path.GetExtension(name).Equals(".scr",StringComparison.OrdinalIgnoreCase))
                sequences += AnimationScriptSourceParser.ParseSeqTracks(text).Length;
            foreach (AnimationScriptInclude include in AnimationScriptSourceParser.ParseIncludes(text))
            {
                string fileName = include.ResourceName + include.Extension;
                string adjacent = name[..(name.LastIndexOf('/') + 1)] + fileName;
                if (entries.ContainsKey(adjacent)) { Visit(adjacent); continue; }
                string[] candidates = entries.Keys.Where(candidate => Path.GetFileName(candidate).Equals(fileName,StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 0)
                {
                    LoadDependencies();
                    var matches = dependencyEntries.Where(candidate => candidate.Entry.FullName.Replace('\\','/').Equals(adjacent, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 0)
                        matches = dependencyEntries.Where(candidate => Path.GetFileName(candidate.Entry.FullName).Equals(fileName,StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1)
                    {
                        string dependencyName = matches[0].Entry.FullName.Replace('\\','/');
                        entries.Add(dependencyName, matches[0].Entry);
                        owners.Add(matches[0].Entry, matches[0].Archive);
                        Visit(dependencyName);
                        continue;
                    }
                    if (matches.Length > 1) throw new InvalidDataException($"Stock bank dependency '{fileName}' is ambiguous across {matches.Length} DLC sources.");
                }
                if (candidates.Length != 1)
                    throw new InvalidDataException($"Stock bank dependency '{fileName}' has {candidates.Length} possible sources; exactly one is required.");
                Visit(candidates[0]);
            }
            inProgress.Remove(name);
        }

        void LoadDependencies()
        {
            if (dependenciesLoaded) return;
            dependenciesLoaded = true;
            DirectoryInfo? dw = Directory.GetParent(Path.GetFullPath(dataArchivePath));
            if (dw?.Name.Equals("DW", StringComparison.OrdinalIgnoreCase) != true || dw.Parent is null) return;
            string[] paths = Directory.EnumerateDirectories(dw.Parent.FullName, "DW_DLC*")
                .SelectMany(folder => Directory.EnumerateFiles(folder, "Data*.pak", SearchOption.TopDirectoryOnly))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 128) throw new InvalidDataException("Stock graph DLC archive count exceeds its bound.");
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchive dependency = ZipFile.OpenRead(path);
                dependencyArchives.Add(dependency);
                foreach (ZipArchiveEntry entry in dependency.Entries.Where(e => e.FullName.EndsWith(".scr", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".def", StringComparison.OrdinalIgnoreCase)))
                    dependencyEntries.Add((entry, Path.GetFileName(path)));
            }
        }
    }
}
