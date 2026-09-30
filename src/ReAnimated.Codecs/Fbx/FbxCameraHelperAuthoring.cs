using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxCameraNode(Guid EntityId, int SourceIndex, string Name, string ParentName,
    TransformMatrix LocalFrame, string ChannelSummary);

public sealed class FbxCameraCalibrationPreview
{
    internal FbxCameraCalibrationPreview(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult candidate,
        FbxCameraNode node, TransformMatrix worldFrame)
    {
        Source = source; Candidate = candidate; Node = node; WorldFrame = worldFrame;
        CameraReviewFrame view = CameraReviewGeometry.Create(worldFrame, CameraLens.Default);
        Forward = view.Forward;
        Up = view.Up;
        RollDegrees = view.RollDegrees;
    }
    internal FbxModelAuthoringImportResult Source { get; }
    internal Dl1RigTemplate? CreationTemplate { get; init; }
    public bool IsCreation => CreationTemplate is not null;
    public FbxModelAuthoringImportResult Candidate { get; }
    public FbxCameraNode Node { get; }
    public TransformMatrix WorldFrame { get; }
    public Vector3D Forward { get; }
    public Vector3D Up { get; }
    public double? RollDegrees { get; }
    public bool HasChanges => !ReferenceEquals(Source, Candidate);
}

/// <summary>Reviewable camera calibration through the same prepared rig used by model output.</summary>
public static partial class FbxCameraHelperAuthoring
{
    public static ImmutableArray<FbxCameraNode> Inspect(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var document = model.Package.Document;
        document.Validate();
        if (document.RiggingSession is not { } session) return [];
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        var bones = document.CreateEffectiveBones();
        return bones.Where(RigCameraHelperAuthoring.IsCamera).Select(b =>
        {
            Guid id = observed[b.Index].EntityId;
            var policy = session.Recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == id);
            string summary = policy is null ? "No channel ownership recorded; native behavior is unverified." :
                $"Position: {Owners(policy.Position)}; rotation: {Owners(policy.Rotation)}; scale: {Owners(policy.Scale)}; mask: {policy.EmittedMask}; LOD: {policy.AnimationLod}. Frame calibration preserves these choices.";
            return new FbxCameraNode(id, b.Index, b.Name, b.ParentIndex < 0 ? "World" : bones[b.ParentIndex].Name,
                session.Recipe.Helpers.FirstOrDefault(h => h.EntityId == id)?.LocalFrame ?? b.ExactLocalBindMatrix, summary);
        }).ToImmutableArray();
        static string Owners(RigChannelOwnership channel) => channel.Owners.IsEmpty ? "unresolved" : string.Join(" + ", channel.Owners);
    }

    public static FbxCameraCalibrationPreview Preview(FbxModelAuthoringImportResult model, Guid entityId, TransformTRS localOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!localOffset.IsFinite || localOffset.Scale != Vector3D.One)
            throw new ArgumentException("Camera calibration offsets must be finite rigid transforms; inherited frame scale is retained.", nameof(localOffset));
        FbxCameraNode node = Inspect(model).SingleOrDefault(n => n.EntityId == entityId)
            ?? throw new InvalidOperationException("The camera helper is absent from the current model.");
        var document = model.Package.Document;
        var updated = RigCameraHelperAuthoring.Apply(document, document.RiggingSession!.CreateJobToken(), entityId,
            node.LocalFrame * localOffset.ToMatrix());
        var candidate = ReferenceEquals(document, updated) ? model : model with
        {
            Package = model.Package with { Document = updated }, Rig = updated.CreateRigDefinition(),
        };
        FbxProfileEditGuard.RequireAllowed(model, candidate, cancellationToken);
        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(candidate, cancellationToken);
        var preparedNode = prepared.Contract.Nodes.Single(n => n.SourceBoneIndex == node.SourceIndex);
        return new(model, candidate, node, preparedNode.GlobalBindMatrix);
    }

    public static FbxCameraCalibrationPreview? RefreshMetadata(FbxCameraCalibrationPreview preview, FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(current);
        var before = preview.Source; var a = before.Package.Document; var b = current.Package.Document;
        if (a.RiggingSession is not { } oldSession || b.RiggingSession is not { } next || !next.Matches(oldSession.CreateJobToken()) ||
            oldSession with { Stage = next.Stage } != next || a with { RiggingSession = next } != b ||
            before.Surfaces != current.Surfaces || before.AnimationClips != current.AnimationClips || !ReferenceEquals(before.Rig, current.Rig) ||
            before.Package.SourceFbx != current.Package.SourceFbx || before.Package.AuthoredLayerPayload != current.Package.AuthoredLayerPayload ||
            before.Package.TexturePayloads != current.Package.TexturePayloads) return null;
        var candidate = preview.HasChanges ? preview.Candidate with { Package = preview.Candidate.Package with
        { Document = preview.Candidate.Package.Document with { RiggingSession = RiggingSessions.Navigate(preview.Candidate.Package.Document.RiggingSession!, next.Stage) } } } : current;
        return new(current, candidate, preview.Node, preview.WorldFrame) { CreationTemplate = preview.CreationTemplate };
    }

    public static bool TryApply(FbxModelAuthoringImportResult current, FbxCameraCalibrationPreview preview,
        out FbxModelAuthoringImportResult result) => TryApply(current, preview, null, out result);

    public static bool TryApply(FbxModelAuthoringImportResult current, FbxCameraCalibrationPreview preview,
        Dl1RigTemplate? currentTemplate, out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(preview);
        result = current;
        if (!ReferenceEquals(current, preview.Source) || preview.CreationTemplate is not null && !ReferenceEquals(currentTemplate, preview.CreationTemplate)) return false;
        result = preview.Candidate;
        return true;
    }
}
