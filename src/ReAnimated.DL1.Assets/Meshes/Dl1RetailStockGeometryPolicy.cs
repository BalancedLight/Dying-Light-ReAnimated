namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>
/// Fail-closed exceptions for exact installed DL1 1.55 geometry payloads whose
/// serialized UV0 rows contain IEEE-half infinities. These are not general
/// material, resource-name, or vertex-layout allowances.
/// </summary>
internal static class Dl1RetailStockGeometryPolicy
{
    private static readonly VertexElementProfile[] HalfPositionLayout =
    [
        new(
            Dl1VertexSemantic.Position,
            0,
            Dl1VertexElementFormat.Half4,
            0),
        new(
            Dl1VertexSemantic.Normal,
            0,
            Dl1VertexElementFormat.Byte4Normalized,
            8),
        new(
            Dl1VertexSemantic.TextureCoordinate,
            0,
            Dl1VertexElementFormat.Half2,
            12),
        new(
            Dl1VertexSemantic.Tangent,
            0,
            Dl1VertexElementFormat.Byte4Normalized,
            16),
    ];

    private static readonly VertexElementProfile[] HorizonLayout =
    [
        new(
            Dl1VertexSemantic.Position,
            0,
            Dl1VertexElementFormat.Float3,
            0),
        new(
            Dl1VertexSemantic.Normal,
            0,
            Dl1VertexElementFormat.Byte4Normalized,
            12),
        new(
            Dl1VertexSemantic.TextureCoordinate,
            0,
            Dl1VertexElementFormat.Half2,
            16),
        new(
            Dl1VertexSemantic.TextureCoordinate,
            1,
            Dl1VertexElementFormat.Half2,
            20),
        new(
            Dl1VertexSemantic.Tangent,
            0,
            Dl1VertexElementFormat.Byte4Normalized,
            24),
        new(
            Dl1VertexSemantic.Unknown,
            0,
            Dl1VertexElementFormat.Byte4Normalized,
            28),
    ];

    private static readonly StockUvProfile[] StockUvProfiles =
    [
        new(
            "furniture_weapon_rack_a",
            "9704fc19b87038046287a11dde300a48c40d565e38df2644accdd573502c6456",
            "furniture_weapon_rack_a",
            "furniture_bookshelf_a.mat",
            20,
            HalfPositionLayout,
            56,
            51,
            32),
        new(
            "ot_glass_a",
            "dc630a7f9425b5a7680682bbb8f49826eec3f2a51c94e4f33ede92100d9fb38d",
            "ot_glass",
            "ot_glass_a.mat",
            20,
            HalfPositionLayout,
            16,
            16,
            16),
        new(
            "slums_cs_terrain_horizon_a",
            "030cccd52ecf3f59c54a696c6c0aa84aaf374ef48589656e40af7bc733b9d8fb",
            "slums_cs_terrain_horizon_a001",
            "horizon_town_constructions.mat",
            32,
            HorizonLayout,
            8,
            0,
            8),
        new(
            "slums_noise_barrier_destro_b",
            "bc728cfecfea850aa630fe14b5c8ad4e06cef0810f0d6b269ce17ffd8bd3ee55",
            "slums_noise_barrier_destro",
            "slums_noise_barrier_a.mat",
            20,
            HalfPositionLayout,
            6,
            6,
            0),
    ];

    public static bool TryGetRawGpuNonFiniteUv0Vertices(
        Dl1MeshData mesh,
        Dl1MeshSurface surface,
        out HashSet<ushort> vertexIndexes,
        out string policyLabel)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(surface);
        vertexIndexes = [];
        policyLabel = string.Empty;
        if (mesh.GeometryProvenance is null ||
            mesh.Surfaces.Count != 1)
        {
            return false;
        }

        StockUvProfile? profile = StockUvProfiles.FirstOrDefault(
            candidate =>
                string.Equals(
                    mesh.ResourceName,
                    candidate.ResourceName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    mesh.GeometryProvenance.LengthDelimitedSha256,
                    candidate.GeometryFingerprint,
                    StringComparison.OrdinalIgnoreCase));
        if (profile is null ||
            !string.Equals(
                surface.Name,
                profile.SurfaceName,
                StringComparison.Ordinal) ||
            surface.LodIndex != 0 ||
            !MatchesLayout(
                surface.VertexLayout,
                profile))
        {
            return false;
        }

