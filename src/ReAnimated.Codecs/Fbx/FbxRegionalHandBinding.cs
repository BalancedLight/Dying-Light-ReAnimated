using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Immutable regional candidate. Only the adapter can construct its validated weight transaction.</summary>
public sealed class RegionalHandBindingPreview
{
    internal RegionalHandBindingPreview(SkinWeightAuthoringSnapshot snapshot, SkinWeightAuthoringPreview? correction,
        AutomaticSkinBindingResult binding, RigHandSide side, string component, Vector3D minimum, Vector3D maximum, int selectedPoints)
    { Snapshot = snapshot; Correction = correction; Binding = binding; Side = side; ComponentId = component; Minimum = minimum; Maximum = maximum; SelectedPointCount = selectedPoints; }
    internal SkinWeightAuthoringSnapshot Snapshot { get; }
    internal SkinWeightAuthoringPreview? Correction { get; }
    public SkinWeightAuthoringPreview? WeightPreview => Correction;
    public AutomaticSkinBindingResult Binding { get; }
    public RigHandSide Side { get; }
    public string ComponentId { get; }
    public Vector3D Minimum { get; }
    public Vector3D Maximum { get; }
    public int SelectedPointCount { get; }
    public int ChangedPointCount => Correction?.Correction.Changes.Length ?? 0;
    public bool CanApply => Binding.AllPointsAssigned && Correction is not null;
}

