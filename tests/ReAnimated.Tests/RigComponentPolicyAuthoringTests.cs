using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigComponentPolicyAuthoringTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(6, 4)]
    [InlineData(7, 4)]
    public void AppliesDefinedMasksAndLodsToAnObservedEntity(int maskValue, int lodValue)
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        RiggingJobToken token = session.CreateJobToken();

        bool applied = RigComponentPolicyAuthoring.TryApply(document, token,
            [new RigComponentPolicyEdit(entityId, (RigAnimationComponents)maskValue,
                (RigAnimationLod)lodValue, null, null, null)], out RiggingSession result);

        Assert.True(applied);
        AnimationComponentPolicy policy = Assert.Single(result.Recipe.ComponentPolicies);
        Assert.Equal((RigAnimationComponents)maskValue, policy.EmittedMask!.Value);
        Assert.Equal((RigAnimationLod)lodValue, policy.AnimationLod!.Value);
    }

    [Fact]
    public void ChangedChannelsAndLodUseOnlySourceBackedAuthoringEvidence()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        RiggingJobToken token = session.CreateJobToken();

        Assert.True(RigComponentPolicyAuthoring.TryApply(document, token,
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.Position | RigAnimationComponents.Rotation,
                RigAnimationLod.Lod2, RigComponentOwner.Attachment, RigComponentOwner.Procedural,
                RigComponentOwner.BindInherited)], out RiggingSession result));

        AnimationComponentPolicy policy = Assert.Single(result.Recipe.ComponentPolicies);
        Assert.Equal(RigComponentOwner.Attachment, Assert.Single(policy.Position.Owners));
        Assert.Equal(RigComponentOwner.Procedural, Assert.Single(policy.Rotation.Owners));
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(policy.Scale.Owners));
        Assert.Equal("authoring-lod-selection-v1", policy.LodRuleId);
        AssertAuthoringEvidence(policy.Position.Evidence, document.Source.ContentSha256, entityId, "position");
        AssertAuthoringEvidence(policy.Rotation.Evidence, document.Source.ContentSha256, entityId, "rotation");
        AssertAuthoringEvidence(policy.Scale.Evidence, document.Source.ContentSha256, entityId, "scale");
        AssertAuthoringEvidence(policy.LodEvidence, document.Source.ContentSha256, entityId, "lod");
        Assert.Equal(session.Revision + 1, result.Revision);
    }

    [Fact]
    public void NullOwnersPreserveMixedChannelsAndUnchangedLodExactly()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        AnimationComponentPolicy original = Assert.Single(session.Recipe.ComponentPolicies);
        RiggingJobToken token = session.CreateJobToken();

        Assert.True(RigComponentPolicyAuthoring.TryApply(document, token,
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.Position,
                original.AnimationLod!.Value, RigComponentOwner.Clip, null, null)], out RiggingSession result));

        AnimationComponentPolicy updated = Assert.Single(result.Recipe.ComponentPolicies);
        Assert.Equal(original.Rotation, updated.Rotation);
        Assert.Equal(original.Scale, updated.Scale);
        Assert.Equal(original.LodRuleId, updated.LodRuleId);
        Assert.Equal(original.LodEvidence, updated.LodEvidence);
    }

    [Fact]
    public void ExplicitSameLodRepairsIncompleteLodDecisionEvidence()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        AnimationComponentPolicy original = Assert.Single(session.Recipe.ComponentPolicies);
        session = session with
        {
            Recipe = session.Recipe with
            {
                ComponentPolicies = [original with { LodRuleId = null, LodEvidence = [] }],
            },
        };
        document = document with { RiggingSession = session };

        Assert.True(RigComponentPolicyAuthoring.TryApply(document, session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, original.EmittedMask!.Value,
                original.AnimationLod!.Value, null, null, null)], out RiggingSession result));

        AnimationComponentPolicy updated = Assert.Single(result.Recipe.ComponentPolicies);
        Assert.Equal("authoring-lod-selection-v1", updated.LodRuleId);
        AssertAuthoringEvidence(updated.LodEvidence, document.Source.ContentSha256, entityId, "lod");
        Assert.Equal(session.Revision + 1, result.Revision);
    }

    [Fact]
    public void GenuineNoOpReturnsTheOriginalSession()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        AnimationComponentPolicy policy = Assert.Single(session.Recipe.ComponentPolicies);
        RiggingJobToken token = session.CreateJobToken();

        bool applied = RigComponentPolicyAuthoring.TryApply(document, token,
            [new RigComponentPolicyEdit(entityId, policy.EmittedMask!.Value,
                policy.AnimationLod!.Value, RigComponentOwner.Clip,
                RigComponentOwner.BindInherited, null)], out RiggingSession result);

        Assert.True(applied);
        Assert.Same(session, result);
    }

    [Fact]
    public void MotionChangeInvalidatesAnimationReview()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        session = RiggingSessions.RecordReview(session, RigStudioStage.Animate,
            session.ComputeInputFingerprint(), DateTimeOffset.UtcNow);
        document = document with { RiggingSession = session };

        Assert.True(RigComponentPolicyAuthoring.TryApply(document, session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.None,
                RigAnimationLod.Off, null, null, null)], out RiggingSession result));

        Assert.Null(result.Stages.Single(stage => stage.Stage == RigStudioStage.Animate).ReviewedInputFingerprint);
    }

    [Fact]
    public void RejectsStaleForeignDuplicateAndUnknownEdits()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        RiggingSession changed = RiggingSessions.Change(session,
            session with { Recipe = session.Recipe with { MotionStrategy = RigMotionStrategy.PreserveAnatomyMapped } },
            RiggingEditKind.Motion);
        CustomModelDocument changedDocument = document with { RiggingSession = changed };
        Assert.False(RigComponentPolicyAuthoring.TryApply(changedDocument, session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.None, RigAnimationLod.Off, null, null, null)],
            out RiggingSession staleResult));
        Assert.Same(changed, staleResult);

        Assert.Throws<InvalidDataException>(() => RigComponentPolicyAuthoring.TryApply(document,
            session.CreateJobToken(),
            [new RigComponentPolicyEdit(Guid.NewGuid(), RigAnimationComponents.None, RigAnimationLod.Off, null, null, null)],
            out _));
        Assert.Throws<ArgumentException>(() => RigComponentPolicyAuthoring.TryApply(document,
            session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.None, RigAnimationLod.Off, null, null, null),
             new RigComponentPolicyEdit(entityId, RigAnimationComponents.None, RigAnimationLod.Off, null, null, null)],
            out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => RigComponentPolicyAuthoring.TryApply(document,
            session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.None, RigAnimationLod.Off,
                (RigComponentOwner)99, null, null)], out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => RigComponentPolicyAuthoring.TryApply(document,
            session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, (RigAnimationComponents)8, RigAnimationLod.Off,
                null, null, null)], out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => RigComponentPolicyAuthoring.TryApply(document,
            session.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.None, (RigAnimationLod)99,
                null, null, null)], out _));
    }

    [Fact]
    public void NullOwnerRefusesAnAbsentOrIncompleteChannel()
    {
        CustomModelDocument document = DocumentWithSession(out RiggingSession session, out Guid entityId);
        document = document with
        {
            RiggingSession = session with
            {
                Recipe = session.Recipe with { ComponentPolicies = [] },
            },
        };
        Assert.Throws<InvalidDataException>(() => RigComponentPolicyAuthoring.TryApply(document,
            document.RiggingSession!.CreateJobToken(),
            [new RigComponentPolicyEdit(entityId, RigAnimationComponents.None, RigAnimationLod.Off, null, null, null)],
            out _));
    }

    private static CustomModelDocument DocumentWithSession(out RiggingSession session, out Guid entityId)
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "component-policy.fbx");
        CustomModelDocument document = imported.Package.Document;
        session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        entityId = session.Recipe.Entities[0].EntityId;
        RigEvidenceReference evidence = new()
        {
            Id = "synthetic-policy-evidence",
            Kind = RigEvidenceKind.OfflineTest,
            ArtifactSha256 = document.Source.ContentSha256,
            Description = "Synthetic source-linked policy evidence for a generic test fixture.",
        };
        RigChannelOwnership Channel(RigComponentOwner owner) => new()
        {
            Owners = [owner],
            Evidence = [evidence],
        };
        AnimationComponentPolicy policy = new()
        {
            EntityId = entityId,
            EmittedMask = RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale,
            AnimationLod = RigAnimationLod.Lod1,
            LodRuleId = "existing-lod-rule",
            LodEvidence = [evidence],
            Position = Channel(RigComponentOwner.Clip),
            Rotation = Channel(RigComponentOwner.BindInherited),
            Scale = new RigChannelOwnership
            {
                Owners = [RigComponentOwner.Clip, RigComponentOwner.BindInherited],
                CompositionRuleId = "existing-mixed-scale-rule",
                Evidence = [evidence],
            },
        };
        session = session with
        {
            Recipe = session.Recipe with { ComponentPolicies = [policy] },
        };
        document = document with { RiggingSession = session };
        document.Validate();
        return document;
    }

    private static void AssertAuthoringEvidence(
        ImmutableArray<RigEvidenceReference> evidence,
        string sourceHash,
        Guid entityId,
        string channel)
    {
        RigEvidenceReference row = Assert.Single(evidence);
        Assert.Equal(RigEvidenceKind.UserOverride, row.Kind);
        Assert.Equal(sourceHash, row.ArtifactSha256);
        Assert.Equal($"authoring-choice:{entityId:N}:{channel}", row.Id);
        Assert.Contains("authoring choice; native runtime behavior unverified", row.Description, StringComparison.Ordinal);
        Assert.Null(row.BuildFingerprint);
        Assert.Null(row.ConsumerId);
    }
}
