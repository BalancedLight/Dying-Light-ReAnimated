using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// One named object and its editor-menu bind transform. Object order is part
/// of the CHR contract and must match the compiled model's object table.
/// </summary>
public sealed record Dl1ChrV4ObjectTransform(
    string Name,
    TransformMatrix LocalTransform);

public sealed record Dl1ChrV4Variant(
    string Name,
    Vector3D Scale,
    ImmutableArray<TransformMatrix> ObjectTransforms);

public sealed record Dl1ChrV4Document(
    ImmutableArray<string> ObjectNames,
    ImmutableArray<Dl1ChrV4Variant> Variants);

public sealed record Dl1ChrV4WriteResult(
    string OutputPath,
    string OutputSha256,
    int ObjectCount,
    ImmutableArray<string> VariantNames);

/// <summary>
/// Bounded reader/writer for the DL1 structured CHR v4 layout used by the
/// editor's character/menu model path. Matrices use the ReAnimated column-
/// vector convention and serialize as the format's twelve row-major values.
/// </summary>
public static class Dl1ChrV4Codec
{
    public const ushort Version = 4;
    public const int MaximumObjectCount = 4_096;
    public const int MaximumVariantCount = 1_024;
    public const int MaximumNameBytes = 4_096;
    public const int MaximumPayloadBytes = 256 * 1024 * 1024;

    private static readonly Encoding Ascii = Encoding.ASCII;

    /// <summary>
    /// Creates the narrow first-release contract: one default variant whose
    /// transform table contains every model object in exact compiled order.
    /// </summary>
    public static Dl1ChrV4Document CreateEditorMenuOneDefaultVariant(
        IEnumerable<Dl1ChrV4ObjectTransform> orderedObjects,
        string variantName = "default")
    {
        ArgumentNullException.ThrowIfNull(orderedObjects);
        Dl1ChrV4ObjectTransform[] objects = orderedObjects.ToArray();
        if (objects.Length == 0)
        {
            throw new ArgumentException(
                "An editor-menu CHR requires at least one ordered model object.",
                nameof(orderedObjects));
        }

        var document = new Dl1ChrV4Document(
            objects.Select(static item => item.Name).ToImmutableArray(),
            [
                new Dl1ChrV4Variant(
                    variantName,
                    Vector3D.One,
                    objects.Select(static item => item.LocalTransform).ToImmutableArray()),
            ]);
        Validate(document);
        return document;
    }

    public static byte[] Build(Dl1ChrV4Document document)
    {
        Validate(document);
        long estimatedLength = checked(
            10L +
            document.ObjectNames.Sum(static name => 2L + Encoding.ASCII.GetByteCount(name)) +
            document.Variants.Sum(variant =>
                2L + Encoding.ASCII.GetByteCount(variant.Name) + 12L +
                (48L * variant.ObjectTransforms.Length)));
        if (estimatedLength > MaximumPayloadBytes || estimatedLength > int.MaxValue)
        {
            throw new InvalidOperationException(
                "The CHR v4 document exceeds the bounded output size.");
        }

        using var output = new MemoryStream(checked((int)estimatedLength));
        using var writer = new BinaryWriter(output, Ascii, leaveOpen: true);
        writer.Write(Version);
        writer.Write(checked((ushort)document.ObjectNames.Length));
        writer.Write((ushort)0);
        foreach (string name in document.ObjectNames)
        {
            WriteName(writer, name);
        }

        writer.Write(0);
        foreach (Dl1ChrV4Variant variant in document.Variants)
        {
            WriteName(writer, variant.Name);
            WriteSingle(writer, variant.Scale.X, $"variant '{variant.Name}' scale X");
            WriteSingle(writer, variant.Scale.Y, $"variant '{variant.Name}' scale Y");
            WriteSingle(writer, variant.Scale.Z, $"variant '{variant.Name}' scale Z");
            foreach (TransformMatrix transform in variant.ObjectTransforms)
            {
                WriteMatrix3X4(writer, transform);
            }
        }

        writer.Flush();
        byte[] payload = output.ToArray();
        _ = Parse(payload);
        return payload;
    }

