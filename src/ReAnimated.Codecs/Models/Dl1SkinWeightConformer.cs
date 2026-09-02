using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// What the weight transfer did, so the wizard can report it instead of
/// silently changing a model's deformation.
/// </summary>
public sealed record Dl1SkinConformanceReport
{
    public required int SurfaceCount { get; init; }

    public required int RemappedPaletteEntries { get; init; }

    /// <summary>
    /// Source bones whose weights were folded into a surviving ancestor
    /// because the bone itself was dropped.
    /// </summary>
    public required ImmutableArray<string> FoldedBones { get; init; }

    /// <summary>
    /// Vertices that lost at least one influence to the influence cap after
    /// folding merged several source bones onto one emitted bone.
    /// </summary>
    public required int VerticesTruncatedByInfluenceCap { get; init; }

    /// <summary>
    /// Largest weight discarded by the influence cap on any single vertex,
    /// before renormalization. A large value means folding materially changed
    /// that vertex's deformation.
    /// </summary>
    public required double LargestDiscardedWeight { get; init; }

    public required int VerticesLeftUnweighted { get; init; }
}

public sealed record Dl1SkinConformanceResult(
    ImmutableArray<FbxModelSurface> Surfaces,
    Dl1SkinConformanceReport Report);

/// <summary>
/// Rewrites imported skin bindings so they address the conformed DL1 rig.
/// </summary>
/// <remarks>
/// Weights are never re-projected onto nearby geometry. Every vertex keeps the
/// influences the artist authored; only the bone each influence names changes.
/// Where a dropped bone folds into a surviving ancestor the two weights are
/// summed, which is the only case where a vertex's effective binding changes,
/// and it is reported.
/// </remarks>
public static class Dl1SkinWeightConformer
{
    /// <summary>Matches the importer's per-vertex influence contract.</summary>
    private const int MaximumInfluencesPerVertex = 4;

    private const int MaximumPaletteEntries = 256;

    /// <summary>
    /// Mirrors <c>Dl1CustomModelRigPreparer</c>: the validated source-MSH
    /// writer retains i16 weights from 2 upward.
    /// </summary>
    private const double MeaningfulWeightThreshold = 8.0 / 32_767.0;

    public static Dl1SkinConformanceResult Conform(
        ImmutableArray<FbxModelSurface> surfaces,
        RigDefinition sourceRig,
        RigConformanceResult fit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(fit);
        if (surfaces.IsDefault)
        {
            throw new ArgumentException("The surface set must be initialized.", nameof(surfaces));
        }

        ImmutableArray<int> sourceToEmitted = BuildSourceToEmitted(sourceRig, fit);
        var foldedBones = ImmutableArray.CreateBuilder<string>();
        for (int index = 0; index < sourceToEmitted.Length; index++)
        {
            int emitted = sourceToEmitted[index];
            if (emitted >= 0 && fit.Bones[emitted].SourceBoneIndex != index)
            {
                foldedBones.Add(sourceRig.Bones[index].Name);
            }
        }

        var conformed = ImmutableArray.CreateBuilder<FbxModelSurface>(surfaces.Length);
        int remappedEntries = 0;
        int truncated = 0;
        int unweighted = 0;
        double largestDiscarded = 0.0;

        foreach (FbxModelSurface surface in surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!surface.IsSkinned || surface.PaletteBoneIndices.IsDefaultOrEmpty)
            {
                conformed.Add(surface);
                continue;
            }

            // Collapse the palette: several source bones can land on one
            // emitted bone once dropped rows fold into their ancestors.
            var emittedPalette = ImmutableArray.CreateBuilder<int>();
            var paletteSlotByEmitted = new Dictionary<int, int>();
            var slotForOldEntry = new int[surface.PaletteBoneIndices.Length];
            for (int entry = 0; entry < surface.PaletteBoneIndices.Length; entry++)
            {
                int sourceIndex = surface.PaletteBoneIndices[entry];
                if ((uint)sourceIndex >= (uint)sourceToEmitted.Length)
                {
                    throw new InvalidDataException(
                        $"Surface '{surface.Id}' references unknown source bone {sourceIndex}.");
                }

                int emitted = sourceToEmitted[sourceIndex];
                if (emitted < 0)
                {
                    slotForOldEntry[entry] = -1;
                    continue;
                }

                if (!paletteSlotByEmitted.TryGetValue(emitted, out int slot))
                {
                    slot = emittedPalette.Count;
                    paletteSlotByEmitted.Add(emitted, slot);
                    emittedPalette.Add(emitted);
                }

                slotForOldEntry[entry] = slot;
            }

            if (emittedPalette.Count > MaximumPaletteEntries)
            {
                throw new InvalidDataException(
                    $"Surface '{surface.Id}' needs {emittedPalette.Count} palette entries after conformance, " +
                    $"above the DL1 limit of {MaximumPaletteEntries}.");
            }

            remappedEntries += emittedPalette.Count;
            var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>(surface.Vertices.Length);
            var accumulator = new Dictionary<int, double>();
            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulator.Clear();
                int influences = Math.Min(
                    vertex.BoneIndices.Length,
                    vertex.BoneWeights.Length);
                for (int influence = 0; influence < influences; influence++)
                {
                    int oldSlot = vertex.BoneIndices[influence];
                    double weight = vertex.BoneWeights[influence];
                    if (weight <= 0.0 ||
                        (uint)oldSlot >= (uint)slotForOldEntry.Length)
                    {
                        continue;
                    }

                    int slot = slotForOldEntry[oldSlot];
                    if (slot < 0)
                    {
                        continue;
                    }

                    // Summing is what makes a folded bone's deformation carry
                    // over to the ancestor that absorbed it.
                    accumulator[slot] = accumulator.TryGetValue(slot, out double existing)
                        ? existing + weight
                        : weight;
                }

                (ImmutableArray<int> indices, ImmutableArray<double> weights, double discarded) =
                    Normalize(accumulator);
                if (discarded > largestDiscarded)
                {
                    largestDiscarded = discarded;
                }

                if (discarded > 0.0)
                {
                    truncated++;
                }

                if (indices.IsEmpty)
                {
                    unweighted++;
                }

                vertices.Add(vertex with
                {
                    BoneIndices = indices,
                    BoneWeights = weights,
                });
            }

