using System.Buffers.Binary;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Materials;

public sealed record Dl1TexturePreviewLimits
{
    public static Dl1TexturePreviewLimits Default { get; } = new();

    public int MaximumMetadataBytes { get; init; } = 64 * 1024;

    public int MaximumDimension { get; init; } = 8192;

    public int MaximumMipCount { get; init; } = 32;

    public int MaximumBaseMipBytes { get; init; } = 128 * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumMetadataBytes <= 0
            || MaximumDimension <= 0
            || MaximumMipCount <= 0
            || MaximumBaseMipBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Dl1TexturePreviewLimits),
                "All texture preview limits must be positive.");
        }
    }
}

/// <summary>
/// Decodes the proven DL1 PC texture header and reads only the base BC mip.
/// Other dimensions, formats, and item layouts fail closed.
/// </summary>
public static class Dl1TexturePreviewDecoder
{
    private const uint Dl1FormatDxt1 = 17;
    private const uint Dl1FormatDxt3 = 18;
    private const uint Dl1FormatDxt5 = 19;

    public static async Task<Dl1TexturePreviewData> DecodeBaseMipAsync(
        RetailAssetRecord asset,
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource,
        Rp6lChunkCache cache,
        Dl1TexturePreviewLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(cache);
        limits ??= Dl1TexturePreviewLimits.Default;
        limits.Validate();
        if (asset.Id.Namespace != RetailAssetNamespace.RpackResource
            || asset.Id.ResourceType != Rp6lResourceTypes.Texture
            || resource.ResourceType != Rp6lResourceTypes.Texture
            || !string.Equals(
                asset.DisplayName,
                resource.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The selected catalog asset does not identify this DL1 texture resource.",
                nameof(asset));
        }

        if (resource.Items.Count != 3)
        {
            throw new InvalidDataException(
                $"DL1 texture '{resource.Name}' has {resource.Items.Count} items; only the validated three-item PC layout is supported.");
        }

        Rp6lItemDescriptor metadataItem = resource.Items[0];
        Rp6lItemDescriptor mipItem = resource.Items[1];
        if (!metadataItem.HasReadableSize
            || metadataItem.SizeOrHash < 16
            || metadataItem.SizeOrHash > limits.MaximumMetadataBytes)
        {
            throw new InvalidDataException(
                $"DL1 texture '{resource.Name}' has an unsafe metadata item.");
        }

        byte[] metadata = await archive.ReadItemBytesAsync(
            metadataItem,
            cache,
            limits.MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> data = metadata;
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int arraySize = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        int mipCount =
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]) & 0x7FFF;
        uint serializedFormat =
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (width <= 0
            || height <= 0
            || width > limits.MaximumDimension
            || height > limits.MaximumDimension
            || depth != 1
            || arraySize != 1
            || mipCount <= 0
            || mipCount > limits.MaximumMipCount)
        {
            throw new InvalidDataException(
                $"DL1 texture '{resource.Name}' has unsupported dimensions or mip inventory.");
        }

        (Dl1PreviewTextureFormat format, int blockBytes) =
            serializedFormat switch
            {
                Dl1FormatDxt1 =>
                    (Dl1PreviewTextureFormat.Bc1Unorm, 8),
                Dl1FormatDxt3 =>
                    (Dl1PreviewTextureFormat.Bc2Unorm, 16),
                Dl1FormatDxt5 =>
                    (Dl1PreviewTextureFormat.Bc3Unorm, 16),
                _ => throw new InvalidDataException(
                    $"DL1 texture '{resource.Name}' uses unsupported format {serializedFormat}."),
            };
        int blockColumns = checked((width + 3) / 4);
        int blockRows = checked((height + 3) / 4);
        int rowPitch = checked(blockColumns * blockBytes);
        int baseMipBytes = checked(rowPitch * blockRows);
        if (baseMipBytes > limits.MaximumBaseMipBytes
            || !mipItem.HasReadableSize
            || mipItem.SizeOrHash < baseMipBytes)
        {
            throw new InvalidDataException(
                $"DL1 texture '{resource.Name}' cannot supply its base mip within the configured bound.");
        }

        byte[] payload =
            GC.AllocateUninitializedArray<byte>(baseMipBytes);
        await using Stream stream = await archive.OpenItemStreamAsync(
            mipItem,
            cache,
            cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        return new Dl1TexturePreviewData(
            asset.Id,
            resource.Name,
            width,
            height,
            mipCount,
            format,
            rowPitch,
            payload);
    }
}
