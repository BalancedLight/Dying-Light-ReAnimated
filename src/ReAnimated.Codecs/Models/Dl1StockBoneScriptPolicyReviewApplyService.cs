using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Reviewer supplied ownership for a selected stock-policy proposal row.</summary>
public sealed record Dl1StockBoneScriptOwnerDecision(
    Guid EntityId,
    RigComponentOwner PositionOwner,
    RigComponentOwner RotationOwner,
    RigComponentOwner ScaleOwner);

/// <summary>An explicit acknowledgment bound to the exact proposal shown to the reviewer.</summary>
public sealed record Dl1StockBoneScriptPolicyReviewAcknowledgment(
    string ProposalFingerprint,
    string SourceSha256,
    bool Reviewed);

/// <summary>
/// Applies only explicitly selected, reviewed stock policy rows. Stock flags are
/// recorded as imported observations; channel ownership remains reviewer input.
/// </summary>
public static class Dl1StockBoneScriptPolicyReviewApplyService
{
    private const uint ComponentBitsMask = 0x0700;
    private const uint LodBitsMask = 0x7000;

    public static bool TryApply(
        CustomModelDocument current,
        RiggingJobToken token,
        Dl1RigTemplate template,
        Dl1PreparedAuthoredRig preparedCurrent,
        Dl1StockBoneScriptPolicyProposal proposal,
        IReadOnlyList<Dl1StockBoneScriptOwnerDecision> decisions,
        Dl1StockBoneScriptPolicyReviewAcknowledgment acknowledgment,
        out CustomModelDocument result)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(preparedCurrent);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(acknowledgment);
        result = current;
        current.Validate();

