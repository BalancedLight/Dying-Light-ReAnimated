using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public sealed record CustomModelPreviewPayload(
    IReadOnlyList<MeshRenderData> Meshes,
    SkeletonRenderData? Skeleton,
    ImmutableArray<string> Diagnostics);

/// <summary>
/// Converts the immutable Models-workspace import into the same renderer
/// contracts used by retail DL1 assets. Texture interpretation remains a
/// neutral authoring preview; source bytes stay unchanged in the package.
/// </summary>
public static class CustomModelPreviewAdapter
{
    public static CustomModelPreviewPayload Create(
        FbxModelAuthoringImportResult imported,
        AnimationClip? clip,
        int frame,
        int? selectedBoneIndex = null)
    {
        ArgumentNullException.ThrowIfNull(imported);
        CustomModelPackage package = imported.Package;
        package.Document.Validate();

        var diagnostics = ImmutableArray.CreateBuilder<string>();
        Dictionary<Guid, CustomModelMaterial> materials = package.Document.Materials
            .ToDictionary(static material => material.Id);
        var textureCache = new Dictionary<string, TextureRenderData?>(
            StringComparer.OrdinalIgnoreCase);
        bool flipTextureCoordinateV = package.Document.BuildSettings.FlipTextureCoordinateV;
        var meshes = new MeshRenderData[imported.Surfaces.Length];
        for (int surfaceIndex = 0; surfaceIndex < imported.Surfaces.Length; surfaceIndex++)
        {
            FbxModelSurface surface = imported.Surfaces[surfaceIndex];
            MeshVertex[] vertices = surface.Vertices
                .Select(vertex => ToRenderVertex(vertex, flipTextureCoordinateV))
                .ToArray();
            Matrix4x4[] inverseBinds = surface.InverseBindMatrices
                .Select(static matrix => CorePreviewAdapter.ToSystemMatrix(matrix))
                .ToArray();
            TextureRenderData? baseColor = null;
            if (materials.TryGetValue(surface.MaterialId, out CustomModelMaterial? material))
            {
                CustomModelTextureBinding? binding = material.Textures.FirstOrDefault(
                    static texture => texture.Semantic == CustomModelTextureSemantic.BaseColor);
                if (binding is not null)
                {
                    if (!textureCache.TryGetValue(binding.ContentSha256, out baseColor))
                    {
                        baseColor = TryDecodeTexture(package, binding, diagnostics);
                        textureCache.Add(binding.ContentSha256, baseColor);
                    }
                }
            }

            meshes[surfaceIndex] = new MeshRenderData(
                surface.Id,
                vertices,
                surface.Indices.ToArray(),
                Matrix4x4.Identity,
                inverseBinds,
                surface.IsSkinned)
            {
                SkinBoneIndices = surface.PaletteBoneIndices.ToArray(),
                BaseColorTexture = baseColor,
                Tint = baseColor is null
                    ? new Vector4(0.66f, 0.69f, 0.72f, 1.0f)
                    : Vector4.One,
            };
        }

        SkeletonRenderData? skeleton = null;
        if (imported.Rig is { } rig)
        {
            SkeletonPose pose = clip is null
                ? rig.CreateBindPose()
                : clip.SamplePose(
                    rig,
                    clip.FrameRate.SecondsForFrame(
                        Math.Clamp(frame, 0, checked((int)Math.Min(int.MaxValue, clip.FrameCount - 1)))),
                    PlaybackMode.Clamp);
            skeleton = CorePreviewAdapter.ToRenderSkeleton(pose, selectedBoneIndex);
        }

        return new CustomModelPreviewPayload(
            meshes,
            skeleton,
            diagnostics.ToImmutable());
    }

    private static MeshVertex ToRenderVertex(
        FbxModelVertex vertex,
        bool flipTextureCoordinateV)
    {
        float[] weights = new float[4];
        float[] indices = new float[4];
        int count = Math.Min(
            4,
            Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length));
        for (int index = 0; index < count; index++)
        {
            indices[index] = vertex.BoneIndices[index];
            weights[index] = checked((float)vertex.BoneWeights[index]);
        }

