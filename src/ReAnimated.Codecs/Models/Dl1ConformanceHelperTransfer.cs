using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// The conformed base table plus the retained authored-helper layer and the
/// maps required to remap palettes and source identities.
/// </summary>
public sealed record Dl1ConformanceHierarchy(
    ImmutableArray<CustomModelBone> Bones,
    ImmutableArray<CustomModelAuthoredHelper> Helpers,
    ImmutableArray<int> FitToEffective,
    ImmutableArray<int> SourceToEffective,
    ImmutableArray<TransformMatrix> EffectiveGlobals,
    ImmutableArray<string> Notes);

/// <summary>
/// Projects a conformance fit onto the split custom-model representation. The
/// fit owns the base-bone result; authored helpers remain authored helpers and
/// are transported through the source rest-pose transfer.
/// </summary>
public static class Dl1ConformanceHelperTransfer
{
    public static Dl1ConformanceHierarchy Build(
        CustomModelDocument document,
        RigConformanceResult fit,
        ImmutableArray<CustomModelBone> fitBones,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(fit);
        document.Validate();
        token.ThrowIfCancellationRequested();

        if (fitBones.IsDefault)
            throw new ArgumentException("The fit bone table is default/uninitialized.", nameof(fitBones));
        if (fitBones.Length != fit.Bones.Length)
            throw new InvalidDataException($"The fit metadata ({fit.Bones.Length}) and fit bone table ({fitBones.Length}) have different lengths.");

        int baseCount = document.Bones.Length;
        int effectiveSourceCount = baseCount + document.AuthoredHelpers.Length;
        if (fit.RestPoseTransfer.SkinningTransforms.Length != effectiveSourceCount)
            throw new InvalidDataException($"The rest-pose transfer has {fit.RestPoseTransfer.SkinningTransforms.Length} transforms, but the source effective hierarchy has {effectiveSourceCount} rows.");

        ValidateFitTable(fit, fitBones, effectiveSourceCount, token);

        if (document.AuthoredHelpers.IsEmpty)
            return BuildWithoutHelpers(document, fit, fitBones, token);

        var sourceToFit = new Dictionary<int, int>();
        for (int index = 0; index < fitBones.Length; index++)
        {
            int source = fit.Bones[index].SourceBoneIndex;
            if (source < 0) continue;
            if (!sourceToFit.TryAdd(source, index))
                throw new InvalidDataException($"Source row {source} is represented by multiple fit rows ({sourceToFit[source]} and {index}); helper projection is ambiguous.");
        }

        var helperFitRows = new Dictionary<int, int>();
        for (int index = 0; index < fitBones.Length; index++)
        {
            int source = fit.Bones[index].SourceBoneIndex;
            if (source < baseCount) continue;
            if (source >= effectiveSourceCount)
                throw new InvalidDataException($"Fit row {index} references source row {source} outside the source effective hierarchy.");

            RigConformedBone metadata = fit.Bones[index];
            if (metadata.IsDeform || metadata.Kind == BoneKind.Deform)
                throw new InvalidDataException($"Authored helper source row {source} was promoted to a deform row by fit row {index}; the split document cannot represent that promotion.");
            helperFitRows.Add(source, index);
        }

        var baseBones = ImmutableArray.CreateBuilder<CustomModelBone>();
        var fitToEffective = new int[fitBones.Length];
        Array.Fill(fitToEffective, -1);
        var fitToBase = new Dictionary<int, int>();
        for (int fitIndex = 0; fitIndex < fitBones.Length; fitIndex++)
        {
            token.ThrowIfCancellationRequested();
            int source = fit.Bones[fitIndex].SourceBoneIndex;
            if (source >= baseCount) continue;

            int outputIndex = baseBones.Count;
            fitToBase.Add(fitIndex, outputIndex);
            fitToEffective[fitIndex] = outputIndex;
            CustomModelBone row = fitBones[fitIndex] with { Index = outputIndex };
            baseBones.Add(row);
        }

        // A base row cannot be parented to an authored helper because the
        // current document stores all imported bones before all authored rows.
        for (int fitIndex = 0; fitIndex < fitBones.Length; fitIndex++)
        {
            if (fit.Bones[fitIndex].SourceBoneIndex >= baseCount) continue;
            int parent = fitBones[fitIndex].ParentIndex;
            if ((uint)parent >= (uint)fitBones.Length) continue;
            if (fit.Bones[parent].SourceBoneIndex >= baseCount)
                throw new InvalidDataException($"Base fit row {fitIndex} is parented to authored-helper fit row {parent}; the split document cannot represent a base-to-helper parent.");
            if (!fitToBase.ContainsKey(parent) && parent >= 0)
                throw new InvalidDataException($"Base fit row {fitIndex} has an unmappable parent fit row {parent}.");
        }

        // Rebuild base parent indices after helper rows have been removed.
        var rebuiltBase = baseBones.ToArray();
        for (int fitIndex = 0; fitIndex < fitBones.Length; fitIndex++)
        {
            if (fit.Bones[fitIndex].SourceBoneIndex >= baseCount) continue;
            int outputIndex = fitToBase[fitIndex];
            int parent = fitBones[fitIndex].ParentIndex;
            int outputParent = parent < 0 ? -1 : fitToBase[parent];
            rebuiltBase[outputIndex] = rebuiltBase[outputIndex] with { ParentIndex = outputParent };
        }

        var baseGlobals = ComputeGlobals(rebuiltBase, "conformed base");
        int outputBaseCount = rebuiltBase.Length;
        var outputGlobals = baseGlobals.ToList();
        var sourceGlobals = ComputeGlobals(document.CreateEffectiveBones(), "source hierarchy");
        var helperDescriptors = new List<HelperDescriptor>(document.AuthoredHelpers.Length);
        var baseNames = rebuiltBase.Select(static bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var helperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int helperOffset = 0; helperOffset < document.AuthoredHelpers.Length; helperOffset++)
        {
            token.ThrowIfCancellationRequested();
            int sourceIndex = baseCount + helperOffset;
            CustomModelAuthoredHelper original = document.AuthoredHelpers[helperOffset];
            TransformMatrix oldGlobal = sourceGlobals[sourceIndex];
            TransformMatrix transported = fit.RestPoseTransfer.SkinningTransforms[sourceIndex] * oldGlobal;
            EnsureFrame(transported, $"helper '{original.Name}' transported global");

            if (helperFitRows.TryGetValue(sourceIndex, out int fitIndex))
            {
                int parentFitIndex = fitBones[fitIndex].ParentIndex;
                string fitName = fitBones[fitIndex].Name;
                if (baseNames.Contains(fitName) || !helperNames.Add(fitName))
                    throw new InvalidDataException($"Conformance helper name '{fitName}' collides with another effective node.");
                helperDescriptors.Add(new HelperDescriptor(
                    sourceIndex,
                    original,
                    fitName,
                    transported,
                    ResolveFitParent(parentFitIndex, fit.Bones, fitToBase, baseCount),
                    fitIndex,
                    fitIndex));
            }
            else
            {
                ParentReference parent = ResolveOriginalParent(document, original.ParentNodeIndex, baseCount, sourceToFit, fitToBase, helperFitRows);
                if (baseNames.Contains(original.Name) || !helperNames.Add(original.Name))
                    throw new InvalidDataException($"Authored helper name '{original.Name}' collides with another effective node.");
                helperDescriptors.Add(new HelperDescriptor(
                    sourceIndex,
                    original,
                    original.Name,
                    transported,
                    parent,
                    -1,
                    sourceIndex));
            }
        }

        var outputHelpers = new List<CustomModelAuthoredHelper>(helperDescriptors.Count);
        var helperOutputBySource = new Dictionary<int, int>();
        var pending = helperDescriptors.OrderBy(static h => h.OrderKey).ToList();
        var notes = new List<string>();
        foreach (HelperDescriptor descriptor in helperDescriptors.Where(h => h.FitIndex < 0))
            notes.Add($"Retained authored helper '{descriptor.Original.Name}' absent from fit; preserved its ID/kind and reparented it to the nearest mapped source ancestor where necessary.");

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            bool progressed = false;
            for (int index = 0; index < pending.Count; index++)
            {
                HelperDescriptor descriptor = pending[index];
                if (descriptor.Parent.HelperSourceIndex is { } parentSource && !helperOutputBySource.ContainsKey(parentSource))
                    continue;

                int parentEffective = descriptor.Parent.HelperSourceIndex is { } helperParent
                    ? outputBaseCount + helperOutputBySource[helperParent]
                    : descriptor.Parent.BaseEffectiveIndex ?? throw new InvalidDataException($"Helper '{descriptor.Original.Name}' has no representable parent.");
                TransformMatrix parentGlobal = outputGlobals[parentEffective];
                TransformMatrix local = parentGlobal.InvertedAffine() * descriptor.TransportedGlobal;
                EnsureFrame(local, $"helper '{descriptor.Original.Name}' local frame");
                TransformTRS trs = ReAnimated.Codecs.Fbx.FbxCoreAnimationAdapter.ProjectAffineToTrs(local, descriptor.Name, "conformance helper frame");
                if (!trs.IsFinite)
                    throw new InvalidDataException($"Helper '{descriptor.Original.Name}' projected local TRS is nonfinite.");

                int outputOffset = outputHelpers.Count;
                CustomModelAuthoredHelper helper = descriptor.Original with
                {
                    Name = descriptor.Name,
                    ParentNodeIndex = parentEffective,
                    LocalTransform = trs,
                    ExactLocalMatrix = local,
                };
                outputHelpers.Add(helper);
                outputGlobals.Add(descriptor.TransportedGlobal);
                helperOutputBySource.Add(descriptor.SourceIndex, outputOffset);
                if (descriptor.FitIndex >= 0)
                    fitToEffective[descriptor.FitIndex] = outputBaseCount + outputOffset;
                pending.RemoveAt(index);
                progressed = true;
                break;
            }

            if (!progressed)
                throw new InvalidDataException("Authored helper parent graph is cyclic or depends on an unavailable fit row.");
        }

