using System.Buffers.Binary;
using System.Collections;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class Rp6lEffectBundleWriterTests
{
    [Theory]
    [InlineData(Rp6lCompression.None)]
    [InlineData(Rp6lCompression.Zlib)]
    public async Task PublishesACompleteEnvelopeWithExactDefinitionsAndRouting(Rp6lCompression compression)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] payload = Payload(
                ("Effects/Éclat", -128, "FutureFeature(17);\r\nLabel(\"λ\");"),
                ("glow", 127, "UnknownDef()\n"));
            var definitions = Rp6lEffectBundleDecoder.Decode(payload);
            Assert.Equal(payload, Rp6lEffectBundleWriter.BuildPayload(definitions));
            Assert.Equal(Rp6lEffectBundleWriter.Build(definitions, compression),
                Rp6lEffectBundleWriter.Build(definitions, compression));
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            string path = Path.Combine(directory, "effects.rpack");
            var result = await Rp6lEffectBundleWriter.WriteNewAsync(path, definitions, compression, cache);
            Assert.Equal(path, result.Path);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), result.ArchiveSha256);
            var readback = result.Readback;
            Assert.Equal(definitions.ToArray(), readback.Definitions.ToArray());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), readback.PayloadSha256);
            Assert.Equal(1, readback.Header.Version);
            Assert.Equal(compression == Rp6lCompression.Zlib ? 1 : 0, readback.Header.CompressionFlags);
            Assert.Equal(1, readback.Header.Unknown);
            Assert.Equal("FX", readback.Resource.Name);
            Assert.Equal(Rp6lResourceTypes.Effect, readback.Resource.ResourceType);
            Assert.Equal(80, readback.Chunk.Flags);
            Assert.Equal(514, readback.Chunk.Category);
            Assert.Equal(1, readback.Chunk.Unknown0);
            Assert.Equal(2, readback.Chunk.Unknown1);
            Assert.Equal(0, readback.Item.StorageGroupId);
            Assert.Equal(0, readback.Item.Offset);
            Assert.Equal(payload.Length, readback.Item.SizeOrHash);
            Assert.Equal(compression, readback.Chunk.Compression);
            var reopened = await Rp6lEffectBundleWriter.ReadBackAsync(path, cache);
            Assert.Equal(readback.Definitions.ToArray(), reopened.Definitions.ToArray());
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void SubsetsCanBeReorderedAndReceiveNewOffsets()
    {
        var original = Rp6lEffectBundleDecoder.Decode(Payload(
            ("first", 3, "First();"), ("second", -7, "Second();")));
        byte[] rebuilt = Rp6lEffectBundleWriter.BuildPayload([original[1], original[0]]);
        var rows = Rp6lEffectBundleDecoder.Decode(rebuilt);
        Assert.Equal("second", rows[0].Name);
        Assert.Equal(-7, rows[0].Kind);
        Assert.Equal("Second();", rows[0].SourceText);
        Assert.Equal(0, rows[0].EntryOffset);
        Assert.Equal(rows[0].EntryByteLength, rows[1].EntryOffset);
        Assert.NotEqual(original[1].EntryOffset, rows[0].EntryOffset);
    }

    [Fact]
    public void DuplicateNamesInvalidDescriptorsAndStrictUtf8AreRejected()
    {
        var definition = Assert.Single(Rp6lEffectBundleDecoder.Decode(Payload(("spark", 0, "Spark();"))));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition, definition with { Name = "SPARK" }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { EntryOffset = -1 }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { TextOffset = definition.TextOffset + 1 }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { TextByteLength = definition.TextByteLength + 1 }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { EntryByteLength = definition.EntryByteLength + 1 }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { ContentSha256 = new string('0', 64) }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { SourceText = "\uD800" }]));
        Assert.Throws<InvalidDataException>(() =>
            Rp6lEffectBundleWriter.BuildPayload([definition with { SourceText = "Spark();\0tail" }]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Rp6lEffectBundleWriter.Build([definition], Rp6lCompression.Lzma));
    }

    [Fact]
    public void NameSourceAndDefinitionCountBoundsAreEnforced()
    {
        var definition = Assert.Single(Rp6lEffectBundleDecoder.Decode(Payload(("spark", 0, "Spark();"))));
        Assert.Throws<InvalidDataException>(() => Rp6lEffectBundleWriter.BuildPayload(
            [definition with { Name = new string('n', Rp6lEffectBundleDecoder.MaximumNameBytes + 1) }]));
        Assert.Throws<InvalidDataException>(() => Rp6lEffectBundleWriter.BuildPayload(
            [definition with { SourceText = new string('s', Rp6lEffectBundleDecoder.MaximumSourceBytes + 1) }]));
        Assert.Throws<InvalidDataException>(() => Rp6lEffectBundleWriter.BuildPayload(new OversizedDefinitionList()));
    }

    [Fact]
    public async Task AnExistingOutputIsNeverReplaced()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "effects.rpack");
            byte[] existing = "existing-output"u8.ToArray();
            await File.WriteAllBytesAsync(path, existing);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            var definitions = Rp6lEffectBundleDecoder.Decode(Payload(("spark", 0, "Spark();")));
            await Assert.ThrowsAsync<IOException>(() =>
                Rp6lEffectBundleWriter.WriteNewAsync(path, definitions, Rp6lCompression.None, cache));
            Assert.Equal(existing, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task AConcurrentDestinationCreationPreservesThatFileAndRemovesTheTemporaryOutput()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "effects.rpack");
            byte[] existing = "concurrent-output"u8.ToArray();
            var definitions = Rp6lEffectBundleDecoder.Decode(Payload(("spark", 0, "Spark();")));
            var concurrent = new CallbackDefinitionList(definitions, () => File.WriteAllBytes(path, existing));
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await Assert.ThrowsAsync<IOException>(() =>
                Rp6lEffectBundleWriter.WriteNewAsync(path, concurrent, Rp6lCompression.Zlib, cache));
            Assert.Equal(existing, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationLeavesNoPublishedOrTemporaryFile(bool cancelWhileReadingDefinitions)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "effects.rpack");
            var definitions = Rp6lEffectBundleDecoder.Decode(Payload(("spark", 0, "Spark();")));
            using var cancellation = new CancellationTokenSource();
            IReadOnlyList<Rp6lEffectDefinition> input = definitions;
            if (cancelWhileReadingDefinitions)
                input = new CallbackDefinitionList(definitions, cancellation.Cancel);
            else
                cancellation.Cancel();
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Rp6lEffectBundleWriter.WriteNewAsync(path, input, Rp6lCompression.None, cache, cancellation.Token));
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData(36)]
    [InlineData(38)]
    [InlineData(52)]
    [InlineData(54)]
    [InlineData(58)]
    [InlineData(74)]
    public async Task ReadbackRejectsAlteredPhysicalRouting(int offset)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var definitions = Rp6lEffectBundleDecoder.Decode(Payload(("spark", 0, "Spark();")));
            byte[] bytes = Rp6lEffectBundleWriter.Build(definitions, Rp6lCompression.None);
            ushort original = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), (ushort)(original ^ 1));
            string path = Path.Combine(directory, "changed.rpack");
            await File.WriteAllBytesAsync(path, bytes);
            await using var cache = new Rp6lChunkCache(new() { CacheDirectory = Path.Combine(directory, "cache") });
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Rp6lEffectBundleWriter.ReadBackAsync(path, cache));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void AggregateByteBoundIsEnforcedBeforePublication()
    {
        string text = new('s', Rp6lEffectBundleDecoder.MaximumSourceBytes);
        byte[] source = Encoding.UTF8.GetBytes(text);
        string hash = Convert.ToHexStringLower(SHA256.HashData(source));
        var definitions = Enumerable.Range(0, 16).Select(index =>
            new Rp6lEffectDefinition($"entry{index:00}", 0, text, 0, 9, source.Length, hash,
                source.Length + 10)).ToArray();
        Assert.Throws<InvalidDataException>(() => Rp6lEffectBundleWriter.BuildPayload(definitions));
    }

    private static byte[] Payload(params (string Name, sbyte Kind, string Text)[] definitions)
    {
        using var output = new MemoryStream();
        foreach (var definition in definitions)
        {
            output.Write(Encoding.UTF8.GetBytes(definition.Name));
            output.WriteByte(0);
            output.WriteByte(unchecked((byte)definition.Kind));
            output.Write(Encoding.UTF8.GetBytes(definition.Text));
            output.WriteByte(0);
        }
        output.WriteByte(0);
        return output.ToArray();
    }

    private sealed class CallbackDefinitionList(
        ImmutableArray<Rp6lEffectDefinition> definitions,
        Action callback) : IReadOnlyList<Rp6lEffectDefinition>
    {
        private Action? _callback = callback;
        public int Count => definitions.Length;
        public Rp6lEffectDefinition this[int index]
        {
            get
            {
                Action? action = _callback;
                _callback = null;
                action?.Invoke();
                return definitions[index];
            }
        }
        public IEnumerator<Rp6lEffectDefinition> GetEnumerator() =>
            Enumerable.Range(0, Count).Select(index => this[index]).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OversizedDefinitionList : IReadOnlyList<Rp6lEffectDefinition>
    {
        public int Count => Rp6lEffectBundleDecoder.MaximumDefinitions + 1;
        public Rp6lEffectDefinition this[int index] => throw new InvalidOperationException("The bound must be checked first.");
        public IEnumerator<Rp6lEffectDefinition> GetEnumerator() => Enumerable.Empty<Rp6lEffectDefinition>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
