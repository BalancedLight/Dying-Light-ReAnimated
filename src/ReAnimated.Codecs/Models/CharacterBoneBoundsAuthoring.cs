using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record CharacterBoneBoundsControl(string Name, string? Axis, FbxModelAuthoringImportResult Model);

public static class CharacterBoneBoundsAuthoring
{
    public static Dl1AuthoredBoneBounds ParseFullDimensions(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double[] values = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 6)
            throw new ArgumentException("Enter center X Y Z followed by full sizes X Y Z.", nameof(text));
        return CreateBounds(new(values[0], values[1], values[2]), new(values[3], values[4], values[5]));
    }

    public static CustomModelDocument UpdateDocument(
        CustomModelDocument document, string exactBoneName, Dl1AuthoredBoneBounds bounds, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!reviewed)
            throw new InvalidOperationException("Review the bounds before applying them.");
        if (!bounds.IsFiniteAndNonNegative)
            throw new ArgumentException("Bounds must be finite with nonnegative sizes.", nameof(bounds));
        int index = ExactBone(document.Bones, exactBoneName);
        return document with
        {
            Bones = document.Bones.SetItem(index, document.Bones[index] with { LocalBounds = bounds }),
            LastBuildReceipt = null,
            CharacterResources = document.CharacterResources is { } resources ? resources with
            {
                CompiledSemanticSha256 = null,
                LoadedResourceSha256 = null,
                VerifiedPlayerScenarios = [],
            } : null,
        };
    }

    public static FbxModelAuthoringImportResult Apply(
        FbxModelAuthoringImportResult model, string exactBoneName,
        Vector3D center, Vector3D fullSizes, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(model);
        CustomModelDocument document = UpdateDocument(
            model.Package.Document, exactBoneName, CreateBounds(center, fullSizes), reviewed);
        return ModelGeometryRevisionCodec.Capture(model with
        {
            Package = model.Package with { Document = document },
            Rig = document.Bones.IsEmpty ? null : document.CreateRigDefinition(),
        });
    }

    public static Dl1AuthoredBoneBounds Fit(FbxModelAuthoringImportResult model, string exactBoneName)
    {
        ArgumentNullException.ThrowIfNull(model);
        ImmutableArray<CustomModelBone> bones = model.Package.Document.CreateEffectiveBones();
        int target = ExactBone(bones, exactBoneName);
        TransformMatrix[] globals = new TransformMatrix[bones.Length];
        for (int index = 0; index < bones.Length; index++)
        {
            CustomModelBone bone = bones[index];
            if (bone.Index != index || bone.ParentIndex < -1 || bone.ParentIndex >= index ||
                !bone.ExactLocalBindMatrix.IsFinite)
                throw new InvalidDataException("The bone hierarchy or bind frame is invalid.");
            globals[index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
            if (!globals[index].IsFinite)
                throw new InvalidDataException("A bone bind frame is not finite.");
        }
        TransformMatrix inverse = globals[target].InvertedAffine();
        Vector3D min = default, max = default;
        bool hasPoint = false;
        foreach (FbxModelSurface surface in model.Surfaces)
        {
            if (surface.PaletteBoneIndices.IsDefault || surface.Vertices.IsDefault ||
                surface.PaletteBoneIndices.Any(index => index < 0 || index >= bones.Length))
                throw new InvalidDataException("A surface bone palette is invalid.");
            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                if (!vertex.Position.IsFinite || vertex.BoneIndices.IsDefault || vertex.BoneWeights.IsDefault ||
                    vertex.BoneIndices.Length != vertex.BoneWeights.Length)
                    throw new InvalidDataException("A weighted surface vertex is invalid.");
                bool weighted = false;
                for (int influence = 0; influence < vertex.BoneIndices.Length; influence++)
                {
                    int local = vertex.BoneIndices[influence];
                    double weight = vertex.BoneWeights[influence];
                    if (local < 0 || local >= surface.PaletteBoneIndices.Length ||
                        !double.IsFinite(weight) || weight < 0)
                        throw new InvalidDataException("A surface bone influence is invalid.");
                    weighted |= surface.PaletteBoneIndices[local] == target && weight >= .05;
                }
                if (!weighted)
                    continue;
                Vector3D point = inverse.TransformPoint(vertex.Position);
                if (!point.IsFinite)
                    throw new InvalidDataException("A bone-local surface point is not finite.");
                if (!hasPoint) { min = max = point; hasPoint = true; }
                else
                {
                    min = new(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
                    max = new(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
                }
            }
        }
        if (!hasPoint)
            throw new InvalidOperationException("This bone has no weighted surface points for a fitted candidate. Enter reviewed dimensions manually.");
        Vector3D full = max - min;
        return CreateBounds((min + max) * .5,
            new(Math.Max(full.X, .01), Math.Max(full.Y, .01), Math.Max(full.Z, .01)));
    }

    public static ImmutableArray<CharacterBoneBoundsControl> CreateAxisControls(
        FbxModelAuthoringImportResult model, string exactBoneName, double scale)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!double.IsFinite(scale) || scale <= 0 || scale == 1)
            throw new ArgumentOutOfRangeException(nameof(scale), "Choose a finite positive scale other than one.");
        int index = ExactBone(model.Package.Document.Bones, exactBoneName);
        Dl1AuthoredBoneBounds original = model.Package.Document.Bones[index].LocalBounds
            ?? throw new InvalidOperationException("The selected bone has no bounds.");
        if (!original.IsFiniteAndNonZero)
            throw new InvalidOperationException("Each edited axis needs a positive finite half extent.");
        var controls = ImmutableArray.CreateBuilder<CharacterBoneBoundsControl>(4);
        controls.Add(new("baseline", null, model));
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3D half = original.HalfExtents;
            double previous = axis == 0 ? half.X : axis == 1 ? half.Y : half.Z;
            double varied = previous * scale;
            if (!double.IsFinite(varied) || varied <= 0 || varied == previous)
                throw new InvalidOperationException("The scale must change the selected half extent to a positive finite value.");
            half = axis switch { 0 => half with { X = varied }, 1 => half with { Y = varied }, _ => half with { Z = varied } };
            string name = axis switch { 0 => "x", 1 => "y", _ => "z" };
            controls.Add(new(name, name.ToUpperInvariant(),
                Apply(model, exactBoneName, original.Center, half * 2, reviewed: true)));
        }
        return controls.MoveToImmutable();
    }

    private static Dl1AuthoredBoneBounds CreateBounds(Vector3D center, Vector3D fullSizes)
    {
        if (!center.IsFinite || !fullSizes.IsFinite || fullSizes.X < 0 || fullSizes.Y < 0 || fullSizes.Z < 0)
            throw new ArgumentException("Bounds must be finite with nonnegative sizes.");
        return new(center, fullSizes / 2);
    }

    private static int ExactBone(ImmutableArray<CustomModelBone> bones, string exactName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactName);
        int match = -1;
        for (int index = 0; index < bones.Length; index++)
        {
            if (!bones[index].Name.Equals(exactName, StringComparison.Ordinal))
                continue;
            if (match >= 0)
                throw new InvalidDataException("The exact bone name is ambiguous.");
            match = index;
        }
        return match >= 0 ? match : throw new ArgumentException("Select an exact rig bone for bounds editing.", nameof(exactName));
    }
}
