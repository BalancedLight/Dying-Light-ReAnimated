using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum RigStudioEntryPath { RepairExistingRig, AdaptExistingRig, AutoRigBiped }
public enum RigMotionStrategy { RepairExistingRig, NativeStockReuse, PreserveAnatomyMapped, ConformToReference }
public enum RigAnatomyPolicy { Preserve, ExplicitConformance }
public enum RigComponentOwner { Unknown, BindInherited, Clip, Procedural, Attachment, RuntimeBodyScale }
public enum RigRoleRequirementKind { Unknown, Required, Conditional, Optional }
public enum RigRoleParentConstraint { Unspecified, Direct, Ancestor }
public enum RigNativeEntityKind { Unknown, Bone, Helper, Mesh, Morph, Particle, Collision }
public enum RigFramePolicy { PreserveSource, GeneratedDeform, Contact, Camera, Socket, Structural, Manual }
public enum RigBoundsPolicy { PreserveSource, Solved, GenerateSegmentProxy }
public enum RigAnimationLod { Lod0 = 0, Lod1 = 1, Lod2 = 2, Lod3 = 3, Off = 4 }
public enum RigRoleCategory { Unknown, Body, Contact, Eye, Gaze, Grip, ReferenceCamera, ViewCamera, Twist, Normal, Structural, Equipment, Effect, Facial, Cloth, Physics, Damage }

[Flags]
public enum RigAnimationComponents { None = 0, Position = 1, Rotation = 2, Scale = 4 }

[Flags]
public enum RigHelperEditFields { None = 0, Name = 1, Parent = 2, Position = 4, Orientation = 8, Extents = 16, Channels = 32, All = 63 }

/// <summary>Source units, bind anatomy and runtime body size are separate authoring decisions.</summary>
public sealed record RigScalePolicy
{
    public double? SourceMetersPerUnit { get; init; }
    public TransformMatrix? SourceToAuthoring { get; init; }
    public bool ImportConversionAlreadyApplied { get; init; }
    public RigAnatomyPolicy Anatomy { get; init; } = RigAnatomyPolicy.Preserve;
    public double RuntimeUniformBodyScale { get; init; } = 1.0;
    public string? NativeBodyScaleRuleId { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public void Validate()
    {
        if (SourceMetersPerUnit is { } unit) RigRecipeRules.Positive(unit, nameof(SourceMetersPerUnit));
        if (SourceToAuthoring is { } matrix) RigRecipeRules.Affine(matrix, nameof(SourceToAuthoring));
        if (ImportConversionAlreadyApplied && (SourceMetersPerUnit is null || SourceToAuthoring is null))
            throw new ArgumentException("An applied import conversion requires recorded units and an exact transform.");
        RigContractRules.Defined(Anatomy, nameof(Anatomy));
        RigRecipeRules.Positive(RuntimeUniformBodyScale, nameof(RuntimeUniformBodyScale));
        RigContractRules.OptionalText(NativeBodyScaleRuleId, nameof(NativeBodyScaleRuleId));
        RigRecipeRules.Evidence(Evidence);
    }
}

/// <summary>Ordered owners are meaningful only with an explicitly identified composition rule.</summary>
public sealed record RigChannelOwnership
{
    public ImmutableArray<RigComponentOwner> Owners { get; init; } = [];
    public string? CompositionRuleId { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public void Validate()
    {
        RigContractRules.Array(Owners, nameof(Owners));
        if (Owners.Distinct().Count() != Owners.Length) throw new ArgumentException("Component owners cannot be duplicated.");
        foreach (RigComponentOwner owner in Owners) RigContractRules.Defined(owner, nameof(Owners));
        if (Owners.Length > 1 && (Owners.Contains(RigComponentOwner.Unknown) || string.IsNullOrWhiteSpace(CompositionRuleId)))
            throw new ArgumentException("Mixed component ownership requires known owners and an explicit composition rule.");
        RigContractRules.OptionalText(CompositionRuleId, nameof(CompositionRuleId));
        RigRecipeRules.Evidence(Evidence);
    }
}

public sealed record AnimationComponentPolicy
{
    public Guid EntityId { get; init; }
    public RigChannelOwnership Position { get; init; } = new();
    public RigChannelOwnership Rotation { get; init; } = new();
    public RigChannelOwnership Scale { get; init; } = new();
    public RigAnimationComponents? EmittedMask { get; init; }
    public string? LodRuleId { get; init; }
    public RigAnimationLod? AnimationLod { get; init; }
    public ImmutableArray<RigEvidenceReference> LodEvidence { get; init; } = [];

