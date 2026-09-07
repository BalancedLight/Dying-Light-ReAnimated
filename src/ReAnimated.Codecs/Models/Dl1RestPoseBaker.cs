using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// What baking the rest-pose transfer did to the geometry.
/// </summary>
public sealed record Dl1RestPoseBakeReport
{
    /// <summary>Largest distance any vertex moved, in meters.</summary>
    public required double MaximumVertexDisplacement { get; init; }

    /// <summary>Mean distance moved across every skinned vertex.</summary>
    public required double MeanVertexDisplacement { get; init; }

    public required int TransformedVertices { get; init; }

    /// <summary>
    /// Vertices carrying no usable influence, which therefore could not be
    /// carried into the new pose and stayed where they were.
    /// </summary>
    public required int UnweightedVertices { get; init; }
}

public sealed record Dl1RestPoseBakeResult(
    ImmutableArray<FbxModelSurface> Surfaces,
    Dl1RestPoseBakeReport Report);

/// <summary>
/// Carries an imported mesh from its authored rest pose into the conformed
/// skeleton's.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the emitted bind pose be DL1's rest pose while the bones
/// still sit inside the geometry. Each vertex is moved by the linear blend of
/// its own influences' rigid transforms - ordinary skinning, evaluated once and
/// baked - so a joint bends exactly as it would if the artist had posed the
/// character themselves.
/// </para>
/// <para>
/// It runs on the imported bindings, before <see cref="Dl1SkinWeightConformer"/>
/// rewrites them, because the transforms are indexed by <em>source</em> bone.
/// </para>
/// </remarks>
public static class Dl1RestPoseBaker
{
    public static Dl1RestPoseBakeResult Bake(
        ImmutableArray<FbxModelSurface> surfaces,
        RigRestPoseTransferResult transfer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (surfaces.IsDefault)
        {
            throw new ArgumentException(
                "The surface set must be initialized.",
                nameof(surfaces));
        }

        ImmutableArray<TransformMatrix> transforms = transfer.SkinningTransforms;
        var baked = ImmutableArray.CreateBuilder<FbxModelSurface>(surfaces.Length);
        double maximum = 0.0;
        double total = 0.0;
        int transformed = 0;
        int unweighted = 0;

        foreach (FbxModelSurface surface in surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!surface.IsSkinned || surface.PaletteBoneIndices.IsDefaultOrEmpty)
            {
                baked.Add(surface);
                continue;
            }

            var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>(
                surface.Vertices.Length);
            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                if (!TryBlend(
                        vertex,
                        surface.PaletteBoneIndices,
                        transforms,
                        out Vector3D position,
                        out Vector3D normal))
                {
                    unweighted++;
                    vertices.Add(vertex);
                    continue;
                }

                double moved = Vector3D.Distance(vertex.Position, position);
                maximum = Math.Max(maximum, moved);
                total += moved;
                transformed++;
                vertices.Add(vertex with
                {
                    Position = position,
                    Normal = normal,
                });
            }

