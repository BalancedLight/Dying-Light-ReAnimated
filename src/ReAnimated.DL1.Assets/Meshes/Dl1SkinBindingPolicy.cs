using System.Numerics;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>
/// Classifies decoded DL1 skin binding data without changing the serialized
/// vertex values. The rigid indexed-palette case is corpus-inferred from the
/// validated Windows 1.55 retail corpus; it is not a claim of live-game
/// validation. The palette-ignored no-BlendIndices case follows the named
/// runtime's declaration and rendering branches.
/// </summary>
public static class Dl1SkinBindingPolicy
{
    public static Dl1SkinBindingMode Classify(
        Dl1VertexLayout layout,
        IReadOnlyList<Dl1MeshVertex> vertices,
        IReadOnlyList<ushort> indices,
        Dl1MeshSubmesh submesh,
        CompactMeshEntity? surfaceEntity = null,
        CompactMatrix3x4? surfaceWorldMatrix = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(submesh);

        int paletteCount =
            submesh.BonePaletteEntityIndexes.Count;
        if (paletteCount == 0)
        {
            return Dl1SkinBindingMode.None;
        }

        bool declaresBlendWeights = layout.Elements.Any(
            static element =>
                element.Semantic ==
                    Dl1VertexSemantic.BlendWeights);
        bool declaresBlendIndices = layout.Elements.Any(
            static element =>
                element.Semantic ==
                    Dl1VertexSemantic.BlendIndices);
        bool hasBlendWeights = layout.Elements.Any(
            static element =>
                element.Semantic ==
                    Dl1VertexSemantic.BlendWeights &&
                element.Format ==
                    Dl1VertexElementFormat.Byte4Normalized);
        bool hasBlendIndices = layout.Elements.Any(
            static element =>
                element.Semantic ==
                    Dl1VertexSemantic.BlendIndices &&
                element.Format ==
                    Dl1VertexElementFormat.Byte4);
        if (hasBlendWeights && hasBlendIndices)
        {
            return IsRigidIndexedPalette(
                    vertices,
                    indices,
                    submesh,
                    paletteCount)
                ? Dl1SkinBindingMode.RigidIndexedPalette
                : Dl1SkinBindingMode.ExplicitVertexWeights;
        }

        if (!hasBlendWeights &&
            !hasBlendIndices &&
            !declaresBlendWeights &&
            !declaresBlendIndices &&
            paletteCount > 0 &&
            surfaceEntity is not null &&
            surfaceWorldMatrix is CompactMatrix3x4 worldMatrix &&
            CanUseStaticEntityTransformIgnoredPalette(
                surfaceEntity,
                worldMatrix))
        {
            return Dl1SkinBindingMode
                .StaticEntityTransformIgnoredPalette;
        }

        return Dl1SkinBindingMode.UnresolvedMissingBlendStreams;
    }

    /// <summary>
    /// The declaration branch never reads the decoded palette when its
    /// skinning feature bit is absent. It submits the hierarchy element world
    /// transform for both root and parented entities, so the only transform
    /// publication gate is an exactly reconstructed finite world matrix.
    /// </summary>
    public static bool CanUseStaticEntityTransformIgnoredPalette(
        CompactMeshEntity entity,
        CompactMatrix3x4 entityWorldMatrix)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.EntityType.HasFlag(
                CompactMeshEntityType.SkinnedMesh) &&
            entityWorldMatrix.IsFinite;
    }

    private static bool IsRigidIndexedPalette(
        IReadOnlyList<Dl1MeshVertex> vertices,
        IReadOnlyList<ushort> indices,
        Dl1MeshSubmesh submesh,
        int paletteCount)
    {
        if (submesh.FirstIndex < 0 ||
            submesh.IndexCount <= 0 ||
            submesh.FirstIndex >
                indices.Count - submesh.IndexCount)
        {
            return false;
        }

        int end = checked(
            submesh.FirstIndex + submesh.IndexCount);
        for (int offset = submesh.FirstIndex;
             offset < end;
             offset++)
        {
            int vertexIndex = indices[offset];
            if (vertexIndex < 0 ||
                vertexIndex >= vertices.Count)
            {
                return false;
            }

            Dl1MeshVertex vertex = vertices[vertexIndex];
            if (vertex.BlendWeights != Vector4.Zero ||
                vertex.LocalBlendIndices.X >= paletteCount ||
                vertex.LocalBlendIndices.Y != 0 ||
                vertex.LocalBlendIndices.Z != 0 ||
                vertex.LocalBlendIndices.W != 0)
            {
                return false;
            }
        }

        return true;
    }
}
