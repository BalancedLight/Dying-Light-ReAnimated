using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Materials;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledMaterialGraphReaderTests
{
    [Fact]
    public void PreservesUnknownContainersRowsAndCompleteStoredBytes()
    {
        byte[] logical = [1, 2, 3];
        byte[] stored = [1, 2, 3, 9, 8];
        byte[] bytes = Build(new Container("future_container", [new(7, logical, stored)]));
        var graph = Read(bytes);
        Assert.Equal(Dl1CompiledMaterialGraphReader.Profile, graph.Profile);
        Assert.Equal(bytes, graph.SourceBytes.ToArray());
        Assert.Equal(bytes[..16], graph.HeaderBytes.ToArray());
        Assert.Equal(Hash(bytes), graph.SourceSha256);
        var container = Assert.Single(graph.Containers);
        Assert.Equal("future_container", container.Name);
        Assert.Equal(bytes.AsSpan(container.RowOffset, 48).ToArray(), container.RowBytes.ToArray());
        var record = Assert.Single(container.Records);
        Assert.Equal(7U, record.Key);
        Assert.Equal(logical, record.LogicalBytes.ToArray());
        Assert.Equal(stored, record.StoredBytes.ToArray());
        Assert.Equal(bytes.AsSpan(record.RowOffset, 16).ToArray(), record.RowBytes.ToArray());
        Assert.Equal(Hash(logical), record.LogicalSha256);
        Assert.Equal(Hash(stored), record.StoredSha256);
        Assert.Equal(record.LogicalSha256, record.SemanticSha256);
        Assert.Equal(3, graph.TotalLogicalBytes);
        Assert.Equal(5, graph.TotalStoredBytes);
        Assert.Equal(1, graph.TotalRecords);
    }

    [Fact]
    public void PaddingNormalizationChangesOnlyTheSemanticComparisonView()
    {
        byte[] text = "hello\0"u8.ToArray();
        byte[] attributes = [1, 4, 3, 2, 1];
        var compact = Read(Build(new Container("strings", [new(1, text)]), new Container("input_attributes", [new(2, attributes)])));
        byte[] paddedText = [.. text, 0, 0];
        byte[] paddedAttributes = [.. attributes, 0, 0, 0];
        byte[] completeText = [.. paddedText, 7, 8, 9, 10];
        var padded = Read(Build(new Container("STRINGS", [new(1, paddedText, completeText)]),
            new Container("input_attributes", [new(2, paddedAttributes)])));
        for (int index = 0; index < 2; index++)
        {
            var before = Assert.Single(compact.Containers[index].Records);
            var after = Assert.Single(padded.Containers[index].Records);
            Assert.Equal(before.SemanticSha256, after.SemanticSha256);
            Assert.Equal(before.LogicalByteLength, after.SemanticByteLength);
            Assert.NotEqual(before.LogicalSha256, after.LogicalSha256);
        }
        Assert.Equal("STRINGS", padded.Containers[0].Name);
        Assert.Equal(completeText, padded.Containers[0].Records[0].StoredBytes.ToArray());
        Assert.Equal(paddedText, padded.Containers[0].Records[0].LogicalBytes.ToArray());
    }

    [Theory]
    [InlineData(56, 0)]
    [InlineData(56, 16)]
    [InlineData(68, 0)]
    [InlineData(68, 16)]
    [InlineData(68, 64)]
    public void HeaderContainerAndRecordTablePayloadOverlapIsRejected(int fieldOffset, int value)
    {
        byte[] bytes = Build(new Container("future", [new(1, [1, 2, 3])]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fieldOffset), checked((uint)value));
        Assert.Throws<InvalidDataException>(() => Read(bytes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PayloadAliasesAndPartialOverlapsAreRejected(bool partial)
    {
        byte[] bytes = Build(new Container("future", [new(1, [1, 2, 3]), new(2, [4, 5, 6])]));
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(68));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), first + (partial ? 1U : 0U));
        Assert.Throws<InvalidDataException>(() => Read(bytes));
    }

    [Fact]
    public void TwoContainersCannotShareARecordTable()
    {
        byte[] bytes = Build(new Container("first", [new(1, [1])]), new Container("second", [new(2, [2])]));
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(56));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), first);
        Assert.Throws<InvalidDataException>(() => Read(bytes));
    }

    [Fact]
    public void DuplicateNamesKeysInvalidFlagsAndOutOfRangeExtentsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            Read(Build(new Container("future", [new(1, [1])]), new Container("FUTURE", [new(2, [2])]))));
        Assert.Throws<InvalidDataException>(() =>
            Read(Build(new Container("future", [new(2, [1]), new(1, [2])]))));
        Assert.Throws<InvalidDataException>(() =>
            Read(Build(new Container("future", [new(1, [1]), new(1, [2])]))));
        byte[] valid = Build(new Container("future", [new(1, [1, 2])]));
        AssertCorrupt(valid, 12, 1);
        AssertCorrupt(valid, 52, 2);
        AssertCorrupt(valid, 60, 1);
        AssertCorrupt(valid, 4, uint.MaxValue);
        AssertCorrupt(valid, 8, uint.MaxValue);
        AssertCorrupt(valid, 68, uint.MaxValue);
        AssertCorrupt(valid, 72, 3);
        AssertCorrupt(valid, 76, uint.MaxValue);
        byte[] dirtyNamePadding = valid.ToArray();
        dirtyNamePadding[47] = 1;
        Assert.Throws<InvalidDataException>(() => Read(dirtyNamePadding));
        byte[] emptyName = valid.ToArray();
        emptyName[16] = 0;
        Assert.Throws<InvalidDataException>(() => Read(emptyName));
    }

    [Theory]
    [InlineData("strings", 0)]
    [InlineData("strings", 1)]
    [InlineData("input_attributes", 0)]
    [InlineData("input_attributes", 1)]
    public void KnownContainerSemanticPaddingMustBeValid(string name, int variant)
    {
        byte[] payload = name == "strings"
            ? variant == 0 ? "unterminated"u8.ToArray() : [(byte)'a', 0, 0, 1]
            : variant == 0 ? [2, 0, 0, 0, 0] : [1, 0, 0, 0, 0, 0, 0, 1];
        Assert.Throws<InvalidDataException>(() => Read(Build(new Container(name, [new(1, payload)]))));
    }

    [Fact]
    public void ConfiguredBudgetsAndCancellationAreEnforced()
    {
        byte[] bytes = Build(new Container("future", [new(1, [1, 2, 3], [1, 2, 3, 4, 5])]));
        Assert.Throws<InvalidDataException>(() => Read(bytes, new() { MaximumDatabaseBytes = bytes.Length - 1 }));
        Assert.Throws<InvalidDataException>(() => Read(bytes, new() { MaximumTableBytes = 79 }));
        Assert.Throws<InvalidDataException>(() => Read(bytes, new() { MaximumRecordBytes = 4 }));
        Assert.Throws<InvalidDataException>(() => Read(bytes, new() { MaximumLogicalBytes = 2 }));
        Assert.Throws<InvalidDataException>(() => Read(bytes, new() { MaximumStoredBytes = 4 }));
        byte[] two = Build(new Container("first", [new(1, [1])]), new Container("second", [new(2, [2])]));
        Assert.Throws<InvalidDataException>(() => Read(two, new() { MaximumContainers = 1 }));
        Assert.Throws<InvalidDataException>(() => Read(two, new() { MaximumRecords = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Read(bytes, new() { MaximumTableBytes = 0 }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Dl1CompiledMaterialGraphReader.Read(bytes.ToImmutableArray(), cancellationToken: cancellation.Token));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledMaterialGraphReader.Read(default));
    }

    [Fact]
    public void EveryTruncationOfAReferencedDatabaseIsRejected()
    {
        byte[] bytes = Build(new Container("future", [new(1, [1, 2, 3])]));
        for (int length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => Read(bytes[..length]));
    }

    private static Dl1CompiledMaterialGraphInventory Read(
        byte[] bytes, Dl1CompiledMaterialGraphLimits? limits = null) =>
        Dl1CompiledMaterialGraphReader.Read(bytes.ToImmutableArray(), limits);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AssertCorrupt(byte[] original, int offset, uint value)
    {
        byte[] bytes = original.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        Assert.Throws<InvalidDataException>(() => Read(bytes));
    }

    private static byte[] Build(params Container[] containers)
    {
        int tableStart = 16 + containers.Length * 48;
        int payloadStart = tableStart + containers.Sum(static container => container.Records.Length) * 16;
        byte[] bytes = new byte[payloadStart + containers.Sum(static container =>
            container.Records.Sum(static record => (record.Stored ?? record.Logical).Length))];
        "ABDM"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)containers.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 16);
        int recordCursor = tableStart;
        int payloadCursor = payloadStart;
        for (int index = 0; index < containers.Length; index++)
        {
            Container container = containers[index];
            Span<byte> row = bytes.AsSpan(16 + index * 48, 48);
            Encoding.ASCII.GetBytes(container.Name).CopyTo(row);
            BinaryPrimitives.WriteUInt32LittleEndian(row[32..], checked((uint)container.Records.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(row[36..], checked((uint)container.Records.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(row[40..], checked((uint)recordCursor));
            foreach (Record record in container.Records)
            {
                byte[] stored = record.Stored ?? record.Logical;
                Span<byte> entry = bytes.AsSpan(recordCursor, 16);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, record.Key);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], checked((uint)payloadCursor));
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)record.Logical.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], checked((uint)stored.Length));
                stored.CopyTo(bytes, payloadCursor);
                payloadCursor += stored.Length;
                recordCursor += 16;
            }
        }
        return bytes;
    }

    private sealed record Record(uint Key, byte[] Logical, byte[]? Stored = null);
    private sealed record Container(string Name, Record[] Records);
}
