using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CustomModelTextureDecoderTests
{
    [Fact]
    public void PickerExtensionsExactlyMatchSharedDecoderContract()
    {
        string[] pickerExtensions = Regex.Matches(
                WindowsProjectFileDialogService.CustomModelTextureFilter,
                @"\*\.[a-z0-9]+",
                RegexOptions.IgnoreCase)
            .Select(static match => match.Value[1..].ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            CustomModelTextureDecoder.SupportedExtensions.Order(StringComparer.Ordinal),
            pickerExtensions);
        Assert.DoesNotContain(".tif", pickerExtensions);
        Assert.DoesNotContain(".tiff", pickerExtensions);
    }

    [Theory]
    [MemberData(nameof(SupportedPayloads))]
    public void EverySupportedEncodingDecodesAndSurvivesDl1MaterialPreparation(
        string displayName,
        byte[] payload)
    {
        Assert.True(CustomModelTextureDecoder.TryDecode(
            payload,
            displayName,
            out CustomModelDecodedTexture? decoded,
            out string failure), failure);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);

        Dl1PreparedMaterialSet prepared = Dl1CustomMaterialWriter.Prepare(
            PackageWithTexture(payload, displayName, CustomModelTextureSemantic.BaseColor),
            "decoder_test",
            CancellationToken.None);
        Assert.Contains(prepared.Files.Keys, static path => path.EndsWith(".dds", StringComparison.Ordinal));
    }

    [Fact]
    public void MaskBindingProducesMapAndMaterialElementAndMissingBaseColorIsReported()
    {
        byte[] payload = BuildTga();
        Dl1PreparedMaterialSet prepared = Dl1CustomMaterialWriter.Prepare(
            PackageWithTexture(payload, "mask.tga", CustomModelTextureSemantic.Mask),
            "mask_test",
            CancellationToken.None);

        string mask = Assert.Single(
            prepared.Files.Keys,
            static path => path.EndsWith("_msk.dds", StringComparison.Ordinal));
        string material = System.Text.Encoding.UTF8.GetString(
            Assert.Single(prepared.Files, static file => file.Key.EndsWith(".dmt", StringComparison.Ordinal)).Value);
        Assert.Contains($"<msk_0_tex>\"{mask}\"</msk_0_tex>", material, StringComparison.Ordinal);
        Assert.Contains(prepared.Notes, static note => note.Contains("opaque white", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> SupportedPayloads()
    {
        yield return ["sample.tga", BuildTga()];
        yield return ["sample_bc7.dds", EncodeDds(CompressionFormat.Bc7, preferDx10: true)];
        yield return ["sample_rgba.dds", BuildUncompressedDds()];
        yield return ["sample_dxt5.dds", EncodeDds(CompressionFormat.Bc3, preferDx10: false)];
    }

    private static byte[] EncodeDds(CompressionFormat format, bool preferDx10)
    {
        byte[] rgba =
        [
            255, 0, 0, 255, 0, 255, 0, 255,
            0, 0, 255, 255, 255, 255, 255, 128,
        ];
        var encoder = new BcEncoder();
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.Format = format;
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.DdsPreferDxt10Header = preferDx10;
        using var stream = new MemoryStream();
        encoder.EncodeToStream(rgba, 2, 2, PixelFormat.Rgba32, stream);
        return stream.ToArray();
    }

    private static byte[] BuildTga()
    {
        byte[] tga = new byte[18 + 16];
        tga[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14, 2), 2);
        tga[16] = 32;
        tga[17] = 0x28;
        byte[] bgra =
        [
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 128,
        ];
        bgra.CopyTo(tga, 18);
        return tga;
    }

    private static byte[] BuildUncompressedDds()
    {
        byte[] dds = new byte[128 + 16];
        "DDS "u8.CopyTo(dds);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(8, 4), 0x100F);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(20, 4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(92, 4), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(96, 4), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(100, 4), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(104, 4), 0xFF000000);
        BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(108, 4), 0x1000);
        BuildTga().AsSpan(18, 16).CopyTo(dds.AsSpan(128));
        return dds;
    }

    private static CustomModelPackage PackageWithTexture(
        byte[] texture,
        string displayName,
        CustomModelTextureSemantic semantic)
    {
        byte[] sourceFbx = "Kaydara FBX Binary  synthetic texture decoder"u8.ToArray();
        string sourceHash = Convert.ToHexString(SHA256.HashData(sourceFbx)).ToLowerInvariant();
        string textureHash = Convert.ToHexString(SHA256.HashData(texture)).ToLowerInvariant();
        string extension = Path.GetExtension(displayName).ToLowerInvariant();
        string entry = $"textures/{textureHash}{extension}";
        var material = new CustomModelMaterial
        {
            Name = "material",
            Textures =
            [
                new CustomModelTextureBinding
                {
                    Semantic = semantic,
                    SourceKind = CustomModelTextureSourceKind.UserOverride,
                    DisplayName = displayName,
                    PackageEntryPath = entry,
                    ContentSha256 = textureHash,
                    MediaType = extension == ".dds" ? "image/vnd-ms.dds" : "image/x-tga",
                },
            ],
        };
        var document = new CustomModelDocument
        {
            Name = "decoder test",
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "decoder.fbx",
                ContentSha256 = sourceHash,
                FbxVersion = 7400,
            },
            RigMode = CustomModelRigMode.StaticProp,
            RigSignature = sourceHash,
            Materials = [material],
        };
        return new CustomModelPackage(
            document,
            sourceFbx.ToImmutableArray(),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(
                entry,
                texture.ToImmutableArray()));
    }
}