        return new MeshVertex(
            new Vector3(
                checked((float)vertex.Position.X),
                checked((float)vertex.Position.Y),
                checked((float)vertex.Position.Z)),
            new Vector3(
                checked((float)vertex.Normal.X),
                checked((float)vertex.Normal.Y),
                checked((float)vertex.Normal.Z)),
            new Vector2(
                checked((float)vertex.TextureCoordinateU),
                checked((float)(flipTextureCoordinateV
                    ? 1.0 - vertex.TextureCoordinateV
                    : vertex.TextureCoordinateV))),
            new Vector4(weights[0], weights[1], weights[2], weights[3]),
            new Vector4(indices[0], indices[1], indices[2], indices[3]));
    }

    private static TextureRenderData? TryDecodeTexture(
        CustomModelPackage package,
        CustomModelTextureBinding binding,
        ImmutableArray<string>.Builder diagnostics)
    {
        if (binding.PackageEntryPath is not { } entryPath ||
            !package.TexturePayloads.TryGetValue(entryPath, out ImmutableArray<byte> payload))
        {
            diagnostics.Add(
                $"Material texture '{binding.DisplayName}' has no embedded or selected bytes; the mesh uses neutral shading.");
            return null;
        }

        try
        {
            if (binding.MediaType.Equals("image/vnd-ms.dds", StringComparison.OrdinalIgnoreCase) ||
                payload.AsSpan().StartsWith("DDS "u8))
            {
                return DecodeDds(binding, payload.AsSpan());
            }

            return DecodeBitmap(binding, payload);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileFormatException or
            InvalidDataException or
            NotSupportedException or
            OverflowException)
        {
            diagnostics.Add(
                $"Texture '{binding.DisplayName}' could not be decoded for preview ({exception.Message}); its original bytes remain packaged.");
            return null;
        }
    }

    private static TextureRenderData DecodeBitmap(
        CustomModelTextureBinding binding,
        ImmutableArray<byte> payload)
    {
        TextureRenderData? decoded = null;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                using var stream = new MemoryStream(payload.ToArray(), writable: false);
                BitmapDecoder decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapSource source = decoder.Frames[0];
                var converted = new FormatConvertedBitmap(
                    source,
                    PixelFormats.Bgra32,
                    null,
                    0.0);
                int rowPitch = checked(converted.PixelWidth * 4);
                byte[] pixels = new byte[checked(rowPitch * converted.PixelHeight)];
                converted.CopyPixels(pixels, rowPitch, 0);
                decoded = new TextureRenderData(
                    $"custom:{binding.Id:N}",
                    converted.PixelWidth,
                    converted.PixelHeight,
                    TextureRenderFormat.Bgra8Unorm,
                    rowPitch,
                    pixels);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "DL ReAnimated custom texture decoder",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }

        return decoded ?? throw new InvalidDataException(
            "The image decoder produced no bitmap.");
    }

    private static TextureRenderData DecodeDds(
        CustomModelTextureBinding binding,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 128 ||
            !payload[..4].SequenceEqual("DDS "u8) ||
            ReadUInt32(payload, 4) != 124 ||
            ReadUInt32(payload, 76) != 32)
        {
            throw new InvalidDataException("DDS header is invalid or truncated.");
        }

        int height = checked((int)ReadUInt32(payload, 12));
        int width = checked((int)ReadUInt32(payload, 16));
        uint fourCc = ReadUInt32(payload, 84);
        (TextureRenderFormat format, int blockBytes) = fourCc switch
        {
            0x3154_5844 => (TextureRenderFormat.Bc1Unorm, 8),  // DXT1
            0x3354_5844 => (TextureRenderFormat.Bc2Unorm, 16), // DXT3
            0x3554_5844 => (TextureRenderFormat.Bc3Unorm, 16), // DXT5
            _ => throw new NotSupportedException(
                $"DDS FourCC 0x{fourCc:X8} is not a supported BC1/BC2/BC3 authoring preview."),
        };
        if (width <= 0 || height <= 0 || width > 32_768 || height > 32_768)
        {
            throw new InvalidDataException("DDS dimensions are outside the bounded preview contract.");
        }

        int rowPitch = checked(Math.Max(1, (width + 3) / 4) * blockBytes);
        int byteCount = checked(rowPitch * Math.Max(1, (height + 3) / 4));
        if (payload.Length < 128 + byteCount)
        {
            throw new InvalidDataException("DDS base mip is truncated.");
        }

        return new TextureRenderData(
            $"custom:{binding.Id:N}",
            width,
            height,
            format,
            rowPitch,
            payload.Slice(128, byteCount).ToArray());
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
}
