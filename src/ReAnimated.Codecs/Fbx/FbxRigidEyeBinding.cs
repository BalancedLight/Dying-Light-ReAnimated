using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

public sealed class RigidEyeBindingPreview
{
    internal RigidEyeBindingPreview(SkinWeightAuthoringSnapshot snapshot, SkinWeightAuthoringPreview correction,
        RigEyeSide side, Guid eyeEntityId, string componentId, int islandIndex, int selectedPoints)
    { Snapshot = snapshot; WeightPreview = correction; Side = side; EyeEntityId = eyeEntityId; ComponentId = componentId; IslandIndex = islandIndex; SelectedPointCount = selectedPoints; }
    internal SkinWeightAuthoringSnapshot Snapshot { get; }
    public SkinWeightAuthoringPreview WeightPreview { get; }
    public RigEyeSide Side { get; }
    public Guid EyeEntityId { get; }
    public string ComponentId { get; }
    public int IslandIndex { get; }
    public int SelectedPointCount { get; }
    public int ChangedPointCount => WeightPreview.Correction.Changes.Length;
}

/// <summary>Explicit whole-island rigid eye binding, with no implicit mirroring or changes outside the selected eye.</summary>
public static class FbxRigidEyeBinding
{
    public const string ReviewDiagnosticCode = "rigid_eye_binding_review";
    public static RigidEyeBindingPreview Preview(FbxModelAuthoringImportResult model, RigEyeSide side, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); cancellationToken.ThrowIfCancellationRequested();
        var document = model.Package.Document;
        var session = document.RiggingSession ?? throw new InvalidOperationException("Eye binding requires a current studio session.");
        var eye = session.Eyes.SingleOrDefault(e => e.Side == side && e.Mode == RigEyeSetupMode.GeometryPivot) ??
            throw new InvalidOperationException("Save the selected eye geometry and pivot first.");
        if (!eye.UserApproved || eye.DeformEntityId is not { } eyeId || eye.ComponentId is not { } componentId || eye.IslandIndex is not { } islandIndex)
            throw new InvalidOperationException("Review the eye setup and create its deform bone before binding.");
        int eyeIndex = GeneratedEyeRig.GetBoneIndex(document, side);
        var component = FbxLocalHandAuthoring.BuildComponent(model, componentId, cancellationToken);
        if ((uint)islandIndex >= (uint)component.Topology.Islands.Length)
            throw new InvalidDataException("The saved eye island no longer exists.");
        var selectedIds = component.Topology.Islands[islandIndex].SourceControlPointIds.ToHashSet();
        if (selectedIds.Count == 0 || selectedIds.Count > 250_000 || !selectedIds.SetEquals(eye.SourceControlPointIds))
            throw new InvalidDataException("Rigid eye binding requires reviewed support covering exactly one current source island.");
        var snapshot = FbxSkinWeightAuthoring.Inspect(model, cancellationToken);
        var selected = snapshot.Points.Select((point, index) => (Point: point, Index: index))
            .Where(p => p.Point.ComponentId == componentId && selectedIds.Contains(p.Point.ControlPointIndex)).ToArray();
        if (selected.Length != selectedIds.Count) throw new InvalidDataException("Some selected eye points have no current draw correspondence.");
        var changes = ImmutableArray.CreateBuilder<SkinWeightCorrectionChange>();
        var changedIds = new HashSet<int>();
        foreach (var (point, index) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Guid locked in point.LockedInfluences)
            {
                double current = point.Weights.Where(w => w.HandleId == locked).Sum(w => w.Weight);
                if (Math.Abs(current - (locked == eyeId ? 1 : 0)) > 1e-12)
                    throw new InvalidOperationException("Rigid eye binding conflicts with a saved influence lock. Unlock that selected influence before replacing its weight.");
            }
            ImmutableArray<GeneratedSkinInfluence> after = [new(eyeId, 1)];
            if (!point.Weights.SequenceEqual(after))
            { changes.Add(new(index, point.Weights, after, 0)); changedIds.Add(point.ControlPointIndex); }
        }
        var globals = new TransformMatrix[document.Bones.Length];
        foreach (var bone in document.Bones) globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        // A new influence uses the current exact bind inverse. Reject changes
        // that would replace a nonidentity expert rest transform without rebasing.
        foreach (var surface in model.Surfaces.Where(s => s.SourceGeometry?.Id == componentId))
        {
            for (int i = 0; i < surface.Vertices.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!changedIds.Contains(surface.SourceCorners[i].ControlPointIndex)) continue;
                var vertex = surface.Vertices[i];
                for (int j = 0; j < vertex.BoneIndices.Length; j++)
                {
                    if (vertex.BoneWeights[j] <= 0) continue;
                    int slot = vertex.BoneIndices[j];
                    if (!(globals[surface.PaletteBoneIndices[slot]] * surface.InverseBindMatrices[slot]).NearlyEquals(TransformMatrix.Identity, 1e-8))
                        throw new InvalidOperationException("The selected eye uses an expert nonidentity rest bind. Review its rest transform before rigid reassignment.");
                }
            }
            int eyeSlot = surface.PaletteBoneIndices.IndexOf(eyeIndex);
            if (changedIds.Count > 0 && eyeSlot >= 0 && !(globals[eyeIndex] * surface.InverseBindMatrices[eyeSlot]).NearlyEquals(TransformMatrix.Identity, 1e-8))
                throw new InvalidOperationException("The target eye influence has an expert nonidentity inverse bind; review it before rigid reassignment.");
        }
        var correction = new SkinWeightAuthoringPreview(snapshot, new(changes.ToImmutable(), []));
        return new(snapshot, correction, side, eyeId, componentId, islandIndex, selected.Length);
    }

    public static bool TryApply(FbxModelAuthoringImportResult current, RigidEyeBindingPreview preview,
        out FbxModelAuthoringImportResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(preview); result = current;
        if (!ReferenceEquals(current, preview.Snapshot.Model) || current.Package.Document.RiggingSession?.Matches(preview.Snapshot.Session.CreateJobToken()) != true) return false;
        if (!FbxSkinWeightAuthoring.TryApply(current, preview.WeightPreview, out result, cancellationToken)) return false;
        if (!ReferenceEquals(result, current))
        {
            var document = result.Package.Document;
            if (result.Surfaces.All(s => s.IsSkinned && s.Vertices.All(v => v.BoneWeights.Length > 0 && Math.Abs(v.BoneWeights.Sum() - 1) <= 1e-10)))
                document = document with { Diagnostics = document.Diagnostics.Where(d => d.Code != FbxGeneratedBodyBinding.UnboundDiagnostic).ToImmutableArray() };
            document = document with { Diagnostics = document.Diagnostics.Where(d => d.Code != ReviewDiagnosticCode).Append(new CustomModelImportDiagnostic
            { Code = ReviewDiagnosticCode, Severity = CustomModelImportSeverity.Warning,
                Message = "Selected eye geometry was rigidly bound to its reviewed eye bone. Review gaze, eyelid contact and morph blending before native acceptance." }).ToImmutableArray() };
            result = result with { Package = result.Package with { Document = document } };
        }
        return true;
    }
    public static RigidEyeBindingPreview? RefreshMetadata(RigidEyeBindingPreview preview, FbxModelAuthoringImportResult current)
    {
        if (!FbxSkinWeightAuthoring.TryRefreshMetadata(preview.Snapshot, preview.WeightPreview, current, out var snapshot, out var correction)) return null;
        return new(snapshot, correction!, preview.Side, preview.EyeEntityId, preview.ComponentId, preview.IslandIndex, preview.SelectedPointCount);
    }
}
