using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Checks whether a published cloth script can address the compiled rig and whether its
/// movable grid nodes influence any rendered vertices. This does not prove Player creates
/// or advances a native cloth instance.
/// </summary>
public static class Dl1CompiledClothReadBackValidator
{
    public static ImmutableArray<string> Validate(
        CompactMeshDocument hierarchy,
        CompiledMeshGeometryDocument geometry,
        IEnumerable<(string ResourceName, NativePhxDocument Document)> clothSources)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(clothSources);

        var exactNames = hierarchy.Entities.ToDictionary(entity => entity.Name, entity => entity.Index,
            StringComparer.Ordinal);
        var foldedNames = hierarchy.Entities.GroupBy(entity => entity.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.Index).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        HashSet<int> weightedEntities = CollectWeightedEntityIndexes(geometry);
        var warnings = ImmutableArray.CreateBuilder<string>();

        foreach ((string resourceName, NativePhxDocument document) in clothSources)
        {
            if (!document.IsValid)
                throw new InvalidDataException($"Compiled cloth read-back received invalid PHX '{resourceName}'.");
            var movableEntities = new HashSet<int>();
            foreach (NativeClothNode node in document.Nodes.Where(node => node.BoneName.Length > 0))
            {
                if (!exactNames.TryGetValue(node.BoneName, out int entityIndex))
                {
                    if (!foldedNames.TryGetValue(node.BoneName, out int[]? folded) || folded.Length != 1)
                        throw new InvalidDataException($"Native PHX '{resourceName}' refers to grid bone '{node.BoneName}' absent from the compiled mesh hierarchy.");
                    entityIndex = folded[0];
                    warnings.Add($"Native PHX '{resourceName}' grid bone '{node.BoneName}' differs in case from compiled '{hierarchy.Entities[entityIndex].Name}'. Check the native binding in Player; source-name validation alone cannot establish it.");
                }
                if (node.Type == 0) movableEntities.Add(entityIndex);
            }

            if (movableEntities.Count > 0 && !movableEntities.Overlaps(weightedEntities))
                warnings.Add($"Native PHX '{resourceName}' has {movableEntities.Count} movable compiled grid bone(s), but none influence a rendered vertex in the compiled mesh. This garment cannot show bone-driven movement until its skin weights or PHX grid are corrected.");
        }
        return warnings.Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    private static HashSet<int> CollectWeightedEntityIndexes(CompiledMeshGeometryDocument geometry)
    {
        var weighted = new HashSet<int>();
        foreach (CompiledMeshSurface surface in geometry.Surfaces)
        foreach (CompiledMeshSubmesh submesh in surface.Submeshes)
        {
            int end = (int)Math.Max(0, Math.Min(surface.Indices.Count,
                (long)submesh.FirstIndex + submesh.IndexCount));
            for (int index = Math.Max(0, submesh.FirstIndex); index < end; index++)
            {
                int vertexIndex = surface.Indices[index];
                if ((uint)vertexIndex >= (uint)surface.Vertices.Count) continue;
                CompiledVertex vertex = surface.Vertices[vertexIndex];
                Span<byte> locals = [vertex.LocalBlendIndices.X, vertex.LocalBlendIndices.Y,
                    vertex.LocalBlendIndices.Z, vertex.LocalBlendIndices.W];
                Span<float> weights = [vertex.BlendWeights.X, vertex.BlendWeights.Y,
                    vertex.BlendWeights.Z, vertex.BlendWeights.W];
                for (int slot = 0; slot < 4; slot++)
                {
                    if (weights[slot] <= 0 || locals[slot] >= submesh.BonePaletteEntityIndexes.Count) continue;
                    int entityIndex = submesh.BonePaletteEntityIndexes[locals[slot]];
                    if (entityIndex >= 0) weighted.Add(entityIndex);
                }
            }
        }
        return weighted;
    }
}
