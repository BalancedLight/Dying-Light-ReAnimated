using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1CustomMaterialWriterDdsTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void MalformedDdsDimensionsAreRejectedBeforeNormalMapDecode()
    {
        (uint Width, uint Height)[] invalidDimensions =
        [
            (0, 4),
            (4, 0),
            (16_385, 4),
            (4, 16_385),
            (8_193, 8_192),
            (uint.MaxValue, 1),
        ];

        foreach ((uint width, uint height) in invalidDimensions)
        {
            byte[] dds = BuildLegacyDds(width, height, "DXT5", baseMipByteCount: 0);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
                PrepareNormalTexture(dds, CustomModelNormalMapConvention.RgbOpenGl));

            Assert.Contains("dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("DXT1", 7)]
    [InlineData("DXT3", 15)]
    [InlineData("DXT5", 15)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void DdsBaseMipMustContainEveryDeclaredCompressionBlock(
        string fourCc,
        int truncatedPayloadLength)
    {
        byte[] dds = BuildLegacyDds(
            width: 4,
            height: 4,
            fourCc,
            truncatedPayloadLength);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            PrepareNormalTexture(dds, CustomModelNormalMapConvention.RgbOpenGl));

        Assert.Contains("base mip", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requires at least", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void ExactDxt5BaseMipCanBeDecodedAndRepacked()
    {
        byte[] dds = BuildLegacyDds(
            width: 4,
            height: 4,
            fourCc: "DXT5",
            baseMipByteCount: 16);

        Dl1PreparedMaterialSet prepared = PrepareNormalTexture(
            dds,
            CustomModelNormalMapConvention.RgbOpenGl);

        byte[] normalDds = Assert.Single(
            prepared.Files,
            static file => file.Key.EndsWith("_nrm.dds", StringComparison.Ordinal)).Value;
        Assert.True(normalDds.AsSpan().StartsWith("DDS "u8));
        Assert.Equal("DXT5", Encoding.ASCII.GetString(normalDds, 84, 4));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void Dxt1CannotMasqueradeAsAnAlphaGreenNormalMap()
    {
        byte[] dds = BuildLegacyDds(
            width: 4,
            height: 4,
            fourCc: "DXT1",
            baseMipByteCount: 8);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            PrepareNormalTexture(dds, CustomModelNormalMapConvention.Dl1AlphaGreen));

        Assert.Contains("DXT1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("alpha", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Dl1PreparedMaterialSet PrepareNormalTexture(
        byte[] dds,
        CustomModelNormalMapConvention convention)
    {
        byte[] sourceFbx = "Kaydara FBX Binary  synthetic DDS validation"u8.ToArray();
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceFbx)).ToLowerInvariant();
        string textureHash = Convert.ToHexString(SHA256.HashData(dds)).ToLowerInvariant();
        string texturePath = $"textures/{textureHash}.dds";
        var binding = new CustomModelTextureBinding
        {
            Id = new Guid("e0f0c14a-670f-55cf-849f-877efbd83190"),
            Semantic = CustomModelTextureSemantic.Normal,
            SourceKind = CustomModelTextureSourceKind.UserOverride,
            ColorSpace = CustomModelTextureColorSpace.Linear,
            NormalMapConvention = convention,
            DisplayName = "synthetic_normal.dds",
            PackageEntryPath = texturePath,
            OriginalReference = "synthetic_normal.dds",
            ContentSha256 = textureHash,
            MediaType = "image/vnd-ms.dds",
        };
        var package = new CustomModelPackage(
            new CustomModelDocument
            {
                ModelId = new Guid("2d0e4b2f-d3ad-59dc-b295-17bb07588a9e"),
                Name = "Synthetic DDS validation",
                RigMode = CustomModelRigMode.StaticProp,
                Source = new CustomModelSourceIdentity
                {
                    OriginalFileName = "synthetic.fbx",
                    ContentSha256 = sourceHash,
                    FbxVersion = 7400,
                },
                RigSignature = sourceHash,
                Materials =
                [
                    new CustomModelMaterial
                    {
                        Id = new Guid("95519846-a5af-582d-b57d-7d33fc1d5971"),
                        Name = "Synthetic material",
                        Textures = [binding],
                    },
                ],
            },
            sourceFbx.ToImmutableArray(),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(
                texturePath,
                dds.ToImmutableArray()));

        return Dl1CustomMaterialWriter.Prepare(
            package,
            "dds_validation",
            CancellationToken.None);
    }

    private static byte[] BuildLegacyDds(
        uint width,
        uint height,
        string fourCc,
        int baseMipByteCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fourCc);
        if (fourCc.Length != 4)
        {
            throw new ArgumentException("DDS FourCC values must contain four characters.", nameof(fourCc));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(baseMipByteCount);
        byte[] bytes = new byte[checked(128 + baseMipByteCount)];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0008_1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(20, 4),
            checked((uint)baseMipByteCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 4);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(bytes, 84);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        return bytes;
    }
}
