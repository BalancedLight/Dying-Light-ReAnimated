using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigAnimationPolicyDefaultsTests
{
    private static CustomModelDocument Document()
    {
        var document = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "extra_bones.fbx").Package.Document;
        return document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.AdaptExistingRig) };
    }

    [Fact]
    public void UnsetExtrasKeepAllTracksAndLodOffWithoutRemovingHierarchy()
    {
        var original = Document();
        var result = RigAnimationPolicyDefaults.Apply(original);
        Assert.Equal(original.Bones, result.Bones);
        Assert.Equal(original.RiggingSession!.Recipe.Entities, result.RiggingSession!.Recipe.Entities);
        Assert.Equal(RiggingSessions.ObserveSourceHierarchy(original).Length, result.RiggingSession.Recipe.ComponentPolicies.Length);
        Assert.All(result.RiggingSession.Recipe.ComponentPolicies, policy =>
        {
            Assert.Equal(RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale, policy.EmittedMask);
            Assert.Equal(RigAnimationLod.Off, policy.AnimationLod);
            Assert.Contains(policy.LodEvidence, evidence => evidence.Id == RigAnimationPolicyDefaults.EvidenceId && evidence.Kind == RigEvidenceKind.ProfileRule);
        });
        Assert.Same(result, RigAnimationPolicyDefaults.Apply(result));
    }

    [Fact]
    public void PartialManualNoneAndLodArePreservedWhileMissingOwnershipGetsDefaults()
    {
        var document = Document();
        var entity = document.RiggingSession!.Recipe.Entities[0];
        var manual = Dl1BoneScriptPolicyTests.Policy(entity.EntityId, RigAnimationComponents.None, RigAnimationLod.Lod2)
            with { Rotation = new(), LodEvidence = [] };
        document = document with { RiggingSession = document.RiggingSession with
        { Recipe = document.RiggingSession.Recipe with { ComponentPolicies = [manual] } } };
        var result = RigAnimationPolicyDefaults.Apply(document);
        var policy = result.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == entity.EntityId);
        Assert.Equal(RigAnimationComponents.None, policy.EmittedMask);
        Assert.Equal(RigAnimationLod.Lod2, policy.AnimationLod);
        Assert.Same(manual.Position, policy.Position);
        Assert.Same(manual.Scale, policy.Scale);
        Assert.Equal(new[] { RigComponentOwner.BindInherited }, policy.Rotation.Owners.ToArray());
        Assert.Equal(manual.LodRuleId, policy.LodRuleId);
        Assert.Contains(policy.LodEvidence, evidence => evidence.Description?.Contains("Preserved saved animation LOD", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CompleteManualCompositionIsNotReplaced()
    {
        var document = Document();
        var entity = document.RiggingSession!.Recipe.Entities[0];
        var manual = Dl1BoneScriptPolicyTests.Policy(entity.EntityId, RigAnimationComponents.Rotation, RigAnimationLod.Lod3);
        manual = manual with { Rotation = manual.Rotation with
        { Owners = [RigComponentOwner.Clip, RigComponentOwner.Procedural], CompositionRuleId = "explicit-composition" } };
        document = document with { RiggingSession = document.RiggingSession with
        { Recipe = document.RiggingSession.Recipe with { ComponentPolicies = [manual] } } };
        var result = RigAnimationPolicyDefaults.Apply(document);
        Assert.Same(manual, result.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == entity.EntityId));
    }

    [Fact]
    public void ExactStockDefaultsOnlyFillUnsetValues()
    {
        var document = Document();
        Guid id = document.RiggingSession!.Recipe.Entities[0].EntityId;
        var result = RigAnimationPolicyDefaults.Apply(document,
            new Dictionary<Guid, (RigAnimationComponents Mask, RigAnimationLod Lod)> { [id] = (RigAnimationComponents.Rotation, RigAnimationLod.Lod1) });
        var policy = result.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == id);
        Assert.Equal(RigAnimationComponents.Rotation, policy.EmittedMask);
        Assert.Equal(RigAnimationLod.Lod1, policy.AnimationLod);
        Assert.Contains(policy.LodEvidence, evidence => evidence.Description?.Contains("reference", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Same(result, RigAnimationPolicyDefaults.Apply(result,
            new Dictionary<Guid, (RigAnimationComponents Mask, RigAnimationLod Lod)> { [id] = (RigAnimationComponents.None, RigAnimationLod.Lod3) }));
    }
}