    public void Validate()
    {
        RigContractRules.Identifier(EntityId, nameof(EntityId));
        ArgumentNullException.ThrowIfNull(Position); Position.Validate();
        ArgumentNullException.ThrowIfNull(Rotation); Rotation.Validate();
        ArgumentNullException.ThrowIfNull(Scale); Scale.Validate();
        if (EmittedMask is { } mask && (mask & ~(RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale)) != 0)
            throw new ArgumentOutOfRangeException(nameof(EmittedMask), "Unknown animation components.");
        RigContractRules.OptionalText(LodRuleId, nameof(LodRuleId));
        if (AnimationLod is { } lod) RigContractRules.Defined(lod, nameof(AnimationLod));
        RigRecipeRules.Evidence(LodEvidence);
    }
}

public sealed record RigProfileReference
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ContentSha256 { get; init; } = string.Empty;
    public string? BuildFingerprint { get; init; }

    public void Validate()
    {
        RigContractRules.Text(Id, nameof(Id));
        RigContractRules.Text(Version, nameof(Version));
        RigContractRules.Hash(ContentSha256, nameof(ContentSha256));
        RigContractRules.OptionalHash(BuildFingerprint, nameof(BuildFingerprint));
    }
}

/// <summary>Native spellings are preserved exactly. Aliases require their own lookup evidence.</summary>
public sealed record RigNativeAlias
{
    public string Name { get; init; } = string.Empty;
    public string LookupRuleId { get; init; } = string.Empty;
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];
    public void Validate()
    {
        RigContractRules.Text(Name, nameof(Name));
        RigContractRules.Text(LookupRuleId, nameof(LookupRuleId));
        RigRecipeRules.Evidence(Evidence);
        if (Evidence.IsEmpty) throw new ArgumentException("An alias requires evidence of its lookup semantics.");
    }
}

