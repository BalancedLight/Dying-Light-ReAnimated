using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record RigRoleEditCheck
{
    public bool AllowRemoval { get; init; }
}

public sealed record RigRoleEditConstraint(string RoleId, Guid EntityId, string EntityName,
    RigHelperEditFields AllowedFields, bool AllowRemoval, ImmutableArray<string> ConsumerIds);

public sealed class RigProfileEditException : InvalidOperationException
{
    public RigProfileEditException(ImmutableArray<RigProfileDiagnostic> diagnostics)
        : base("The active profile rejects this edit. " + string.Join(" ", diagnostics.Take(6).Select(d => d.Message)) +
            (diagnostics.Length > 6 ? $" {diagnostics.Length - 6} additional conflicts." : string.Empty) +
            " Review the draft or select a profile permitting these changes in a separate reviewed transaction.") => Diagnostics = diagnostics;
    public ImmutableArray<RigProfileDiagnostic> Diagnostics { get; }
}

/// <summary>Admission for explicitly declared profile edit permissions; independent of helper locks and native acceptance.</summary>
public static class RigProfileEditGuard
{
    public static ImmutableArray<RigRoleEditConstraint> Constraints(RiggingSession? session)
    {
        if (session?.Recipe.ProfileSnapshot is not { } profile) return [];
        var recipe = session.Recipe;
        var resolution = RigProfileResolver.Resolve(profile, recipe.SelectedCapabilityIds, recipe.Assignments.Select(a => a.RoleId));
        return resolution.Roles.Where(r => r.Role.ValidationRules?.Edits is not null)
            .SelectMany(r => recipe.Assignments.Where(a => a.RoleId == r.Role.Id).Select(a => new RigRoleEditConstraint(r.Role.Id, a.EntityId,
                recipe.Entities.Single(e => e.EntityId == a.EntityId).NativeName, r.Role.AllowedEdits, r.Role.ValidationRules!.Edits!.AllowRemoval, r.ConsumerIds)))
            .ToImmutableArray();
    }

