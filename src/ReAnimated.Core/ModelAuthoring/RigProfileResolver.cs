using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>A corrective diagnostic, not a compiler or runtime certification.</summary>
public sealed record RigProfileDiagnostic(
    string Code, RigValidationStatus Status, string Message, string CorrectiveOperation,
    string? CapabilityId = null, string? RoleId = null, Guid? EntityId = null,
    ImmutableArray<string> ConsumerIds = default);

public sealed record RigResolvedRole(RigRuntimeRole Role, bool Required, ImmutableArray<string> ConsumerIds);

public sealed record RigProfileResolution(
    RigProfileReference Profile, ImmutableArray<string> CapabilityIds,
    ImmutableArray<RigResolvedRole> Roles, ImmutableArray<RigProfileDiagnostic> Diagnostics)
{
    public RigValidationStatus Status => Diagnostics.Any(static d => d.Status == RigValidationStatus.Failed)
        ? RigValidationStatus.Failed
        : Diagnostics.Any(static d => d.Status == RigValidationStatus.Unverified)
            ? RigValidationStatus.Unverified : RigValidationStatus.Passed;
}

/// <summary>
/// Resolves only selected capabilities and their dependency closure. It never adds,
/// deletes or renames an imported entity, and a resolved contract is not runtime proof.
/// </summary>
public static class RigProfileResolver
{
    public static RigProfileResolution Resolve(RigCapabilityProfile profile, IEnumerable<string> selectedCapabilityIds,
        IEnumerable<string>? presentRoleIds = null)
    {
        ArgumentNullException.ThrowIfNull(profile); profile.Validate();
        ArgumentNullException.ThrowIfNull(selectedCapabilityIds);
        var capabilities = profile.Capabilities.ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var roles = profile.Roles.ToDictionary(static r => r.Id, StringComparer.Ordinal);
        var selected = new SortedSet<string>(selectedCapabilityIds, StringComparer.Ordinal);
        var diagnostics = new List<RigProfileDiagnostic>();
        var present = (presentRoleIds ?? []).ToHashSet(StringComparer.Ordinal);
        var active = new SortedSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in selected)
        {
            RigContractRules.Text(id, nameof(selectedCapabilityIds));
            VisitCapability(id);
        }

        var included = new Dictionary<string, bool>(StringComparer.Ordinal);
        var roleVisiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (string capabilityId in active)
            foreach (string roleId in capabilities[capabilityId].RoleIds.Order(StringComparer.Ordinal))
                VisitRole(roleId, forced: false);

        foreach (RigConsumerCoverage consumer in profile.Consumers.OrderBy(static c => c.ConsumerId, StringComparer.Ordinal))
        {
            if (!consumer.CapabilityIds.Any(active.Contains)) continue;
            foreach (string roleId in consumer.DiscoveredRoleIds.Order(StringComparer.Ordinal)) VisitRole(roleId, forced: false);
            if (!consumer.Inspected || !consumer.Unknowns.IsEmpty || !HasBuildEvidence(consumer.Evidence))
                diagnostics.Add(new("consumer-coverage-unverified", RigValidationStatus.Unverified,
                    $"Consumer '{consumer.ConsumerId}' has incomplete or unverified dependency coverage.",
                    "Inspect the consumer on the selected build and record unresolved lookups and their role rules.",
                    ConsumerIds: [consumer.ConsumerId]));
        }

        foreach (string capabilityId in active)
            if (!profile.Consumers.Any(c => c.CapabilityIds.Contains(capabilityId, StringComparer.Ordinal)))
                diagnostics.Add(new("capability-consumers-missing", RigValidationStatus.Unverified,
                    $"Capability '{capabilityId}' has no declared consumer coverage.",
                    "Inventory the capability's native consumers before claiming completeness.", capabilityId));

        var consumersByRole = profile.Consumers.Where(c => c.CapabilityIds.Any(active.Contains))
            .SelectMany(c => ConsumerRoles(c).Select(roleId => (RoleId: roleId, c.ConsumerId)))
            .ToLookup(static row => row.RoleId, static row => row.ConsumerId, StringComparer.Ordinal);
        var resolved = ImmutableArray.CreateBuilder<RigResolvedRole>();
        foreach ((string id, bool required) in included.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            RigRuntimeRole role = roles[id];
            ImmutableArray<string> consumers = consumersByRole[id].Order(StringComparer.Ordinal).ToImmutableArray();
            if (role.Requirement == RigRoleRequirementKind.Unknown || !role.RulesComplete || !HasBuildEvidence(role.Evidence))
                diagnostics.Add(new("role-rules-unverified", RigValidationStatus.Unverified,
                    $"Role '{id}' lacks complete build-matched requirement rules.",
                    "Resolve representation, owner, parent, frame, component and retention rules from the native consumer.",
                    RoleId: id, ConsumerIds: consumers));
            if (consumers.IsEmpty && required)
                diagnostics.Add(new("role-consumers-missing", RigValidationStatus.Unverified,
                    $"Required role '{id}' has no inspected consumer dependency path.",
                    "Associate the required role with its native consumer or a consumer's prerequisite role.", RoleId: id));
            resolved.Add(new(role, required, consumers));
        }
        return new(profile.Identity, active.ToImmutableArray(), resolved.ToImmutable(), diagnostics.ToImmutableArray());