public sealed record RigRuntimeRole
{
    public string Id { get; init; } = string.Empty;
    public RigRoleCategory Category { get; init; }
    public RigNativeEntityKind EntityKind { get; init; }
    public string? NativeName { get; init; }
    public string? OwnerAssetRoleId { get; init; }
    public ImmutableArray<RigNativeAlias> Aliases { get; init; } = [];
    public RigRoleRequirementKind Requirement { get; init; } = RigRoleRequirementKind.Unknown;
    public string? ConditionCapabilityId { get; init; }
    public ImmutableArray<string> PrerequisiteRoleIds { get; init; } = [];
    public string? ParentRoleId { get; init; }
    public RigRoleParentConstraint ParentConstraint { get; init; }
    public int MinimumCount { get; init; } = 1;
    public int? MaximumCount { get; init; } = 1;
    public bool? SkinInfluenceAllowed { get; init; }
    public RigFramePolicy FramePolicy { get; init; } = RigFramePolicy.PreserveSource;
    public string? FrameRuleId { get; init; }
    public string? ComponentRuleId { get; init; }
    public string? RetentionRuleId { get; init; }
    public RigHelperEditFields AllowedEdits { get; init; }
    public ImmutableArray<string> RequiredVariantIds { get; init; } = [];
    public ImmutableArray<int> RequiredLods { get; init; } = [];
    public bool RulesComplete { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public void Validate()
    {
        RigContractRules.Text(Id, nameof(Id));
        RigContractRules.Defined(Category, nameof(Category));
        RigContractRules.Defined(EntityKind, nameof(EntityKind));
        RigContractRules.Defined(Requirement, nameof(Requirement));
        RigContractRules.Defined(ParentConstraint, nameof(ParentConstraint));
        RigContractRules.Defined(FramePolicy, nameof(FramePolicy));
        RigContractRules.OptionalText(NativeName, nameof(NativeName));
        RigContractRules.OptionalText(OwnerAssetRoleId, nameof(OwnerAssetRoleId));
        RigContractRules.OptionalText(ConditionCapabilityId, nameof(ConditionCapabilityId));
        RigContractRules.OptionalText(ParentRoleId, nameof(ParentRoleId));
        RigContractRules.OptionalText(FrameRuleId, nameof(FrameRuleId));
        RigContractRules.OptionalText(ComponentRuleId, nameof(ComponentRuleId));
        RigContractRules.OptionalText(RetentionRuleId, nameof(RetentionRuleId));
        if (Requirement == RigRoleRequirementKind.Conditional && ConditionCapabilityId is null)
            throw new ArgumentException("A conditional role must identify its selecting capability.");
        if (ParentConstraint != RigRoleParentConstraint.Unspecified && ParentRoleId is null)
            throw new ArgumentException("A parent constraint requires a semantic parent role.");
        if (MinimumCount < 0 || MaximumCount is { } maximum && maximum < MinimumCount)
            throw new ArgumentException("Role multiplicity is invalid.");
        if ((AllowedEdits & ~RigHelperEditFields.All) != 0) throw new ArgumentException("Unknown helper edit fields.");
        RigRecipeRules.Names(PrerequisiteRoleIds, nameof(PrerequisiteRoleIds));
        RigRecipeRules.Names(RequiredVariantIds, nameof(RequiredVariantIds));
        RigContractRules.Array(RequiredLods, nameof(RequiredLods));
        if (RequiredLods.Any(static lod => lod < 0) || RequiredLods.Distinct().Count() != RequiredLods.Length)
            throw new ArgumentException("Required LOD identities must be unique and nonnegative.");
        RigContractRules.Array(Aliases, nameof(Aliases));
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (NativeName is not null) names.Add(NativeName);
        foreach (RigNativeAlias alias in Aliases)
        {
            alias.Validate();
            if (!names.Add(alias.Name)) throw new ArgumentException("A native alias is duplicated.");
        }
        RigRecipeRules.Evidence(Evidence);
        if (RulesComplete && (NativeName is null || OwnerAssetRoleId is null || EntityKind == RigNativeEntityKind.Unknown || Evidence.IsEmpty ||
            ComponentRuleId is null || RetentionRuleId is null ||
            EntityKind is RigNativeEntityKind.Bone or RigNativeEntityKind.Helper && FrameRuleId is null))
            throw new ArgumentException("Complete role rules require representation, native identity, frame/component/retention rules and supporting evidence.");
    }
}

public sealed record RigCapabilityDefinition
{
    public string Id { get; init; } = string.Empty;
    public ImmutableArray<string> RoleIds { get; init; } = [];
    public ImmutableArray<string> PrerequisiteCapabilityIds { get; init; } = [];
    public ImmutableArray<RigFacetRequirement> Facets { get; init; } =
        Enum.GetValues<RigValidationFacet>().Select(static facet => new RigFacetRequirement(facet)).ToImmutableArray();
    public void Validate()
    {
        RigContractRules.Text(Id, nameof(Id));
        RigRecipeRules.Names(RoleIds, nameof(RoleIds));
        RigRecipeRules.Names(PrerequisiteCapabilityIds, nameof(PrerequisiteCapabilityIds));
        RigContractRules.Array(Facets, nameof(Facets));
        if (Facets.Length != Enum.GetValues<RigValidationFacet>().Length ||
            Facets.Select(static f => f.Facet).Distinct().Count() != Facets.Length)
            throw new ArgumentException("A capability must declare each validation facet exactly once.");
        foreach (RigFacetRequirement facet in Facets)
        {
            RigContractRules.Defined(facet.Facet, nameof(Facets));
            if (!facet.Applicable) RigContractRules.Text(facet.Reason, nameof(facet.Reason));
        }
    }
}

public sealed record RigConsumerCoverage
{
    public string ConsumerId { get; init; } = string.Empty;
    public ImmutableArray<string> CapabilityIds { get; init; } = [];
    public ImmutableArray<string> DiscoveredRoleIds { get; init; } = [];
    public ImmutableArray<string> Unknowns { get; init; } = [];
    public bool Inspected { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];
    public void Validate()
    {
        RigContractRules.Text(ConsumerId, nameof(ConsumerId));
        RigRecipeRules.Names(CapabilityIds, nameof(CapabilityIds));
        RigRecipeRules.Names(DiscoveredRoleIds, nameof(DiscoveredRoleIds));
        RigRecipeRules.Names(Unknowns, nameof(Unknowns));
        RigRecipeRules.Evidence(Evidence);
        if (Inspected && Evidence.IsEmpty) throw new ArgumentException("Inspected consumers require provenance.");
    }
}

/// <summary>One declared capability family, not a universal union skeleton.</summary>
public sealed record RigCapabilityProfile
{
    public RigProfileReference Identity { get; init; } = new();
    public string FamilyId { get; init; } = string.Empty;
    public ImmutableArray<RigRuntimeRole> Roles { get; init; } = [];
    public ImmutableArray<RigCapabilityDefinition> Capabilities { get; init; } = [];
    public ImmutableArray<RigConsumerCoverage> Consumers { get; init; } = [];
    public bool ConsumerCoverageComplete { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Identity); Identity.Validate();
        RigContractRules.Text(FamilyId, nameof(FamilyId));
        RigContractRules.Array(Roles, nameof(Roles));
        RigContractRules.Array(Capabilities, nameof(Capabilities));
        RigContractRules.Array(Consumers, nameof(Consumers));
        foreach (RigRuntimeRole role in Roles) role.Validate();
        foreach (RigCapabilityDefinition capability in Capabilities) capability.Validate();
        foreach (RigConsumerCoverage consumer in Consumers) consumer.Validate();
        if (Roles.Select(static r => r.Id).Distinct(StringComparer.Ordinal).Count() != Roles.Length ||
            Capabilities.Select(static c => c.Id).Distinct(StringComparer.Ordinal).Count() != Capabilities.Length ||
            Consumers.Select(static c => c.ConsumerId).Distinct(StringComparer.Ordinal).Count() != Consumers.Length)
            throw new ArgumentException("Profile role, capability and consumer identities must be unique.");
        var roleIds = Roles.Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);
        var capabilityIds = Capabilities.Select(static c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (Capabilities.Any(c => c.RoleIds.Any(id => !roleIds.Contains(id)) || c.PrerequisiteCapabilityIds.Any(id => !capabilityIds.Contains(id))) ||
            Roles.Any(r => r.PrerequisiteRoleIds.Any(id => !roleIds.Contains(id)) || r.ParentRoleId is { } p && !roleIds.Contains(p) ||
                           r.ConditionCapabilityId is { } c && !capabilityIds.Contains(c)) ||
            Consumers.Any(c => c.DiscoveredRoleIds.Any(id => !roleIds.Contains(id)) || c.CapabilityIds.Any(id => !capabilityIds.Contains(id))))
            throw new ArgumentException("A profile dependency references an undeclared identity.");
        if (ConsumerCoverageComplete && (Identity.BuildFingerprint is null || Consumers.IsEmpty ||
            Consumers.Any(static c => !c.Inspected || !c.Unknowns.IsEmpty || c.CapabilityIds.IsEmpty) || Roles.Any(static r => !r.RulesComplete)))
            throw new ArgumentException("Complete coverage requires a pinned build and inspected, resolved consumer rules.");
        if (ConsumerCoverageComplete && (Consumers.Any(c => !HasBuildEvidence(c.Evidence)) || Roles.Any(r => !HasBuildEvidence(r.Evidence))))
            throw new ArgumentException("Complete native coverage requires build-matched profile evidence for every consumer and role.");
        bool HasBuildEvidence(ImmutableArray<RigEvidenceReference> evidence) => evidence.Any(e =>
            e.Kind == RigEvidenceKind.ProfileRule && e.ArtifactSha256 is not null && e.BuildFingerprint is not null &&
            RigContractRules.SameHash(e.BuildFingerprint, Identity.BuildFingerprint));
    }
}

