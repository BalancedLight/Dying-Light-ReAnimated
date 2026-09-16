using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

public sealed record SkinWeightInfluenceChoice(Guid EntityId, int BoneIndex, string Name);

/// <summary>An immutable observation of original source points, independent of draw-local palette order.</summary>
public sealed class SkinWeightAuthoringSnapshot
{
    internal SkinWeightAuthoringSnapshot(FbxModelAuthoringImportResult model, RiggingSession session,
        ImmutableArray<SkinWeightInfluenceChoice> influences, ImmutableArray<SkinWeightCorrectionPoint> points,
        ImmutableArray<Vector3D> positions, ImmutableArray<(int A, int B, int C)> triangles)
    { Model = model; Session = session; Influences = influences; Points = points; Positions = positions; Triangles = triangles; }
    internal FbxModelAuthoringImportResult Model { get; }
    public RiggingSession Session { get; }
    public ImmutableArray<SkinWeightInfluenceChoice> Influences { get; }
    public ImmutableArray<SkinWeightCorrectionPoint> Points { get; }
    public ImmutableArray<Vector3D> Positions { get; }
    public ImmutableArray<(int A, int B, int C)> Triangles { get; }
}

/// <summary>A correction preview can only be created from a validated source observation.</summary>
public sealed class SkinWeightAuthoringPreview
{
    internal SkinWeightAuthoringPreview(SkinWeightAuthoringSnapshot snapshot, SkinWeightCorrectionResult correction)
    { Snapshot = snapshot; Correction = correction; }
    internal SkinWeightAuthoringSnapshot Snapshot { get; }
    public SkinWeightCorrectionResult Correction { get; }
    public ImmutableArray<SkinWeightCorrectionPoint> SourcePoints => Snapshot.Points;
    public ImmutableArray<SkinWeightInfluenceChoice> Influences => Snapshot.Influences;
}

public static class FbxSkinWeightAuthoring
{
    public static bool TryRefreshMetadata(SkinWeightAuthoringSnapshot snapshot, SkinWeightAuthoringPreview? preview,
        FbxModelAuthoringImportResult current, out SkinWeightAuthoringSnapshot refreshed, out SkinWeightAuthoringPreview? refreshedPreview)
    {
        refreshed = snapshot; refreshedPreview = preview;
        var previous = snapshot.Model;
        if (previous.Surfaces != current.Surfaces || !ReferenceEquals(previous.Rig, current.Rig) ||
            previous.Package.Document.ModelId != current.Package.Document.ModelId ||
            previous.Package.Document.Source.ContentSha256 != current.Package.Document.Source.ContentSha256 ||
            previous.Package.Document.RigSignature != current.Package.Document.RigSignature ||
            !previous.Package.Document.Bones.SequenceEqual(current.Package.Document.Bones) || previous.Package.SourceFbx != current.Package.SourceFbx ||
            !(SameSessionInputs(previous.Package.Document.RiggingSession, current.Package.Document.RiggingSession) ||
                previous.Package.Document.RiggingSession is null && current.Package.Document.RiggingSession?.Matches(snapshot.Session.CreateJobToken()) == true) ||
            preview is not null && !ReferenceEquals(preview.Snapshot, snapshot)) return false;
        refreshed = new(current, current.Package.Document.RiggingSession ?? snapshot.Session, snapshot.Influences, snapshot.Points, snapshot.Positions, snapshot.Triangles);
        refreshedPreview = preview is null ? null : new(refreshed, preview.Correction);
        return true;
    }

    private static bool SameSessionInputs(RiggingSession? previous, RiggingSession? current) => ReferenceEquals(previous, current) ||
        previous is not null && current is not null && current.Matches(previous.CreateJobToken()) &&
        current.RequiresSourceReview == previous.RequiresSourceReview && current.PreviousSourceSha256 == previous.PreviousSourceSha256;

