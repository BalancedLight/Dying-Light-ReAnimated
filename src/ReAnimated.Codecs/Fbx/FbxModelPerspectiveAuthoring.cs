using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Mapping;
using System.Globalization;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxPerspectiveTriangleReview(
    CustomModelPerspectiveTriangleKey Key,
    bool ProposedHidden,
    bool Certain,
    string Reason);

public sealed record FbxModelPerspectiveProposal(
    CustomModelPerspectiveSelection Selection,
    ImmutableArray<FbxPerspectiveTriangleReview> Reviews);

public sealed record FbxModelPerspectiveExportView(
    FbxModelAuthoringImportResult Model,
    ImmutableArray<string> RetainedMorphNames,
    ImmutableArray<string> DroppedMorphNames);

/// <summary>Builds derived FPP/TPP surface views without mutating an imported model.</summary>
public static class FbxModelPerspectiveAuthoring
{
    public static FbxModelPerspectiveProposal ProposeFirstPerson(
        FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string sourceHash = model.Package.Document.Source.ContentSha256;
        string fingerprint = ComputeGeometryFingerprint(model);
        HashSet<int> headBones = FindHeadBranchBones(model);
        var reviews = ImmutableArray.CreateBuilder<FbxPerspectiveTriangleReview>();

        foreach (FbxModelSurface surface in model.Surfaces)
        {
            if (surface.SourceTriangles.IsDefaultOrEmpty)
                continue;
            ValidateSurfaceShape(surface);
            string component = surface.SourceGeometry?.Id ?? surface.Id;
            for (int index = 0; index < surface.SourceTriangles.Length; index++)
            {
                GeometrySourceTriangle triangle = surface.SourceTriangles[index];
                CustomModelPerspectiveTriangleKey key = new(component, triangle);
                int a = checked((int)surface.Indices[index * 3]);
                int b = checked((int)surface.Indices[index * 3 + 1]);
                int c = checked((int)surface.Indices[index * 3 + 2]);
                FbxModelVertex[] corners = [surface.Vertices[a], surface.Vertices[b], surface.Vertices[c]];
                bool weighted = corners.All(static vertex => !vertex.BoneIndices.IsEmpty && !vertex.BoneWeights.IsEmpty);
                double[] masses = corners.Select(vertex => HeadMass(vertex, surface, headBones)).ToArray();
                double[] totals = corners.Select(static vertex => vertex.BoneWeights.Where(static weight => weight > 0).Sum()).ToArray();
                bool certain = weighted && totals.All(static total => total > 0.0 && double.IsFinite(total)) &&
                    (masses.Zip(totals).All(static pair => pair.First / pair.Second >= 0.95) ||
                     masses.All(static mass => mass <= 0.0));
                bool head = certain && masses.Zip(totals).All(static pair => pair.First / pair.Second >= 0.95);
                reviews.Add(new(key, head, certain, head
                    ? "All retained corner weights resolve to the reviewed head branch."
                    : certain ? "Triangle has mixed or insufficient head-branch weight evidence."
                    : "Unweighted or mixed corners require an explicit visibility choice."));
            }
        }

        CustomModelPerspectiveSelection selection = new()
        {
            Perspective = CustomModelPerspective.FirstPerson,
            SourceSha256 = sourceHash,
            SourceGeometryFingerprint = fingerprint,
            HiddenTriangles = reviews.Where(static row => row.ProposedHidden).Select(static row => row.Key).ToImmutableArray(),
        };
        selection.Validate();
        return new(selection, reviews.ToImmutable());
    }