            conformed.Add(surface with
            {
                Vertices = vertices.MoveToImmutable(),
                PaletteBoneIndices = emittedPalette.ToImmutable(),
                // Inverse binds are re-derived from the emitted hierarchy by
                // Dl1CustomModelRigPreparer, which owns the Chrome frame
                // authoring. Carrying stale source matrices here would be
                // silently wrong, so they are cleared.
                InverseBindMatrices = [],
            });
        }

        return new Dl1SkinConformanceResult(
            conformed.MoveToImmutable(),
            new Dl1SkinConformanceReport
            {
                SurfaceCount = surfaces.Length,
                RemappedPaletteEntries = remappedEntries,
                FoldedBones = foldedBones.ToImmutable(),
                VerticesTruncatedByInfluenceCap = truncated,
                LargestDiscardedWeight = largestDiscarded,
                VerticesLeftUnweighted = unweighted,
            });
    }

    /// <summary>
    /// Sorts influences by descending weight, drops sub-threshold rows, caps
    /// the count, and renormalizes to sum one.
    /// </summary>
    private static (ImmutableArray<int> Indices, ImmutableArray<double> Weights, double Discarded)
        Normalize(Dictionary<int, double> accumulator)
    {
        if (accumulator.Count == 0)
        {
            return ([], [], 0.0);
        }

        (int Slot, double Weight)[] ordered = accumulator
            .Where(static pair => pair.Value > MeaningfulWeightThreshold)
            .Select(static pair => (Slot: pair.Key, Weight: pair.Value))
            .OrderByDescending(static pair => pair.Weight)
            .ThenBy(static pair => pair.Slot)
            .ToArray();
        if (ordered.Length == 0)
        {
            return ([], [], 0.0);
        }

        double discarded = 0.0;
        if (ordered.Length > MaximumInfluencesPerVertex)
        {
            for (int index = MaximumInfluencesPerVertex; index < ordered.Length; index++)
            {
                discarded += ordered[index].Weight;
            }

            ordered = ordered[..MaximumInfluencesPerVertex];
        }

        double total = ordered.Sum(static pair => pair.Weight);
        if (total <= 0.0)
        {
            return ([], [], discarded);
        }

        var indices = ImmutableArray.CreateBuilder<int>(ordered.Length);
        var weights = ImmutableArray.CreateBuilder<double>(ordered.Length);
        foreach ((int slot, double weight) in ordered)
        {
            indices.Add(slot);
            weights.Add(weight / total);
        }

        return (indices.MoveToImmutable(), weights.MoveToImmutable(), discarded);
    }

    /// <summary>
    /// Maps every source bone to the emitted bone that now carries its weights.
    /// A dropped bone resolves to its nearest surviving source ancestor.
    /// </summary>
    private static ImmutableArray<int> BuildSourceToEmitted(
        RigDefinition sourceRig,
        RigConformanceResult fit)
    {
        var direct = new int[sourceRig.BoneCount];
        Array.Fill(direct, -1);
        foreach (RigConformedBone bone in fit.Bones)
        {
            if (bone.SourceBoneIndex >= 0 &&
                bone.SourceBoneIndex < direct.Length)
            {
                direct[bone.SourceBoneIndex] = bone.Index;
            }
        }

        var resolved = ImmutableArray.CreateBuilder<int>(sourceRig.BoneCount);
        for (int index = 0; index < sourceRig.BoneCount; index++)
        {
            int current = index;
            while (current >= 0 && direct[current] < 0)
            {
                current = sourceRig.Bones[current].ParentIndex;
            }

            resolved.Add(current >= 0 ? direct[current] : -1);
        }

        return resolved.MoveToImmutable();
    }
}