    public static SkinWeightAuthoringSnapshot Inspect(FbxModelAuthoringImportResult model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        var document = model.Package.Document;
        document.Validate();
        if (document.Bones.IsEmpty) throw new InvalidOperationException("Create or import a rig before assigning skin weights.");
        var session = document.RiggingSession ?? RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        var observed = RiggingSessions.ObserveSourceHierarchy(document with { RiggingSession = session });
        var influences = document.Bones.Select(b => new SkinWeightInfluenceChoice(observed[b.Index].EntityId, b.Index, b.Name)).ToImmutableArray();
        var rows = new Dictionary<(string Component, int Point), (Vector3D Position, ImmutableArray<GeneratedSkinInfluence> Weights)>();
        var edges = new HashSet<((string Component, int Point) A, (string Component, int Point) B)>();
        var triangles = new List<((string Component, int Point) A, (string Component, int Point) B, (string Component, int Point) C)>();
        foreach (var surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = surface.SourceGeometry ?? throw new InvalidDataException("Weight editing requires original source-point correspondence.");
            if (surface.SourceCorners.Length != surface.Vertices.Length || surface.Indices.Length % 3 != 0 ||
                surface.Indices.Any(i => i >= surface.Vertices.Length)) throw new InvalidDataException("The draw has invalid source corner or triangle correspondence.");
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                int point = surface.SourceCorners[i].ControlPointIndex;
                var vertex = surface.Vertices[i];
                if ((uint)point >= (uint)geometry.ControlPoints.Length || vertex.BoneIndices.Length != vertex.BoneWeights.Length ||
                    vertex.BoneIndices.Any(slot => (uint)slot >= (uint)surface.PaletteBoneIndices.Length)) throw new InvalidDataException("A source point has an invalid palette or vertex reference.");
                var weights = vertex.BoneIndices.Select((slot, index) => {
                    int bone = surface.PaletteBoneIndices[slot];
                    if ((uint)bone >= (uint)influences.Length) throw new InvalidDataException("A draw-local palette does not identify a model bone.");
                    double weight = vertex.BoneWeights[index];
                    if (!double.IsFinite(weight) || weight < 0) throw new InvalidDataException("Inspect and repair invalid source weights before correction.");
                    return new GeneratedSkinInfluence(influences[bone].EntityId, weight);
                }).Where(static w => w.Weight > 0).OrderBy(static w => w.HandleId).ToImmutableArray();
                if (weights.Select(static w => w.HandleId).Distinct().Count() != weights.Length) throw new InvalidDataException("A source point repeats an influence; reconcile its original weights before editing.");
                var key = (geometry.Id, point);
                if (rows.TryGetValue(key, out var prior) && (prior.Position != vertex.Position || !prior.Weights.SequenceEqual(weights)))
                    throw new InvalidDataException("A source point has conflicting weights or positions across seams. Reconcile that correspondence before editing.");
                rows[key] = (vertex.Position, weights);
                if (rows.Count > 250_000) throw new InvalidDataException("This weight-editing observation exceeds the supported point budget.");
            }
            for (int i = 0; i < surface.Indices.Length; i += 3)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                triangles.Add(((geometry.Id, surface.SourceCorners[(int)surface.Indices[i]].ControlPointIndex),
                    (geometry.Id, surface.SourceCorners[(int)surface.Indices[i + 1]].ControlPointIndex),
                    (geometry.Id, surface.SourceCorners[(int)surface.Indices[i + 2]].ControlPointIndex)));
                if (triangles.Count > 2_000_000) throw new InvalidDataException("The weight surface exceeds its supported triangle budget.");
                for (int corner = 0; corner < 3; corner++)
                {
                    int a = surface.SourceCorners[(int)surface.Indices[i + corner]].ControlPointIndex;
                    int b = surface.SourceCorners[(int)surface.Indices[i + (corner + 1) % 3]].ControlPointIndex;
                    if (a != b) { edges.Add(((geometry.Id, a), (geometry.Id, b))); edges.Add(((geometry.Id, b), (geometry.Id, a))); }
                }
                if (edges.Count > 4_000_000) throw new InvalidDataException("This weight-editing observation exceeds the supported adjacency budget.");
            }
        }
        var keys = rows.Keys.OrderBy(static k => k.Component, StringComparer.Ordinal).ThenBy(static k => k.Point).ToArray();
        var indexByKey = keys.Select((k, i) => (k, i)).ToDictionary(static p => p.k, static p => p.i);
        var neighbors = edges.ToLookup(static edge => edge.A, edge => indexByKey[edge.B]);
        var locks = session.WeightLocks.ToLookup(static l => (l.ComponentId, l.ControlPointIndex), static l => l.EntityId);
        if (session.WeightLocks.Any(l => !indexByKey.ContainsKey((l.ComponentId, l.ControlPointIndex)) || !influences.Any(b => b.EntityId == l.EntityId)))
            throw new InvalidDataException("A saved weight lock no longer identifies an observed source point and bone.");
        var points = keys.Select(k => new SkinWeightCorrectionPoint(k.Component, k.Point, rows[k].Weights,
            locks[k].Order().ToImmutableArray(), neighbors[k].Order().ToImmutableArray())).ToImmutableArray();
        return new(model, session, influences, points, keys.Select(k => rows[k].Position).ToImmutableArray(),
            triangles.Select(t => (indexByKey[t.A], indexByKey[t.B], indexByKey[t.C])).ToImmutableArray());
    }

    public static SkinWeightAuthoringPreview Preview(SkinWeightAuthoringSnapshot snapshot,
        IReadOnlyList<SkinWeightCorrectionSelection> selection, SkinWeightCorrectionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (options.MaximumInfluences is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(options), "Native source corrections support one to four influences.");
        if (options.TargetInfluence is { } target && !snapshot.Influences.Any(b => b.EntityId == target)) throw new InvalidDataException("Choose an observed model bone for the correction.");
        if (snapshot.Session.MirrorWeightEdits)
        {
            if (options.Kind != SkinWeightCorrectionKind.SetInfluence || options.TargetInfluence is not { } sourceInfluence)
                throw new InvalidDataException("Paired correction currently requires Set influence weight. Disable mirroring before normalization or smoothing.");
            var pair = snapshot.Session.WeightMirrorPairs.SingleOrDefault(p => p.EntityId == sourceInfluence || p.CounterpartEntityId == sourceInfluence)
                ?? throw new InvalidDataException("Save an explicit counterpart for this influence before mirrored correction.");
            Guid counterpart = pair.EntityId == sourceInfluence ? pair.CounterpartEntityId : pair.EntityId;
            var map = SkinWeightMirroring.Build(snapshot.Points, snapshot.Positions, snapshot.Session.SymmetryOrigin, snapshot.Session.SymmetryNormal,
                snapshot.Session.WeightMirrorTolerance, cancellationToken);
            return new(snapshot, SkinWeightMirroring.SetMirrored(snapshot.Points, selection, map, sourceInfluence, counterpart, options.TargetWeight, cancellationToken));
        }
        return new(snapshot, SkinWeightCorrection.Compute(snapshot.Points, selection, options, cancellationToken));
    }

    public static bool TrySetMirroring(FbxModelAuthoringImportResult model, SkinWeightAuthoringSnapshot snapshot, Guid influence,
        Guid? counterpart, bool enabled, Vector3D origin, Vector3D normal, double tolerance, out FbxModelAuthoringImportResult result)
    {
        result = model;
        if (!ReferenceEquals(model, snapshot.Model)) return false;
        if (!snapshot.Influences.Any(b => b.EntityId == influence) || counterpart is { } id && !snapshot.Influences.Any(b => b.EntityId == id) || enabled && counterpart is null)
            throw new ArgumentException("Choose two observed model influences for a mirror pair.");
        var pairs = counterpart is not { } target ? snapshot.Session.WeightMirrorPairs : snapshot.Session.WeightMirrorPairs.Where(p => p.EntityId != influence && p.CounterpartEntityId != influence &&
            p.EntityId != target && p.CounterpartEntityId != target).Append(influence.CompareTo(target) <= 0 ? new RigWeightMirrorPair(influence, target) : new RigWeightMirrorPair(target, influence))
            .OrderBy(static p => p.EntityId).ToImmutableArray();
        var candidate = snapshot.Session with { WeightMirrorPairs = pairs, MirrorWeightEdits = enabled, SymmetryOrigin = origin,
            SymmetryNormal = normal, WeightMirrorTolerance = tolerance };
        candidate.Validate();
        if (candidate.ComputeInputFingerprint() == snapshot.Session.ComputeInputFingerprint()) return true;
        var session = RiggingSessions.Change(snapshot.Session, candidate, origin == snapshot.Session.SymmetryOrigin && normal == snapshot.Session.SymmetryNormal
            ? RiggingEditKind.Skinning : RiggingEditKind.Coordinates);
        result = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session, LastBuildReceipt = null } } };
        result.Package.Document.Validate(); return true;
    }

    public static bool TryApply(FbxModelAuthoringImportResult model, SkinWeightAuthoringPreview preview,
        out FbxModelAuthoringImportResult result, CancellationToken cancellationToken = default)
    {
        result = model;
        ArgumentNullException.ThrowIfNull(preview);
        var snapshot = preview.Snapshot;
        if (!ReferenceEquals(model, snapshot.Model)) return false;
        if (!preview.Correction.CanApply) throw new InvalidDataException("Resolve the correction preview diagnostics before applying.");
        if (preview.Correction.Changes.IsEmpty) return true;
        var changes = preview.Correction.Changes.ToDictionary(change => (
            snapshot.Points[change.PointIndex].ComponentId, snapshot.Points[change.PointIndex].ControlPointIndex));
        var affected = changes.Keys.Select(static k => k.ComponentId).ToHashSet(StringComparer.Ordinal);
        var boneById = snapshot.Influences.ToDictionary(static b => b.EntityId, static b => b.BoneIndex);
        var globals = new TransformMatrix[model.Package.Document.Bones.Length];
        foreach (var bone in model.Package.Document.Bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var componentInverses = new Dictionary<string, Dictionary<int, TransformMatrix>>(StringComparer.Ordinal);
        foreach (var surface in model.Surfaces.Where(s => affected.Contains(s.SourceGeometry!.Id)))
        {
            string id = surface.SourceGeometry!.Id;
            if (!componentInverses.TryGetValue(id, out var inverses)) componentInverses.Add(id, inverses = []);
            if (surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length) throw new InvalidDataException("The component has incomplete source inverse binds.");
            for (int slot = 0; slot < surface.PaletteBoneIndices.Length; slot++)
            {
                int bone = surface.PaletteBoneIndices[slot];
                var matrix = surface.InverseBindMatrices[slot];
                if (inverses.TryGetValue(bone, out var prior) && prior != matrix) throw new InvalidDataException("The component has conflicting inverse binds across draws.");
                inverses[bone] = matrix;
            }
        }
        var surfaces = ImmutableArray.CreateBuilder<FbxModelSurface>();
        foreach (var surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = surface.SourceGeometry!.Id;
            if (!affected.Contains(id)) { surfaces.Add(surface); continue; }
            // Keep every original expert inverse bind. Only newly assigned influences receive their exact current bind inverse.
            var inverseByBone = componentInverses[id];
            var vertices = surface.Vertices.Select((v, i) => {
                int point = surface.SourceCorners[i].ControlPointIndex;
                if (!changes.TryGetValue((id, point), out var change))
                    return v with { BoneIndices = v.BoneIndices.Select(slot => surface.PaletteBoneIndices[slot]).ToImmutableArray() };
                return v with { BoneIndices = change.After.Select(w => boneById[w.HandleId]).ToImmutableArray(), BoneWeights = change.After.Select(static w => w.Weight).ToImmutableArray() };
            }).ToImmutableArray();
            var palette = Enumerable.Range(0, globals.Length).ToImmutableArray();
            var inverse = palette.Select(b => inverseByBone.TryGetValue(b, out var original) ? original : globals[b].InvertedAffine()).ToImmutableArray();
            surfaces.AddRange(FbxAuthoredSurfacePartitioner.Partition(surface with { Vertices = vertices, PaletteBoneIndices = palette, InverseBindMatrices = inverse }, cancellationToken));
        }
        var session = RiggingSessions.Change(snapshot.Session, snapshot.Session, RiggingEditKind.Skinning);
        var updatedSurfaces = surfaces.ToImmutable();
        var usedBones = updatedSurfaces.SelectMany(s => s.Vertices.SelectMany(v => v.BoneIndices.Select(slot => s.PaletteBoneIndices[slot]))).ToHashSet();
        var bones = model.Package.Document.Bones.Select(b => usedBones.Contains(b.Index) && !b.IsWeighted ? b with { IsWeighted = true } : b).ToImmutableArray();
        var document = model.Package.Document with { RiggingSession = session, LastBuildReceipt = null, Bones = bones };
        document = document with { RigSignature = CustomModelContractSignatures.ComputeRig(document.CreateEffectiveBones()) };
        result = FbxAuthoredModelLayer.Capture(model with { Package = model.Package with { Document = document }, Surfaces = updatedSurfaces,
            Rig = bones.SequenceEqual(model.Package.Document.Bones) ? model.Rig : document.CreateRigDefinition() }, cancellationToken);
        return true;
    }

    public static bool TrySetLocks(FbxModelAuthoringImportResult model, SkinWeightAuthoringSnapshot snapshot,
        IReadOnlyList<int> pointIndices, Guid influenceId, bool locked, out FbxModelAuthoringImportResult result)
    {
        result = model;
        if (!ReferenceEquals(model, snapshot.Model)) return false;
        if (!snapshot.Influences.Any(i => i.EntityId == influenceId) || pointIndices.Count == 0 || pointIndices.Distinct().Count() != pointIndices.Count ||
            pointIndices.Any(i => (uint)i >= (uint)snapshot.Points.Length)) throw new ArgumentException("Weight locks require a nonempty selection of observed points and one observed influence.");
        var locks = snapshot.Session.WeightLocks.ToHashSet();
        foreach (int i in pointIndices)
        {
            var point = snapshot.Points[i];
            var entry = new RigSkinWeightLock(point.ComponentId, point.ControlPointIndex, influenceId);
            if (locked) locks.Add(entry); else locks.Remove(entry);
        }
        if (locks.SetEquals(snapshot.Session.WeightLocks)) return true;
        var session = RiggingSessions.Change(snapshot.Session, snapshot.Session with {
            WeightLocks = locks.OrderBy(static l => l.ComponentId, StringComparer.Ordinal).ThenBy(static l => l.ControlPointIndex).ThenBy(static l => l.EntityId).ToImmutableArray(),
        }, RiggingEditKind.Skinning);
        result = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session, LastBuildReceipt = null } } };
        result.Package.Document.Validate();
        return true;
    }
}
