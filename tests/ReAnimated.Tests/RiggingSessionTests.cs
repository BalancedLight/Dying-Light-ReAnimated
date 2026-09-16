using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RiggingSessionTests
{
    private static CustomModelPackage Package() => FbxModelAuthoringImporter.Import(
        BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx").Package;
    private static RiggingSession Session() => RiggingSessions.Create(Package().Document, RigStudioEntryPath.RepairExistingRig);
    private static CustomModelPackage Load(byte[] bytes)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic.dlrmodel");
            File.WriteAllBytes(path, bytes);
            return CustomModelPackageSerializer.Load(path);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    private static RigLandmark Guide(bool locked = false) => new()
    {
        RoleId = "body.elbow.left", Position = new Vector3D(.2, 1.2, .1), Locked = locked,
    };

    [Fact]
    public void NewSessionPreservesIntentAndDoesNotInventNativeReadiness()
    {
        CustomModelPackage source = Package();
        var session = RiggingSessions.Create(source.Document, RigStudioEntryPath.AdaptExistingRig);
        Assert.Equal(RigAnatomyPolicy.Preserve, session.Recipe.ScalePolicy.Anatomy);
        Assert.Equal(RigMotionStrategy.PreserveAnatomyMapped, session.Recipe.MotionStrategy);
        Assert.Equal(1, session.Recipe.ScalePolicy.RuntimeUniformBodyScale);
        Assert.Null(session.Recipe.ScalePolicy.SourceToAuthoring);
        Assert.Null(session.Recipe.Profile);
        Assert.Empty(session.ValidationHistory);
        Assert.Equal(7, session.Stages.Length);
        Assert.All(session.Stages, s => Assert.Null(s.ReviewedInputFingerprint));
        Assert.Equal(source.Document.Bones.Length, session.Recipe.Entities.Length);
        Assert.All(session.Recipe.Entities, e => Assert.Equal(RigNativeEntityKind.Unknown, e.Kind));
        Assert.Null(source.Document.RiggingSession);
    }

    [Fact]
    public void ImportedSemanticIdentitiesDoNotDependOnPhysicalRowOrder()
    {
        CustomModelPackage source = Package();
        var first = RiggingSessions.Create(source.Document, RigStudioEntryPath.RepairExistingRig);
        var second = RiggingSessions.Create(source.Document, RigStudioEntryPath.RepairExistingRig);
        Assert.Equal(first.Recipe.Entities.Select(e => e.EntityId), second.Recipe.Entities.Select(e => e.EntityId));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void SourceHierarchyObservationUsesActualBoneParentsAndStableIdentity()
    {
        CustomModelDocument document = Package().Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        var result = RiggingSessions.ObserveSourceHierarchy(document with { RiggingSession = session });
        Assert.Equal(document.Bones.Length, result.Length);
        foreach (CustomModelBone bone in document.Bones)
        {
            Assert.Equal(session.Recipe.Entities[bone.Index].EntityId, result[bone.Index].EntityId);
            Assert.Equal(bone.ParentIndex < 0 ? (Guid?)null : session.Recipe.Entities[bone.ParentIndex].EntityId, result[bone.Index].ParentEntityId);
        }
        var reordered = session with { Recipe = session.Recipe with { Entities = session.Recipe.Entities.Reverse().ToImmutableArray() } };
        Assert.Equal<RigParentObservation>(result, RiggingSessions.ObserveSourceHierarchy(document with { RiggingSession = reordered }));
        var stale = session with { SourceSha256 = new string('a', 64) };
        Assert.Throws<InvalidOperationException>(() => RiggingSessions.ObserveSourceHierarchy(document with { RiggingSession = stale }));
        Assert.Throws<InvalidOperationException>(() => RiggingSessions.ObserveSourceHierarchy(document with { RiggingSession = session with { RequiresSourceReview = true } }));
    }

    [Fact]
    public void FramePoliciesRoundTripWithBoundsAndProvenance()
    {
        CustomModelPackage package = Package();
        RiggingSession session = RiggingSessions.Create(package.Document, RigStudioEntryPath.RepairExistingRig);
        var policy = new RigEntityFramePolicy
        {
            EntityId = session.Recipe.Entities[0].EntityId, FramePolicy = RigFramePolicy.Structural,
            BoundsPolicy = RigBoundsPolicy.PreserveSource, BoundsCenter = new(.01, -.02, .03), BoundsHalfExtents = new(.04, .02, .03),
            SolvedGlobalFrame = TransformMatrix.Identity,
            Evidence = [new() { Id = "synthetic-reviewed-frame", Kind = RigEvidenceKind.UserOverride, Description = "Reviewed synthetic control." }],
        };
        session = session with { Recipe = session.Recipe with { FramePolicies = [policy] } };
        package = package with { Document = package.Document with { RiggingSession = session } };
        CustomModelPackage restored = Load(CustomModelPackageSerializer.Serialize(package).ToArray());
        RigEntityFramePolicy actual = Assert.Single(restored.Document.RiggingSession!.Recipe.FramePolicies);
        Assert.Equal(policy.EntityId, actual.EntityId);
        Assert.Equal(policy.SolvedGlobalFrame, actual.SolvedGlobalFrame);
        Assert.Equal(policy.BoundsCenter, actual.BoundsCenter);
        Assert.Equal(policy.BoundsHalfExtents, actual.BoundsHalfExtents);
        Assert.Equal(policy.BoundsPolicy, actual.BoundsPolicy);
        Assert.Equal(policy.Evidence[0], Assert.Single(actual.Evidence));
        Assert.Equal(session.ComputeInputFingerprint(), restored.Document.RiggingSession.ComputeInputFingerprint());
    }

    [Fact]
    public void StaticSourceReachesItsDistinctEntryWithoutPretendingItHasABind()
    {
        var staticDocument = Package().Document with { Bones = [], RigMode = CustomModelRigMode.StaticProp };
        var session = RiggingSessions.Create(staticDocument, RigStudioEntryPath.AutoRigBiped);
        Assert.Empty(session.Recipe.Entities);
        Assert.Empty(session.ValidationHistory);
        Assert.Throws<ArgumentException>(() => RiggingSessions.Create(staticDocument, RigStudioEntryPath.RepairExistingRig));
        Assert.Throws<ArgumentException>(() => RiggingSessions.Create(Package().Document, RigStudioEntryPath.AutoRigBiped));
    }

    [Fact]
    public void GeometryEditsInvalidateDownstreamReviewsButKeepImportReview()
    {
        var session = Session();
        foreach (RigStudioStage stage in Enum.GetValues<RigStudioStage>())
            session = RiggingSessions.RecordReview(session, stage, session.ComputeInputFingerprint(), DateTimeOffset.UnixEpoch);
        var changed = RiggingSessions.Change(session, session with { Landmarks = [Guide()] }, RiggingEditKind.Anatomy);
        Assert.NotNull(changed.Stages.Single(s => s.Stage == RigStudioStage.Import).ReviewedInputFingerprint);
        Assert.NotNull(changed.Stages.Single(s => s.Stage == RigStudioStage.Detect).ReviewedInputFingerprint);
        Assert.All(changed.Stages.Where(s => s.Stage >= RigStudioStage.Fit), s => Assert.Null(s.ReviewedInputFingerprint));
        Assert.Empty(changed.ValidationHistory);
    }

    [Fact]
    public void AnEditLabelCannotHideChangedSourceInputs()
    {
        var session = Session();
        session = RiggingSessions.RecordReview(session, RigStudioStage.Import, session.ComputeInputFingerprint(), DateTimeOffset.UnixEpoch);
        var changed = RiggingSessions.Change(session, session with { SourceSha256 = RiggingValidationTests.Hash('0') }, RiggingEditKind.Verification);
        Assert.All(changed.Stages, s => Assert.Null(s.ReviewedInputFingerprint));
    }

    [Fact]
    public void LockedGuidesSurviveDetectorResultsAndRequireExplicitUnlock()
    {
        var guide = Guide(true);
        var session = Session() with { Landmarks = [guide] };
        Assert.Throws<InvalidOperationException>(() => RiggingSessions.Change(session,
            session with { Landmarks = [guide with { Position = Vector3D.Zero }] }, RiggingEditKind.Anatomy));
        Assert.True(RiggingSessions.TryAcceptLandmarks(session, session.CreateJobToken(),
            [guide with { Id = Guid.NewGuid(), Position = Vector3D.Zero, Locked = false }], out var accepted));
        Assert.Equal(guide, Assert.Single(accepted.Landmarks));
        var unlocked = RiggingSessions.Change(session, session with { Landmarks = [guide with { Locked = false }] }, RiggingEditKind.Anatomy, true);
        Assert.False(unlocked.Landmarks[0].Locked);
    }

    [Fact]
    public void StaleAndUndoAbaJobsCannotOverwriteNewerDecisions()
    {
        var original = Session();
        var token = original.CreateJobToken();
        var changed = RiggingSessions.Change(original, original with { Landmarks = [Guide()] }, RiggingEditKind.Anatomy);
        Assert.False(RiggingSessions.TryAcceptLandmarks(changed, token, [], out var unchanged));
        Assert.Same(changed, unchanged);
        var undone = RiggingSessions.RestoreForUndo(changed, original);
        Assert.Equal(original.ComputeInputFingerprint(), undone.ComputeInputFingerprint());
        Assert.False(undone.Matches(token));
        Assert.True(undone.Revision > changed.Revision);
    }

    [Fact]
    public void SourceChangesKeepLocksAndHistoricalEvidenceButRequireReview()
    {
        var session = Session() with { Landmarks = [Guide(true)] };
        session = RiggingSessions.RecordValidation(session, RiggingValidationTests.Passed(RigValidationFacet.GeometryAndBind));
        var changed = RiggingSessions.ReconcileSource(session, RiggingValidationTests.Hash('0'));
        Assert.True(changed.RequiresSourceReview);
        Assert.False(changed.MatchesSource(changed.SourceSha256));
        Assert.Equal(session.SourceSha256, changed.PreviousSourceSha256);
        Assert.Equal(session.Landmarks, changed.Landmarks);
        Assert.Equal(session.ValidationHistory, changed.ValidationHistory);
        var reviewed = RiggingSessions.RecordReview(changed, RigStudioStage.Import, changed.ComputeInputFingerprint(), DateTimeOffset.UnixEpoch);
        Assert.True(reviewed.MatchesSource(changed.SourceSha256));
    }

    [Fact]
    public void ModelPackageAndActualFbxReimportRetainTheStudioSession()
    {
        var original = Package();
        var session = RiggingSessions.Create(original.Document, RigStudioEntryPath.RepairExistingRig) with { Landmarks = [Guide(true)] };
        var package = original with { Document = original.Document with { RiggingSession = session } };
        byte[] first = CustomModelPackageSerializer.Serialize(package).ToArray();
        var restored = Load(first);
        Assert.Equal(first, CustomModelPackageSerializer.Serialize(restored).ToArray());
        Assert.Equal(session.ComputeInputFingerprint(), restored.Document.RiggingSession!.ComputeInputFingerprint());
        Assert.True(original.SourceFbx.AsSpan().SequenceEqual(restored.SourceFbx.AsSpan()));
        var imported = FbxModelAuthoringImporter.ImportPackage(restored);
        Assert.Equal(session.ComputeInputFingerprint(), imported.Package.Document.RiggingSession!.ComputeInputFingerprint());
        var reimport = FbxModelAuthoringImporter.PreviewReimport(restored, original.SourceFbx.AsSpan(), "generic.fbx");
        Assert.Equal(session.ComputeInputFingerprint(), reimport.Replacement.Package.Document.RiggingSession!.ComputeInputFingerprint());
    }

    [Fact]
    public void SchemaFiveMigrationPreservesLegacyMeaningAndDoesNotOptIntoStudio()
    {
        var package = Package();
        var stored = package with { Document = package.Document with
        {
            BuildSettings = package.Document.BuildSettings with { ReferenceExistingAnimationLibrary = true, AnimationScriptAlias = "stock_bank" },
            FacialPresets = new() { Presets = [new() { Name = "Neutral" }] },
        } };
        using var stream = new MemoryStream();
        stream.Write(CustomModelPackageSerializer.Serialize(stored).AsSpan());
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("model.json")!;
            JsonObject json;
            using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            json["schemaVersion"] = 5;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("model.json").Open());
            writer.Write(json.ToJsonString());
        }
        var loaded = Load(stream.ToArray());
        Assert.Equal(CustomModelDocument.CurrentSchemaVersion, loaded.Document.SchemaVersion);
        Assert.Null(loaded.Document.RiggingSession);
        Assert.True(loaded.Document.BuildSettings.ReferenceExistingAnimationLibrary);
        Assert.Equal("Neutral", Assert.Single(loaded.Document.FacialPresets.Presets).Name);
        Assert.True(package.SourceFbx.AsSpan().SequenceEqual(loaded.SourceFbx.AsSpan()));
    }

    [Fact]
    public void SessionCannotBeAssignedToAnotherModelOrHideMissingStages()
    {
        var package = Package();
        var session = RiggingSessions.Create(package.Document, RigStudioEntryPath.RepairExistingRig);
        Assert.Throws<ArgumentException>(() => (package.Document with { RiggingSession = session with { OwnerModelId = Guid.NewGuid() } }).Validate());
        Assert.Throws<ArgumentException>(() => (session with { Stages = session.Stages.RemoveAt(0) }).Validate());
        Assert.Throws<ArgumentException>(() => (session with { SymmetryNormal = Vector3D.Zero }).Validate());
    }
}
