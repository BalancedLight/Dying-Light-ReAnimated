using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

/// <summary>
/// Bakes draw-slot pose matrices into FBX base and morph geometry. Draw
/// identity, palettes, inverse binds and source provenance remain unchanged.
/// </summary>
public static class FbxSurfacePoseBaker
{
    public static FbxModelSurface Bake(FbxModelSurface surface,
        IReadOnlyList<TransformMatrix> paletteTransforms,
        CancellationToken cancellationToken = default)
        => BakeCore(surface, paletteTransforms, true, cancellationToken, out _);

    internal static FbxModelSurface BakeLegacy(FbxModelSurface surface,
        IReadOnlyList<TransformMatrix> paletteTransforms,
        CancellationToken cancellationToken = default)
        => BakeCore(surface, paletteTransforms, false, cancellationToken, out _);

    private static FbxModelSurface BakeCore(FbxModelSurface surface,
        IReadOnlyList<TransformMatrix> paletteTransforms, bool strict,
        CancellationToken cancellationToken, out bool[] validVertices)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(paletteTransforms);
        cancellationToken.ThrowIfCancellationRequested();
        validVertices = [];
        if (!surface.IsSkinned) return surface;
        if (surface.Vertices.IsDefault || surface.PaletteBoneIndices.IsDefault ||
            surface.PaletteBoneIndices.Length == 0 || paletteTransforms.Count != surface.PaletteBoneIndices.Length ||
            strict && surface.Vertices.Any(vertex => vertex.BoneIndices.IsDefault || vertex.BoneWeights.IsDefault ||
                vertex.BoneIndices.Length != vertex.BoneWeights.Length || vertex.BoneIndices.Length > 4))
            throw new InvalidDataException("A skinned draw has incomplete pose or influence data.");
        var normalTransforms = new TransformMatrix[paletteTransforms.Count];
        for (int slot = 0; slot < paletteTransforms.Count; slot++)
        {
            TransformMatrix transform = paletteTransforms[slot];
            if (!transform.IsFinite || !double.IsFinite(transform.LinearDeterminant) || transform.LinearDeterminant == 0 ||
                Math.Abs(transform.M41) > 1e-9 || Math.Abs(transform.M42) > 1e-9 || Math.Abs(transform.M43) > 1e-9 ||
                Math.Abs(transform.M44 - 1) > 1e-9)
                throw new InvalidDataException("A pose palette contains a non-finite or singular affine transform.");
            TransformMatrix inverse = transform.InvertedAffine();
            if (!inverse.IsFinite)
                throw new InvalidDataException("A pose palette inverse is non-finite.");
            normalTransforms[slot] = inverse;
        }

