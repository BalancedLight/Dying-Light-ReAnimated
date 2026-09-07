using System.Security.Cryptography;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1CompiledAnimationBankReference(
    string BankName,
    string ArchivePath,
    string ArchiveSha256,
    int ResourceIndex,
    int SequenceCount,
    string RecordsSha256,
    string NamesSha256)
{
    public bool BankPresentOnDisk { get; } = true;
    public bool CompiledModelDependencyValidated { get; }
    public bool RuntimeBindingVerified { get; }
}

/// <summary>Checks the installed type-322 resource rather than inferring availability from SCR source.</summary>
public static class Dl1CompiledAnimationBankValidator
{
    public static async Task<Dl1CompiledAnimationBankReference> ValidateAsync(
        string dataArchivePath,
        string bankName,
        CancellationToken cancellationToken = default)
    {
        string bank = Dl1SourceModelWriter.RequireExactResourceName(bankName, 63, "stock animation bank");
        DirectoryInfo dataOwner = Directory.GetParent(Path.GetFullPath(dataArchivePath))!;
        var roots = new List<string> { Path.Combine(dataOwner.FullName, "Data") };
        if (dataOwner.Name.Equals("DW", StringComparison.OrdinalIgnoreCase) && dataOwner.Parent is not null)
        {
            roots.AddRange(Directory.EnumerateDirectories(dataOwner.Parent.FullName, "DW_DLC*")
                .Select(path => Path.Combine(path, "Data")));
        }

        string[] archives = roots.Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.rpack", SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (archives.Length > 2048)
            throw new InvalidDataException("Installed animation-bank archive inventory exceeds its bound.");
        var matches = new List<(Rp6lArchive Archive, Rp6lResourceDescriptor Resource)>();
        foreach (string path in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            matches.AddRange(archive.Resources.Where(resource => resource.ResourceType == Rp6lResourceTypes.AnimationScript &&
                resource.Name.Equals(bank, StringComparison.OrdinalIgnoreCase)).Select(resource => (archive, resource)));
        }

        if (matches.Count != 1)
            throw new InvalidDataException($"Existing animation bank '{bank}' has {matches.Count} compiled type-322 resources in the selected installation; exactly one nonempty bank is required. Reachable SCR source alone does not make a bank available to the engine.");
        (Rp6lArchive selected, Rp6lResourceDescriptor resource) = matches[0];
        if (resource.Items.Count < 2 || !resource.Items[0].HasReadableSize || !resource.Items[1].HasReadableSize)
            throw new InvalidDataException($"Compiled animation bank '{bank}' has no readable sequence/name sections.");
        await using var cache = new Rp6lChunkCache(new()
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "DLReAnimated", "stock-bank-inspection"),
            MaximumMemoryBytes = 128L * 1024 * 1024,
            MaximumMemoryEntryBytes = 64 * 1024 * 1024,
            MaximumDiskBytes = 512L * 1024 * 1024,
        });
        byte[] records = await selected.ReadItemBytesAsync(resource.Items[0], cache, maximumBytes: 64 * 1024 * 1024,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        byte[] names = await selected.ReadItemBytesAsync(resource.Items[1], cache, maximumBytes: 64 * 1024 * 1024,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ParsedAnimationScr parsed = AnimationScrCodec.Parse(new(records, names));
        if (parsed.Sequences.IsEmpty)
            throw new InvalidDataException($"Compiled animation bank '{bank}' contains no sequences.");
        await using FileStream archiveStream = File.OpenRead(selected.Path);
        string archiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false));
        return new(bank, selected.Path, archiveHash, resource.Index, parsed.Sequences.Length,
            Convert.ToHexStringLower(SHA256.HashData(records)), Convert.ToHexStringLower(SHA256.HashData(names)));
    }
}
