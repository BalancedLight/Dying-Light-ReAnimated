using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigRoleAssignmentValidatorTests
{
    private static (RigCapabilityProfile Profile, RuntimeRigRecipe Recipe, ImmutableArray<RigParentObservation> Parents) Fixture()
    {
        RigCapabilityProfile profile = RigProfileResolverTests.Profile();
        Guid asset = Guid.NewGuid();
        var root = new RigEntityBinding { EntityId = Guid.NewGuid(), OwnerAssetId = asset, NativeName = "root", Kind = RigNativeEntityKind.Helper, Imported = false };
        var sole = root with { EntityId = Guid.NewGuid(), NativeName = "sole" };
        var extra = root with { EntityId = Guid.NewGuid(), NativeName = "unknown-extra", Kind = RigNativeEntityKind.Unknown };
        return (profile, new RuntimeRigRecipe
        {
            Profile = profile.Identity, SelectedCapabilityIds = ["locomotion"], AssetRoles = [new("character", asset)],
            Entities = [root, sole, extra], Assignments = [new("root", root.EntityId), new("sole", sole.EntityId)],
        }, [new(root.EntityId, null), new(sole.EntityId, root.EntityId), new(extra.EntityId, root.EntityId)]);
    }

    [Fact]
    public void ValidAssignmentsPreserveUnknownNodesAndIgnorePhysicalRowOrder()
    {
        var (profile, recipe, parents) = Fixture();
        Assert.Equal(RigValidationStatus.Passed, RigRoleAssignmentValidator.Validate(profile, recipe, parents).Status);
        var reordered = recipe with { Entities = recipe.Entities.Reverse().ToImmutableArray(), Assignments = recipe.Assignments.Reverse().ToImmutableArray() };
        Assert.Equal(RigValidationStatus.Passed, RigRoleAssignmentValidator.Validate(profile, reordered, parents.Reverse().ToImmutableArray()).Status);
        Assert.Equal(3, recipe.Entities.Length);
        Assert.Equal(RigNativeEntityKind.Unknown, recipe.Entities[2].Kind);
    }

    [Fact]
    public void MissingContactReportsItsConsumerAndCorrectiveOperation()
    {
        var (profile, recipe, parents) = Fixture();
        var result = RigRoleAssignmentValidator.Validate(profile, recipe with { Assignments = [recipe.Assignments[0]] }, parents);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "role-multiplicity");
        Assert.Equal("sole", diagnostic.RoleId);
        Assert.Equal<string>(["contact-reader"], diagnostic.ConsumerIds);
        Assert.Contains("Assign or create", diagnostic.CorrectiveOperation);
        Assert.Equal(RigValidationStatus.Failed, result.Status);
    }

    [Fact]
    public void WrongParentFailsAndMissingObservationStaysUnverified()
    {
        var (profile, recipe, parents) = Fixture();
        Guid sole = recipe.Entities[1].EntityId;
        var wrong = parents.SetItem(1, new(sole, recipe.Entities[2].EntityId));
        var result = RigRoleAssignmentValidator.Validate(profile, recipe, wrong);
        Assert.Contains(result.Diagnostics, d => d.Code == "role-parent-conflict" && d.EntityId == sole);
        Assert.Equal(RigValidationStatus.Unverified, RigRoleAssignmentValidator.Validate(profile, recipe, [parents[0]]).Status);
    }

    [Fact]
    public void AncestorConstraintTraversesObservedIntermediatesAndDetectsCycles()
    {
        var (profile, recipe, parents) = Fixture();
        profile = profile with { Roles = profile.Roles.SetItem(1, profile.Roles[1] with { ParentConstraint = RigRoleParentConstraint.Ancestor }) };
        parents = parents.SetItem(1, new(recipe.Entities[1].EntityId, recipe.Entities[2].EntityId));
        Assert.Equal(RigValidationStatus.Passed, RigRoleAssignmentValidator.Validate(profile, recipe, parents).Status);
        parents = parents.SetItem(2, new(recipe.Entities[2].EntityId, recipe.Entities[1].EntityId));
        Assert.Contains(RigRoleAssignmentValidator.Validate(profile, recipe, parents).Diagnostics, d => d.Code == "role-parent-cycle");
    }

    [Fact]
    public void OwnerAndTypeConflictsDoNotPassOnMatchingNames()
    {
        var (profile, recipe, parents) = Fixture();
        recipe = recipe with { Entities = recipe.Entities.SetItem(1, recipe.Entities[1] with { OwnerAssetId = Guid.NewGuid(), Kind = RigNativeEntityKind.Morph }) };
        var result = RigRoleAssignmentValidator.Validate(profile, recipe, parents);
        Assert.Contains(result.Diagnostics, d => d.Code == "role-owner-conflict");
        Assert.Contains(result.Diagnostics, d => d.Code == "role-type-conflict");
        Assert.Contains(result.Diagnostics, d => d.Code == "hierarchy-observation-invalid");
    }

    [Fact]
    public void CaseSensitiveNativeNamesNeedBuildVerifiedAliases()
    {
        var (profile, recipe, parents) = Fixture();
        recipe = recipe with { Entities = recipe.Entities.SetItem(1, recipe.Entities[1] with { NativeName = "Sole" }) };
        Assert.Contains(RigRoleAssignmentValidator.Validate(profile, recipe, parents).Diagnostics, d => d.Code == "role-native-name-conflict");
        profile = profile with { Roles = profile.Roles.SetItem(1, profile.Roles[1] with
            { Aliases = [new() { Name = "Sole", LookupRuleId = "synthetic-alias-rule", Evidence = [RigProfileResolverTests.Evidence()] }] }) };
        Assert.Equal(RigValidationStatus.Passed, RigRoleAssignmentValidator.Validate(profile, recipe, parents).Status);
    }

    [Fact]
    public void StaleProfileAndCompetingAssignmentsFail()
    {
        var (profile, recipe, parents) = Fixture();
        var stale = recipe with { Profile = recipe.Profile! with { ContentSha256 = new string('d', 64) } };
        Assert.Contains(RigRoleAssignmentValidator.Validate(profile, stale, parents).Diagnostics, d => d.Code == "profile-identity-mismatch");
        var duplicate = recipe with { Assignments = recipe.Assignments.Add(new("sole", recipe.Entities[2].EntityId)) };
        Assert.Contains(RigRoleAssignmentValidator.Validate(profile, duplicate, parents).Diagnostics, d => d.Code == "role-multiplicity");
    }
}