/// <summary>Generated finger/wrist candidates within an explicit hand region; outside source points are never edited.</summary>
public static class FbxRegionalHandBinding
{
    public const string ReviewDiagnosticCode = "regional_hand_binding_review";
    public static RegionalHandBindingPreview Preview(FbxModelAuthoringImportResult model, RigHandSide side, string componentId,
        Vector3D minimum, Vector3D maximum, SourceVolumeGridOptions gridOptions, AutomaticSkinBindingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(gridOptions);
        if (!Enum.IsDefined(side) || !minimum.IsFinite || !maximum.IsFinite || maximum.X <= minimum.X || maximum.Y <= minimum.Y || maximum.Z <= minimum.Z)
            throw new ArgumentException("Regional hand binding needs a hand side and a finite positive sampling box.");
        var document = model.Package.Document;
        if (!GeneratedBodyRig.IsGenerated(document)) throw new InvalidOperationException("Regional hand binding requires an owned generated rig.");
        string sideName = side.ToString().ToLowerInvariant();
        var segments = GeneratedBodyRig.GetSegments(document);
        var handSegments = segments.Where(s => s.RoleId == "hand." + sideName || s.RoleId.StartsWith("finger." + sideName + ".", StringComparison.Ordinal)).ToArray();
        if (handSegments.Length < 2) throw new InvalidOperationException("Append reviewed finger bones before binding this hand.");
        if (!ReferenceEquals(GeneratedHandRig.Append(document, side), document))
            throw new InvalidOperationException("The hand's reviewed setup does not match its materialized finger rig.");
        var snapshot = FbxSkinWeightAuthoring.Inspect(model, cancellationToken);
        if (model.Surfaces.Any(s => s.SourceGeometry?.Id == componentId && !s.IsSkinned))
            throw new InvalidOperationException("Create and review the initial body binding before regional hand weight refinement.");
        var component = FbxLocalHandAuthoring.BuildComponent(model, componentId, cancellationToken);
        var selected = snapshot.Points.Select((p, i) => (Point: p, Index: i)).Where(p => p.Point.ComponentId == componentId &&
            Inside(component.Geometry.ControlPoints[p.Point.ControlPointIndex], minimum, maximum)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("The selected hand region contains no source control points.");
        if (selected.Any(p => p.Point.Weights.IsEmpty || Math.Abs(p.Point.Weights.Sum(static w => w.Weight) - 1) > 1e-12))
            throw new InvalidOperationException("Selected hand points require a valid normalized source binding before regional refinement.");
        var selectedIds = selected.Select(p => p.Point.ControlPointIndex).ToHashSet();
        var globals = new TransformMatrix[document.Bones.Length];
        foreach (var bone in document.Bones) globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        // Redistributing an expert nonidentity rest transform can alter neutral
        // positions or morph normals. It needs a reviewed rest transaction first.
        foreach (var surface in model.Surfaces.Where(s => s.SourceGeometry?.Id == componentId && s.IsSkinned))
        {
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!selectedIds.Contains(surface.SourceCorners[i].ControlPointIndex)) continue;
                var vertex = surface.Vertices[i];
                for (int w = 0; w < vertex.BoneIndices.Length; w++)
                {
                    if (vertex.BoneWeights[w] <= 0) continue;
                    int slot = vertex.BoneIndices[w];
                    if (!(globals[surface.PaletteBoneIndices[slot]] * surface.InverseBindMatrices[slot]).NearlyEquals(TransformMatrix.Identity, 1e-8))
                        throw new InvalidOperationException("The selected hand uses an expert nonidentity bind. Review its rest transform before automatic weight redistribution.");
                }
            }
        }
        var handles = handSegments.Select(s => new SkinBindingHandle(s.EntityId, s.Start, s.End)).ToDictionary(static h => h.Id);
        var eligible = handles.Keys.Order().ToImmutableArray();
        var influences = snapshot.Influences.ToDictionary(static i => i.EntityId);
        var points = ImmutableArray.CreateBuilder<SkinBindingPoint>(selected.Length);
        foreach (var (point, _) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fixedWeights = point.LockedInfluences.Select(id => new FixedSkinInfluence(id, point.Weights.Where(w => w.HandleId == id).Sum(static w => w.Weight))).ToImmutableArray();
            foreach (var locked in fixedWeights)
            {
                if (!handles.ContainsKey(locked.HandleId))
                {
                    var position = globals[influences[locked.HandleId].BoneIndex].Translation;
                    handles.Add(locked.HandleId, new(locked.HandleId, position, position));
                }
            }
            points.Add(new(componentId, point.ControlPointIndex, component.Geometry.ControlPoints[point.ControlPointIndex])
            {
                FixedInfluences = fixedWeights,
                AllowedHandles = eligible.Concat(fixedWeights.Where(static f => f.Weight > 0).Select(static f => f.HandleId)).Distinct().Order().ToImmutableArray(),
            });
        }
        var volume = SourceGeometryVolume.Build(new(document.Source.ContentSha256, [component]), cancellationToken: cancellationToken);
        var grid = SourceVolumeGrid.BuildRegion(volume, minimum, maximum, gridOptions, cancellationToken: cancellationToken);
        options ??= new();
        if (options.MaximumInfluences > 4 || !options.CellRegions.IsEmpty) throw new ArgumentException("This adapter requires at most four influences and owns its regional sampling grid.", nameof(options));
        var binding = AutomaticSkinBinder.Bind(grid, handles.Values.OrderBy(static h => h.Id).ToArray(), points.ToImmutable(), options, cancellationToken: cancellationToken);
        if (!binding.AllPointsAssigned) return new(snapshot, null, binding, side, componentId, minimum, maximum, selected.Length);
        var selectedById = selected.ToDictionary(p => p.Point.ControlPointIndex);
        var changes = ImmutableArray.CreateBuilder<SkinWeightCorrectionChange>();
        if (binding.Points.Length != selected.Length) throw new InvalidDataException("Regional binding returned a different source point inventory.");
        foreach (var row in binding.Points)
        {
            if (row.ComponentId != componentId || !selectedById.Remove(row.ControlPointIndex, out var source))
                throw new InvalidDataException("Regional binding returned an unselected or repeated source point.");
            foreach (Guid id in source.Point.LockedInfluences)
                if (Math.Abs(source.Point.Weights.Where(w => w.HandleId == id).Sum(static w => w.Weight) - row.Influences.Where(w => w.HandleId == id).Sum(static w => w.Weight)) > 1e-12)
                    throw new InvalidDataException("Regional binding changed a locked influence fraction.");
            var after = row.Influences.OrderBy(static w => w.HandleId).ToImmutableArray();
            if (!source.Point.Weights.OrderBy(static w => w.HandleId).SequenceEqual(after))
                changes.Add(new(source.Index, source.Point.Weights, after, row.RemovedWeightBeforeRenormalization));
        }
        var correction = new SkinWeightAuthoringPreview(snapshot, new(changes.ToImmutable(), binding.Diagnostics));
        return new(snapshot, correction, binding, side, componentId, minimum, maximum, selected.Length);
    }

    public static bool TryApply(FbxModelAuthoringImportResult current, RegionalHandBindingPreview preview,
        out FbxModelAuthoringImportResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview); result = current;
        if (!ReferenceEquals(current, preview.Snapshot.Model) || !current.Package.Document.RiggingSession!.Matches(preview.Snapshot.Session.CreateJobToken())) return false;
        if (!preview.CanApply) throw new InvalidOperationException("Resolve unassigned regional hand weights before applying.");
        if (!FbxSkinWeightAuthoring.TryApply(current, preview.Correction!, out result, cancellationToken)) return false;
        if (!ReferenceEquals(result, current))
        {
            var document = result.Package.Document;
            document = document with { Diagnostics = document.Diagnostics.Where(d => d.Code != ReviewDiagnosticCode).Append(new CustomModelImportDiagnostic
            {
                Code = ReviewDiagnosticCode, Severity = CustomModelImportSeverity.Warning,
                Message = "Regional hand weights are an automatic candidate. Review finger curl, wrist motion and deformation before native acceptance.",
            }).ToImmutableArray() };
            result = result with { Package = result.Package with { Document = document } };
        }
        return true;
    }

    public static RegionalHandBindingPreview? RefreshMetadata(RegionalHandBindingPreview preview, FbxModelAuthoringImportResult current)
    {
        if (!FbxSkinWeightAuthoring.TryRefreshMetadata(preview.Snapshot, preview.Correction, current, out var snapshot, out var correction)) return null;
        return new(snapshot, correction, preview.Binding, preview.Side, preview.ComponentId, preview.Minimum, preview.Maximum, preview.SelectedPointCount);
    }
    private static bool Inside(Vector3D p, Vector3D min, Vector3D max) => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
}
