using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

/// <summary>
/// Produces a mesh- and asset-independent identity for the nodes that can
/// receive skeletal animation. Runtime rig identity remains the stronger
/// <see cref="RigSignature"/> contract.
/// </summary>
public static class AnimationSkeletonSignature
{
    public const string Algorithm = "dlra-animation-skeleton-v1";

    public static string Compute(RigDefinition rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendString(hash, Algorithm);
        AppendInt32(hash, rig.BoneCount);
        foreach (BoneDefinition bone in rig.Bones)
        {
            AppendInt32(hash, bone.Index);
            AppendString(hash, bone.Name);
            AppendInt32(hash, bone.ParentIndex);
            AppendInt32(hash, (int)bone.Kind);
            AppendInt32(hash, bone.RequiredForExport ? 1 : 0);
            AppendUInt32(hash, bone.DescriptorHash ?? uint.MaxValue);
            AppendTransform(hash, bone.LocalBindPose);
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
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

public enum DirectBoneIdentityEvidence
{
    UniqueDescriptor,
    ExactNormalizedName,
}

/// <summary>
/// One deterministic, non-retargeted source-to-target node identity.
/// Compatible direct playback permits different physical indexes, but never
/// fuzzy semantics, anatomical transfer, fan-out, or bind correction.
/// </summary>
public sealed record DirectBoneBinding
{
    public int SourceBoneIndex { get; init; }

    public int TargetBoneIndex { get; init; }

    public string SourceBoneName { get; init; } = string.Empty;

    public string TargetBoneName { get; init; } = string.Empty;

    public DirectBoneIdentityEvidence IdentityEvidence { get; init; }

    public bool ParentTopologyMatches { get; init; }

    public bool LocalBindMatches { get; init; }

    public bool GlobalBindMatches { get; init; }
}

/// <summary>
/// A reviewed-free direct animation binding. Its rows are generated only from
/// deterministic node identity, hierarchy, and bind evidence.
/// </summary>
public sealed class DirectRigBinding
{
    public const string PolicyVersion = "dlra-direct-rig-binding-v1";

    [JsonConstructor]
    public DirectRigBinding(
        string sourceSkeletonSignature,
        string targetSkeletonSignature,
        string evidenceFingerprint,
        string policy,
        ImmutableArray<DirectBoneBinding> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sourceSkeletonSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            targetSkeletonSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        if (rows.IsDefault)
        {
            throw new ArgumentException(
                "Direct binding rows must be initialized.",
                nameof(rows));
        }

        SourceSkeletonSignature = sourceSkeletonSignature;
        TargetSkeletonSignature = targetSkeletonSignature;
        EvidenceFingerprint = evidenceFingerprint;
        Policy = policy;
        Rows = rows;
        ValidateShape();
    }

    public DirectRigBinding(
        string sourceSkeletonSignature,
        string targetSkeletonSignature,
        string evidenceFingerprint,
        string policy,
        IEnumerable<DirectBoneBinding> rows)
        : this(
            sourceSkeletonSignature,
            targetSkeletonSignature,
            evidenceFingerprint,
            policy,
            rows?.ToImmutableArray() ??
                throw new ArgumentNullException(nameof(rows)))
    {
    }

    public string SourceSkeletonSignature { get; }

    public string TargetSkeletonSignature { get; }

    public string EvidenceFingerprint { get; }

    public string Policy { get; }

    public ImmutableArray<DirectBoneBinding> Rows { get; }

    public void ValidateFor(
        RigDefinition sourceRig,
        RigDefinition targetRig)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(targetRig);
        ValidateShape();
        if (!string.Equals(
                SourceSkeletonSignature,
                AnimationSkeletonSignature.Compute(sourceRig),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                TargetSkeletonSignature,
                AnimationSkeletonSignature.Compute(targetRig),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The direct binding skeleton signatures do not match the evaluated rigs.");
        }

        foreach (DirectBoneBinding row in Rows)
        {
            if ((uint)row.SourceBoneIndex >= (uint)sourceRig.BoneCount ||
                (uint)row.TargetBoneIndex >= (uint)targetRig.BoneCount ||
                !string.Equals(
                    row.SourceBoneName,
                    sourceRig.Bones[row.SourceBoneIndex].Name,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    row.TargetBoneName,
                    targetRig.Bones[row.TargetBoneIndex].Name,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A direct binding row no longer matches its source or target node identity.");
            }
        }

        string actual = DirectRigBindingFingerprint.Compute(
            SourceSkeletonSignature,
            TargetSkeletonSignature,
            Policy,
            Rows);
        if (!string.Equals(
                actual,
                EvidenceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The direct binding evidence fingerprint is stale.");
        }
    }

    private void ValidateShape()
    {
        ValidateSha256(SourceSkeletonSignature, nameof(SourceSkeletonSignature));
        ValidateSha256(TargetSkeletonSignature, nameof(TargetSkeletonSignature));
        ValidateSha256(EvidenceFingerprint, nameof(EvidenceFingerprint));
        if (!string.Equals(Policy, PolicyVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported direct-binding policy '{Policy}'.",
                nameof(Policy));
        }

        if (Rows.Any(static row =>
                row.SourceBoneIndex < 0 ||
                row.TargetBoneIndex < 0 ||
                string.IsNullOrWhiteSpace(row.SourceBoneName) ||
                string.IsNullOrWhiteSpace(row.TargetBoneName) ||
                !Enum.IsDefined(row.IdentityEvidence) ||
                !row.ParentTopologyMatches ||
                !row.LocalBindMatches ||
                !row.GlobalBindMatches) ||
            Rows.Select(static row => row.SourceBoneIndex)
                .Distinct().Count() != Rows.Length ||
            Rows.Select(static row => row.TargetBoneIndex)
                .Distinct().Count() != Rows.Length)
        {
            throw new ArgumentException(
                "Direct binding rows must be unique, deterministic, and fully supported by hierarchy and bind evidence.",
                nameof(Rows));
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A direct-binding signature must be a SHA-256 value.",
                parameterName);
        }
    }
}

public static class DirectRigBindingFingerprint
{
    public static string Compute(
        string sourceSkeletonSignature,
        string targetSkeletonSignature,
        string policyVersion,
        IEnumerable<DirectBoneBinding> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSkeletonSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSkeletonSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyVersion);
        ArgumentNullException.ThrowIfNull(rows);

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        Append(hash, policyVersion);
        Append(hash, sourceSkeletonSignature);
        Append(hash, targetSkeletonSignature);
        foreach (DirectBoneBinding row in rows
                     .OrderBy(static row => row.SourceBoneIndex)
                     .ThenBy(static row => row.TargetBoneIndex))
        {
            Append(hash, row.SourceBoneIndex.ToString(
                CultureInfo.InvariantCulture));
            Append(hash, row.TargetBoneIndex.ToString(
                CultureInfo.InvariantCulture));
            Append(hash, row.SourceBoneName);
            Append(hash, row.TargetBoneName);
            Append(hash, ((int)row.IdentityEvidence).ToString(
                CultureInfo.InvariantCulture));
            Append(hash, row.ParentTopologyMatches ? "1" : "0");
            Append(hash, row.LocalBindMatches ? "1" : "0");
            Append(hash, row.GlobalBindMatches ? "1" : "0");
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] payload = Encoding.UTF8.GetBytes(value.Normalize(
            NormalizationForm.FormKC));
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        hash.AppendData(length);
        hash.AppendData(payload);
    }
}
