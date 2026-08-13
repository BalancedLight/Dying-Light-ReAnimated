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

    public Dl1AuthoredRigContract(
        string sourceModelName,
        string sourceFbxSha256,
        IEnumerable<Dl1AuthoredRigNode> nodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFbxSha256);
        ArgumentNullException.ThrowIfNull(nodes);

        ImmutableArray<Dl1AuthoredRigNode> nodeArray = nodes.ToImmutableArray();
        Validate(nodeArray);

        SourceModelName = sourceModelName;
        SourceFbxSha256 = sourceFbxSha256;
        Nodes = nodeArray;
        SourceToPhysicalIndices = BuildSourceToPhysical(nodeArray);
        BindFingerprint = HashBind(nodeArray);
        SkeletonFingerprint = HashSkeleton(nodeArray);
        DescriptorFingerprint = HashDescriptors(nodeArray);
        ContractId = $"authored:{HashComposite(BindFingerprint, SkeletonFingerprint, DescriptorFingerprint)[..24]}";
    }

    public string SourceModelName { get; }

    public string SourceFbxSha256 { get; }

    public ImmutableArray<Dl1AuthoredRigNode> Nodes { get; }

    public ImmutableArray<int> SourceToPhysicalIndices { get; }

    public string BindFingerprint { get; }

    public string SkeletonFingerprint { get; }

    public string DescriptorFingerprint { get; }

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
                node.LocalBindMatrix.Decompose(),
                node.Kind,
                requiredForExport: true,
                descriptorHash: node.DescriptorHash));
        }

        return new RigDefinition(
            ContractId,
            SourceModelName,
            bones.MoveToImmutable(),
            sourceAssetFingerprint: new SourceAssetFingerprint(
                "source/model.fbx",
                SourceFbxSha256,
                ContractId));
    }

    private static void Validate(ImmutableArray<Dl1AuthoredRigNode> nodes)
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
