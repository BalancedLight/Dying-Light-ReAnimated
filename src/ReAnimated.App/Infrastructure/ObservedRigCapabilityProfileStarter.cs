using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.Infrastructure;

/// <summary>Creates a portable, incomplete node inventory from an already verified DL1 template.</summary>
public static class ObservedRigCapabilityProfileStarter
{
    public static RigCapabilityProfile Create(Dl1RigTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.SourceFingerprint.Length != 64 || template.SourceFingerprint.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("The resolved template must carry an exact SHA-256 fingerprint.", nameof(template));

        var roleIds = template.Entities.ToDictionary(entity => entity.Index, CreateRoleId);
        var provenance = new RigEvidenceReference
        {
            Id = "observed-template:" + template.SourceFingerprint.ToLowerInvariant(),
            Kind = RigEvidenceKind.ImportedSource,
            ArtifactSha256 = template.SourceFingerprint.ToLowerInvariant(),
            Description = $"Observed node inventory from resolved template '{template.ProfileName}' and resource '{template.SourceResourceName}'. " +
                "This decoded-content fingerprint does not identify a native build or runtime consumer."
        };

        var roles = template.Entities.Select(entity => new RigRuntimeRole
        {
            Id = roleIds[entity.Index],
            NativeName = entity.Name,
            EntityKind = ToProfileKind(entity.Kind),
            ParentRoleId = entity.ParentIndex >= 0 ? roleIds[entity.ParentIndex] : null,
            Requirement = RigRoleRequirementKind.Unknown,
            Category = RigRoleCategory.Unknown,
            SkinInfluenceAllowed = null,
            Aliases = [],
            PrerequisiteRoleIds = [],
            ParentConstraint = RigRoleParentConstraint.Unspecified,
            RequiredLods = [],
            RequiredVariantIds = [],
            RulesComplete = false,
            Evidence = [provenance with
            {
                Id = provenance.Id + ":node:" + entity.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Description = $"Observed template node index {entity.Index}; exact name '{entity.Name}'; " +
                    $"parent '{(entity.ParentIndex >= 0 ? template[entity.ParentIndex].Name : "<root>")}'; " +
                    $"source kind {entity.Kind}; deform flag {entity.IsDeform}; semantic anchor '{entity.SemanticRole ?? "<none>"}'."
            }]
        }).ToImmutableArray();

        return RigCapabilityProfileSerializer.Seal(new RigCapabilityProfile
        {
            Identity = new()
            {
                Id = "observed-template-" + template.SourceFingerprint[..16].ToLowerInvariant(),
                Version = "1",
                ContentSha256 = new string('0', 64)
            },
            FamilyId = "observed-template-inventory",
            Roles = roles,
            Capabilities = [new RigCapabilityDefinition
            {
                Id = "observed-node-inventory",
                RoleIds = roles.Select(role => role.Id).ToImmutableArray()
            }],
            Consumers = [],
            ConsumerCoverageComplete = false
        });
    }

    private static string CreateRoleId(Dl1RigTemplateEntity entity) =>
        string.IsNullOrWhiteSpace(entity.SemanticRole)
            ? "observed-node-" + entity.Index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)
            : "observed-anchor-" + entity.SemanticRole;

    private static RigNativeEntityKind ToProfileKind(BoneKind kind) => kind switch
    {
        BoneKind.Root or BoneKind.Deform => RigNativeEntityKind.Bone,
        BoneKind.Helper or BoneKind.Camera or BoneKind.Prop => RigNativeEntityKind.Helper,
        _ => RigNativeEntityKind.Unknown
    };
}
