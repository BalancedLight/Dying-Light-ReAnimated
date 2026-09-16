using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigHierarchyAuthoringTests
{
    [Fact]
    public void ReparentRetainsEveryGlobalAndStableIdentity()
    {
        CustomModelDocument document = WithGeneratedSession();
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        (int selected, int parent) = FindLaterNonDescendant(document.Bones);
        TransformMatrix[] before = Globals(document.CreateEffectiveBones());
        Guid selectedId = observed[selected].EntityId;
        Guid parentId = observed[parent].EntityId;
        RigHierarchyEditResult result = RigHierarchyAuthoring.Apply(
            document, document.RiggingSession!.CreateJobToken(), selectedId, parentId);
        TransformMatrix[] after = Globals(result.Document.CreateEffectiveBones());

        Assert.Equal(document.Bones.Length, result.OldToNewBoneIndices.Length);
        Assert.Equal(document.CreateEffectiveBones().Length, result.OldToNewEffectiveIndices.Length);
        ImmutableArray<RigParentObservation> reordered = RiggingSessions.ObserveSourceHierarchy(result.Document);
        for (int oldIndex = 0; oldIndex < before.Length; oldIndex++)
        {
            int newIndex = result.OldToNewEffectiveIndices[oldIndex];
            Assert.True(before[oldIndex].NearlyEquals(after[newIndex], 1e-10));
            Assert.Equal(observed[oldIndex].EntityId, reordered[newIndex].EntityId);
        }
        Assert.Equal(selectedId, result.Document.RiggingSession!.ParentDecisions.Single().EntityId);
        Assert.Equal(parentId, result.Document.RiggingSession.ParentDecisions[0].ParentEntityId);
        Assert.Equal(document.Source, result.Document.Source);
        for (int oldIndex = 0; oldIndex < document.Bones.Length; oldIndex++)
            Assert.Equal(document.Bones[oldIndex].Name, result.Document.Bones[result.OldToNewBoneIndices[oldIndex]].Name);
    }

    [Fact]
    public void WorldParentIsSupportedAndNoOpDoesNotAdvanceRevision()
    {
        CustomModelDocument document = WithGeneratedSession();
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        int selected = Enumerable.Range(0, document.Bones.Length).First(index => document.Bones[index].ParentIndex >= 0);
        Guid entityId = observed[selected].EntityId;
        RigHierarchyEditResult result = RigHierarchyAuthoring.Apply(
            document, document.RiggingSession!.CreateJobToken(), entityId, null);
        Assert.Equal(-1, result.Document.Bones[result.OldToNewBoneIndices[selected]].ParentIndex);
        long revision = result.Document.RiggingSession!.Revision;
        RigHierarchyEditResult noOp = RigHierarchyAuthoring.Apply(
            result.Document, result.Document.RiggingSession.CreateJobToken(), entityId, null);
        Assert.Same(result.Document, noOp.Document);
        Assert.Equal(revision, noOp.Document.RiggingSession!.Revision);
    }

    [Fact]
    public void RejectsCyclesAndForeignParents()
    {
        CustomModelDocument document = WithGeneratedSession();
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        int selected = Enumerable.Range(0, document.Bones.Length).First(index => document.Bones[index].ParentIndex >= 0);
        int descendant = document.Bones.Select((bone, index) => (bone, index)).FirstOrDefault(row => IsDescendant(document.Bones, selected, row.index) && row.index != selected).index;
        if (descendant >= 0)
            Assert.Throws<InvalidOperationException>(() => RigHierarchyAuthoring.Apply(document,
                document.RiggingSession!.CreateJobToken(), observed[selected].EntityId, observed[descendant].EntityId));
        Assert.Throws<ArgumentException>(() => RigHierarchyAuthoring.Apply(document,
            document.RiggingSession!.CreateJobToken(), observed[selected].EntityId, Guid.NewGuid()));
    }

    [Fact]
    public void LockedSourceHelperRecipeCannotLoseItsParentOrLocalDecision()
    {
        CustomModelDocument source = WithGeneratedSession();
        (int child, int alternate) = FindLaterNonDescendant(source.Bones);
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(source);
        Guid childId = observed[child].EntityId;
        Guid parentId = observed[source.Bones[child].ParentIndex].EntityId;
        RiggingSession session = source.RiggingSession!;
        var entities = session.Recipe.Entities.ToBuilder();
        entities[0] = entities[0] with { Kind = ReAnimated.Core.ModelAuthoring.RigNativeEntityKind.Bone };
        entities[child] = entities[child] with { Kind = ReAnimated.Core.ModelAuthoring.RigNativeEntityKind.Helper };
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = entities.ToImmutable(),
                Helpers = [new HelperRecipe
                {
                    EntityId = childId, OwnerAssetId = source.ModelId, RoleId = "locked.source.helper",
                    ParentEntityId = parentId, LocalFrame = source.Bones[child].ExactLocalBindMatrix,
                    FramePolicy = RigFramePolicy.Manual, PlacementProvenance = RigEvidenceKind.ImportedSource,
                    LockedFields = RigHelperEditFields.Parent | RigHelperEditFields.Position,
                }],
                FramePolicies = [],
            },
        };
        CustomModelDocument document = source with { RiggingSession = session };
        document.Validate();
        Assert.Throws<InvalidOperationException>(() => RigHierarchyAuthoring.Apply(
            document, session.CreateJobToken(), observed[child].EntityId, observed[alternate].EntityId));
    }

    [Fact]
    public void StructuralSourceHelperCanBecomeBaseParentWithoutMovingGlobals()
    {
        CustomModelDocument source = WithSession();
        RiggingSession session = source.RiggingSession!;
        int selected = Enumerable.Range(1, source.Bones.Length - 1).First(index => source.Bones[index].ParentIndex >= 0);
        long helperFbxId = 9000001;
        Guid helperEntityId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        CustomModelBone helperBone = new()
        {
            Index = source.Bones.Length,
            FbxObjectId = helperFbxId,
            Name = "source_structural_helper",
            ParentIndex = -1,
            LocalBindTransform = TransformTRS.Identity,
            ExactLocalBindMatrix = TransformMatrix.Identity,
            Kind = ReAnimated.Core.Domain.BoneKind.Helper,
        };
        var entities = session.Recipe.Entities.ToBuilder();
        entities.Add(new RigEntityBinding
        {
            EntityId = helperEntityId,
            OwnerAssetId = source.ModelId,
            SourceEntityId = "fbx:" + helperFbxId,
            NativeName = helperBone.Name,
            Kind = RigNativeEntityKind.Helper,
        });
        session = session with { Recipe = session.Recipe with { Entities = entities.ToImmutable() } };
        CustomModelDocument document = source with
        {
            Bones = source.Bones.Add(helperBone),
            RiggingSession = session,
        };
        document.Validate();
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        TransformMatrix[] before = Globals(document.CreateEffectiveBones());
        RigHierarchyEditResult result = RigHierarchyAuthoring.Apply(
            document, session.CreateJobToken(), observed[selected].EntityId, helperEntityId);
        TransformMatrix[] after = Globals(result.Document.CreateEffectiveBones());

        Assert.True(before[selected].NearlyEquals(after[result.OldToNewEffectiveIndices[selected]], 1e-10));
        Assert.Equal(result.Document.Bones[result.OldToNewBoneIndices[selected]].ParentIndex,
            result.OldToNewBoneIndices[source.Bones.Length]);
    }

    [Fact]
    public void InvalidGeneratedBodyCannotBeAdmittedByAnOtherwiseValidEyeExtension()
    {
        CustomModelDocument body = GeneratedEyeRigTests.GeneratedBodyWithReviewedHands().Package.Document;
        RiggingSession session = body.RiggingSession!;
        Guid parentId = session.Recipe.Assignments.Single(assignment => assignment.RoleId == "hand.left").EntityId;
        RigEyeSetup eye = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.GeometryPivot,
            GeometryKind = RigEyeGeometryKind.ManualPivot,
            ParentEntityId = parentId,
            GlobalFrame = TransformMatrix.CreateTranslation(new(-1.8, 1.55, .1)),
            ComponentId = session.Components[0].Id,
            IslandIndex = 0,
            SourceControlPointIds = [0, 1],
            UserApproved = true,
        };
        body = body with { RiggingSession = session with { Eyes = [eye] } };
        body.Validate();
        CustomModelDocument withEye = GeneratedEyeRig.Append(body, RigEyeSide.Left, "eye_left_deform");
        CustomModelDocument invalidBody = withEye with
        {
            Bones = withEye.Bones.SetItem(0, withEye.Bones[0] with { Name = "corrupt_body_root" }),
        };
        invalidBody.Validate();

        Assert.False(GeneratedBodyRig.IsGenerated(invalidBody));
        Assert.Throws<InvalidOperationException>(() => RiggingSessions.ObserveSourceHierarchy(invalidBody));
    }

    [Fact]
    public void ReparentedMiddleFingerRetainsSemanticRestSegmentEndpoints()
    {
        CustomModelDocument body = GeneratedEyeRigTests.GeneratedBodyWithReviewedHands().Package.Document;
        CustomModelDocument withHand = GeneratedHandRig.Append(body, RigHandSide.Left);
        RiggingSession session = withHand.RiggingSession!;
        Guid middleFinger = session.Recipe.Assignments.Single(assignment => assignment.RoleId == "finger.left.index.2").EntityId;
        Guid alternateParent = session.Recipe.Assignments.Single(assignment => assignment.RoleId == "arm.left.upper").EntityId;
        ImmutableArray<GeneratedBodySegment> before = GeneratedBodyRig.GetSegments(withHand)
            .Where(segment => segment.RoleId.StartsWith("finger.left.index.", StringComparison.Ordinal)).ToImmutableArray();

        RigHierarchyEditResult edit = RigHierarchyAuthoring.Apply(
            withHand, session.CreateJobToken(), middleFinger, alternateParent);
        ImmutableArray<GeneratedBodySegment> after = GeneratedBodyRig.GetSegments(edit.Document)
            .Where(segment => segment.RoleId.StartsWith("finger.left.index.", StringComparison.Ordinal)).ToImmutableArray();

        Assert.Equal(before.Length, after.Length);
        foreach (GeneratedBodySegment expected in before)
        {
            GeneratedBodySegment actual = after.Single(segment => segment.EntityId == expected.EntityId);
            Assert.True((expected.Start-actual.Start).Length < 1e-10);
            Assert.True((expected.End-actual.End).Length < 1e-10);
        }
        Assert.True(GeneratedBodyRig.IsGenerated(edit.Document));
    }

    private static CustomModelDocument WithSession()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "hierarchy.fbx");
        CustomModelDocument document = imported.Package.Document;
        return document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
    }

    private static CustomModelDocument WithGeneratedSession() =>
        GeneratedEyeRigTests.GeneratedBodyWithReviewedHands().Package.Document;

    private static (int Selected, int Parent) FindLaterNonDescendant(IReadOnlyList<CustomModelBone> bones)
    {
        for (int selected = 1; selected < bones.Count; selected++)
            for (int parent = selected + 1; parent < bones.Count; parent++)
                if (bones[parent].ParentIndex != selected && !IsDescendant(bones, selected, parent))
                    return (selected, parent);
        throw new InvalidOperationException("The generic fixture has no later non-descendant parent candidate.");
    }

    private static bool IsDescendant(IReadOnlyList<CustomModelBone> bones, int ancestor, int candidate)
    {
        for (int current = candidate; current >= 0; current = bones[current].ParentIndex)
            if (current == ancestor) return true;
        return false;
    }

    private static TransformMatrix[] Globals(IReadOnlyList<CustomModelBone> bones)
    {
        var result = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones)
            result[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : result[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return result;
    }
}
