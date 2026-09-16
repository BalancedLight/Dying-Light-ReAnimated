using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Rebuilds draw-local palettes while retaining original source corner/triangle and morph identities.</summary>
internal static class FbxAuthoredSurfacePartitioner
{
    private const int MaximumPaletteEntries = 256;
    private const int MaximumVertices = 65_535;

    public static ImmutableArray<FbxModelSurface> Partition(FbxModelSurface surface, CancellationToken cancellationToken)
    {
        if (surface.Vertices.IsDefault || surface.Indices.IsDefault || surface.SourceCorners.Length != surface.Vertices.Length ||
            surface.SourceTriangles.Length * 3 != surface.Indices.Length || surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length ||
            surface.Indices.Any(i => i >= surface.Vertices.Length) || surface.Vertices.Any(v => v.BoneIndices.IsDefault || v.BoneWeights.IsDefault ||
                v.BoneIndices.Length != v.BoneWeights.Length || v.BoneIndices.Any(i => (uint)i >= (uint)surface.PaletteBoneIndices.Length)) ||
            surface.MorphTargets.Any(m => m.PositionDeltas.Length != surface.Vertices.Length || !m.NormalDeltas.IsDefaultOrEmpty && m.NormalDeltas.Length != surface.Vertices.Length))
            throw new InvalidDataException("Authored draw partition input has invalid source, palette, vertex or morph references.");
        var parts = new List<FbxModelSurface>();
        var usedVertices = new List<int>(); var localBySource = new Dictionary<int, int>();
        var palette = new List<int>(); var localByGlobal = new Dictionary<int, int>();
        var triangles = new List<int>(); var indices = new List<uint>();
        bool? skinned = null;
        for (int triangle = 0; triangle < surface.SourceTriangles.Length; triangle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] corners = [checked((int)surface.Indices[triangle * 3]), checked((int)surface.Indices[triangle * 3 + 1]), checked((int)surface.Indices[triangle * 3 + 2])];
            var bones = corners.SelectMany(i => surface.Vertices[i].BoneIndices.Select(slot => surface.PaletteBoneIndices[slot])).Distinct().Order().ToArray();
            bool triangleSkinned = bones.Length > 0;
            if (triangleSkinned && corners.Any(i => surface.Vertices[i].BoneIndices.IsEmpty))
                throw new InvalidDataException("A triangle mixes weighted and unweighted corners; complete its binding before publication.");
            if (bones.Length > MaximumPaletteEntries) throw new InvalidDataException("One authored triangle exceeds the native draw palette limit.");
            int addedVertices = corners.Distinct().Count(i => !localBySource.ContainsKey(i));
            if (triangles.Count > 0 && (skinned != triangleSkinned || palette.Count + bones.Count(b => !localByGlobal.ContainsKey(b)) > MaximumPaletteEntries ||
                usedVertices.Count + addedVertices > MaximumVertices)) Flush();
            skinned = triangleSkinned;
            foreach (int bone in bones)
                if (!localByGlobal.ContainsKey(bone)) { localByGlobal.Add(bone, palette.Count); palette.Add(bone); }
            foreach (int originalIndex in corners)
            {
                if (!localBySource.TryGetValue(originalIndex, out int local))
                { local = usedVertices.Count; usedVertices.Add(originalIndex); localBySource.Add(originalIndex, local); }
                indices.Add((uint)local);
            }
            triangles.Add(triangle);
        }
        Flush();
        if (parts.Count == 0) return [surface];
        if (parts.Count == 1) parts[0] = parts[0] with { Id = surface.Id };
        return parts.ToImmutableArray();

        void Flush()
        {
            if (triangles.Count == 0) return;
            var inverseByGlobal = surface.PaletteBoneIndices.Select((global, slot) => (global, slot)).ToDictionary(static p => p.global, static p => p.slot);
            var vertices = usedVertices.Select(i => surface.Vertices[i] with {
                BoneIndices = surface.Vertices[i].BoneIndices.Select(slot => localByGlobal[surface.PaletteBoneIndices[slot]]).ToImmutableArray(),
            }).ToImmutableArray();
            parts.Add(surface with {
                Id = surface.Id + ":authored:" + parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Vertices = vertices, Indices = indices.ToImmutableArray(), PaletteBoneIndices = palette.ToImmutableArray(),
                InverseBindMatrices = palette.Select(global => surface.InverseBindMatrices[inverseByGlobal[global]]).ToImmutableArray(), IsSkinned = skinned == true,
                SourceCorners = usedVertices.Select(i => surface.SourceCorners[i]).ToImmutableArray(),
                SourceTriangles = triangles.Select(i => surface.SourceTriangles[i]).ToImmutableArray(),
                MorphTargets = surface.MorphTargets.Select(m => m with {
                    PositionDeltas = usedVertices.Select(i => m.PositionDeltas[i]).ToImmutableArray(),
                    NormalDeltas = m.NormalDeltas.IsDefaultOrEmpty ? [] : usedVertices.Select(i => m.NormalDeltas[i]).ToImmutableArray(),
                }).ToImmutableArray(),
            });
            usedVertices.Clear(); localBySource.Clear(); palette.Clear(); localByGlobal.Clear(); triangles.Clear(); indices.Clear(); skinned = null;
        }
    }
}