        var sourceToEffective = new int[effectiveSourceCount];
        Array.Fill(sourceToEffective, -1);
        for (int fitIndex = 0; fitIndex < fitBones.Length; fitIndex++)
        {
            int source = fit.Bones[fitIndex].SourceBoneIndex;
            if (source >= 0 && source < baseCount && fitToEffective[fitIndex] >= 0)
                sourceToEffective[source] = fitToEffective[fitIndex];
        }
        foreach ((int source, int outputOffset) in helperOutputBySource)
            sourceToEffective[source] = outputBaseCount + outputOffset;


        var finalBones = rebuiltBase.ToImmutableArray();
        var finalHelpers = outputHelpers.ToImmutableArray();
        return new Dl1ConformanceHierarchy(
            finalBones,
            finalHelpers,
            fitToEffective.ToImmutableArray(),
            sourceToEffective.ToImmutableArray(),
            outputGlobals.ToImmutableArray(),
            notes.ToImmutableArray());
    }

    private static Dl1ConformanceHierarchy BuildWithoutHelpers(
        CustomModelDocument document,
        RigConformanceResult fit,
        ImmutableArray<CustomModelBone> fitBones,
        CancellationToken token)
    {
        var bones = ImmutableArray.CreateBuilder<CustomModelBone>(fitBones.Length);
        var fitToEffective = new int[fitBones.Length];
        Array.Fill(fitToEffective, -1);
        var sourceToEffective = new int[document.Bones.Length];
        Array.Fill(sourceToEffective, -1);
        for (int index = 0; index < fitBones.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            CustomModelBone row = fitBones[index] with { Index = index };
            fitToEffective[index] = index;
            bones.Add(row);
            int source = fit.Bones[index].SourceBoneIndex;
            if (source >= 0 && source < document.Bones.Length)
                sourceToEffective[source] = index;
        }
        return new Dl1ConformanceHierarchy(
            bones.MoveToImmutable(),
            [],
            fitToEffective.ToImmutableArray(),
            sourceToEffective.ToImmutableArray(),
            ComputeGlobals(fitBones, "fit hierarchy").ToImmutableArray(),
            []);
    }

    private static void ValidateFitTable(
        RigConformanceResult fit,
        ImmutableArray<CustomModelBone> fitBones,
        int sourceCount,
        CancellationToken token)
    {
        for (int index = 0; index < fitBones.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            CustomModelBone row = fitBones[index];
            if (row.Index != index)
                throw new InvalidDataException($"Fit row {index} has physical index {row.Index}; the projection requires contiguous fit rows.");
            if (row.ParentIndex >= index || row.ParentIndex < -1)
                throw new InvalidDataException($"Fit row {index} has invalid parent index {row.ParentIndex}.");
            int source = fit.Bones[index].SourceBoneIndex;
            if (source < -1 || source >= sourceCount)
                throw new InvalidDataException($"Fit row {index} has stale source index {source}.");
            if (!row.ExactLocalBindMatrix.IsFinite || !double.IsFinite(row.ExactLocalBindMatrix.LinearDeterminant) || row.ExactLocalBindMatrix.LinearDeterminant == 0)
                throw new InvalidDataException($"Fit row {index} has a nonfinite or singular exact local frame.");
        }
    }

    private static ParentReference ResolveFitParent(
        int parentFitIndex,
        ImmutableArray<RigConformedBone> fitMetadata,
        Dictionary<int, int> fitToBase,
        int baseCount)
    {
        if (parentFitIndex < 0)
            throw new InvalidDataException("A retained helper fit row has no parent; authored helpers require a concrete parent.");
        if ((uint)parentFitIndex >= (uint)fitMetadata.Length)
            throw new InvalidDataException($"A retained helper fit row references unavailable parent fit row {parentFitIndex}.");
        int source = fitMetadata[parentFitIndex].SourceBoneIndex;
        if (source >= baseCount)
            return new ParentReference(source, null);
        if (!fitToBase.TryGetValue(parentFitIndex, out int outputBase))
            throw new InvalidDataException($"A retained helper fit row references dropped base parent fit row {parentFitIndex}.");
        return new ParentReference(null, outputBase);
    }

    private static ParentReference ResolveOriginalParent(
        CustomModelDocument document,
        int parentNodeIndex,
        int baseCount,
        Dictionary<int, int> sourceToFit,
        Dictionary<int, int> fitToBase,
        Dictionary<int, int> helperFitRows)
    {
        if (parentNodeIndex < 0 || parentNodeIndex >= document.Bones.Length + document.AuthoredHelpers.Length)
            throw new InvalidDataException($"Authored helper parent index {parentNodeIndex} is outside the source effective hierarchy.");
        if (parentNodeIndex >= baseCount)
            return new ParentReference(parentNodeIndex, null);

        int current = parentNodeIndex;
        while (current >= 0)
        {
            if (sourceToFit.TryGetValue(current, out int fitIndex) && fitToBase.TryGetValue(fitIndex, out int outputBase))
                return new ParentReference(null, outputBase);
            current = document.Bones[current].ParentIndex;
        }
        throw new InvalidDataException($"Authored helper source parent {parentNodeIndex} has no mapped source ancestor in the fit.");
    }

    private static TransformMatrix[] ComputeGlobals(
        IReadOnlyList<CustomModelBone> bones,
        string label)
    {
        var globals = new TransformMatrix[bones.Count];
        for (int index = 0; index < bones.Count; index++)
        {
            int parent = bones[index].ParentIndex;
            globals[index] = parent < 0 ? bones[index].ExactLocalBindMatrix : globals[parent] * bones[index].ExactLocalBindMatrix;
            EnsureFrame(globals[index], $"{label} row {index}");
        }
        return globals;
    }

    private static void EnsureFrame(TransformMatrix matrix, string label)
    {
        if (!matrix.IsFinite || !double.IsFinite(matrix.LinearDeterminant) || matrix.LinearDeterminant == 0)
            throw new InvalidDataException($"{label} is nonfinite or singular.");
    }

    private sealed record ParentReference(int? HelperSourceIndex, int? BaseEffectiveIndex);

    private sealed record HelperDescriptor(
        int SourceIndex,
        CustomModelAuthoredHelper Original,
        string Name,
        TransformMatrix TransportedGlobal,
        ParentReference Parent,
        int FitIndex,
        int OrderKey);
}