            baked.Add(surface with
            {
                Vertices = vertices.MoveToImmutable(),
                MorphTargets = BakeMorphTargets(
                    surface,
                    transforms,
                    cancellationToken),
            });
        }

        return new Dl1RestPoseBakeResult(
            baked.MoveToImmutable(),
            new Dl1RestPoseBakeReport
            {
                MaximumVertexDisplacement = maximum,
                MeanVertexDisplacement = transformed > 0 ? total / transformed : 0.0,
                TransformedVertices = transformed,
                UnweightedVertices = unweighted,
            });
    }

    /// <summary>
    /// Linear-blend skins one vertex through its influences. Normals use the
    /// rotation alone and are renormalized, since blended rotations do not stay
    /// unit length.
    /// </summary>
    private static bool TryBlend(
        FbxModelVertex vertex,
        ImmutableArray<int> palette,
        ImmutableArray<TransformMatrix> transforms,
        out Vector3D position,
        out Vector3D normal)
    {
        position = Vector3D.Zero;
        normal = Vector3D.Zero;
        double totalWeight = 0.0;
        int influences = Math.Min(
            vertex.BoneIndices.Length,
            vertex.BoneWeights.Length);

        for (int influence = 0; influence < influences; influence++)
        {
            int slot = vertex.BoneIndices[influence];
            double weight = vertex.BoneWeights[influence];
            if (weight <= 0.0 || (uint)slot >= (uint)palette.Length)
            {
                continue;
            }

            int sourceBone = palette[slot];
            if ((uint)sourceBone >= (uint)transforms.Length)
            {
                throw new InvalidDataException(
                    $"A skin palette references unknown source bone {sourceBone}.");
            }

            TransformMatrix transform = transforms[sourceBone];
            position += transform.TransformPoint(vertex.Position) * weight;
            normal += transform.TransformDirection(vertex.Normal) * weight;
            totalWeight += weight;
        }

        if (totalWeight <= 0.0 || !position.IsFinite)
        {
            return false;
        }

        position /= totalWeight;
        normal = normal.TryNormalize(out Vector3D unit)
            ? unit
            : vertex.Normal;
        return true;
    }

    /// <summary>
    /// Morph deltas live in the bind frame, so they rotate with the bone that
    /// drives their vertex. Translation is deliberately excluded - a delta is a
    /// displacement, not a point.
    /// </summary>
    private static ImmutableArray<FbxModelMorphTarget> BakeMorphTargets(
        FbxModelSurface surface,
        ImmutableArray<TransformMatrix> transforms,
        CancellationToken cancellationToken)
    {
        if (surface.MorphTargets.IsDefaultOrEmpty)
        {
            return surface.MorphTargets;
        }

        var baked = ImmutableArray.CreateBuilder<FbxModelMorphTarget>(
            surface.MorphTargets.Length);
        foreach (FbxModelMorphTarget morph in surface.MorphTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deltas = ImmutableArray.CreateBuilder<Vector3D>(
                morph.PositionDeltas.Length);
            var normalDeltas = ImmutableArray.CreateBuilder<Vector3D>(morph.NormalDeltas.Length);
            if (!morph.NormalDeltas.IsDefaultOrEmpty && morph.NormalDeltas.Length != surface.Vertices.Length)
                throw new InvalidDataException($"Morph '{morph.Name}' normal deltas do not match the surface vertex count.");
            for (int index = 0; index < morph.PositionDeltas.Length; index++)
            {
                deltas.Add(RotateDelta(
                    morph.PositionDeltas[index],
                    index < surface.Vertices.Length
                        ? surface.Vertices[index]
                        : null,
                    surface.PaletteBoneIndices,
                    transforms));
            }

            for (int index = 0; index < morph.NormalDeltas.Length; index++)
            {
                FbxModelVertex vertex = surface.Vertices[index];
                Vector3D baseNormal = RotateDelta(vertex.Normal, vertex, surface.PaletteBoneIndices, transforms);
                Vector3D targetNormal = RotateDelta(vertex.Normal + morph.NormalDeltas[index], vertex, surface.PaletteBoneIndices, transforms);
                if (!baseNormal.TryNormalize(out Vector3D normalizedBase) || !targetNormal.TryNormalize(out Vector3D normalizedTarget))
                    throw new InvalidDataException($"Morph '{morph.Name}' has an invalid normal after rest-pose transfer.");
                normalDeltas.Add(normalizedTarget - normalizedBase);
            }

            baked.Add(morph with { PositionDeltas = deltas.MoveToImmutable(), NormalDeltas = normalDeltas.MoveToImmutable() });
        }

        return baked.ToImmutable();
    }

    private static Vector3D RotateDelta(
        Vector3D delta,
        FbxModelVertex? vertex,
        ImmutableArray<int> palette,
        ImmutableArray<TransformMatrix> transforms)
    {
        if (vertex is not { } source)
        {
            return delta;
        }

        Vector3D rotated = Vector3D.Zero;
        double totalWeight = 0.0;
        int influences = Math.Min(
            source.BoneIndices.Length,
            source.BoneWeights.Length);
        for (int influence = 0; influence < influences; influence++)
        {
            int slot = source.BoneIndices[influence];
            double weight = source.BoneWeights[influence];
            if (weight <= 0.0 ||
                (uint)slot >= (uint)palette.Length ||
                (uint)palette[slot] >= (uint)transforms.Length)
            {
                continue;
            }

            rotated += transforms[palette[slot]].TransformDirection(delta) * weight;
            totalWeight += weight;
        }

        return totalWeight > 0.0 && rotated.IsFinite ? rotated / totalWeight : delta;
    }
}
