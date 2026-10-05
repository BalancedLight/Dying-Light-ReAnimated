using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Materials;

public sealed record Dl1CompiledMaterialStringReceipt(
    string Value, uint Key, int ContainerIndex, int EntryIndex, int RowOffset, int SourceOffset,
    int LogicalByteLength, int StoredByteLength, ImmutableArray<byte> LogicalBytes,
    ImmutableArray<byte> StoredBytes, string LogicalSha256, string StoredSha256, string ProviderSha256);

public sealed class Dl1CompiledMaterialStringTable
{
    public const int MaximumProviderBytes = 256 * 1024 * 1024;
    private const int HeaderBytes = 16;
    private const int ContainerRowBytes = 48;
    private const int RecordRowBytes = 16;
    private readonly ImmutableArray<byte> _provider;
    private readonly Entry[] _entries;
    private readonly int _containerIndex;

    private Dl1CompiledMaterialStringTable(ImmutableArray<byte> provider, Entry[] entries,
        int containerIndex, string providerSha256)
    {
        _provider = provider;
        _entries = entries;
        _containerIndex = containerIndex;
        ProviderSha256 = providerSha256;
    }

    public string ProviderSha256 { get; }
    public int Count => _entries.Length;

    public static Dl1CompiledMaterialStringReceipt? Read(ImmutableArray<byte> providerBytes, uint key,
        Dl1CompiledMaterialGraphLimits? limits = null, CancellationToken cancellationToken = default) =>
        Open(providerBytes, limits, cancellationToken).Find(key, cancellationToken);

