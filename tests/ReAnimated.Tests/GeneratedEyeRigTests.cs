using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class GeneratedEyeRigTests
{
    [Fact]
    public void AppendsOwnedUnweightedEyeAndPreservesImportedSource()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        TransformMatrix parentGlobal = Globals(original.Bones)[0];
        TransformMatrix global = parentGlobal * TransformMatrix.CreateTranslation(new(.2, .3, .4));
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, parentId, global);
        original = original with { RiggingSession = session with { Eyes = [setup] } };
        original.Validate();

        CustomModelDocument result = GeneratedEyeRig.Append(original, RigEyeSide.Left, "eye_left_deform");
        CustomModelBone eye = result.Bones[^1];
        RigEyeSetup saved = Assert.Single(result.RiggingSession!.Eyes);
        Assert.Equal(BoneKind.Deform, eye.Kind);
        Assert.False(eye.IsWeighted);
        Assert.Equal("eye_left_deform", eye.Name);
        Assert.Equal(original.Bones.Length, eye.Index);
        Assert.Equal(saved.DeformEntityId, result.RiggingSession.Recipe.Entities.Single(entity => entity.NativeName == eye.Name).EntityId);
        Assert.Equal(eye.Index, GeneratedEyeRig.GetBoneIndex(result, RigEyeSide.Left));
        Assert.True(Globals(result.Bones)[eye.Index].NearlyEquals(global, 1e-10));
        Assert.Equal<CustomModelBone>(original.Bones, result.Bones.RemoveAt(result.Bones.Length - 1));
        Assert.Equal<CustomModelMeshPart>(original.Meshes, result.Meshes);
        Assert.Equal(original.Source, result.Source);
    }

    [Fact]
    public void ExistingEyeIsSemanticNoOpButChangedFrameRequiresSeparateTransaction()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, parentId,
            TransformMatrix.CreateTranslation(new(.2, .3, .4)));
        original = original with { RiggingSession = session with { Eyes = [setup] } };
        original.Validate();

        CustomModelDocument first = GeneratedEyeRig.Append(original, RigEyeSide.Left, "eye_left_deform");
        CustomModelDocument repeated = GeneratedEyeRig.Append(first, RigEyeSide.Left, "eye_left_deform");
        Assert.Same(first, repeated);

        RiggingSession changedSession = RiggingSessions.Change(first.RiggingSession!, first.RiggingSession! with
        {
            Eyes = [first.RiggingSession.Eyes[0] with { GlobalFrame = first.RiggingSession.Eyes[0].GlobalFrame with { M14 = .9 } }],
        }, RiggingEditKind.Anatomy);
        CustomModelDocument changed = first with { RiggingSession = changedSession };
        Assert.Throws<InvalidOperationException>(() => GeneratedEyeRig.Append(changed, RigEyeSide.Left, "eye_left_deform"));
    }

    [Fact]
    public void AppendsBothSidesInOrderAndRejectsCameraNamesAndMissingSupport()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup left = Setup(session, RigEyeSide.Left, parentId, TransformMatrix.CreateTranslation(new(-.2, .3, .4)));
        original = original with { RiggingSession = session with { Eyes = [left] } };
        original.Validate();
        CustomModelDocument first = GeneratedEyeRig.Append(original, RigEyeSide.Left, "eye_left_deform");
        RiggingSession firstSession = first.RiggingSession!;
        RigEyeSetup right = Setup(firstSession, RigEyeSide.Right, parentId, TransformMatrix.CreateTranslation(new(.2, .3, .4)));
        first = first with { RiggingSession = firstSession with { Eyes = firstSession.Eyes.Add(right) } };
        first.Validate();
        CustomModelDocument both = GeneratedEyeRig.Append(first, RigEyeSide.Right, "eye_right_deform");

        Assert.Equal(2, both.RiggingSession!.Eyes.Count(eye => eye.DeformEntityId is not null));
        Assert.Equal("eye_left_deform", both.Bones[^2].Name);
        Assert.Equal("eye_right_deform", both.Bones[^1].Name);
        Assert.Throws<InvalidOperationException>(() => GeneratedEyeRig.Append(original, RigEyeSide.Left, "EyeCamera"));
        CustomModelDocument missingSupport = original with
        {
            RiggingSession = session with
            {
                Eyes = [left with { SourceControlPointIds = [] }],
            },
        };
        Assert.Throws<InvalidOperationException>(() => GeneratedEyeRig.Append(missingSupport, RigEyeSide.Left,
            "eye_left_deform"));
    }

    [Fact]
    public void ShiftsOnlyAuthoredHelperParentsWhenEyeBoneIsAppended()
    {
        CustomModelDocument original = WithSession();
        CustomModelDocument parentHelper = CustomModelHelperAuthoring.DuplicateAsHelper(
            original, 0, CustomModelAuthoredHelperKind.Helper, "eye_parent_helper");
        CustomModelDocument childHelper = CustomModelHelperAuthoring.DuplicateAsHelper(
            parentHelper, parentHelper.Bones.Length, CustomModelAuthoredHelperKind.Helper, "eye_child_helper");
        RiggingSession session = childHelper.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, parentId, TransformMatrix.CreateTranslation(new(.1, .2, .3)));
        childHelper = childHelper with { RiggingSession = session with { Eyes = [setup] } };
        childHelper.Validate();
        int oldBoneCount = childHelper.Bones.Length;
        int oldChildParent = childHelper.AuthoredHelpers[1].ParentNodeIndex;

        CustomModelDocument result = GeneratedEyeRig.Append(childHelper, RigEyeSide.Left, "eye_left_deform");

        Assert.Equal(oldChildParent + 1, result.AuthoredHelpers[1].ParentNodeIndex);
        Assert.Equal(oldBoneCount, result.Bones.Length - 1);
        Assert.Equal(parentHelper.AuthoredHelpers[0].Id, result.AuthoredHelpers[0].Id);
        Assert.Equal(childHelper.Source, result.Source);
    }

    [Fact]
    public void UsesExactAffineParentCompositionAndRejectsUnreviewedOrNonEyeModes()
    {
        CustomModelDocument original = WithSession();
        TransformMatrix parent = new(
            2, .3, 0, 0,
            0, 1.5, .2, 0,
            0, 0, .75, 0,
            0, 0, 0, 1);
        original = original with
        {
            Bones = original.Bones.SetItem(0, original.Bones[0] with
            {
                LocalBindTransform = TransformTRS.Identity,
                ExactLocalBindMatrix = parent,
            }),
        };
        RiggingSession session = RiggingSessions.Create(original, RigStudioEntryPath.RepairExistingRig);
        Guid parentId = session.Recipe.Entities[0].EntityId;
        TransformMatrix global = parent * new TransformMatrix(
            1, .15, 0, .3,
            0, 1, .1, .2,
            0, 0, 1, .4,
            0, 0, 0, 1);
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, parentId, global);
        original = original with { RiggingSession = session with { Eyes = [setup] } };
        original.Validate();
        CustomModelDocument result = GeneratedEyeRig.Append(original, RigEyeSide.Left, "eye_left_deform");
        CustomModelBone eye = result.Bones[^1];
        Assert.True(eye.ExactLocalBindMatrix.NearlyEquals(parent.InvertedAffine() * global, 1e-10));
        Assert.True(Globals(result.Bones)[eye.Index].NearlyEquals(global, 1e-10));
        Assert.Throws<InvalidOperationException>(() => GeneratedEyeRig.Append(
            original, RigEyeSide.Right, "RefCamera"));
    }

    [Fact]
    public void SupportsSharedEyeSideForSingleEyeAnatomy()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup setup = Setup(session, RigEyeSide.Shared, parentId,
            TransformMatrix.CreateTranslation(new(0, .3, .4)));
        original = original with { RiggingSession = session with { Eyes = [setup] } };
        original.Validate();

        CustomModelDocument result = GeneratedEyeRig.Append(original, RigEyeSide.Shared, "eye_shared_deform");

        RigEyeSetup saved = Assert.Single(result.RiggingSession!.Eyes);
        Assert.Equal(RigEyeSide.Shared, saved.Side);
        Assert.Equal("generated-eye:eye.shared.deform",
            result.RiggingSession.Recipe.Entities.Single(entity => entity.EntityId == saved.DeformEntityId).SourceEntityId);
        Assert.Equal(result.Bones.Length - 1, GeneratedEyeRig.GetBoneIndex(result, RigEyeSide.Shared));
    }

    [Fact]
    public void InterleavesEyesAndFingerChainsWithoutLeakingEyesIntoBodySegments()
    {
        FbxModelAuthoringImportResult generated = GeneratedBodyWithReviewedHands();
        CustomModelDocument body = generated.Package.Document;
        CustomModelDocument withParent = CustomModelHelperAuthoring.DuplicateAsHelper(
            body, 8, CustomModelAuthoredHelperKind.Helper, "interleave_parent");
        CustomModelDocument withChild = CustomModelHelperAuthoring.DuplicateAsHelper(
            withParent, withParent.Bones.Length, CustomModelAuthoredHelperKind.Helper, "interleave_child");
        int oldChildParent = withChild.AuthoredHelpers[1].ParentNodeIndex;

        RiggingSession session = withChild.RiggingSession!;
        Guid leftParent = session.Recipe.Assignments.Single(assignment => assignment.RoleId == "hand.left").EntityId;
        RigEyeSetup left = Eye(session, RigEyeSide.Left, leftParent, new(-1.8, 1.55, .1));
        withChild = withChild with { RiggingSession = session with { Eyes = [left] } };
        withChild.Validate();
        CustomModelDocument leftEye = GeneratedEyeRig.Append(withChild, RigEyeSide.Left, "eye_left_deform");
        CustomModelDocument leftHand = GeneratedHandRig.Append(leftEye, RigHandSide.Left);

        RiggingSession leftSession = leftHand.RiggingSession!;
        Guid rightParent = leftSession.Recipe.Assignments.Single(assignment => assignment.RoleId == "hand.right").EntityId;
        RigEyeSetup right = Eye(leftSession, RigEyeSide.Right, rightParent, new(1.8, 1.55, .1));
        leftHand = leftHand with { RiggingSession = leftSession with { Eyes = leftSession.Eyes.Add(right) } };
        leftHand.Validate();
        CustomModelDocument rightEye = GeneratedEyeRig.Append(leftHand, RigEyeSide.Right, "eye_right_deform");
        CustomModelDocument completed = GeneratedHandRig.Append(rightEye, RigHandSide.Right);

        Assert.True(GeneratedBodyRig.IsGenerated(completed));
        ImmutableArray<GeneratedBodySegment> segments = GeneratedBodyRig.GetSegments(completed);
        Assert.Contains(segments, segment => segment.RoleId.StartsWith("finger.left.index.", StringComparison.Ordinal));
        Assert.Contains(segments, segment => segment.RoleId.StartsWith("finger.right.index.", StringComparison.Ordinal));
        Assert.DoesNotContain(segments, segment => segment.RoleId.StartsWith("eye.", StringComparison.Ordinal));
        Assert.Equal(oldChildParent + 8, completed.AuthoredHelpers[1].ParentNodeIndex);
        Assert.Contains(completed.Bones, bone => bone.Name == "eye_left_deform");
        Assert.Contains(completed.Bones, bone => bone.Name == "eye_right_deform");

        CustomModelBone unknown = completed.Bones[^1] with
        {
            Index = completed.Bones.Length,
            Name = "unowned_extension",
            ParentIndex = 0,
            FbxObjectId = 0,
        };
        Assert.False(GeneratedBodyRig.IsGenerated(completed with { Bones = completed.Bones.Add(unknown) }));
    }

    private static RigEyeSetup Setup(RiggingSession session, RigEyeSide side, Guid parentId, TransformMatrix global) => new()
    {
        Side = side,
        Mode = RigEyeSetupMode.GeometryPivot,
        GeometryKind = RigEyeGeometryKind.ManualPivot,
        ParentEntityId = parentId,
        GlobalFrame = global,
        ComponentId = session.Components[0].Id,
        IslandIndex = 0,
        SourceControlPointIds = [0, 1],
        UserApproved = true,
        Evidence = [new RigEvidenceReference
        {
            Id = "generated-eye-test-review",
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = session.SourceSha256,
            Description = "Reviewed generic generated eye frame.",
        }],
    };

    private static RigEyeSetup Eye(RiggingSession session, RigEyeSide side, Guid parentId, Vector3D position) => new()
    {
        Side = side,
        Mode = RigEyeSetupMode.GeometryPivot,
        GeometryKind = RigEyeGeometryKind.ManualPivot,
        ParentEntityId = parentId,
        GlobalFrame = TransformMatrix.CreateTranslation(position),
        ComponentId = session.Components[0].Id,
        IslandIndex = 0,
        SourceControlPointIds = [0, 1],
        UserApproved = true,
        Evidence = [new RigEvidenceReference
        {
            Id = "interleaved-eye-review-" + side,
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = session.SourceSha256,
            Description = "Reviewed generic interleaved eye frame.",
        }],
    };

    internal static FbxModelAuthoringImportResult GeneratedBodyWithReviewedHands()
    {
        FbxModelAuthoringImportResult source = GeneratedBodyWorkflowTests.WithFixtureGuides(
            GeneratedBodyWorkflowTests.Source());
        CustomModelDocument document = source.Package.Document;
        RiggingSession session = document.RiggingSession!;
        (RigFingerDeclaration leftFinger, ImmutableArray<RigLandmark> leftGuides) = Finger("left", 700, -1.6);
        (RigFingerDeclaration rightFinger, ImmutableArray<RigLandmark> rightGuides) = Finger("right", 800, 1.6);
        ImmutableArray<RigLandmark> allGuides = session.Landmarks.Concat(leftGuides).Concat(rightGuides).ToImmutableArray();
        RigLandmark leftWrist = allGuides.Single(guide => guide.RoleId == "hand.left");
        RigLandmark rightWrist = allGuides.Single(guide => guide.RoleId == "hand.right");
        session = session with
        {
            Landmarks = allGuides,
            Hands =
            [
                new RigHandSetup
                {
                    Side = RigHandSide.Left, WristGuideId = leftWrist.Id, UserApproved = true,
                    Fingers = [leftFinger],
                },
                new RigHandSetup
                {
                    Side = RigHandSide.Right, WristGuideId = rightWrist.Id, UserApproved = true,
                    Fingers = [rightFinger],
                },
            ],
        };
        session.Validate();
        return FbxGeneratedBodyBinding.Generate(source with
        {
            Package = source.Package with
            {
                Document = document with { RiggingSession = session },
            },
        });
    }

    private static (RigFingerDeclaration Declaration, ImmutableArray<RigLandmark> Guides) Finger(
        string side, int seed, double startX)
    {
        ImmutableArray<RigLandmark> guides = Enumerable.Range(1, 4).Select(segment => new RigLandmark
        {
            Id = new Guid(seed + segment, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            RoleId = $"finger.{side}.index.{segment}",
            Position = new(startX + (side == "left" ? -.2 * segment : .2 * segment), 1.55, .1),
            Provenance = RigEvidenceKind.GeometryInference,
        }).ToImmutableArray();
        return (new RigFingerDeclaration
        {
            Id = "index",
            Presence = RigFingerPresence.Present,
            JointGuideIds = guides.Select(guide => guide.Id).ToImmutableArray(),
            CurlPlaneNormal = Vector3D.UnitY,
            UserApproved = true,
        }, guides);
    }

    private static CustomModelDocument WithSession()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generated-eye.fbx");
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
