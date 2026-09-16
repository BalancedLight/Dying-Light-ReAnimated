using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class GeneratedHandRigTests
{
    private static readonly string[] BodyRoles =
    [
        "body.pelvis", "body.spine.0", "body.spine.1", "body.spine.2", "body.neck.0", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left", "arm.right.upper", "arm.right.lower", "hand.right",
        "leg.left.upper", "leg.left.lower", "foot.left", "leg.right.upper", "leg.right.lower", "foot.right",
    ];

    [Fact]
    public void AppendsReviewedFingerBonesWithStableFramesAndTerminalTip()
    {
        CustomModelDocument document = GeneratedBodyDocument(out _);
        CustomModelDocument appended = GeneratedHandRig.Append(document, RigHandSide.Left);
        Assert.Equal(22, appended.Bones.Length);
        Assert.Equal<CustomModelBone>(document.Bones, appended.Bones.Take(document.Bones.Length));
        Assert.Equal(3, GeneratedBodyRig.GetSegments(appended).Count(segment => segment.RoleId.StartsWith("finger.left.index.", StringComparison.Ordinal)));
        Assert.DoesNotContain(appended.Bones, bone => bone.Name == "finger_left_index_4");
        Assert.All(appended.Bones.Skip(19), bone => Assert.Equal(BoneKind.Deform, bone.Kind));
        var guides = appended.RiggingSession!.Landmarks.ToDictionary(guide => guide.RoleId, StringComparer.Ordinal);
        foreach (GeneratedBodySegment segment in GeneratedBodyRig.GetSegments(appended).Where(segment => segment.RoleId.StartsWith("finger.left.index.", StringComparison.Ordinal)))
        {
            Assert.True(segment.Start.IsFinite && segment.End.IsFinite);
            Assert.NotEqual(segment.Start, segment.End);
        }
        Assert.Equal(guides["finger.left.index.4"].Position,
            GeneratedBodyRig.GetSegments(appended).Single(segment => segment.RoleId == "finger.left.index.3").End);
        Assert.True(GeneratedBodyRig.IsGenerated(appended));
        Assert.All(appended.RiggingSession.Recipe.Entities.Where(entity => entity.SourceEntityId?.StartsWith("generated-body:finger.left.", StringComparison.Ordinal) == true),
            entity => Assert.False(entity.Imported));
    }

    [Fact]
    public void AppendIsIdempotentAndSecondHandPreservesFirstHand()
    {
        CustomModelDocument baseDocument = GeneratedBodyDocument(out _);
        CustomModelDocument left = GeneratedHandRig.Append(baseDocument, RigHandSide.Left);
        CustomModelDocument same = GeneratedHandRig.Append(left, RigHandSide.Left);
        Assert.Same(left, same);
        CustomModelDocument both = GeneratedHandRig.Append(left, RigHandSide.Right);
        Assert.Equal<CustomModelBone>(left.Bones, both.Bones.Take(left.Bones.Length));
        Assert.Contains(both.Bones, bone => bone.Name == "finger_right_thumb_1");
        Assert.True(GeneratedBodyRig.IsGenerated(both));
    }

    [Fact]
    public void ShiftsHelperParentsThatPointIntoSeparateAuthoredLayer()
    {
        CustomModelDocument body = GeneratedHandRig.Append(GeneratedBodyDocument(out _), RigHandSide.Left);
        // The first helper is parented to the body hand bone; the second is
        // parented to the first helper and therefore must move after append.
        CustomModelDocument withParent = CustomModelHelperAuthoring.DuplicateAsHelper(body, 8, CustomModelAuthoredHelperKind.Helper, "generic_parent");
        CustomModelDocument withChild = CustomModelHelperAuthoring.DuplicateAsHelper(withParent, withParent.Bones.Length, CustomModelAuthoredHelperKind.Helper, "generic_child");
        CustomModelDocument appended = GeneratedHandRig.Append(withChild, RigHandSide.Right);
        Assert.Equal(withChild.AuthoredHelpers[0].ParentNodeIndex, appended.AuthoredHelpers[0].ParentNodeIndex);
        Assert.Equal(withChild.AuthoredHelpers[1].ParentNodeIndex + 1, appended.AuthoredHelpers[1].ParentNodeIndex);
        appended.Validate();
    }

    [Fact]
    public void RefusesUnresolvedLockedOrDifferingExistingExtension()
    {
        CustomModelDocument document = GeneratedBodyDocument(out _);
        RiggingSession baseSession = document.RiggingSession!;
        RigHandSetup unresolved = baseSession.Hands.Single(hand => hand.Side == RigHandSide.Left) with
        {
            Fingers = [baseSession.Hands.Single(hand => hand.Side == RigHandSide.Left).Fingers[0] with { Presence = RigFingerPresence.Unresolved, UserApproved = false }],
        };
        CustomModelDocument unresolvedDocument = document with
        {
            RiggingSession = baseSession with { Hands = [unresolved, baseSession.Hands.Single(hand => hand.Side == RigHandSide.Right)] },
        };
        Assert.Throws<InvalidOperationException>(() => GeneratedHandRig.Append(unresolvedDocument, RigHandSide.Left));

        RigHandSetup unapproved = baseSession.Hands.Single(hand => hand.Side == RigHandSide.Left) with { UserApproved = false };
        CustomModelDocument unapprovedDocument = document with
        {
            RiggingSession = baseSession with { Hands = [unapproved, baseSession.Hands.Single(hand => hand.Side == RigHandSide.Right)] },
        };
        Assert.Throws<InvalidOperationException>(() => GeneratedHandRig.Append(unapprovedDocument, RigHandSide.Left));

        CustomModelDocument appended = GeneratedHandRig.Append(document, RigHandSide.Left);
        RiggingSession appendedSession = appended.RiggingSession!;
        int guideIndex = IndexOfGuide(appendedSession, "finger.left.index.1");
        RiggingSession changedSession = appendedSession with
        {
            Landmarks = appendedSession.Landmarks.SetItem(guideIndex,
                appendedSession.Landmarks[guideIndex] with { Position = appendedSession.Landmarks[guideIndex].Position + Vector3D.UnitZ }),
        };
        Assert.Throws<InvalidOperationException>(() => GeneratedHandRig.Append(appended with { RiggingSession = changedSession }, RigHandSide.Left));
    }

    private static CustomModelDocument GeneratedBodyDocument(out RiggingSession session)
    {
        var model = GeneratedBodyWorkflowTests.Source();
        ImmutableArray<RigLandmark> bodyGuides = BodyRoles.Select((role, index) => new RigLandmark
        {
            Id = new Guid(index + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), RoleId = role,
            Position = BodyPosition(role), Provenance = RigEvidenceKind.GeometryInference,
        }).ToImmutableArray();
        var left = Finger("index", 100, -1.5);
        var leftAbsent = new RigFingerDeclaration { Id = "ring", Presence = RigFingerPresence.Absent };
        var right = Finger("thumb", 200, 1.5, guideCount: 2);
        var rightAbsent = new RigFingerDeclaration { Id = "index", Presence = RigFingerPresence.Absent };
        ImmutableArray<RigLandmark> fingerGuides = left.Guides.Concat(right.Guides).ToImmutableArray();
        var allGuides = bodyGuides.Concat(fingerGuides).ToImmutableArray();
        session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped) with
        {
            Landmarks = allGuides,
            Hands =
            [
                new RigHandSetup { Side = RigHandSide.Left, WristGuideId = allGuides.Single(guide => guide.RoleId == "hand.left").Id,
                    PalmFrame = TransformMatrix.Identity, UserApproved = true, Fingers = [left.Declaration, leftAbsent] },
                new RigHandSetup { Side = RigHandSide.Right, WristGuideId = allGuides.Single(guide => guide.RoleId == "hand.right").Id,
                    PalmFrame = TransformMatrix.Identity, UserApproved = true, Fingers = [right.Declaration, rightAbsent] },
            ],
        };
        session.Validate();
        var generated = ReAnimated.Codecs.Fbx.FbxGeneratedBodyBinding.Generate(model with
            { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } });
        session = generated.Package.Document.RiggingSession!;
        return generated.Package.Document;
    }

    private static (RigFingerDeclaration Declaration, ImmutableArray<RigLandmark> Guides) Finger(string id, int seed, double x, int guideCount = 4)
    {
        var guides = Enumerable.Range(1, guideCount).Select(segment => new RigLandmark
        {
            Id = new Guid(seed + segment, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            RoleId = $"finger.{(x < 0 ? "left" : "right")}.{id}.{segment}",
            Position = new(x + (x < 0 ? -.2 * segment : .2 * segment), 1.5, 0),
            Provenance = RigEvidenceKind.GeometryInference,
        }).ToImmutableArray();
        return (new RigFingerDeclaration { Id = id, Presence = RigFingerPresence.Present, JointGuideIds = guides.Select(guide => guide.Id).ToImmutableArray(), CurlPlaneNormal = Vector3D.UnitY, UserApproved = true }, guides);
    }

    private static Vector3D BodyPosition(string role) => role switch
    {
        "body.pelvis" => new(0, 0, 0), "body.spine.0" => new(0, .5, 0), "body.spine.1" => new(0, 1, 0),
        "body.spine.2" => new(0, 1.5, 0), "body.neck.0" => new(0, 2, 0), "body.head" => new(0, 2.5, 0),
        "arm.left.upper" => new(-.5, 1.5, 0), "arm.left.lower" => new(-1, 1.5, 0), "hand.left" => new(-1.5, 1.5, 0),
        "arm.right.upper" => new(.5, 1.5, 0), "arm.right.lower" => new(1, 1.5, 0), "hand.right" => new(1.5, 1.5, 0),
        "leg.left.upper" => new(-.25, -.5, 0), "leg.left.lower" => new(-.25, -1.25, 0), "foot.left" => new(-.25, -2, 0),
        "leg.right.upper" => new(.25, -.5, 0), "leg.right.lower" => new(.25, -1.25, 0), "foot.right" => new(.25, -2, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static int IndexOfGuide(RiggingSession session, string role) =>
        session.Landmarks.Select((guide, index) => (guide, index)).Single(row => row.guide.RoleId == role).index;
}
