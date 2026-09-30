using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Codecs.Fbx;

public sealed record CapabilityRoleReview(string Id, string NativeName, string Category, string Requirement,
    bool Active, bool Required, int AssignedCount, string Status, string Details, ImmutableArray<Guid> EntityIds);

public sealed record CapabilityProfileReview(ImmutableArray<CapabilityRoleReview> Roles, ImmutableArray<RigProfileDiagnostic> Diagnostics,
    ImmutableArray<string> EffectiveCapabilities, int PreservedUnassignedNodes)
{
    public ImmutableArray<RigRoleCheckedRule> PassedRuleChecks { get; init; } = [];
}

public sealed class CapabilityProfilePreview
{
    internal CapabilityProfilePreview(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult candidate, CapabilityProfileReview review)
    { Source = source; Candidate = candidate; Review = review; }
    internal FbxModelAuthoringImportResult Source { get; }
    public FbxModelAuthoringImportResult Candidate { get; }
    public CapabilityProfileReview Review { get; }
    public bool HasChanges => !ReferenceEquals(Source, Candidate);
}

/// <summary>Explicit capability/role decisions. Never creates or prunes nodes from role names.</summary>
public static class FbxCapabilityProfileAuthoring
{
    public static ImmutableArray<RigProfileDiagnostic> ValidateExport(FbxModelAuthoringImportResult model, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); token.ThrowIfCancellationRequested();
        var recipe = model.Package.Document.RiggingSession?.Recipe;
        if (recipe?.Profile is null) return [];
        if (recipe.ProfileSnapshot is null)
            return [new("profile-definition-unavailable", RigValidationStatus.Unverified,
                "The selected capability profile has no saved definition; its required roles were not checked.",
                "Load and review the exact profile definition before capability acceptance.")];
        var report = Inspect(model, token);
        var failures = report.Diagnostics.Where(d => d.Status == RigValidationStatus.Failed).ToArray();
        if (failures.Length > 0)
            throw new InvalidDataException("The selected capability profile blocks export: " + string.Join(" ", failures.Select(d => d.Message + " " + d.CorrectiveOperation)));
        return report.Diagnostics;
    }

    public static CapabilityProfilePreview Preview(FbxModelAuthoringImportResult model, RigCapabilityProfile profile,
        ImmutableArray<string> capabilities, ImmutableArray<RigRoleAssignment> assignments,
        ImmutableArray<RigAssetRoleBinding> owners, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); token.ThrowIfCancellationRequested();
        RigCapabilityProfileSerializer.Verify(profile);
        var doc = model.Package.Document;
        var session = doc.RiggingSession ?? throw new InvalidOperationException("Start a Studio session before selecting runtime capabilities.");
        var recipe = session.Recipe with { Profile = profile.Identity, ProfileSnapshot = profile,
            SelectedCapabilityIds = capabilities, Assignments = assignments, AssetRoles = owners };
        recipe.Validate();
        var updated = RiggingSessions.Change(session, session with { Recipe = recipe }, RiggingEditKind.Profile);
        doc = doc with { RiggingSession = updated, LastBuildReceipt = null }; doc.Validate();
        var candidate = model with { Package = model.Package with { Document = doc } };
        return new(model, candidate, Inspect(candidate, token));
    }

    public static CapabilityProfileReview Inspect(FbxModelAuthoringImportResult model, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); token.ThrowIfCancellationRequested();
        var doc = model.Package.Document; doc.Validate();
        var recipe = doc.RiggingSession?.Recipe ?? throw new InvalidOperationException("No Studio session is available.");
        var profile = recipe.ProfileSnapshot ?? throw new InvalidOperationException("The saved profile has no portable definition. Load its exact reviewed profile file.");
        var assessment = RigRoleAssignmentValidator.Validate(profile, recipe, RiggingSessions.ObserveSourceHierarchy(doc));
        var diagnostics = assessment.Diagnostics.ToBuilder();
        var prepared = model.Rig is null ? null : Dl1CustomModelRigPreparer.Prepare(model, token);
        var ruleAssessment = prepared is null ? new RigRoleRuleAssessment(
            [new("profile-prepared-rules-unverified", RigValidationStatus.Unverified, "No prepared animation hierarchy is available for role checks.", "Prepare the owning rig before frame, channel and retention acceptance.")], [])
            : Dl1CapabilityRuleValidator.Assess(doc, prepared.Contract, token);
        diagnostics.AddRange(ruleAssessment.Diagnostics);
        var usage = FbxStructuralHelperAuthoring.Inspect(model, prepared, token).ToDictionary(r => r.EntityId);
        var active = assessment.Resolution.Roles.ToDictionary(r => r.Role.Id, StringComparer.Ordinal);
        var rows = ImmutableArray.CreateBuilder<CapabilityRoleReview>();
        foreach (var role in profile.Roles.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var assigned = recipe.Assignments.Where(a => a.RoleId == role.Id).Select(a => a.EntityId).ToImmutableArray();
            bool enabled = active.TryGetValue(role.Id, out var resolved);
            if (enabled)
            {
                foreach (Guid id in assigned)
                {
                    if (!usage.TryGetValue(id, out var node))
                        diagnostics.Add(new("role-usage-unobserved", RigValidationStatus.Unverified,
                            $"Weight/frame use for role '{role.Id}' was not observed in this model.", "Inspect its owning asset separately.", RoleId: role.Id, EntityId: id));
                    else if (role.SkinInfluenceAllowed == false && node.WeightedCorners > 0)
                        diagnostics.Add(new("role-weight-conflict", RigValidationStatus.Failed,
                            $"Role '{role.Id}' forbids skin influences but '{node.Name}' affects {node.WeightedCorners} draw corners.",
                            "Review the role assignment or correct its weights with the skin tools; no weights were changed.", RoleId: role.Id, EntityId: id));
                    else if (role.SkinInfluenceAllowed is null)
                        diagnostics.Add(new("role-weight-rule-unverified", RigValidationStatus.Unverified,
                            $"Role '{role.Id}' has no declared skin-weight eligibility rule.", "Resolve the native consumer's weight rule before acceptance.", RoleId: role.Id, EntityId: id));
                }
            }
            var issues = diagnostics.Where(d => d.RoleId == role.Id).ToArray();
            string status = !enabled ? "Not selected" : issues.Any(d => d.Status == RigValidationStatus.Failed) ? "Needs repair"
                : issues.Any(d => d.Status == RigValidationStatus.Unverified) ? "Needs evidence" : "Assignment checks passed";
            string framePolicy = role.ValidationRules?.Frame is { } frameCheck ? string.Join(", ", frameCheck.AllowedPolicies) : role.FramePolicy + " (hint; no executable frame check)";
            string editPolicy = role.ValidationRules?.Edits is { } edits ? $"Allowed authoring fields: {role.AllowedEdits}; removal {(edits.AllowRemoval ? "allowed" : "protected")}." : "Edit permissions undeclared; legacy authoring behavior is retained.";
            string details = editPolicy + "\n" + $"Owner: {role.OwnerAssetRoleId ?? "unresolved"}; parent: {role.ParentRoleId ?? "undeclared"} ({role.ParentConstraint}).\n" +
                $"Frame policies: {framePolicy}; frame rule ID: {role.FrameRuleId ?? "unresolved"}; component rule ID: {role.ComponentRuleId ?? "unresolved"}; retention rule ID: {role.RetentionRuleId ?? "unresolved"}.\n" +
                $"Skin influences: {(role.SkinInfluenceAllowed is null ? "unresolved" : role.SkinInfluenceAllowed.Value ? "allowed by declared profile" : "forbidden by declared profile")}.\n" +
                string.Join("\n", ruleAssessment.PassedChecks.Where(c => c.RoleId == role.Id).Select(c => c.Check + ": " + c.Observation)) + "\n" +
                string.Join("\n", issues.Select(d => d.Message + " " + d.CorrectiveOperation));
            rows.Add(new(role.Id, role.NativeName ?? "Unresolved native name", role.Category.ToString(), role.Requirement.ToString(),
                enabled, resolved?.Required ?? false, assigned.Length, status, details, assigned));
        }
        var declared = profile.Roles.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var previous in recipe.Assignments.Where(a => !declared.Contains(a.RoleId)).GroupBy(a => a.RoleId, StringComparer.Ordinal))
        {
            string details = "This saved assignment is not declared by the loaded profile. Assign the node to a current role or remove only the old assignment; the node itself is retained.\n" +
                string.Join("\n", diagnostics.Where(d => d.RoleId == previous.Key).Select(d => d.Message));
            rows.Add(new(previous.Key, "Previous profile assignment", "Unresolved", "Not declared", false, false,
                previous.Count(), "Needs migration", details, previous.Select(a => a.EntityId).ToImmutableArray()));
        }
        return new(rows.ToImmutable(), diagnostics.ToImmutable(), assessment.Resolution.CapabilityIds,
            recipe.Entities.Count(e => !recipe.Assignments.Any(a => a.EntityId == e.EntityId))) { PassedRuleChecks = ruleAssessment.PassedChecks };
    }

    public static CapabilityProfilePreview? RefreshMetadata(CapabilityProfilePreview preview, FbxModelAuthoringImportResult current)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(current);
        var before = preview.Source; var a = before.Package.Document; var b = current.Package.Document;
        if (a.RiggingSession is not { } oldSession || b.RiggingSession is not { } next || !next.Matches(oldSession.CreateJobToken()) ||
            oldSession with { Stage = next.Stage } != next || a with { RiggingSession = next } != b ||
            before.Surfaces != current.Surfaces || before.AnimationClips != current.AnimationClips || !ReferenceEquals(before.Rig, current.Rig) ||
            before.Package.SourceFbx != current.Package.SourceFbx || before.Package.AuthoredLayerPayload != current.Package.AuthoredLayerPayload ||
            before.Package.TexturePayloads != current.Package.TexturePayloads) return null;
        var candidate = preview.HasChanges ? preview.Candidate with { Package = preview.Candidate.Package with
        { Document = preview.Candidate.Package.Document with { RiggingSession = RiggingSessions.Navigate(preview.Candidate.Package.Document.RiggingSession!, next.Stage) } } } : current;
        return new(current, candidate, preview.Review);
    }

    public static bool TryApply(FbxModelAuthoringImportResult current, CapabilityProfilePreview preview, out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(preview);
        result = current;
        if (!ReferenceEquals(current, preview.Source)) return false;
        result = preview.Candidate; return true;
    }
}
