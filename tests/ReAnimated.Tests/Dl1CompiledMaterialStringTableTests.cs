using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Materials;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledMaterialStringTableTests
{
    [Fact]
    public void ReusableLookupKeepsExactValueExtentsAndLogicalStoredProviderHashes()
    {
        const string value = "Folder/GENERIC_TEXTURE.dds";
        uint key = Dl1ResourceNameHash.Compute(value);
        byte[] text = Encoding.ASCII.GetBytes(value + "\0");
        byte[] logical = [.. text, .. new byte[((text.Length + 3) & ~3) - text.Length]];
        byte[] stored = [.. logical, 0x91, 0x83];
        byte[] provider = Build(new Container("future", [new(1, [0x13, 0x24])]), new Container("STRINGS", [new(key, logical, stored)]));
        byte[] before = provider.ToArray();
        var table = Dl1CompiledMaterialStringTable.Open(provider.ToImmutableArray());
        var result = Assert.IsType<Dl1CompiledMaterialStringReceipt>(table.Find(key));
        Assert.Equal(value, result.Value);
        Assert.Equal(key, result.Key);
        Assert.Equal(1, result.ContainerIndex);
        Assert.Equal(0, result.EntryIndex);
        Assert.Equal(logical.Length, result.LogicalByteLength);
        Assert.Equal(stored.Length, result.StoredByteLength);
        Assert.Equal(logical, result.LogicalBytes.ToArray());
        Assert.Equal(stored, result.StoredBytes.ToArray());
        Assert.Equal(stored, provider[result.SourceOffset..(result.SourceOffset + result.StoredByteLength)]);
        Assert.Equal(key, BinaryPrimitives.ReadUInt32LittleEndian(provider.AsSpan(result.RowOffset)));
        Assert.Equal(Hash(logical), result.LogicalSha256);
        Assert.Equal(Hash(stored), result.StoredSha256);
        Assert.Equal(Hash(provider), result.ProviderSha256);
        Assert.Equal(table.ProviderSha256, result.ProviderSha256);
        Assert.Equal(value, table.Find(key)!.Value);
        Assert.Equal(before, provider);
    }

    [Fact]
    public void MissingKeyAndAbsentStringContainerReturnNull()
    {
        byte[] provider = Build(new Container("strings", [StringRecord("generic.dds")]));
        var table = Dl1CompiledMaterialStringTable.Open(provider.ToImmutableArray());
        Assert.Equal(1, table.Count);
        Assert.Null(table.Find(Dl1ResourceNameHash.Compute("missing.dds")));
        Assert.Null(Dl1CompiledMaterialStringTable.Read(Build(new Container("future", [new(1, [1, 2])])).ToImmutableArray(), 1));
        Assert.Null(Dl1CompiledMaterialStringTable.Open(Build().ToImmutableArray()).Find(1));
    }

    [Fact]
    public void DuplicateContainersKeysAndUnsortedKeysAreRejectedAtOpen()
    {
        var record = StringRecord("generic.dds");
        Assert.Throws<InvalidDataException>(() => Open(Build(new Container("strings", [record]), new Container("STRINGS", [record]))));
        Assert.Throws<InvalidDataException>(() => Open(Build(new Container("strings", [record, record]))));
        Assert.Throws<InvalidDataException>(() => Open(Build(new Container("strings", [new(2, [0]), new(1, [0])]))));
    }

    [Fact]
    public void TargetTerminationAsciiPaddingAndHashAreValidatedOnFind()
    {
        uint key = Dl1ResourceNameHash.Compute("generic.dds");
        AssertTargetRejected(Build(new Container("strings", [new(key, "generic.dds"u8.ToArray())])), key);
        AssertTargetRejected(Build(new Container("strings", [new(key, [(byte)'a', 0, 0, 1])])), key);
        AssertTargetRejected(Build(new Container("strings", [new(key, [0xff, 0])])), key);
        AssertTargetRejected(Build(new Container("strings", [new(key, "other.dds\0"u8.ToArray())])), key);
        byte[] valid = Build(new Container("strings", [StringRecord("generic.dds")]));
        AssertTargetRejected(Corrupt(valid, 64, key ^ 1), key ^ 1);
    }

    [Fact]
    public void AllContainerTablesAndSelectedStringPayloadExtentsAreBoundedAndDisjoint()
    {
        byte[] valid = Build(new Container("strings", [StringRecord("generic.dds")]));
        foreach (var bytes in new[]
        {
            Corrupt(valid, 8, uint.MaxValue), Corrupt(valid, 40 + 16, uint.MaxValue),
            Corrupt(valid, 68, uint.MaxValue), Corrupt(valid, 72, uint.MaxValue),
            Corrupt(valid, 76, 1), Corrupt(valid, 68, 16), Corrupt(valid, 68, 64),
        }) Assert.Throws<InvalidDataException>(() => Open(bytes));
        byte[] two = Build(new Container("future", [new(1, [1])]), new Container("strings", [StringRecord("generic.dds")]));
        uint firstTable = BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(56));
        Assert.Throws<InvalidDataException>(() => Open(Corrupt(two, 104, firstTable)));
        var record = StringRecord("generic.dds");
        byte[] duplicateExtent = Build(new Container("strings", [record, new(record.Key + 1, record.Logical)]));
        uint firstPayload = BinaryPrimitives.ReadUInt32LittleEndian(duplicateExtent.AsSpan(68));
        Assert.Throws<InvalidDataException>(() => Open(Corrupt(duplicateExtent, 84, firstPayload)));
    }

    [Fact]
    public void ConfiguredBudgetsAndCancellationApplyToOpenAndFind()
    {
        byte[] bytes = Build(new Container("strings", [StringRecord("generic.dds")]));
        Assert.Throws<InvalidDataException>(() => Open(bytes, new() { MaximumDatabaseBytes = bytes.Length - 1 }));
        Assert.Throws<InvalidDataException>(() => Open(bytes, new() { MaximumRecordBytes = 1 }));
        Assert.Throws<InvalidDataException>(() => Open(bytes, new() { MaximumTableBytes = 79 }));
        Assert.Throws<InvalidDataException>(() => Open(bytes, new() { MaximumLogicalBytes = 1 }));
        Assert.Throws<InvalidDataException>(() => Open(bytes, new() { MaximumStoredBytes = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(bytes, new() { MaximumContainers = 0 }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledMaterialStringTable.Open(default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Dl1CompiledMaterialStringTable.Open(bytes.ToImmutableArray(), cancellationToken: cancellation.Token));
        var table = Open(bytes);
        Assert.Throws<OperationCanceledException>(() => table.Find(Dl1ResourceNameHash.Compute("generic.dds"), cancellation.Token));
    }

    private static Dl1CompiledMaterialStringTable Open(byte[] bytes, Dl1CompiledMaterialGraphLimits? limits = null) =>
        Dl1CompiledMaterialStringTable.Open(bytes.ToImmutableArray(), limits);
    private static Record StringRecord(string value) => new(Dl1ResourceNameHash.Compute(value), Encoding.ASCII.GetBytes(value + "\0"));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Corrupt(byte[] source, int offset, uint value)
    {
        byte[] copy = source.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }
    private static void AssertTargetRejected(byte[] provider, uint key) =>
        Assert.Throws<InvalidDataException>(() => Open(provider).Find(key));

    private static byte[] Build(params Container[] containers)
    {
        int table = 16 + containers.Length * 48;
        int payload = table + containers.Sum(container => container.Records.Length) * 16;
        byte[] bytes = new byte[payload + containers.Sum(container => container.Records.Sum(record => (record.Stored ?? record.Logical).Length))];
        "ABDM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)containers.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 16);
        foreach (var pair in containers.Select((container, index) => (container, index)))
        {
            Span<byte> row = bytes.AsSpan(16 + pair.index * 48, 48);
            Encoding.ASCII.GetBytes(pair.container.Name).CopyTo(row);
            BinaryPrimitives.WriteUInt32LittleEndian(row[32..], (uint)pair.container.Records.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[36..], (uint)pair.container.Records.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(row[40..], (uint)table);
            foreach (Record record in pair.container.Records)
            {
                byte[] stored = record.Stored ?? record.Logical;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table), record.Key);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table + 4), (uint)payload);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table + 8), (uint)record.Logical.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table + 12), (uint)stored.Length);
                stored.CopyTo(bytes, payload);
                table += 16;
                payload += stored.Length;
            }
        }
        return bytes;
    }
    private sealed record Record(uint Key, byte[] Logical, byte[]? Stored = null);
    private sealed record Container(string Name, Record[] Records);
}
