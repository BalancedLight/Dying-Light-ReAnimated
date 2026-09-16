using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigRestPoseAuthoringTests
{
    [Fact]
    public void CompensatedChildrenRetainTheirStationaryEyeReview()
    {
        var document=RigidEyeBindingTests.WithEyes().Package.Document;
        int head=document.Bones.Single(b=>b.Name=="body_head").Index;
        var eye=document.RiggingSession!.Eyes.Single(e=>e.Side==RigEyeSide.Left);
        var before=Globals(document.CreateEffectiveBones());
        var entity=RiggingSessions.ObserveSourceHierarchy(document)[head].EntityId;
        var result=RigRestPoseAuthoring.Apply(document,document.RiggingSession.CreateJobToken(),entity,
            before[head]*TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY,.35)),RigRestDescendantMode.KeepGlobal);
        Assert.Same(eye,result.Document.RiggingSession!.Eyes.Single(e=>e.Side==RigEyeSide.Left));
        Assert.True(GeneratedBodyRig.IsGenerated(result.Document));
    }
    [Fact]
    public void KeepGlobalCompensatesDirectChildrenAndFollowLocalCarriesTheChange()
    {
        CustomModelDocument original = WithSession();
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(original);
        int selected = 0;
        int child = original.Bones.Select((bone, index) => (bone, index)).Single(row => row.bone.ParentIndex == selected).index;
        Guid entityId = observed[selected].EntityId;
        TransformMatrix beforeChild = Globals(original.CreateEffectiveBones())[child];
        TransformMatrix desired = Globals(original.CreateEffectiveBones())[selected] with { M14 = Globals(original.CreateEffectiveBones())[selected].M14 + .25 };

        RigRestPoseEditResult kept = RigRestPoseAuthoring.Apply(
            original, original.RiggingSession!.CreateJobToken(), entityId, desired, RigRestDescendantMode.KeepGlobal);
        RigRestPoseEditResult followed = RigRestPoseAuthoring.Apply(
            original, original.RiggingSession!.CreateJobToken(), entityId, desired, RigRestDescendantMode.FollowLocal);

        Assert.True(kept.AfterGlobals[child].NearlyEquals(beforeChild, 1e-10));
        Assert.False(followed.AfterGlobals[child].NearlyEquals(beforeChild, 1e-10));
        Assert.True(followed.Document.Bones[child].ExactLocalBindMatrix == original.Bones[child].ExactLocalBindMatrix);
        Assert.Contains(selected, kept.ChangedBoneIndices);
        Assert.Contains(child, kept.ChangedBoneIndices);
    }

    [Fact]
    public void UnchangedRequestReturnsTheSameDocumentWithoutARevision()
    {
        CustomModelDocument document = WithSession();
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        TransformMatrix global = Globals(document.CreateEffectiveBones())[0];

        RigRestPoseEditResult result = RigRestPoseAuthoring.Apply(
            document, document.RiggingSession!.CreateJobToken(), observed[0].EntityId, global, RigRestDescendantMode.KeepGlobal);

        Assert.Same(document, result.Document);
        Assert.Empty(result.ChangedBoneIndices);
        Assert.Empty(result.ChangedEntityIds);
        Assert.Equal(document.RiggingSession.Revision, result.Document.RiggingSession!.Revision);
    }

    [Fact]
    public void GeneratedEyeFrameAndFittedGlobeReviewBecomeManualAfterRestEdit()
    {
        CustomModelDocument source = WithSession();
        RiggingSession session = source.RiggingSession!;
        Guid parentId = RiggingSessions.ObserveSourceHierarchy(source)[0].EntityId;
        RigEyeSetup setup = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.GeometryPivot,
            GeometryKind = RigEyeGeometryKind.GlobeCandidate,
            ParentEntityId = parentId,
            GlobalFrame = TransformMatrix.CreateTranslation(new(.2, .3, .4)),
            ComponentId = session.Components[0].Id,
            IslandIndex = 0,
            SourceControlPointIds = [0, 1, 2],
            GlobeRadius = .2,
            UserApproved = true,
            Evidence = [new RigEvidenceReference
            {
                Id = "rest-eye-review",
                Kind = RigEvidenceKind.GeometryInference,
                ArtifactSha256 = session.SourceSha256,
                Description = "Reviewed generic globe candidate for rest-pose testing.",
            }],
        };
        source = source with { RiggingSession = session with { Eyes = [setup] } };
        source.Validate();
        CustomModelDocument generated = GeneratedEyeRig.Append(source, RigEyeSide.Left, "eye_left_deform");
        RiggingSession generatedSession = generated.RiggingSession!;
        Guid eyeEntity = generatedSession.Eyes[0].DeformEntityId!.Value;
        TransformMatrix oldGlobal = Globals(generated.Bones)[generated.Bones.Length - 1];
        TransformMatrix desired = oldGlobal with { M14 = oldGlobal.M14 + .15 };

        RigRestPoseEditResult result = RigRestPoseAuthoring.Apply(
            generated, generatedSession.CreateJobToken(), eyeEntity, desired, RigRestDescendantMode.FollowLocal);
        RigEyeSetup updated = Assert.Single(result.Document.RiggingSession!.Eyes);

        Assert.False(updated.UserApproved);
        Assert.Equal(RigEyeGeometryKind.ManualPivot, updated.GeometryKind);
        Assert.Null(updated.GlobeRadius);
        Assert.True(updated.GlobalFrame.NearlyEquals(desired, 1e-10));
        Assert.Equal(eyeEntity, updated.DeformEntityId);
    }

    [Fact]
    public void KeepGlobalHonorsLockedDirectHelperPosition()
    {
        CustomModelDocument source = WithSession();
        CustomModelDocument withHelper = CustomModelHelperAuthoring.DuplicateAsHelper(
            source, 0, CustomModelAuthoredHelperKind.Helper, "locked_rest_helper");
        RiggingSession session = withHelper.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        Guid helperId = withHelper.AuthoredHelpers[0].Id;
        var entities = session.Recipe.Entities.ToBuilder();
        entities[0] = entities[0] with { Kind = RigNativeEntityKind.Bone };
        HelperRecipe recipe = new()
        {
            EntityId = helperId,
            OwnerAssetId = withHelper.ModelId,
            RoleId = "locked.rest.helper",
            ParentEntityId = parentId,
            LocalFrame = withHelper.AuthoredHelpers[0].ExactLocalMatrix,
            FramePolicy = RigFramePolicy.Manual,
            PlacementProvenance = RigEvidenceKind.UserOverride,
            LockedFields = RigHelperEditFields.Position,
        };
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = entities.ToImmutable(),
                Helpers = [recipe],
                FramePolicies = [],
            },
        };
        withHelper = withHelper with { RiggingSession = session };
        withHelper.Validate();
        TransformMatrix selected = Globals(withHelper.CreateEffectiveBones())[0];
        TransformMatrix desired = selected with { M14 = selected.M14 + .1 };

        Assert.Throws<InvalidOperationException>(() => RigRestPoseAuthoring.Apply(
            withHelper, session.CreateJobToken(), parentId, desired, RigRestDescendantMode.KeepGlobal));
    }

    [Fact]
    public void KeepGlobalHonorsLockedImportedHelperRecipe()
    {
        CustomModelDocument source = WithSession();
        RiggingSession session = source.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        int childIndex = source.Bones.Select((bone, index) => (bone, index)).Single(row => row.bone.ParentIndex == 0).index;
        Guid childId = session.Recipe.Entities[childIndex].EntityId;
        var entities = session.Recipe.Entities.ToBuilder();
        entities[0] = entities[0] with { Kind = RigNativeEntityKind.Bone };
        entities[childIndex] = entities[childIndex] with { Kind = RigNativeEntityKind.Helper };
        HelperRecipe recipe = new()
        {
            EntityId = childId,
            OwnerAssetId = source.ModelId,
            RoleId = "locked.imported.helper",
            ParentEntityId = parentId,
            LocalFrame = source.Bones[childIndex].ExactLocalBindMatrix,
            FramePolicy = RigFramePolicy.Manual,
            PlacementProvenance = RigEvidenceKind.ImportedSource,
            LockedFields = RigHelperEditFields.Position,
        };
        session = session with
        {
            Recipe = session.Recipe with { Entities = entities.ToImmutable(), Helpers = [recipe], FramePolicies = [] },
        };
        CustomModelDocument document = source with { RiggingSession = session };
        document.Validate();
        TransformMatrix selected = Globals(document.CreateEffectiveBones())[0];
        TransformMatrix desired = selected with { M14 = selected.M14 + .1 };

        Assert.Throws<InvalidOperationException>(() => RigRestPoseAuthoring.Apply(
            document, session.CreateJobToken(), parentId, desired, RigRestDescendantMode.KeepGlobal));
    }

    [Fact]
    public void GeneratedWristRestEditUpdatesPalmAndPreservesReviewedHandFrames()
    {
        CustomModelDocument body = GeneratedEyeRigTests.GeneratedBodyWithReviewedHands().Package.Document;
        CustomModelDocument withHand = GeneratedHandRig.Append(body, RigHandSide.Left);
        RiggingSession session = withHand.RiggingSession!;
        Guid wristEntity = session.Recipe.Assignments.Single(assignment => assignment.RoleId == "hand.left").EntityId;
        RigHandSetup oldHand = session.Hands.Single(hand => hand.Side == RigHandSide.Left);
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(withHand);
        int wristIndex = observed.Select((row, index) => (row, index)).Single(row => row.row.EntityId == wristEntity).index;
        TransformMatrix oldWrist = Globals(withHand.CreateEffectiveBones())[wristIndex];
        TransformMatrix desired = oldWrist with { M14 = oldWrist.M14 + .1 };

        RigRestPoseEditResult edit = RigRestPoseAuthoring.Apply(
            withHand, session.CreateJobToken(), wristEntity, desired, RigRestDescendantMode.FollowLocal);
        RigHandSetup updatedHand = edit.Document.RiggingSession!.Hands.Single(hand => hand.Side == RigHandSide.Left);

        Assert.False(updatedHand.UserApproved);
        Assert.False(updatedHand.PalmFrame.NearlyEquals(oldHand.PalmFrame, 1e-12));
        Assert.Contains(edit.Document.RiggingSession.Recipe.FramePolicies, policy =>
            policy.EntityId != Guid.Empty && policy.Evidence.Any(evidence => evidence.Id.StartsWith("rest-pose-manual:", StringComparison.Ordinal)));
        RiggingSession reviewed = edit.Document.RiggingSession with
        {
            Hands = edit.Document.RiggingSession.Hands.Select(hand => hand with
            {
                UserApproved = true,
                Fingers = hand.Fingers.Select(finger => finger with { UserApproved = true }).ToImmutableArray(),
            }).ToImmutableArray(),
        };
        CustomModelDocument rereviewed = edit.Document with { RiggingSession = reviewed };
        rereviewed.Validate();
        Assert.Same(rereviewed, GeneratedHandRig.Append(rereviewed, RigHandSide.Left));

        RiggingSession rereviewedSession = rereviewed.RiggingSession!;
        RigHandSetup editedHand = rereviewedSession.Hands.Single(hand => hand.Side == RigHandSide.Left) with
        {
            Fingers = [rereviewedSession.Hands.Single(hand => hand.Side == RigHandSide.Left).Fingers[0] with { RollDegrees = 9.0 }],
        };
        CustomModelDocument edited = rereviewed with
        {
            RiggingSession = rereviewedSession with { Hands = [editedHand, rereviewedSession.Hands.Single(hand => hand.Side == RigHandSide.Right)] },
        };
        edited.Validate();
        Assert.Throws<InvalidOperationException>(() => GeneratedHandRig.Append(edited, RigHandSide.Left));
    }

    private static CustomModelDocument WithSession()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "rest-pose.fbx");
        CustomModelDocument document = imported.Package.Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        return document with { RiggingSession = session };
    }

    private static TransformMatrix[] Globals(IReadOnlyList<CustomModelBone> bones)
    {
        var result = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones)
            result[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : result[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return result;
    }
}
