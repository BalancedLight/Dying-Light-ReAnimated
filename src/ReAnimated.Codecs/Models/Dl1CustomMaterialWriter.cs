using System.Collections.Immutable;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

internal sealed record Dl1PreparedMaterialSet(
    ImmutableArray<string> MaterialReferences,
    ImmutableDictionary<string, byte[]> Files,
    ImmutableArray<string> Notes);

/// <summary>
/// Converts user-owned model-package textures into the source material contract
/// consumed by Techland's model compiler. Existing retail material references
/// remain references; their bytes are never copied into a project or build.
/// </summary>
internal static class Dl1CustomMaterialWriter
{
    private const int MaximumTextureDimension = 16_384;
    private const long MaximumTexturePixels = 67_108_864;

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static Dl1PreparedMaterialSet Prepare(
        CustomModelPackage package,
        string resourceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        package.Document.Validate();

        var references = ImmutableArray.CreateBuilder<string>();
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<CustomModelMaterial> materials = package.Document.Materials;
        if (materials.IsEmpty)
        {
            materials =
            [
                new CustomModelMaterial
                {
                    Name = "Default material",
                },
            ];
        }

        for (int index = 0; index < materials.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CustomModelMaterial material = materials[index];
            if (!string.IsNullOrWhiteSpace(material.ExistingDl1MaterialReference))
            {
                references.Add(SanitizeMaterialReference(
                    material.ExistingDl1MaterialReference!,
                    resourceName,
                    index));
                continue;
            }

            string stem = CreateUniqueStem(
                $"{resourceName}_{material.Name}",
                usedStems);
            references.Add($"{stem}.mat");

            string diffuseName = $"{stem}.dds";
            string normalName = $"{stem}_nrm.dds";
            string specularName = $"{stem}_shn.dds";
            string maskName = $"{stem}_msk.dds";
            if (FindTexture(material, CustomModelTextureSemantic.BaseColor) is null)
            {
                notes.Add(
                    $"Material '{material.Name}' has no BaseColor texture; DL1 output uses an opaque white 4x4 fallback.");
            }

            files.Add(
                diffuseName,
                CreateRequiredTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.BaseColor,
                    [255, 255, 255, 255],
                    CompressionFormat.Bc1,
                    cancellationToken));

            if (TryCreateTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.Normal,
                    cancellationToken) is { } normalTexture)
            {
                files.Add(normalName, normalTexture);
            }
            else
            {
                normalName = string.Empty;
            }

