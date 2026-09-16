using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public enum RigRestSurfaceMode { PreserveSurface, BakePose }
public sealed record RestPoseEditReport(int ChangedNodes, int ChangedComponents, int UpdatedInverseBinds,
    int EvaluatedVertices, double MaximumVisibleDisplacement, RigRestSurfaceMode SurfaceMode);

/// <summary>Immutable, source-bound rest edit. Only this adapter constructs an applicable preview.</summary>
public sealed class FbxRestPosePreview
{
    internal FbxRestPosePreview(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult candidate,
        RiggingJobToken token, Guid entityId, TransformMatrix beforeFrame, TransformMatrix afterFrame, RestPoseEditReport report)
    { SourceModel = source; PreviewModel = candidate; Token = token; EntityId = entityId; BeforeFrame = beforeFrame; AfterFrame = afterFrame; Report = report; }
    internal FbxModelAuthoringImportResult SourceModel { get; }
    internal RiggingJobToken Token { get; }
    public FbxModelAuthoringImportResult PreviewModel { get; }
    public Guid EntityId { get; }
    public TransformMatrix BeforeFrame { get; }
    public TransformMatrix AfterFrame { get; }
    public RestPoseEditReport Report { get; }
    public bool HasChanges => !ReferenceEquals(SourceModel, PreviewModel);
}

