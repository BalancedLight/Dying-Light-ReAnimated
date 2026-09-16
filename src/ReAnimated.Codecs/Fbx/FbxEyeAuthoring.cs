using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxEyeSourceNodeObservation(
    Guid EntityId,
    string SourceEntityId,
    string Name,
    RigNativeEntityKind Kind,
    BoneKind EffectiveBoneKind,
    bool Imported,
    int ParentIndex,
    TransformMatrix LocalFrame,
    TransformMatrix GlobalFrame);

/// <summary>Immutable eye geometry evidence and pivot proposal from one source snapshot.</summary>
public sealed class FbxEyeGeometryWork
{
    internal FbxEyeGeometryWork(FbxModelAuthoringImportResult model, RiggingSession session,
        RiggingJobToken token, EyePivotDetectionResult detection)
    { Model = model; Session = session; Token = token; Detection = detection; }

    internal FbxModelAuthoringImportResult Model { get; }
    public RiggingSession Session { get; }
    public RiggingJobToken Token { get; }
    public EyePivotDetectionResult Detection { get; }
}
/// <summary>Read-only FBX eye inspection and source-linked setup persistence.</summary>
public static class FbxEyeAuthoring
{
    public static FbxEyeGeometryWork InspectGeometry(FbxModelAuthoringImportResult model,
        string componentId, int islandIndex, EyePivotDetectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        (FbxModelAuthoringImportResult current, RiggingSession session) = CurrentModel(model);
        SourceGeometryComponentAnalysis component = FbxLocalHandAuthoring.BuildComponent(current, componentId, cancellationToken);
        SourceGeometryAnalysis analysis = new(current.Package.Document.Source.ContentSha256, [component]);
        EyePivotDetectionResult detection = EyePivotDetector.Detect(analysis, componentId, islandIndex, options, cancellationToken);
        return new(current, session, session.CreateJobToken(), detection);
    }