    public static void RequireRecipeAllowed(RiggingSession before, RiggingSession after)
    {
        var constraints = Constraints(before); if (constraints.IsEmpty) return;
        var failures = ImmutableArray.CreateBuilder<RigProfileDiagnostic>();
        foreach (var rule in constraints)
        {
            var next = after.Recipe.Entities.FirstOrDefault(e => e.EntityId == rule.EntityId);
            if (next is null) { AddRemoval(rule, failures); continue; }
            RigHelperEditFields changed = RigHelperEditFields.None;
            if (next.NativeName != rule.EntityName) changed |= RigHelperEditFields.Name;
            var oldHelper = before.Recipe.Helpers.FirstOrDefault(h => h.EntityId == rule.EntityId);
            var newHelper = after.Recipe.Helpers.FirstOrDefault(h => h.EntityId == rule.EntityId);
            var oldPolicy = before.Recipe.FramePolicies.FirstOrDefault(p => p.EntityId == rule.EntityId);
            var newPolicy = after.Recipe.FramePolicies.FirstOrDefault(p => p.EntityId == rule.EntityId);
            var oldFrame = oldHelper?.LocalFrame ?? oldPolicy?.SolvedGlobalFrame;
            var newFrame = newHelper?.LocalFrame ?? newPolicy?.SolvedGlobalFrame;
            bool basisChanged = (oldHelper is null) != (newHelper is null) || oldHelper?.FollowPreparedParent != newHelper?.FollowPreparedParent;
            if (basisChanged || (oldHelper?.FramePolicy ?? oldPolicy?.FramePolicy ?? RigFramePolicy.PreserveSource) !=
                (newHelper?.FramePolicy ?? newPolicy?.FramePolicy ?? RigFramePolicy.PreserveSource))
                changed |= RigHelperEditFields.Position | RigHelperEditFields.Orientation;
            changed |= FrameChanges(oldFrame, newFrame);
            if (oldHelper is not null && newHelper is not null && oldHelper.ParentEntityId != newHelper.ParentEntityId)
                changed |= RigHelperEditFields.Parent;
            if ((oldHelper?.BoundsCenter ?? oldPolicy?.BoundsCenter) != (newHelper?.BoundsCenter ?? newPolicy?.BoundsCenter) ||
                (oldHelper?.BoundsHalfExtents ?? oldPolicy?.BoundsHalfExtents) != (newHelper?.BoundsHalfExtents ?? newPolicy?.BoundsHalfExtents) ||
                oldPolicy?.BoundsPolicy != newPolicy?.BoundsPolicy)
                changed |= RigHelperEditFields.Extents;
            var oldComponents = before.Recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == rule.EntityId);
            var newComponents = after.Recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == rule.EntityId);
            if (!SameChannels(oldComponents, newComponents)) changed |= RigHelperEditFields.Channels;
            AddChanges(rule, changed, "stored decisions", failures);
        }
        ThrowIfAny(failures);
    }

    public static void RequireDocumentAllowed(CustomModelDocument before, CustomModelDocument after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        if (before.ModelId != after.ModelId || before.RiggingSession is not { } oldSession) return;
        var constraints = Constraints(oldSession); if (constraints.IsEmpty) return;
        if (after.RiggingSession is { } nextSession) RequireRecipeAllowed(oldSession, nextSession);
        var oldRows = Rows(before); var nextRows = Rows(after);
        var failures = ImmutableArray.CreateBuilder<RigProfileDiagnostic>();
        foreach (var rule in constraints)
        {
            if (!oldRows.TryGetValue(rule.EntityId, out var previous)) continue;
            if (!nextRows.TryGetValue(rule.EntityId, out var next)) { AddRemoval(rule, failures); continue; }
            var changed = FrameChanges(previous.Frame, next.Frame);
            if (previous.Name != next.Name) changed |= RigHelperEditFields.Name;
            if (previous.Parent != next.Parent) changed |= RigHelperEditFields.Parent;
            AddChanges(rule, changed, "authored hierarchy", failures);
        }
        ThrowIfAny(failures);
    }

    public static RigHelperEditFields FrameChanges(TransformMatrix? before, TransformMatrix? after)
    {
        if (before is null || after is null) return before == after ? RigHelperEditFields.None : RigHelperEditFields.Position | RigHelperEditFields.Orientation;
        var a = before.Value; var b = after.Value; var fields = RigHelperEditFields.None;
        if (Vector3D.Distance(a.Translation, b.Translation) > 1e-10) fields |= RigHelperEditFields.Position;
        if (!(a with { M14 = 0, M24 = 0, M34 = 0 }).NearlyEquals(b with { M14 = 0, M24 = 0, M34 = 0 }, 1e-10)) fields |= RigHelperEditFields.Orientation;
        return fields;
    }

    public static void AddChanges(RigRoleEditConstraint rule, RigHelperEditFields changed, string layer, ImmutableArray<RigProfileDiagnostic>.Builder failures)
    {
        var forbidden = changed & ~rule.AllowedFields;
        if (forbidden != RigHelperEditFields.None)
            failures.Add(new("profile-edit-fields-denied", RigValidationStatus.Failed,
                $"{rule.RoleId} / {rule.EntityName}: {layer} changes protected {forbidden} fields.",
                "Review the profile's allowed authoring edits or keep these fields unchanged.", RoleId: rule.RoleId, EntityId: rule.EntityId, ConsumerIds: rule.ConsumerIds));
    }

    public static void AddRemoval(RigRoleEditConstraint rule, ImmutableArray<RigProfileDiagnostic>.Builder failures)
    {
        if (!rule.AllowRemoval) failures.Add(new("profile-edit-removal-denied", RigValidationStatus.Failed,
            $"{rule.RoleId} / {rule.EntityName}: removal is protected by the profile.",
            "Keep the entity or review a profile revision allowing removal.", RoleId: rule.RoleId, EntityId: rule.EntityId, ConsumerIds: rule.ConsumerIds));
    }

    public static void ThrowIfAny(ImmutableArray<RigProfileDiagnostic>.Builder failures)
    { if (failures.Count > 0) throw new RigProfileEditException(failures.ToImmutable()); }

    private static Dictionary<Guid, (string Name, Guid? Parent, TransformMatrix Frame)> Rows(CustomModelDocument document)
    {
        if (document.RiggingSession is null) return [];
        var observed = RiggingSessions.ObserveSourceHierarchy(document); var bones = document.CreateEffectiveBones();
        return observed.Select((o, i) => (o.EntityId, Value: (bones[i].Name, o.ParentEntityId, bones[i].ExactLocalBindMatrix)))
            .ToDictionary(p => p.EntityId, p => p.Value);
    }
    private static bool SameChannels(AnimationComponentPolicy? a, AnimationComponentPolicy? b) =>
        a is null || b is null ? a == b : a.EmittedMask == b.EmittedMask && a.AnimationLod == b.AnimationLod &&
            a.Position.Owners.SequenceEqual(b.Position.Owners) && a.Rotation.Owners.SequenceEqual(b.Rotation.Owners) && a.Scale.Owners.SequenceEqual(b.Scale.Owners);
}
