using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>The conservative state of an evidence-backed retail family candidate.</summary>
public enum Dl1RetailFamilyReadiness
{
    Unknown,
    Partial,
    /// <summary>Offline decoded evidence and selected profile capabilities are complete; this is not native/runtime proof.</summary>
    Full,
}

/// <summary>The result of attempting to pair two player perspective candidates.</summary>
public enum Dl1RetailFamilyPairStatus
{
    Paired,
    Conflict,
    Unverified,
}

public enum Dl1RetailFamilyCandidateDiagnosticKind
{
    Information,
    Unverified,
    Conflict,
}

/// <summary>
/// A diagnostic for a candidate or perspective pair. These messages describe
/// offline decoded evidence only; they are never native or live-game proof.
/// </summary>
public sealed record Dl1RetailFamilyCandidateDiagnostic(
    string Code,
    Dl1RetailFamilyCandidateDiagnosticKind Kind,
    string Message);

/// <summary>
/// The names and semantic roles actually present in the decoded hierarchy and
/// exact supplied template. Declared profile roles are deliberately excluded.
/// </summary>
public sealed record Dl1RetailFamilyRoleInventory(
    ImmutableArray<string> EntityNames,
    ImmutableArray<string> RoleIds)
{
    public ImmutableArray<string> ActualEntityNames => EntityNames;

    public ImmutableArray<string> ActualRoleIds => RoleIds;

    public bool Contains(RigRuntimeRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        if (RoleIds.Contains(role.Id, StringComparer.Ordinal))
        {
            return true;
        }

        if (role.NativeName is { } nativeName &&
            EntityNames.Contains(nativeName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return role.Aliases.Any(alias =>
            EntityNames.Contains(alias.Name, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// One decoded retail mesh/profile/template candidate with all source identity
/// retained. The class carries no synthetic stock geometry and makes no claim
/// about native loader or runtime behavior.
/// </summary>
public sealed record Dl1RetailFamilyCandidate
{
    public required RetailAssetRecord Asset { get; init; }

    public required Dl1RetailMeshProfile Profile { get; init; }

    public Dl1RigTemplate? Template { get; init; }

    public required Dl1RetailFamilyRoleInventory RoleInventory { get; init; }

    public required RigProfileResolution ProfileResolution { get; init; }

    public required Dl1RetailFamilyReadiness Readiness { get; init; }

    public required ImmutableArray<Dl1RetailFamilyCandidateDiagnostic> Diagnostics { get; init; }

    /// <summary>The decoded resource identity retained without retaining mesh payload bytes.</summary>
    public required string MeshResourceName { get; init; }

    public Dl1MeshGeometryKind GeometryKind => Profile.GeometryKind;

    public RetailAssetId AssetId => Asset.Id;

    public string ProviderId => Asset.Source.ProviderId;

    /// <summary>Exact decoded mesh hash when available; catalog enumeration may not hash individual resources.</summary>
    public string? ContentFingerprint => Template?.SourceFingerprint is { Length: 64 } decoded &&
        decoded.All(char.IsAsciiHexDigit) ? decoded.ToLowerInvariant() : Asset.Id.ContentFingerprint;

    public string? RigSignature => Profile.RigSignature;

    /// <summary>Decoded hierarchy only. The normal rig signature includes each asset's own ID.</summary>
    public string? PairingRigSignature => Template is null ? null :
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            Template.Entities.Select(e => new
            {
                e.Index, e.Name, e.ParentIndex, e.Kind, e.IsDeform,
                e.LocalRestMatrix, e.SemanticRole,
            }).ToArray())));

    public ImmutableArray<string> ActualRoleIds => RoleInventory.RoleIds;

    /// <summary>True only for the offline, selected-capability readiness scope.</summary>
    public bool IsReady => Readiness == Dl1RetailFamilyReadiness.Full;

    /// <summary>
    /// Convenience entry point for callers that do not need to retain a
    /// service instance. Missing capability inputs deliberately produce an
    /// unknown candidate rather than an anatomy-based default.
    /// </summary>
    public static Dl1RetailFamilyCandidate Assess(
        RetailAssetRecord asset,
        Dl1MeshData mesh,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile? capabilityProfile = null,
        IEnumerable<string>? selectedCapabilityIds = null) =>
        Dl1RetailFamilyCandidateService.Assess(
            asset,
            mesh,
            profile,
            template,
            capabilityProfile,
            selectedCapabilityIds);

    public static Dl1RetailFamilyCandidate Assess(
        RetailAssetRecord asset,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile? capabilityProfile = null,
        IEnumerable<string>? selectedCapabilityIds = null) =>
        Dl1RetailFamilyCandidateService.Assess(
            asset,
            profile,
            template,
            capabilityProfile,
            selectedCapabilityIds);
}

/// <summary>A pair report that retains both candidates when pairing is refused.</summary>
public sealed record Dl1RetailFamilyPair(
    Dl1RetailFamilyPairStatus Status,
    Dl1RetailFamilyCandidate? FirstPerson,
    Dl1RetailFamilyCandidate? ThirdPerson,
    ImmutableArray<Dl1RetailFamilyCandidateDiagnostic> Diagnostics)
{
    public bool IsPaired => Status == Dl1RetailFamilyPairStatus.Paired;
}

/// <summary>
/// Builds bounded family candidates from caller-supplied decoded retail data.
/// Capability readiness is always resolved against the supplied profile and
/// selected capability IDs; the service does not assume player anatomy.
/// </summary>
public static class Dl1RetailFamilyCandidateService
{
    public static Dl1RetailFamilyCandidate Assess(
        RetailAssetRecord asset,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile? capabilityProfile = null,
        IEnumerable<string>? selectedCapabilityIds = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(profile);
        RigCapabilityProfile effectiveProfile = capabilityProfile ?? UnavailableProfile();
        Dl1RetailFamilyCandidate candidate = AnalyzeMetadata(
            asset,
            profile,
            template,
            effectiveProfile,
            selectedCapabilityIds ?? []);
        return capabilityProfile is not null
            ? candidate
            : candidate with
            {
                Readiness = Dl1RetailFamilyReadiness.Unknown,
                Diagnostics = candidate.Diagnostics.Add(new(
                    "capabilities.profile-unavailable",
                    Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                    "No RigCapabilityProfile was supplied; capability readiness remains unknown.")),
            };
    }

    public static Dl1RetailFamilyCandidate Assess(
        RetailAssetRecord asset,
        Dl1MeshData mesh,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile? capabilityProfile = null,
        IEnumerable<string>? selectedCapabilityIds = null)
    {
        if (capabilityProfile is not null)
        {
            return Analyze(
                asset,
                mesh,
                profile,
                template,
                capabilityProfile,
                selectedCapabilityIds ?? []);
        }

        RigCapabilityProfile emptyProfile = UnavailableProfile();
        Dl1RetailFamilyCandidate candidate = Analyze(
            asset,
            mesh,
            profile,
            template,
            emptyProfile,
            []);
        return candidate with
        {
            Readiness = Dl1RetailFamilyReadiness.Unknown,
            Diagnostics = candidate.Diagnostics.Add(new(
                "capabilities.profile-unavailable",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "No RigCapabilityProfile was supplied; capability readiness remains unknown.")),
        };
    }

    public static Dl1RetailFamilyCandidate Analyze(
        RetailAssetRecord asset,
        Dl1MeshData mesh,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile capabilityProfile,
        IEnumerable<string> selectedCapabilityIds)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(capabilityProfile);
        ArgumentNullException.ThrowIfNull(selectedCapabilityIds);

        return AnalyzeCore(
            asset,
            mesh,
            profile,
            template,
            capabilityProfile,
            selectedCapabilityIds);
    }

    private static Dl1RetailFamilyCandidate AnalyzeMetadata(
        RetailAssetRecord asset,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile capabilityProfile,
        IEnumerable<string> selectedCapabilityIds) => AnalyzeCore(
        asset,
        null,
        profile,
        template,
        capabilityProfile,
        selectedCapabilityIds);

    private static Dl1RetailFamilyCandidate AnalyzeCore(
        RetailAssetRecord asset,
        Dl1MeshData? mesh,
        Dl1RetailMeshProfile profile,
        Dl1RigTemplate? template,
        RigCapabilityProfile capabilityProfile,
        IEnumerable<string> selectedCapabilityIds)
    {
        string[] selected = selectedCapabilityIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Dl1RetailFamilyRoleInventory inventory =
            Dl1RetailFamilyRoleInventoryFactory.Create(mesh, template);
        string[] presentRoleIds = inventory.RoleIds
            .Concat(capabilityProfile.Roles
                .Where(inventory.Contains)
                .Select(static role => role.Id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        RigProfileResolution resolution = RigProfileResolver.Resolve(
            capabilityProfile,
            selected,
            presentRoleIds);
        var diagnostics = new List<Dl1RetailFamilyCandidateDiagnostic>();

        bool assetMatchesProfile = profile.AssetId == asset.Id;
        if (!assetMatchesProfile)
        {
            diagnostics.Add(new(
                "identity.profile-asset-mismatch",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "The classified profile does not retain the exact supplied retail asset identity."));
        }

        bool assetMatchesMesh = mesh is null || string.Equals(
            asset.Id.Name,
            mesh.ResourceName.Trim().Replace('\\', '/').TrimStart('/'),
            StringComparison.OrdinalIgnoreCase);
        if (mesh is not null && !assetMatchesMesh)
        {
            diagnostics.Add(new(
                "identity.asset-mesh-mismatch",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "The catalog resource name and decoded mesh resource name disagree."));
        }

        bool templateMatchesMesh = mesh is null || template is null || string.Equals(
            template.SourceResourceName,
            mesh.ResourceName,
            StringComparison.OrdinalIgnoreCase);
        if (mesh is not null && !templateMatchesMesh)
        {
            diagnostics.Add(new(
                "identity.template-mesh-mismatch",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "The exact supplied template was extracted from a different resource name."));
        }

        bool decodedHash = template?.SourceFingerprint is { Length: 64 } sourceHash && sourceHash.All(char.IsAsciiHexDigit);
        if (asset.Id.ContentFingerprint is { } catalogHash && decodedHash &&
            !catalogHash.Equals(template!.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                "identity.catalog-decoded-hash-conflict",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "Catalog content identity and decoded reference SHA-256 disagree."));
        }
        if (asset.Id.ContentFingerprint is null && !decodedHash)
        {
            diagnostics.Add(new(
                "identity.content-hash-unavailable",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "The asset is retained, but its provider did not supply a decoded content SHA-256."));
        }

        if (mesh is null)
        {
            diagnostics.Add(new(
                "decode.mesh-unavailable",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Only compact metadata was supplied; complete mesh decode evidence was not available for readiness."));
        }
        else if (!mesh.IsStructurallyValid)
        {
            diagnostics.Add(new(
                "decode.mesh-invalid",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Decoded mesh diagnostics contain errors, so family readiness cannot be complete."));
        }

        if (profile.GeometryKind != Dl1MeshGeometryKind.Skinned ||
            profile.RigFamily == Dl1RigFamily.Unknown ||
            profile.RigFamilyConfidence < Dl1ClassificationConfidence.Medium ||
            profile.RigSignature is null ||
            template is null)
        {
            diagnostics.Add(new(
                "family.core-evidence-incomplete",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "A complete candidate requires valid decoded skin, corroborated family evidence, a rig signature, and an exact template."));
        }

        if (selected.Length == 0)
        {
            diagnostics.Add(new(
                "capabilities.none-selected",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "No capability was selected, so readiness is not established."));
        }

        foreach (RigProfileDiagnostic diagnostic in resolution.Diagnostics)
        {
            diagnostics.Add(new(
                diagnostic.Code,
                diagnostic.Status == RigValidationStatus.Failed
                    ? Dl1RetailFamilyCandidateDiagnosticKind.Conflict
                    : Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                diagnostic.Message));
        }

        foreach (RigResolvedRole resolvedRole in resolution.Roles)
        {
            if (inventory.Contains(resolvedRole.Role))
            {
                continue;
            }

            diagnostics.Add(new(
                "role.missing-from-decoded-inventory",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                $"Resolved role '{resolvedRole.Role.Id}' is absent from the actual decoded role inventory."));
        }

        bool hasIdentityConflict = diagnostics.Any(static diagnostic =>
            diagnostic.Kind == Dl1RetailFamilyCandidateDiagnosticKind.Conflict &&
            diagnostic.Code.StartsWith("identity.", StringComparison.Ordinal));
        bool coreEvidence = mesh is not null &&
            assetMatchesProfile &&
            assetMatchesMesh &&
            templateMatchesMesh &&
            mesh.IsStructurallyValid &&
            profile.GeometryKind == Dl1MeshGeometryKind.Skinned &&
            profile.RigFamily != Dl1RigFamily.Unknown &&
            profile.RigFamilyConfidence >= Dl1ClassificationConfidence.Medium &&
            profile.RigSignature is not null &&
            template is not null;
        bool allResolvedRolesPresent = resolution.Roles.All(resolved =>
            inventory.Contains(resolved.Role));
        Dl1RetailFamilyReadiness readiness =
            coreEvidence &&
            !hasIdentityConflict &&
            selected.Length > 0 &&
            resolution.Status == RigValidationStatus.Passed &&
            allResolvedRolesPresent &&
            !diagnostics.Any(static diagnostic =>
                diagnostic.Kind == Dl1RetailFamilyCandidateDiagnosticKind.Conflict)
                ? Dl1RetailFamilyReadiness.Full
                : coreEvidence && !hasIdentityConflict &&
                  resolution.Status != RigValidationStatus.Failed
                    ? Dl1RetailFamilyReadiness.Partial
                    : Dl1RetailFamilyReadiness.Unknown;

        return new Dl1RetailFamilyCandidate
        {
            Asset = asset,
            Profile = profile,
            Template = template,
            MeshResourceName = mesh?.ResourceName ?? profile.AssetId.Name,
            RoleInventory = inventory,
            ProfileResolution = resolution,
            Readiness = readiness,
            Diagnostics = diagnostics.ToImmutableArray(),
        };
    }

    /// <summary>
    /// Pairs only evidence-backed player FPP and TPP candidates. A name hint,
    /// stock-bank membership, or shared family label never establishes a pair.
    /// </summary>
    public static Dl1RetailFamilyPair Pair(
        Dl1RetailFamilyCandidate first,
        Dl1RetailFamilyCandidate second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var diagnostics = new List<Dl1RetailFamilyCandidateDiagnostic>();
        Dl1RetailFamilyCandidate? fpp = null;
        Dl1RetailFamilyCandidate? tpp = null;

        if (first.Profile.Perspective == Dl1MeshPerspective.FirstPerson &&
            second.Profile.Perspective == Dl1MeshPerspective.ThirdPerson)
        {
            fpp = first;
            tpp = second;
        }
        else if (second.Profile.Perspective == Dl1MeshPerspective.FirstPerson &&
                 first.Profile.Perspective == Dl1MeshPerspective.ThirdPerson)
        {
            fpp = second;
            tpp = first;
        }
        else
        {
            diagnostics.Add(new(
                "pair.perspective-unverified",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Pairing requires one decoded first-person and one decoded third-person perspective."));
        }

        if (fpp is null || tpp is null)
        {
            return new(Dl1RetailFamilyPairStatus.Unverified, fpp, tpp, diagnostics.ToImmutableArray());
        }

        if (fpp.Profile.PerspectiveConfidence < Dl1ClassificationConfidence.Medium ||
            tpp.Profile.PerspectiveConfidence < Dl1ClassificationConfidence.Medium)
        {
            diagnostics.Add(new(
                "pair.perspective-confidence-insufficient",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Both perspectives require at least medium decoded classification confidence."));
        }

        if (fpp.Profile.RigFamily != Dl1RigFamily.Player ||
            tpp.Profile.RigFamily != Dl1RigFamily.Player)
        {
            diagnostics.Add(new(
                "pair.family-not-player",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "Only a corroborated Player family may form a player FPP/TPP pair."));
        }

        if (fpp.Profile.RigFamily != tpp.Profile.RigFamily)
        {
            diagnostics.Add(new(
                "pair.family-conflict",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "The two decoded profiles identify different rig families."));
        }

        if (fpp.PairingRigSignature is null || tpp.PairingRigSignature is null)
        {
            diagnostics.Add(new(
                "pair.rig-signature-unverified",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Both candidates require a decoded rig signature before pairing."));
        }
        else if (!string.Equals(fpp.PairingRigSignature, tpp.PairingRigSignature, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                "pair.rig-signature-conflict",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "The decoded FPP and TPP rig signatures differ."));
        }

        if (fpp.ActualRoleIds.IsEmpty || tpp.ActualRoleIds.IsEmpty)
        {
            diagnostics.Add(new(
                "pair.role-inventory-unverified",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Both candidates require a nonempty decoded semantic role inventory."));
        }
        else if (!fpp.ActualRoleIds.ToHashSet(StringComparer.Ordinal)
                     .SetEquals(tpp.ActualRoleIds))
        {
            diagnostics.Add(new(
                "pair.role-inventory-conflict",
                Dl1RetailFamilyCandidateDiagnosticKind.Conflict,
                "The actual decoded role sets differ; perspective labels cannot repair that mismatch."));
        }

        if (!first.IsReady || !second.IsReady)
        {
            diagnostics.Add(new(
                "pair.candidate-unverified",
                Dl1RetailFamilyCandidateDiagnosticKind.Unverified,
                "Both candidates must be fully ready against their explicitly selected capabilities."));
        }

        Dl1RetailFamilyPairStatus status = diagnostics.Any(static diagnostic =>
            diagnostic.Kind == Dl1RetailFamilyCandidateDiagnosticKind.Conflict)
            ? Dl1RetailFamilyPairStatus.Conflict
            : diagnostics.Count > 0
                ? Dl1RetailFamilyPairStatus.Unverified
                : Dl1RetailFamilyPairStatus.Paired;
        return new(status, fpp, tpp, diagnostics.ToImmutableArray());
    }

    private static RigCapabilityProfile UnavailableProfile() => new()
    {
        Identity = new()
        {
            Id = "capability-profile-unavailable",
            Version = "0",
            ContentSha256 = new string('0', 64),
        },
        FamilyId = "unverified",
    };

    private static class Dl1RetailFamilyRoleInventoryFactory
    {
        public static Dl1RetailFamilyRoleInventory Create(
            Dl1MeshData? mesh,
            Dl1RigTemplate? template)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roles = new HashSet<string>(StringComparer.Ordinal);
            if (mesh is not null)
            {
                foreach (var entity in mesh.Hierarchy.Entities)
                {
                    names.Add(entity.Name);
                    if (Dl1RigDefinitionFactory.TryResolveSemanticRole(entity.Name) is { } role)
                    {
                        roles.Add(role);
                    }
                }

                if (mesh.Rig is not null)
                {
                    foreach (BoneDefinition bone in mesh.Rig.Bones)
                    {
                        names.Add(bone.Name);
                        if (bone.SemanticRole is { } role)
                        {
                            roles.Add(role);
                        }
                    }
                }
            }

            if (template is not null)
            {
                foreach (Dl1RigTemplateEntity entity in template.Entities)
                {
                    names.Add(entity.Name);
                    if (entity.SemanticRole is { } role)
                    {
                        roles.Add(role);
                    }
                }
            }

            return new(
                names.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
                roles.Order(StringComparer.Ordinal).ToImmutableArray());
        }
    }
}