        HashSet<ushort> referenced = surface.Indices.ToHashSet();
        int positiveInfinityComponents = 0;
        int negativeInfinityComponents = 0;
        foreach (ushort vertexIndex in referenced)
        {
            if (vertexIndex >= surface.Vertices.Count)
            {
                return false;
            }

            System.Numerics.Vector2 uv =
                surface.Vertices[vertexIndex].TextureCoordinate0;
            if (float.IsFinite(uv.X) &&
                float.IsFinite(uv.Y))
            {
                continue;
            }

            if (!CountInfinity(
                    uv.X,
                    ref positiveInfinityComponents,
                    ref negativeInfinityComponents) ||
                !CountInfinity(
                    uv.Y,
                    ref positiveInfinityComponents,
                    ref negativeInfinityComponents))
            {
                return false;
            }

            vertexIndexes.Add(vertexIndex);
        }

        if (vertexIndexes.Count !=
                profile.NonFiniteVertexCount ||
            positiveInfinityComponents !=
                profile.PositiveInfinityComponentCount ||
            negativeInfinityComponents !=
                profile.NegativeInfinityComponentCount ||
            !HasExclusiveMaterialOwnership(
                mesh,
                surface,
                vertexIndexes,
                profile.MaterialName))
        {
            vertexIndexes.Clear();
            return false;
        }

        policyLabel =
            $"{profile.ResourceName}/{profile.SurfaceName} " +
            $"({profile.GeometryFingerprint})";
        return true;
    }

    private static bool CountInfinity(
        float value,
        ref int positiveInfinityComponents,
        ref int negativeInfinityComponents)
    {
        if (float.IsFinite(value))
        {
            return true;
        }

        if (float.IsPositiveInfinity(value))
        {
            positiveInfinityComponents++;
            return true;
        }

        if (float.IsNegativeInfinity(value))
        {
            negativeInfinityComponents++;
            return true;
        }

        return false;
    }

    private static bool MatchesLayout(
        Dl1VertexLayout layout,
        StockUvProfile profile)
    {
        if (layout.Stride != profile.Stride ||
            layout.Elements.Count !=
                profile.Elements.Count)
        {
            return false;
        }

        for (int index = 0;
             index < profile.Elements.Count;
             index++)
        {
            Dl1VertexElement actual = layout.Elements[index];
            VertexElementProfile expected =
                profile.Elements[index];
            if (actual.Semantic != expected.Semantic ||
                actual.SemanticIndex != expected.SemanticIndex ||
                actual.Format != expected.Format ||
                actual.StreamIndex != 0 ||
                actual.ByteOffset != expected.ByteOffset)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExclusiveMaterialOwnership(
        Dl1MeshData mesh,
        Dl1MeshSurface surface,
        HashSet<ushort> nonFiniteVertices,
        string expectedMaterialName)
    {
        if (surface.Submeshes.Count == 0)
        {
            return IsExpectedMaterial(
                mesh,
                surface.MaterialSlotIndex,
                expectedMaterialName);
        }

        HashSet<int> badReferenceOffsets = [];
        for (int offset = 0;
             offset < surface.Indices.Count;
             offset++)
        {
            if (nonFiniteVertices.Contains(
                    surface.Indices[offset]))
            {
                badReferenceOffsets.Add(offset);
            }
        }

        HashSet<int> coveredBadReferenceOffsets = [];
        foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
        {
            if (submesh.FirstIndex < 0 ||
                submesh.IndexCount <= 0 ||
                submesh.FirstIndex >
                    surface.Indices.Count ||
                submesh.IndexCount >
                    surface.Indices.Count -
                    submesh.FirstIndex)
            {
                return false;
            }

            for (int offset = submesh.FirstIndex;
                 offset <
                 submesh.FirstIndex + submesh.IndexCount;
                 offset++)
            {
                if (!nonFiniteVertices.Contains(
                        surface.Indices[offset]))
                {
                    continue;
                }

                if (!IsExpectedMaterial(
                        mesh,
                        submesh.MaterialSlotIndex,
                        expectedMaterialName))
                {
                    return false;
                }

                coveredBadReferenceOffsets.Add(offset);
            }
        }

        return badReferenceOffsets.SetEquals(
            coveredBadReferenceOffsets);
    }

    private static bool IsExpectedMaterial(
        Dl1MeshData mesh,
        int materialSlotIndex,
        string expectedMaterialName) =>
        string.Equals(
            mesh.MaterialSlots.FirstOrDefault(
                slot => slot.Index == materialSlotIndex)
                ?.DatabaseName,
            expectedMaterialName,
            StringComparison.Ordinal);

    private sealed record StockUvProfile(
        string ResourceName,
        string GeometryFingerprint,
        string SurfaceName,
        string MaterialName,
        int Stride,
        IReadOnlyList<VertexElementProfile> Elements,
        int NonFiniteVertexCount,
        int PositiveInfinityComponentCount,
        int NegativeInfinityComponentCount);

    private sealed record VertexElementProfile(
        Dl1VertexSemantic Semantic,
        int SemanticIndex,
        Dl1VertexElementFormat Format,
        int ByteOffset);
}