    /// <summary>Reads owned source nodes using stable entity IDs and exact affine bind globals.</summary>
    public static ImmutableArray<FbxEyeSourceNodeObservation> ObserveSourceNodes(
        FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        (FbxModelAuthoringImportResult current, RiggingSession session) = CurrentModel(model);
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(current.Package.Document);
        ImmutableArray<CustomModelBone> bones = current.Package.Document.CreateEffectiveBones();
        if (observed.Length != bones.Length)
            throw new InvalidDataException("The owned source hierarchy does not cover the effective node inventory.");
        var entities = session.Recipe.Entities.ToDictionary(static entity => entity.EntityId);
        var globals = new TransformMatrix[bones.Length];
        var result = ImmutableArray.CreateBuilder<FbxEyeSourceNodeObservation>(bones.Length);
        foreach (CustomModelBone bone in bones)
        {
            globals[bone.Index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
            Guid id = observed[bone.Index].EntityId;
            if (!entities.TryGetValue(id, out RigEntityBinding? entity))
                throw new InvalidDataException("An owned source node has no stable entity binding.");
            string sourceId = entity.SourceEntityId ?? "authored:" + id.ToString("N");
            result.Add(new(id, sourceId, entity.NativeName, entity.Kind, bone.Kind, entity.Imported, bone.ParentIndex,
                bone.ExactLocalBindMatrix, globals[bone.Index]));
        }
        return result.MoveToImmutable();
    }

    public static FbxEyeGeometryWork? RefreshMetadata(FbxEyeGeometryWork work,
        FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(current);
        FbxModelAuthoringImportResult previous = work.Model;
        CustomModelDocument before = previous.Package.Document;
        CustomModelDocument after = current.Package.Document;
        if (previous.Surfaces != current.Surfaces || !ReferenceEquals(previous.Rig, current.Rig) ||
            before.ModelId != after.ModelId || before.Source.ContentSha256 != after.Source.ContentSha256 ||
            previous.Package.SourceFbx != current.Package.SourceFbx || before.RigSignature != after.RigSignature ||
            !before.Bones.SequenceEqual(after.Bones) || !before.AuthoredHelpers.SequenceEqual(after.AuthoredHelpers) ||
            after.RiggingSession?.Matches(work.Token) != true)
            return null;
        return new(current, after.RiggingSession!, work.Token, work.Detection);
    }

    /// <summary>
    /// Saves one validated source-linked eye decision by replacing only the
    /// matching side/mode row. Source nodes and render surfaces remain untouched.
    /// </summary>
    public static FbxModelAuthoringImportResult SaveSetup(FbxModelAuthoringImportResult model,
        RiggingJobToken token, RigEyeSetup setup)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(setup);
        CustomModelDocument document = model.Package.Document;
        document.Validate();
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("Eye setup requires a current rigging session.");
        if (!session.Matches(token) || !session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidOperationException("Eye setup inputs changed; inspect the current source again.");
        var previous = session.Eyes.FirstOrDefault(e => e.Side == setup.Side && e.Mode == setup.Mode);
        if (previous?.DeformEntityId != setup.DeformEntityId)
            throw new InvalidOperationException("Generated eye identities must be created through the eye rig transaction and cannot be reassigned or removed through setup metadata.");
        if (setup.DeformEntityId is not null && previous is not null &&
            (!previous.GlobalFrame.NearlyEquals(setup.GlobalFrame, 1e-10) || previous.ParentEntityId != setup.ParentEntityId))
            throw new InvalidOperationException("Changing a generated eye frame or parent requires a reviewed rest transaction.");
        ImmutableArray<FbxEyeSourceNodeObservation> nodes = ObserveSourceNodes(model);
        var owned = nodes.Select(static node => node.EntityId).ToHashSet();
        var componentIds = session.Components.Select(static component => component.Id).ToHashSet(StringComparer.Ordinal);
        setup.Validate();
        foreach (Guid? reference in new[] { setup.SourceEntityId, setup.ParentEntityId, setup.HelperEntityId, setup.DeformEntityId })
            if (reference is { } id && !owned.Contains(id))
                throw new ArgumentException("The eye setup references an entity that is not owned by the current source.", nameof(setup));
        if (setup.ComponentId is { } componentReference && !componentIds.Contains(componentReference))
            throw new ArgumentException("The eye setup references a component that is not owned by the current source.", nameof(setup));
        var existingMorphs = document.MorphChannels.Select(m => m.DescriptorHash).ToHashSet();
        if (setup.MorphDescriptors.Any(id => !existingMorphs.Contains(id)))
            throw new InvalidDataException("Eye setup references a facial descriptor absent from this model.");
        ValidateGeometryReferences(model, setup);
        if (setup.Mode == RigEyeSetupMode.SourceEye)
        {
            FbxEyeSourceNodeObservation source = nodes.Single(node => node.EntityId == setup.SourceEntityId);
            if (source.EffectiveBoneKind == BoneKind.Camera || source.Name.Equals("EyeCamera", StringComparison.OrdinalIgnoreCase) || source.Name.Equals("RefCamera", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A source-eye setup cannot select a camera node or reserved camera name.");
            if (source.GlobalFrame != setup.GlobalFrame)
                throw new InvalidDataException("A source-eye setup must preserve the exact observed affine source frame.");
        }
        ImmutableArray<RigEyeSetup> eyes = session.Eyes
            .Where(eye => eye.Side != setup.Side || eye.Mode != setup.Mode)
            .Append(setup)
            .OrderBy(static eye => eye.Side)
            .ThenBy(static eye => eye.Mode)
            .ToImmutableArray();
        RiggingSession changed = RiggingSessions.Change(session, session with { Eyes = eyes }, RiggingEditKind.Anatomy);
        CustomModelDocument updated = document with { RiggingSession = changed, LastBuildReceipt = null };
        updated.Validate();
        return model with { Package = model.Package with { Document = updated } };
    }

    private static (FbxModelAuthoringImportResult Model, RiggingSession Session) CurrentModel(
        FbxModelAuthoringImportResult model)
    {
        CustomModelDocument document = model.Package.Document;
        document.Validate();
        RigStudioEntryPath expected = document.Bones.IsEmpty
            ? RigStudioEntryPath.AutoRigBiped
            : RigStudioEntryPath.AdaptExistingRig;
        RiggingSession session = document.RiggingSession ?? RiggingSessions.Create(document, expected);
        bool compatible = document.Bones.IsEmpty
            ? session.EntryPath == RigStudioEntryPath.AutoRigBiped
            : session.EntryPath is RigStudioEntryPath.AdaptExistingRig or RigStudioEntryPath.RepairExistingRig ||
              GeneratedBodyRig.IsGenerated(document with { RiggingSession = session });
        if (!compatible || !session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidOperationException("Eye authoring requires a current AutoRigBiped, AdaptExistingRig, RepairExistingRig, or owned generated-body source session.");
        if (ReferenceEquals(document.RiggingSession, session)) return (model, session);
        return (model with { Package = model.Package with { Document = document with { RiggingSession = session } } }, session);
    }

    private static void ValidateGeometryReferences(FbxModelAuthoringImportResult model, RigEyeSetup setup)
    {
        if (setup.ComponentId is not { } componentId) return;
        SourceGeometryComponentAnalysis component = FbxLocalHandAuthoring.BuildComponent(model, componentId, CancellationToken.None);
        if (setup.IslandIndex is { } islandIndex)
        {
            if ((uint)islandIndex >= (uint)component.Topology.Islands.Length)
                throw new ArgumentOutOfRangeException(nameof(setup));
            var islandPoints = component.Topology.Islands[islandIndex].SourceControlPointIds.ToHashSet();
            if (setup.SourceControlPointIds.Any(id => !islandPoints.Contains(id)))
                throw new InvalidDataException("Eye setup support points must belong to the selected source island.");
        }
        else if (setup.SourceControlPointIds.Any(id => (uint)id >= (uint)component.Geometry.ControlPoints.Length))
            throw new InvalidDataException("Eye setup support points must identify source control points.");
        var morphs = model.Surfaces.Where(surface => surface.SourceGeometry?.Id == componentId)
            .SelectMany(static surface => surface.MorphTargets).Select(static morph => morph.DescriptorHash).ToHashSet();
        if (setup.MorphDescriptors.Any(descriptor => !morphs.Contains(descriptor)))
            throw new InvalidDataException("Eye setup references a morph descriptor absent from the selected source component.");
    }
}
