using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>
/// Identifies the exact raw payloads that produced a decoded retail mesh.
/// Lengths are retained so the fingerprint contract remains explicit and
/// independently testable.
/// </summary>
public sealed record Dl1MeshGeometryProvenance(
    string LengthDelimitedSha256,
    long MetadataLength,
    long VariantLength,
    long VertexLength,
    long IndexLength);

public static class Dl1MeshGeometryFingerprint
{
    /// <summary>
    /// Hashes item 0 metadata, item 1 variants, item 3 vertices, and item 4
    /// indices in that fixed order. Each payload is prefixed by its signed
    /// little-endian Int64 byte length.
    /// </summary>
    public static Dl1MeshGeometryProvenance Create(
        ReadOnlySpan<byte> metadata,
        ReadOnlySpan<byte> variants,
        ReadOnlySpan<byte> vertices,
        ReadOnlySpan<byte> indices)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthDelimited(hash, metadata);
        AppendLengthDelimited(hash, variants);
        AppendLengthDelimited(hash, vertices);
        AppendLengthDelimited(hash, indices);
        return new Dl1MeshGeometryProvenance(
            Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant(),
            metadata.Length,
            variants.Length,
            vertices.Length,
            indices.Length);
    }

    private static void AppendLengthDelimited(
        IncrementalHash hash,
        ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(
            length,
            payload.Length);
        hash.AppendData(length);
        hash.AppendData(payload);
    }
}
