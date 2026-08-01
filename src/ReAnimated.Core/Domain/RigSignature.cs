using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

/// <summary>
/// Produces a deterministic identity for the complete authoring contract of a
/// rig. The signature contains no retail payload bytes.
/// </summary>
public static class RigSignature
{
    public const string Algorithm = "dlra-rig-signature-v1";

    public static string Compute(RigDefinition rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendString(hash, Algorithm);
        AppendString(hash, rig.Id);
        AppendInt32(hash, rig.BoneCount);
        foreach (BoneDefinition bone in rig.Bones)
        {
            AppendInt32(hash, bone.Index);
            AppendString(hash, bone.Name);
            AppendInt32(hash, bone.ParentIndex);
            AppendInt32(hash, (int)bone.Kind);
            AppendInt32(hash, bone.RequiredForExport ? 1 : 0);
            AppendUInt32(hash, bone.DescriptorHash ?? uint.MaxValue);
            AppendString(hash, bone.SemanticRole ?? string.Empty);
            AppendTransform(hash, bone.LocalBindPose);
        }

        AppendInt32(hash, rig.MorphChannels.Length);
        foreach (MorphChannelDefinition morph in rig.MorphChannels)
        {
            AppendInt32(hash, morph.Index);
            AppendString(hash, morph.Name);
            AppendUInt32(hash, morph.DescriptorHash ?? uint.MaxValue);
            AppendString(hash, morph.SemanticRole ?? string.Empty);
            AppendDouble(hash, morph.MinimumValue);
            AppendDouble(hash, morph.MaximumValue);
        }

        AppendInt32(hash, rig.IkChains.Length);
        foreach (TwoBoneIkChainDefinition chain in rig.IkChains)
        {
            AppendString(hash, chain.Name);
            AppendInt32(hash, chain.RootBoneIndex);
            AppendInt32(hash, chain.JointBoneIndex);
            AppendInt32(hash, chain.EndBoneIndex);
        }

        if (rig.SourceAssetFingerprint is { } source)
        {
            AppendString(hash, source.RelativeResourcePath);
            AppendString(hash, source.ContentSha256);
            AppendString(hash, source.ResourceId ?? string.Empty);
        }
        else
        {
            AppendString(hash, string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendTransform(
        IncrementalHash hash,
        TransformTRS transform)
    {
        AppendDouble(hash, transform.Translation.X);
        AppendDouble(hash, transform.Translation.Y);
        AppendDouble(hash, transform.Translation.Z);
        AppendDouble(hash, transform.Rotation.X);
        AppendDouble(hash, transform.Rotation.Y);
        AppendDouble(hash, transform.Rotation.Z);
        AppendDouble(hash, transform.Rotation.W);
        AppendDouble(hash, transform.Scale.X);
        AppendDouble(hash, transform.Scale.Y);
        AppendDouble(hash, transform.Scale.Z);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value.Normalize(
            NormalizationForm.FormKC));
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendDouble(IncrementalHash hash, double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes,
            BitConverter.DoubleToInt64Bits(value));
        hash.AppendData(bytes);
    }
}