        var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>(surface.Vertices.Length);
        var evaluationVertices = surface.Vertices.ToArray();
        var normalScales = new double[surface.Vertices.Length];
        validVertices = new bool[surface.Vertices.Length];
        for (int index = 0; index < surface.Vertices.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxModelVertex vertex = surface.Vertices[index];
            if (!vertex.Position.IsFinite || !vertex.Normal.IsFinite)
                throw new InvalidDataException("A skinned draw contains a non-finite vertex or normal.");
            Vector3D position;
            Vector3D normal;
            double normalScale;
            if (strict)
            {
                (position, normal, normalScale) = Blend(vertex, paletteTransforms, normalTransforms);
            }
            else if (!TryBlendLegacy(vertex, surface.PaletteBoneIndices, paletteTransforms, normalTransforms,
                out position, out normal, out normalScale))
            {
                vertices.Add(vertex);
                continue;
            }
            validVertices[index] = true;
            if (!strict && (vertex.BoneIndices.Length != vertex.BoneWeights.Length ||
                vertex.BoneIndices.Any(slot => (uint)slot >= (uint)paletteTransforms.Count) || vertex.BoneWeights.Any(weight => weight <= 0)))
            {
                var usable = Enumerable.Range(0, Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length))
                    .Where(i => vertex.BoneWeights[i] > 0 && (uint)vertex.BoneIndices[i] < (uint)paletteTransforms.Count).ToArray();
                // Match the legacy base-vertex evaluation for morph deltas too,
                // without rewriting the published source influence arrays.
                evaluationVertices[index] = vertex with
                {
                    BoneIndices = usable.Select(i => vertex.BoneIndices[i]).ToImmutableArray(),
                    BoneWeights = usable.Select(i => vertex.BoneWeights[i]).ToImmutableArray(),
                };
            }
            normalScales[index] = normalScale;
            vertices.Add(vertex with { Position = position, Normal = normal });
        }

        var morphs = ImmutableArray.CreateBuilder<FbxModelMorphTarget>(surface.MorphTargets.Length);
        foreach (FbxModelMorphTarget morph in surface.MorphTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (strict && (morph.PositionDeltas.IsDefault || morph.PositionDeltas.Length != surface.Vertices.Length) ||
                strict &&
                !morph.PositionDeltas.All(static delta => delta.IsFinite) ||
                strict && !morph.NormalDeltas.IsDefaultOrEmpty && (morph.NormalDeltas.Length != surface.Vertices.Length || !morph.NormalDeltas.All(static delta => delta.IsFinite)) ||
                !strict && !morph.NormalDeltas.IsDefaultOrEmpty && morph.NormalDeltas.Length != surface.Vertices.Length)
                throw new InvalidDataException("A morph does not cover the skinned draw with finite deltas.");
            var positions = ImmutableArray.CreateBuilder<Vector3D>(morph.PositionDeltas.Length);
            var normals = morph.NormalDeltas.IsDefaultOrEmpty ? null : ImmutableArray.CreateBuilder<Vector3D>(morph.NormalDeltas.Length);
            for (int index = 0; index < morph.PositionDeltas.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Vector3D positionDelta;
                if (!strict && (index >= surface.Vertices.Length || !validVertices[index]))
                {
                    positionDelta = morph.PositionDeltas[index];
                }
                else
                {
                    FbxModelVertex vertex = evaluationVertices[index];
                    positionDelta = BlendDirection(vertex, morph.PositionDeltas[index], paletteTransforms);
                }
                if (!positionDelta.IsFinite)
                    throw new InvalidDataException("A baked morph position delta is non-finite.");
                positions.Add(positionDelta);
            }
            if (normals is not null)
            {
                for (int index = 0; index < morph.NormalDeltas.Length; index++)
                {
                    Vector3D normalDelta = !strict && !validVertices[index]
                        ? morph.NormalDeltas[index]
                        : BlendNormalDirection(evaluationVertices[index], morph.NormalDeltas[index], normalTransforms) / normalScales[index];
                    if (!normalDelta.IsFinite)
                        throw new InvalidDataException("A baked morph normal delta is non-finite.");
                    normals.Add(normalDelta);
                }
            }
            morphs.Add(morph with
            {
                PositionDeltas = positions.MoveToImmutable(),
                NormalDeltas = normals?.MoveToImmutable() ?? morph.NormalDeltas,
            });
        }
        return surface with { Vertices = vertices.MoveToImmutable(), MorphTargets = morphs.MoveToImmutable() };
    }

    private static bool TryBlendLegacy(FbxModelVertex vertex, ImmutableArray<int> palette,
        IReadOnlyList<TransformMatrix> positions, TransformMatrix[] normalTransforms,
        out Vector3D position, out Vector3D normal, out double normalScale)
    {
        position = Vector3D.Zero;
        normal = Vector3D.Zero;
        normalScale = 0;
        double total = 0;
        int influences = Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length);
        for (int i = 0; i < influences; i++)
        {
            int slot = vertex.BoneIndices[i];
            double weight = vertex.BoneWeights[i];
            if (weight <= 0 || (uint)slot >= (uint)palette.Length) continue;
            position += positions[slot].TransformPoint(vertex.Position) * weight;
            normal += ApplyInverseTranspose(normalTransforms[slot], vertex.Normal) * weight;
            total += weight;
        }
        if (!double.IsFinite(total) || total <= 0 || !position.IsFinite) return false;
        position /= total;
        normal /= total;
        normalScale = normal.Length;
        if (!double.IsFinite(normalScale) || normalScale <= 1e-12)
        {
            normal = vertex.Normal;
            normalScale = 1;
            return true;
        }
        normal /= normalScale;
        return normal.IsFinite;
    }

    private static (Vector3D Position, Vector3D Normal, double NormalScale) Blend(
        FbxModelVertex vertex, IReadOnlyList<TransformMatrix> positions,
        TransformMatrix[] normalTransforms)
    {
        double total = vertex.BoneWeights.Sum();
        if (!double.IsFinite(total) || total <= 1e-12 || vertex.BoneWeights.Any(static weight => !double.IsFinite(weight) || weight < 0))
            throw new InvalidDataException("A skinned vertex has no usable normalized positive influence.");
        Vector3D position = Vector3D.Zero;
        Vector3D normal = Vector3D.Zero;
        for (int i = 0; i < vertex.BoneIndices.Length; i++)
        {
            int slot = vertex.BoneIndices[i];
            if ((uint)slot >= (uint)positions.Count) throw new InvalidDataException("A vertex references a missing draw palette slot.");
            double weight = vertex.BoneWeights[i] / total;
            position += positions[slot].TransformPoint(vertex.Position) * weight;
            normal += ApplyInverseTranspose(normalTransforms[slot], vertex.Normal) * weight;
        }
        double scale = normal.Length;
        if (!double.IsFinite(scale) || scale <= 1e-12 || !position.IsFinite)
            throw new InvalidDataException("A pose palette collapses a base vertex normal or produces a non-finite position.");
        return (position, normal / scale, scale);
    }

    private static Vector3D BlendDirection(FbxModelVertex vertex, Vector3D direction,
        IReadOnlyList<TransformMatrix> transforms)
    {
        double total = vertex.BoneWeights.Sum();
        Vector3D result = Vector3D.Zero;
        for (int i = 0; i < vertex.BoneIndices.Length; i++)
            result += transforms[vertex.BoneIndices[i]].TransformDirection(direction) * (vertex.BoneWeights[i] / total);
        if (!result.IsFinite) throw new InvalidDataException("A morph position delta became non-finite during pose baking.");
        return result;
    }

    private static Vector3D BlendNormalDirection(FbxModelVertex vertex, Vector3D direction,
        TransformMatrix[] normalTransforms)
    {
        double total = vertex.BoneWeights.Sum();
        Vector3D result = Vector3D.Zero;
        for (int i = 0; i < vertex.BoneIndices.Length; i++)
            result += ApplyInverseTranspose(normalTransforms[vertex.BoneIndices[i]], direction) * (vertex.BoneWeights[i] / total);
        if (!result.IsFinite) throw new InvalidDataException("A morph normal delta became non-finite during pose baking.");
        return result;
    }

    private static Vector3D ApplyInverseTranspose(TransformMatrix inverse,
        Vector3D direction) => new(
        inverse.M11 * direction.X + inverse.M21 * direction.Y + inverse.M31 * direction.Z,
        inverse.M12 * direction.X + inverse.M22 * direction.Y + inverse.M32 * direction.Z,
        inverse.M13 * direction.X + inverse.M23 * direction.Y + inverse.M33 * direction.Z);
}