    public static Dl1ChrV4Document Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                "The CHR v4 payload exceeds the bounded input size.");
        }

        if (payload.Length < 10)
        {
            throw new InvalidDataException("The CHR v4 payload is truncated.");
        }

        var offset = 0;
        ushort version = ReadUInt16(payload, ref offset);
        if (version != Version)
        {
            throw new InvalidDataException(
                $"Unsupported CHR version {version}; DL1 structured CHR version {Version} is required.");
        }

        int objectCount = ReadUInt16(payload, ref offset);
        if (objectCount is <= 0 or > MaximumObjectCount)
        {
            throw new InvalidDataException(
                $"CHR v4 declares an unsafe object count of {objectCount:N0}.");
        }

        ushort reserved = ReadUInt16(payload, ref offset);
        if (reserved != 0)
        {
            throw new InvalidDataException(
                $"CHR v4 has a non-zero reserved header value 0x{reserved:X4}.");
        }

        var objectNames = ImmutableArray.CreateBuilder<string>(objectCount);
        for (var index = 0; index < objectCount; index++)
        {
            objectNames.Add(ReadName(payload, ref offset, $"object {index}"));
        }

        int tableMarker = ReadInt32(payload, ref offset);
        if (tableMarker != 0)
        {
            throw new InvalidDataException(
                $"CHR v4 has an unsupported object-table marker {tableMarker}.");
        }

        var variants = ImmutableArray.CreateBuilder<Dl1ChrV4Variant>();
        while (offset < payload.Length)
        {
            if (variants.Count >= MaximumVariantCount)
            {
                throw new InvalidDataException(
                    "CHR v4 exceeds the bounded variant count.");
            }

            string name = ReadName(payload, ref offset, $"variant {variants.Count}");
            Vector3D scale = new(
                ReadSingle(payload, ref offset),
                ReadSingle(payload, ref offset),
                ReadSingle(payload, ref offset));
            int transformBytes = checked(objectCount * 48);
            Require(payload, offset, transformBytes, $"variant '{name}' transform table");
            var transforms = ImmutableArray.CreateBuilder<TransformMatrix>(objectCount);
            for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
            {
                transforms.Add(ReadMatrix3X4(payload, ref offset));
            }

            variants.Add(new Dl1ChrV4Variant(name, scale, transforms.MoveToImmutable()));
        }

        var document = new Dl1ChrV4Document(
            objectNames.MoveToImmutable(),
            variants.ToImmutable());
        try
        {
            Validate(document);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException(
                "CHR v4 failed its structural validation.",
                exception);
        }

        return document;
    }

    public static async Task<Dl1ChrV4WriteResult> WriteEditorMenuOneDefaultVariantAtomicAsync(
        string path,
        IEnumerable<Dl1ChrV4ObjectTransform> orderedObjects,
        string variantName = "default",
        CancellationToken cancellationToken = default) =>
        await WriteAtomicAsync(
            path,
            CreateEditorMenuOneDefaultVariant(orderedObjects, variantName),
            cancellationToken).ConfigureAwait(false);

    public static async Task<Dl1ChrV4WriteResult> WriteAtomicAsync(
        string path,
        Dl1ChrV4Document document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] payload = Build(document);
        string outputPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(outputPath), ".chr", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".chr";
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("CHR output path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            byte[] reopenedBytes = await File.ReadAllBytesAsync(
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            Dl1ChrV4Document reopened = Parse(reopenedBytes);
            EnsureEquivalent(document, reopened);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new Dl1ChrV4WriteResult(
            outputPath,
            Sha256(payload),
            document.ObjectNames.Length,
            document.Variants.Select(static variant => variant.Name).ToImmutableArray());
    }

    private static void Validate(Dl1ChrV4Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.ObjectNames.IsDefaultOrEmpty ||
            document.ObjectNames.Length > MaximumObjectCount)
        {
            throw new ArgumentException(
                $"CHR v4 requires between 1 and {MaximumObjectCount:N0} object names.",
                nameof(document));
        }

        if (document.Variants.IsDefaultOrEmpty ||
            document.Variants.Length > MaximumVariantCount)
        {
            throw new ArgumentException(
                $"CHR v4 requires between 1 and {MaximumVariantCount:N0} variants.",
                nameof(document));
        }

        var objectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in document.ObjectNames)
        {
            ValidateName(name, "object");
            if (!objectNames.Add(name))
            {
                throw new ArgumentException(
                    $"CHR v4 object name '{name}' is duplicated.",
                    nameof(document));
            }
        }

        var variantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Dl1ChrV4Variant variant in document.Variants)
        {
            ArgumentNullException.ThrowIfNull(variant);
            ValidateName(variant.Name, "variant");
            if (!variantNames.Add(variant.Name))
            {
                throw new ArgumentException(
                    $"CHR v4 variant name '{variant.Name}' is duplicated.",
                    nameof(document));
            }

            if (!variant.Scale.IsFinite ||
                Math.Abs(variant.Scale.X) <= 1e-12 ||
                Math.Abs(variant.Scale.Y) <= 1e-12 ||
                Math.Abs(variant.Scale.Z) <= 1e-12)
            {
                throw new ArgumentException(
                    $"CHR v4 variant '{variant.Name}' has an invalid scale.",
                    nameof(document));
            }

            if (variant.ObjectTransforms.IsDefault ||
                variant.ObjectTransforms.Length != document.ObjectNames.Length)
            {
                throw new ArgumentException(
                    $"CHR v4 variant '{variant.Name}' does not have exactly one transform per object.",
                    nameof(document));
            }

            foreach (TransformMatrix transform in variant.ObjectTransforms)
            {
                ValidateMatrix(transform, variant.Name);
            }
        }
    }

    private static void ValidateName(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length != name.Trim().Length ||
            name.Any(static character => character is < ' ' or > '~') ||
            Ascii.GetByteCount(name) > MaximumNameBytes)
        {
            throw new ArgumentException(
                $"CHR v4 {kind} name '{name}' is not bounded printable ASCII.");
        }
    }

    private static void ValidateMatrix(TransformMatrix matrix, string variantName)
    {
        if (!matrix.IsFinite ||
            Math.Abs(matrix.M41) > 1e-9 ||
            Math.Abs(matrix.M42) > 1e-9 ||
            Math.Abs(matrix.M43) > 1e-9 ||
            Math.Abs(matrix.M44 - 1.0) > 1e-9)
        {
            throw new ArgumentException(
                $"CHR v4 variant '{variantName}' contains a non-affine or non-finite transform.");
        }
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        byte[] bytes = Ascii.GetBytes(name);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private static void WriteMatrix3X4(BinaryWriter writer, TransformMatrix matrix)
    {
        ValidateMatrix(matrix, "writer input");
        WriteSingle(writer, matrix.M11, "matrix M11");
        WriteSingle(writer, matrix.M12, "matrix M12");
        WriteSingle(writer, matrix.M13, "matrix M13");
        WriteSingle(writer, matrix.M14, "matrix M14");
        WriteSingle(writer, matrix.M21, "matrix M21");
        WriteSingle(writer, matrix.M22, "matrix M22");
        WriteSingle(writer, matrix.M23, "matrix M23");
        WriteSingle(writer, matrix.M24, "matrix M24");
        WriteSingle(writer, matrix.M31, "matrix M31");
        WriteSingle(writer, matrix.M32, "matrix M32");
        WriteSingle(writer, matrix.M33, "matrix M33");
        WriteSingle(writer, matrix.M34, "matrix M34");
    }

    private static void WriteSingle(BinaryWriter writer, double value, string label)
    {
        float encoded = (float)value;
        if (!float.IsFinite(encoded))
        {
            throw new InvalidOperationException(
                $"CHR v4 {label} cannot be represented as a finite 32-bit float.");
        }

        writer.Write(encoded);
    }

    private static string ReadName(ReadOnlySpan<byte> payload, ref int offset, string label)
    {
        int length = ReadUInt16(payload, ref offset);
        if (length <= 0 || length > MaximumNameBytes)
        {
            throw new InvalidDataException(
                $"CHR v4 {label} has an invalid name length of {length:N0} bytes.");
        }

        Require(payload, offset, length, $"{label} name");
        ReadOnlySpan<byte> encoded = payload.Slice(offset, length);
        if (encoded.ContainsAnyExceptInRange((byte)' ', (byte)'~'))
        {
            throw new InvalidDataException(
                $"CHR v4 {label} is not printable ASCII.");
        }

        string result = Ascii.GetString(encoded);
        offset += length;
        return result;
    }

    private static TransformMatrix ReadMatrix3X4(ReadOnlySpan<byte> payload, ref int offset) =>
        new(
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            ReadSingle(payload, ref offset),
            0.0,
            0.0,
            0.0,
            1.0);

    private static ushort ReadUInt16(ReadOnlySpan<byte> payload, ref int offset)
    {
        Require(payload, offset, sizeof(ushort), "16-bit value");
        ushort result = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += sizeof(ushort);
        return result;
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, ref int offset)
    {
        Require(payload, offset, sizeof(int), "32-bit value");
        int result = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
        offset += sizeof(int);
        return result;
    }

    private static float ReadSingle(ReadOnlySpan<byte> payload, ref int offset)
    {
        Require(payload, offset, sizeof(float), "32-bit float");
        float result = BinaryPrimitives.ReadSingleLittleEndian(payload[offset..]);
        offset += sizeof(float);
        if (!float.IsFinite(result))
        {
            throw new InvalidDataException("CHR v4 contains a non-finite floating-point value.");
        }

        return result;
    }

    private static void Require(
        ReadOnlySpan<byte> payload,
        int offset,
        int count,
        string label)
    {
        if (offset < 0 || count < 0 || offset > payload.Length - count)
        {
            throw new InvalidDataException($"CHR v4 is truncated while reading {label}.");
        }
    }

    private static void EnsureEquivalent(
        Dl1ChrV4Document expected,
        Dl1ChrV4Document actual)
    {
        if (!expected.ObjectNames.SequenceEqual(actual.ObjectNames, StringComparer.Ordinal) ||
            expected.Variants.Length != actual.Variants.Length)
        {
            throw new InvalidDataException(
                "Reopened CHR v4 object or variant identities differ from the staged document.");
        }

        for (var variantIndex = 0; variantIndex < expected.Variants.Length; variantIndex++)
        {
            Dl1ChrV4Variant left = expected.Variants[variantIndex];
            Dl1ChrV4Variant right = actual.Variants[variantIndex];
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !NearlyEquals(left.Scale, right.Scale, 1e-5) ||
                left.ObjectTransforms.Length != right.ObjectTransforms.Length)
            {
                throw new InvalidDataException(
                    "Reopened CHR v4 variant metadata differs from the staged document.");
            }

            for (var objectIndex = 0; objectIndex < left.ObjectTransforms.Length; objectIndex++)
            {
                if (!left.ObjectTransforms[objectIndex].NearlyEquals(
                        right.ObjectTransforms[objectIndex],
                        1e-5))
                {
                    throw new InvalidDataException(
                        $"Reopened CHR v4 variant '{left.Name}' object {objectIndex:N0} transform differs from the staged document.");
                }
            }
        }
    }

    private static bool NearlyEquals(Vector3D left, Vector3D right, double tolerance) =>
        Math.Abs(left.X - right.X) <= tolerance &&
        Math.Abs(left.Y - right.Y) <= tolerance &&
        Math.Abs(left.Z - right.Z) <= tolerance;

    private static string Sha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
}
