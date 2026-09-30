using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public sealed record RigSetupMatch(string Key, string Name, Guid? DestinationEntityId, string Reason, bool RequiresFrameReview);

public sealed class RigSetupTransferPreview
{
    internal RigSetupTransferPreview(FbxModelAuthoringImportResult source, FbxModelAuthoringImportResult candidate,
        RigSetupPreset setup, CapabilityProfileReview review)
    { Source = source; Candidate = candidate; Setup = setup; Review = review; }
    internal FbxModelAuthoringImportResult Source { get; }
    public FbxModelAuthoringImportResult Candidate { get; }
    public RigSetupPreset Setup { get; }
    public CapabilityProfileReview Review { get; }
}

/// <summary>Resolves portable choices onto destination-owned identities; fitted anatomy stays local.</summary>
public static class FbxRigSetupTransfer
{
    public static ImmutableArray<RigSetupMatch> Propose(FbxModelAuthoringImportResult destination, RigSetupPreset setup)
    {
        RigSetupPresetSerializer.Verify(setup);
        var session = Session(destination);
        var recipe = session.Recipe;
        var owned = recipe.Entities.Where(e => e.OwnerAssetId == session.OwnerModelId).ToArray();
        bool sameFamily = recipe.ProfileSnapshot?.FamilyId == setup.Profile.FamilyId;
        return setup.Nodes.Select(node =>
        {
            var byRole = sameFamily ? owned.Where(e => e.Kind == node.Kind && node.Roles.Length > 0 &&
                node.Roles.All(r => recipe.Assignments.Any(a => a.RoleId == r && a.EntityId == e.EntityId))).ToArray() : [];
            var byName = owned.Where(e => e.Kind == node.Kind && e.NativeName.Equals(node.NativeName, StringComparison.Ordinal)).ToArray();
            var found = byRole.Length > 0 ? byRole : byName;
            bool conflict = byRole.Length == 1 && byName.Length > 0 && !byName.Any(e => e.EntityId == byRole[0].EntityId);
            return new RigSetupMatch(node.Key, node.NativeName, found.Length == 1 && !conflict ? found[0].EntityId : null,
                conflict ? "Role and name point to different nodes; choose explicitly." : found.Length == 1 ? byRole.Length == 1 ? "Matching role in the same profile family; review required." : "Exact name and kind; review required." : found.Length == 0 ? "No matching node. Create or select the intended destination node." : "Several matching nodes; choose explicitly.", node.RequiresFrameReview);
        }).ToImmutableArray();
    }

    public static RigSetupTransferPreview Preview(FbxModelAuthoringImportResult destination, RigSetupPreset setup,
        IReadOnlyDictionary<string, Guid> mapping, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(mapping); RigSetupPresetSerializer.Verify(setup); token.ThrowIfCancellationRequested();
        var session = Session(destination); var recipe = session.Recipe;
        if (mapping.Count != setup.Nodes.Length || setup.Nodes.Any(n => !mapping.ContainsKey(n.Key)) || mapping.Values.Distinct().Count() != mapping.Count)
            throw new InvalidOperationException("Every setup node needs a distinct destination mapping; unresolved or duplicate mappings cannot be applied.");
        foreach (var node in setup.Nodes)
        {
            var entity = recipe.Entities.SingleOrDefault(e => e.EntityId == mapping[node.Key]);
            if (entity is null || entity.OwnerAssetId != session.OwnerModelId || entity.Kind != node.Kind)
                throw new InvalidOperationException($"'{node.NativeName}' needs a destination node of the same kind owned by this model.");
        }
        var replacedRoles = setup.Nodes.SelectMany(n => n.Roles).ToHashSet(StringComparer.Ordinal);
        if (recipe.AssetRoles.Any(o => setup.CharacterOwnerRoles.Contains(o.RoleId, StringComparer.Ordinal) && o.AssetId != session.OwnerModelId))
            throw new InvalidOperationException("A setup owner role belongs to another destination asset. Review that owner separately before replacing it.");
        var assignments = recipe.Assignments.Where(a => !replacedRoles.Contains(a.RoleId)).Concat(setup.Nodes.SelectMany(n => n.Roles.Select(r => new RigRoleAssignment(r, mapping[n.Key])))).ToImmutableArray();
        var owners = recipe.AssetRoles.Where(o => !setup.CharacterOwnerRoles.Contains(o.RoleId, StringComparer.Ordinal)).Concat(setup.CharacterOwnerRoles.Select(r => new RigAssetRoleBinding(r, session.OwnerModelId))).ToImmutableArray();
        var channelIds = setup.Nodes.Where(n => n.Channels is not null).Select(n => mapping[n.Key]).ToHashSet();
        var channels = recipe.ComponentPolicies.Where(p => !channelIds.Contains(p.EntityId)).Concat(setup.Nodes.Where(n => n.Channels is not null).Select(n =>
        {
            var policy = n.Channels!.Bind(mapping[n.Key]);
            ImmutableArray<RigEvidenceReference> evidence = [new()
            {
                Id = "setup-transfer:" + n.Key,
                Kind = RigEvidenceKind.UserOverride,
                ArtifactSha256 = destination.Package.Document.Source.ContentSha256,
                Description = $"Proposed channel choices from reusable setup {setup.ContentSha256} on this destination source. Applying requires author review; this records an authoring decision, not native consumer or runtime validation.",
            }];
            return policy with { Position = policy.Position with { Evidence = evidence }, Rotation = policy.Rotation with { Evidence = evidence },
                Scale = policy.Scale with { Evidence = evidence }, LodEvidence = evidence };
        })).ToImmutableArray();
        var nextRecipe = recipe with { Profile = setup.Profile.Identity, ProfileSnapshot = setup.Profile,
            SelectedCapabilityIds = setup.Capabilities, Assignments = assignments, AssetRoles = owners, ComponentPolicies = channels };
        var updated = RiggingSessions.Change(session, session with { Recipe = nextRecipe }, RiggingEditKind.Profile);
        var doc = destination.Package.Document with { RiggingSession = updated, LastBuildReceipt = null }; doc.Validate();
        var candidate = destination with { Package = destination.Package with { Document = doc } };
        FbxProfileEditGuard.RequireAllowed(destination, candidate, token);
        return new(destination, candidate, setup, FbxCapabilityProfileAuthoring.Inspect(candidate, token));
    }

    public static bool TryApply(FbxModelAuthoringImportResult current, RigSetupTransferPreview preview, out FbxModelAuthoringImportResult result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(preview);
        result = current;
        if (!ReferenceEquals(current, preview.Source)) return false;
        result = preview.Candidate; return true;
    }

    public static RigSetupTransferPreview? RefreshMetadata(RigSetupTransferPreview preview, FbxModelAuthoringImportResult current)
    {
        var compatible = FbxCapabilityProfileAuthoring.RefreshMetadata(new(preview.Source, preview.Candidate, preview.Review), current);
        return compatible is null ? null : new(current, compatible.Candidate, preview.Setup, preview.Review);
    }

    private static RiggingSession Session(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model); model.Package.Document.Validate();
        var session = model.Package.Document.RiggingSession ?? throw new InvalidOperationException("Start a destination Studio session before applying a setup.");
        if (!session.MatchesSource(model.Package.Document.Source.ContentSha256)) throw new InvalidOperationException("The destination source changed; review it before transferring a setup.");
        return session;
    }
}
