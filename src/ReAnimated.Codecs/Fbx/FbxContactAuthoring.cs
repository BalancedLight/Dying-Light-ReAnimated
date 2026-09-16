using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

public sealed class ContactAuthoringSnapshot
{
    internal ContactAuthoringSnapshot(FbxModelAuthoringImportResult model, Guid parentId, string componentId,
        ImmutableArray<int> pointIds, TransformMatrix parentGlobal, ContactFootprintFit fit)
    { Model = model; ParentEntityId = parentId; ComponentId = componentId; ControlPointIds = pointIds; ParentGlobal = parentGlobal; Fit = fit; }
    internal FbxModelAuthoringImportResult Model { get; }
    public Guid ParentEntityId { get; }
    public string ComponentId { get; }
    public ImmutableArray<int> ControlPointIds { get; }
    public TransformMatrix ParentGlobal { get; }
    public ContactFootprintFit Fit { get; }
}

/// <summary>Original point identity plus exact bind-deformed geometry; no edits to skinning or source surfaces.</summary>
public static class FbxContactAuthoring
{
    public static ContactAuthoringSnapshot Inspect(FbxModelAuthoringImportResult model, Guid parentEntityId,
        string componentId, double? minimumParentWeight, IReadOnlySet<int>? controlPointIds,
        ContactFootprintOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        var document = model.Package.Document;
        document.Validate();
        var session = document.RiggingSession ?? throw new InvalidOperationException("Start a studio session before fitting contacts.");
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        int parentIndex = -1;
        for (int i = 0; i < observed.Length; i++) if (observed[i].EntityId == parentEntityId) parentIndex = i;
        if (parentIndex < 0 || parentIndex >= document.Bones.Length || !session.Recipe.Entities.Any(e => e.EntityId == parentEntityId && e.OwnerAssetId == document.ModelId &&
            (e.Kind == RigNativeEntityKind.Bone || e.Kind == RigNativeEntityKind.Unknown && document.Bones[parentIndex].Kind == BoneKind.Deform)))
            throw new ArgumentException("Select an observed foot bone from this model.", nameof(parentEntityId));
        if (minimumParentWeight is { } threshold && (!double.IsFinite(threshold) || threshold is <= 0 or > 1))
            throw new ArgumentException("The selected foot's weight threshold must be greater than zero and at most one.", nameof(minimumParentWeight));
        var bones = document.CreateEffectiveBones();
        // Contact geometry must use the exact authored bind, including affine
        // shear or non-unit scale. RigDefinition's preview bind intentionally
        // projects those matrices to TRS, which is unsuitable for source
        // footprint placement. Surface inverse binds remain per-draw below.
        var globals = new TransformMatrix[bones.Length];
        foreach (CustomModelBone bone in bones)
        {
            globals[bone.Index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        }
        var footBranch = new bool[bones.Length];
        for (int i = 0; i < bones.Length; i++) footBranch[i] = i == parentIndex || bones[i].ParentIndex >= 0 && footBranch[bones[i].ParentIndex];
        var positions = new Dictionary<int, Vector3D>();
        var sourceIds = new HashSet<int>();
        bool foundComponent = false;
        foreach (var surface in model.Surfaces.Where(s => s.SourceGeometry?.Id == componentId))
        {
            foundComponent = true;
            var geometry = surface.SourceGeometry!;
            if (surface.SourceCorners.Length != surface.Vertices.Length || surface.IsSkinned && surface.InverseBindMatrices.Length != surface.PaletteBoneIndices.Length)
                throw new InvalidDataException("The selected component has incomplete corner or bind correspondence.");
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                int id = surface.SourceCorners[i].ControlPointIndex;
                if ((uint)id >= (uint)geometry.ControlPoints.Length) throw new InvalidDataException("A draw corner references a missing source point.");
                sourceIds.Add(id);
                if (controlPointIds is not null && !controlPointIds.Contains(id)) continue;
                var vertex = surface.Vertices[i];
                Vector3D position = vertex.Position;
                double total = 0, owned = 0;
                if (surface.IsSkinned)
                {
                    if (vertex.BoneIndices.Length != vertex.BoneWeights.Length) throw new InvalidDataException("The selected component has mismatched weights.");
                    position = Vector3D.Zero;
                    for (int j = 0; j < vertex.BoneIndices.Length; j++)
                    {
                        int slot = vertex.BoneIndices[j]; double weight = vertex.BoneWeights[j];
                        if ((uint)slot >= (uint)surface.PaletteBoneIndices.Length || !double.IsFinite(weight) || weight < 0)
                            throw new InvalidDataException("The selected component has an invalid skin palette or weight.");
                        int bone = surface.PaletteBoneIndices[slot];
                        if ((uint)bone >= (uint)globals.Length) throw new InvalidDataException("The selected component references a missing bone.");
                        if (weight == 0) continue;
                        var matrix = globals[bone] * surface.InverseBindMatrices[slot];
                        if (!matrix.IsFinite) throw new InvalidDataException("The selected component has a non-finite bind transform.");
                        position += matrix.TransformPoint(vertex.Position) * weight;
                        total += weight;
                        if (footBranch[bone]) owned += weight;
                    }
                    if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("A skinned shoe point has no valid positive influence.");
                    position /= total;
                }
                if (minimumParentWeight is { } minimum && (total <= 0 || owned / total < minimum)) continue;
                if (!position.IsFinite) throw new InvalidDataException("The selected component contains a non-finite bind-deformed position.");
                if (positions.TryGetValue(id, out var previous) && (position - previous).Length > 1e-8 * Math.Max(1, position.Length))
                    throw new InvalidDataException("One source point has conflicting bind-deformed positions across draw seams.");
                positions[id] = position;
                if (positions.Count > 250_000) throw new InvalidDataException("Contact fitting exceeds the selected point budget.");
            }
        }
        if (!foundComponent) throw new ArgumentException("The selected source component is missing.", nameof(componentId));
        if (controlPointIds is not null && (controlPointIds.Count == 0 || controlPointIds.Any(id => !sourceIds.Contains(id))))
            throw new ArgumentException("The point selection is empty or contains missing source control points.", nameof(controlPointIds));
        var keys = positions.Keys.Order().ToImmutableArray();
        var fit = ContactFootprintSolver.Fit(keys.Select(id => positions[id]).ToArray(), globals[parentIndex], options, cancellationToken);
        return new(model, parentEntityId, componentId, keys, globals[parentIndex], fit);
    }

