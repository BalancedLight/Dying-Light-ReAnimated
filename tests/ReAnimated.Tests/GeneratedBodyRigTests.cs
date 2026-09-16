using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class GeneratedBodyRigTests
{
    private static readonly string[] Roles =
    [
        "body.pelvis", "body.spine.0", "body.spine.1", "body.spine.2", "body.neck.0", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left", "arm.right.upper", "arm.right.lower", "hand.right",
        "leg.left.upper", "leg.left.lower", "foot.left", "leg.right.upper", "leg.right.lower", "foot.right",
    ];

    [Fact]
    public void BuildsTopologicallyOrderedGuideFramesWithStableIdentities()
    {
        CustomModelDocument document = Document(out RiggingSession session);

        GeneratedBodyRigResult first = GeneratedBodyRig.Build(document);
        GeneratedBodyRigResult second = GeneratedBodyRig.Build(document);

        Assert.Equal(19, first.Bones.Length);
        Assert.Equal(18, first.Segments.Length);
        Assert.Equal<CustomModelBone>(first.Bones, second.Bones);
        Assert.Equal(first.Session.Recipe.Entities.Select(static entity => entity.EntityId), second.Session.Recipe.Entities.Select(static entity => entity.EntityId));
        Assert.Equal("root_motion", first.Bones[0].Name);
        Assert.Equal(-1, first.Bones[0].ParentIndex);
        Assert.False(first.Bones[0].IsWeighted);
        Assert.All(first.Bones.Skip(1), bone =>
        {
            Assert.Equal(BoneKind.Deform, bone.Kind);
            Assert.True(bone.IsWeighted);
            Assert.True(bone.ExactLocalBindMatrix.IsFinite);
            Assert.NotEqual(0.0, bone.ExactLocalBindMatrix.LinearDeterminant);
        });
        Assert.Equal(session.Id, first.Session.Id);
        Assert.Equal(session.Revision + 1, first.Session.Revision);
        Assert.All(first.Session.Landmarks, landmark => Assert.Equal(
            session.Landmarks.Single(original => original.Id == landmark.Id).Position,
            landmark.Position));
    }

    [Fact]
    public void GeneratedGlobalFramesPointPlusXAlongDeclaredSegments()
    {
        CustomModelDocument document = Document(out _);
        GeneratedBodyRigResult result = GeneratedBodyRig.Build(document);
        var globals = new TransformMatrix[result.Bones.Length];
        foreach (CustomModelBone bone in result.Bones)
        {
            globals[bone.Index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        }

        foreach (GeneratedBodySegment segment in result.Segments)
        {
            CustomModelBone bone = result.Bones.Single(candidate => candidate.Name == segment.RoleId.Replace('.', '_'));
            TransformMatrix global = globals[bone.Index];
            Vector3D x = new(global.M11, global.M21, global.M31);
            Vector3D expected = segment.End != segment.Start
                ? (segment.End - segment.Start).Normalized(epsilon: 0.0)
                : (segment.Start - globals[bone.ParentIndex].Translation).Normalized(epsilon: 0.0);
            Assert.InRange((x - expected).Length, 0.0, 1e-10);
            Assert.Equal(segment.Start, global.Translation);
        }
        Assert.All(result.Segments.Where(static segment =>
            segment.RoleId is "body.head" or "hand.left" or "hand.right" or "foot.left" or "foot.right"),
            segment => Assert.Equal(segment.Start, segment.End));
        Assert.All(result.Segments.Where(static segment =>
            segment.RoleId is not ("body.head" or "hand.left" or "hand.right" or "foot.left" or "foot.right")),
            segment => Assert.NotEqual(segment.Start, segment.End));
        Assert.Equal(result.Segments.Single(segment => segment.RoleId == "body.pelvis").End, globals[2].Translation);
    }

    [Fact]
    public void RecognizesOwnedRigAfterDocumentPersistenceAndReconstructsSegments()
    {
        CustomModelDocument document = Document(out _);
        GeneratedBodyRigResult built = GeneratedBodyRig.Build(document);
        CustomModelDocument persisted = document with
        {
            Bones = built.Bones,
            RigSignature = CustomModelContractSignatures.ComputeRig(built.Bones),
            RiggingSession = built.Session,
        };

        Assert.True(GeneratedBodyRig.IsGenerated(persisted));
        Assert.Equal<GeneratedBodySegment>(built.Segments, GeneratedBodyRig.GetSegments(persisted));
        Assert.False(GeneratedBodyRig.IsGenerated(persisted with { RiggingSession = built.Session with { RequiresSourceReview = true } }));
    }

    [Fact]
    public void RejectsMissingDuplicateCollapsedAndStaleGuideInputs()
    {
        CustomModelDocument document = Document(out RiggingSession session);
        Guid removed = session.Landmarks.Single(landmark => landmark.RoleId == "hand.left").Id;
        RiggingSession missing = session with { Landmarks = session.Landmarks.Where(landmark => landmark.Id != removed).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => GeneratedBodyRig.Build(document with { RiggingSession = missing }));

        RigLandmark duplicate = session.Landmarks.Single(landmark => landmark.RoleId == "hand.right") with { Id = Guid.NewGuid(), RoleId = "hand.left" };
        RiggingSession duplicated = session with { Landmarks = session.Landmarks.Add(duplicate) };
        Assert.Throws<InvalidDataException>(() => GeneratedBodyRig.Build(document with { RiggingSession = duplicated }));

        RigLandmark collapsed = session.Landmarks.Single(landmark => landmark.RoleId == "hand.left") with { Position = session.Landmarks.Single(landmark => landmark.RoleId == "arm.left.lower").Position };
        RiggingSession collapsedSession = session with { Landmarks = session.Landmarks.Replace(session.Landmarks.Single(landmark => landmark.RoleId == "hand.left"), collapsed) };
        Assert.Throws<InvalidDataException>(() => GeneratedBodyRig.Build(document with { RiggingSession = collapsedSession }));

        RiggingSession stale = session with { RequiresSourceReview = true };
        Assert.Throws<InvalidOperationException>(() => GeneratedBodyRig.Build(document with { RiggingSession = stale }));
    }

    [Fact]
    public void RefusesExistingBonesAndNameCollisionsWithoutChangingSourceDocument()
    {
        CustomModelDocument document = Document(out RiggingSession session);
        CustomModelBone existing = new()
        {
            Index = 0,
            Name = "existing",
            ParentIndex = -1,
            LocalBindTransform = TransformTRS.Identity,
            ExactLocalBindMatrix = TransformMatrix.Identity,
            Kind = BoneKind.Root,
        };
        CustomModelDocument withBone = document with
        {
            Bones = [existing],
            RigSignature = CustomModelContractSignatures.ComputeRig([existing]),
        };
        Assert.Throws<InvalidOperationException>(() => GeneratedBodyRig.Build(withBone));

        RigEntityBinding collision = new()
        {
            EntityId = Guid.NewGuid(),
            OwnerAssetId = document.ModelId,
            SourceEntityId = "unrelated",
            NativeName = "body_pelvis",
            Kind = RigNativeEntityKind.Bone,
            Imported = false,
        };
        RiggingSession collidingSession = session with
        {
            Recipe = session.Recipe with { Entities = [collision] },
        };
        Assert.Throws<InvalidDataException>(() => GeneratedBodyRig.Build(document with { RiggingSession = collidingSession }));
        Assert.Empty(document.Bones);
        Assert.Equal(session.Landmarks, document.RiggingSession!.Landmarks);
    }

    [Fact]
    public void PreservesUnrelatedLockedGuidesAndRecipeDataWithoutApprovalClaims()
    {
        CustomModelDocument document = Document(out RiggingSession session);
        RigLandmark unrelated = new()
        {
            RoleId = "eye.left",
            Position = new(.1, 2.1, .2),
            Locked = true,
            UserApproved = true,
            Provenance = RigEvidenceKind.UserOverride,
        };
        RiggingSession withUnrelated = session with
        {
            Landmarks = session.Landmarks.Add(unrelated),
            MirroringEnabled = true,
        };
        GeneratedBodyRigResult result = GeneratedBodyRig.Build(document with { RiggingSession = withUnrelated });

        Assert.Equal(unrelated, result.Session.Landmarks.Single(landmark => landmark.Id == unrelated.Id));
        Assert.True(result.Session.MirroringEnabled);
        Assert.All(result.Session.Recipe.FramePolicies, policy =>
            Assert.DoesNotContain(
                policy.Evidence,
                evidence => evidence.Kind is RigEvidenceKind.CompiledReadBack or RigEvidenceKind.LoadedResourceCapture or RigEvidenceKind.LiveScenarioCapture));
        Assert.All(result.Session.Landmarks, landmark => Assert.False(
            result.Session.Recipe.Assignments.Any(assignment => assignment.EntityId == landmark.Id) && landmark.UserApproved));
    }

    private static CustomModelDocument Document(out RiggingSession session)
    {
        var model = AnatomicalDetectionWorkflowTests.CreateUnriggedModel();
        session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped) with
        {
            Landmarks = Landmarks(),
        };
        session.Validate();
        return model.Package.Document with { RiggingSession = session };
    }

    private static ImmutableArray<RigLandmark> Landmarks()
    {
        var positions = new Dictionary<string, Vector3D>(StringComparer.Ordinal)
        {
            ["body.pelvis"] = new(0, 0, 0),
            ["body.spine.0"] = new(0, .5, 0),
            ["body.spine.1"] = new(0, 1, 0),
            ["body.spine.2"] = new(0, 1.5, 0),
            ["body.neck.0"] = new(0, 2, 0),
            ["body.head"] = new(0, 2.5, 0),
            ["arm.left.upper"] = new(-.5, 1.5, 0),
            ["arm.left.lower"] = new(-1, 1.5, 0),
            ["hand.left"] = new(-1.5, 1.5, 0),
            ["arm.right.upper"] = new(.5, 1.5, 0),
            ["arm.right.lower"] = new(1, 1.5, 0),
            ["hand.right"] = new(1.5, 1.5, 0),
            ["leg.left.upper"] = new(-.25, -.5, 0),
            ["leg.left.lower"] = new(-.25, -1.25, 0),
            ["foot.left"] = new(-.25, -2, 0),
            ["leg.right.upper"] = new(.25, -.5, 0),
            ["leg.right.lower"] = new(.25, -1.25, 0),
            ["foot.right"] = new(.25, -2, 0),
        };
        return Roles.Select((role, index) => new RigLandmark
        {
            Id = new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            RoleId = role,
            Position = positions[role],
            Provenance = RigEvidenceKind.GeometryInference,
        }).ToImmutableArray();
    }
}
