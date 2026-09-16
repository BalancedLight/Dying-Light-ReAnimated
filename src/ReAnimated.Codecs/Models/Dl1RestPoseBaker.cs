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

            TransformMatrix[] paletteTransforms = surface.PaletteBoneIndices
                .Select(index => index >= 0 && index < transforms.Length
                    ? transforms[index]
                    : TransformMatrix.Identity)
                .ToArray();
            foreach (FbxModelVertex vertex in surface.Vertices)
                _ = HasUsableInfluence(vertex, surface.PaletteBoneIndices, transforms);
            FbxModelSurface posed = FbxSurfacePoseBaker.BakeLegacy(surface, paletteTransforms, cancellationToken);
            for (int index = 0; index < surface.Vertices.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FbxModelVertex vertex = surface.Vertices[index];
                if (!HasUsableInfluence(vertex, surface.PaletteBoneIndices, transforms))
                {
                    unweighted++;
                    continue;
                }
                double moved = Vector3D.Distance(vertex.Position, posed.Vertices[index].Position);
                maximum = Math.Max(maximum, moved);
                total += moved;
                transformed++;
            }
            baked.Add(posed);
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

    private static bool HasUsableInfluence(
        FbxModelVertex vertex, ImmutableArray<int> palette,
        ImmutableArray<TransformMatrix> transforms)
    {
        double total = 0;
        int influences = Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length);
        for (int index = 0; index < influences; index++)
        {
            int slot = vertex.BoneIndices[index];
            double weight = vertex.BoneWeights[index];
            if (weight <= 0 || (uint)slot >= (uint)palette.Length) continue;
            int source = palette[slot];
            if ((uint)source >= (uint)transforms.Length)
                throw new InvalidDataException($"A skin palette references unknown source bone {source}.");
            total += weight;
        }
        return double.IsFinite(total) && total > 0;
    }
}