/// <summary>Stable authoring identity; no serialized physical-node or vertex-local palette index.</summary>
public sealed record RigEntityBinding
{
    public Guid EntityId { get; init; }
    public Guid OwnerAssetId { get; init; }
    public string? SourceEntityId { get; init; }
    public string NativeName { get; init; } = string.Empty;
    public RigNativeEntityKind Kind { get; init; }
    public bool Imported { get; init; } = true;
    public void Validate()
    {
        RigContractRules.Identifier(EntityId, nameof(EntityId));
        RigContractRules.Identifier(OwnerAssetId, nameof(OwnerAssetId));
        RigContractRules.Text(NativeName, nameof(NativeName));
        RigContractRules.Defined(Kind, nameof(Kind));
        RigContractRules.OptionalText(SourceEntityId, nameof(SourceEntityId));
        if (Imported && SourceEntityId is null) throw new ArgumentException("Imported entities require a source identity.");
    }
}

public sealed record RigRoleAssignment(string RoleId, Guid EntityId);
public sealed record RigAssetRoleBinding(string RoleId, Guid AssetId);

/// <summary>One entity's frame/bounds decision; indices belong to the final preparer only.</summary>
public sealed record RigEntityFramePolicy
{
    public Guid EntityId { get; init; }
    public RigFramePolicy FramePolicy { get; init; } = RigFramePolicy.PreserveSource;
    public RigBoundsPolicy BoundsPolicy { get; init; } = RigBoundsPolicy.GenerateSegmentProxy;
    public TransformMatrix? SolvedGlobalFrame { get; init; }
    public Vector3D? BoundsCenter { get; init; }
    public Vector3D? BoundsHalfExtents { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    public void Validate()
    {
        RigContractRules.Identifier(EntityId, nameof(EntityId));
        RigContractRules.Defined(FramePolicy, nameof(FramePolicy));
        RigContractRules.Defined(BoundsPolicy, nameof(BoundsPolicy));
        if (SolvedGlobalFrame is { } frame) RigRecipeRules.Affine(frame, nameof(SolvedGlobalFrame));
        if (FramePolicy == RigFramePolicy.PreserveSource && SolvedGlobalFrame is not null)
            throw new ArgumentException("Preserve-source frames cannot also carry a replacement frame.");
        if (BoundsPolicy == RigBoundsPolicy.GenerateSegmentProxy && (BoundsCenter is not null || BoundsHalfExtents is not null) ||
            BoundsPolicy != RigBoundsPolicy.GenerateSegmentProxy && (BoundsCenter is null || BoundsHalfExtents is null))
            throw new ArgumentException("Bounds must have one explicit owner: retained/solved bounds or generated segment bounds.");
        if (BoundsCenter is { } center && !center.IsFinite || BoundsHalfExtents is { } half && (!half.IsFinite || half.X < 0 || half.Y < 0 || half.Z < 0))
            throw new ArgumentException("Entity bounds must be finite and nonnegative.");
        if (FramePolicy is RigFramePolicy.Contact or RigFramePolicy.Camera or RigFramePolicy.Socket or RigFramePolicy.Structural &&
            BoundsPolicy == RigBoundsPolicy.GenerateSegmentProxy)
            throw new ArgumentException("Role-specific helper bounds require retained or solved bounds, not a generic segment proxy.");
        RigRecipeRules.Evidence(Evidence);
    }
}

public sealed record HelperRecipe
{
    public Guid EntityId { get; init; }
    public Guid OwnerAssetId { get; init; }
    public string RoleId { get; init; } = string.Empty;
    public Guid ParentEntityId { get; init; }
    public TransformMatrix LocalFrame { get; init; } = TransformMatrix.Identity;
    public Vector3D? BoundsCenter { get; init; }
    public Vector3D? BoundsHalfExtents { get; init; }
    public RigFramePolicy FramePolicy { get; init; } = RigFramePolicy.PreserveSource;
    public RigEvidenceKind PlacementProvenance { get; init; } = RigEvidenceKind.ImportedSource;
    public double? DetectionConfidence { get; init; }
    public RigHelperEditFields LockedFields { get; init; }
    public bool UserApproved { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];
    public void Validate()
    {
        RigContractRules.Identifier(EntityId, nameof(EntityId));
        RigContractRules.Identifier(OwnerAssetId, nameof(OwnerAssetId));
        RigContractRules.Identifier(ParentEntityId, nameof(ParentEntityId));
        if (ParentEntityId == EntityId) throw new ArgumentException("A helper cannot parent itself.");
        RigContractRules.Text(RoleId, nameof(RoleId));
        RigRecipeRules.Affine(LocalFrame, nameof(LocalFrame));
        RigContractRules.Defined(FramePolicy, nameof(FramePolicy));
        RigContractRules.Defined(PlacementProvenance, nameof(PlacementProvenance));
        if ((BoundsCenter is null) != (BoundsHalfExtents is null)) throw new ArgumentException("Bounds require a center and half extents together.");
        if (BoundsCenter is { } center && !center.IsFinite || BoundsHalfExtents is { } half && (!half.IsFinite || half.X < 0 || half.Y < 0 || half.Z < 0))
            throw new ArgumentException("Helper bounds must be finite and nonnegative.");
        if (DetectionConfidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            throw new ArgumentException("Detection confidence must be a finite 0..1 value; it is not a validation result.");
        if ((LockedFields & ~RigHelperEditFields.All) != 0) throw new ArgumentException("Unknown helper lock fields.");
        RigRecipeRules.Evidence(Evidence);
    }
}

/// <summary>Approved decisions consumed by the existing final authored-rig owner.</summary>
public sealed record RuntimeRigRecipe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RigProfileReference? Profile { get; init; }
    public ImmutableArray<string> SelectedCapabilityIds { get; init; } = [];
    public ImmutableArray<RigAssetRoleBinding> AssetRoles { get; init; } = [];
    public ImmutableArray<RigEntityBinding> Entities { get; init; } = [];
    public ImmutableArray<RigRoleAssignment> Assignments { get; init; } = [];
    public ImmutableArray<HelperRecipe> Helpers { get; init; } = [];
    public ImmutableArray<AnimationComponentPolicy> ComponentPolicies { get; init; } = [];
    public ImmutableArray<RigEntityFramePolicy> FramePolicies { get; init; } = [];
    public RigScalePolicy ScalePolicy { get; init; } = new();
    public RigMotionStrategy MotionStrategy { get; init; } = RigMotionStrategy.RepairExistingRig;

