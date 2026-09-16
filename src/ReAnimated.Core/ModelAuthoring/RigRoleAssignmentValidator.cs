using System.Collections.Immutable;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>A source/compiled hierarchy observation keyed by semantic identity, not a node index.</summary>
public sealed record RigParentObservation(Guid EntityId, Guid? ParentEntityId);

public sealed record RigRoleAssignmentAssessment(RigProfileResolution Resolution, ImmutableArray<RigProfileDiagnostic> Diagnostics)
{
    public RigValidationStatus Status => Diagnostics.Any(static d => d.Status == RigValidationStatus.Failed)
        ? RigValidationStatus.Failed : Diagnostics.Any(static d => d.Status == RigValidationStatus.Unverified)
            ? RigValidationStatus.Unverified : RigValidationStatus.Passed;
}

/// <summary>
/// Checks semantic assignments and observed hierarchy without rewriting the source rig.
/// Success covers these checks only; frame/component, compiled and live facets remain separate.
/// </summary>
public static class RigRoleAssignmentValidator
{
    public static RigRoleAssignmentAssessment Validate(RigCapabilityProfile profile, RuntimeRigRecipe recipe,
        ImmutableArray<RigParentObservation> hierarchy)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recipe); recipe.Validate();
        RigContractRules.Array(hierarchy, nameof(hierarchy));
        RigProfileResolution resolution = RigProfileResolver.Resolve(profile, recipe.SelectedCapabilityIds,
            recipe.Assignments.Select(static a => a.RoleId));
        var diagnostics = resolution.Diagnostics.ToList();
        if (recipe.Profile is not { } selected || selected.Id != profile.Identity.Id || selected.Version != profile.Identity.Version ||
            !RigContractRules.SameHash(selected.ContentSha256, profile.Identity.ContentSha256) ||
            !RigContractRules.SameHash(selected.BuildFingerprint, profile.Identity.BuildFingerprint))
            diagnostics.Add(new("profile-identity-mismatch", RigValidationStatus.Failed,
                "The recipe's selected profile does not match the evaluated profile content and build.",
                "Review and migrate the recipe against the selected profile before validating its assignments."));

        var entities = recipe.Entities.ToDictionary(static e => e.EntityId);
        var owners = recipe.AssetRoles.ToDictionary(static a => a.RoleId, static a => a.AssetId, StringComparer.Ordinal);
        var assignments = recipe.Assignments.ToLookup(static a => a.RoleId, StringComparer.Ordinal);
        var parents = new Dictionary<Guid, Guid?>();
        foreach (RigParentObservation observation in hierarchy)
        {
            if (!entities.TryGetValue(observation.EntityId, out RigEntityBinding? childEntity) ||
                observation.ParentEntityId is { } parent && (!entities.TryGetValue(parent, out RigEntityBinding? parentEntity) ||
                    parentEntity.OwnerAssetId != childEntity.OwnerAssetId) ||
                !parents.TryAdd(observation.EntityId, observation.ParentEntityId))
                diagnostics.Add(new("hierarchy-observation-invalid", RigValidationStatus.Failed,
                    "A hierarchy observation is duplicated, references an absent entity or crosses owning assets.",
                    "Read the hierarchy from the current owning asset using stable entity identities.", EntityId: observation.EntityId));
        }
        var inspected = new HashSet<Guid>();
        foreach (Guid entityId in parents.Keys.Order())
        {
            Guid current = entityId;
            var path = new HashSet<Guid>();
            while (!inspected.Contains(current) && parents.TryGetValue(current, out Guid? parent))
            {
                if (!path.Add(current))
                {
                    diagnostics.Add(new("hierarchy-cycle", RigValidationStatus.Failed,
                        "The observed hierarchy contains a cycle.", "Repair the parent links before preparing or exporting the rig.", EntityId: current));
                    break;
                }
                if (parent is null) break;
                current = parent.Value;
            }
            inspected.UnionWith(path);
        }

        foreach (RigRoleAssignment assignment in recipe.Assignments)
            if (!profile.Roles.Any(r => r.Id == assignment.RoleId))
                diagnostics.Add(new("role-assignment-unknown", RigValidationStatus.Failed,
                    $"Assigned role '{assignment.RoleId}' is not declared by the selected profile.",
                    "Migrate the role assignment while retaining the source entity.", RoleId: assignment.RoleId, EntityId: assignment.EntityId));

        foreach (RigResolvedRole resolved in resolution.Roles)
        {
            RigRuntimeRole role = resolved.Role;
            RigRoleAssignment[] matches = assignments[role.Id].ToArray();
            int minimum = resolved.Required ? Math.Max(1, role.MinimumCount) : matches.Length == 0 ? 0 : role.MinimumCount;
            if (matches.Length < minimum || role.MaximumCount is { } maximum && matches.Length > maximum)
                Add("role-multiplicity", RigValidationStatus.Failed,
                    $"Role '{role.Id}' has {matches.Length} assigned entities; expected at least {minimum}" +
                    (role.MaximumCount is { } max ? $" and at most {max}." : "."),
                    "Assign or create the missing role entities, or resolve competing assignments without deleting imported extras.");
            foreach (RigRoleAssignment match in matches)
            {
                RigEntityBinding entity = entities[match.EntityId];
                if (role.OwnerAssetRoleId is null || !owners.TryGetValue(role.OwnerAssetRoleId, out Guid owner))
                    Add("role-owner-unresolved", RigValidationStatus.Unverified, $"Role '{role.Id}' has no resolved owning asset.",
                        "Bind the semantic asset role to the character, equipment or camera asset that owns it.", entity.EntityId);
                else if (entity.OwnerAssetId != owner)
                    Add("role-owner-conflict", RigValidationStatus.Failed, $"Role '{role.Id}' is assigned to an entity in another asset.",
                        "Assign the role to the entity in its declared owning asset.", entity.EntityId);
                if (role.EntityKind == RigNativeEntityKind.Unknown || entity.Kind == RigNativeEntityKind.Unknown)
                    Add("role-type-unverified", RigValidationStatus.Unverified, $"Role '{role.Id}' has an unresolved native entity type.",
                        "Inspect and record the native entity representation.", entity.EntityId);
                else if (role.EntityKind != entity.Kind)
                    Add("role-type-conflict", RigValidationStatus.Failed, $"Role '{role.Id}' expects {role.EntityKind}, but the assigned entity is {entity.Kind}.",
                        "Choose an entity with the required native representation.", entity.EntityId);
                if (role.NativeName is not null && !string.Equals(role.NativeName, entity.NativeName, StringComparison.Ordinal) &&
                    !role.Aliases.Any(a => string.Equals(a.Name, entity.NativeName, StringComparison.Ordinal) &&
                        a.Evidence.Any(e => e.Kind == RigEvidenceKind.ProfileRule && e.ArtifactSha256 is not null && e.BuildFingerprint is not null &&
                            RigContractRules.SameHash(e.BuildFingerprint, profile.Identity.BuildFingerprint))))
                    Add("role-native-name-conflict", RigValidationStatus.Failed, $"Entity '{entity.NativeName}' does not match the native identity for role '{role.Id}'.",
                        "Use the required spelling or a build-verified alias; do not normalize native names speculatively.", entity.EntityId);
                if (role.ParentConstraint == RigRoleParentConstraint.Unspecified) continue;
                var expectedParents = assignments[role.ParentRoleId!].Select(static a => a.EntityId).ToHashSet();
                Guid current = entity.EntityId;
                var visited = new HashSet<Guid>();
                while (true)
                {
                    if (!visited.Add(current))
                    {
                        Add("role-parent-cycle", RigValidationStatus.Failed, $"Role '{role.Id}' lies in a cyclic hierarchy.",
                            "Repair the hierarchy cycle before export.", entity.EntityId); break;
                    }
                    if (!parents.TryGetValue(current, out Guid? parent))
                    {
                        Add("role-parent-unverified", RigValidationStatus.Unverified, $"Parentage for role '{role.Id}' has not been observed completely.",
                            "Read the current source or compiled hierarchy before validating the parent constraint.", entity.EntityId); break;
                    }
                    if (parent is { } parentId && expectedParents.Contains(parentId)) break;
                    if (parent is null || role.ParentConstraint == RigRoleParentConstraint.Direct)
                    {
                        Add("role-parent-conflict", RigValidationStatus.Failed, $"Role '{role.Id}' does not satisfy its {role.ParentConstraint} parent role '{role.ParentRoleId}'.",
                            "Reparent the role using its profile rule while preserving its intended world frame.", entity.EntityId); break;
                    }
                    current = parent.Value;
                }
            }

            void Add(string code, RigValidationStatus status, string message, string correctiveOperation, Guid? entityId = null) =>
                diagnostics.Add(new(code, status, message, correctiveOperation, RoleId: role.Id, EntityId: entityId, ConsumerIds: resolved.ConsumerIds));
        }
        return new(resolution, diagnostics.ToImmutableArray());
    }
}
