using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Geometry-derived editor collision proposals for explicitly selected rig bones.</summary>
public static class SecondaryColliderAuthoring
{
    private const double MeaningfulWeight = 0.05;
    private const double MinimumRadius = 1e-6;

    public static SecondaryCollider FitSphere(FbxModelAuthoringImportResult model, string boneName)
    {
        ArgumentNullException.ThrowIfNull(model);
        CustomModelBone[] bones = model.Package.Document.CreateEffectiveBones().ToArray();
        int bone = FindBone(bones, boneName);
        TransformMatrix[] globals = BuildGlobals(bones);
        List<Vector3D> points = WeightedPoints(model.Surfaces, bones.Length, [bone]);
        if (points.Count == 0)
            throw new InvalidOperationException($"Bone '{boneName}' has no weighted geometry for a fitted sphere.");

        TransformMatrix inverse = globals[bone].InvertedAffine();
        List<Vector3D> localPoints = points.Select(inverse.TransformPoint).ToList();
        (Vector3D minimum, Vector3D maximum) = Bounds(localPoints);
        Vector3D center = (minimum + maximum) * 0.5;
        double radius = localPoints.Max(point => Vector3D.Distance(point, center));
        RequireRadius(radius, boneName);
        return new() { BoneName = boneName, LocalPosition = center, Radius = radius };
    }

    public static SecondaryCollider FitCapsule(
        FbxModelAuthoringImportResult model, string startBoneName, string endBoneName)
    {
        ArgumentNullException.ThrowIfNull(model);
        CustomModelBone[] bones = model.Package.Document.CreateEffectiveBones().ToArray();
        int start = FindBone(bones, startBoneName);
        int end = FindBone(bones, endBoneName);
        if (start == end) throw new ArgumentException("Choose two different bones for a capsule.", nameof(endBoneName));
        int[] chain = BoneChain(bones, start, end);
        TransformMatrix[] globals = BuildGlobals(bones);
        Vector3D startPosition = globals[start].Translation;
        Vector3D endPosition = globals[end].Translation;
        Vector3D axis = endPosition - startPosition;
        if (axis.LengthSquared <= MinimumRadius * MinimumRadius)
            throw new InvalidOperationException("The selected bone pivots do not form a capsule axis.");

        List<Vector3D> points = WeightedPoints(model.Surfaces, bones.Length, chain);
        if (points.Count == 0)
            throw new InvalidOperationException("The selected bone chain has no weighted geometry for a fitted capsule.");
        double axisLengthSquared = axis.LengthSquared;
        double radius = 0;
        foreach (Vector3D point in points)
        {
            double amount = Math.Clamp(Vector3D.Dot(point - startPosition, axis) / axisLengthSquared, 0, 1);
            radius = Math.Max(radius, Vector3D.Distance(point, startPosition + axis * amount));
        }
        double attachmentScale = Math.Max(MaxScale(globals[start]), MaxScale(globals[end]));
        if (!double.IsFinite(attachmentScale) || attachmentScale <= MinimumRadius)
            throw new InvalidDataException("The selected bone chain has an invalid bind scale.");
        radius /= attachmentScale;
        RequireRadius(radius, $"{startBoneName}–{endBoneName}");
        return new()
        {
            BoneName = startBoneName,
            LocalPosition = Vector3D.Zero,
            EndBoneName = endBoneName,
            EndLocalPosition = Vector3D.Zero,
            Radius = radius,
        };
    }

