using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Initial channel choices for a newly fitted model using an existing animation bank.</summary>
public static class ExistingAnimationBankDefaults
{
    private const string EvidenceId = "existing-bank-model-proportions-v1";

    public static bool IsSelected(CustomModelDocument document) =>
        document.RiggingSession?.Recipe.ComponentPolicies.Any(policy =>
            policy.LodEvidence.Any(evidence => evidence.Id == EvidenceId)) == true;

    public static CustomModelDocument Apply(
        CustomModelDocument document,
        Dl1RigTemplate template,
        StockHumanoidRootChannel rootPosition = StockHumanoidRootChannel.Clip,
        StockHumanoidRootChannel rootRotation = StockHumanoidRootChannel.Clip,
        StockHumanoidRootChannel rootScale = StockHumanoidRootChannel.Bind)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(template);
        document.Validate();
        if (document.RigConformance is not { } fit ||
            !string.Equals(fit.TemplateId, template.TemplateId, StringComparison.Ordinal) ||
            !string.Equals(fit.TemplateFingerprint, template.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !fit.MatchesAppliedOutputRig(document.CreateRigDefinition(), document.Source.ContentSha256))
            return document;

        RiggingSession session = document.RiggingSession ?? RiggingSessions.Create(document, RigStudioEntryPath.AdaptExistingRig);
        if (!session.MatchesSource(document.Source.ContentSha256)) return document;
        CustomModelDocument current = document with { RiggingSession = session };
        StockHumanoidChannelPolicyProposal proposal = StockHumanoidChannelPolicyAuthoring.Propose(
            current, template, rootPosition, rootRotation, rootScale);
        var existing = session.Recipe.ComponentPolicies.Select(policy => policy.EntityId).ToHashSet();
        var proposed = proposal.Edits.ToDictionary(edit => edit.EntityId);
        var observed = RiggingSessions.ObserveSourceHierarchy(current).ToDictionary(row => row.EntityId);
        bool nativeFrames = RigGripChannelDefaults.MatchesNativeFrames(current, template);
        var edits = session.Recipe.Entities.Where(entity => entity.OwnerAssetId == document.ModelId &&
                observed.ContainsKey(entity.EntityId) && !existing.Contains(entity.EntityId))
            .Select(entity => RigGripChannelDefaults.IsNativeHandHolder(entity.NativeName)
                ? new RigComponentPolicyEdit(entity.EntityId, RigAnimationComponents.Rotation |
                    (nativeFrames ? RigAnimationComponents.Position : RigAnimationComponents.None),
                    RigAnimationLod.Off, nativeFrames ? RigComponentOwner.Clip : RigComponentOwner.BindInherited,
                    RigComponentOwner.Clip, RigComponentOwner.BindInherited)
                : proposed.TryGetValue(entity.EntityId, out RigComponentPolicyEdit? edit)
                ? edit
                : new RigComponentPolicyEdit(entity.EntityId,
                    RigAnimationComponents.Position | RigAnimationComponents.Rotation,
                    RigAnimationLod.Off, RigComponentOwner.Clip, RigComponentOwner.Clip, RigComponentOwner.BindInherited))
            .ToArray();
        if (edits.Length == 0) return document;
        if (!RigComponentPolicyAuthoring.TryApply(current, session.CreateJobToken(), edits, out RiggingSession updated))
            return document;

        var added = edits.Select(edit => edit.EntityId).ToHashSet();
        var evidence = new RigEvidenceReference
        {
            Id = EvidenceId,
            Kind = RigEvidenceKind.ProfileRule,
            ArtifactSha256 = template.SourceFingerprint,
            Description = "Existing-bank default: fitted body joints retain bind position and scale and use bank rotation. Root choices and existing policies take precedence.",
        };
        updated = updated with
        {
            Recipe = updated.Recipe with
            {
                ComponentPolicies = updated.Recipe.ComponentPolicies.Select(policy =>
                {
                    if (!added.Contains(policy.EntityId)) return policy;
                    RigEntityBinding entity = session.Recipe.Entities.Single(entity => entity.EntityId == policy.EntityId);
                    if (RigGripChannelDefaults.IsNativeHandHolder(entity.NativeName))
                    {
                        AnimationComponentPolicy grip = RigGripChannelDefaults.Create(policy.EntityId, template.SourceFingerprint, nativeFrames);
                        return grip with { LodEvidence = grip.LodEvidence.Add(evidence) };
                    }
                    return policy with
                    {
                        Position = policy.Position with { Evidence = [evidence] },
                        Rotation = policy.Rotation with { Evidence = [evidence] },
                        Scale = policy.Scale with { Evidence = [evidence] },
                        LodEvidence = [evidence],
                    };
                }).ToImmutableArray(),
            },
        };
        CustomModelDocument result = current with { RiggingSession = updated, LastBuildReceipt = null };
        result.Validate();
        return result;
    }
}
