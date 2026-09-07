using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1PreparedSkinSurface(
    ImmutableArray<int> PhysicalPalette,
    ImmutableArray<TransformMatrix> InverseBindMatrices);

/// <summary>
/// One immutable conversion from the imported FBX rig to the exact hierarchy
/// consumed by Chrome source-model output and the model authoring preview.
/// </summary>
public sealed class Dl1PreparedAuthoredRig
{
    internal Dl1PreparedAuthoredRig(
        Dl1AuthoredRigContract contract,
        RigDefinition sourceRig,
        ImmutableArray<Dl1PreparedSkinSurface> surfaces)
    {
        Contract = contract;
        SourceRig = sourceRig;
        PreviewRig = contract.CreateRigDefinition();
        Surfaces = surfaces;
    }

    public Dl1AuthoredRigContract Contract { get; }

    public RigDefinition SourceRig { get; }

    public RigDefinition PreviewRig { get; }

    public ImmutableArray<Dl1PreparedSkinSurface> Surfaces { get; }

    public ImmutableArray<Dl1AuthoredRigDecompositionDiagnostic> Diagnostics =>
        Contract.DecompositionDiagnostics;

    /// <summary>
    /// Expresses a sampled source pose in the emitted Chrome bind frames.
    /// The global bind-basis conversion guarantees that the source bind maps
    /// exactly to the emitted bind while preserving the animated global pose.
    /// </summary>
    public SkeletonPose RebasePose(SkeletonPose sourcePose)
    {
        ArgumentNullException.ThrowIfNull(sourcePose);
        if (!ReferenceEquals(sourcePose.Rig, SourceRig) &&
            !string.Equals(sourcePose.Rig.Id, SourceRig.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The sampled pose does not belong to the prepared source rig.", nameof(sourcePose));
        }

        ImmutableArray<TransformMatrix> sourceBindGlobals = SourceRig.CreateBindPose().GlobalMatrices;
        var targetGlobals = ImmutableArray.CreateBuilder<TransformMatrix>(Contract.Nodes.Length);
        var targetLocals = ImmutableArray.CreateBuilder<TransformTRS>(Contract.Nodes.Length);
        foreach (Dl1AuthoredRigNode node in Contract.Nodes)
        {
            TransformMatrix bindBasis = sourceBindGlobals[node.SourceBoneIndex].InvertedAffine() *
                node.GlobalBindMatrix;
            TransformMatrix targetGlobal = sourcePose.GlobalMatrices[node.SourceBoneIndex] * bindBasis;
            TransformMatrix targetLocal = node.ParentPhysicalIndex < 0
                ? targetGlobal
                : targetGlobals[node.ParentPhysicalIndex].InvertedAffine() * targetGlobal;
            targetGlobals.Add(targetGlobal);
            targetLocals.Add(ProjectAffineToTrs(
                targetLocal,
                node.Name,
                node.PhysicalIndex));
        }

        return new SkeletonPose(PreviewRig, targetLocals.MoveToImmutable());
    }

    private static TransformTRS ProjectAffineToTrs(
        TransformMatrix matrix,
        string boneName,
        int physicalIndex)
    {
        try
        {
            return matrix.Decompose(1e-7);
        }
        catch (InvalidOperationException exception)
        {
            try
            {
                TransformMatrix rotation =
                    Dl1CustomModelRigPreparer.OrthonormalizeRotation(matrix);
                double scaleX = new Vector3D(matrix.M11, matrix.M21, matrix.M31).Length;
                double scaleY = new Vector3D(matrix.M12, matrix.M22, matrix.M32).Length;
                double scaleZ = new Vector3D(matrix.M13, matrix.M23, matrix.M33).Length;
                return new TransformTRS(
                    matrix.Translation,
                    QuaternionD.FromRotationMatrix(rotation),
                    new Vector3D(
                        Math.Max(scaleX, 1e-8),
                        Math.Max(scaleY, 1e-8),
                        Math.Max(scaleZ, 1e-8)));
            }
            catch (Exception repairException) when (
                repairException is InvalidOperationException or InvalidDataException)
            {
                throw new InvalidDataException(
                    $"Authored-rig bone '{boneName}' at physical index {physicalIndex} " +
                    $"could not be rebased to TRS: {exception.Message}",
                    repairException);
            }
        }
    }
}

/// <summary>
/// Prepares emitted bone frames, exact inverse-global references, deterministic
/// physical order and nonzero segment-proxy bounds. Existing animation banks
/// require the source bind basis; newly authored animation uses Chrome +X frames.
/// </summary>
public static class Dl1CustomModelRigPreparer
{
    private const double MinimumHalfExtent = 0.005;
    // Python's validated source-MSH writer retains i16 weights from 2 upward
    // and samples bounds above four times that threshold.
    private const double MeaningfulWeightThreshold = 8.0 / 32_767.0;