    private static List<Vector3D> WeightedPoints(
        ImmutableArray<FbxModelSurface> surfaces, int boneCount, IEnumerable<int> supportedBones)
    {
        HashSet<int> support = supportedBones.ToHashSet();
        var points = new List<Vector3D>();
        foreach (FbxModelSurface surface in surfaces)
        {
            if (surface.PaletteBoneIndices.IsDefault || surface.Vertices.IsDefault ||
                surface.PaletteBoneIndices.Any(index => index < 0 || index >= boneCount))
                throw new InvalidDataException("Collision fitting found an invalid surface palette.");
            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                if (!vertex.Position.IsFinite || vertex.BoneIndices.IsDefault || vertex.BoneWeights.IsDefault ||
                    vertex.BoneIndices.Length != vertex.BoneWeights.Length)
                    throw new InvalidDataException("Collision fitting found an invalid weighted vertex.");
                bool supported = false;
                for (int influence = 0; influence < vertex.BoneIndices.Length; influence++)
                {
                    int paletteIndex = vertex.BoneIndices[influence];
                    double weight = vertex.BoneWeights[influence];
                    if ((uint)paletteIndex >= (uint)surface.PaletteBoneIndices.Length ||
                        !double.IsFinite(weight) || weight < 0)
                        throw new InvalidDataException("Collision fitting found an invalid bone influence.");
                    if (weight >= MeaningfulWeight && support.Contains(surface.PaletteBoneIndices[paletteIndex]))
                        supported = true;
                }
                if (supported) points.Add(vertex.Position);
            }
        }
        return points;
    }

    private static (Vector3D Minimum, Vector3D Maximum) Bounds(List<Vector3D> points)
    {
        Vector3D minimum = points[0], maximum = points[0];
        for (int index = 1; index < points.Count; index++)
        {
            Vector3D point = points[index];
            minimum = new(Math.Min(minimum.X, point.X), Math.Min(minimum.Y, point.Y), Math.Min(minimum.Z, point.Z));
            maximum = new(Math.Max(maximum.X, point.X), Math.Max(maximum.Y, point.Y), Math.Max(maximum.Z, point.Z));
        }
        return (minimum, maximum);
    }

    private static TransformMatrix[] BuildGlobals(CustomModelBone[] bones)
    {
        var globals = new TransformMatrix[bones.Length];
        for (int index = 0; index < bones.Length; index++)
        {
            CustomModelBone bone = bones[index];
            if (bone.Index != index || bone.ParentIndex < -1 || bone.ParentIndex >= index || !bone.ExactLocalBindMatrix.IsFinite)
                throw new InvalidDataException("Collision fitting requires a valid parent-first bone hierarchy.");
            globals[index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
            if (!globals[index].IsFinite) throw new InvalidDataException("A collision-fit bone frame is not finite.");
        }
        return globals;
    }

    private static int FindBone(CustomModelBone[] bones, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        int match = Array.FindIndex(bones, bone => bone.Name.Equals(name, StringComparison.Ordinal));
        return match >= 0 ? match : throw new ArgumentException($"Bone '{name}' is absent from the model.", nameof(name));
    }

    private static int[] BoneChain(CustomModelBone[] bones, int start, int end)
    {
        var startAncestors = Ancestors(bones, start);
        var endAncestors = Ancestors(bones, end);
        int common = startAncestors.FirstOrDefault(endAncestors.Contains, -1);
        if (common < 0) throw new ArgumentException("Choose bones on the same hierarchy chain.", nameof(end));
        var chain = new List<int>();
        for (int current = start; current != common; current = bones[current].ParentIndex) chain.Add(current);
        chain.Add(common);
        var tail = new List<int>();
        for (int current = end; current != common; current = bones[current].ParentIndex) tail.Add(current);
        tail.Reverse();
        chain.AddRange(tail);
        return chain.ToArray();
    }

    private static HashSet<int> Ancestors(CustomModelBone[] bones, int index)
    {
        var result = new HashSet<int>();
        for (int current = index; current >= 0; current = bones[current].ParentIndex) result.Add(current);
        return result;
    }

    private static void RequireRadius(double radius, string boneName)
    {
        if (!double.IsFinite(radius) || radius <= MinimumRadius)
            throw new InvalidOperationException($"The weighted geometry for '{boneName}' does not provide a usable collision radius.");
    }

    private static double MaxScale(TransformMatrix matrix) => Math.Max(
        matrix.TransformDirection(Vector3D.UnitX).Length,
        Math.Max(matrix.TransformDirection(Vector3D.UnitY).Length, matrix.TransformDirection(Vector3D.UnitZ).Length));
}
