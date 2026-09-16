using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigHandSetupTests : IDisposable
{
    private static readonly Guid Wrist = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Index1 = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Index2 = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid RightWrist = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void HandSetupRoundTripsWithGuideAndEvidenceIdentity()
    {
        CustomModelDocument document = DocumentWithHands(out RiggingSession session);
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "hand-setup.fbx");
        string path = Path.Combine(_directory, "hand-setup.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(imported.Package with { Document = document }, path);

        CustomModelDocument reopened = CustomModelPackageSerializer.Load(path).Document;
        RigHandSetup hand = Assert.Single(reopened.RiggingSession!.Hands);
        Assert.Equal(RigHandSide.Left, hand.Side);
        Assert.Equal(Wrist, hand.WristGuideId);
        Assert.True(hand.PalmFrame.NearlyEquals(session.Hands[0].PalmFrame, 1e-12));
        RigFingerDeclaration finger = Assert.Single(hand.Fingers);
        Assert.Equal("index", finger.Id);
        Assert.Equal(RigFingerPresence.Present, finger.Presence);
        Assert.Equal<Guid>([Index1, Index2], finger.JointGuideIds);
        Assert.Equal(RigEvidenceKind.GeometryInference, Assert.Single(hand.Evidence).Kind);
    }

    [Fact]
    public void HandSetupParticipatesInFingerprintAndAnatomyInvalidation()
    {
        _ = DocumentWithHands(out RiggingSession session);
        string before = session.ComputeInputFingerprint();
        // Keep the test independent of UI fields: changing the persisted palm
        // frame is an authoring input and must invalidate the same stage.
        RiggingSession edited = session with
        {
            Hands = [session.Hands[0] with
            {
                PalmFrame = session.Hands[0].PalmFrame with { M13 = .15 },
            }],
        };
        Assert.NotEqual(before, edited.ComputeInputFingerprint());
        RiggingSession reviewed = RiggingSessions.RecordReview(session, RigStudioStage.Fit,
            session.ComputeInputFingerprint(), DateTimeOffset.UtcNow);
        RiggingSession changed = RiggingSessions.Change(reviewed, edited, RiggingEditKind.Anatomy);
        Assert.Null(changed.Stages.Single(state => state.Stage == RigStudioStage.Fit).ReviewedInputFingerprint);
        Assert.Equal(reviewed.Revision + 1, changed.Revision);
    }

    [Fact]
    public void SourceChangesInvalidateHandApprovalsButKeepGuidePositionsAndLocks()
    {
        RiggingSession session = ApprovedSession(out _);
        string source = new string('a', 64);
        RiggingSession changed = RiggingSessions.ReconcileSource(session, source);
        Assert.False(changed.Hands[0].UserApproved);
        Assert.False(changed.Hands[0].Fingers[0].UserApproved);
        Assert.Equal(session.Landmarks, changed.Landmarks);
        Assert.Equal(session.Landmarks.Where(guide => guide.Locked).Select(guide => guide.Id),
            changed.Landmarks.Where(guide => guide.Locked).Select(guide => guide.Id));
        Assert.True(changed.RequiresSourceReview);
    }

    [Fact]
    public void LandmarkChangesInvalidateHandApprovalsWhileLocksRemainPreserved()
    {
        RiggingSession session = ApprovedSession(out _);
        int editableIndex = IndexOfGuide(session, Index2);
        RiggingSession candidate = session with
        {
            Landmarks = session.Landmarks.SetItem(editableIndex,
                session.Landmarks[editableIndex] with { Position = session.Landmarks[editableIndex].Position + Vector3D.UnitX }),
        };
        RiggingSession changed = RiggingSessions.Change(session, candidate, RiggingEditKind.Anatomy);
        Assert.False(changed.Hands[0].UserApproved);
        Assert.False(changed.Hands[0].Fingers[0].UserApproved);
        Assert.True(changed.Landmarks.Single(guide => guide.Id == Wrist).Locked);
    }

    [Fact]
    public void LandmarkAcceptanceRetainsHandGuidesAndRejectsDanglingDeclarations()
    {
        RiggingSession session = ApprovedSession(out _);
        Assert.True(RiggingSessions.TryAcceptLandmarks(session, session.CreateJobToken(), [], out RiggingSession retained));
        Assert.Equal(session.Landmarks.Where(g => g.Id == Wrist || g.Id == Index1 || g.Id == Index2), retained.Landmarks);
        RiggingSession dangling = session with
        {
            Landmarks = session.Landmarks.RemoveAt(IndexOfGuide(session, Index2)),
        };
        Assert.Throws<ArgumentException>(() => dangling.Validate());
    }

    [Fact]
    public void StandaloneHandValidationChecksMalformedFingerDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new RigHandSetup
        {
            Side = RigHandSide.Left,
            WristGuideId = Wrist,
            Fingers = [new RigFingerDeclaration { Id = "index", Presence = RigFingerPresence.Present, JointGuideIds = [Index1] }],
        }.Validate());
    }

    [Fact]
    public void PresentDigitRequiresContiguousExistingGuidesAndAbsenceCannotInventThem()
    {
        CustomModelDocument valid = DocumentWithHands(out _);
        var landmarks = valid.RiggingSession!.Landmarks.ToBuilder();
        int removedIndex = 0;
        while (landmarks[removedIndex].Id != Index2) removedIndex++;
        landmarks.RemoveAt(removedIndex);
        RigHandSetup missing = valid.RiggingSession.Hands[0] with
        {
            Fingers = [valid.RiggingSession.Hands[0].Fingers[0] with { JointGuideIds = [Index1] }],
        };
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid, landmarks.ToImmutable(), [missing]).Validate());

        RigHandSetup absent = valid.RiggingSession.Hands[0] with
        {
            Fingers = [new RigFingerDeclaration { Id = "index", Presence = RigFingerPresence.Absent, JointGuideIds = [] }],
        };
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid, valid.RiggingSession.Landmarks, [absent with { Fingers = [new RigFingerDeclaration {
            Id = "index", Presence = RigFingerPresence.Absent, JointGuideIds = [Index1] }] }]).Validate());
    }

    [Fact]
    public void RejectsInvalidLinksDuplicateSidesAndInvalidFrames()
    {
        CustomModelDocument valid = DocumentWithHands(out _);
        RigHandSetup hand = valid.RiggingSession!.Hands[0];
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid,
            valid.RiggingSession.Landmarks, [hand with { WristGuideId = Guid.NewGuid() }]).Validate());
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid,
            valid.RiggingSession.Landmarks, [hand with { Fingers = [hand.Fingers[0] with { JointGuideIds = [Wrist, Index2] }] }]).Validate());
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid,
            valid.RiggingSession.Landmarks, [hand with { PalmFrame = TransformMatrix.CreateScale(new(-1, 1, 1)) }]).Validate());
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid,
            valid.RiggingSession.Landmarks, [hand with { Fingers = [hand.Fingers[0], hand.Fingers[0] with { Presence = RigFingerPresence.Unresolved, JointGuideIds = [] }] }]).Validate());
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid,
            valid.RiggingSession.Landmarks, [hand, hand]).Validate());
        Assert.Throws<ArgumentException>(() => DocumentWithSession(valid,
            valid.RiggingSession.Landmarks, [hand with { Fingers = [hand.Fingers[0] with { CurlPlaneNormal = Vector3D.Zero }] }]).Validate());
    }

    [Fact]
    public void ExistingSessionWithoutHandsRetainsEmptySchemaSevenDefault()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "hand-free.fbx");
        CustomModelDocument document = imported.Package.Document with
        {
            RiggingSession = RiggingSessions.Create(imported.Package.Document, RigStudioEntryPath.RepairExistingRig),
        };
        document.Validate();
        Assert.Empty(document.RiggingSession!.Hands);
    }

    private static CustomModelDocument DocumentWithHands(out RiggingSession session)
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "hand-setup.fbx");
        CustomModelDocument document = imported.Package.Document;
        session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        ImmutableArray<RigLandmark> landmarks =
        [
            new() { Id = Wrist, RoleId = "hand.left", Position = new(-.2, 1, 0) },
            new() { Id = RightWrist, RoleId = "hand.right", Position = new(.2, 1, 0) },
            new() { Id = Index1, RoleId = "finger.left.index.1", Position = new(-.25, 1, -.02) },
            new() { Id = Index2, RoleId = "finger.left.index.2", Position = new(-.3, 1, -.04) },
        ];
        var evidence = new RigEvidenceReference
        {
            Id = "synthetic-hand-evidence",
            Kind = RigEvidenceKind.GeometryInference,
            ArtifactSha256 = document.Source.ContentSha256,
            Description = "Synthetic hand setup evidence for a generic test fixture.",
        };
        RigHandSetup hand = new()
        {
            Side = RigHandSide.Left,
            WristGuideId = Wrist,
            PalmFrame = TransformMatrix.Identity with { M12 = .25, M14 = -.2 },
            UserApproved = false,
            Fingers =
            [
                new RigFingerDeclaration
                {
                    Id = "index",
                    Presence = RigFingerPresence.Present,
                    JointGuideIds = [Index1, Index2],
                    CurlPlaneNormal = Vector3D.UnitY,
                    RollDegrees = 12.5,
                    Evidence = [evidence],
                },
            ],
            Evidence = [evidence],
        };
        session = session with { Landmarks = landmarks, Hands = [hand] };
        return document with { RiggingSession = session };
    }

    private static RiggingSession ApprovedSession(out CustomModelDocument document)
    {
        document = DocumentWithHands(out RiggingSession session);
        ImmutableArray<RigLandmark> guides = session.Landmarks.Select(guide => guide.Id == Wrist || guide.Id == Index1
            ? guide with { Locked = true }
            : guide).ToImmutableArray();
        RigFingerDeclaration finger = session.Hands[0].Fingers[0] with { UserApproved = true };
        session = session with
        {
            Landmarks = guides,
            Hands = [session.Hands[0] with { UserApproved = true, Fingers = [finger] }],
        };
        session.Validate();
        document = document with { RiggingSession = session };
        return session;
    }

    private static CustomModelDocument DocumentWithSession(CustomModelDocument source,
        ImmutableArray<RigLandmark> landmarks, ImmutableArray<RigHandSetup> hands) =>
        source with { RiggingSession = source.RiggingSession! with { Landmarks = landmarks, Hands = hands } };

    private static int IndexOfGuide(RiggingSession session, Guid id)
    {
        for (int index = 0; index < session.Landmarks.Length; index++)
            if (session.Landmarks[index].Id == id) return index;
        return -1;
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}
