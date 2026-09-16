using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigProfileResolverTests
{
    internal static RigEvidenceReference Evidence(string id = "synthetic-rule") => new()
    {
        Id = id, Kind = RigEvidenceKind.ProfileRule, ArtifactSha256 = new string('a', 64), BuildFingerprint = new string('b', 64),
    };

    internal static RigRuntimeRole Role(string id) => new()
    {
        Id = id, NativeName = id, OwnerAssetRoleId = "character", Category = RigRoleCategory.Contact,
        EntityKind = RigNativeEntityKind.Helper, Requirement = RigRoleRequirementKind.Required,
        FrameRuleId = "synthetic-frame", ComponentRuleId = "synthetic-components", RetentionRuleId = "synthetic-retention",
        RulesComplete = true, Evidence = [Evidence()],
    };

    internal static RigCapabilityProfile Profile() => new()
    {
        Identity = new() { Id = "synthetic-profile", Version = "1", ContentSha256 = new string('c', 64), BuildFingerprint = new string('b', 64) },
        FamilyId = "synthetic-biped",
        Roles = [Role("root"), Role("sole") with { ParentRoleId = "root", ParentConstraint = RigRoleParentConstraint.Direct }, Role("view")],
        Capabilities = [new() { Id = "locomotion", RoleIds = ["sole"] }, new() { Id = "partial-view", RoleIds = ["view"] }],
        Consumers =
        [
            new() { ConsumerId = "contact-reader", CapabilityIds = ["locomotion"], DiscoveredRoleIds = ["sole"], Inspected = true, Evidence = [Evidence()] },
            new() { ConsumerId = "view-reader", CapabilityIds = ["partial-view"], DiscoveredRoleIds = ["view"], Inspected = true, Evidence = [Evidence()] },
        ],
        ConsumerCoverageComplete = true,
    };

    [Fact]
    public void PartialSelectionDoesNotImportUnrelatedLocomotionRequirements()
    {
        RigProfileResolution result = RigProfileResolver.Resolve(Profile(), ["partial-view"]);
        Assert.Equal(RigValidationStatus.Passed, result.Status);
        Assert.Equal("view", Assert.Single(result.Roles).Role.Id);
        Assert.Equal<string>(["view-reader"], result.Roles[0].ConsumerIds);
    }

    [Fact]
    public void ClosureIncludesParentAndAttributesItToItsConsumer()
    {
        RigProfileResolution result = RigProfileResolver.Resolve(Profile(), ["locomotion"]);
        Assert.Equal(RigValidationStatus.Passed, result.Status);
        Assert.Equal(["root", "sole"], result.Roles.Select(r => r.Role.Id));
        Assert.All(result.Roles, r => Assert.True(r.Required));
        Assert.All(result.Roles, r => Assert.Equal<string>(["contact-reader"], r.ConsumerIds));
    }

    [Fact]
    public void PrerequisiteCapabilitiesEnableConditionalRoles()
    {
        RigCapabilityProfile profile = Profile();
        profile = profile with
        {
            Capabilities = profile.Capabilities.SetItem(0, profile.Capabilities[0] with { PrerequisiteCapabilityIds = ["partial-view"] }),
            Roles = profile.Roles.SetItem(1, profile.Roles[1] with { Requirement = RigRoleRequirementKind.Conditional, ConditionCapabilityId = "partial-view" }),
        };
        var result = RigProfileResolver.Resolve(profile, ["locomotion"]);
        Assert.Equal<string>(["locomotion", "partial-view"], result.CapabilityIds);
        Assert.Equal(["root", "sole", "view"], result.Roles.Select(r => r.Role.Id));
        Assert.Equal(RigValidationStatus.Passed, result.Status);
    }

    [Fact]
    public void DisabledConditionalDependencyFailsRatherThanSilentlyDisappearing()
    {
        RigCapabilityProfile profile = Profile();
        profile = profile with { Roles = profile.Roles.SetItem(0, profile.Roles[0] with
            { Requirement = RigRoleRequirementKind.Conditional, ConditionCapabilityId = "partial-view" }) };
        var result = RigProfileResolver.Resolve(profile, ["locomotion"]);
        Assert.Equal(RigValidationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "role-condition-conflict" && d.RoleId == "root");
    }

    [Fact]
    public void OptionalBranchNeedsItsPrerequisitesOnlyWhenUsed()
    {
        RigCapabilityProfile profile = Profile();
        profile = profile with { Roles = profile.Roles.SetItem(1, profile.Roles[1] with { Requirement = RigRoleRequirementKind.Optional }) };
        Assert.Equal("sole", Assert.Single(RigProfileResolver.Resolve(profile, ["locomotion"]).Roles).Role.Id);
        var used = RigProfileResolver.Resolve(profile, ["locomotion"], ["sole"]);
        Assert.Equal(["root", "sole"], used.Roles.Select(r => r.Role.Id));
        Assert.True(used.Roles[0].Required);
        Assert.False(used.Roles[1].Required);
    }

    [Fact]
    public void CyclesFailWithoutRecursionOverflow()
    {
        RigCapabilityProfile profile = Profile();
        var capabilityCycle = profile with { Capabilities =
            [profile.Capabilities[0] with { PrerequisiteCapabilityIds = ["partial-view"] },
             profile.Capabilities[1] with { PrerequisiteCapabilityIds = ["locomotion"] }] };
        Assert.Contains(RigProfileResolver.Resolve(capabilityCycle, ["locomotion"]).Diagnostics, d => d.Code == "capability-cycle");
        var roleCycle = profile with { Roles = profile.Roles.SetItem(0, profile.Roles[0] with { PrerequisiteRoleIds = ["sole"] }) };
        Assert.Contains(RigProfileResolver.Resolve(roleCycle, ["locomotion"]).Diagnostics, d => d.Code == "role-cycle");
    }

    [Fact]
    public void IncompleteUnrelatedConsumerDoesNotBlockPartialProfile()
    {
        RigCapabilityProfile profile = Profile();
        profile = profile with { ConsumerCoverageComplete = false,
            Consumers = profile.Consumers.SetItem(0, profile.Consumers[0] with { Inspected = false, Unknowns = ["unresolved-lookup"] }) };
        Assert.Equal(RigValidationStatus.Passed, RigProfileResolver.Resolve(profile, ["partial-view"]).Status);
        Assert.Equal(RigValidationStatus.Unverified, RigProfileResolver.Resolve(profile, ["locomotion"]).Status);
    }

    [Fact]
    public void UnknownCoverageOrWrongBuildCannotPass()
    {
        RigCapabilityProfile profile = Profile();
        var wrongBuild = profile with { ConsumerCoverageComplete = false, Identity = profile.Identity with { BuildFingerprint = new string('d', 64) } };
        Assert.Equal(RigValidationStatus.Unverified, RigProfileResolver.Resolve(wrongBuild, ["locomotion"]).Status);
        var unknownRole = profile with { Roles = profile.Roles.SetItem(1, profile.Roles[1] with { Requirement = RigRoleRequirementKind.Unknown }) };
        Assert.Equal(RigValidationStatus.Unverified, RigProfileResolver.Resolve(unknownRole, ["locomotion"]).Status);
        Assert.Equal(RigValidationStatus.Failed, RigProfileResolver.Resolve(profile, ["missing-capability"]).Status);
        Assert.Throws<ArgumentException>(() => (wrongBuild with { ConsumerCoverageComplete = true }).Validate());
    }

    [Fact]
    public void SelectionOrderDoesNotChangeClosureOrDiagnosticOrder()
    {
        RigCapabilityProfile profile = Profile();
        var first = RigProfileResolver.Resolve(profile, ["partial-view", "locomotion"]);
        var second = RigProfileResolver.Resolve(profile with { Roles = profile.Roles.Reverse().ToImmutableArray() }, ["locomotion", "partial-view", "locomotion"]);
        Assert.Equal<string>(first.CapabilityIds, second.CapabilityIds);
        Assert.Equal(first.Roles.Select(r => r.Role.Id), second.Roles.Select(r => r.Role.Id));
        Assert.Equal<RigProfileDiagnostic>(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public void DeepRoleGraphsUseBoundedHeapTraversalAndRetainConsumerAttribution()
    {
        const int count = 4096;
        static string Id(int index) => "role-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RigCapabilityProfile profile = Profile() with
        {
            Roles = Enumerable.Range(0, count).Select(i => Role(Id(i)) with
                { PrerequisiteRoleIds = i == count - 1 ? [] : [Id(i + 1)] }).ToImmutableArray(),
            Capabilities = [new() { Id = "deep", RoleIds = [Id(0)] }],
            Consumers = [new() { ConsumerId = "deep-reader", CapabilityIds = ["deep"], DiscoveredRoleIds = [Id(0)], Inspected = true, Evidence = [Evidence()] }],
        };
        var result = RigProfileResolver.Resolve(profile, ["deep"]);
        Assert.Equal(RigValidationStatus.Passed, result.Status);
        Assert.Equal(count, result.Roles.Length);
        Assert.All(result.Roles, r => Assert.Equal("deep-reader", Assert.Single(r.ConsumerIds)));
    }
}