        if (!acknowledgment.Reviewed)
            throw new InvalidOperationException("The stock policy proposal must be explicitly reviewed before applying rows.");
        if (!string.Equals(proposal.Fingerprint, Dl1StockBoneScriptPolicyProposalService.ComputeFingerprint(proposal), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The stock policy proposal changed after it was created.");
        if (!string.Equals(acknowledgment.ProposalFingerprint, proposal.Fingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acknowledgment.SourceSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The review acknowledgment does not match this proposal and source resource.");
        if (!string.Equals(proposal.SourceSha256, template.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The proposal source hash does not match the selected DL1 template.");

        RiggingSession session = current.RiggingSession ?? throw new InvalidOperationException("Start a Studio session before applying stock policy decisions.");
        if (!session.Matches(token))
            return false;
        if (!session.MatchesSource(current.Source.ContentSha256))
            throw new InvalidDataException("Studio source identity requires review before applying stock policy decisions.");
        ValidateCurrentRig(template, current, preparedCurrent);

        var currentPolicies = session.Recipe.ComponentPolicies.GroupBy(static policy => policy.EntityId)
            .ToDictionary(static group => group.Key, static group => group.Count() == 1 ? group.Single() : null);
        ValidateProposalRows(template, preparedCurrent, currentPolicies, proposal);

        var decisionById = new Dictionary<Guid, Dl1StockBoneScriptOwnerDecision>();
        foreach (Dl1StockBoneScriptOwnerDecision decision in decisions)
        {
            if (decision.EntityId == Guid.Empty || !decisionById.TryAdd(decision.EntityId, decision))
                throw new ArgumentException("Selected stock policy decisions require unique non-empty entity IDs.", nameof(decisions));
            ValidateOwner(decision.PositionOwner);
            ValidateOwner(decision.RotationOwner);
            ValidateOwner(decision.ScaleOwner);
        }

        var selectedRows = proposal.Rows.Where(row => row.Status == Dl1StockPolicyProposalStatus.Proposed &&
            row.DestinationEntityId is { } id && decisionById.ContainsKey(id)).ToArray();
        if (selectedRows.Length != decisionById.Count)
            throw new InvalidDataException("Every selected decision must identify one Proposed row in this proposal.");
        if (selectedRows.Length == 0)
            throw new ArgumentException("Select at least one Proposed row with explicit channel owners.", nameof(decisions));

        var edits = selectedRows.Select(row =>
        {
            Guid id = row.DestinationEntityId!.Value;
            Dl1StockBoneScriptOwnerDecision decision = decisionById[id];
            return new RigComponentPolicyEdit(id, row.ProposedMask!.Value, row.ProposedLod!.Value,
                decision.PositionOwner, decision.RotationOwner, decision.ScaleOwner);
        }).ToArray();

        if (!RigComponentPolicyAuthoring.TryApply(current, token, edits, out RiggingSession appliedSession))
            return false;

        ImmutableArray<AnimationComponentPolicy> policies = appliedSession.Recipe.ComponentPolicies;
        foreach (Dl1StockBoneScriptPolicyReviewRow row in selectedRows)
        {
            Guid id = row.DestinationEntityId!.Value;
            int index = -1;
            for (int candidate = 0; candidate < policies.Length; candidate++)
                if (policies[candidate].EntityId == id) { index = candidate; break; }
            if (index < 0) throw new InvalidDataException("The applied policy row disappeared during authoring.");
            AnimationComponentPolicy policy = policies[index];
            var observed = new RigEvidenceReference
            {
                Id = $"stock-bscr-observation:{proposal.SourceSha256}:{row.SourceIndex}",
                Kind = RigEvidenceKind.ImportedSource,
                ArtifactSha256 = proposal.SourceSha256,
                Description = $"Decoded stock BSCR observation: raw flags 0x{row.RawFlags!.Value:X8}; mask and LOD only; channel ownership unresolved.",
            };
            if (!policy.LodEvidence.Any(existing => existing.Id == observed.Id))
                policies = policies.SetItem(index, policy with { LodEvidence = policy.LodEvidence.Add(observed) });
        }

        if (appliedSession.Revision == session.Revision)
        {
            if (policies.SequenceEqual(session.Recipe.ComponentPolicies))
                return true;
            // Core TryApply can be an exact no-op when the reviewer repeats all
            // current values. Persist new ImportedSource evidence as one undo step.
            RiggingSession replacement = session with { Recipe = session.Recipe with { ComponentPolicies = policies } };
            appliedSession = RiggingSessions.Change(session, replacement, RiggingEditKind.Motion);
        }
        else
        {
            appliedSession = appliedSession with { Recipe = appliedSession.Recipe with { ComponentPolicies = policies } };
        }

        result = current with { RiggingSession = appliedSession, LastBuildReceipt = null };
        result.Validate();
        return true;
    }

    private static void ValidateCurrentRig(Dl1RigTemplate template, CustomModelDocument document, Dl1PreparedAuthoredRig prepared)
    {
        CustomModelRigConformance conformance = document.RigConformance ??
            throw new InvalidDataException("The destination has no saved DL1 conformance record.");
        if (!string.Equals(conformance.TemplateId, template.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(conformance.TemplateFingerprint, template.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(conformance.TemplateSourceResourceName, template.SourceResourceName, StringComparison.Ordinal) ||
            !conformance.MatchesAppliedOutputRig(prepared.SourceRig, document.Source.ContentSha256))
            throw new InvalidDataException("The current template, source identity, or applied output rig no longer matches the proposal.");
    }

    private static void ValidateProposalRows(
        Dl1RigTemplate template,
        Dl1PreparedAuthoredRig prepared,
        Dictionary<Guid, AnimationComponentPolicy?> currentPolicies,
        Dl1StockBoneScriptPolicyProposal proposal)
    {
        if (proposal.Rows.IsDefault || proposal.Rows.Length < template.EntityCount ||
            proposal.Rows.Length > template.EntityCount + prepared.Contract.Nodes.Length)
            throw new InvalidDataException("The proposal row count cannot cover the current template and prepared destination hierarchy.");
        var sourceRows = new HashSet<int>();
        var destinationRows = new HashSet<Guid>();
        var destinationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Dl1StockBoneScriptPolicyReviewRow row in proposal.Rows)
        {
            if (row.SourceIndex is { } anySourceIndex)
            {
                if ((uint)anySourceIndex >= (uint)template.Entities.Length || !sourceRows.Add(anySourceIndex) ||
                    !string.Equals(row.SourceSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A stock source row is duplicated or has a stale source identity.");
                Dl1RigTemplateEntity source = template.Entities[anySourceIndex];
                if (!string.Equals(row.DestinationName, source.Name, StringComparison.Ordinal))
                    throw new InvalidDataException("A stock source row no longer matches its template name.");
            }
            if (row.Status == Dl1StockPolicyProposalStatus.Proposed)
            {
                if (row.SourceIndex is not { } sourceIndex || (uint)sourceIndex >= (uint)template.Entities.Length ||
                    row.DestinationEntityId is not { } entityId || entityId == Guid.Empty || row.RawFlags is not { } flags ||
                    row.ProposedMask is not { } mask || row.ProposedLod is not { } lod ||
                    !destinationRows.Add(entityId))
                    throw new InvalidDataException("A Proposed stock policy row is incomplete or duplicated.");
                Dl1RigTemplateEntity source = template.Entities[sourceIndex];
                Dl1AuthoredRigNode? destination = prepared.Contract.Nodes.SingleOrDefault(node => node.SemanticEntityId == entityId);
                RigAnimationComponents decodedMask = (RigAnimationComponents)((flags & ComponentBitsMask) >> 8);
                int lodValue = (int)((flags & LodBitsMask) >> 12);
                if (!string.Equals(row.SourceSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(row.DestinationName, source.Name, StringComparison.Ordinal) ||
                    destination is null || !string.Equals(destination.Name, row.DestinationName, StringComparison.Ordinal) ||
                    mask != decodedMask || lodValue > (int)RigAnimationLod.Off || lod != (RigAnimationLod)lodValue ||
                    !destinationNames.Add(destination.Name))
                    throw new InvalidDataException("A Proposed row no longer matches its exact stock flags or current semantic destination.");

                currentPolicies.TryGetValue(entityId, out AnimationComponentPolicy? current);
                if (row.CurrentMask != current?.EmittedMask || row.CurrentLod != current?.AnimationLod)
                    throw new InvalidDataException("A Proposed row's saved current mask or LOD changed after proposal creation.");
            }
            else if (row.Status == Dl1StockPolicyProposalStatus.UnresolvedExtraDestination)
            {
                // Extra rows are retained verbatim and may never be targeted by an apply decision.
                Dl1AuthoredRigNode[] matches = prepared.Contract.Nodes.Where(node => node.Name == row.DestinationName &&
                    (row.DestinationEntityId is null ? node.SemanticEntityId is null : node.SemanticEntityId == row.DestinationEntityId)).ToArray();
                if (row.SourceIndex is not null || matches.Length != 1 || !destinationNames.Add(row.DestinationName) ||
                    row.DestinationEntityId is { } extraId && !destinationRows.Add(extraId))
                    throw new InvalidDataException("An unresolved extra row no longer identifies the retained destination node.");
            }
            else if (row.DestinationEntityId is not null && row.Status != Dl1StockPolicyProposalStatus.UnresolvedMissingSemanticEntityId)
            {
                throw new InvalidDataException("An unresolved source row cannot be silently treated as a selected Proposed row.");
            }
        }
        if (sourceRows.Count != template.EntityCount)
            throw new InvalidDataException("The proposal does not contain exactly one Proposed-or-unresolved row per stock source entity.");
        int destinationCoverage = prepared.Contract.Nodes.Count(node => destinationNames.Contains(node.Name) ||
            node.SemanticEntityId is { } id && destinationRows.Contains(id));
        if (destinationCoverage != prepared.Contract.Nodes.Length)
            throw new InvalidDataException("The proposal silently omits a destination node instead of retaining it as a stock match or unresolved extra.");
    }

    private static void ValidateOwner(RigComponentOwner owner)
    {
        if (!Enum.IsDefined(owner) || owner == RigComponentOwner.Unknown)
            throw new ArgumentOutOfRangeException(nameof(owner), "Each selected row requires explicit POS, ROT, and SCL owners.");
    }
}
