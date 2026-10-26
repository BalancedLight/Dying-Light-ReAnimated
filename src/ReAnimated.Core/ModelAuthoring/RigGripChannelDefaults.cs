using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Animation-bank defaults for the two native hand attachment nodes.</summary>
public static class RigGripChannelDefaults
{
    public const string EvidenceId = "bank-grip-rotation-refresh";
    public const string NativeFrameEvidenceId = "bank-grip-native-frames";
    public const double NativeFrameTolerance = 5e-5;

    public static bool IsNativeHandHolder(string name) =>
        name.Equals("l_handholder", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("r_handholder", StringComparison.OrdinalIgnoreCase);

    public static CustomModelDocument Apply(
        CustomModelDocument document,
        RiggingJobToken token,
        IEnumerable<Guid> entityIds,
        bool preferNativePositionTracks = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(entityIds);
        document.Validate();
        RiggingSession session = document.RiggingSession
            ?? throw new InvalidOperationException("Start the model's rig setup before setting grip animation channels.");
        if (!session.Matches(token))
            throw new InvalidOperationException("The hand setup changed; refresh it before setting grip animation channels.");
        var constraints = RigProfileEditGuard.Constraints(session);
        var policies = session.Recipe.ComponentPolicies.ToBuilder();
        bool changed = false;
        foreach (Guid id in entityIds.Distinct())
        {
            RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(entity => entity.EntityId == id);
            if (entity is null || entity.OwnerAssetId != document.ModelId)
                throw new ArgumentException("Grip animation channels require an entity owned by this model.", nameof(entityIds));
            if (!IsNativeHandHolder(entity.NativeName)) continue;
            HelperRecipe? helper = session.Recipe.Helpers.FirstOrDefault(helper => helper.EntityId == id);
            if (helper?.LockedFields.HasFlag(RigHelperEditFields.Channels) == true ||
                constraints.Any(constraint => constraint.EntityId == id &&
                    !constraint.AllowedFields.HasFlag(RigHelperEditFields.Channels)))
                continue;
            AnimationComponentPolicy? current = session.Recipe.ComponentPolicies.FirstOrDefault(policy => policy.EntityId == id);
            if (current is not null && (HasUserDecision(current) || HasComposition(current) || MatchesDefault(current, preferNativePositionTracks)))
                continue;
            AnimationComponentPolicy replacement = Create(id,
                document.RigConformance?.TemplateFingerprint ?? document.Source.ContentSha256,
                preferNativePositionTracks);
            int index = current is null ? -1 : policies.IndexOf(current);
            if (index < 0) policies.Add(replacement);
            else policies[index] = replacement;
            changed = true;
        }
        if (!changed) return document;
        RiggingSession updated = session with
        {
            Recipe = session.Recipe with { ComponentPolicies = policies.ToImmutable() },
        };
        updated = RiggingSessions.Change(session, updated, RiggingEditKind.Motion);
        CustomModelDocument result = document with { RiggingSession = updated, LastBuildReceipt = null };
        result.Validate();
        return result;
    }

    public static bool MatchesNativeFrames(CustomModelDocument document, Dl1RigTemplate reference)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reference);
        var bones = document.CreateEffectiveBones();
        var byName = bones.ToDictionary(static bone => bone.Name, StringComparer.OrdinalIgnoreCase);
        var referenceNames = reference.Entities.Select(static entity => entity.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (bones.Any(bone => IsNativeHandHolder(bone.Name) && !referenceNames.Contains(bone.Name))) return false;
        Dictionary<string, Guid>? ids = document.RiggingSession is null ? null
            : RiggingSessions.ObserveSourceHierarchy(document).Select((node, index) => (bones[index].Name, node.EntityId))
                .ToDictionary(static node => node.Name, static node => node.EntityId, StringComparer.OrdinalIgnoreCase);
        foreach (Dl1RigTemplateEntity target in reference.Entities)
        {
            if (!byName.TryGetValue(target.Name, out var bone) ||
                !bone.ExactLocalBindMatrix.NearlyEquals(target.LocalRestMatrix, NativeFrameTolerance))
                return false;
            string? parent = bone.ParentIndex < 0 ? null : bones[bone.ParentIndex].Name;
            string? expectedParent = target.ParentIndex < 0 ? null : reference[target.ParentIndex].Name;
            if (!string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase)) return false;
            if (ids is null || !ids.TryGetValue(bone.Name, out Guid id)) continue;
            HelperRecipe? helper = document.RiggingSession!.Recipe.Helpers.FirstOrDefault(helper => helper.EntityId == id);
            if (helper is not null && !helper.LocalFrame.NearlyEquals(target.LocalRestMatrix, NativeFrameTolerance))
                return false;
            RigEntityFramePolicy? frame = document.RiggingSession.Recipe.FramePolicies.FirstOrDefault(frame => frame.EntityId == id);
            if (frame?.SolvedGlobalFrame is { } solved && !solved.NearlyEquals(target.GlobalRestMatrix, NativeFrameTolerance))
                return false;
        }
        return true;
    }

    internal static AnimationComponentPolicy Create(Guid entityId, string artifactSha256, bool preferNativePositionTracks = false)
    {
        RigEvidenceReference evidence = new()
        {
            Id = preferNativePositionTracks ? NativeFrameEvidenceId : EvidenceId,
            Kind = RigEvidenceKind.ProfileRule,
            ArtifactSha256 = artifactSha256,
            Description = preferNativePositionTracks
                ? "Hand holders matching the native frames use animation-bank position and rotation while retaining bind scale."
                : "Native hand holders retain calibrated bind position and scale while their rotation follows the animation bank.",
        };
        return new()
        {
            EntityId = entityId,
            Position = new() { Owners = [preferNativePositionTracks ? RigComponentOwner.Clip : RigComponentOwner.BindInherited], Evidence = [evidence] },
            Rotation = new() { Owners = [RigComponentOwner.Clip], Evidence = [evidence] },
            Scale = new() { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] },
            EmittedMask = RigAnimationComponents.Rotation |
                (preferNativePositionTracks ? RigAnimationComponents.Position : RigAnimationComponents.None),
            AnimationLod = RigAnimationLod.Off,
            LodRuleId = evidence.Id,
            LodEvidence = [evidence],
        };
    }

    private static bool HasUserDecision(AnimationComponentPolicy policy) =>
        policy.Position.Evidence.Concat(policy.Rotation.Evidence).Concat(policy.Scale.Evidence)
            .Concat(policy.LodEvidence).Any(evidence => evidence.Kind == RigEvidenceKind.UserOverride);

    private static bool HasComposition(AnimationComponentPolicy policy) =>
        new[] { policy.Position, policy.Rotation, policy.Scale }.Any(channel =>
            channel.CompositionRuleId is not null || channel.Owners.Length > 1);

    private static bool MatchesDefault(AnimationComponentPolicy policy, bool preferNativePositionTracks) =>
        policy.EmittedMask == (RigAnimationComponents.Rotation |
            (preferNativePositionTracks ? RigAnimationComponents.Position : RigAnimationComponents.None)) &&
        policy.AnimationLod == RigAnimationLod.Off &&
        policy.Position.Owners.SequenceEqual([preferNativePositionTracks ? RigComponentOwner.Clip : RigComponentOwner.BindInherited]) &&
        policy.Rotation.Owners.SequenceEqual([RigComponentOwner.Clip]) &&
        policy.Scale.Owners.SequenceEqual([RigComponentOwner.BindInherited]);
}
