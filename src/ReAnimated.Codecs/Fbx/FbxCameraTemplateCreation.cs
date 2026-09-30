using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public sealed record CameraTemplateObservation(int SourceIndex, string Name, string SourceName,
    string ParentName, TransformMatrix LocalFrame, string TemplateId, string ResourceName, string ResourceSha256);

public static partial class FbxCameraHelperAuthoring
{
    public static ImmutableArray<CameraTemplateObservation> ObserveTemplate(Dl1RigTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.SourceFingerprint.Length != 64 || template.SourceFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Camera creation requires the resolved reference resource's SHA-256.");
        return template.Entities.Where(e => e.Kind == BoneKind.Camera && !e.IsDeform && e.ParentIndex >= 0 &&
                (e.Name.Equals("EyeCamera", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("RefCamera", StringComparison.OrdinalIgnoreCase)))
            .Select(e => new CameraTemplateObservation(e.Index,
                e.Name.Equals("EyeCamera", StringComparison.OrdinalIgnoreCase) ? "EyeCamera" : "RefCamera",
                e.Name, template.Entities[e.ParentIndex].Name, e.LocalRestMatrix,
                template.TemplateId, template.SourceResourceName, template.SourceFingerprint)).ToImmutableArray();
    }

    public static FbxCameraCalibrationPreview PreviewCreation(FbxModelAuthoringImportResult model, Dl1RigTemplate template,
        string cameraName, Guid targetParentEntityId, TransformTRS localOffset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!localOffset.IsFinite || localOffset.Scale != Vector3D.One)
            throw new ArgumentException("Camera creation offsets must be finite rigid transforms.", nameof(localOffset));
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraName);
        CameraTemplateObservation observation = ObserveTemplate(template).SingleOrDefault(o => o.Name.Equals(cameraName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Select a supported camera helper from the resolved reference.");
        var document = model.Package.Document;
        var session = document.RiggingSession ?? throw new InvalidOperationException("Start a studio session before adding a camera helper.");
        var updated = RigCameraHelperAuthoring.Create(document, session.CreateJobToken(), observation.Name,
            targetParentEntityId, observation.LocalFrame * localOffset.ToMatrix());
        Guid createdId = updated.AuthoredHelpers.Single(h => h.Name == observation.Name).Id;
        var createdSession = updated.RiggingSession!;
        var origin = new RigHelperTemplateOrigin
        {
            TemplateId = template.TemplateId, ProfileName = template.ProfileName,
            ResourceName = template.SourceResourceName, ResourceSha256 = template.SourceFingerprint,
            SourceNodeName = observation.SourceName, SourceParentName = observation.ParentName,
            SourceLocalFrame = observation.LocalFrame, LocalOffsetAtCreation = localOffset.ToMatrix(),
        };
        var evidence = new RigEvidenceReference
        {
            Id = "camera-template-origin:" + createdId.ToString("N"), Kind = RigEvidenceKind.ImportedSource,
            ArtifactSha256 = template.SourceFingerprint,
            Description = $"Camera frame observed in reference {template.ProfileName}/{template.SourceResourceName}, parent {observation.ParentName}. " +
                "Target parent and offset were explicitly selected. Native requirement, channel ownership and LOD rules remain unverified.",
        };
        var helpers = createdSession.Recipe.Helpers.Select(h => h.EntityId == createdId
            ? h with { TemplateOrigin = origin, Evidence = h.Evidence.Add(evidence) } : h).ToImmutableArray();
        updated = updated with { RiggingSession = RiggingSessions.Change(createdSession,
            createdSession with { Recipe = createdSession.Recipe with { Helpers = helpers } }, RiggingEditKind.Helpers) };
        updated.Validate();
        var candidate = model with { Package = model.Package with { Document = updated }, Rig = updated.CreateRigDefinition() };
        FbxCameraNode node = Inspect(candidate).Single(n => n.EntityId == createdId);
        var prepared = Dl1CustomModelRigPreparer.Prepare(candidate, cancellationToken);
        return new(model, candidate, node, prepared.Contract.Nodes.Single(n => n.SourceBoneIndex == node.SourceIndex).GlobalBindMatrix)
        { CreationTemplate = template };
    }
}
