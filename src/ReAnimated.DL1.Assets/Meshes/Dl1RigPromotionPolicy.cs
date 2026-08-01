using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.DL1.Assets.Meshes;

public sealed record Dl1RigPromotionAnalysis(
    IReadOnlyList<int> NonTrsEntityIndexes,
    IReadOnlyList<int> DeclaredPaletteEntityIndexes,
    IReadOnlyList<int> EffectiveSkinEntityIndexes,
    bool HasUnresolvedSkinBindings)
{
    public bool HasEffectiveNonTrsSkinInfluence =>
        NonTrsEntityIndexes.Intersect(
            EffectiveSkinEntityIndexes).Any();
}

/// <summary>
/// Separates exact raw-matrix preview capability from the stricter local-TRS
/// authoring contract. This policy never fabricates a transform or skin
/// influence.
/// </summary>
public static class Dl1RigPromotionPolicy
{
    public static Dl1RigPromotionAnalysis Analyze(
        CompactMeshDocument hierarchy,
        IReadOnlyList<Dl1MeshSurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(surfaces);

        int animationEntityCount = Math.Clamp(
            hierarchy.AnimationEntityCountCandidate,
            0,
            hierarchy.Entities.Count);
        HashSet<int> nonTrs = [];
        for (int index = 0; index < animationEntityCount; index++)
        {
            CompactMatrix3x4 local =
                hierarchy.Entities[index].LocalMatrix;
            var matrix = new TransformMatrix(
                local.M11,
                local.M12,
                local.M13,
                local.M14,
                local.M21,
                local.M22,
                local.M23,
                local.M24,
                local.M31,
                local.M32,
                local.M33,
                local.M34,
                0,
                0,
                0,
                1);
            try
            {
                _ = matrix.Decompose(1.0e-4);
            }
            catch (InvalidOperationException)
            {
                nonTrs.Add(index);
            }
        }

        HashSet<int> declared = [];
        HashSet<int> effective = [];
        bool unresolved = false;
        foreach (Dl1MeshSurface surface in surfaces)
        {
            foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
            {
                IReadOnlyList<short> palette =
                    submesh.BonePaletteEntityIndexes;
                if (submesh.SkinBindingMode ==
                    Dl1SkinBindingMode
                        .StaticEntityTransformIgnoredPalette)
                {
                    // This palette is serialized but is not a skin binding:
                    // the named runtime skips palette setup for the exact
                    // no-BlendIndices declaration path.
                    continue;
                }

                foreach (short entityIndex in palette)
                {
                    if (entityIndex < 0 ||
                        entityIndex >= animationEntityCount)
                    {
                        unresolved = true;
                        continue;
                    }

                    declared.Add(entityIndex);
                }

                if (palette.Count == 0)
                {
                    continue;
                }

                if (submesh.SkinBindingMode !=
                    Dl1SkinBindingMode.ExplicitVertexWeights ||
                    submesh.FirstIndex < 0 ||
                    submesh.IndexCount <= 0 ||
                    submesh.FirstIndex > surface.Indices.Count ||
                    submesh.IndexCount >
                        surface.Indices.Count - submesh.FirstIndex)
                {
                    unresolved = true;
                    continue;
                }

                int end = checked(
                    submesh.FirstIndex + submesh.IndexCount);
                for (int offset = submesh.FirstIndex;
                     offset < end;
                     offset++)
                {
                    int vertexIndex = surface.Indices[offset];
                    if ((uint)vertexIndex >=
                        (uint)surface.Vertices.Count)
                    {
                        unresolved = true;
                        continue;
                    }

                    Dl1MeshVertex vertex =
                        surface.Vertices[vertexIndex];
                    ReadOnlySpan<float> weights =
                    [
                        vertex.BlendWeights.X,
                        vertex.BlendWeights.Y,
                        vertex.BlendWeights.Z,
                        vertex.BlendWeights.W,
                    ];
                    ReadOnlySpan<byte> localIndexes =
                    [
                        vertex.LocalBlendIndices.X,
                        vertex.LocalBlendIndices.Y,
                        vertex.LocalBlendIndices.Z,
                        vertex.LocalBlendIndices.W,
                    ];
                    float sum = 0;
                    for (int component = 0;
                         component < weights.Length;
                         component++)
                    {
                        float weight = weights[component];
                        if (!float.IsFinite(weight) ||
                            weight < 0 ||
                            weight > 1)
                        {
                            unresolved = true;
                            continue;
                        }

                        sum += weight;
                        if (weight <= 1.0e-6f)
                        {
                            continue;
                        }

                        int localIndex = localIndexes[component];
                        if ((uint)localIndex >=
                            (uint)palette.Count ||
                            palette[localIndex] < 0 ||
                            palette[localIndex] >=
                                animationEntityCount)
                        {
                            unresolved = true;
                            continue;
                        }

                        effective.Add(palette[localIndex]);
                    }

                    if (!float.IsFinite(sum) ||
                        MathF.Abs(sum - 1) > 0.02f)
                    {
                        unresolved = true;
                    }
                }
            }
        }

        return new Dl1RigPromotionAnalysis(
            nonTrs.Order().ToArray(),
            declared.Order().ToArray(),
            effective.Order().ToArray(),
            unresolved);
    }