    public static Dl1CompiledMaterialStringTable Open(ImmutableArray<byte> providerBytes,
        Dl1CompiledMaterialGraphLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= Dl1CompiledMaterialGraphLimits.Default;
        limits.Validate();
        if (providerBytes.IsDefault || providerBytes.Length < HeaderBytes ||
            providerBytes.Length > MaximumProviderBytes || providerBytes.Length > limits.MaximumDatabaseBytes)
            throw new InvalidDataException("The compiled material provider exceeds its byte bounds.");
        ReadOnlySpan<byte> bytes = providerBytes.AsSpan();
        if (!bytes[..4].SequenceEqual("ABDM"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            throw new InvalidDataException("The compiled material provider header is unsupported.");
        int containers = ReadCount(bytes[4..], limits.MaximumContainers);
        int tableLength = checked(containers * ContainerRowBytes);
        int table = ReadRange(bytes[8..], tableLength, bytes.Length);
        long tableBytes = HeaderBytes + (long)tableLength;
        long totalRecords = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<(int Offset, int Length)> { (0, HeaderBytes), (table, tableLength) };
        int selectedIndex = -1, selectedTable = 0, selectedCount = 0;
        for (int index = 0; index < containers; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> row = bytes.Slice(table + index * ContainerRowBytes, ContainerRowBytes);
            int end = row[..32].IndexOf((byte)0);
            if (end < 1 || row[(end + 1)..32].ContainsAnyExcept((byte)0))
                throw new InvalidDataException("A compiled material container name is malformed.");
            foreach (byte character in row[..end])
                if (character is < 0x20 or > 0x7e)
                    throw new InvalidDataException("A compiled material container name is not ASCII.");
            string name = Encoding.ASCII.GetString(row[..end]);
            if (!names.Add(name)) throw new InvalidDataException("The compiled material provider repeats a container.");
            int count = ReadCount(row[32..], limits.MaximumRecords);
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[36..]) != count ||
                BinaryPrimitives.ReadUInt32LittleEndian(row[44..]) != 0)
                throw new InvalidDataException("A compiled material container layout is unsupported.");
            totalRecords += count;
            if (totalRecords > limits.MaximumRecords) throw new InvalidDataException("The compiled material record budget was exceeded.");
            int recordLength = checked(count * RecordRowBytes);
            int records = ReadRange(row[40..], recordLength, bytes.Length);
            tableBytes += recordLength;
            if (tableBytes > limits.MaximumTableBytes) throw new InvalidDataException("The compiled material table budget was exceeded.");
            if (recordLength > 0) ranges.Add((records, recordLength));
            if (name.Equals("strings", StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
                selectedTable = records;
                selectedCount = count;
            }
        }
        if (tableBytes > limits.MaximumTableBytes) throw new InvalidDataException("The compiled material table budget was exceeded.");
        var entries = new Entry[selectedCount];
        uint previous = 0;
        long logicalTotal = 0, storedTotal = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowOffset = selectedTable + index * RecordRowBytes;
            ReadOnlySpan<byte> row = bytes.Slice(rowOffset, RecordRowBytes);
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(row);
            int logical = ReadCount(row[8..], limits.MaximumRecordBytes);
            int stored = ReadCount(row[12..], limits.MaximumRecordBytes);
            if (logical > stored || (index > 0 && key <= previous))
                throw new InvalidDataException("The material string table repeats keys or has invalid lengths/order.");
            int offset = ReadRange(row[4..], stored, bytes.Length);
            logicalTotal += logical;
            storedTotal += stored;
            if (logicalTotal > limits.MaximumLogicalBytes || storedTotal > limits.MaximumStoredBytes)
                throw new InvalidDataException("The material string payload budget was exceeded.");
            if (stored > 0) ranges.Add((offset, stored));
            entries[index] = new(key, rowOffset, offset, logical, stored);
            previous = key;
        }
        int previousEnd = 0;
        foreach (var range in ranges.Where(static row => row.Length > 0).OrderBy(static row => row.Offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (range.Offset < previousEnd) throw new InvalidDataException("Compiled material string/table extents overlap.");
            previousEnd = checked(range.Offset + range.Length);
        }
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        cancellationToken.ThrowIfCancellationRequested();
        return new(providerBytes, entries, selectedIndex, hash);
    }

    public Dl1CompiledMaterialStringReceipt? Find(uint key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int first = 0, last = _entries.Length - 1;
        while (first <= last)
        {
            int middle = first + (last - first) / 2;
            Entry entry = _entries[middle];
            if (entry.Key < key) { first = middle + 1; continue; }
            if (entry.Key > key) { last = middle - 1; continue; }
            ReadOnlySpan<byte> logical = _provider.AsSpan().Slice(entry.Offset, entry.Logical);
            ReadOnlySpan<byte> stored = _provider.AsSpan().Slice(entry.Offset, entry.Stored);
            int end = logical.IndexOf((byte)0);
            int meaningful = end + 1;
            if (end < 0 || (logical.Length != meaningful && logical.Length != ((meaningful + 3) & ~3)) ||
                logical[meaningful..].ContainsAnyExcept((byte)0))
                throw new InvalidDataException("The selected material string has invalid termination or padding.");
            foreach (byte character in logical[..end])
                if (character is < 0x20 or > 0x7e)
                    throw new InvalidDataException("The selected material string is not a printable ASCII filename.");
            string value = Encoding.ASCII.GetString(logical[..end]);
            if (ComputeFilenameKey(value) != key)
                throw new InvalidDataException("The selected material string does not match its serialized lookup hash.");
            string logicalHash = Convert.ToHexStringLower(SHA256.HashData(logical));
            string storedHash = Convert.ToHexStringLower(SHA256.HashData(stored));
            cancellationToken.ThrowIfCancellationRequested();
            return new(value, key, _containerIndex, middle, entry.RowOffset, entry.Offset, entry.Logical, entry.Stored,
                ImmutableArray.CreateRange(logical.ToArray()), ImmutableArray.CreateRange(stored.ToArray()),
                logicalHash, storedHash, ProviderSha256);
        }
        return null;
    }

    private static uint ComputeFilenameKey(string value)
    {
        string leaf = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
        uint crc = 0x811C9DC5U ^ uint.MaxValue;
        foreach (char character in leaf)
        {
            byte ascii = (byte)(character is >= 'A' and <= 'Z' ? character + ('a' - 'A') : character);
            crc ^= ascii;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320U & unchecked((uint)-(int)(crc & 1)));
        }
        return crc ^ uint.MaxValue;
    }

    private static int ReadCount(ReadOnlySpan<byte> bytes, int maximum)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (value > maximum) throw new InvalidDataException("A compiled material string-table count exceeds its bound.");
        return checked((int)value);
    }

    private static int ReadRange(ReadOnlySpan<byte> bytes, int length, int providerLength)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (offset > providerLength || length > providerLength - (long)offset)
            throw new InvalidDataException("A compiled material string-table extent is outside its provider.");
        return checked((int)offset);
    }

    private sealed record Entry(uint Key, int RowOffset, int Offset, int Logical, int Stored);
}
