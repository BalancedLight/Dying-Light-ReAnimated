using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>An explicit authoring choice for one observed rig entity's channels and LOD.</summary>
public sealed record RigComponentPolicyEdit(
    Guid EntityId,
    RigAnimationComponents Mask,
    RigAnimationLod Lod,
    RigComponentOwner? PositionOwner,
    RigComponentOwner? RotationOwner,
    RigComponentOwner? ScaleOwner);

/// <summary>Applies reviewed component/LOD choices without fabricating native validation.</summary>
public static class RigComponentPolicyAuthoring
{
    private const RigAnimationComponents AllComponents =
        RigAnimationComponents.Position |
        RigAnimationComponents.Rotation |
        RigAnimationComponents.Scale;

    public static bool TryApply(
        CustomModelDocument document,
        RiggingJobToken token,
        IReadOnlyList<RigComponentPolicyEdit> edits,
        out RiggingSession result)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(edits);
        document.Validate();
        result = document.RiggingSession ?? throw new InvalidOperationException("A rigging session is required.");
        result.Validate();
        if (edits.Count == 0)
        {
            throw new ArgumentException("At least one component policy edit is required.", nameof(edits));
        }

        if (!result.Matches(token))
        {
            return false;
        }

        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        var observedIds = observed.Select(static row => row.EntityId).ToHashSet();
        var entities = result.Recipe.Entities.ToDictionary(static entity => entity.EntityId);
        var editIds = new HashSet<Guid>();
        foreach (RigComponentPolicyEdit edit in edits)
        {
            if (edit.EntityId == Guid.Empty || !editIds.Add(edit.EntityId))
            {
                throw new ArgumentException("Component policy edits require unique non-empty entity IDs.", nameof(edits));
            }

            if (!Enum.IsDefined(edit.Lod) || (edit.Mask & ~AllComponents) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(edits), "Component policy masks and LODs must be defined values.");
            }

            ValidateOwner(edit.PositionOwner);
            ValidateOwner(edit.RotationOwner);
            ValidateOwner(edit.ScaleOwner);
            if (!observedIds.Contains(edit.EntityId) ||
                !entities.TryGetValue(edit.EntityId, out RigEntityBinding? entity) ||
                entity.OwnerAssetId != document.ModelId)
            {
                throw new InvalidDataException("Component policy edits must target observed entities owned by this model.");
            }
        }

        var componentPolicies = result.Recipe.ComponentPolicies.ToBuilder();
        foreach (RigComponentPolicyEdit edit in edits.OrderBy(static edit => edit.EntityId))
        {
            AnimationComponentPolicy? existing = result.Recipe.ComponentPolicies.FirstOrDefault(
                policy => policy.EntityId == edit.EntityId);
            RigChannelOwnership position = ApplyChannel(
                existing?.Position,
                edit.PositionOwner,
                document.Source.ContentSha256,
                edit.EntityId,
                "position");
            RigChannelOwnership rotation = ApplyChannel(
                existing?.Rotation,
                edit.RotationOwner,
                document.Source.ContentSha256,
                edit.EntityId,
                "rotation");
            RigChannelOwnership scale = ApplyChannel(
                existing?.Scale,
                edit.ScaleOwner,
                document.Source.ContentSha256,
                edit.EntityId,
                "scale");
            bool lodChanged = existing is null || existing.AnimationLod != edit.Lod ||
                string.IsNullOrWhiteSpace(existing.LodRuleId) || !HasArtifactEvidence(existing.LodEvidence);
            AnimationComponentPolicy updated = existing is null
                ? new AnimationComponentPolicy
                {
                    EntityId = edit.EntityId,
                    EmittedMask = edit.Mask,
                    Position = position,
                    Rotation = rotation,
                    Scale = scale,
                    AnimationLod = edit.Lod,
                    LodRuleId = "authoring-lod-selection-v1",
                    LodEvidence = [AuthoringEvidence(document.Source.ContentSha256, edit.EntityId, "lod")],
                }
                : existing with
                {
                    EmittedMask = edit.Mask,
                    Position = position,
                    Rotation = rotation,
                    Scale = scale,
                    AnimationLod = edit.Lod,
                    LodRuleId = lodChanged ? "authoring-lod-selection-v1" : existing.LodRuleId,
                    LodEvidence = lodChanged
                        ? [AuthoringEvidence(document.Source.ContentSha256, edit.EntityId, "lod")]
                        : existing.LodEvidence,
                };
            int index = existing is null ? -1 : componentPolicies.IndexOf(existing);
            if (index >= 0)
            {
                componentPolicies[index] = updated;
            }
            else
            {
                componentPolicies.Add(updated);
            }
        }

        ImmutableArray<AnimationComponentPolicy> updatedPolicies = componentPolicies.ToImmutable();
        if (updatedPolicies.SequenceEqual(result.Recipe.ComponentPolicies))
        {
            return true;
        }

        RiggingSession replacement = result with
        {
            Recipe = result.Recipe with { ComponentPolicies = updatedPolicies },
        };
        result = RiggingSessions.Change(result, replacement, RiggingEditKind.Motion);
        return true;
    }

    private static RigChannelOwnership ApplyChannel(
        RigChannelOwnership? existing,
        RigComponentOwner? owner,
        string sourceSha256,
        Guid entityId,
        string channel)
    {
        if (owner is null)
        {
            if (existing is null || !IsComplete(existing))
            {
                throw new InvalidDataException(
                    $"The existing {channel} ownership for entity {entityId:N} is incomplete and cannot be preserved.");
            }

            return existing;
        }

        if (existing is not null &&
            existing.Owners.Length == 1 &&
            existing.Owners[0] == owner &&
            existing.CompositionRuleId is null &&
            IsComplete(existing))
        {
            return existing;
        }

        return new RigChannelOwnership
        {
            Owners = [owner.Value],
            Evidence = [AuthoringEvidence(sourceSha256, entityId, channel)],
        };
    }

    private static bool IsComplete(RigChannelOwnership ownership) =>
        !ownership.Owners.IsDefaultOrEmpty &&
        ownership.Owners.All(static owner => owner != RigComponentOwner.Unknown) &&
        ownership.Owners.Distinct().Count() == ownership.Owners.Length &&
        (ownership.Owners.Length <= 1 || !string.IsNullOrWhiteSpace(ownership.CompositionRuleId)) &&
        HasArtifactEvidence(ownership.Evidence);

    private static bool HasArtifactEvidence(ImmutableArray<RigEvidenceReference> evidence) =>
        !evidence.IsDefaultOrEmpty && evidence.Any(static row => row.ArtifactSha256 is not null);

    private static RigEvidenceReference AuthoringEvidence(
        string sourceSha256,
        Guid entityId,
        string channel) =>
        new()
        {
            Id = $"authoring-choice:{entityId:N}:{channel}",
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = sourceSha256,
            Description = "authoring choice; native runtime behavior unverified",
        };

    private static void ValidateOwner(RigComponentOwner? owner)
    {
        if (owner is { } value && (!Enum.IsDefined(value) || value == RigComponentOwner.Unknown))
        {
            throw new ArgumentOutOfRangeException(nameof(owner), "A changed channel owner must be a defined non-Unknown owner.");
        }
    }
}
