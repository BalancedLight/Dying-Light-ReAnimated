using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1CompiledRigNodeReadBack(Guid? EntityId, string SourceName, string CompiledName,
    int SourcePhysicalIndex, int CompiledEntityIndex, int CompiledParentIndex,
    double LocalMatrixError, double ReferenceMatrixError, double BoundsError);

/// <summary>Checks the prepared animation hierarchy against decoded compiler output, without changing either.</summary>
public static class Dl1CompiledRigValidator
{
    private const double FloatTolerance = 1e-5;

    public static ImmutableArray<Dl1CompiledRigNodeReadBack> Validate(Dl1AuthoredRigContract expected, CompactMeshDocument compiled)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.IsStructurallyValid) throw new InvalidDataException("The compiled hierarchy is structurally invalid.");
        for (int index = 0; index < compiled.Entities.Count; index++)
            if (compiled.Entities[index].Index != index || compiled.Entities[index].ParentIndex < -1 || compiled.Entities[index].ParentIndex >= index)
                throw new InvalidDataException("The compiled hierarchy is not a contiguous parent-before-child table.");
        var byName = compiled.Entities.ToLookup(static n => n.Name, StringComparer.OrdinalIgnoreCase);
        var matched = new CompactMeshEntity[expected.Nodes.Length];
        foreach (Dl1AuthoredRigNode node in expected.Nodes)
        {
            CompactMeshEntity[] candidates = byName[node.Name].ToArray();
            if (candidates.Length != 1) throw new InvalidDataException($"Prepared node '{node.Name}' has {candidates.Length} compiled matches.");
            matched[node.PhysicalIndex] = candidates[0];
        }
        var result = ImmutableArray.CreateBuilder<Dl1CompiledRigNodeReadBack>(expected.Nodes.Length);
        foreach (Dl1AuthoredRigNode node in expected.Nodes)
        {
            CompactMeshEntity actual = matched[node.PhysicalIndex];
            if (!actual.LocalMatrix.IsFinite || !actual.ReferenceMatrix.IsFinite || !actual.Bounds.IsFinite ||
                actual.Bounds.HalfX < 0 || actual.Bounds.HalfY < 0 || actual.Bounds.HalfZ < 0)
                throw new InvalidDataException($"Compiled node '{actual.Name}' has non-finite matrices or invalid bounds.");
            int parent = node.ParentPhysicalIndex < 0 ? -1 : matched[node.ParentPhysicalIndex].Index;
            if (actual.ParentIndex != parent) throw new InvalidDataException($"Compiled node '{actual.Name}' changed its prepared parent.");
            CompactMeshEntityType type = node.IsDeform ? CompactMeshEntityType.Bone : CompactMeshEntityType.Helper;
            if (actual.EntityType != type) throw new InvalidDataException($"Compiled node '{actual.Name}' changed its prepared representation from {type} to {actual.EntityType}.");
            double localError = MatrixError(node.LocalBindMatrix, actual.LocalMatrix);
            double referenceError = MatrixError(node.InverseGlobalReferenceMatrix, actual.ReferenceMatrix);
            double boundsError = BoundsError(node.Bounds, actual.Bounds);
            if (!double.IsFinite(localError) || !double.IsFinite(referenceError) || !double.IsFinite(boundsError) ||
                localError > FloatTolerance || referenceError > FloatTolerance || boundsError > FloatTolerance)
                throw new InvalidDataException($"Compiled node '{actual.Name}' changed its prepared frame/reference/bounds " +
                    $"(maximum errors {localError:E3}/{referenceError:E3}/{boundsError:E3}). No model RPack was published.");
            result.Add(new(node.SemanticEntityId, node.Name, actual.Name, node.PhysicalIndex, actual.Index, actual.ParentIndex,
                localError, referenceError, boundsError));
        }
        return result.MoveToImmutable();
    }

    private static double MatrixError(TransformMatrix expected, CompactMatrix3x4 actual)
    {
        double[] differences =
        [
            expected.M11 - actual.M11, expected.M12 - actual.M12, expected.M13 - actual.M13, expected.M14 - actual.M14,
            expected.M21 - actual.M21, expected.M22 - actual.M22, expected.M23 - actual.M23, expected.M24 - actual.M24,
            expected.M31 - actual.M31, expected.M32 - actual.M32, expected.M33 - actual.M33, expected.M34 - actual.M34,
        ];
        return differences.Max(static d => Math.Abs(d));
    }

    private static double BoundsError(Dl1AuthoredBoneBounds expected, CompactBounds actual) => new[]
    {
        expected.Center.X - actual.CenterX, expected.Center.Y - actual.CenterY, expected.Center.Z - actual.CenterZ,
        expected.HalfExtents.X - actual.HalfX, expected.HalfExtents.Y - actual.HalfY, expected.HalfExtents.Z - actual.HalfZ,
    }.Max(static d => Math.Abs(d));
}
