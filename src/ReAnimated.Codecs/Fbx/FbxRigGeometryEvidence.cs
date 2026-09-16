using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

/// <summary>
/// Captures CURRENT rest vertices, CURRENT palette weights and CURRENT exact bind frames for correspondence.
/// Original FBX provenance remains separate: it must not be paired with an already conformed rig.
/// </summary>
public static class FbxRigGeometryEvidence
{
    public static RigGeometryEvidence Build(FbxModelAuthoringImportResult model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Package.Document.Validate();
        var rig = model.Rig ?? throw new ArgumentException("Geometry correspondence requires a current rig.", nameof(model));
        var document = model.Package.Document;
        var bones = document.CreateEffectiveBones();
        if (bones.Length != rig.BoneCount) throw new InvalidDataException("Current model and rig inventories disagree.");
        if (document.RiggingSession is { } session && !string.Equals(session.SourceSha256, document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Current component decisions belong to another source revision.");
        var selections = document.RiggingSession?.Components.ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(bones.Length);
        foreach (var bone in bones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bone.Name != rig.Bones[bone.Index].Name || bone.ParentIndex != rig.Bones[bone.Index].ParentIndex ||
                !bone.LocalBindTransform.ToMatrix().NearlyEquals(rig.Bones[bone.Index].LocalBindPose.ToMatrix(), 1e-10))
                throw new InvalidDataException("Current geometry document and rig bind state disagree.");
            globals.Add(bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix);
        }
        var regions = new Dictionary<int, Accumulator>();
        long triangleCount = 0;
        foreach (var surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string component = surface.SourceGeometry?.Id ?? surface.Id;
            if (selections is not null)
            {
                if (!selections.TryGetValue(component, out var selection)) throw new InvalidDataException("Current geometry component decisions need reconciliation.");
                if (!selection.Included || !selection.UseForAnatomy) continue;
            }
            if (surface.Indices.Length % 3 != 0) throw new InvalidDataException("Current geometry contains an incomplete triangle.");
            triangleCount += surface.Indices.Length / 3;
            if (triangleCount > TriangleSpatialIndex.MaximumTriangleCount) throw new InvalidDataException("Current geometry exceeds correspondence triangle limits.");
            for (int offset = 0; offset < surface.Indices.Length; offset += 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint a = surface.Indices[offset], b = surface.Indices[offset + 1], c = surface.Indices[offset + 2];
                if (a >= surface.Vertices.Length || b >= surface.Vertices.Length || c >= surface.Vertices.Length)
                    throw new InvalidDataException("Current geometry triangle index is invalid.");
                Vector3D pa = surface.Vertices[(int)a].Position, pb = surface.Vertices[(int)b].Position, pc = surface.Vertices[(int)c].Position;
                if (!pa.IsFinite || !pb.IsFinite || !pc.IsFinite) throw new InvalidDataException("Current geometry is non-finite.");
                double area = Vector3D.Cross(pb - pa, pc - pa).Length / 2;
                if (!double.IsFinite(area)) throw new InvalidDataException("Current geometry area exceeds supported numeric range.");
                if (area <= 0) continue;
                Accumulate((int)a, area / 3);
                Accumulate((int)b, area / 3);
                Accumulate((int)c, area / 3);
            }
            void Accumulate(int vertexIndex, double area)
            {
                var vertex = surface.Vertices[vertexIndex];
                if (vertex.BoneIndices.Length != vertex.BoneWeights.Length) throw new InvalidDataException("Current skin weights do not match their palette indexes.");
                double total = vertex.BoneWeights.Sum();
                if (vertex.BoneWeights.Length > 0 && (!double.IsFinite(total) || total <= 0)) throw new InvalidDataException("Current skin weight total is invalid.");
                int point = surface.SourceCorners.Length == surface.Vertices.Length ? surface.SourceCorners[vertexIndex].ControlPointIndex : vertexIndex;
                string pointComponent = surface.SourceCorners.Length == surface.Vertices.Length ? component : surface.Id;
                for (int i = 0; i < vertex.BoneWeights.Length; i++)
                {
                    int paletteIndex = vertex.BoneIndices[i];
                    double weight = vertex.BoneWeights[i];
                    if ((uint)paletteIndex >= (uint)surface.PaletteBoneIndices.Length || !double.IsFinite(weight) || weight < 0)
                        throw new InvalidDataException("Current skin influence is invalid.");
                    int bone = surface.PaletteBoneIndices[paletteIndex];
                    if ((uint)bone >= (uint)rig.BoneCount) throw new InvalidDataException("Current skin palette refers to a missing bone.");
                    if (weight == 0) continue;
                    if (!regions.TryGetValue(bone, out var region)) regions.Add(bone, region = new());
                    region.Add(pointComponent, point, vertex.Position, area * (weight / total));
                }
            }
        }
        var evidence = new RigGeometryEvidence(document.Source.ContentSha256, "current-render-binding",
            bones.Select(static b => b.Name).ToImmutableArray(), bones.Select(static b => b.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(static b => b.LocalBindPose).ToImmutableArray(), globals.ToImmutable(),
            regions.OrderBy(static pair => pair.Key).Select(static pair => pair.Value.Finish(pair.Key)).ToImmutableArray());
        evidence.Validate(rig);
        return evidence;
    }

    private sealed class Accumulator
    {
        private readonly HashSet<(string Component, int Point)> _points = [];
        private double _mass;
        private Vector3D _centroid;
        private Vector3D _min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        private Vector3D _max = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        public void Add(string component, int point, Vector3D position, double mass)
        {
            if (point < 0 || !double.IsFinite(mass) || mass <= 0) throw new InvalidDataException("Current influence geometry is invalid.");
            _points.Add((component, point));
            double total = _mass + mass;
            if (!double.IsFinite(total)) throw new InvalidDataException("Current influence geometry mass overflowed.");
            _centroid += (position - _centroid) * (mass / total);
            _mass = total;
            _min = new(Math.Min(_min.X, position.X), Math.Min(_min.Y, position.Y), Math.Min(_min.Z, position.Z));
            _max = new(Math.Max(_max.X, position.X), Math.Max(_max.Y, position.Y), Math.Max(_max.Z, position.Z));
        }
        public RigBoneGeometrySupport Finish(int bone) => new(bone, _points.Count, _mass, _centroid, _min, _max);
    }
}
