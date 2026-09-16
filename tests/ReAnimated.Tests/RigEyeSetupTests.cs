using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigEyeSetupTests
{
    private static readonly Guid SourceEye = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Parent = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Helper = Guid.Parse("20000000-0000-0000-0000-000000000003");
    private static readonly Guid Owner = Guid.Parse("20000000-0000-0000-0000-000000000004");

    [Fact]
    public void EyeSetupsRoundTripAndParticipateInFingerprint()
    {
        RiggingSession session = SessionWithEyes();
        string before = session.ComputeInputFingerprint();
        string json = JsonSerializer.Serialize(session);
        RiggingSession restored = JsonSerializer.Deserialize<RiggingSession>(json)!;

        restored.Validate();
        Assert.Equal(before, restored.ComputeInputFingerprint());
        Assert.Equal(session.Eyes.Length, restored.Eyes.Length);
        for (int index = 0; index < session.Eyes.Length; index++)
        {
            RigEyeSetup expected = session.Eyes[index];
            RigEyeSetup actual = restored.Eyes[index];
            Assert.Equal(expected.Side, actual.Side);
            Assert.Equal(expected.Mode, actual.Mode);
            Assert.Equal(expected.GeometryKind, actual.GeometryKind);
            Assert.Equal(expected.SourceEntityId, actual.SourceEntityId);
            Assert.Equal(expected.ParentEntityId, actual.ParentEntityId);
            Assert.Equal(expected.HelperEntityId, actual.HelperEntityId);
            Assert.Equal(expected.DeformEntityId, actual.DeformEntityId);
            Assert.Equal(expected.GlobalFrame, actual.GlobalFrame);
            Assert.Equal(expected.ComponentId, actual.ComponentId);
            Assert.Equal(expected.IslandIndex, actual.IslandIndex);
            Assert.Equal<int>(expected.SourceControlPointIds, actual.SourceControlPointIds);
            Assert.Equal(expected.GlobeRadius, actual.GlobeRadius);
            Assert.Equal<uint>(expected.MorphDescriptors, actual.MorphDescriptors);
            Assert.Equal(expected.UserApproved, actual.UserApproved);
            Assert.Equal<RigEvidenceReference>(expected.Evidence, actual.Evidence);
        }
        Assert.Equal(RigEyeSetupMode.SourceEye, restored.Eyes[0].Mode);
        Assert.Equal(RigEyeSetupMode.GeometryPivot, restored.Eyes[1].Mode);

        RiggingSession differentSide = session with { Eyes = [session.Eyes[0] with { Side = RigEyeSide.Right }] };
        RiggingSession differentMode = session with { Eyes = [session.Eyes[0] with { Mode = RigEyeSetupMode.GazeReference }] };
        Assert.NotEqual(before, differentSide.ComputeInputFingerprint());
        Assert.NotEqual(before, differentMode.ComputeInputFingerprint());
    }

    [Fact]
    public void LegacySessionJsonDefaultsEyesToEmpty()
    {
        RiggingSession restored = JsonSerializer.Deserialize<RiggingSession>("{}")!;
        Assert.Empty(restored.Eyes);
    }

    [Fact]
    public void SourceReconciliationClearsEyeApprovalButRetainsPlacementEvidence()
    {
        RiggingSession session = SessionWithEyes() with
        {
            Eyes = SessionWithEyes().Eyes.Select(static eye => eye with { UserApproved = true }).ToImmutableArray(),
        };
        TransformMatrix frame = session.Eyes[1].GlobalFrame;
        RiggingSession changed = RiggingSessions.ReconcileSource(session, Hash('b'));

        Assert.All(changed.Eyes, static eye => Assert.False(eye.UserApproved));
        Assert.Equal(frame, changed.Eyes[1].GlobalFrame);
        Assert.Equal(session.Eyes[1].SourceControlPointIds, changed.Eyes[1].SourceControlPointIds);
        Assert.Equal(session.Eyes[1].Evidence, changed.Eyes[1].Evidence);
        Assert.True(changed.RequiresSourceReview);
    }

    [Fact]
    public void PaintedGazeReferenceIsAllowedButCannotClaimAComputedGlobe()
    {
        RigEyeSetup painted = new()
        {
            Side = RigEyeSide.Shared,
            Mode = RigEyeSetupMode.GazeReference,
            GeometryKind = RigEyeGeometryKind.Painted,
            GlobalFrame = TransformMatrix.CreateTranslation(new(.1, .2, .3)),
        };
        painted.Validate();

        new RigEyeSetup
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.SourceEye,
            GeometryKind = RigEyeGeometryKind.Painted,
            SourceEntityId = SourceEye,
            GlobalFrame = TransformMatrix.CreateScale(new(-1, 1, 1)),
        }.Validate();

        Assert.Throws<ArgumentException>(() => (painted with { GlobeRadius = .25 }).Validate());
        Assert.Throws<ArgumentException>(() => (painted with { Mode = RigEyeSetupMode.GeometryPivot }).Validate());
    }

    [Fact]
    public void MimicStoresOnlyDescriptorInventoryAndSourceEyeNeedsAnEntity()
    {
        new RigEyeSetup
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.SourceEye,
            SourceEntityId = SourceEye,
            GlobalFrame = TransformMatrix.CreateScale(new(-1, 1, 1)),
        }.Validate();

        new RigEyeSetup
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.Mimic,
            MorphDescriptors = [17u, 31u],
        }.Validate();

        Assert.Throws<ArgumentException>(() => new RigEyeSetup
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.Mimic,
            HelperEntityId = Helper,
        }.Validate());
        Assert.Throws<ArgumentException>(() => new RigEyeSetup
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.SourceEye,
        }.Validate());
    }

    [Fact]
    public void SessionRejectsDuplicateModesDanglingReferencesAndInvalidGeometry()
    {
        RiggingSession session = SessionWithEyes();
        Assert.Throws<ArgumentException>(() => (session with { Eyes = [session.Eyes[0], session.Eyes[0] with { SourceEntityId = Parent }] }).Validate());
        Assert.Throws<ArgumentException>(() => (session with { Eyes = [session.Eyes[0] with { SourceEntityId = Guid.NewGuid() }] }).Validate());
        Assert.Throws<ArgumentException>(() => (session with { Eyes = [session.Eyes[1] with { GlobalFrame = TransformMatrix.CreateScale(new(-1, 1, 1)) }] }).Validate());
        Assert.Throws<ArgumentException>(() => (session with { Eyes = [session.Eyes[1] with { SourceControlPointIds = [1, 1] }] }).Validate());
        Assert.Throws<ArgumentException>(() => (session with { Eyes = [session.Eyes[1] with { ComponentId = "missing-component" }] }).Validate());
    }

    [Fact]
    public void GlobeCandidateRequiresSupportAndEvidence()
    {
        RigEyeSetup candidate = SessionWithEyes().Eyes[1];
        Assert.Throws<ArgumentException>(() => (candidate with { SourceControlPointIds = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (candidate with { Evidence = [] }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (candidate with { GlobeRadius = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (candidate with { IslandIndex = -1 }).Validate());
    }

    private static RiggingSession SessionWithEyes()
    {
        var evidence = new RigEvidenceReference
        {
            Id = "synthetic-eye-evidence",
            Kind = RigEvidenceKind.GeometryInference,
            Description = "Generic synthetic eye setup evidence.",
        };
        var entities = ImmutableArray.Create(
            new RigEntityBinding
            {
                EntityId = SourceEye, OwnerAssetId = Owner, SourceEntityId = "source-eye",
                NativeName = "source_eye", Kind = RigNativeEntityKind.Bone,
            },
            new RigEntityBinding
            {
                EntityId = Parent, OwnerAssetId = Owner, SourceEntityId = "source-parent",
                NativeName = "source_parent", Kind = RigNativeEntityKind.Bone,
            },
            new RigEntityBinding
            {
                EntityId = Helper, OwnerAssetId = Owner, SourceEntityId = "authored-helper",
                NativeName = "eye_helper", Kind = RigNativeEntityKind.Helper, Imported = false,
            });
        return new RiggingSession
        {
            OwnerModelId = Owner,
            SourceSha256 = Hash('a'),
            EntryPath = RigStudioEntryPath.RepairExistingRig,
            Components = [new RigGeometryComponent
            {
                Id = "component:eye",
                DisplayName = "Synthetic eye component",
                Kind = RigGeometryComponentKind.Body,
            }],
            Recipe = new RuntimeRigRecipe { Entities = entities },
            Eyes =
            [
                new RigEyeSetup
                {
                    Side = RigEyeSide.Left,
                    Mode = RigEyeSetupMode.SourceEye,
                    SourceEntityId = SourceEye,
                    ParentEntityId = Parent,
                    MorphDescriptors = [17u],
                    Evidence = [evidence],
                },
                new RigEyeSetup
                {
                    Side = RigEyeSide.Right,
                    Mode = RigEyeSetupMode.GeometryPivot,
                    GeometryKind = RigEyeGeometryKind.GlobeCandidate,
                    ParentEntityId = Parent,
                    HelperEntityId = Helper,
                    GlobalFrame = TransformMatrix.CreateTranslation(new(.5, 1.2, .1)),
                    ComponentId = "component:eye",
                    IslandIndex = 3,
                    SourceControlPointIds = [12, 18, 20],
                    GlobeRadius = .22,
                    Evidence = [evidence],
                },
            ],
        };
    }

    private static string Hash(char value) => new(value, 64);
}
