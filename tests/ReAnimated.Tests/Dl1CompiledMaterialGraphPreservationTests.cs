using System.Buffers.Binary;
using System.Text;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

/// <summary>Hermetic ABDM records verify that append-only material updates preserve every prior graph.</summary>
public sealed class Dl1CompiledMaterialGraphPreservationTests
{
    private const uint MaterialKey = 0x1020_3040;
    private const uint ShaderKey = 0x2030_4050;
    private const uint GraphKey = 0x3040_5060;
    private const uint AttributeKey = 0x4050_6070;

    [Theory]
    [InlineData("shaders")]
    [InlineData("graph_nodes")]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsChangedPriorShaderOrGraphPayload(string containerName)
    {
        Container[] original = Baseline();
        Container[] updated = Baseline();
        int index = Array.FindIndex(updated, container => container.Name == containerName);
        byte[] changed = updated[index].Records[0].Payload.ToArray();
        changed[7] ^= 0x5A;
        updated[index] = updated[index] with
        { Records = [updated[index].Records[0] with { Payload = changed }] };

        Assert.Throws<InvalidDataException>(() => Validate(Build(original), Build(updated)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsMissingPriorGraphKeyEvenWhenMaterialsAreUnchanged()
    {
        Container[] updated = Baseline();
        updated[2] = updated[2] with { Records = [] };

        Assert.Throws<InvalidDataException>(() => Validate(Build(Baseline()), Build(updated)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsMissingPriorGraphContainer()
    {
        Container[] updated = Baseline().Where(container => container.Name != "graph_nodes").ToArray();

        Assert.Throws<InvalidDataException>(() => Validate(Build(Baseline()), Build(updated)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void AcceptsAppendedMaterialAndGraphRecordsWithRelocatedExistingTables()
    {
        Container[] updated = Baseline();
        updated[0] = updated[0] with
        { Records = [updated[0].Records[0], new(MaterialKey + 1, MaterialPayload(MaterialKey + 1, 0xAABB_CCDD))] };
        updated[2] = updated[2] with
        { Records = [updated[2].Records[0], new(GraphKey + 1, OpaquePayload(GraphKey + 1, 0xBBCC_DDEE))] };
        Array.Reverse(updated);

        Validate(Build(Baseline()), Build(updated));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [Trait("ValidationTier", "Hermetic")]
    public void AcceptsOnlyCountDerivedEightByteZeroPaddingForInputAttributes(int count, bool trimPadding)
    {
        byte[] meaningful = AttributePayload(count);
        byte[] aligned = PadTo(meaningful, AlignUp8(meaningful.Length));
        byte[] previous = trimPadding ? aligned : meaningful;
        byte[] updated = trimPadding ? meaningful : aligned;

        Validate(Build(WithAttributes(previous)), Build(WithAttributes(updated)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsNonzeroInputAttributeLogicalTail()
    {
        byte[] original = AttributePayload(2);
        byte[] changed = PadTo(original, AlignUp8(original.Length));
        changed[^1] = 0x7F;

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithAttributes(original)), Build(WithAttributes(changed))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsZeroInputAttributeTailAtAnUndeclaredAlignmentLength()
    {
        byte[] original = AttributePayload(2);
        byte[] changed = PadTo(original, original.Length + 3);

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithAttributes(original)), Build(WithAttributes(changed))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsTruncatedCountedInputAttributes()
    {
        byte[] original = AttributePayload(2);
        byte[] truncated = original[..^1];

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithAttributes(original)), Build(WithAttributes(truncated))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsChangedCountedAttributeBytesDespiteValidZeroPadding()
    {
        byte[] original = AttributePayload(2);
        byte[] changed = PadTo(original, AlignUp8(original.Length));
        changed[5] ^= 0x11;

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithAttributes(original)), Build(WithAttributes(changed))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsAnAdditionalCountedZeroAttributeInsteadOfTreatingItAsPadding()
    {
        byte[] original = AttributePayload(2);
        byte[] changed = PadTo(original, 1 + 4 * 3);
        changed[0] = 3;

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithAttributes(original)), Build(WithAttributes(changed))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DoesNotIgnoreLogicalZeroExtensionOfAnOpaqueShaderRecord()
    {
        Container[] original = Baseline();
        Container[] updated = Baseline();
        updated[1] = updated[1] with
        { Records = [updated[1].Records[0] with { Payload = PadTo(updated[1].Records[0].Payload, 16) }] };

        Assert.Throws<InvalidDataException>(() => Validate(Build(original), Build(updated)));
    }

    [Theory]
    [InlineData(45, false)]
    [InlineData(47, false)]
    [InlineData(31, false)]
    [InlineData(45, true)]
    [InlineData(47, true)]
    [InlineData(31, true)]
    [InlineData(41, false)]
    [InlineData(26, false)]
    [InlineData(34, false)]
    [Trait("ValidationTier", "Hermetic")]
    public void AcceptsCountDerivedFourByteZeroPaddingForTerminatedStrings(int meaningfulLength, bool trimPadding)
    {
        byte[] meaningful = StringPayload(meaningfulLength);
        byte[] padded = PadTo(meaningful, AlignUp4(meaningful.Length));
        byte[] previous = trimPadding ? padded : meaningful;
        byte[] updated = trimPadding ? meaningful : padded;

        Validate(Build(WithString(previous, padded.Length)), Build(WithString(updated, padded.Length)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsEightBytePaddingWhenTheStringRequiresOnlyFourByteAlignment()
    {
        byte[] original = StringPayload(41);
        byte[] changed = PadTo(original, 48);

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithString(original, 44)), Build(WithString(changed, 48))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsNonzeroBytesAfterTheStringTerminator()
    {
        byte[] original = StringPayload(45);
        byte[] changed = PadTo(original, AlignUp8(original.Length));
        changed[^1] = 0x41;

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithString(original, 48)), Build(WithString(changed, 48))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsZeroStringTailAtAnUndeclaredAlignmentLength()
    {
        byte[] original = StringPayload(45);
        byte[] changed = PadTo(original, 46);

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithString(original, 48)), Build(WithString(changed, 48))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsRenamedStringPrefixEvenWithValidZeroPadding()
    {
        byte[] original = StringPayload(45);
        byte[] changed = PadTo(original, AlignUp8(original.Length));
        changed[0] = (byte)'h';

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithString(original, 48)), Build(WithString(changed, 48))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void RejectsStringWithNoNullTerminator()
    {
        byte[] original = StringPayload(45);
        byte[] changed = original.ToArray();
        changed[^1] = (byte)'g';

        Assert.Throws<InvalidDataException>(() =>
            Validate(Build(WithString(original, 48)), Build(WithString(changed, 48))));
    }

    private static Container[] WithString(byte[] payload, int storedLength) => Baseline()
        .Append(new Container("strings", [new Record(0x5060_7080, payload) { StoredByteLength = storedLength }]))
        .ToArray();

    private static byte[] StringPayload(int meaningfulLength)
    {
        byte[] payload = new byte[meaningfulLength];
        payload.AsSpan(0, meaningfulLength - 1).Fill((byte)'g');
        return payload;
    }

    private static void Validate(byte[] original, byte[] updated)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string previousPath = Path.Combine(directory, "previous.mp");
            string updatedPath = Path.Combine(directory, "updated.mp");
            File.WriteAllBytes(previousPath, original);
            File.WriteAllBytes(updatedPath, updated);
            Dl1OfficialModelCompiler.ValidateCompiledMaterialDatabasePreserves(previousPath, updatedPath);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Container[] Baseline() =>
    [
        new("materials", [new(MaterialKey, MaterialPayload(MaterialKey, 0x1234_5678))]),
        new("shaders", [new(ShaderKey, OpaquePayload(ShaderKey, 0x2345_6789))]),
        new("graph_nodes", [new(GraphKey, OpaquePayload(GraphKey, 0x3456_789A))]),
        new("input_attributes", [new(AttributeKey, AttributePayload(2))]),
    ];

    private static Container[] WithAttributes(byte[] payload)
    {
        Container[] containers = Baseline();
        containers[3] = containers[3] with { Records = [new(AttributeKey, payload)] };
        return containers;
    }

    private static byte[] MaterialPayload(uint key, uint value)
    {
        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, key);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), value);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22), 0);
        return payload;
    }

    private static byte[] OpaquePayload(uint key, uint value)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, key);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), value);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 0x4567_89AB);
        return payload;
    }

    private static byte[] AttributePayload(int count)
    {
        byte[] payload = new byte[1 + 4 * count];
        payload[0] = checked((byte)count);
        for (int index = 0; index < count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1 + 4 * index), checked((uint)(0x1020 + index)));
        return payload;
    }

    private static int AlignUp4(int value) => checked((value + 3) & ~3);

    private static int AlignUp8(int value) => checked((value + 7) & ~7);

    private static byte[] PadTo(byte[] payload, int length)
    {
        byte[] result = new byte[length];
        payload.CopyTo(result, 0);
        return result;
    }

    private static byte[] Build(params Container[] containers)
    {
        const int headerSize = 16;
        const int containerRowSize = 48;
        const int recordRowSize = 16;
        int tableOffset = headerSize + containerRowSize * containers.Length;
        int payloadOffset = tableOffset + containers.Sum(container => container.Records.Length) * recordRowSize;
        byte[] output = new byte[payloadOffset + containers.Sum(container => container.Records.Sum(record => record.StoredByteLength ?? record.Payload.Length))];
        "ABDM"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), checked((uint)containers.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), headerSize);
        int tableCursor = tableOffset;
        int payloadCursor = payloadOffset;
        for (int containerIndex = 0; containerIndex < containers.Length; containerIndex++)
        {
            Container container = containers[containerIndex];
            Span<byte> row = output.AsSpan(headerSize + containerIndex * containerRowSize, containerRowSize);
            Encoding.ASCII.GetBytes(container.Name).CopyTo(row);
            BinaryPrimitives.WriteUInt32LittleEndian(row[32..], checked((uint)container.Records.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(row[36..], checked((uint)container.Records.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(row[40..], checked((uint)tableCursor));
            foreach (Record record in container.Records.OrderBy(record => record.Key))
            {
                Span<byte> recordRow = output.AsSpan(tableCursor, recordRowSize);
                BinaryPrimitives.WriteUInt32LittleEndian(recordRow, record.Key);
                BinaryPrimitives.WriteUInt32LittleEndian(recordRow[4..], checked((uint)payloadCursor));
                BinaryPrimitives.WriteUInt32LittleEndian(recordRow[8..], checked((uint)record.Payload.Length));
                BinaryPrimitives.WriteUInt32LittleEndian(recordRow[12..], checked((uint)(record.StoredByteLength ?? record.Payload.Length)));
                record.Payload.CopyTo(output, payloadCursor);
                tableCursor += recordRowSize;
                payloadCursor += record.StoredByteLength ?? record.Payload.Length;
            }
        }
        return output;
    }

    private sealed record Record(uint Key, byte[] Payload)
    {
        public int? StoredByteLength { get; init; }
    }
    private sealed record Container(string Name, Record[] Records);
}
