using System.Collections.Immutable;
using System.Globalization;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxModelLodLevelLayout(int LodIndex, ImmutableArray<int> SurfaceIndexes);
public sealed record FbxModelLodNodeLayout(string Name, ImmutableArray<FbxModelLodLevelLayout> Levels);

/// <summary>
/// Associates all imported draws with the exact retained FBX object graph.
/// Embedded source bytes restore this layout on package reopen; no suffix or
/// material-name matching is used to infer correspondence between levels.
/// </summary>
public static class FbxModelLodLayout
{
    public static ImmutableArray<FbxModelLodNodeLayout> Create(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SourceLodGroups.IsDefault || model.Surfaces.IsDefault)
            throw new InvalidDataException("Source LOD and draw collections must be initialized.");
        var nodes = ImmutableArray.CreateBuilder<FbxModelLodNodeLayout>();
        var claimed = new HashSet<int>();
        var components = model.Surfaces.Select((surface, index) => (surface, index))
            .Where(row => row.surface.SourceGeometry is not null)
            .GroupBy(row => row.surface.SourceGeometry!.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.index).ToImmutableArray(), StringComparer.Ordinal);
        foreach (var native in model.SourceCharacterLods)
        {
            var levels = ImmutableArray.CreateBuilder<FbxModelLodLevelLayout>();
            foreach (var level in native.Levels)
            {
                if (level.LodIndex != levels.Count || level.SurfaceIds.IsDefaultOrEmpty)
                    throw new InvalidDataException("Native LOD indices must be complete and contiguous.");
                var indexes = level.SurfaceIds.Select(id => Array.FindIndex(model.Surfaces.ToArray(), s => s.Id == id)).ToImmutableArray();
                if (indexes.Any(i => i < 0 || !claimed.Add(i))) throw new InvalidDataException("Native LOD references a missing or multiply owned surface.");
                levels.Add(new(level.LodIndex, indexes));
            }
            if (levels.Count == 0) throw new InvalidDataException("Native LOD group is empty.");
            nodes.Add(new(native.Name, levels.ToImmutable()));
        }
        foreach (FbxLodGroupEvidence group in model.SourceLodGroups)
        {
            if (group.Levels.IsDefaultOrEmpty)
                throw new InvalidDataException($"LOD group '{group.GroupName}' has no levels.");
            var levels = ImmutableArray.CreateBuilder<FbxModelLodLevelLayout>();
            foreach (FbxLodLevelEvidence level in group.Levels)
            {
                if (level.LevelIndex != levels.Count || level.Geometries.IsDefaultOrEmpty)
                    throw new InvalidDataException($"LOD group '{group.GroupName}' has missing or unordered level evidence.");
                var surfaces = ImmutableArray.CreateBuilder<int>();
                foreach (FbxLodGeometryEvidence geometry in level.Geometries)
                {
                    string componentId = string.Create(CultureInfo.InvariantCulture,
                        $"fbx:{geometry.OwnerModelId}:{geometry.GeometryObjectId}");
                    if (!components.TryGetValue(componentId, out ImmutableArray<int> draws) || draws.IsEmpty)
                        throw new InvalidDataException($"LOD group '{group.GroupName}' level {level.LevelIndex} has no imported draw for source component '{componentId}'.");
                    foreach (int index in draws)
                    {
                        if (!claimed.Add(index))
                            throw new InvalidDataException($"Draw '{model.Surfaces[index].Id}' belongs to more than one source LOD level.");
                        surfaces.Add(index);
                    }
                }
                levels.Add(new(level.LevelIndex, surfaces.ToImmutable()));
            }
            bool skinned = model.Surfaces[levels[0].SurfaceIndexes[0]].IsSkinned;
            if (levels.SelectMany(level => level.SurfaceIndexes).Any(index => model.Surfaces[index].IsSkinned != skinned))
                throw new InvalidDataException($"LOD group '{group.GroupName}' mixes static and skinned draws; a single native node type cannot preserve that group.");
            nodes.Add(new(group.GroupName, levels.ToImmutable()));
        }
        for (int index = 0; index < model.Surfaces.Length; index++)
        {
            if (!claimed.Contains(index))
                nodes.Add(new(model.Surfaces[index].MeshName, [new(0, [index])]));
        }
        return nodes.ToImmutable();
    }

    public static ImmutableHashSet<int> GetBaseSurfaceIndexes(FbxModelAuthoringImportResult model) =>
        Create(model).SelectMany(node => node.Levels[0].SurfaceIndexes).ToImmutableHashSet();
}