        void VisitCapability(string id)
        {
            var pending = new Stack<(string Id, bool Exit)>();
            pending.Push((id, false));
            while (pending.TryPop(out var step))
            {
                string current = step.Id;
                if (step.Exit) { visiting.Remove(current); active.Add(current); continue; }
                if (!capabilities.TryGetValue(current, out RigCapabilityDefinition? capability))
                {
                    diagnostics.Add(new("capability-unknown", RigValidationStatus.Failed,
                        $"Selected capability '{current}' does not exist in the profile.", "Select a declared capability or change the profile.", current));
                    continue;
                }
                if (visiting.Contains(current))
                {
                    diagnostics.Add(new("capability-cycle", RigValidationStatus.Failed,
                        $"Capability dependencies cycle through '{current}'.", "Repair the profile dependency cycle.", current));
                    continue;
                }
                if (active.Contains(current)) continue;
                visiting.Add(current);
                pending.Push((current, true));
                foreach (string prerequisite in capability.PrerequisiteCapabilityIds.OrderDescending(StringComparer.Ordinal)) pending.Push((prerequisite, false));
            }
        }

        void VisitRole(string id, bool forced)
        {
            var pending = new Stack<(string Id, bool Forced, bool Exit)>();
            pending.Push((id, forced, false));
            while (pending.TryPop(out var step))
            {
                string current = step.Id;
                if (step.Exit) { roleVisiting.Remove(current); continue; }
                RigRuntimeRole role = roles[current];
                bool enabled = role.Requirement != RigRoleRequirementKind.Conditional || active.Contains(role.ConditionCapabilityId!);
                if (!enabled)
                {
                    if (step.Forced) diagnostics.Add(new("role-condition-conflict", RigValidationStatus.Failed,
                        $"A required dependency reaches disabled conditional role '{current}'.",
                        "Select its required capability or correct the profile dependency.", RoleId: current));
                    continue;
                }
                bool required = step.Forced || role.Requirement is RigRoleRequirementKind.Required or RigRoleRequirementKind.Conditional;
                if (roleVisiting.Contains(current))
                {
                    diagnostics.Add(new("role-cycle", RigValidationStatus.Failed,
                        $"Role dependencies cycle through '{current}'.", "Repair the role prerequisite or parent cycle.", RoleId: current));
                    continue;
                }
                if (included.TryGetValue(current, out bool previous) && (previous || !required)) continue;
                included[current] = required;
                // An unused optional node does not make its dependent branch mandatory.
                if (!required && !present.Contains(current)) continue;
                roleVisiting.Add(current);
                pending.Push((current, false, true));
                if (role.ParentRoleId is { } parent) pending.Push((parent, true, false));
                foreach (string dependency in role.PrerequisiteRoleIds.OrderDescending(StringComparer.Ordinal)) pending.Push((dependency, true, false));
            }
        }

        bool HasBuildEvidence(ImmutableArray<RigEvidenceReference> evidence) => profile.Identity.BuildFingerprint is not null &&
            evidence.Any(e => e.Kind == RigEvidenceKind.ProfileRule && e.ArtifactSha256 is not null &&
                e.BuildFingerprint is not null && RigContractRules.SameHash(e.BuildFingerprint, profile.Identity.BuildFingerprint));

        IEnumerable<string> ConsumerRoles(RigConsumerCoverage consumer)
        {
            var pending = new Stack<string>(consumer.DiscoveredRoleIds);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryPop(out string? current))
            {
                if (!visited.Add(current) || !included.ContainsKey(current)) continue;
                yield return current;
                if (!included[current] && !present.Contains(current)) continue;
                foreach (string prerequisite in roles[current].PrerequisiteRoleIds) pending.Push(prerequisite);
                if (roles[current].ParentRoleId is { } parent) pending.Push(parent);
            }
        }
    }
}