    public void Validate()
    {
        RigContractRules.Identifier(Id, nameof(Id)); Profile?.Validate();
        RigRecipeRules.Names(SelectedCapabilityIds, nameof(SelectedCapabilityIds));
        RigContractRules.Array(AssetRoles, nameof(AssetRoles));
        RigContractRules.Array(Entities, nameof(Entities));
        RigContractRules.Array(Assignments, nameof(Assignments));
        RigContractRules.Array(Helpers, nameof(Helpers));
        RigContractRules.Array(ComponentPolicies, nameof(ComponentPolicies));
        RigContractRules.Array(FramePolicies, nameof(FramePolicies));
        ArgumentNullException.ThrowIfNull(ScalePolicy); ScalePolicy.Validate();
        RigContractRules.Defined(MotionStrategy, nameof(MotionStrategy));
        if (MotionStrategy == RigMotionStrategy.ConformToReference && ScalePolicy.Anatomy != RigAnatomyPolicy.ExplicitConformance ||
            MotionStrategy != RigMotionStrategy.ConformToReference && ScalePolicy.Anatomy == RigAnatomyPolicy.ExplicitConformance)
            throw new ArgumentException("Anatomy conformance must be an explicit, consistent motion/scale choice.");
        var byId = new Dictionary<Guid, RigEntityBinding>();
        var sourceIds = new HashSet<(Guid, string)>();
        var assetRoles = new HashSet<string>(StringComparer.Ordinal);
        foreach (RigAssetRoleBinding asset in AssetRoles)
        {
            RigContractRules.Text(asset.RoleId, nameof(asset.RoleId));
            RigContractRules.Identifier(asset.AssetId, nameof(asset.AssetId));
            if (!assetRoles.Add(asset.RoleId)) throw new ArgumentException("An asset role cannot have competing owners.");
        }
        foreach (RigEntityBinding entity in Entities)
        {
            entity.Validate();
            if (!byId.TryAdd(entity.EntityId, entity) || entity.SourceEntityId is { } source && !sourceIds.Add((entity.OwnerAssetId, source)))
                throw new ArgumentException("Recipe entity or source identities are duplicated.");
        }
        var assignments = new HashSet<(string, Guid)>();
        foreach (RigRoleAssignment assignment in Assignments)
        {
            RigContractRules.Text(assignment.RoleId, nameof(assignment.RoleId));
            if (!byId.ContainsKey(assignment.EntityId) || !assignments.Add((assignment.RoleId, assignment.EntityId)))
                throw new ArgumentException("A role assignment is duplicated or references a missing entity.");
        }
        var helperIds = new HashSet<Guid>();
        foreach (HelperRecipe helper in Helpers)
        {
            helper.Validate();
            if (!helperIds.Add(helper.EntityId) || !byId.TryGetValue(helper.EntityId, out RigEntityBinding? entity) ||
                !byId.TryGetValue(helper.ParentEntityId, out RigEntityBinding? parent) || entity.OwnerAssetId != helper.OwnerAssetId || parent.OwnerAssetId != helper.OwnerAssetId)
                throw new ArgumentException("Helper parents and entities must exist in the same owning asset.");
            if (entity.Kind is not (RigNativeEntityKind.Bone or RigNativeEntityKind.Helper) || parent.Kind is not (RigNativeEntityKind.Bone or RigNativeEntityKind.Helper))
                throw new ArgumentException("Bone/helper frames cannot be assigned to morphs, particles, collision records or mesh surfaces.");
        }
        var parents = Helpers.ToDictionary(static h => h.EntityId, static h => h.ParentEntityId);
        foreach (Guid helper in helperIds)
        {
            var visited = new HashSet<Guid>(); Guid current = helper;
            while (parents.TryGetValue(current, out Guid parent))
            {
                if (!visited.Add(current)) throw new ArgumentException("Helper parenting contains a cycle.");
                current = parent;
            }
        }
        var policies = new HashSet<Guid>();
        foreach (AnimationComponentPolicy policy in ComponentPolicies)
        {
            policy.Validate();
            if (!byId.ContainsKey(policy.EntityId) || !policies.Add(policy.EntityId))
                throw new ArgumentException("Component policies require distinct, existing entities.");
        }
        var framePolicies = new HashSet<Guid>();
        foreach (RigEntityFramePolicy policy in FramePolicies)
        {
            policy.Validate();
            if (!byId.ContainsKey(policy.EntityId) || !framePolicies.Add(policy.EntityId) || helperIds.Contains(policy.EntityId))
                throw new ArgumentException("Frame policies require existing, distinct entities and cannot compete with a helper recipe.");
        }
    }
}

internal static class RigRecipeRules
{
    public static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name, "A positive finite value is required.");
    }
    public static void Affine(TransformMatrix matrix, string name)
    {
        if (!matrix.IsFinite || matrix.M41 != 0 || matrix.M42 != 0 || matrix.M43 != 0 || matrix.M44 != 1 ||
            !double.IsFinite(matrix.LinearDeterminant) || matrix.LinearDeterminant == 0)
            throw new ArgumentException("A finite, invertible affine frame is required; identity fallback is not permitted.", name);
    }
    public static void Names(ImmutableArray<string> values, string name)
    {
        RigContractRules.Array(values, name);
        foreach (string value in values) RigContractRules.Text(value, name);
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Identity collections must not contain duplicates.", name);
    }
    public static void Evidence(ImmutableArray<RigEvidenceReference> references)
    {
        RigContractRules.Array(references, nameof(references));
        foreach (RigEvidenceReference reference in references) reference.Validate();
        if (references.Select(static e => e.Id).Distinct(StringComparer.Ordinal).Count() != references.Length)
            throw new ArgumentException("Evidence identities must be unique.", nameof(references));
    }
}
