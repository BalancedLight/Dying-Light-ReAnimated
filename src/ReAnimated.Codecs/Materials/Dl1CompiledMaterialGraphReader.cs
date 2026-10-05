using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Materials;

public sealed record Dl1CompiledMaterialGraphLimits
{
    public static Dl1CompiledMaterialGraphLimits Default { get; } = new();
    public int MaximumDatabaseBytes { get; init; } = 512 * 1024 * 1024;
    public int MaximumContainers { get; init; } = 128;
    public int MaximumRecords { get; init; } = 1_000_000;
    public int MaximumTableBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumRecordBytes { get; init; } = 1024 * 1024;
    public long MaximumLogicalBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumStoredBytes { get; init; } = 512L * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumDatabaseBytes <= 0 || MaximumContainers <= 0 || MaximumRecords <= 0 ||
            MaximumTableBytes <= 0 || MaximumRecordBytes <= 0 ||
            MaximumLogicalBytes <= 0 || MaximumStoredBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(Dl1CompiledMaterialGraphLimits), "Graph limits must be positive.");
    }
}

public sealed record Dl1CompiledMaterialGraphRecord(
    uint Key, int RowOffset, int SourceOffset, int LogicalByteLength, int StoredByteLength,
    ImmutableArray<byte> RowBytes, ImmutableArray<byte> LogicalBytes, ImmutableArray<byte> StoredBytes,
    string LogicalSha256, string StoredSha256, int SemanticByteLength, string SemanticSha256);

public sealed record Dl1CompiledMaterialGraphContainer(
    string Name, int RowOffset, int RecordTableOffset, ImmutableArray<byte> RowBytes,
    ImmutableArray<Dl1CompiledMaterialGraphRecord> Records);

public sealed record Dl1CompiledMaterialGraphInventory(
    string Profile, ImmutableArray<byte> SourceBytes, ImmutableArray<byte> HeaderBytes,
    int ContainerTableOffset, ImmutableArray<Dl1CompiledMaterialGraphContainer> Containers,
    int TotalRecords, long TotalLogicalBytes, long TotalStoredBytes, string SourceSha256);

public static class Dl1CompiledMaterialGraphReader
{
    public const string Profile = "dl1-abdm-uncompressed-graph-v1";

