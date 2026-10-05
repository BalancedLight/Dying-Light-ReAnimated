using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

/// <summary>Strict sculpt mapping through original control-point ordering; never nearest-point matching.</summary>
public static class ManualMorphShapeAuthoring
{
    public static ImmutableArray<Vector3D> ComputePositionDeltas(FbxModelSurface target, FbxModelSurface sculpt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sculpt);
        if ((target.SourceGeometry is null || sculpt.SourceGeometry is null) &&
            target.Vertices.Length == sculpt.Vertices.Length && target.Indices.SequenceEqual(sculpt.Indices))
            return sculpt.Vertices.Zip(target.Vertices).Select(p => p.First.Position - p.Second.Position).ToImmutableArray();
        if (target.SourceGeometry is null || sculpt.SourceGeometry is null ||
            target.SourceGeometry.ControlPoints.Length != sculpt.SourceGeometry.ControlPoints.Length ||
            target.SourceCorners.Length != target.Vertices.Length || sculpt.SourceCorners.Length != sculpt.Vertices.Length ||
            target.Indices.Length != sculpt.Indices.Length || target.Indices.Length % 3 != 0)
            throw new InvalidDataException("Manual sculpt must retain original control-point topology and ordering.");
        int pointCount = target.SourceGeometry.ControlPoints.Length;
        var positions = new Dictionary<int, Vector3D>();
        for (int i = 0; i < sculpt.Vertices.Length; i++)
        {
            int point = sculpt.SourceCorners[i].ControlPointIndex;
            Vector3D position = sculpt.Vertices[i].Position;
            if (point < 0 || point >= pointCount || !position.IsFinite ||
                positions.TryGetValue(point, out Vector3D previous) && (position - previous).Length > 1e-9)
                throw new InvalidDataException("Sculpt corners contain invalid or conflicting control-point positions.");
            positions[point] = position;
        }
        for (int i = 0; i < target.Indices.Length; i++)
        {
            uint targetVertex = target.Indices[i], sculptVertex = sculpt.Indices[i];
            if (targetVertex >= target.SourceCorners.Length || sculptVertex >= sculpt.SourceCorners.Length ||
                target.SourceCorners[(int)targetVertex].ControlPointIndex != sculpt.SourceCorners[(int)sculptVertex].ControlPointIndex)
                throw new InvalidDataException("Manual sculpt triangulation or control-point ordering differs from the target.");
        }
        var deltas = ImmutableArray.CreateBuilder<Vector3D>(target.Vertices.Length);
        for (int i = 0; i < target.Vertices.Length; i++)
        {
            int point = target.SourceCorners[i].ControlPointIndex;
            if (point < 0 || point >= pointCount || !positions.TryGetValue(point, out Vector3D position))
                throw new InvalidDataException("Manual sculpt does not supply every target control point.");
            deltas.Add(position - target.Vertices[i].Position);
        }
        return deltas.ToImmutable();
    }
}
