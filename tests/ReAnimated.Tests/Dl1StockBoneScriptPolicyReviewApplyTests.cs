using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1StockBoneScriptPolicyReviewApplyTests
{
    private const int StockNodeCount = 87;
    private const string StockHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void AppliesSelectedRowsAsOneUndoStepAndPreservesUnmatchedExtra()
    {
        Fixture fixture = CreateFixture();
        Dl1StockBoneScriptPolicyProposal proposal = fixture.Proposal;
        Assert.Equal(StockNodeCount, proposal.Rows.Count(row => row.SourceIndex is not null));
        Assert.Equal(StockNodeCount, proposal.Rows.Count(row => row.Status == Dl1StockPolicyProposalStatus.Proposed));
        var extra = Assert.Single(proposal.Rows.Where(row => row.Status == Dl1StockPolicyProposalStatus.UnresolvedExtraDestination));
        Assert.Equal("synthetic_extra", extra.DestinationName);
        Guid[] selected = proposal.Rows.Take(3).Select(row => row.DestinationEntityId!.Value).ToArray();
        var decisions = selected.Select(id => new Dl1StockBoneScriptOwnerDecision(id,
            RigComponentOwner.Clip, RigComponentOwner.BindInherited, RigComponentOwner.Procedural)).ToArray();

        Assert.True(Apply(fixture, fixture.Document, fixture.Session.CreateJobToken(), proposal, decisions, reviewed: true,
            out CustomModelDocument result));

        RiggingSession updated = result.RiggingSession!;
        Assert.Equal(fixture.Session.Revision + 1, updated.Revision);
        Assert.Equal(fixture.Document.Bones[^1], result.Bones[^1]);
        Assert.Equal(fixture.Document.RiggingSession!.Recipe.Entities[^1], updated.Recipe.Entities[^1]);
        Assert.Equal(StockNodeCount, proposal.Rows.Count(row => row.Status == Dl1StockPolicyProposalStatus.Proposed));
        Assert.Equal(3, updated.Recipe.ComponentPolicies.Length);
        foreach (AnimationComponentPolicy policy in updated.Recipe.ComponentPolicies)
        {
            Assert.Equal(RigEvidenceKind.UserOverride, Assert.Single(policy.Position.Evidence).Kind);
            Assert.Equal(RigEvidenceKind.UserOverride, Assert.Single(policy.Rotation.Evidence).Kind);
            Assert.Equal(RigEvidenceKind.UserOverride, Assert.Single(policy.Scale.Evidence).Kind);
            Assert.Contains(policy.LodEvidence, evidence => evidence.Kind == RigEvidenceKind.UserOverride);
            RigEvidenceReference imported = Assert.Single(policy.LodEvidence.Where(evidence => evidence.Kind == RigEvidenceKind.ImportedSource));
            Assert.Equal(StockHash, imported.ArtifactSha256);
            Assert.Contains("raw flags", imported.Description, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Throws<InvalidDataException>(() => Dl1BoneScriptPolicyResolver.Resolve(result, fixture.Prepared.Contract));
    }

    [Fact]
    public void RejectsStaleTokenAlteredProposalAndUnreviewedApply()
    {
        Fixture fixture = CreateFixture();
        var decision = new Dl1StockBoneScriptOwnerDecision(fixture.Proposal.Rows[0].DestinationEntityId!.Value,
            RigComponentOwner.Clip, RigComponentOwner.Clip, RigComponentOwner.BindInherited);
        RiggingSession changedSession = RiggingSessions.Change(fixture.Session,
            fixture.Session with { Stage = RigStudioStage.Detect }, RiggingEditKind.Detection);
        CustomModelDocument changedDocument = fixture.Document with { RiggingSession = changedSession };

        Assert.False(Apply(fixture, changedDocument, fixture.Session.CreateJobToken(), fixture.Proposal, [decision], true, out var staleResult));
        Assert.Same(changedDocument, staleResult);

        Dl1StockBoneScriptPolicyProposal altered = fixture.Proposal with
        {
            Rows = fixture.Proposal.Rows.SetItem(0, fixture.Proposal.Rows[0] with { RawFlags = fixture.Proposal.Rows[0].RawFlags!.Value ^ 0x100u }),
        };
        Assert.Throws<InvalidDataException>(() => Apply(fixture, fixture.Document, fixture.Session.CreateJobToken(), altered, [decision], true, out _));
        Assert.Throws<InvalidOperationException>(() => Apply(fixture, fixture.Document, fixture.Session.CreateJobToken(), fixture.Proposal, [decision], false, out _));
    }

    [Fact]
    public void ReapplyingAnAlreadyReviewedSelectionDoesNotCreateAnotherUndoStep()
    {
        Fixture fixture = CreateFixture();
        Guid[] selected = fixture.Proposal.Rows.Take(3).Select(row => row.DestinationEntityId!.Value).ToArray();
        var decisions = selected.Select(id => new Dl1StockBoneScriptOwnerDecision(id,
            RigComponentOwner.Clip, RigComponentOwner.BindInherited, RigComponentOwner.Procedural)).ToArray();
        Assert.True(Apply(fixture, fixture.Document, fixture.Session.CreateJobToken(), fixture.Proposal, decisions, true,
            out CustomModelDocument firstResult));

        Dictionary<Guid, AnimationComponentPolicy> applied = firstResult.RiggingSession!.Recipe.ComponentPolicies
            .ToDictionary(policy => policy.EntityId);
        var refreshedRows = fixture.Proposal.Rows.Select(row => row.DestinationEntityId is { } id && applied.TryGetValue(id, out AnimationComponentPolicy? policy)
            ? row with { CurrentMask = policy.EmittedMask, CurrentLod = policy.AnimationLod }
            : row).ToImmutableArray();
        Dl1StockBoneScriptPolicyProposal refreshedDraft = fixture.Proposal with { Rows = refreshedRows, Fingerprint = string.Empty };
        Dl1StockBoneScriptPolicyProposal refreshed = refreshedDraft with
        {
            Fingerprint = Dl1StockBoneScriptPolicyProposalService.ComputeFingerprint(refreshedDraft),
        };

        Assert.True(Apply(fixture, firstResult, firstResult.RiggingSession.CreateJobToken(), refreshed, decisions, true,
            out CustomModelDocument repeatedResult));
        Assert.Same(firstResult, repeatedResult);
        Assert.Equal(firstResult.RiggingSession.Revision, repeatedResult.RiggingSession!.Revision);
    }

    private static bool Apply(Fixture fixture, CustomModelDocument document, RiggingJobToken token,
        Dl1StockBoneScriptPolicyProposal proposal, IReadOnlyList<Dl1StockBoneScriptOwnerDecision> decisions,
        bool reviewed, out CustomModelDocument result) => Dl1StockBoneScriptPolicyReviewApplyService.TryApply(
            document, token, fixture.Template, fixture.Prepared, proposal, decisions,
            new Dl1StockBoneScriptPolicyReviewAcknowledgment(proposal.Fingerprint, proposal.SourceSha256, reviewed), out result);

    private static Fixture CreateFixture()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "synthetic_stock_policy.fbx");
        string modelHash = imported.Package.Document.Source.ContentSha256;
        const int destinationCount = StockNodeCount + 1;
        ImmutableArray<CustomModelBone> bones = Enumerable.Range(0, destinationCount).Select(index => new CustomModelBone
        {
            Index = index,
            FbxObjectId = index + 1,
            Name = index == StockNodeCount ? "synthetic_extra" : $"synthetic_node_{index:D3}",
            ParentIndex = index == 0 || index == StockNodeCount ? -1 : 0,
            LocalBindTransform = TransformTRS.Identity,
            ExactLocalBindMatrix = TransformMatrix.Identity,
            Kind = index == 0 ? BoneKind.Root : index == StockNodeCount ? BoneKind.Helper : BoneKind.Deform,
            IsWeighted = index > 0 && index < StockNodeCount,
        }).ToImmutableArray();
        CustomModelDocument document = imported.Package.Document with
        {
            Bones = bones,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones),
            RiggingSession = null,
        };
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        document = document with { RiggingSession = session };

        TransformMatrix identity = TransformMatrix.Identity;
        var nodes = ImmutableArray.CreateBuilder<Dl1AuthoredRigNode>(destinationCount);
        for (int index = 0; index < destinationCount; index++)
        {
            CustomModelBone bone = bones[index];
            nodes.Add(new Dl1AuthoredRigNode
            {
                PhysicalIndex = index,
                SourceBoneIndex = index,
                Name = bone.Name,
                ParentPhysicalIndex = bone.ParentIndex,
                Kind = bone.Kind,
                IsDeform = bone.IsWeighted,
                SemanticEntityId = session.Recipe.Entities[index].EntityId,
                FramePolicy = RigFramePolicy.Manual,
                BoundsPolicy = RigBoundsPolicy.PreserveSource,
                LocalBindMatrix = identity,
                GlobalBindMatrix = identity,
                InverseGlobalReferenceMatrix = identity,
                Bounds = new Dl1AuthoredBoneBounds(Vector3D.Zero, new Vector3D(.1, .1, .1)),
                DescriptorHash = (uint)(index + 1),
            });
        }
        var contract = new Dl1AuthoredRigContract("synthetic-policy-rig", modelHash, nodes.ToImmutable());
        var prepared = new Dl1PreparedAuthoredRig(contract, contract.CreateRigDefinition(), [], []);
        var templateEntities = Enumerable.Range(0, StockNodeCount).Select(index => new Dl1RigTemplateEntity
        {
            Index = index,
            Name = bones[index].Name,
            ParentIndex = bones[index].ParentIndex,
            Kind = bones[index].Kind,
            IsDeform = bones[index].IsWeighted,
            LocalRestMatrix = identity,
            GlobalRestMatrix = identity,
        }).ToArray();
        var template = new Dl1RigTemplate("synthetic-policy-profile", "synthetic-policy-resource", StockHash, templateEntities);
        var conformance = new CustomModelRigConformance
        {
            TemplateId = template.TemplateId,
            TemplateProfileName = template.ProfileName,
            TemplateSourceResourceName = template.SourceResourceName,
            TemplateFingerprint = template.SourceFingerprint,
            SourceFbxSha256 = modelHash,
            AppliedOutputRigSignature = RigSignature.Compute(prepared.SourceRig),
        };
        document = document with { RigConformance = conformance };
        session = document.RiggingSession!;
        var rows = ImmutableArray.CreateBuilder<Dl1StockBoneScriptPolicyReviewRow>(destinationCount);
        for (int index = 0; index < StockNodeCount; index++)
        {
            uint rawFlags = (uint)(((index % 8) << 8) | ((index % 5) << 12));
            rows.Add(new(index, session.Recipe.Entities[index].EntityId, bones[index].Name, StockHash, rawFlags,
                null, null, (RigAnimationComponents)(index % 8), (RigAnimationLod)(index % 5),
                Dl1StockPolicyProposalStatus.Proposed, "Synthetic reviewed row."));
        }
        rows.Add(new(null, session.Recipe.Entities[^1].EntityId, bones[^1].Name, StockHash, null,
            null, null, null, null, Dl1StockPolicyProposalStatus.UnresolvedExtraDestination, "Synthetic extra destination."));
        var draft = new Dl1StockBoneScriptPolicyProposal(StockHash, rows.ToImmutable());
        var proposal = draft with { Fingerprint = Dl1StockBoneScriptPolicyProposalService.ComputeFingerprint(draft) };
        return new(template, prepared, document, session, proposal);
    }

    private sealed record Fixture(Dl1RigTemplate Template, Dl1PreparedAuthoredRig Prepared,
        CustomModelDocument Document, RiggingSession Session, Dl1StockBoneScriptPolicyProposal Proposal);
}