    public static Dl1CompiledMaterialGraphInventory Read(
        ImmutableArray<byte> database,
        Dl1CompiledMaterialGraphLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= Dl1CompiledMaterialGraphLimits.Default;
        limits.Validate();
        if (database.IsDefault || database.Length < 16 || database.Length > limits.MaximumDatabaseBytes)
            throw new InvalidDataException("The compiled material graph has an invalid byte length.");
        ReadOnlySpan<byte> bytes = database.AsSpan();
        if (!bytes[..4].SequenceEqual("ABDM"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0)
            throw new InvalidDataException("The compiled material graph has an unsupported header.");
        int containerCount = Count(bytes[4..], limits.MaximumContainers, "container");
        int containerTable = Range(bytes[8..], checked(containerCount * 48), bytes.Length);
        long tableBytes = checked(16L + containerCount * 48L);
        var containers = new List<ContainerSpec>(containerCount);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<GraphRange> { new(0, 16) };
        AddRange(ranges, containerTable, checked(containerCount * 48));
        int totalRecords = 0;
        for (int index = 0; index < containerCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowOffset = containerTable + index * 48;
            ReadOnlySpan<byte> row = bytes.Slice(rowOffset, 48);
            int nameEnd = row[..32].IndexOf((byte)0);
            if (nameEnd < 1 || row[(nameEnd + 1)..32].ContainsAnyExcept((byte)0) ||
                !PrintableAscii(row[..nameEnd]))
                throw new InvalidDataException("A compiled graph container name is invalid.");
            string name = Encoding.ASCII.GetString(row[..nameEnd]);
            if (!names.Add(name))
                throw new InvalidDataException("The compiled graph repeats a container name.");
            int count = Count(row[32..], limits.MaximumRecords, "record");
            if (BinaryPrimitives.ReadUInt32LittleEndian(row[36..]) != count ||
                BinaryPrimitives.ReadUInt32LittleEndian(row[44..]) != 0)
                throw new InvalidDataException("A compiled graph container has an unsupported layout.");
            totalRecords = checked(totalRecords + count);
            if (totalRecords > limits.MaximumRecords)
                throw new InvalidDataException("The compiled graph record budget was exceeded.");
            int recordBytes = checked(count * 16);
            int recordsOffset = Range(row[40..], recordBytes, bytes.Length);
            tableBytes = checked(tableBytes + recordBytes);
            if (tableBytes > limits.MaximumTableBytes)
                throw new InvalidDataException("The compiled graph table budget was exceeded.");
            AddRange(ranges, recordsOffset, recordBytes);
            containers.Add(new(name, rowOffset, recordsOffset, count));
        }
        if (tableBytes > limits.MaximumTableBytes)
            throw new InvalidDataException("The compiled graph table budget was exceeded.");
        ValidateDisjoint(ranges, cancellationToken);

        var records = new List<RecordSpec>(totalRecords);
        long logicalTotal = 0;
        long storedTotal = 0;
        foreach (ContainerSpec container in containers)
        {
            uint previous = 0;
            for (int index = 0; index < container.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rowOffset = container.RecordTableOffset + index * 16;
                ReadOnlySpan<byte> row = bytes.Slice(rowOffset, 16);
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(row);
                int logical = Count(row[8..], limits.MaximumRecordBytes, "logical byte");
                int stored = Count(row[12..], limits.MaximumRecordBytes, "stored byte");
                if (logical > stored || (index > 0 && key <= previous))
                    throw new InvalidDataException("A compiled graph record has invalid lengths or key order.");
                int offset = Range(row[4..], stored, bytes.Length);
                logicalTotal = checked(logicalTotal + logical);
                storedTotal = checked(storedTotal + stored);
                if (logicalTotal > limits.MaximumLogicalBytes || storedTotal > limits.MaximumStoredBytes)
                    throw new InvalidDataException("The compiled graph payload budget was exceeded.");
                AddRange(ranges, offset, stored);
                records.Add(new(rowOffset, key, offset, logical, stored));
                previous = key;
            }
        }
        ValidateDisjoint(ranges, cancellationToken);

        int recordIndex = 0;
        var result = ImmutableArray.CreateBuilder<Dl1CompiledMaterialGraphContainer>(containerCount);
        foreach (ContainerSpec container in containers)
        {
            var entries = ImmutableArray.CreateBuilder<Dl1CompiledMaterialGraphRecord>(container.Count);
            for (int index = 0; index < container.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RecordSpec record = records[recordIndex++];
                ReadOnlySpan<byte> logical = bytes.Slice(record.SourceOffset, record.Logical);
                ReadOnlySpan<byte> stored = bytes.Slice(record.SourceOffset, record.Stored);
                int meaningful = SemanticLength(container.Name, logical);
                ImmutableArray<byte> storedBytes = Copy(stored);
                ImmutableArray<byte> logicalBytes = record.Logical == record.Stored ? storedBytes : Copy(logical);
                entries.Add(new(record.Key, record.RowOffset, record.SourceOffset, record.Logical, record.Stored,
                    Copy(bytes.Slice(record.RowOffset, 16)), logicalBytes, storedBytes,
                    Digest(logical), Digest(stored), meaningful, Digest(logical[..meaningful])));
            }
            result.Add(new(container.Name, container.RowOffset, container.RecordTableOffset,
                Copy(bytes.Slice(container.RowOffset, 48)), entries.MoveToImmutable()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Profile, database, Copy(bytes[..16]), containerTable, result.MoveToImmutable(),
            totalRecords, logicalTotal, storedTotal, Digest(bytes));
    }

    private static int SemanticLength(string container, ReadOnlySpan<byte> logical)
    {
        if (container.Equals("input_attributes", StringComparison.OrdinalIgnoreCase))
        {
            if (logical.IsEmpty)
                throw new InvalidDataException("The input attribute record is missing its count.");
            int meaningful = 1 + 4 * logical[0];
            if (meaningful > logical.Length ||
                (logical.Length != meaningful && logical.Length != ((meaningful + 7) & ~7)) ||
                logical[meaningful..].ContainsAnyExcept((byte)0))
                throw new InvalidDataException("The input attribute record has invalid count or padding.");
            return meaningful;
        }
        if (container.Equals("strings", StringComparison.OrdinalIgnoreCase))
        {
            int end = logical.IndexOf((byte)0);
            int meaningful = end + 1;
            if (end < 0 || (logical.Length != meaningful && logical.Length != ((meaningful + 3) & ~3)) ||
                logical[meaningful..].ContainsAnyExcept((byte)0))
                throw new InvalidDataException("The string record has invalid termination or padding.");
            return meaningful;
        }
        return logical.Length;
    }

    private static int Count(ReadOnlySpan<byte> row, int maximum, string field)
    {
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(row);
        if (count > maximum)
            throw new InvalidDataException($"The compiled graph {field} count exceeds its limit.");
        return checked((int)count);
    }

    private static int Range(ReadOnlySpan<byte> row, int count, int length)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(row);
        if (offset > length || count > length - (long)offset)
            throw new InvalidDataException("A compiled graph range is outside the database.");
        return checked((int)offset);
    }

    private static bool PrintableAscii(ReadOnlySpan<byte> value)
    {
        foreach (byte item in value)
            if (item is < 0x20 or > 0x7E)
                return false;
        return true;
    }

    private static void AddRange(List<GraphRange> ranges, int offset, int length)
    {
        if (length > 0)
            ranges.Add(new(offset, length));
    }

    private static void ValidateDisjoint(List<GraphRange> ranges, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ranges.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        long previousEnd = 0;
        foreach (GraphRange range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (range.Offset < previousEnd)
                throw new InvalidDataException("The compiled graph has overlapping header, table, or payload ranges.");
            previousEnd = (long)range.Offset + range.Length;
        }
    }

    private static ImmutableArray<byte> Copy(ReadOnlySpan<byte> bytes) =>
        ImmutableArray.CreateRange(bytes.ToArray());

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record ContainerSpec(string Name, int RowOffset, int RecordTableOffset, int Count);
    private sealed record RecordSpec(int RowOffset, uint Key, int SourceOffset, int Logical, int Stored);
    private readonly record struct GraphRange(int Offset, int Length);
}