    public static FbxModelAuthoringImportResult Apply(
        FbxModelAuthoringImportResult model,
        CustomModelPerspectiveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(selection);
        selection.Validate();
        if (!string.Equals(selection.SourceSha256, model.Package.Document.Source.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(selection.SourceGeometryFingerprint, ComputeGeometryFingerprint(model), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The perspective selection belongs to a different source model or geometry revision.");
        ValidateSelectionIds(model, selection);
        if (selection.Perspective == CustomModelPerspective.ThirdPerson) return model;

        ImmutableArray<FbxModelSurface> surfaces = model.Surfaces
            .SelectMany(surface => FilterSurface(surface, selection))
            .ToImmutableArray();
        return model with { Surfaces = surfaces };
    }

    /// <summary>Creates an export-only FPP view; the editable source model is unchanged.</summary>
    public static FbxModelPerspectiveExportView CreateFirstPersonExportView(
        FbxModelAuthoringImportResult model,
        CustomModelPerspectiveSelection selection)
    {
        ArgumentNullException.ThrowIfNull(model);
        FbxModelAuthoringImportResult visible = Apply(model, selection);
        HashSet<string> retained = visible.Surfaces
            .SelectMany(static surface => surface.MorphTargets.Select(target => target.Name))
            .ToHashSet(StringComparer.Ordinal);
        ImmutableArray<CustomModelMorphChannel> channels = model.Package.Document.MorphChannels
            .Where(channel => retained.Contains(channel.Name))
            .Select((channel, index) => channel with { Index = index })
            .ToImmutableArray();
        HashSet<string> retainedNames = channels.Select(static channel => channel.Name)
            .ToHashSet(StringComparer.Ordinal);
        CustomModelDocument sourceDocument = model.Package.Document;
        FacialPresetLibrary facial = FilterFacialPresets(sourceDocument.FacialPresets, retainedNames);
        ImmutableArray<string> allNames = sourceDocument.MorphChannels.Select(static channel => channel.Name).ToImmutableArray();
        HashSet<string> droppedNames = allNames.Where(name => !retainedNames.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        CustomModelDocument exportDocument = sourceDocument with
        {
            MorphChannels = channels,
            MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(channels, visible.Surfaces),
            FacialPresets = facial,
        };
        exportDocument.Validate();
        ImmutableDictionary<Guid, AnimationClip> clips = model.AnimationClips
            .ToImmutableDictionary(pair => pair.Key, pair => FilterMorphTracks(pair.Value, droppedNames));
        FbxModelAuthoringImportResult exportModel = visible with
        {
            Package = visible.Package with { Document = exportDocument },
            AnimationClips = clips,
        };
        return new(exportModel, channels.Select(static channel => channel.Name).ToImmutableArray(),
            droppedNames.ToImmutableArray());
    }

    public static string ComputeGeometryFingerprint(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (model.Rig is { } rig)
            foreach (BoneDefinition bone in rig.Bones)
            {
                Add(hash, bone.Name);
                Add(hash, bone.SemanticRole ?? string.Empty);
                Add(hash, bone.ParentIndex.ToString(CultureInfo.InvariantCulture));
            }
        foreach (FbxModelSurface surface in model.Surfaces)
        {
            string component = surface.SourceGeometry?.Id ?? surface.Id;
            Add(hash, component);
            Add(hash, surface.Id);
            Add(hash, surface.MeshName);
            Add(hash, surface.MaterialId.ToString("N", CultureInfo.InvariantCulture));
            foreach (FbxModelVertex vertex in surface.Vertices)
            {
                Add(hash, vertex.Position.X.ToString("R", CultureInfo.InvariantCulture));
                Add(hash, vertex.Position.Y.ToString("R", CultureInfo.InvariantCulture));
                Add(hash, vertex.Position.Z.ToString("R", CultureInfo.InvariantCulture));
                foreach (int bone in vertex.BoneIndices) Add(hash, bone.ToString(CultureInfo.InvariantCulture));
                foreach (double weight in vertex.BoneWeights) Add(hash, weight.ToString("R", CultureInfo.InvariantCulture));
            }
            foreach (int bone in surface.PaletteBoneIndices) Add(hash, bone.ToString(CultureInfo.InvariantCulture));
            foreach (TransformMatrix matrix in surface.InverseBindMatrices)
                foreach (double value in MatrixValues(matrix)) Add(hash, value.ToString("R", CultureInfo.InvariantCulture));
            foreach (GeometrySourceTriangle triangle in surface.SourceTriangles)
            {
                Add(hash, triangle.PolygonIndex.ToString(CultureInfo.InvariantCulture));
                Add(hash, triangle.TriangleInPolygon.ToString(CultureInfo.InvariantCulture));
            }
            foreach (uint index in surface.Indices) Add(hash, index.ToString(CultureInfo.InvariantCulture));
            foreach (GeometrySourceCorner corner in surface.SourceCorners)
            {
                Add(hash, corner.ControlPointIndex.ToString(CultureInfo.InvariantCulture));
                Add(hash, corner.PolygonVertexIndex.ToString(CultureInfo.InvariantCulture));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
        static void Add(IncrementalHash hash, string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        static IEnumerable<double> MatrixValues(TransformMatrix matrix) =>
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        ];
    }

    private static ImmutableArray<FbxModelSurface> FilterSurface(
        FbxModelSurface surface,
        CustomModelPerspectiveSelection selection)
    {
        string component = surface.SourceGeometry?.Id ?? surface.Id;
        CustomModelPerspectiveSurfaceKey surfaceKey = new(component, surface.Id);
        if (selection.HiddenSurfaces.Contains(surfaceKey)) return [];
        if (surface.SourceTriangles.IsDefaultOrEmpty) return [surface];
        ValidateSurfaceShape(surface);
        List<int> kept = [];
        for (int i = 0; i < surface.SourceTriangles.Length; i++)
            if (!selection.Hides(component, surface.Id, surface.SourceTriangles[i])) kept.Add(i);
        if (kept.Count == surface.SourceTriangles.Length) return [surface];
        if (kept.Count == 0) return [];
        var used = kept.SelectMany(i => new[] { (int)surface.Indices[i * 3], (int)surface.Indices[i * 3 + 1], (int)surface.Indices[i * 3 + 2] }).Distinct().ToArray();
        Dictionary<int, int> remap = used.Select((old, next) => (old, next)).ToDictionary(static x => x.old, static x => x.next);
        ImmutableArray<uint> indices = kept.SelectMany(i => new[] { surface.Indices[i * 3], surface.Indices[i * 3 + 1], surface.Indices[i * 3 + 2] }).Select(index => (uint)remap[(int)index]).ToImmutableArray();
        ImmutableArray<FbxModelVertex> vertices = used.Select(index => surface.Vertices[index]).ToImmutableArray();
        ImmutableArray<FbxModelMorphTarget> morphs = surface.MorphTargets.Select(morph => morph with
        {
            PositionDeltas = used.Select(index => morph.PositionDeltas[index]).ToImmutableArray(),
            NormalDeltas = morph.NormalDeltas.IsDefaultOrEmpty ? [] : used.Select(index => morph.NormalDeltas[index]).ToImmutableArray(),
        }).ToImmutableArray();
        return [surface with
        {
            Vertices = vertices, Indices = indices,
            SourceCorners = used.Select(index => surface.SourceCorners[index]).ToImmutableArray(),
            SourceTriangles = kept.Select(index => surface.SourceTriangles[index]).ToImmutableArray(),
            MorphTargets = morphs,
        }];
    }

    private static HashSet<int> FindHeadBranchBones(FbxModelAuthoringImportResult model)
    {
        HashSet<int> direct = model.Rig?.Bones
            .Select((bone, index) => (bone, index))
            .Where(static x => string.Equals(
                x.bone.SemanticRole ?? HumanoidBoneSemanticClassifier.Classify(x.bone.Name)?.Role,
                "body.head",
                StringComparison.Ordinal))
            .Select(static x => x.index).ToHashSet() ?? [];
        if (model.Rig is { } rig)
        {
            bool changed;
            do
            {
                changed = false;
                for (int index = 0; index < rig.Bones.Length; index++)
                    if (!direct.Contains(index) && direct.Contains(rig.Bones[index].ParentIndex) && direct.Add(index))
                        changed = true;
            }
            while (changed);
        }
        return direct;
    }

    private static double HeadMass(FbxModelVertex vertex, FbxModelSurface surface, HashSet<int> headBones) =>
        vertex.BoneIndices
            .Select((slot, index) => (slot, index))
            .Where(pair => headBones.Contains(surface.PaletteBoneIndices[pair.slot]))
            .Sum(pair => Math.Max(0.0, vertex.BoneWeights[pair.index]));

    private static void ValidateSelectionIds(
        FbxModelAuthoringImportResult model,
        CustomModelPerspectiveSelection selection)
    {
        HashSet<CustomModelPerspectiveSurfaceKey> surfaces = model.Surfaces
            .Select(surface => new CustomModelPerspectiveSurfaceKey(
                surface.SourceGeometry?.Id ?? surface.Id,
                surface.Id)).ToHashSet();
        HashSet<CustomModelPerspectiveTriangleKey> triangles = model.Surfaces
            .SelectMany(surface => surface.SourceTriangles.Select(triangle =>
                new CustomModelPerspectiveTriangleKey(
                    surface.SourceGeometry?.Id ?? surface.Id,
                    triangle))).ToHashSet();
        if (selection.HiddenSurfaces.Concat(selection.KeptSurfaces).Any(key => !surfaces.Contains(key)) ||
            selection.HiddenTriangles.Any(key => !triangles.Contains(key)))
            throw new InvalidOperationException("Perspective selection contains an unknown source surface or triangle identity.");
    }

    private static FacialPresetLibrary FilterFacialPresets(
        FacialPresetLibrary source,
        HashSet<string> retained)
    {
        static ImmutableDictionary<string, double> Filter(
            ImmutableDictionary<string, double> values,
            IReadOnlySet<string> names) => values
                .Where(pair => names.Contains(pair.Key))
                .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return source with
        {
            Controls = source.Controls.Where(control => retained.Contains(control.MorphName))
                .Select(control => control with
                {
                    PartnerName = control.PartnerName is { } partner && retained.Contains(partner) ? partner : null,
                }).ToImmutableArray(),
            Presets = source.Presets.Select(preset => preset with
            {
                Weights = Filter(preset.Weights, retained),
                SpeechWeights = Filter(preset.SpeechWeights, retained),
            }).ToImmutableArray(),
        };
    }

    private static AnimationClip FilterMorphTracks(
        AnimationClip clip,
        HashSet<string> dropped) =>
        new(
            clip.Name,
            clip.FrameRate,
            clip.FrameCount,
            clip.TransformTracks,
            clip.ScalarTracks.Where(track => !dropped.Contains(track.ChannelName)).ToImmutableArray(),
            clip.AuxiliaryTransformTracks);


    private static void ValidateSurfaceShape(FbxModelSurface surface)
    {
        if (surface.SourceTriangles.Length * 3 != surface.Indices.Length ||
            surface.SourceCorners.Length != surface.Vertices.Length ||
            surface.Indices.Any(index => index >= surface.Vertices.Length) ||
            surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length ||
            surface.Vertices.Any(vertex => vertex.BoneIndices.Length != vertex.BoneWeights.Length ||
                vertex.BoneIndices.Any(slot => slot < 0 || slot >= surface.PaletteBoneIndices.Length) ||
                vertex.BoneWeights.Any(weight => !double.IsFinite(weight) || weight < 0.0)) ||
            surface.MorphTargets.Any(morph => morph.PositionDeltas.Length != surface.Vertices.Length ||
                (!morph.NormalDeltas.IsDefaultOrEmpty && morph.NormalDeltas.Length != surface.Vertices.Length)))
            throw new InvalidDataException("Perspective filtering requires complete source, skin and morph arrays.");
    }
}
