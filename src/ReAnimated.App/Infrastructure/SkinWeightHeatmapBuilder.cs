using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

/// <summary>Uses a preview-only gradient texture; source UVs, materials, weights and morph streams stay untouched.</summary>
public static class SkinWeightHeatmapBuilder
{
    private static readonly TextureRenderData Gradient = CreateGradient();

    public static ImmutableArray<MeshRenderData> Build(FbxModelAuthoringImportResult model, IReadOnlyList<MeshRenderData> meshes,
        int sourceBoneIndex, SkinWeightAuthoringPreview? preview = null)
    {
        if ((uint)sourceBoneIndex >= (uint)model.Package.Document.Bones.Length || meshes.Count != model.Surfaces.Length)
            throw new ArgumentException("Weight heatmap requires the current source bone and prepared draw inventory.");
        var changed = new Dictionary<(string, int), double>();
        if (preview is not null)
        {
            Guid influence = preview.Influences.Single(b => b.BoneIndex == sourceBoneIndex).EntityId;
            foreach (var row in preview.Correction.Changes)
            {
                var point = preview.SourcePoints[row.PointIndex];
                changed.Add((point.ComponentId, point.ControlPointIndex), row.After.Where(w => w.HandleId == influence).Sum(static w => w.Weight));
            }
        }
        var result = ImmutableArray.CreateBuilder<MeshRenderData>(meshes.Count);
        for (int draw = 0; draw < meshes.Count; draw++)
        {
            var surface = model.Surfaces[draw];
            var mesh = meshes[draw];
            if (mesh.Vertices.Length != surface.Vertices.Length) throw new InvalidDataException("Weight heatmap source corner correspondence changed.");
            var vertices = mesh.Vertices.ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                var source = surface.Vertices[i];
                double weight = source.BoneIndices.Select((slot, j) => surface.PaletteBoneIndices[slot] == sourceBoneIndex ? source.BoneWeights[j] : 0).Sum();
                if (surface.SourceGeometry is { } geometry && surface.SourceCorners.Length == vertices.Length &&
                    changed.TryGetValue((geometry.Id, surface.SourceCorners[i].ControlPointIndex), out double replacement)) weight = replacement;
                var v = vertices[i];
                float u = (float)((Math.Clamp(weight, 0, 1) * 255 + .5) / 256);
                vertices[i] = new(v.Position, v.Normal, new(u, .5f), v.BoneWeights, v.BoneIndices);
            }
            result.Add(mesh with { Vertices = vertices, BaseColorTexture = Gradient, Tint = Vector4.One });
        }
        return result.MoveToImmutable();
    }

    private static TextureRenderData CreateGradient()
    {
        byte[] rgba = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            double weight = i / 255.0;
            rgba[i * 4] = (byte)Math.Round(255 * Math.Clamp(1 - weight * 2, 0, 1));
            rgba[i * 4 + 1] = (byte)Math.Round(255 * (1 - Math.Abs(weight * 2 - 1)));
            rgba[i * 4 + 2] = (byte)Math.Round(255 * Math.Clamp(weight * 2, 0, 1));
            rgba[i * 4 + 3] = 255;
        }
        return new("weight-inspection-gradient-v1", 256, 1, TextureRenderFormat.Bgra8Unorm, 1024, rgba);
    }
}
