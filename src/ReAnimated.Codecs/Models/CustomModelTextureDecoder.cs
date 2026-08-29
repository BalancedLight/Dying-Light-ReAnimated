using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using StbImageSharp;

namespace ReAnimated.Codecs.Models;

public sealed record CustomModelDecodedTexture(
    int Width,
    int Height,
    byte[] Rgba8,
    string MediaType);

public readonly record struct CustomModelLegacyDdsInfo(
    int Width,
    int Height,
    int BaseMipByteCount,
    CompressionFormat Format,
    bool HasAlphaChannel);

public static class CustomModelTextureDecoder
{
    private const int LegacyDdsHeaderLength = 128;
    private const int MaximumTextureDimension = 16_384;
    private const long MaximumTexturePixels = 67_108_864;

    public static ImmutableArray<string> SupportedExtensions { get; } =
        [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds"];

    public static bool TryDecode(
        ReadOnlySpan<byte> payload,
        string displayName,
        out CustomModelDecodedTexture? decoded,
        out string failureReason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            decoded = payload.StartsWith("DDS "u8)
                ? DecodeDds(payload, cancellationToken)
                : DecodeBitmap(payload);
            ValidateDimensions(decoded, displayName);
            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
            InvalidOperationException or IndexOutOfRangeException or
            NotSupportedException or OverflowException)
        {
            decoded = null;
            failureReason = $"Texture '{displayName}' could not be decoded: {exception.Message}";
            return false;
        }
    }

    public static bool TryReadLegacyDdsPassthrough(
        ReadOnlySpan<byte> payload,
        string displayName,
        out CustomModelLegacyDdsInfo info,
        out string failureReason)
    {
        try
        {
            info = ReadLegacyDdsPassthrough(payload, displayName);
            failureReason = string.Empty;
            return true;
        }
        catch (InvalidDataException exception)
        {
            info = default;
            failureReason = exception.Message;
            return false;
        }
    }

    internal static bool IsLegacyDdsPassthroughEncoding(ReadOnlySpan<byte> payload) =>
        payload.Length >= 88 &&
        payload.StartsWith("DDS "u8) &&
        (payload.Slice(84, 4).SequenceEqual("DXT1"u8) ||
         payload.Slice(84, 4).SequenceEqual("DXT3"u8) ||
         payload.Slice(84, 4).SequenceEqual("DXT5"u8));

    internal static CustomModelLegacyDdsInfo ReadLegacyDdsPassthrough(
        ReadOnlySpan<byte> bytes,
        string displayName)
    {
        if (bytes.Length < LegacyDdsHeaderLength || !bytes.StartsWith("DDS "u8))
        {
            throw new InvalidDataException($"Texture '{displayName}' is not a complete DDS file.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != 124 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(76, 4)) != 32)
        {
            throw new InvalidDataException($"Texture '{displayName}' has an invalid legacy DDS header.");
        }

        uint heightValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(12, 4));
        uint widthValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(16, 4));
        ValidateDimensions(widthValue, heightValue, displayName, "DDS");
        ReadOnlySpan<byte> fourCc = bytes.Slice(84, 4);
        (CompressionFormat format, int blockBytes, bool hasAlphaChannel) = fourCc switch
        {
            _ when fourCc.SequenceEqual("DXT1"u8) => (CompressionFormat.Bc1, 8, false),
            _ when fourCc.SequenceEqual("DXT3"u8) => (CompressionFormat.Bc2, 16, true),
            _ when fourCc.SequenceEqual("DXT5"u8) => (CompressionFormat.Bc3, 16, true),
            _ => throw new InvalidDataException(
                $"Texture '{displayName}' is DDS but not a legacy DXT1, DXT3, or DXT5 passthrough."),
        };
        int width = checked((int)widthValue);
        int height = checked((int)heightValue);
        long byteCount = checked(((long)width + 3) / 4 * (((long)height + 3) / 4) * blockBytes);
        if (bytes.Length < LegacyDdsHeaderLength + byteCount)
        {
            throw new InvalidDataException(
                $"Texture '{displayName}' has a truncated DDS base mip. Its {width:N0}x{height:N0} " +
                $"{Encoding.ASCII.GetString(fourCc)} image requires at least {byteCount:N0} payload bytes.");
        }

        return new CustomModelLegacyDdsInfo(
            width,
            height,
            checked((int)byteCount),
            format,
            hasAlphaChannel);
    }

    private static CustomModelDecodedTexture DecodeDds(
        ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        var pixels = new BcDecoder()
            .Decode2DAsync(stream, cancellationToken)
            .GetAwaiter()
            .GetResult();
        cancellationToken.ThrowIfCancellationRequested();
        int width = pixels.Width;
        int height = pixels.Height;
        byte[] rgba = new byte[checked(width * height * 4)];
        var span = pixels.Span;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ColorRgba32 pixel = span.DangerousGetReferenceAt(y, x);
                int offset = checked((y * width + x) * 4);
                rgba[offset] = pixel.r;
                rgba[offset + 1] = pixel.g;
                rgba[offset + 2] = pixel.b;
                rgba[offset + 3] = pixel.a;
            }
        }

        return new CustomModelDecodedTexture(width, height, rgba, "image/vnd-ms.dds");
    }

    private static CustomModelDecodedTexture DecodeBitmap(ReadOnlySpan<byte> payload)
    {
        ImageResult image = ImageResult.FromMemory(
            payload.ToArray(),
            ColorComponents.RedGreenBlueAlpha);
        return new CustomModelDecodedTexture(
            image.Width,
            image.Height,
            image.Data,
            DetectBitmapMediaType(payload));
    }

    private static string DetectBitmapMediaType(ReadOnlySpan<byte> payload)
    {
        if (payload.StartsWith("\x89PNG\r\n\x1a\n"u8)) return "image/png";
        if (payload.Length >= 3 &&
            payload[0] == 0xff && payload[1] == 0xd8 && payload[2] == 0xff)
        {
            return "image/jpeg";
        }
        if (payload.StartsWith("BM"u8)) return "image/bmp";
        return "image/x-tga";
    }

    private static void ValidateDimensions(
        CustomModelDecodedTexture decoded,
        string displayName)
    {
        ValidateDimensions(
            checked((uint)decoded.Width),
            checked((uint)decoded.Height),
            displayName,
            "image");
        if (decoded.Rgba8.Length != checked(decoded.Width * decoded.Height * 4))
        {
            throw new InvalidDataException(
                $"Texture '{displayName}' decoded to an incomplete RGBA image.");
        }
    }

    private static void ValidateDimensions(
        uint width,
        uint height,
        string displayName,
        string kind)
    {
        if (width == 0 || height == 0 ||
            width > MaximumTextureDimension || height > MaximumTextureDimension ||
            (long)width * height > MaximumTexturePixels)
        {
            throw new InvalidDataException(
                $"Texture '{displayName}' has {kind} dimensions {width:N0}x{height:N0} outside the bounded authoring contract.");
        }
    }
}
