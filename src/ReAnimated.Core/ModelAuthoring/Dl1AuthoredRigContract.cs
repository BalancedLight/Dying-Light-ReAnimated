using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// A bone-local AABB authored for Chrome's hierarchy and model-bounds logic.
/// Chrome copies these bounds into the compiled entity; zero-sized rows make
/// the editor draw pivot dots instead of useful bone segments.
/// </summary>
public readonly record struct Dl1AuthoredBoneBounds(
    Vector3D Center,
    Vector3D HalfExtents)
{
    public bool IsFiniteAndNonZero =>
        Center.IsFinite &&
        HalfExtents.IsFinite &&
        HalfExtents.X > 0.0 &&
        HalfExtents.Y > 0.0 &&
        HalfExtents.Z > 0.0;
}

/// <summary>
/// One animation entity exactly as it will be emitted into a Chrome source
/// MSH. Physical indexes, local matrices, inverse-global references, and
/// bounds are one indivisible identity contract.
/// </summary>
public sealed record Dl1AuthoredRigDecompositionDiagnostic(
    string Code,
    string BoneName,
    int PhysicalIndex,
    double OrthogonalityResidual,
    string Message);

public sealed record Dl1AuthoredRigNode
{
    public required int PhysicalIndex { get; init; }

    public required int SourceBoneIndex { get; init; }

    public required string Name { get; init; }

    public required int ParentPhysicalIndex { get; init; }

    public required BoneKind Kind { get; init; }

    public required bool IsDeform { get; init; }

    public required TransformMatrix LocalBindMatrix { get; init; }

    public required TransformMatrix GlobalBindMatrix { get; init; }

    public required TransformMatrix InverseGlobalReferenceMatrix { get; init; }

    public required Dl1AuthoredBoneBounds Bounds { get; init; }

    public required uint DescriptorHash { get; init; }
}

/// <summary>
/// Immutable emitted-rig identity shared by model output, animation output,
/// and the authoring preview. It deliberately fingerprints the serialized
/// physical hierarchy rather than the pre-conversion FBX hierarchy.
/// </summary>
public sealed class Dl1AuthoredRigContract
{
    private const double MatrixTolerance = 5e-5;
    private const double DecompositionTolerance = 1e-7;
    private readonly ImmutableArray<TransformTRS> _localBindPoses;

    public Dl1AuthoredRigContract(
        string sourceModelName,
        string sourceFbxSha256,
        IEnumerable<Dl1AuthoredRigNode> nodes,
        IEnumerable<MorphChannelDefinition>? morphChannels = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFbxSha256);
        ArgumentNullException.ThrowIfNull(nodes);

        ImmutableArray<Dl1AuthoredRigNode> nodeArray = nodes.ToImmutableArray();
        ImmutableArray<MorphChannelDefinition> morphArray =
            morphChannels?.ToImmutableArray() ?? [];
        Validate(nodeArray, morphArray);

