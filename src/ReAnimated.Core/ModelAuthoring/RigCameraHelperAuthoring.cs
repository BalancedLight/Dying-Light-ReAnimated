using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Explicit camera-frame edits. Native camera semantics and channel ownership are never inferred.</summary>
public static class RigCameraHelperAuthoring
{
    public static bool IsCamera(CustomModelBone bone) => bone.Kind == BoneKind.Camera ||
        bone.Name.Equals(Dl1PreviewContract.EyeCameraBoneName, StringComparison.Ordinal) ||
        bone.Name.Equals(Dl1PreviewContract.ReferenceCameraBoneName, StringComparison.Ordinal);

    public static CustomModelDocument Create(CustomModelDocument document, RiggingJobToken token,
        string name, Guid parentEntityId, TransformMatrix localFrame)
    {
        RequireSession(document, token);
        if (name is not (Dl1PreviewContract.EyeCameraBoneName or Dl1PreviewContract.ReferenceCameraBoneName))
            throw new ArgumentException("Choose the explicit EyeCamera or RefCamera identity.", nameof(name));
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        int parent = Find(observed, parentEntityId);
        if (parent < 0) throw new InvalidOperationException("The camera parent is no longer present in this model.");
        CustomModelDocument working = CustomModelHelperAuthoring.DuplicateAsHelper(document, parent,
            CustomModelAuthoredHelperKind.Camera, name);
        return Apply(working, working.RiggingSession!.CreateJobToken(), working.AuthoredHelpers[^1].Id, localFrame);
    }

