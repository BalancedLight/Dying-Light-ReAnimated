using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class TerminalHelperChannelPolicyAuthoringTests
{
    [Fact]
    public void ExcludingATakeFromExportPreservesItsOriginalTrackEvidence()
    {
        var fixture = CreateFixture();
        var document = fixture.Model.Package.Document;
        document = document with { AnimationClips = document.AnimationClips.Select(clip => clip with { Included = false }).ToImmutableArray() };
        var model = fixture.Model with { Package = fixture.Model.Package with { Document = document } };
        var observations = TerminalHelperChannelPolicyAuthoring.Observe(model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Off);
        var track = Assert.Single(Assert.Single(observations).Tracks);
        Assert.False(track.IncludedForExport);
        Assert.Equal(2, track.Keys.Length);
        var proposal = TerminalHelperChannelPolicyAuthoring.Propose(document, observations);
        var row = Assert.Single(proposal.Rows);
        Assert.Equal(TerminalHelperPolicyRowStatus.Proposed, row.Status);
        Assert.Contains("takes excluded from export=1", row.Evidence, StringComparison.Ordinal);
        Assert.Contains("constant value differs from fitted bind", row.Evidence, StringComparison.Ordinal);
        Assert.False(Assert.Single(document.AnimationClips).Included);
    }

    [Fact]
    public void ExplicitOffLodRemainsAnIndependentReviewedAuthoringChoice()
    {
        var fixture = CreateFixture();
        var observations = TerminalHelperChannelPolicyAuthoring.Observe(fixture.Model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Off);
        var proposal = TerminalHelperChannelPolicyAuthoring.Propose(fixture.Model.Package.Document, observations);
        Assert.Equal(TerminalHelperPolicyRowStatus.Proposed, Assert.Single(proposal.Rows).Status);
        Assert.Equal(RigAnimationLod.Off, Assert.Single(proposal.Edits).Lod);
        Assert.Throws<InvalidOperationException>(() => TerminalHelperChannelPolicyAuthoring.TryApply(
            fixture.Model.Package.Document, observations, proposal, reviewed: false, out _));
        Assert.True(TerminalHelperChannelPolicyAuthoring.TryApply(fixture.Model.Package.Document, observations, proposal, reviewed: true, out var updated));
        var policy = Assert.Single(updated.Recipe.ComponentPolicies);
        Assert.Equal(RigAnimationLod.Off, policy.AnimationLod);
        Assert.Equal(RigAnimationComponents.None, policy.EmittedMask);
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(policy.Position.Owners));
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalHelperChannelPolicyAuthoring.Observe(
            fixture.Model, new HashSet<Guid> { fixture.EntityId }, (RigAnimationLod)99));
    }

    [Fact]
    public void ProposesOnlyAfterReviewAndShowsConstantTrackDifferenceFromFittedBind()
    {
        var fixture = CreateFixture();
        ImmutableArray<TerminalHelperFitObservation> observations = TerminalHelperChannelPolicyAuthoring.Observe(
            fixture.Model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Lod1);

        TerminalHelperChannelPolicyProposal proposal = TerminalHelperChannelPolicyAuthoring.Propose(
            fixture.Model.Package.Document, observations);
        TerminalHelperChannelPolicyRow row = Assert.Single(proposal.Rows);
        Assert.Equal(TerminalHelperPolicyRowStatus.Proposed, row.Status);
        Assert.Contains("constant value differs from fitted bind", row.Evidence, StringComparison.Ordinal);
        Assert.Contains("POS=1", row.Evidence, StringComparison.Ordinal);
        Assert.Contains("does not establish safe discard", row.Evidence, StringComparison.Ordinal);
        Assert.Equal(RigAnimationComponents.None, Assert.Single(proposal.Edits).Mask);
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(proposal.Edits).PositionOwner);
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(proposal.Edits).RotationOwner);
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(proposal.Edits).ScaleOwner);
        Assert.Equal(RigAnimationLod.Lod1, Assert.Single(proposal.Edits).Lod);

        Assert.Throws<InvalidOperationException>(() => TerminalHelperChannelPolicyAuthoring.TryApply(
            fixture.Model.Package.Document, observations, proposal, reviewed: false, out _));
        Assert.True(TerminalHelperChannelPolicyAuthoring.TryApply(
            fixture.Model.Package.Document, observations, proposal, reviewed: true, out RiggingSession updated));
        AnimationComponentPolicy policy = Assert.Single(updated.Recipe.ComponentPolicies);
        Assert.Equal(RigAnimationComponents.None, policy.EmittedMask);
        Assert.All(policy.Position.Owners, owner => Assert.Equal(RigComponentOwner.BindInherited, owner));
    }

    [Fact]
    public void ExistingSavedRowIsWholeNodeOnlyUnsetAndNeverOverwritten()
    {
        var fixture = CreateFixture();
        CustomModelDocument document = fixture.Model.Package.Document;
        RigComponentPolicyEdit savedEdit = new(fixture.EntityId,
            RigAnimationComponents.Position | RigAnimationComponents.Rotation,
            RigAnimationLod.Lod3, RigComponentOwner.Clip, RigComponentOwner.Clip, RigComponentOwner.BindInherited);
        Assert.True(RigComponentPolicyAuthoring.TryApply(document, document.RiggingSession!.CreateJobToken(),
            [savedEdit], out RiggingSession saved));
        document = document with { RiggingSession = saved };
        FbxModelAuthoringImportResult model = fixture.Model with { Package = fixture.Model.Package with { Document = document } };
        ImmutableArray<TerminalHelperFitObservation> observations = TerminalHelperChannelPolicyAuthoring.Observe(
            model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Lod1);

        TerminalHelperChannelPolicyProposal proposal = TerminalHelperChannelPolicyAuthoring.Propose(document, observations);
        Assert.Empty(proposal.Edits);
        Assert.Equal(TerminalHelperPolicyRowStatus.ExistingDecision, Assert.Single(proposal.Rows).Status);
        Assert.False(TerminalHelperChannelPolicyAuthoring.TryApply(document, observations, proposal,
            reviewed: true, out _));
        Assert.Equal(savedEdit.Mask, Assert.Single(saved.Recipe.ComponentPolicies).EmittedMask);
        Assert.Equal(savedEdit.Lod, Assert.Single(saved.Recipe.ComponentPolicies).AnimationLod);
    }

    [Fact]
    public void NonconstantTracksAndChangedRigEvidenceFailClosed()
    {
        var fixture = CreateFixture();
        ImmutableArray<TerminalHelperFitObservation> observations = TerminalHelperChannelPolicyAuthoring.Observe(
            fixture.Model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Lod1);
        TerminalHelperFitObservation observation = Assert.Single(observations);
        TransformTRS changed = observation.Tracks[0].Keys[1] with
        {
            Translation = new Vector3D(2, 0, 0),
        };
        ImmutableArray<TerminalHelperFitObservation> animated = observations.SetItem(0,
            observation with { Tracks = [observation.Tracks[0] with { Keys = [observation.Tracks[0].Keys[0], changed] }] });
        Assert.Equal(TerminalHelperPolicyRowStatus.Ineligible,
            Assert.Single(TerminalHelperChannelPolicyAuthoring.Propose(fixture.Model.Package.Document, animated).Rows).Status);

        TerminalHelperChannelPolicyProposal proposal = TerminalHelperChannelPolicyAuthoring.Propose(
            fixture.Model.Package.Document, observations);
        ImmutableArray<TerminalHelperFitObservation> changedFit = observations.SetItem(0,
            observation with { FittedLocalBind = observation.FittedLocalBind with { Translation = new Vector3D(3, 0, 0) } });
        Assert.False(TerminalHelperChannelPolicyAuthoring.TryApply(
            fixture.Model.Package.Document, changedFit, proposal, reviewed: true, out _));
        ImmutableArray<TerminalHelperFitObservation> changedRig = observations.SetItem(0,
            observation with { EffectiveRigFingerprint = new string('f', 64) });
        Assert.False(TerminalHelperChannelPolicyAuthoring.TryApply(
            fixture.Model.Package.Document, changedRig, proposal, reviewed: true, out _));
    }

    [Fact]
    public void ObserverUsesSourceSurfaceWeightsAndRequiresCallerSuppliedUnmatchedEvidence()
    {
        var fixture = CreateFixture();
        var noMatch = TerminalHelperChannelPolicyAuthoring.Observe(fixture.Model, new HashSet<Guid>(), RigAnimationLod.Lod1);
        TerminalHelperChannelPolicyProposal proposal = TerminalHelperChannelPolicyAuthoring.Propose(
            fixture.Model.Package.Document, noMatch);
        Assert.Equal(TerminalHelperPolicyRowStatus.Ineligible, Assert.Single(proposal.Rows).Status);
        Assert.Contains("not explicitly marked unmatched", proposal.Rows[0].Evidence, StringComparison.Ordinal);

        TerminalHelperFitObservation row = Assert.Single(TerminalHelperChannelPolicyAuthoring.Observe(
            fixture.Model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Lod1));
        Assert.Equal(0, row.RenderWeightCount);
        Assert.Equal(fixture.HelperIndex, row.BoneIndex);
    }

    [Fact]
    public void ObserverMapsSourceHelperAcrossAnInsertedEffectiveTargetBone()
    {
        var fixture = CreateFixture();
        FbxModelAuthoringImportResult model = fixture.Model;
        RigDefinition oldRig = model.Rig!;
        var effectiveBones = oldRig.Bones.Take(fixture.HelperIndex).ToList();
        effectiveBones.Add(new BoneDefinition(fixture.HelperIndex, "fit_target_extra", 0,
            TransformTRS.Identity, BoneKind.Helper));
        effectiveBones.AddRange(oldRig.Bones.Skip(fixture.HelperIndex).Select(bone =>
            new BoneDefinition(bone.Index + 1, bone.Name,
                bone.ParentIndex >= fixture.HelperIndex ? bone.ParentIndex + 1 : bone.ParentIndex,
                bone.LocalBindPose, bone.Kind)));
        var reindexedRig = new RigDefinition("synthetic-effective-fit", "Synthetic effective fit", effectiveBones,
            oldRig.MorphChannels, oldRig.SourceAssetFingerprint, oldRig.IkChains);
        AnimationClip oldClip = model.AnimationClips.Values.Single();
        AnimationClip reindexedClip = new(oldClip.Name, oldClip.FrameRate, oldClip.FrameCount,
            [new TransformTrack(fixture.HelperIndex + 1, oldClip.TransformTracks[0].Keyframes)]);
        model = model with { Rig = reindexedRig, AnimationClips = model.AnimationClips.SetItem(
            model.AnimationClips.Keys.Single(), reindexedClip) };

        TerminalHelperFitObservation observation = Assert.Single(TerminalHelperChannelPolicyAuthoring.Observe(
            model, new HashSet<Guid> { fixture.EntityId }, RigAnimationLod.Lod2));
        Assert.Equal(fixture.HelperIndex, observation.BoneIndex);
        Assert.Equal(fixture.HelperIndex + 1, observation.FittedRigBoneIndex);
        Assert.Equal(fixture.HelperIndex + 1, observation.Tracks[0].BoneIndex);
        TerminalHelperChannelPolicyProposal proposal = TerminalHelperChannelPolicyAuthoring.Propose(
            model.Package.Document, [observation]);
        Assert.Equal(TerminalHelperPolicyRowStatus.Proposed, Assert.Single(proposal.Rows).Status);
    }

    private static Fixture CreateFixture()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "synthetic-model.fbx");
        CustomModelDocument source = imported.Package.Document;
        int helperIndex = source.Bones.Length;
        CustomModelBone parent = source.Bones[0];
        CustomModelBone helper = parent with
        {
            Index = helperIndex,
            FbxObjectId = 87001,
            Name = "aux_marker",
            ParentIndex = 0,
            Kind = BoneKind.Helper,
            IsWeighted = false,
            LocalBindTransform = TransformTRS.Identity,
            ExactLocalBindMatrix = TransformMatrix.Identity,
        };
        CustomModelDocument document = source with { Bones = source.Bones.Add(helper), RiggingSession = null };
        document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
        Guid entityId = document.RiggingSession.Recipe.Entities.Single(entity => entity.SourceEntityId == "fbx:87001").EntityId;
        var clipId = Guid.NewGuid();
        var sampled = new TransformTRS(new Vector3D(1, 0, 0), QuaternionD.Identity, Vector3D.One);
        var clip = new AnimationClip("synthetic_clip", new FrameRate(30, 1), 2,
            [new TransformTrack(helperIndex, [new TransformKeyframe(0, sampled), new TransformKeyframe(1, sampled)])]);
        CustomModelAnimationClip metadata = new()
        {
            Id = clipId, SourceName = "synthetic_clip", DisplayName = "synthetic_clip", Included = true,
            FrameRate = new FrameRate(30, 1), StartFrame = 0, FrameCount = 2,
            SourceFingerprint = new string('a', 64), HasSkeletalTracks = true,
        };
        document = document with { AnimationClips = [metadata] };
        document.Validate();
        var model = imported with
        {
            Package = imported.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
            AnimationClips = ImmutableDictionary<Guid, AnimationClip>.Empty.Add(clipId, clip),
        };
        return new(model, entityId, helperIndex);
    }

    private sealed record Fixture(FbxModelAuthoringImportResult Model, Guid EntityId, int HelperIndex);
}