/// <summary>Joint-frame editing with explicit inverse-bind compensation or coherent base/morph pose baking.</summary>
public static class FbxRestPoseAuthoring
{
    public const string ReviewDiagnosticCode = "rest_pose_motion_review";
    public static FbxRestPosePreview Preview(FbxModelAuthoringImportResult model, Guid entityId,
        TransformMatrix desiredGlobal, RigRestDescendantMode descendants, RigRestSurfaceMode surfaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(surfaces)) throw new ArgumentOutOfRangeException(nameof(surfaces));
        var session = model.Package.Document.RiggingSession ?? throw new InvalidOperationException("Start a studio session before editing joint rest frames.");
        var token = session.CreateJobToken();
        var observed = RiggingSessions.ObserveSourceHierarchy(model.Package.Document);
        int selectedIndex = Enumerable.Range(0, observed.Length).Single(i => observed[i].EntityId == entityId);
        var edit = RigRestPoseAuthoring.Apply(model.Package.Document, token, entityId, desiredGlobal, descendants);
        if (ReferenceEquals(edit.Document, model.Package.Document))
            return new(model, model, token, entityId, edit.BeforeGlobals[selectedIndex], edit.AfterGlobals[selectedIndex], new(0,0,0,0,0,surfaces));
        // Validate correspondence and influence values before evaluating or
        // persisting any surface. No new weights are solved by a rest edit.
        _ = FbxSkinWeightAuthoring.Inspect(model, cancellationToken);
        var changed = edit.ChangedBoneIndices.Where(i => !edit.BeforeGlobals[i].NearlyEquals(edit.AfterGlobals[i], 1e-12)).ToHashSet();
        var affectedComponents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in model.Surfaces.Where(s => s.IsSkinned))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (surface.Vertices.Any(v => v.BoneIndices.Select((slot, i) => (slot, weight:v.BoneWeights[i]))
                    .Any(w => w.weight > 0 && changed.Contains(surface.PaletteBoneIndices[w.slot]))))
                affectedComponents.Add(surface.SourceGeometry!.Id);
        }
        var updated = ImmutableArray.CreateBuilder<FbxModelSurface>(model.Surfaces.Length);
        int inverseCount = 0, vertexCount = 0; double maximum = 0;
        foreach (var surface in model.Surfaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!surface.IsSkinned) { updated.Add(surface); continue; }
            if (surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length)
                throw new InvalidDataException("The source draw has an incomplete inverse-bind palette.");
            bool bake = surfaces == RigRestSurfaceMode.BakePose && affectedComponents.Contains(surface.SourceGeometry!.Id);
            var inverses = surface.InverseBindMatrices.ToBuilder();
            var transforms = ImmutableArray.CreateBuilder<TransformMatrix>(surface.PaletteBoneIndices.Length);
            bool changedInverse = false;
            for (int slot = 0; slot < surface.PaletteBoneIndices.Length; slot++)
            {
                int bone = surface.PaletteBoneIndices[slot];
                var before = edit.BeforeGlobals[bone]; var after = edit.AfterGlobals[bone]; var inverse = surface.InverseBindMatrices[slot];
                transforms.Add(after * inverse);
                if (bake || !before.NearlyEquals(after, 1e-12))
                {
                    // Retain expert offsets when the surface must remain fixed.
                    inverses[slot] = bake ? after.InvertedAffine() : after.InvertedAffine() * before * inverse;
                    changedInverse |= inverses[slot] != inverse; inverseCount++;
                }
            }
            var candidate = bake ? FbxSurfacePoseBaker.Bake(surface, transforms.ToImmutable(), cancellationToken) : surface;
            if (bake)
            {
                for (int i = 0; i < surface.Vertices.Length; i++)
                {
                    if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var vertex = surface.Vertices[i]; var oldVisible = Vector3D.Zero; double sum = vertex.BoneWeights.Sum();
                    for (int j = 0; j < vertex.BoneIndices.Length; j++)
                    {
                        int slot = vertex.BoneIndices[j]; int bone = surface.PaletteBoneIndices[slot];
                        oldVisible += (edit.BeforeGlobals[bone] * surface.InverseBindMatrices[slot]).TransformPoint(vertex.Position) * (vertex.BoneWeights[j] / sum);
                    }
                    maximum = Math.Max(maximum, Vector3D.Distance(oldVisible, candidate.Vertices[i].Position)); vertexCount++;
                }
            }
            if (changedInverse) candidate = candidate with { InverseBindMatrices = inverses.ToImmutable() };
            updated.Add(candidate);
        }
        var document = edit.Document with { Diagnostics = edit.Document.Diagnostics.Where(d => d.Code != ReviewDiagnosticCode).Append(new CustomModelImportDiagnostic
        { Code = ReviewDiagnosticCode, Severity = CustomModelImportSeverity.Warning,
            Message = "Joint rest frames changed through a reviewed authoring transaction. Original animation data was retained; review clip motion, helper behavior and native binding before acceptance." }).ToImmutableArray() };
        var candidateModel = model with { Package = model.Package with { Document = document }, Rig = document.CreateRigDefinition(), Surfaces = updated.MoveToImmutable() };
        candidateModel = FbxAuthoredModelLayer.Capture(candidateModel, cancellationToken);
        return new(model, candidateModel, token, entityId, edit.BeforeGlobals[selectedIndex], edit.AfterGlobals[selectedIndex],
            new(edit.ChangedBoneIndices.Length, affectedComponents.Count, inverseCount, vertexCount, maximum, surfaces));
    }

    public static bool TryApply(FbxModelAuthoringImportResult current, FbxRestPosePreview preview, out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(preview); result = current;
        if (!ReferenceEquals(current, preview.SourceModel) || current.Package.Document.RiggingSession?.Matches(preview.Token) != true) return false;
        preview.PreviewModel.Package.Document.Validate(); result = preview.PreviewModel; return true;
    }

    public static FbxRestPosePreview? RefreshMetadata(FbxRestPosePreview preview, FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(current);
        var before = preview.SourceModel; var a = before.Package.Document; var b = current.Package.Document;
        if (a.RiggingSession is not { } oldSession || b.RiggingSession is not { } newSession || !newSession.Matches(preview.Token) ||
            oldSession with { Stage = newSession.Stage } != newSession || a with { RiggingSession = newSession } != b ||
            before.Surfaces != current.Surfaces || !ReferenceEquals(before.Rig,current.Rig) || before.Package.SourceFbx != current.Package.SourceFbx ||
            before.Package.AuthoredLayerPayload != current.Package.AuthoredLayerPayload) return null;
        var candidate = preview.HasChanges ? preview.PreviewModel with { Package = preview.PreviewModel.Package with { Document = preview.PreviewModel.Package.Document with
            { RiggingSession = RiggingSessions.Navigate(preview.PreviewModel.Package.Document.RiggingSession!,newSession.Stage) } } } : current;
        return new(current,candidate,preview.Token,preview.EntityId,preview.BeforeFrame,preview.AfterFrame,preview.Report);
    }
}
