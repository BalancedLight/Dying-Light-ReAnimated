using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>One reviewed choice for a root channel in the stock-humanoid rotation-only proposal.</summary>
public enum StockHumanoidRootChannel { Bind, Clip }

public enum StockHumanoidPolicyRowStatus { Proposed, ExistingDecision, UnmatchedExtra, Helper }

public sealed record StockHumanoidPolicyRow(
    Guid EntityId, string Name, StockHumanoidPolicyRowStatus Status,
    RigAnimationComponents? CurrentMask, RigAnimationLod? CurrentLod,
    RigAnimationComponents? ProposedMask, RigAnimationLod? ProposedLod,
    string Reason);

/// <summary>
/// A read-only, source/session-pinned authoring proposal. Only exact bones from
/// the selected humanoid template are proposed; extras remain for separate review.
/// </summary>
public sealed record StockHumanoidChannelPolicyProposal(
    RiggingJobToken Token, string TemplateId, string SourceSha256, Guid RootEntityId,
    ImmutableArray<StockHumanoidPolicyRow> Rows,
    ImmutableArray<RigComponentPolicyEdit> Edits);

public static class StockHumanoidChannelPolicyAuthoring
{
    public static StockHumanoidChannelPolicyProposal Propose(
        CustomModelDocument document, Dl1RigTemplate template,
        StockHumanoidRootChannel rootPosition,
        StockHumanoidRootChannel rootRotation,
        StockHumanoidRootChannel rootScale)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        document.Validate();
        if (!Enum.IsDefined(rootPosition) || !Enum.IsDefined(rootRotation) || !Enum.IsDefined(rootScale))
            throw new ArgumentOutOfRangeException(nameof(rootPosition));
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("A rigging session is required.");
        if (!session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidDataException("The rigging session does not match this source model.");

        var hierarchy = RiggingSessions.ObserveSourceHierarchy(document).ToDictionary(static row => row.EntityId);
        var existing = session.Recipe.ComponentPolicies.ToDictionary(static row => row.EntityId);
        // A player template can have independent prop-holder roots. The body.root
        // semantic role is the only explicit body origin; other roots must not
        // receive this proposal merely because their names match the template.
        int? semanticRoot = template.TryFindByRole("body.root");
        int rootTemplateIndex;
        if (semanticRoot is { } namedRoot)
        {
            rootTemplateIndex = namedRoot;
        }
        else
        {
            int[] topLevel = template.Entities.Where(static row => row.ParentIndex < 0)
                .Select(static row => row.Index).ToArray();
            if (topLevel.Length != 1)
                throw new InvalidDataException("The selected template has multiple roots but no body.root role. Choose a resolved humanoid template before applying this proposal.");
            rootTemplateIndex = topLevel[0];
        }
        bool IsBodyBranch(Dl1RigTemplateEntity row)
        {
            int index = row.Index;
            while (index >= 0)
            {
                if (index == rootTemplateIndex) return true;
                index = template[index].ParentIndex;
            }
            return false;
        }
        var templateBones = template.Entities.Where(row =>
                IsBodyBranch(row) && (row.IsDeform || row.Index == rootTemplateIndex))
            .Select(static row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Imported source bones start as Unknown until a later capability review.
        // Identify them against the document's observed bone table, not the optional kind hint.
        var sourceBones = document.Bones.Select(static row => row.Name).ToHashSet(StringComparer.Ordinal);
        var eligibleBones = session.Recipe.Entities.Where(row => row.OwnerAssetId == document.ModelId &&
            row.Kind is RigNativeEntityKind.Bone or RigNativeEntityKind.Unknown &&
            sourceBones.Contains(row.NativeName) && templateBones.Contains(row.NativeName) &&
            hierarchy.ContainsKey(row.EntityId)).Select(static row => row.EntityId).ToHashSet();
        string bodyRootName = template[rootTemplateIndex].Name;
        Guid[] roots = session.Recipe.Entities.Where(row => row.OwnerAssetId == document.ModelId &&
            eligibleBones.Contains(row.EntityId) &&
            string.Equals(row.NativeName, bodyRootName, StringComparison.OrdinalIgnoreCase))
            .Select(static row => row.EntityId).ToArray();
        if (roots.Length != 1)
            throw new InvalidDataException($"Expected one exact body.root bone; found {roots.Length}. Review the selected humanoid template and current hierarchy.");
        Guid rootId = roots[0];
        bool IsObservedBodyBranch(Guid id)
        {
            for (int remaining = hierarchy.Count; remaining > 0 && hierarchy.TryGetValue(id, out RigParentObservation? node); remaining--)
            {
                if (id == rootId) return true;
                if (node.ParentEntityId is not { } parent) return false;
                id = parent;
            }
            return false;
        }
        var rows = ImmutableArray.CreateBuilder<StockHumanoidPolicyRow>();
        var edits = ImmutableArray.CreateBuilder<RigComponentPolicyEdit>();
        foreach (RigEntityBinding entity in session.Recipe.Entities.Where(row => row.OwnerAssetId == document.ModelId))
        {
            if (!hierarchy.ContainsKey(entity.EntityId))
                continue;
            existing.TryGetValue(entity.EntityId, out AnimationComponentPolicy? current);
            if (entity.Kind is not (RigNativeEntityKind.Bone or RigNativeEntityKind.Unknown) ||
                !sourceBones.Contains(entity.NativeName))
            {
                rows.Add(new(entity.EntityId, entity.NativeName, StockHumanoidPolicyRowStatus.Helper,
                    current?.EmittedMask, current?.AnimationLod, null, null,
                    "Helper policy remains unchanged."));
                continue;
            }
            if (!templateBones.Contains(entity.NativeName) || !IsObservedBodyBranch(entity.EntityId))
            {
                rows.Add(new(entity.EntityId, entity.NativeName, StockHumanoidPolicyRowStatus.UnmatchedExtra,
                    current?.EmittedMask, current?.AnimationLod, null, null,
                    "Unrelated template root or extra/secondary bone; this policy leaves it for separate review."));
                continue;
            }

            bool root = entity.EntityId == rootId;
            RigAnimationComponents mask = root
                ? (rootPosition == StockHumanoidRootChannel.Clip ? RigAnimationComponents.Position : RigAnimationComponents.None) |
                  (rootRotation == StockHumanoidRootChannel.Clip ? RigAnimationComponents.Rotation : RigAnimationComponents.None) |
                  (rootScale == StockHumanoidRootChannel.Clip ? RigAnimationComponents.Scale : RigAnimationComponents.None)
                : RigAnimationComponents.Rotation;
            RigAnimationLod lod = current?.AnimationLod ?? RigAnimationLod.Off;
            edits.Add(new(entity.EntityId, mask, lod,
                root && rootPosition == StockHumanoidRootChannel.Clip ? RigComponentOwner.Clip : RigComponentOwner.BindInherited,
                root && rootRotation == StockHumanoidRootChannel.Bind ? RigComponentOwner.BindInherited : RigComponentOwner.Clip,
                root && rootScale == StockHumanoidRootChannel.Clip ? RigComponentOwner.Clip : RigComponentOwner.BindInherited));
            rows.Add(new(entity.EntityId, entity.NativeName,
                current is null ? StockHumanoidPolicyRowStatus.Proposed : StockHumanoidPolicyRowStatus.ExistingDecision,
                current?.EmittedMask, current?.AnimationLod, mask, lod,
                root ? "Root POS, ROT and SCL use the three explicit choices above."
                     : "Keep authored bind translation and scale; accept clip rotation. Native behavior needs validation."));
        }
        return new(session.CreateJobToken(), template.TemplateId, document.Source.ContentSha256, rootId,
            rows.ToImmutable(), edits.ToImmutable());
    }

    /// <summary>Applies only reviewed rows as one undoable motion edit; existing rows require separate consent.</summary>
    public static bool TryApply(
        CustomModelDocument document, Dl1RigTemplate template,
        StockHumanoidChannelPolicyProposal proposal, bool includeExistingDecisions,
        bool reviewed, out RiggingSession result)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(proposal);
        result = document.RiggingSession ?? throw new InvalidOperationException("A rigging session is required.");
        if (!reviewed) throw new InvalidOperationException("The proposal must be reviewed before applying.");
        if (!result.Matches(proposal.Token)) return false;
        if (!string.Equals(template.TemplateId, proposal.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(document.Source.ContentSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The proposal does not match the selected template or source model.");
        // Recompute against the saved root choices, expressed by the proposal's root edit.
        RigComponentPolicyEdit root = proposal.Edits.Single(edit => edit.EntityId == proposal.RootEntityId);
        var fresh = Propose(document, template,
            root.PositionOwner == RigComponentOwner.Clip ? StockHumanoidRootChannel.Clip : StockHumanoidRootChannel.Bind,
            root.RotationOwner == RigComponentOwner.Clip ? StockHumanoidRootChannel.Clip : StockHumanoidRootChannel.Bind,
            root.ScaleOwner == RigComponentOwner.Clip ? StockHumanoidRootChannel.Clip : StockHumanoidRootChannel.Bind);
        if (fresh.RootEntityId != proposal.RootEntityId ||
            !fresh.Rows.SequenceEqual(proposal.Rows) || !fresh.Edits.SequenceEqual(proposal.Edits))
            throw new InvalidDataException("The saved channel decisions changed. Preview the proposal again.");
        var statuses = proposal.Rows.ToDictionary(static row => row.EntityId, static row => row.Status);
        RigComponentPolicyEdit[] selected = proposal.Edits.Where(edit =>
            statuses[edit.EntityId] == StockHumanoidPolicyRowStatus.Proposed ||
            includeExistingDecisions && statuses[edit.EntityId] == StockHumanoidPolicyRowStatus.ExistingDecision).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No rows in the selected review scope can be applied.");
        return RigComponentPolicyAuthoring.TryApply(document, proposal.Token, selected, out result);
    }
}
