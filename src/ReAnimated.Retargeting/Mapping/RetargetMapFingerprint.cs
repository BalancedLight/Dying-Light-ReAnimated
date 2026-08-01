using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Retargeting.Mapping;

public static class RetargetMapFingerprint
{
    public const string Algorithm = "dlra-retarget-mapping-v3";

    public static string Compute(
        string sourceRigSignature,
        string targetRigSignature,
        string? targetAssetFingerprint,
        RetargetMap? mapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sourceRigSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            targetRigSignature);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendString(hash, Algorithm);
        AppendString(hash, sourceRigSignature);
        AppendString(hash, targetRigSignature);
        AppendString(hash, targetAssetFingerprint ?? string.Empty);
        if (mapping is null)
        {
            AppendInt32(hash, 0);
        }
        else
        {
            BoneMapEntry[] entries = mapping.Entries
                .OrderBy(static entry => entry.TargetBoneIndex)
                .ToArray();
            AppendInt32(hash, entries.Length);
            foreach (BoneMapEntry entry in entries)
            {
                AppendInt32(hash, entry.SourceBoneIndex);
                AppendInt32(hash, entry.TargetBoneIndex);
                AppendInt32(hash, (int)entry.Method);
                AppendInt64(
                    hash,
                    BitConverter.DoubleToInt64Bits(
                        entry.Confidence));
                AppendInt32(hash, entry.IsLocked ? 1 : 0);
                AppendInt32(hash, entry.IsReviewed ? 1 : 0);
                AppendInt32(hash, (int)entry.MappingKind);
                AppendInt32(hash, (int)entry.TransferPolicy);
                AppendInt32(hash, (int)entry.ComponentPolicy);
            }

            int[] targetBindReviews = mapping
                .ReviewedTargetBindBoneIndices
                .Order()
                .ToArray();
            AppendInt32(hash, targetBindReviews.Length);
            foreach (int targetBoneIndex in targetBindReviews)
            {
                AppendInt32(hash, targetBoneIndex);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void AppendString(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(
        IncrementalHash hash,
        int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(
        IncrementalHash hash,
        long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
