using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// One physical-node expectation captured from the source MSH values written
/// for the official compiler, after conversion to the serialized float layout.
/// </summary>
public sealed record Dl1PreparedPhysicalNodeExpectation(
    int PhysicalIndex,
    string Name,
    int ParentIndex,
    uint SourceMshType,
    CompactMeshEntityType ExpectedEntityType,
    CompactMatrix3x4 LocalMatrix,
    CompactMatrix3x4 ReferenceMatrix,
    CompactBounds Bounds);

public sealed record Dl1PreparedPhysicalNodeReadBack(
    Dl1PreparedPhysicalNodeExpectation Expected,
    string CompiledName,
    int CompiledEntityIndex,
    int CompiledParentIndex,
    CompactMeshEntityType CompiledEntityType,
    CompactMatrix3x4 CompiledLocalMatrix,
    CompactMatrix3x4 CompiledReferenceMatrix,
    CompactBounds CompiledBounds,
    double LocalMatrixError,
    double ReferenceMatrixError,
    double BoundsError);

public sealed record Dl1PreparedPhysicalNodeReadBackEvidence(
    string ContractFingerprint,
    int VerifiedNodeCount,
    ImmutableArray<Dl1PreparedPhysicalNodeReadBack> Nodes);

/// <summary>
/// Verifies the compact hierarchy against every physical node emitted by the
/// source writer. It checks serialized source-MSH identity, parent, type, frame,
/// reference and bounds fields without interpreting component flags or runtime behavior.
/// </summary>
public static class Dl1PreparedPhysicalNodeReadBackValidator
{
    // Match the existing authored-rig read-back tolerance; prepared values are
    // already rounded to the source MSH's float32 field representation.
    private const double FloatTolerance = 1e-5;
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Source MSH stores the node-type discriminant as one of these four exact
    /// values; compact-mesh output stores the corresponding byte discriminant.
    /// Other source types remain unsupported and fail closed.
    /// </summary>
    internal static CompactMeshEntityType MapSourceMshType(uint sourceMshType) =>
        sourceMshType switch
        {
            1 => CompactMeshEntityType.Mesh,
            2 => CompactMeshEntityType.SkinnedMesh,
            4 => CompactMeshEntityType.Helper,
            8 => CompactMeshEntityType.Bone,
            _ => throw new InvalidDataException(
                $"Prepared source MSH node type 0x{sourceMshType:X8} has no exact compact entity-type mapping."),
        };

    public static Dl1PreparedPhysicalNodeReadBackEvidence Validate(
        ImmutableArray<Dl1PreparedPhysicalNodeExpectation> expected,
        CompactMeshDocument compiled)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (expected.IsDefaultOrEmpty)
            throw new ArgumentException("Prepared physical-node expectations are required.", nameof(expected));
        if (!compiled.IsStructurallyValid ||
            compiled.DeclaredEntityCount != expected.Length ||
            compiled.Entities.Count != expected.Length)
        {
            throw new InvalidDataException(
                $"Compiled compact declared/observed entity count {compiled.DeclaredEntityCount}/{compiled.Entities.Count} differs from the {expected.Length} prepared physical nodes, or the hierarchy is structurally invalid. No model RPack was published.");
        }