    public static bool CanPublishRawMatrixHelperPreview(
        CompactMeshDocument hierarchy,
        IReadOnlyList<Dl1MeshSurface> surfaces,
        string promotionFailure)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentException.ThrowIfNullOrWhiteSpace(promotionFailure);

        Dl1RigPromotionAnalysis analysis =
            Analyze(hierarchy, surfaces);
        if (!hierarchy.IsStructurallyValid ||
            hierarchy.Bones.Count != 0 ||
            hierarchy.Helpers.Count == 0 ||
            surfaces.Count == 0 ||
            !promotionFailure.Contains(
                "singular or sheared local transform",
                StringComparison.OrdinalIgnoreCase) ||
            analysis.DeclaredPaletteEntityIndexes.Count > 0 ||
            analysis.HasUnresolvedSkinBindings)
        {
            return false;
        }

        return CanPublishRawBindPosePreview(
            hierarchy,
            surfaces);
    }

    /// <summary>
    /// Returns true only when full compact matrices can produce an exact raw
    /// bind-pose preview while every local-TRS authoring operation remains
    /// disabled. Non-TRS rows must be outside every declared skin palette,
    /// all skin bindings must resolve, and every effective deform bind must
    /// be finite and invertible.
    /// </summary>
    public static bool CanPublishRawBindPosePreview(
        CompactMeshDocument hierarchy,
        IReadOnlyList<Dl1MeshSurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(surfaces);

        Dl1RigPromotionAnalysis analysis =
            Analyze(hierarchy, surfaces);
        if (!hierarchy.IsStructurallyValid ||
            surfaces.Count == 0 ||
            analysis.NonTrsEntityIndexes.Count == 0 ||
            analysis.HasUnresolvedSkinBindings ||
            analysis.NonTrsEntityIndexes.Intersect(
                analysis.DeclaredPaletteEntityIndexes).Any())
        {
            return false;
        }

        IReadOnlyList<CompactMatrix3x4> worldMatrices;
        try
        {
            worldMatrices = hierarchy.ReconstructGlobalMatrices();
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (worldMatrices.Any(static matrix => !matrix.IsFinite))
        {
            return false;
        }

        foreach (int entityIndex in
                 analysis.EffectiveSkinEntityIndexes)
        {
            if ((uint)entityIndex >=
                (uint)worldMatrices.Count)
            {
                return false;
            }

            try
            {
                _ = ToTransformMatrix(
                        worldMatrices[entityIndex])
                    .InvertedAffine();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return true;
    }

    private static TransformMatrix ToTransformMatrix(
        in CompactMatrix3x4 matrix) =>
        new(
            matrix.M11,
            matrix.M12,
            matrix.M13,
            matrix.M14,
            matrix.M21,
            matrix.M22,
            matrix.M23,
            matrix.M24,
            matrix.M31,
            matrix.M32,
            matrix.M33,
            matrix.M34,
            0,
            0,
            0,
            1);
}
