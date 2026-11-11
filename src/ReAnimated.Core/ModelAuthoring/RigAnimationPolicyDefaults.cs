using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Completes authoring choices without removing nodes or claiming runtime validation.</summary>
public static class RigAnimationPolicyDefaults
{
    public const string EvidenceId = "automatic-preserve-tracks-v1";
    private const RigAnimationComponents All = RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale;

    public static CustomModelDocument Apply(CustomModelDocument document,
        IReadOnlyDictionary<Guid, (RigAnimationComponents Mask, RigAnimationLod Lod)>? stockDefaults = null,
        IReadOnlySet<Guid>? entityIds = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (document.RiggingSession is not { } session) return document;
        if (!session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidDataException("The rigging session belongs to a different source. Re-match the model before assigning animation defaults.");
        var policies = session.Recipe.ComponentPolicies.ToBuilder();
        var existing = session.Recipe.ComponentPolicies.ToDictionary(policy => policy.EntityId);
        RigEvidenceReference Evidence(string id, string description) => new()
        {
            Id = id, Kind = RigEvidenceKind.ProfileRule, ArtifactSha256 = document.Source.ContentSha256,
            Description = description + " This records authoring intent and source association, not runtime validation.",
        };
        foreach (var node in RiggingSessions.ObserveSourceHierarchy(document))
        {
            if (entityIds is not null && !entityIds.Contains(node.EntityId)) continue;
            existing.TryGetValue(node.EntityId, out var saved);
            var defaults = stockDefaults is not null && stockDefaults.TryGetValue(node.EntityId, out var stock)
                ? stock : (Mask: All, Lod: RigAnimationLod.Off);
            var mask = saved?.EmittedMask ?? defaults.Mask;
            var lod = saved?.AnimationLod ?? defaults.Lod;
            bool hasReference = stockDefaults?.ContainsKey(node.EntityId) == true;
            var channelEvidence = saved?.EmittedMask is not null
                ? Evidence("automatic-preserved-choice-v1", $"Preserved saved channel mask {mask}; completed only missing ownership.")
                : hasReference
                    ? Evidence("automatic-reference-choice-v1", $"Applied supplied exact reference mask {mask}; enabled channels follow clips, omitted channels inherit bind transforms.")
                    : Evidence(EvidenceId, "Unmatched node default: preserve position, rotation and scale tracks; no bone or animation data was removed.");
            var lodEvidence = saved?.AnimationLod is not null
                ? Evidence("automatic-preserved-choice-v1", $"Preserved saved animation LOD {lod}; original supporting evidence was unavailable.")
                : hasReference
                    ? Evidence("automatic-reference-choice-v1", $"Applied supplied exact reference animation LOD {lod}.")
                    : Evidence(EvidenceId, "Unmatched node default: LOD_OFF, retaining tracks without distance-based animation culling.");
            RigChannelOwnership Complete(RigChannelOwnership? channel, RigAnimationComponents component)
            {
                if (channel is not null && !channel.Owners.IsEmpty && !channel.Owners.Contains(RigComponentOwner.Unknown))
                    return channel.Evidence.Any(row => row.ArtifactSha256 is not null) ? channel
                        : channel with { Evidence = channel.Evidence.Add(Evidence("automatic-preserved-choice-v1", "Preserved saved channel owners; original supporting evidence was unavailable.")) };
                return new() { Owners = [mask.HasFlag(component) ? RigComponentOwner.Clip : RigComponentOwner.BindInherited], Evidence = [channelEvidence] };
            }
            var replacement = (saved ?? new() { EntityId = node.EntityId }) with
            {
                EmittedMask = mask, AnimationLod = lod,
                LodRuleId = saved?.LodRuleId ?? EvidenceId,
                LodEvidence = saved is not null && saved.LodEvidence.Any(row => row.ArtifactSha256 is not null)
                    ? saved.LodEvidence : (saved?.LodEvidence ?? []).Add(lodEvidence),
                Position = Complete(saved?.Position, RigAnimationComponents.Position),
                Rotation = Complete(saved?.Rotation, RigAnimationComponents.Rotation),
                Scale = Complete(saved?.Scale, RigAnimationComponents.Scale),
            };
            if (replacement == saved) continue;
            if (saved is null) policies.Add(replacement);
            else policies[policies.IndexOf(saved)] = replacement;
        }
        if (policies.SequenceEqual(session.Recipe.ComponentPolicies)) return document;
        var updated = RiggingSessions.Change(session, session with
        { Recipe = session.Recipe with { ComponentPolicies = policies.ToImmutable() } }, RiggingEditKind.Motion);
        return document with { RiggingSession = updated, LastBuildReceipt = null };
    }
}