    public static CustomModelDocument Apply(CustomModelDocument document, RiggingJobToken token,
        Guid cameraEntityId, TransformMatrix localFrame)
    {
        RiggingSession session = RequireSession(document, token);
        RigRecipeRules.Affine(localFrame, nameof(localFrame));
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        var bones = document.CreateEffectiveBones();
        int index = Find(observed, cameraEntityId);
        if (index < 0 || !IsCamera(bones[index]))
            throw new InvalidOperationException("Select an observed camera helper from this model.");
        var bone = bones[index];
        if (bone.ParentIndex < 0) throw new InvalidOperationException("A calibrated camera requires an explicit parent frame.");
        for (int candidate = index; candidate < bones.Length; candidate++)
        {
            int cursor = candidate;
            while (cursor >= 0 && cursor != index) cursor = bones[cursor].ParentIndex;
            if (cursor == index && bones[candidate].IsWeighted)
                throw new InvalidOperationException("This camera has weighted geometry in its branch. Use a full rest-pose transaction before changing its frame.");
        }
        Guid parent = observed[bone.ParentIndex].EntityId;
        HelperRecipe? previous = session.Recipe.Helpers.FirstOrDefault(h => h.EntityId == cameraEntityId);
        RigEntityFramePolicy? standalone = session.Recipe.FramePolicies.FirstOrDefault(p => p.EntityId == cameraEntityId);
        if (standalone?.SolvedGlobalFrame is { } solved && !solved.NearlyEquals(SourceGlobal(bones, index), 1e-9))
            throw new InvalidOperationException("This camera has an independent global frame rule. Review that rule before applying a parent-local calibration.");
        TransformMatrix current = previous?.LocalFrame ?? bone.ExactLocalBindMatrix;
        if (previous is not null && previous.ParentEntityId != parent)
            throw new InvalidOperationException("The camera recipe parent differs from the observed parent. Resolve the hierarchy transaction first.");
        if (previous is not null)
        {
            if (!previous.FollowPreparedParent &&
                (previous.LockedFields & (RigHelperEditFields.Parent | RigHelperEditFields.Position | RigHelperEditFields.Orientation)) != 0)
                throw new InvalidOperationException("The camera frame basis is locked; changing to prepared-parent composition requires an explicit unlock.");
            if (previous.LockedFields.HasFlag(RigHelperEditFields.Position) && current.Translation != localFrame.Translation)
                throw new InvalidOperationException("The camera position is locked.");
            if (previous.LockedFields.HasFlag(RigHelperEditFields.Orientation) &&
                WithoutTranslation(current) != WithoutTranslation(localFrame))
                throw new InvalidOperationException("The camera orientation is locked.");
        }
        if (current.NearlyEquals(localFrame, 1e-12) && previous?.FramePolicy == RigFramePolicy.Camera && previous.FollowPreparedParent) return document;
        var evidence = new RigEvidenceReference
        {
            Id = $"camera-frame-review:{cameraEntityId:N}", Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = session.SourceSha256,
            Description = "Explicit independent parent-local camera calibration. Native frame consumers, clipping and channel ownership remain separately unverified.",
        };
        HelperRecipe recipe = (previous ?? new HelperRecipe
        {
            EntityId = cameraEntityId, OwnerAssetId = document.ModelId, ParentEntityId = parent,
            RoleId = bone.Name == Dl1PreviewContract.ReferenceCameraBoneName ? "camera.reference" :
                bone.Name == Dl1PreviewContract.EyeCameraBoneName ? "camera.eye" : "camera.authored",
            BoundsCenter = standalone?.BoundsCenter ?? Vector3D.Zero,
            BoundsHalfExtents = standalone?.BoundsHalfExtents ?? Vector3D.Zero,
        }) with
        {
            LocalFrame = localFrame, FramePolicy = RigFramePolicy.Camera, FollowPreparedParent = true, UserApproved = true,
            PlacementProvenance = RigEvidenceKind.UserOverride,
            Evidence = (previous?.Evidence ?? standalone?.Evidence ?? [])
                .Where(e => e.Id != evidence.Id).Append(evidence).ToImmutableArray(),
        };
        // Observed camera and parent representations can be classified without guessing runtime channel owners.
        var entities = session.Recipe.Entities.Select(e => e.EntityId == cameraEntityId || e.EntityId == parent
            ? e with { Kind = e.Kind == RigNativeEntityKind.Unknown
                ? (e.EntityId == cameraEntityId || bones[bone.ParentIndex].Kind is BoneKind.Camera or BoneKind.Helper or BoneKind.Prop
                    ? RigNativeEntityKind.Helper : RigNativeEntityKind.Bone) : e.Kind }
            : e).ToImmutableArray();
        var next = session with { Recipe = session.Recipe with
        {
            Entities = entities,
            Helpers = session.Recipe.Helpers.Where(h => h.EntityId != cameraEntityId).Append(recipe).ToImmutableArray(),
            FramePolicies = session.Recipe.FramePolicies.Where(p => p.EntityId != cameraEntityId).ToImmutableArray(),
        } };
        var result = document with { RiggingSession = RiggingSessions.Change(session, next, RiggingEditKind.Helpers), LastBuildReceipt = null };
        result = RiggingHelperMaterializer.Apply(result);
        result.Validate();
        RigProfileEditGuard.RequireDocumentAllowed(document, result);
        return result;
    }

    private static TransformMatrix SourceGlobal(ImmutableArray<CustomModelBone> bones, int index)
    {
        TransformMatrix result = bones[index].ExactLocalBindMatrix;
        for (int parent = bones[index].ParentIndex; parent >= 0; parent = bones[parent].ParentIndex)
            result = bones[parent].ExactLocalBindMatrix * result;
        return result;
    }

    private static TransformMatrix WithoutTranslation(TransformMatrix m) => new(
        m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0,
        m.M31, m.M32, m.M33, 0, m.M41, m.M42, m.M43, m.M44);

    private static RiggingSession RequireSession(CustomModelDocument document, RiggingJobToken token)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        document.Validate();
        var session = document.RiggingSession ?? throw new InvalidOperationException("Start a studio session before camera calibration.");
        if (!session.Matches(token) || !session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidOperationException("The camera calibration source changed. Inspect the current model again.");
        return session;
    }

    private static int Find(ImmutableArray<RigParentObservation> nodes, Guid id)
    {
        for (int i = 0; i < nodes.Length; i++) if (nodes[i].EntityId == id) return i;
        return -1;
    }
}