        SourceModelName = sourceModelName;
        SourceFbxSha256 = sourceFbxSha256;
        Nodes = nodeArray;
        MorphChannels = morphArray;
        (_localBindPoses, DecompositionDiagnostics) =
            BuildLocalBindPoses(nodeArray);
        SourceToPhysicalIndices = BuildSourceToPhysical(nodeArray);
        BindFingerprint = HashBind(nodeArray);
        SkeletonFingerprint = HashSkeleton(nodeArray);
        DescriptorFingerprint = HashDescriptors(nodeArray);
        MorphFingerprint = HashMorphs(morphArray);
        ContractId = $"authored:{HashComposite(BindFingerprint, SkeletonFingerprint, DescriptorFingerprint, MorphFingerprint)[..24]}";
    }

    public string SourceModelName { get; }

    public string SourceFbxSha256 { get; }

    public ImmutableArray<Dl1AuthoredRigNode> Nodes { get; }

    public ImmutableArray<MorphChannelDefinition> MorphChannels { get; }

    public ImmutableArray<Dl1AuthoredRigDecompositionDiagnostic>
        DecompositionDiagnostics { get; }

    public ImmutableArray<int> SourceToPhysicalIndices { get; }

    public string BindFingerprint { get; }

    public string SkeletonFingerprint { get; }

    public string DescriptorFingerprint { get; }

    public string MorphFingerprint { get; }

    public string ContractId { get; }

    public RigDefinition CreateRigDefinition()
    {
        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(Nodes.Length);
        foreach (Dl1AuthoredRigNode node in Nodes)
        {
            bones.Add(new BoneDefinition(
                node.PhysicalIndex,
                node.Name,
                node.ParentPhysicalIndex,
                _localBindPoses[node.PhysicalIndex],
                node.Kind,
                requiredForExport: true,
                descriptorHash: node.DescriptorHash));
        }

        return new RigDefinition(
            ContractId,
            SourceModelName,
            bones.MoveToImmutable(),
            MorphChannels,
            sourceAssetFingerprint: new SourceAssetFingerprint(
                "source/model.fbx",
                SourceFbxSha256,
                ContractId));
    }

    private static (
        ImmutableArray<TransformTRS> LocalBindPoses,
        ImmutableArray<Dl1AuthoredRigDecompositionDiagnostic> Diagnostics)
        BuildLocalBindPoses(ImmutableArray<Dl1AuthoredRigNode> nodes)
    {
        var poses = ImmutableArray.CreateBuilder<TransformTRS>(nodes.Length);
        var diagnostics = ImmutableArray.CreateBuilder<Dl1AuthoredRigDecompositionDiagnostic>();
        foreach (Dl1AuthoredRigNode node in nodes)
        {
            double residual = MeasureOrthogonalityResidual(node.LocalBindMatrix);
            try
            {
                poses.Add(node.LocalBindMatrix.Decompose(DecompositionTolerance));
            }
            catch (InvalidOperationException exception)
            {
                try
                {
                    poses.Add(ProjectAffineToTrs(node.LocalBindMatrix));
                }
                catch (Exception repairException) when (
                    repairException is InvalidOperationException or ArgumentException)
                {
                    throw new InvalidDataException(
                        $"Authored-rig bone '{node.Name}' at physical index {node.PhysicalIndex} " +
                        $"could not be decomposed (orthogonality residual {residual:E6}): {exception.Message}",
                        repairException);
                }

                diagnostics.Add(new Dl1AuthoredRigDecompositionDiagnostic(
                    "model_authored_bind_orthonormalized",
                    node.Name,
                    node.PhysicalIndex,
                    residual,
                    $"Bone '{node.Name}' at physical index {node.PhysicalIndex} had orthogonality " +
                    $"residual {residual:E6}; its DL1 preview bind was projected to TRS."));
            }
        }

        return (poses.MoveToImmutable(), diagnostics.ToImmutable());
    }

    private static TransformTRS ProjectAffineToTrs(TransformMatrix matrix)
    {
        TransformMatrix rotation = OrthonormalizeRotation(matrix);
        double scaleX = new Vector3D(matrix.M11, matrix.M21, matrix.M31).Length;
        double scaleY = new Vector3D(matrix.M12, matrix.M22, matrix.M32).Length;
        double scaleZ = new Vector3D(matrix.M13, matrix.M23, matrix.M33).Length;
        return new TransformTRS(
            matrix.Translation,
            QuaternionD.FromRotationMatrix(rotation),
            new Vector3D(
                Math.Max(scaleX, 1e-8),
                Math.Max(scaleY, 1e-8),
                Math.Max(scaleZ, 1e-8)));
    }

    private static double MeasureOrthogonalityResidual(TransformMatrix matrix)
    {
        Vector3D x = new(matrix.M11, matrix.M21, matrix.M31);
        Vector3D y = new(matrix.M12, matrix.M22, matrix.M32);
        Vector3D z = new(matrix.M13, matrix.M23, matrix.M33);
        if (!x.TryNormalize(out x, 1e-15) ||
            !y.TryNormalize(out y, 1e-15) ||
            !z.TryNormalize(out z, 1e-15))
        {
            return double.PositiveInfinity;
        }

        return Math.Max(
            Math.Abs(Vector3D.Dot(x, y)),
            Math.Max(
                Math.Abs(Vector3D.Dot(x, z)),
                Math.Abs(Vector3D.Dot(y, z))));
    }

    private static TransformMatrix OrthonormalizeRotation(TransformMatrix value)
    {
        if (!value.IsFinite)
        {
            throw new InvalidDataException("An authored Chrome bone frame must be finite.");
        }

        TransformMatrix projected = LinearPart(value);
        double frobenius = Math.Sqrt(
            (projected.M11 * projected.M11) + (projected.M12 * projected.M12) + (projected.M13 * projected.M13) +
            (projected.M21 * projected.M21) + (projected.M22 * projected.M22) + (projected.M23 * projected.M23) +
            (projected.M31 * projected.M31) + (projected.M32 * projected.M32) + (projected.M33 * projected.M33));
        if (double.IsFinite(frobenius) && frobenius > 1e-12)
        {
            projected = ScaleLinear(projected, 1.0 / frobenius);
            for (int iteration = 0; iteration < 32; iteration++)
            {
                TransformMatrix inverse;
                try
                {
                    inverse = projected.InvertedAffine();
                }
                catch (InvalidOperationException)
                {
                    projected = LinearPart(value);
                    break;
                }

                TransformMatrix next = AverageLinear(
                    projected,
                    TransposeLinear(inverse));
                double delta = MaximumLinearDifference(projected, next);
                projected = next;
                if (delta <= 1e-13)
                {
                    break;
                }
            }
        }

        Vector3D originalX = new(projected.M11, projected.M21, projected.M31);
        Vector3D originalY = new(projected.M12, projected.M22, projected.M32);
        Vector3D originalZ = new(projected.M13, projected.M23, projected.M33);
        Vector3D x = NormalizeOrFallback(originalX, originalY, Vector3D.UnitX);
        Vector3D yCandidate = originalY - (x * Vector3D.Dot(originalY, x));
        if (!yCandidate.TryNormalize(out Vector3D y, 1e-10))
        {
            yCandidate = originalZ - (x * Vector3D.Dot(originalZ, x));
            if (!yCandidate.TryNormalize(out y, 1e-10))
            {
                Vector3D seed = Math.Abs(Vector3D.Dot(x, Vector3D.UnitZ)) < 0.95
                    ? Vector3D.UnitZ
                    : Vector3D.UnitY;
                y = Vector3D.Cross(seed, x).Normalized();
            }
        }

        Vector3D z = Vector3D.Cross(x, y).Normalized();
        if (Vector3D.Dot(z, originalZ) < 0.0)
        {
            y = -y;
            z = -z;
        }

        return new TransformMatrix(
            x.X, y.X, z.X, value.M14,
            x.Y, y.Y, z.Y, value.M24,
            x.Z, y.Z, z.Z, value.M34,
            0.0, 0.0, 0.0, 1.0);
    }

    private static TransformMatrix LinearPart(TransformMatrix value) =>
        new(
            value.M11, value.M12, value.M13, 0.0,
            value.M21, value.M22, value.M23, 0.0,
            value.M31, value.M32, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix ScaleLinear(TransformMatrix value, double scale) =>
        new(
            value.M11 * scale, value.M12 * scale, value.M13 * scale, 0.0,
            value.M21 * scale, value.M22 * scale, value.M23 * scale, 0.0,
            value.M31 * scale, value.M32 * scale, value.M33 * scale, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix TransposeLinear(TransformMatrix value) =>
        new(
            value.M11, value.M21, value.M31, 0.0,
            value.M12, value.M22, value.M32, 0.0,
            value.M13, value.M23, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix AverageLinear(
        TransformMatrix left,
        TransformMatrix right) =>
        new(
            (left.M11 + right.M11) * 0.5, (left.M12 + right.M12) * 0.5, (left.M13 + right.M13) * 0.5, 0.0,
            (left.M21 + right.M21) * 0.5, (left.M22 + right.M22) * 0.5, (left.M23 + right.M23) * 0.5, 0.0,
            (left.M31 + right.M31) * 0.5, (left.M32 + right.M32) * 0.5, (left.M33 + right.M33) * 0.5, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static double MaximumLinearDifference(
        TransformMatrix left,
        TransformMatrix right)
    {
        double maximum = 0.0;
        maximum = Math.Max(maximum, Math.Abs(left.M11 - right.M11));
        maximum = Math.Max(maximum, Math.Abs(left.M12 - right.M12));
        maximum = Math.Max(maximum, Math.Abs(left.M13 - right.M13));
        maximum = Math.Max(maximum, Math.Abs(left.M21 - right.M21));
        maximum = Math.Max(maximum, Math.Abs(left.M22 - right.M22));
        maximum = Math.Max(maximum, Math.Abs(left.M23 - right.M23));
        maximum = Math.Max(maximum, Math.Abs(left.M31 - right.M31));
        maximum = Math.Max(maximum, Math.Abs(left.M32 - right.M32));
        return Math.Max(maximum, Math.Abs(left.M33 - right.M33));
    }

    private static Vector3D NormalizeOrFallback(
        Vector3D first,
        Vector3D second,
        Vector3D fallback)
    {
        if (first.TryNormalize(out Vector3D result))
        {
            return result;
        }

        if (second.TryNormalize(out result))
        {
            return result;
        }

        return fallback;
    }

    private static void Validate(
        ImmutableArray<Dl1AuthoredRigNode> nodes,
        ImmutableArray<MorphChannelDefinition> morphChannels)
    {
        if (nodes.IsEmpty)
        {
            throw new ArgumentException("An authored rig must contain at least one animation entity.", nameof(nodes));
        }

        var sourceIndexes = new HashSet<int>();
        var normalizedNames = new HashSet<string>(StringComparer.Ordinal);
        var descriptors = new Dictionary<uint, string>();
        for (int index = 0; index < nodes.Length; index++)
        {
            Dl1AuthoredRigNode node = nodes[index];
            if (node.PhysicalIndex != index ||
                node.SourceBoneIndex < 0 ||
                !sourceIndexes.Add(node.SourceBoneIndex))
            {
                throw new ArgumentException("Authored-rig physical and source indexes must be unique and contiguous.", nameof(nodes));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(node.Name, nameof(nodes));
            string normalizedName = node.Name.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
            if (!normalizedNames.Add(normalizedName))
            {
                throw new ArgumentException($"Authored-rig animation entity '{node.Name}' has a duplicate normalized name.", nameof(nodes));
            }

            if (node.ParentPhysicalIndex < -1 || node.ParentPhysicalIndex >= index)
            {
                throw new ArgumentException($"Authored-rig entity '{node.Name}' does not use topological physical order.", nameof(nodes));
            }

            if (!node.LocalBindMatrix.IsFinite ||
                !node.GlobalBindMatrix.IsFinite ||
                !node.InverseGlobalReferenceMatrix.IsFinite ||
                !node.Bounds.IsFiniteAndNonZero)
            {
                throw new ArgumentException($"Authored-rig entity '{node.Name}' contains an invalid matrix or bound.", nameof(nodes));
            }

            TransformMatrix reconstructed = node.ParentPhysicalIndex < 0
                ? node.LocalBindMatrix
                : nodes[node.ParentPhysicalIndex].GlobalBindMatrix * node.LocalBindMatrix;
            if (!reconstructed.NearlyEquals(node.GlobalBindMatrix, MatrixTolerance))
            {
                throw new ArgumentException($"Authored-rig entity '{node.Name}' does not reconstruct from its parent/local bind.", nameof(nodes));
            }

            if (!(node.GlobalBindMatrix * node.InverseGlobalReferenceMatrix)
                .NearlyEquals(TransformMatrix.Identity, MatrixTolerance))
            {
                throw new ArgumentException($"Authored-rig entity '{node.Name}' violates the inverse-global reference contract.", nameof(nodes));
            }

            ValidateOrthonormalFrame(node);
            if (descriptors.TryGetValue(node.DescriptorHash, out string? existing))
            {
                throw new ArgumentException(
                    $"Authored-rig entities '{existing}' and '{node.Name}' collide at descriptor 0x{node.DescriptorHash:X8}.",
                    nameof(nodes));
            }

            descriptors.Add(node.DescriptorHash, node.Name);
        }

        var morphNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < morphChannels.Length; index++)
        {
            MorphChannelDefinition morph = morphChannels[index];
            if (morph.Index != index || !morphNames.Add(morph.Name))
            {
                throw new ArgumentException(
                    "Authored-rig morph channels must have contiguous indexes and unique names.",
                    nameof(morphChannels));
            }

            if (morph.DescriptorHash is not { } descriptor)
            {
                throw new ArgumentException(
                    $"Authored-rig morph '{morph.Name}' has no DL1 descriptor.",
                    nameof(morphChannels));
            }

            if (descriptors.TryGetValue(descriptor, out string? existing))
            {
                throw new ArgumentException(
                    $"Authored-rig entities '{existing}' and '{morph.Name}' collide at descriptor 0x{descriptor:X8}.",
                    nameof(morphChannels));
            }

            descriptors.Add(descriptor, morph.Name);
        }

        if (sourceIndexes.Count != nodes.Length || sourceIndexes.Max() != nodes.Length - 1)
        {
            throw new ArgumentException("Authored-rig source indexes must form a complete zero-based permutation.", nameof(nodes));
        }
    }

    private static void ValidateOrthonormalFrame(Dl1AuthoredRigNode node)
    {
        Vector3D x = new(node.GlobalBindMatrix.M11, node.GlobalBindMatrix.M21, node.GlobalBindMatrix.M31);
        Vector3D y = new(node.GlobalBindMatrix.M12, node.GlobalBindMatrix.M22, node.GlobalBindMatrix.M32);
        Vector3D z = new(node.GlobalBindMatrix.M13, node.GlobalBindMatrix.M23, node.GlobalBindMatrix.M33);
        if (Math.Abs(x.Length - 1.0) > MatrixTolerance ||
            Math.Abs(y.Length - 1.0) > MatrixTolerance ||
            Math.Abs(z.Length - 1.0) > MatrixTolerance ||
            Math.Abs(Vector3D.Dot(x, y)) > MatrixTolerance ||
            Math.Abs(Vector3D.Dot(x, z)) > MatrixTolerance ||
            Math.Abs(Vector3D.Dot(y, z)) > MatrixTolerance ||
            Math.Abs(node.GlobalBindMatrix.LinearDeterminant - 1.0) > MatrixTolerance)
        {
            throw new ArgumentException($"Authored-rig entity '{node.Name}' does not have a right-handed orthonormal Chrome frame.");
        }
    }

    private static ImmutableArray<int> BuildSourceToPhysical(ImmutableArray<Dl1AuthoredRigNode> nodes)
    {
        var result = ImmutableArray.CreateBuilder<int>(nodes.Length);
        result.Count = nodes.Length;
        foreach (Dl1AuthoredRigNode node in nodes)
        {
            result[node.SourceBoneIndex] = node.PhysicalIndex;
        }

        return result.MoveToImmutable();
    }

    private static string HashBind(ImmutableArray<Dl1AuthoredRigNode> nodes) =>
        Hash(writer =>
        {
            foreach (Dl1AuthoredRigNode node in nodes)
            {
                writer.Write(node.PhysicalIndex);
                writer.Write(node.ParentPhysicalIndex);
                WriteMatrix(writer, node.LocalBindMatrix);
                WriteMatrix(writer, node.GlobalBindMatrix);
                WriteMatrix(writer, node.InverseGlobalReferenceMatrix);
                WriteVector(writer, node.Bounds.Center);
                WriteVector(writer, node.Bounds.HalfExtents);
            }
        });

    private static string HashSkeleton(ImmutableArray<Dl1AuthoredRigNode> nodes) =>
        Hash(writer =>
        {
            foreach (Dl1AuthoredRigNode node in nodes)
            {
                writer.Write(node.PhysicalIndex);
                writer.Write(node.SourceBoneIndex);
                writer.Write(node.Name.Normalize(NormalizationForm.FormKC).ToUpperInvariant());
                writer.Write(node.ParentPhysicalIndex);
                writer.Write((int)node.Kind);
                writer.Write(node.IsDeform);
            }
        });

    private static string HashDescriptors(ImmutableArray<Dl1AuthoredRigNode> nodes) =>
        Hash(writer =>
        {
            foreach (Dl1AuthoredRigNode node in nodes)
            {
                writer.Write(node.PhysicalIndex);
                writer.Write(node.DescriptorHash);
            }
        });

    private static string HashMorphs(ImmutableArray<MorphChannelDefinition> morphChannels) =>
        Hash(writer =>
        {
            foreach (MorphChannelDefinition morph in morphChannels)
            {
                writer.Write(morph.Index);
                writer.Write(morph.Name.Normalize(NormalizationForm.FormKC).ToUpperInvariant());
                writer.Write(morph.DescriptorHash ?? 0u);
                writer.Write(morph.SemanticRole ?? string.Empty);
            }
        });

    private static string HashComposite(params string[] values) =>
        Hash(writer =>
        {
            foreach (string value in values)
            {
                writer.Write(value);
            }
        });

    private static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
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
