using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1BoneScriptPolicyTests
{
    internal static RiggingSession WithPolicies(RiggingSession session) => session with
    {
        Recipe = session.Recipe with
        {
            ComponentPolicies = session.Recipe.Entities.Select((entity, index) => Policy(entity.EntityId,
                (RigAnimationComponents)(index % 8), (RigAnimationLod)(index % 5))).ToImmutableArray(),
        },
    };

    internal static AnimationComponentPolicy Policy(Guid id, RigAnimationComponents mask, RigAnimationLod lod)
    {
        var evidence = new RigEvidenceReference { Id = "synthetic-policy-artifact", Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = new string('a', 64), Description = "Synthetic component-policy fixture, not native behavior evidence." };
        RigChannelOwnership Channel(RigAnimationComponents component) => new()
        {
            Owners = [mask.HasFlag(component) ? RigComponentOwner.Clip : RigComponentOwner.BindInherited], Evidence = [evidence],
        };
        return new()
        {
            EntityId = id, EmittedMask = mask, AnimationLod = lod, LodRuleId = "synthetic-lod-selection", LodEvidence = [evidence],
            Position = Channel(RigAnimationComponents.Position), Rotation = Channel(RigAnimationComponents.Rotation), Scale = Channel(RigAnimationComponents.Scale),
        };
    }

    private static FbxModelAuthoringImportResult Model(bool studio)
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
        if (!studio) return model;
        var session = WithPolicies(RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig));
        return model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = session } } };
    }

    [Fact]
    public void ExplicitDecisionsFollowStableEntitiesWhenRecipeRowsAreReordered()
    {
        var model = Model(studio: true);
        var doc = model.Package.Document;
        var contract = Dl1CustomModelRigPreparer.Prepare(model).Contract;
        var first = Dl1BoneScriptPolicyResolver.Resolve(doc, contract);
        var reordered = doc with { RiggingSession = doc.RiggingSession! with { Recipe = doc.RiggingSession.Recipe with
            { ComponentPolicies = doc.RiggingSession.Recipe.ComponentPolicies.Reverse().ToImmutableArray() } } };
        var second = Dl1BoneScriptPolicyResolver.Resolve(reordered, contract);
        Assert.Equal<Dl1ResolvedBoneScriptPolicy>(first, second);
        Assert.Equal(RigAnimationComponents.None, first[0].Mask);
        Assert.Equal("NONE", first[0].Components);
        Assert.Equal("LOD_0", first[0].LodToken);
    }

    [Theory]
    [InlineData(0, "NONE")]
    [InlineData(1, "POS")]
    [InlineData(2, "ROT")]
    [InlineData(3, "POS | ROT")]
    [InlineData(4, "SCL")]
    [InlineData(5, "POS | SCL")]
    [InlineData(6, "ROT | SCL")]
    [InlineData(7, "POS | ROT | SCL")]
    public void EveryPortableMaskHasAnExplicitSourceTokenCombination(int mask, string expected) =>
        Assert.Equal(expected, Dl1BoneScriptPolicyResolver.FormatComponents((RigAnimationComponents)mask));

    [Fact]
    public async Task StudioExportRecordsDecisionsAndDoesNotAssumeRootScale()
    {
        var model = Model(studio: true);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var result = await Dl1SourceModelWriter.WriteAsync(new() { Model = model, ResourceName = "policy_fixture", OutputDirectory = directory });
            string script = await File.ReadAllTextAsync(result.BoneScriptPath);
            Assert.Contains($"SetBoneAnimTrans(\"{model.Package.Document.Bones[0].Name}\", NONE, LOD_0);", script);
            Assert.Contains("policy_fixture.components.json", result.OutputSha256.Keys);
            string audit = await File.ReadAllTextAsync(Path.Combine(directory, "policy_fixture.components.json"));
            Assert.Contains("\"runtimeBehaviorVerified\": false", audit);
            Assert.Contains("synthetic-policy-artifact", audit);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public async Task UnresolvedStudioPolicyFailsBeforeTouchingExistingOutput()
    {
        var model = Model(studio: true);
        var doc = model.Package.Document;
        model = model with { Package = model.Package with { Document = doc with { RiggingSession = doc.RiggingSession! with
            { Recipe = doc.RiggingSession.Recipe with { ComponentPolicies = [] } } } } };
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string sentinel = Path.Combine(directory, "existing.txt");
            await File.WriteAllTextAsync(sentinel, "retain");
            await Assert.ThrowsAsync<InvalidDataException>(() => Dl1SourceModelWriter.WriteAsync(new() { Model = model, ResourceName = "policy_fixture", OutputDirectory = directory }));
            Assert.Equal("retain", await File.ReadAllTextAsync(sentinel));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void UnknownOwnershipAndMissingLodEvidenceCannotBeExported()
    {
        var model = Model(studio: true);
        var doc = model.Package.Document;
        var contract = Dl1CustomModelRigPreparer.Prepare(model).Contract;
        var policy = doc.RiggingSession!.Recipe.ComponentPolicies[0];
        foreach (var invalid in new[] { policy with { Position = new() }, policy with { LodEvidence = [] }, policy with { AnimationLod = null } })
        {
            var changed = doc with { RiggingSession = doc.RiggingSession with { Recipe = doc.RiggingSession.Recipe with
                { ComponentPolicies = doc.RiggingSession.Recipe.ComponentPolicies.SetItem(0, invalid) } } };
            Assert.Throws<InvalidDataException>(() => Dl1BoneScriptPolicyResolver.Resolve(changed, contract));
        }
    }

    [Fact]
    public async Task LegacyModelKeepsItsExistingBscrPolicy()
    {
        var model = Model(studio: false);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var result = await Dl1SourceModelWriter.WriteAsync(new() { Model = model, ResourceName = "legacy_fixture", OutputDirectory = directory });
            string script = await File.ReadAllTextAsync(result.BoneScriptPath);
            Assert.Contains($"SetBoneAnimTrans(\"{model.Package.Document.Bones[0].Name}\", POS | ROT | SCL, LOD_OFF);", script);
            Assert.DoesNotContain("legacy_fixture.components.json", result.OutputSha256.Keys);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void CompiledReadBackChecksFlagsAndReportsCanonicalNames()
    {
        var hierarchy = CompactMeshDecoder.Decode(RpackTestData.BuildCompactMeshPayload());
        var original = hierarchy.Entities[0];
        var expected = Policy(Guid.NewGuid(), RigAnimationComponents.None, RigAnimationLod.Off);
        var policy = new Dl1ResolvedBoneScriptPolicy(expected.EntityId, 0, original.Name.ToUpperInvariant(), expected.EmittedMask!.Value, expected.AnimationLod!.Value, expected);
        var node = original with { Name = original.Name.ToLowerInvariant(), Flags = (original.Flags & ~0x7700u) | 0x4000u };
        var compiled = hierarchy with { Entities = hierarchy.Entities.Select(e => e.Index == original.Index ? node : e).ToArray() };
        var result = Assert.Single(Dl1CompiledBoneScriptValidator.Validate(compiled, [policy]));
        Assert.Equal(node.Name, result.CompiledName);
        Assert.Equal(0u, result.ComponentBits);
        Assert.Equal(0x4000u, result.LodBits);
        var altered = compiled with { Entities = compiled.Entities.Select(e => e.Index == node.Index ? e with { Flags = e.Flags | 0x100u } : e).ToArray() };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledBoneScriptValidator.Validate(altered, [policy]));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledBoneScriptValidator.Validate(compiled, [policy with { Name = "absent_node" }]));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledBoneScriptValidator.Validate(compiled, [policy, policy]));
    }

    [Fact]
    public void ComponentAndLodChangesInvalidateTheCompilerInputFingerprint()
    {
        var model = Model(studio: true);
        string before = Dl1OfficialModelCompiler.CalculateInputFingerprint(model, "generic", "default", null);
        var session = model.Package.Document.RiggingSession!;
        var original = session.Recipe.ComponentPolicies[0];
        foreach (var changed in new[] { original with { EmittedMask = RigAnimationComponents.Rotation }, original with { AnimationLod = RigAnimationLod.Off } })
        {
            var replacement = model with { Package = model.Package with { Document = model.Package.Document with
            {
                RiggingSession = session with { Recipe = session.Recipe with { ComponentPolicies = session.Recipe.ComponentPolicies.SetItem(0, changed) } },
            } } };
            Assert.NotEqual(before, Dl1OfficialModelCompiler.CalculateInputFingerprint(replacement, "generic", "default", null));
        }
    }
}