            if (TryCreateTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.Specular,
                    cancellationToken) is { } specularTexture)
            {
                files.Add(specularName, specularTexture);
            }
            else
            {
                specularName = string.Empty;
            }

            if (TryCreateTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.Mask,
                    cancellationToken) is { } maskTexture)
            {
                files.Add(maskName, maskTexture);
            }
            else
            {
                maskName = string.Empty;
            }

            files.Add(
                $"{stem}.dmt",
                Utf8WithoutBom.GetBytes(BuildMaterialSource(
                    diffuseName,
                    normalName,
                    specularName,
                    maskName)));
        }

        return new Dl1PreparedMaterialSet(
            references.ToImmutable(),
            files.ToImmutable(),
            notes.ToImmutable());
    }

    internal static string BuildMaterialSource(
        string diffuseName,
        string normalName,
        string specularName,
        string maskName = "") =>
        "<MaterialData>\r\n" +
        "<TemplateData>\r\n" +
        "<template>standard</template>\r\n" +
        $"<nrm_0_tex>\"{normalName}\"</nrm_0_tex>\r\n" +
        $"<spc_0_tex>\"{specularName}\"</spc_0_tex>\r\n" +
        $"<msk_0_tex>\"{maskName}\"</msk_0_tex>\r\n" +
        $"<dif_0_tex>\"{diffuseName}\"</dif_0_tex>\r\n" +
        "</TemplateData>\r\n" +
        "</MaterialData>\r\n";

    private static byte[] CreateRequiredTexture(
        CustomModelPackage package,
        CustomModelMaterial material,
        CustomModelTextureSemantic semantic,
        ReadOnlySpan<byte> fallbackColor,
        CompressionFormat fallbackFormat,
        CancellationToken cancellationToken)
    {
        CustomModelTextureBinding? binding = FindTexture(material, semantic);
        if (binding is not null)
        {
            return CreateTexture(package, binding, cancellationToken);
        }

        byte[] pixels = new byte[4 * 4 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            fallbackColor.CopyTo(pixels.AsSpan(offset, 4));
        }

        return EncodeRgba(pixels, 4, 4, fallbackFormat, cancellationToken);
    }

    private static byte[]? TryCreateTexture(
        CustomModelPackage package,
        CustomModelMaterial material,
        CustomModelTextureSemantic semantic,
        CancellationToken cancellationToken)
    {
        CustomModelTextureBinding? binding = FindTexture(material, semantic);
        return binding is null ? null : CreateTexture(package, binding, cancellationToken);
    }

    private static CustomModelTextureBinding? FindTexture(
        CustomModelMaterial material,
        CustomModelTextureSemantic semantic) =>
        material.Textures.FirstOrDefault(texture => texture.Semantic == semantic);

    private static byte[] CreateTexture(
        CustomModelPackage package,
        CustomModelTextureBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.PackageEntryPath is not { } entryPath ||
            !package.TexturePayloads.TryGetValue(entryPath, out ImmutableArray<byte> payload))
        {
            throw new InvalidDataException(
                $"Texture '{binding.DisplayName}' is declared by the model but has no package payload.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (CustomModelTextureDecoder.IsLegacyDdsPassthroughEncoding(payload.AsSpan()))
        {
            CustomModelLegacyDdsInfo dds =
                CustomModelTextureDecoder.ReadLegacyDdsPassthrough(
                    payload.AsSpan(),
                    binding.DisplayName);
            if (binding.Semantic != CustomModelTextureSemantic.Normal ||
                binding.NormalMapConvention == CustomModelNormalMapConvention.Dl1AlphaGreen)
            {
                if (binding.Semantic == CustomModelTextureSemantic.Normal &&
                    binding.NormalMapConvention == CustomModelNormalMapConvention.Dl1AlphaGreen &&
                    !dds.HasAlphaChannel)
                {
                    throw new InvalidDataException(
                        $"Texture '{binding.DisplayName}' uses DXT1 for a DL1 alpha/green normal map. " +
                        "DXT3 or DXT5 data is required to preserve tangent X in the alpha channel.");
                }

                return payload.ToArray();
            }
        }

        return EncodeImage(payload.AsSpan(), binding, cancellationToken);
    }

    private static byte[] EncodeImage(
        ReadOnlySpan<byte> payload,
        CustomModelTextureBinding binding,
        CancellationToken cancellationToken)
    {
        if (!CustomModelTextureDecoder.TryDecode(
                payload,
                binding.DisplayName,
                out CustomModelDecodedTexture? decoded,
                out string failureReason,
                cancellationToken) ||
            decoded is null)
        {
            throw new InvalidDataException(failureReason);
        }

        return EncodeDecodedImage(
            new DecodedTexture(decoded.Width, decoded.Height, decoded.Rgba8),
            binding,
            cancellationToken);
    }

    private static byte[] EncodeDecodedImage(
        DecodedTexture decoded,
        CustomModelTextureBinding binding,
        CancellationToken cancellationToken)
    {
        if (decoded.Width <= 0 ||
            decoded.Height <= 0 ||
            decoded.Width > MaximumTextureDimension ||
            decoded.Height > MaximumTextureDimension ||
            (long)decoded.Width * decoded.Height > MaximumTexturePixels ||
            decoded.Data.Length != checked(decoded.Width * decoded.Height * 4))
        {
            throw new InvalidDataException(
                $"The {binding.Semantic} texture dimensions {decoded.Width:N0}x{decoded.Height:N0} exceed the bounded DL1 authoring contract.");
        }

        byte[] outputPixels = decoded.Data;
        if (binding.Semantic == CustomModelTextureSemantic.Normal)
        {
            outputPixels = RepackNormalForDl1(
                decoded.Data,
                binding.NormalMapConvention);
        }

        bool hasAlpha = false;
        for (int offset = 3; offset < decoded.Data.Length; offset += 4)
        {
            if (decoded.Data[offset] != byte.MaxValue)
            {
                hasAlpha = true;
                break;
            }
        }

        CompressionFormat format = binding.Semantic switch
        {
            CustomModelTextureSemantic.Normal => CompressionFormat.Bc3,
            CustomModelTextureSemantic.BaseColor when hasAlpha => CompressionFormat.Bc3,
            _ => CompressionFormat.Bc1,
        };
        return EncodeRgba(
            outputPixels,
            decoded.Width,
            decoded.Height,
            format,
            cancellationToken);
    }

    internal static byte[] RepackNormalForDl1(
        ReadOnlySpan<byte> rgba,
        CustomModelNormalMapConvention convention)
    {
        if (rgba.Length == 0 || rgba.Length % 4 != 0)
        {
            throw new ArgumentException("Normal-map pixels must be non-empty RGBA32 data.", nameof(rgba));
        }

        if (!Enum.IsDefined(convention))
        {
            throw new ArgumentOutOfRangeException(
                nameof(convention),
                convention,
                "The normal-map convention is not supported.");
        }

        if (convention == CustomModelNormalMapConvention.Dl1AlphaGreen)
        {
            return rgba.ToArray();
        }

        byte[] packed = new byte[rgba.Length];
        for (int offset = 0; offset < rgba.Length; offset += 4)
        {
            double x = rgba[offset] / 127.5 - 1.0;
            double y = rgba[offset + 1] / 127.5 - 1.0;
            if (convention == CustomModelNormalMapConvention.RgbDirectX)
            {
                y = -y;
            }

            double lengthSquared = x * x + y * y;
            if (lengthSquared > 1.0)
            {
                double inverseLength = 1.0 / Math.Sqrt(lengthSquared);
                x *= inverseLength;
                y *= inverseLength;
            }

            packed[offset] = 0;
            packed[offset + 1] = EncodeSignedUnit(y);
            packed[offset + 2] = byte.MaxValue;
            packed[offset + 3] = EncodeSignedUnit(x);
        }

        return packed;
    }

    private static byte EncodeSignedUnit(double value) =>
        (byte)Math.Clamp((int)Math.Round((value * 0.5 + 0.5) * 255.0), 0, 255);

    private static byte[] EncodeRgba(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        CompressionFormat format,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoder = new BcEncoder();
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.Format = format;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.DdsPreferDxt10Header = false;
        encoder.OutputOptions.DdsBc1WriteAlphaFlag = format == CompressionFormat.Bc1WithAlpha;
        using var output = new MemoryStream();
        encoder.EncodeToStream(
            pixels,
            width,
            height,
            PixelFormat.Rgba32,
            output);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] encoded = output.ToArray();
        _ = CustomModelTextureDecoder.ReadLegacyDdsPassthrough(
            encoded,
            "generated texture");
        return encoded;
    }

    private static string CreateUniqueStem(string source, HashSet<string> used)
    {
        string stem = Dl1SourceModelWriter.SanitizeName(source, 55);
        if (used.Add(stem))
        {
            return stem;
        }

        for (int suffix = 2; ; suffix++)
        {
            string suffixText = $"_{suffix}";
            string candidate = Dl1SourceModelWriter.SanitizeName(
                stem[..Math.Min(stem.Length, 55 - suffixText.Length)] + suffixText,
                55);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeMaterialReference(
        string source,
        string resourceName,
        int index)
    {
        string fileName = Path.GetFileName(source.Replace('\\', '/'));
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{Dl1SourceModelWriter.SanitizeName(
            string.IsNullOrWhiteSpace(stem)
                ? $"{resourceName}_material_{index:00}"
                : stem,
            55)}.mat";
    }

    private sealed record DecodedTexture(int Width, int Height, byte[] Data);

}