    public static Dl1PreparedAuthoredRig Prepare(
        FbxModelAuthoringImportResult model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Package.Document.Validate();
        RigDefinition sourceRig = model.Rig ?? throw new InvalidOperationException(
            "A static custom model has no authored animation rig.");
        ImmutableArray<CustomModelBone> sourceBones =
            model.Package.Document.CreateEffectiveBones();
        if (sourceBones.Length != sourceRig.BoneCount)
        {
            throw new InvalidDataException("The imported custom-model rig and source-bone table disagree.");
        }

        ValidateUniqueNames(sourceBones);
        ImmutableArray<int> depthFirstSource = BuildDepthFirstOrder(sourceBones);
        var sourceToPhysical = ImmutableArray.CreateBuilder<int>(sourceBones.Length);
        sourceToPhysical.Count = sourceBones.Length;
        for (int physicalIndex = 0; physicalIndex < depthFirstSource.Length; physicalIndex++)
        {
            sourceToPhysical[depthFirstSource[physicalIndex]] = physicalIndex;
        }

        ImmutableArray<TransformMatrix> sourceGlobals = ComputeSourceGlobals(sourceBones);
        var physicalOriginalGlobals = ImmutableArray.CreateBuilder<TransformMatrix>(sourceBones.Length);
        var physicalParents = ImmutableArray.CreateBuilder<int>(sourceBones.Length);
        var physicalDeform = ImmutableArray.CreateBuilder<bool>(sourceBones.Length);
        foreach (int sourceIndex in depthFirstSource)
        {
            CustomModelBone bone = sourceBones[sourceIndex];
            physicalOriginalGlobals.Add(sourceGlobals[sourceIndex]);
            physicalParents.Add(bone.ParentIndex < 0 ? -1 : sourceToPhysical[bone.ParentIndex]);
            physicalDeform.Add(IsDeform(bone));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<TransformMatrix> originalGlobals = physicalOriginalGlobals.MoveToImmutable();
        ImmutableArray<int> parentArray = physicalParents.MoveToImmutable();
        ImmutableArray<bool> deformArray = physicalDeform.MoveToImmutable();
        bool preserveSourceFrames = model.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary;
        HashSet<string> secondaryBones = model.Package.Document.SecondaryMotion.Groups
            .SelectMany(group => group.Particles)
            .Where(particle => particle.DrivenBoneName is not null)
            .Select(particle => particle.DrivenBoneName!)
            .ToHashSet(StringComparer.Ordinal);
        ImmutableArray<TransformMatrix> chromeGlobals = !preserveSourceFrames || secondaryBones.Count > 0
            ? AuthorChromeFrames(originalGlobals, parentArray, deformArray, cancellationToken)
            : originalGlobals;
        ImmutableArray<TransformMatrix> authoredGlobals = preserveSourceFrames
            ? depthFirstSource.Select((sourceIndex, physicalIndex) => secondaryBones.Contains(sourceBones[sourceIndex].Name)
                ? chromeGlobals[physicalIndex]
                : originalGlobals[physicalIndex]).ToImmutableArray()
            : chromeGlobals;
        ImmutableArray<Dl1AuthoredBoneBounds> bounds = ComputeSegmentProxyBounds(
            model.Surfaces,
            authoredGlobals,
            parentArray,
            sourceToPhysical.ToImmutable(),
            cancellationToken);

        var nodes = ImmutableArray.CreateBuilder<Dl1AuthoredRigNode>(sourceBones.Length);
        var descriptorOwners = new Dictionary<uint, string>();
        for (int physicalIndex = 0; physicalIndex < depthFirstSource.Length; physicalIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sourceIndex = depthFirstSource[physicalIndex];
            CustomModelBone sourceBone = sourceBones[sourceIndex];
            int parent = sourceBone.ParentIndex < 0 ? -1 : sourceToPhysical[sourceBone.ParentIndex];
            bool preserveLocal = preserveSourceFrames && !secondaryBones.Contains(sourceBone.Name) &&
                (sourceBone.ParentIndex < 0 || !secondaryBones.Contains(sourceBones[sourceBone.ParentIndex].Name));
            TransformMatrix local = preserveLocal
                ? sourceBone.ExactLocalBindMatrix
                : parent < 0
                    ? authoredGlobals[physicalIndex]
                    : authoredGlobals[parent].InvertedAffine() * authoredGlobals[physicalIndex];
            bool deform = IsDeform(sourceBone);
            uint descriptor = Dl1NameHash.Compute(sourceBone.Name);
            if (descriptorOwners.TryGetValue(descriptor, out string? existing))
            {
                throw new InvalidDataException(
                    $"DL1 descriptor collision 0x{descriptor:X8} between custom-rig entities '{existing}' and '{sourceBone.Name}'.");
            }

            descriptorOwners.Add(descriptor, sourceBone.Name);
            nodes.Add(new Dl1AuthoredRigNode
            {
                PhysicalIndex = physicalIndex,
                SourceBoneIndex = sourceIndex,
                Name = sourceBone.Name,
                ParentPhysicalIndex = parent,
                Kind = deform
                    ? sourceBone.Kind is BoneKind.Root ? BoneKind.Root : BoneKind.Deform
                    : sourceBone.Kind is BoneKind.Camera or BoneKind.Prop
                        ? sourceBone.Kind
                        : BoneKind.Helper,
                IsDeform = deform,
                LocalBindMatrix = local,
                GlobalBindMatrix = authoredGlobals[physicalIndex],
                InverseGlobalReferenceMatrix = authoredGlobals[physicalIndex].InvertedAffine(),
                Bounds = bounds[physicalIndex],
                DescriptorHash = descriptor,
            });
        }

        var contract = new Dl1AuthoredRigContract(
            model.Package.Document.Name,
            model.Package.Document.Source.ContentSha256,
            nodes.MoveToImmutable(),
            sourceRig.MorphChannels);
        var preparedSurfaces = ImmutableArray.CreateBuilder<Dl1PreparedSkinSurface>(model.Surfaces.Length);
        foreach (FbxModelSurface surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<int> physicalPalette = surface.PaletteBoneIndices
                .Select(sourceIndex =>
                {
                    if ((uint)sourceIndex >= (uint)contract.SourceToPhysicalIndices.Length)
                    {
                        throw new InvalidDataException($"Surface '{surface.Id}' references unknown source bone {sourceIndex}.");
                    }

                    return contract.SourceToPhysicalIndices[sourceIndex];
                })
                .ToImmutableArray();
            ImmutableArray<TransformMatrix> inverseBinds = physicalPalette
                .Select(physicalIndex => contract.Nodes[physicalIndex].InverseGlobalReferenceMatrix)
                .ToImmutableArray();
            preparedSurfaces.Add(new Dl1PreparedSkinSurface(physicalPalette, inverseBinds));
        }

        return new Dl1PreparedAuthoredRig(
            contract,
            sourceRig,
            preparedSurfaces.MoveToImmutable());
    }

    internal static TransformMatrix OrthonormalizeRotation(TransformMatrix value)
    {
        if (!value.IsFinite)
        {
            throw new InvalidDataException("A Chrome bone frame must be finite.");
        }

        // Newton's polar iteration computes the same nearest orthogonal factor
        // as the Python oracle's U*Vt SVD projection for ordinary nonsingular
        // affine binds. Scaling first leaves that factor unchanged and avoids
        // slow convergence on authoring packages that carry centimeter scale.
        TransformMatrix projected = LinearPart(value);
        double frobenius = Math.Sqrt(
            (projected.M11 * projected.M11) + (projected.M12 * projected.M12) + (projected.M13 * projected.M13) +
            (projected.M21 * projected.M21) + (projected.M22 * projected.M22) + (projected.M23 * projected.M23) +
            (projected.M31 * projected.M31) + (projected.M32 * projected.M32) + (projected.M33 * projected.M33));
        if (double.IsFinite(frobenius) && frobenius > 1e-12)
        {
            projected = ScaleLinear(projected, 1.0 / frobenius);
            for (int iteration = 0; iteration < 32; iteration++)
            {
                TransformMatrix inverse;
                try
                {
                    inverse = projected.InvertedAffine();
                }
                catch (InvalidOperationException)
                {
                    projected = LinearPart(value);
                    break;
                }

                TransformMatrix next = AverageLinear(projected, TransposeLinear(inverse));
                double delta = MaximumLinearDifference(projected, next);
                projected = next;
                if (delta <= 1e-13)
                {
                    break;
                }
            }
        }

        Vector3D originalX = new(projected.M11, projected.M21, projected.M31);
        Vector3D originalY = new(projected.M12, projected.M22, projected.M32);
        Vector3D originalZ = new(projected.M13, projected.M23, projected.M33);
        Vector3D x = NormalizeOrFallback(originalX, originalY, Vector3D.UnitX);
        Vector3D yCandidate = originalY - (x * Vector3D.Dot(originalY, x));
        if (!yCandidate.TryNormalize(out Vector3D y, 1e-10))
        {
            yCandidate = originalZ - (x * Vector3D.Dot(originalZ, x));
            if (!yCandidate.TryNormalize(out y, 1e-10))
            {
                Vector3D seed = Math.Abs(Vector3D.Dot(x, Vector3D.UnitZ)) < 0.95
                    ? Vector3D.UnitZ
                    : Vector3D.UnitY;
                y = Vector3D.Cross(seed, x).Normalized();
            }
        }

        Vector3D z = Vector3D.Cross(x, y).Normalized();
        if (Vector3D.Dot(z, originalZ) < 0.0)
        {
            y = -y;
            z = -z;
        }

        return RotationMatrix(x, y, z, value.Translation);
    }

    private static TransformMatrix LinearPart(TransformMatrix value) =>
        new(
            value.M11, value.M12, value.M13, 0.0,
            value.M21, value.M22, value.M23, 0.0,
            value.M31, value.M32, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix ScaleLinear(TransformMatrix value, double scale) =>
        new(
            value.M11 * scale, value.M12 * scale, value.M13 * scale, 0.0,
            value.M21 * scale, value.M22 * scale, value.M23 * scale, 0.0,
            value.M31 * scale, value.M32 * scale, value.M33 * scale, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix TransposeLinear(TransformMatrix value) =>
        new(
            value.M11, value.M21, value.M31, 0.0,
            value.M12, value.M22, value.M32, 0.0,
            value.M13, value.M23, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix AverageLinear(TransformMatrix left, TransformMatrix right) =>
        new(
            (left.M11 + right.M11) * 0.5, (left.M12 + right.M12) * 0.5, (left.M13 + right.M13) * 0.5, 0.0,
            (left.M21 + right.M21) * 0.5, (left.M22 + right.M22) * 0.5, (left.M23 + right.M23) * 0.5, 0.0,
            (left.M31 + right.M31) * 0.5, (left.M32 + right.M32) * 0.5, (left.M33 + right.M33) * 0.5, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static double MaximumLinearDifference(TransformMatrix left, TransformMatrix right)
    {
        double maximum = 0.0;
        maximum = Math.Max(maximum, Math.Abs(left.M11 - right.M11));
        maximum = Math.Max(maximum, Math.Abs(left.M12 - right.M12));
        maximum = Math.Max(maximum, Math.Abs(left.M13 - right.M13));
        maximum = Math.Max(maximum, Math.Abs(left.M21 - right.M21));
        maximum = Math.Max(maximum, Math.Abs(left.M22 - right.M22));
        maximum = Math.Max(maximum, Math.Abs(left.M23 - right.M23));
        maximum = Math.Max(maximum, Math.Abs(left.M31 - right.M31));
        maximum = Math.Max(maximum, Math.Abs(left.M32 - right.M32));
        return Math.Max(maximum, Math.Abs(left.M33 - right.M33));
    }

    private static ImmutableArray<TransformMatrix> AuthorChromeFrames(
        ImmutableArray<TransformMatrix> originalGlobals,
        ImmutableArray<int> parents,
        ImmutableArray<bool> deform,
        CancellationToken cancellationToken)
    {
        int count = originalGlobals.Length;
        Vector3D[] positions = originalGlobals.Select(static matrix => matrix.Translation).ToArray();
        TransformMatrix[] references = originalGlobals.Select(OrthonormalizeRotation).ToArray();
        List<int>[] children = BuildChildren(parents);
        var authored = ImmutableArray.CreateBuilder<TransformMatrix>(count);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chosenChild = -1;
            Vector3D? chosenDirection = null;
            double chosenAlignment = double.NegativeInfinity;
            bool chosenDeform = false;
            double chosenLength = double.NegativeInfinity;
            Vector3D referenceY = new(references[index].M12, references[index].M22, references[index].M32);
            foreach (int child in children[index])
            {
                Vector3D? candidate = FindDescendantDirection(index, child, positions, children);
                if (candidate is not { } direction)
                {
                    continue;
                }

                double length = direction.Length;
                double alignment = Vector3D.Dot(direction / length, referenceY);
                bool candidateDeform = deform[child];
                if (alignment > chosenAlignment ||
                    (alignment == chosenAlignment && candidateDeform && !chosenDeform) ||
                    (alignment == chosenAlignment && candidateDeform == chosenDeform && length > chosenLength) ||
                    (alignment == chosenAlignment && candidateDeform == chosenDeform && length == chosenLength && child < chosenChild))
                {
                    chosenChild = child;
                    chosenDirection = direction;
                    chosenAlignment = alignment;
                    chosenDeform = candidateDeform;
                    chosenLength = length;
                }
            }

            if (chosenDirection is null && parents[index] >= 0)
            {
                Vector3D incoming = positions[index] - positions[parents[index]];
                if (incoming.Length > 1e-8)
                {
                    chosenDirection = incoming;
                }
            }

            TransformMatrix rotation = chosenDirection is { } directionToAim
                ? AimPositiveX(directionToAim, references[index])
                : parents[index] >= 0
                    ? authored[parents[index]]
                    : references[index];
            authored.Add(WithTranslation(rotation, positions[index]));
        }

        return authored.MoveToImmutable();
    }

    private static TransformMatrix AimPositiveX(Vector3D direction, TransformMatrix reference)
    {
        if (!direction.TryNormalize(out Vector3D x, 1e-10))
        {
            return OrthonormalizeRotation(reference);
        }

        TransformMatrix orthonormalReference = OrthonormalizeRotation(reference);
        Vector3D referenceZ = new(orthonormalReference.M13, orthonormalReference.M23, orthonormalReference.M33);
        Vector3D zCandidate = referenceZ - (x * Vector3D.Dot(referenceZ, x));
        if (!zCandidate.TryNormalize(out Vector3D z, 1e-8))
        {
            Vector3D referenceX = new(orthonormalReference.M11, orthonormalReference.M21, orthonormalReference.M31);
            zCandidate = referenceX - (x * Vector3D.Dot(referenceX, x));
            if (!zCandidate.TryNormalize(out z, 1e-8))
            {
                Vector3D seed = Math.Abs(Vector3D.Dot(Vector3D.UnitZ, x)) > 0.95
                    ? Vector3D.UnitY
                    : Vector3D.UnitZ;
                z = (seed - (x * Vector3D.Dot(seed, x))).Normalized();
            }
        }

        Vector3D y = Vector3D.Cross(z, x).Normalized();
        return RotationMatrix(x, y, z, Vector3D.Zero);
    }

    private static ImmutableArray<Dl1AuthoredBoneBounds> ComputeSegmentProxyBounds(
        ImmutableArray<FbxModelSurface> surfaces,
        ImmutableArray<TransformMatrix> globals,
        ImmutableArray<int> parents,
        ImmutableArray<int> sourceToPhysical,
        CancellationToken cancellationToken)
    {
        int count = globals.Length;
        TransformMatrix[] inverseGlobals = globals.Select(static matrix => matrix.InvertedAffine()).ToArray();
        var pointsByBone = Enumerable.Range(0, count).Select(static _ => new List<Vector3D>()).ToArray();
        foreach (FbxModelSurface surface in surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                int bestPhysical = -1;
                double bestWeight = double.NegativeInfinity;
                for (int influenceIndex = 0;
                     influenceIndex < Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length);
                     influenceIndex++)
                {
                    int localPaletteIndex = vertex.BoneIndices[influenceIndex];
                    double weight = vertex.BoneWeights[influenceIndex];
                    if (weight <= MeaningfulWeightThreshold ||
                        (uint)localPaletteIndex >= (uint)surface.PaletteBoneIndices.Length)
                    {
                        continue;
                    }

                    int sourceIndex = surface.PaletteBoneIndices[localPaletteIndex];
                    if ((uint)sourceIndex >= (uint)sourceToPhysical.Length)
                    {
                        throw new InvalidDataException($"Surface '{surface.Id}' references unknown source bone {sourceIndex}.");
                    }

                    int physical = sourceToPhysical[sourceIndex];
                    if (weight > bestWeight || (weight == bestWeight && physical < bestPhysical))
                    {
                        bestPhysical = physical;
                        bestWeight = weight;
                    }
                }

                if (bestPhysical >= 0)
                {
                    pointsByBone[bestPhysical].Add(inverseGlobals[bestPhysical].TransformPoint(vertex.Position));
                }
            }
        }

        List<int>[] children = BuildChildren(parents);
        var result = ImmutableArray.CreateBuilder<Dl1AuthoredBoneBounds>(count);
        Vector3D aggregateLow = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        Vector3D aggregateHigh = new(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentPoints = new List<Vector3D> { Vector3D.Zero };
            Vector3D[] childPoints = children[index]
                .Select(child => inverseGlobals[index].TransformPoint(globals[child].Translation))
                .ToArray();
            if (childPoints.Length > 0)
            {
                segmentPoints.Add(childPoints.MaxBy(static value =>
                    (Alignment: value.X / Math.Max(value.Length, 1e-12), Length: value.Length)));
            }
            else
            {
                segmentPoints.Add(new Vector3D(
                    ComputeTerminalLength(index, globals, parents, children),
                    0.0,
                    0.0));
            }

            Vector3D segmentEnd = segmentPoints[1];
            Vector3D low = new(
                Math.Min(0.0, segmentEnd.X),
                Math.Min(0.0, segmentEnd.Y),
                Math.Min(0.0, segmentEnd.Z));
            Vector3D high = new(
                Math.Max(0.0, segmentEnd.X),
                Math.Max(0.0, segmentEnd.Y),
                Math.Max(0.0, segmentEnd.Z));
            double segmentLength = segmentEnd.Length;
            double defaultRadius = Math.Max(
                MinimumHalfExtent,
                Math.Min(0.025, Math.Max(segmentLength, 0.02) * 0.12));
            double radiusCap = Math.Max(0.015, Math.Min(0.08, Math.Max(segmentLength, 0.02) * 0.25));
            double radiusY = pointsByBone[index].Count > 0
                ? Math.Min(radiusCap, Math.Max(defaultRadius, PercentileAbsolute(pointsByBone[index], static value => value.Y, 0.75)))
                : defaultRadius;
            double radiusZ = pointsByBone[index].Count > 0
                ? Math.Min(radiusCap, Math.Max(defaultRadius, PercentileAbsolute(pointsByBone[index], static value => value.Z, 0.75)))
                : defaultRadius;
            low = new Vector3D(low.X, -radiusY, -radiusZ);
            high = new Vector3D(high.X, radiusY, radiusZ);
            Vector3D center = (low + high) * 0.5;
            Vector3D half = new(
                Math.Max((high.X - low.X) * 0.5, MinimumHalfExtent),
                Math.Max((high.Y - low.Y) * 0.5, MinimumHalfExtent),
                Math.Max((high.Z - low.Z) * 0.5, MinimumHalfExtent));
            result.Add(new Dl1AuthoredBoneBounds(center, half));

            foreach (Vector3D localCorner in BoundsCorners(center, half))
            {
                Vector3D modelCorner = globals[index].TransformPoint(localCorner);
                aggregateLow = ComponentMin(aggregateLow, modelCorner);
                aggregateHigh = ComponentMax(aggregateHigh, modelCorner);
            }
        }

        if (!aggregateLow.IsFinite || !aggregateHigh.IsFinite ||
            Vector3D.Distance(aggregateLow, aggregateHigh) <= 0.01)
        {
            throw new InvalidDataException("Generated Chrome bone bounds collapse to an invalid model extent.");
        }

        return result.MoveToImmutable();
    }

    private static double ComputeTerminalLength(
        int index,
        ImmutableArray<TransformMatrix> globals,
        ImmutableArray<int> parents,
        List<int>[] children)
    {
        int parent = parents[index];
        double parentLength = parent >= 0
            ? Vector3D.Distance(globals[index].Translation, globals[parent].Translation)
            : 0.04;
        double continuation = parentLength;
        int ancestor = parent;
        for (int depth = 0; depth < 2 && ancestor >= 0; depth++)
        {
            Vector3D origin = globals[ancestor].Translation;
            double candidate = children[ancestor]
                .Where(child => child != index)
                .Select(child => Vector3D.Distance(globals[child].Translation, origin))
                .DefaultIfEmpty(0.0)
                .Max();
            continuation = Math.Max(continuation, candidate);
            if (continuation > 0.02)
            {
                break;
            }

            ancestor = parents[ancestor];
        }

        return Math.Max(0.012, Math.Min(0.22, Math.Max(parentLength * 0.35, continuation * 0.5)));
    }

    private static double PercentileAbsolute(
        List<Vector3D> points,
        Func<Vector3D, double> selector,
        double percentile)
    {
        double[] values = points.Select(point => Math.Abs(selector(point))).Order().ToArray();
        if (values.Length == 0)
        {
            return 0.0;
        }

        double position = (values.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper
            ? values[lower]
            : values[lower] + ((values[upper] - values[lower]) * (position - lower));
    }

    private static IEnumerable<Vector3D> BoundsCorners(Vector3D center, Vector3D half)
    {
        foreach (double x in new[] { -1.0, 1.0 })
        foreach (double y in new[] { -1.0, 1.0 })
        foreach (double z in new[] { -1.0, 1.0 })
        {
            yield return center + Vector3D.ComponentMultiply(half, new Vector3D(x, y, z));
        }
    }

    private static Vector3D? FindDescendantDirection(
        int originIndex,
        int child,
        Vector3D[] positions,
        List<int>[] children)
    {
        var pending = new Queue<int>();
        var visited = new HashSet<int>();
        pending.Enqueue(child);
        while (pending.TryDequeue(out int current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            Vector3D direction = positions[current] - positions[originIndex];
            if (direction.Length > 1e-8)
            {
                return direction;
            }

            foreach (int descendant in children[current])
            {
                pending.Enqueue(descendant);
            }
        }

        return null;
    }

    private static ImmutableArray<int> BuildDepthFirstOrder(ImmutableArray<CustomModelBone> bones)
    {
        var children = Enumerable.Range(0, bones.Length)
            .Select(static _ => new List<int>())
            .ToArray();
        var roots = new List<int>();
        foreach (CustomModelBone bone in bones)
        {
            if (bone.ParentIndex < 0)
            {
                roots.Add(bone.Index);
            }
            else
            {
                children[bone.ParentIndex].Add(bone.Index);
            }
        }

        var result = ImmutableArray.CreateBuilder<int>(bones.Length);
        void Visit(int index)
        {
            result.Add(index);
            foreach (int child in children[index])
            {
                Visit(child);
            }
        }

        foreach (int root in roots)
        {
            Visit(root);
        }

        if (result.Count != bones.Length)
        {
            throw new InvalidDataException("The custom-model bone hierarchy contains an unreachable entity.");
        }

        return result.MoveToImmutable();
    }

    private static ImmutableArray<TransformMatrix> ComputeSourceGlobals(ImmutableArray<CustomModelBone> bones)
    {
        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(bones.Length);
        foreach (CustomModelBone bone in bones)
        {
            globals.Add(bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix);
        }

        return globals.MoveToImmutable();
    }

    private static List<int>[] BuildChildren(ImmutableArray<int> parents)
    {
        var children = Enumerable.Range(0, parents.Length).Select(static _ => new List<int>()).ToArray();
        for (int index = 0; index < parents.Length; index++)
        {
            if (parents[index] >= 0)
            {
                children[parents[index]].Add(index);
            }
        }

        return children;
    }

    private static bool IsDeform(CustomModelBone bone) =>
        bone.IsWeighted && bone.Kind is BoneKind.Deform or BoneKind.Root;

    private static void ValidateUniqueNames(ImmutableArray<CustomModelBone> bones)
    {
        string[] duplicate = bones
            .GroupBy(static bone => bone.Name.Normalize(NormalizationForm.FormKC), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicate.Length > 0)
        {
            throw new InvalidDataException(
                $"Custom-model animation entity names must be unique: {string.Join(", ", duplicate)}.");
        }
    }

    private static Vector3D NormalizeOrFallback(Vector3D first, Vector3D second, Vector3D fallback)
    {
        if (first.TryNormalize(out Vector3D result))
        {
            return result;
        }

        if (second.TryNormalize(out result))
        {
            return result;
        }

        return fallback;
    }

    private static TransformMatrix RotationMatrix(
        Vector3D x,
        Vector3D y,
        Vector3D z,
        Vector3D translation) =>
        new(
            x.X, y.X, z.X, translation.X,
            x.Y, y.Y, z.Y, translation.Y,
            x.Z, y.Z, z.Z, translation.Z,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix WithTranslation(TransformMatrix value, Vector3D translation) =>
        new(
            value.M11, value.M12, value.M13, translation.X,
            value.M21, value.M22, value.M23, translation.Y,
            value.M31, value.M32, value.M33, translation.Z,
            0.0, 0.0, 0.0, 1.0);

    private static Vector3D ComponentMin(Vector3D left, Vector3D right) =>
        new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

    private static Vector3D ComponentMax(Vector3D left, Vector3D right) =>
        new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));
}