    public static ContactAuthoringSnapshot? RefreshMetadata(ContactAuthoringSnapshot snapshot, FbxModelAuthoringImportResult current)
    {
        var previous = snapshot.Model;
        if (previous.Surfaces != current.Surfaces || !ReferenceEquals(previous.Rig, current.Rig) ||
            previous.Package.Document.ModelId != current.Package.Document.ModelId || previous.Package.SourceFbx != current.Package.SourceFbx ||
            previous.Package.Document.Source.ContentSha256 != current.Package.Document.Source.ContentSha256 ||
            previous.Package.Document.RigSignature != current.Package.Document.RigSignature ||
            !previous.Package.Document.Bones.SequenceEqual(current.Package.Document.Bones) ||
            !previous.Package.Document.AuthoredHelpers.SequenceEqual(current.Package.Document.AuthoredHelpers) ||
            current.Package.Document.RiggingSession?.Matches(previous.Package.Document.RiggingSession!.CreateJobToken()) != true) return null;
        return new(current, snapshot.ParentEntityId, snapshot.ComponentId, snapshot.ControlPointIds, snapshot.ParentGlobal, snapshot.Fit);
    }

    public static FbxModelAuthoringImportResult Apply(ContactAuthoringSnapshot snapshot, FbxModelAuthoringImportResult current,
        Guid? helperEntityId, string name, string roleId, TransformMatrix localFrame, Vector3D center, Vector3D halfExtents, bool overridden)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.Model, current)) throw new InvalidOperationException("The model changed. Fit and review the contact again.");
        var document = RigContactHelperAuthoring.Apply(current.Package.Document, current.Package.Document.RiggingSession!.CreateJobToken(),
            snapshot.ParentEntityId, helperEntityId, name, roleId, localFrame, center, halfExtents,
            overridden ? RigEvidenceKind.UserOverride : RigEvidenceKind.GeometryInference,
            $"contact-footprint-v1; source component {snapshot.ComponentId}; {snapshot.ControlPointIds.Length} selected original points; authoring review, native behavior unverified");
        return ReferenceEquals(document, current.Package.Document) ? current : current with
        { Package = current.Package with { Document = document }, Rig = document.CreateRigDefinition() };
    }
}
