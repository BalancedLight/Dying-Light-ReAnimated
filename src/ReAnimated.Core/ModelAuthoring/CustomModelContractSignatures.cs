using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Stable custom-model compatibility signatures. Unlike an imported asset
/// fingerprint, these hashes deliberately exclude source bytes and FBX object
/// ids so a re-export with the same hierarchy and binds can preserve variants.
/// </summary>
public static class CustomModelContractSignatures
{
    private const string RigAlgorithm = "dlra-custom-model-rig-contract-v2";

    public static string ComputeRig(IEnumerable<CustomModelBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        ImmutableArray<CustomModelBone> rows = bones.ToImmutableArray();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   Encoding.UTF8,
                   leaveOpen: true))
        {
            writer.Write(RigAlgorithm);
            writer.Write(rows.Length);
            foreach (CustomModelBone bone in rows)
            {
                writer.Write(bone.Index);
                writer.Write(bone.Name.Normalize(NormalizationForm.FormKC));
                writer.Write(bone.ParentIndex);
                writer.Write((int)bone.Kind);
                writer.Write(bone.IsWeighted);
                WriteTransform(writer, bone.LocalBindTransform);
                WriteMatrix(writer, bone.ExactLocalBindMatrix);
            }
        }

        return Convert.ToHexString(SHA256.HashData(
                stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
    }

    private static void WriteTransform(BinaryWriter writer, TransformTRS value)
    {
        WriteVector(writer, value.Translation);
        writer.Write(value.Rotation.X);
        writer.Write(value.Rotation.Y);
        writer.Write(value.Rotation.Z);
        writer.Write(value.Rotation.W);
        WriteVector(writer, value.Scale);
    }

    private static void WriteMatrix(BinaryWriter writer, TransformMatrix value)
    {
        writer.Write(value.M11); writer.Write(value.M12); writer.Write(value.M13); writer.Write(value.M14);
        writer.Write(value.M21); writer.Write(value.M22); writer.Write(value.M23); writer.Write(value.M24);
        writer.Write(value.M31); writer.Write(value.M32); writer.Write(value.M33); writer.Write(value.M34);
        writer.Write(value.M41); writer.Write(value.M42); writer.Write(value.M43); writer.Write(value.M44);
    }

    private static void WriteVector(BinaryWriter writer, Vector3D value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }
}
