using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

[Trait("ValidationTier", "Hermetic")]
[Trait("Gate", "CustomModelDeployment")]
public sealed class Dl1RuntimeMeshObjectValidatorTests
{
    [Theory]
    [InlineData(RpackTestCompression.None)]
    [InlineData(RpackTestCompression.Zlib)]
    [InlineData(RpackTestCompression.Lzma)]
    public async Task ValidatesPublishedLayoutAndEmbeddedDependencyWithoutClaimingRuntimeBinding(
        RpackTestCompression compression)
    {
        using var fixture = new Fixture();
        byte[] metadata = WithAlias("shared_motion.scr");
        string path = fixture.Write(metadata, compression: compression);

        var result = await Dl1RuntimeMeshObjectValidator.ValidateAsync(path, "generic_actor", "shared_motion");

        Assert.True(result.RuntimeObjectLayoutValidated);
        Assert.True(result.CompiledModelDependencyValidated);
        Assert.False(result.RuntimeBindingVerified);
        Assert.Equal("shared_motion.scr", result.EmbeddedAnimationScriptAlias);
        Assert.Equal(3, result.AnimationEntityCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(metadata)), Assert.Single(result.MeshItemSha256));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), result.ObjectSha256);
    }

    [Fact]
    public async Task StaticValidationDoesNotRequireAnAnimationAlias()
    {
        using var fixture = new Fixture();
        byte[] metadata = RpackTestData.BuildCompactMeshPayload();
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0x64), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0x68), 0);

        var result = await Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(metadata), "generic_actor");

        Assert.True(result.RuntimeObjectLayoutValidated);
        Assert.False(result.CompiledModelDependencyValidated);
        Assert.False(result.RuntimeBindingVerified);
        Assert.Null(result.EmbeddedAnimationScriptAlias);
        Assert.Equal(0, result.AnimationEntityCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RejectsCompilerAddressingAndCompilerOnlyResourceTypes(bool zeroChunkOffset, bool compilerType)
    {
        using var fixture = new Fixture();
        byte[] bytes = Build(WithAlias("shared_motion.scr"));
        if (zeroChunkOffset) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0);
        if (compilerType) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(74), unchecked((short)0x8110));

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.WriteBytes(bytes), "generic_actor", "shared_motion"));

        Assert.Contains("compiler", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalizationMakesTheCompilerWorkUnitPublishableWithoutChangingPayloads()
    {
        using var fixture = new Fixture();
        byte[] metadata = WithAlias("shared_motion.scr");
        byte[] bytes = Build(metadata);
        uint compilerPayloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), compilerPayloadOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 0);
        bytes[57] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(74), unchecked((short)0x8110));
        string raw = fixture.WriteBytes(bytes);
        string normalized = Path.Combine(fixture.Directory, "published.msh_obj");

        await Rp6lCompilerObjectNormalizer.NormalizeAtomicAsync(raw, normalized);
        var result = await Dl1RuntimeMeshObjectValidator.ValidateAsync(normalized, "generic_actor", "shared_motion");

        Assert.True(result.RuntimeObjectLayoutValidated);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(metadata)), Assert.Single(result.MeshItemSha256));
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0xA1)]
    public async Task RejectsLoadSuppressedMeshPayloadEvenWhenItsContainerAndAliasAreValid(byte itemFlags)
    {
        using var fixture = new Fixture();
        byte[] bytes = Build(WithAlias("shared_motion.scr"));
        bytes[57] = itemFlags;

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.WriteBytes(bytes), "generic_actor", "shared_motion"));

        Assert.Contains("suppresses loading", error.Message);
    }

    [Theory]
    [InlineData((ushort)0x2002)]
    [InlineData((ushort)0x2102)]
    [InlineData((ushort)0x2202)]
    public async Task DoesNotRejectLegalChunkModesOrUnrelatedItemFlagBits(ushort category)
    {
        using var fixture = new Fixture();
        byte[] bytes = Build(WithAlias("shared_motion.scr"));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(38), category);
        bytes[57] = 0xA0;

        var result = await Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.WriteBytes(bytes), "generic_actor", "shared_motion");

        Assert.True(result.RuntimeObjectLayoutValidated);
        Assert.True(result.CompiledModelDependencyValidated);
        Assert.False(result.RuntimeBindingVerified);
    }

    [Fact]
    public async Task RejectsMissingExpectedMeshIdentity()
    {
        using var fixture = new Fixture();
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(WithAlias("shared_motion.scr")), "different_actor", "shared_motion"));
        Assert.Contains("0 resources matching", error.Message);
    }

    [Fact]
    public async Task RejectsAmbiguousExpectedMeshIdentity()
    {
        using var fixture = new Fixture();
        byte[] original = Build(WithAlias("shared_motion.scr"));
        const int resourceRowOffset = 72;
        const int insertOffset = resourceRowOffset + 12;
        byte[] duplicate = new byte[original.Length + 12];
        original.AsSpan(0, insertOffset).CopyTo(duplicate);
        original.AsSpan(resourceRowOffset, 12).CopyTo(duplicate.AsSpan(insertOffset));
        original.AsSpan(insertOffset).CopyTo(duplicate.AsSpan(insertOffset + 12));
        BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(20), 2);
        uint payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(40));
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(40), payloadOffset + 12);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.WriteBytes(duplicate), "generic_actor", "shared_motion"));
        Assert.Contains("2 resources matching", error.Message);
    }

    [Fact]
    public async Task IgnoresARequestedAliasInAnUnreferencedTail()
    {
        using var fixture = new Fixture();
        byte[] wrongAlias = WithAlias("wrong_motion.scr");
        byte[] metadata = [.. wrongAlias, .. Encoding.UTF8.GetBytes("shared_motion.scr\0")];

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(metadata), "generic_actor", "shared_motion"));
        Assert.Contains("wrong_motion.scr", error.Message);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(ulong.MaxValue)]
    public async Task RejectsMissingOrInvalidAliasPointerEvenWhenTheExpectedStringExists(ulong fixupValue)
    {
        using var fixture = new Fixture();
        byte[] metadata = WithAlias("shared_motion.scr");
        BinaryPrimitives.WriteUInt64LittleEndian(metadata.AsSpan(0x48), fixupValue);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(metadata), "generic_actor", "shared_motion"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnterminatedAndInvalidUtf8Alias(bool invalidUtf8)
    {
        using var fixture = new Fixture();
        byte[] metadata = WithAlias("shared_motion.scr");
        int aliasOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(metadata.AsSpan(0x48)) - 1);
        if (invalidUtf8) metadata[aliasOffset] = 0xFF;
        else metadata[^1] = (byte)'x';

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(metadata), "generic_actor", "shared_motion"));
    }

    [Fact]
    public async Task RejectsInvalidHierarchy()
    {
        using var fixture = new Fixture();
        byte[] metadata = WithAlias("shared_motion.scr");
        BinaryPrimitives.WriteInt16LittleEndian(metadata.AsSpan(0xB0 + 0xC6), 0);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(metadata), "generic_actor", "shared_motion"));
        Assert.Contains("hierarchy", error.Message);
    }

    [Fact]
    public async Task StockReferenceRejectsZeroAnimationEntities()
    {
        using var fixture = new Fixture();
        byte[] metadata = WithAlias("shared_motion.scr");
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0x64), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(metadata.AsSpan(0x68), 0);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Dl1RuntimeMeshObjectValidator.ValidateAsync(fixture.Write(metadata), "generic_actor", "shared_motion"));
        Assert.Contains("animation entities", error.Message);
    }

    private static byte[] WithAlias(string alias)
    {
        byte[] original = RpackTestData.BuildCompactMeshPayload();
        byte[] metadata = [.. original, .. Encoding.UTF8.GetBytes(alias + '\0')];
        BinaryPrimitives.WriteUInt64LittleEndian(metadata.AsSpan(0x48), checked((ulong)original.Length + 1));
        return metadata;
    }

    private static byte[] Build(byte[] metadata, RpackTestCompression compression = RpackTestCompression.None) =>
        RpackTestData.BuildArchive("generic_actor", Rp6lResourceTypes.Mesh, [new(0, metadata)], compression);

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = RpackTestData.CreateTemporaryDirectory();
        public string Write(byte[] metadata, RpackTestCompression compression = RpackTestCompression.None) =>
            WriteBytes(Build(metadata, compression));
        public string WriteBytes(byte[] bytes)
        {
            string path = Path.Combine(Directory, "source.msh_obj");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public void Dispose() => RpackTestData.DeleteTemporaryDirectory(Directory);
    }
}
