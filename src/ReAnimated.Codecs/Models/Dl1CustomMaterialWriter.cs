using System.Collections.Immutable;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using ReAnimated.Core.ModelAuthoring;
using StbImageSharp;

namespace ReAnimated.Codecs.Models;

internal sealed record Dl1PreparedMaterialSet(
    ImmutableArray<string> MaterialReferences,
    ImmutableDictionary<string, byte[]> Files);

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
            files.Add(
                diffuseName,
                CreateTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.BaseColor,
                    [255, 255, 255, 255],
                    CompressionFormat.Bc1,
                    cancellationToken));
            files.Add(
                normalName,
                CreateTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.Normal,
                    [128, 128, 255, 255],
                    CompressionFormat.Bc3,
                    cancellationToken));
            files.Add(
                specularName,
                CreateTexture(
                    package,
                    material,
                    CustomModelTextureSemantic.Specular,
                    [0, 0, 0, 255],
                    CompressionFormat.Bc1,
                    cancellationToken));
            files.Add(
                $"{stem}.dmt",
                Utf8WithoutBom.GetBytes(BuildMaterialSource(
                    diffuseName,
                    normalName,
                    specularName)));
        }

        return new Dl1PreparedMaterialSet(
            references.ToImmutable(),
            files.ToImmutable());
    }

    internal static string BuildMaterialSource(
        string diffuseName,
        string normalName,
        string specularName) =>
        "<MaterialData>\r\n" +
        "<TemplateData>\r\n" +
        "<template>standard</template>\r\n" +
        $"<nrm_0_tex>\"{normalName}\"</nrm_0_tex>\r\n" +
        $"<spc_0_tex>\"{specularName}\"</spc_0_tex>\r\n" +
        $"<dif_0_tex>\"{diffuseName}\"</dif_0_tex>\r\n" +
        "</TemplateData>\r\n" +
        "</MaterialData>\r\n";

    private static byte[] CreateTexture(
        CustomModelPackage package,
        CustomModelMaterial material,
        CustomModelTextureSemantic semantic,
        ReadOnlySpan<byte> fallbackColor,
        CompressionFormat fallbackFormat,
        CancellationToken cancellationToken)
    {
        CustomModelTextureBinding? binding = material.Textures.FirstOrDefault(
            texture => texture.Semantic == semantic);
        if (binding?.PackageEntryPath is { } entryPath &&
            package.TexturePayloads.TryGetValue(entryPath, out ImmutableArray<byte> payload))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.AsSpan().StartsWith("DDS "u8))
            {
                ValidateLegacyDds(payload.AsSpan(), binding.DisplayName);
                return payload.ToArray();
            }

            return EncodeImage(payload.AsSpan(), semantic, cancellationToken);
        }

        byte[] pixels = new byte[4 * 4 * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            fallbackColor.CopyTo(pixels.AsSpan(offset, 4));
        }

        return EncodeRgba(pixels, 4, 4, fallbackFormat, cancellationToken);
    }

    private static byte[] EncodeImage(
        ReadOnlySpan<byte> payload,
        CustomModelTextureSemantic semantic,
        CancellationToken cancellationToken)
    {
        ImageResult decoded;
        try
        {
            decoded = ImageResult.FromMemory(
                payload.ToArray(),
                ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"The {semantic} texture is not a supported bitmap image.",
                exception);
        }

        if (decoded.Width <= 0 ||
            decoded.Height <= 0 ||
            decoded.Width > MaximumTextureDimension ||
            decoded.Height > MaximumTextureDimension ||
            (long)decoded.Width * decoded.Height > MaximumTexturePixels ||
            decoded.Data.Length != checked(decoded.Width * decoded.Height * 4))
        {
            throw new InvalidDataException(
                $"The {semantic} texture dimensions {decoded.Width:N0}x{decoded.Height:N0} exceed the bounded DL1 authoring contract.");
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

        CompressionFormat format = semantic switch
        {
            CustomModelTextureSemantic.Normal => CompressionFormat.Bc3,
            CustomModelTextureSemantic.BaseColor when hasAlpha => CompressionFormat.Bc3,
            _ => CompressionFormat.Bc1,
        };
        return EncodeRgba(
            decoded.Data,
            decoded.Width,
            decoded.Height,
            format,
            cancellationToken);
    }

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
        ValidateLegacyDds(encoded, "generated texture");
        return encoded;
    }

    private static void ValidateLegacyDds(ReadOnlySpan<byte> bytes, string displayName)
    {
        if (bytes.Length < 128 || !bytes.StartsWith("DDS "u8))
        {
            throw new InvalidDataException(
                $"Texture '{displayName}' is not a complete DDS file.");
        }

        ReadOnlySpan<byte> fourCc = bytes.Slice(84, 4);
        if (!fourCc.SequenceEqual("DXT1"u8) &&
            !fourCc.SequenceEqual("DXT3"u8) &&
            !fourCc.SequenceEqual("DXT5"u8))
        {
            throw new InvalidDataException(
                $"Texture '{displayName}' uses an unsupported DDS encoding. DL1 model builds require legacy DXT1, DXT3, or DXT5 data.");
        }
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
}
