using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RuntimeRigRecipeTests
{
    [Fact]
    public void ImportConversionAndAnatomyMustBeExplicitAndConsistent()
    {
        new RigScalePolicy().Validate();
        Assert.Throws<ArgumentException>(() => new RigScalePolicy { ImportConversionAlreadyApplied = true }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RigScalePolicy { RuntimeUniformBodyScale = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RigScalePolicy { SourceMetersPerUnit = double.NaN }.Validate());
        var converted = new RigScalePolicy { SourceMetersPerUnit = .01, SourceToAuthoring = TransformMatrix.Identity, ImportConversionAlreadyApplied = true };
        converted.Validate();
        Assert.Equal(1, converted.RuntimeUniformBodyScale);
        Assert.Equal(RigAnatomyPolicy.Preserve, converted.Anatomy);
        var recipe = new RuntimeRigRecipe { MotionStrategy = RigMotionStrategy.ConformToReference };
        Assert.Throws<ArgumentException>(recipe.Validate);
        (recipe with { ScalePolicy = new() { Anatomy = RigAnatomyPolicy.ExplicitConformance } }).Validate();
        Assert.Throws<ArgumentException>(() => new RuntimeRigRecipe { ScalePolicy = new() { Anatomy = RigAnatomyPolicy.ExplicitConformance } }.Validate());
    }

    [Fact]
    public void MixedChannelOwnersRequireKnownComposition()
    {
        new RigChannelOwnership { Owners = [RigComponentOwner.Unknown] }.Validate();
        var mixed = new RigChannelOwnership { Owners = [RigComponentOwner.Clip, RigComponentOwner.RuntimeBodyScale] };
        Assert.Throws<ArgumentException>(mixed.Validate);
        (mixed with { CompositionRuleId = "synthetic-composition" }).Validate();
        Assert.Throws<ArgumentException>(() => new RigChannelOwnership { Owners = [RigComponentOwner.Unknown, RigComponentOwner.Clip], CompositionRuleId = "synthetic" }.Validate());
        Assert.Throws<ArgumentException>(() => new RigChannelOwnership { Owners = [RigComponentOwner.Clip, RigComponentOwner.Clip] }.Validate());
    }

    [Fact]
    public void HelperFramesCannotCrossAssetsOrUseMorphEntities()
    {
        Guid asset = Guid.NewGuid();
        var root = new RigEntityBinding { EntityId = Guid.NewGuid(), OwnerAssetId = asset, NativeName = "root", Kind = RigNativeEntityKind.Bone, Imported = false };
        var child = root with { EntityId = Guid.NewGuid(), NativeName = "helper", Kind = RigNativeEntityKind.Helper };
        var recipe = new RuntimeRigRecipe
        {
            Entities = [root, child], Assignments = [new("contact", child.EntityId)],
            Helpers = [new() { EntityId = child.EntityId, OwnerAssetId = asset, ParentEntityId = root.EntityId, RoleId = "contact" }],
        };
        recipe.Validate();
        Assert.Throws<ArgumentException>(() => (recipe with { Entities = [root, child with { Kind = RigNativeEntityKind.Morph }] }).Validate());
        Assert.Throws<ArgumentException>(() => (recipe with { Entities = [root with { OwnerAssetId = Guid.NewGuid() }, child] }).Validate());
        var cycle = recipe with { Helpers = recipe.Helpers.Add(new() { EntityId = root.EntityId, OwnerAssetId = asset, ParentEntityId = child.EntityId, RoleId = "root" }) };
        Assert.Throws<ArgumentException>(cycle.Validate);
    }

    [Fact]
    public void HelperBoundsAndFramesRejectIncompleteOrDegenerateData()
    {
        var helper = new HelperRecipe { EntityId = Guid.NewGuid(), OwnerAssetId = Guid.NewGuid(), ParentEntityId = Guid.NewGuid(), RoleId = "contact" };
        helper.Validate();
        Assert.Throws<ArgumentException>(() => (helper with { BoundsCenter = Vector3D.Zero }).Validate());
        Assert.Throws<ArgumentException>(() => (helper with { BoundsCenter = Vector3D.Zero, BoundsHalfExtents = new(-1, 1, 1) }).Validate());
        Assert.Throws<ArgumentException>(() => (helper with { LocalFrame = default }).Validate());
        Assert.Throws<ArgumentException>(() => (helper with { DetectionConfidence = double.NaN }).Validate());
    }

    [Fact]
    public void CoverageCannotClaimCompletenessWithoutScopedConsumers()
    {
        RigCapabilityProfile profile = RigProfileResolverTests.Profile();
        Assert.Throws<ArgumentException>(() => (profile with { Consumers = profile.Consumers.SetItem(0, profile.Consumers[0] with { CapabilityIds = [] }) }).Validate());
        Assert.Throws<ArgumentException>(() => (profile with { Consumers = profile.Consumers.SetItem(0, profile.Consumers[0] with { CapabilityIds = ["unknown"] }) }).Validate());
        Assert.Throws<ArgumentException>(() => (profile with { Roles = profile.Roles.SetItem(0, profile.Roles[0] with { PrerequisiteRoleIds = ["unknown"] }) }).Validate());
    }

    [Fact]
    public void EntityAndAssetIdentitiesCannotHaveCompetingBindings()
    {
        Guid asset = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new RuntimeRigRecipe { AssetRoles = [new("character", asset), new("character", Guid.NewGuid())] }.Validate());
        var entity = new RigEntityBinding { EntityId = Guid.NewGuid(), OwnerAssetId = asset, SourceEntityId = "source-1", NativeName = "root" };
        Assert.Throws<ArgumentException>(() => new RuntimeRigRecipe { Entities = [entity, entity with { EntityId = Guid.NewGuid() }] }.Validate());
        new RuntimeRigRecipe { Entities = [entity, entity with { EntityId = Guid.NewGuid(), OwnerAssetId = Guid.NewGuid() }] }.Validate();
    }
}