        var expectedNames = new HashSet<string>(IdentityComparer);
        var compiledNames = new HashSet<string>(IdentityComparer);
        for (int index = 0; index < expected.Length; index++)
        {
            Dl1PreparedPhysicalNodeExpectation node = expected[index];
            CompactMeshEntity actual = compiled.Entities[index];
            if (node.PhysicalIndex != index ||
                string.IsNullOrWhiteSpace(node.Name) ||
                !expectedNames.Add(node.Name) ||
                node.ParentIndex < -1 || node.ParentIndex >= index ||
                !node.LocalMatrix.IsFinite ||
                !node.ReferenceMatrix.IsFinite ||
                !node.Bounds.IsFinite ||
                node.Bounds.HalfX < 0 || node.Bounds.HalfY < 0 || node.Bounds.HalfZ < 0 ||
                node.ExpectedEntityType != MapSourceMshType(node.SourceMshType))
            {
                throw new InvalidDataException("Prepared physical-node expectations are malformed or ambiguous.");
            }

            if (actual.Index != index ||
                string.IsNullOrWhiteSpace(actual.Name) ||
                !compiledNames.Add(actual.Name))
            {
                throw new InvalidDataException("Compiled compact entity indices or names are not unique and contiguous for prepared-node read-back.");
            }

            if (!IdentityComparer.Equals(node.Name, actual.Name))
            {
                throw new InvalidDataException(
                    $"Compiled physical node {index} is '{actual.Name}', expected source-MSH node '{node.Name}'. No model RPack was published.");
            }

            if (actual.ParentIndex != node.ParentIndex)
            {
                throw new InvalidDataException(
                    $"Compiled physical node '{actual.Name}' changed its prepared parent index from {node.ParentIndex} to {actual.ParentIndex}. No model RPack was published.");
            }

            if (actual.EntityType != node.ExpectedEntityType)
            {
                throw new InvalidDataException(
                    $"Compiled physical node '{actual.Name}' changed its stored entity type from {node.ExpectedEntityType} to {actual.EntityType}. No model RPack was published.");
            }

            if (!actual.LocalMatrix.IsFinite || !actual.ReferenceMatrix.IsFinite ||
                !actual.Bounds.IsFinite || actual.Bounds.HalfX < 0 ||
                actual.Bounds.HalfY < 0 || actual.Bounds.HalfZ < 0)
            {
                throw new InvalidDataException(
                    $"Compiled physical node '{actual.Name}' contains non-finite frames or invalid bounds.");
            }

            double localError = MatrixError(node.LocalMatrix, actual.LocalMatrix);
            double referenceError = MatrixError(node.ReferenceMatrix, actual.ReferenceMatrix);
            double boundsError = BoundsError(node.Bounds, actual.Bounds);
            if (!double.IsFinite(localError) || !double.IsFinite(referenceError) ||
                !double.IsFinite(boundsError) || localError > FloatTolerance ||
                referenceError > FloatTolerance || boundsError > FloatTolerance)
            {
                throw new InvalidDataException(
                    $"Compiled physical node '{actual.Name}' differs from its source-MSH frame/reference/bounds " +
                    $"(maximum errors {localError:E3}/{referenceError:E3}/{boundsError:E3}; tolerance {FloatTolerance:E3}). No model RPack was published.");
            }
        }

        int expectedRootCount = expected.Count(static node => node.ParentIndex < 0);
        if (compiled.DeclaredRootCount != expectedRootCount ||
            compiled.ObservedRootCount != expectedRootCount)
        {
            throw new InvalidDataException(
                $"Compiled compact declared/observed root count {compiled.DeclaredRootCount}/{compiled.ObservedRootCount} differs from the {expectedRootCount} prepared physical roots. No model RPack was published.");
        }

        ImmutableArray<Dl1PreparedPhysicalNodeReadBack> rows = expected
            .Select((node, index) =>
            {
                CompactMeshEntity actual = compiled.Entities[index];
                return new Dl1PreparedPhysicalNodeReadBack(
                    node,
                    actual.Name,
                    actual.Index,
                    actual.ParentIndex,
                    actual.EntityType,
                    actual.LocalMatrix,
                    actual.ReferenceMatrix,
                    actual.Bounds,
                    MatrixError(node.LocalMatrix, actual.LocalMatrix),
                    MatrixError(node.ReferenceMatrix, actual.ReferenceMatrix),
                    BoundsError(node.Bounds, actual.Bounds));
            })
            .ToImmutableArray();
        byte[] fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(
            new { expected, nodes = rows },
            FingerprintJsonOptions);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes));
        return new(fingerprint, rows.Length, rows);
    }

    private static double MatrixError(CompactMatrix3x4 expected, CompactMatrix3x4 actual) => new[]
    {
        expected.M11 - actual.M11, expected.M12 - actual.M12, expected.M13 - actual.M13, expected.M14 - actual.M14,
        expected.M21 - actual.M21, expected.M22 - actual.M22, expected.M23 - actual.M23, expected.M24 - actual.M24,
        expected.M31 - actual.M31, expected.M32 - actual.M32, expected.M33 - actual.M33, expected.M34 - actual.M34,
    }.Max(static difference => Math.Abs(difference));

    private static double BoundsError(CompactBounds expected, CompactBounds actual) => new[]
    {
        expected.CenterX - actual.CenterX, expected.CenterY - actual.CenterY, expected.CenterZ - actual.CenterZ,
        expected.HalfX - actual.HalfX, expected.HalfY - actual.HalfY, expected.HalfZ - actual.HalfZ,
    }.Max(static difference => Math.Abs(difference));
}
