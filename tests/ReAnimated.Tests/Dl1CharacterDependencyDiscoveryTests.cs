using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class Dl1CharacterDependencyDiscoveryTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task AutomaticClothCandidatePreservesIncludesWithoutAssumingActorConfiguration()
    {
        var catalog=new FakeCatalog();var root=catalog.AddMesh();
        var cloth=catalog.AddFile("characters/hero.mpcloth","MeshPartCloth(\"fabric.phx\",1,1);");
        var physics=catalog.AddFile("characters/fabric.phx","MeshPartCloth(){ Setting(1); }");
        var result=await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(),root,catalog);
        var candidate=Assert.Single(result.Roots);
        Assert.Equal(Dl1CharacterDependencyBasis.AutomaticClothName,candidate.Basis);
        Assert.Equal(Dl1CharacterDependencyStatus.Candidate,candidate.Status);
        Assert.Equal(cloth.Id.StableKey,candidate.Selected!.Id.StableKey);
        Assert.Contains(result.References,r=>r.Selected?.Id.StableKey==physics.Id.StableKey);
        Assert.Empty(result.VerifiedCompanionRoots);
    }

    [Fact]
    public async Task PresetMeshFieldRetainsSourceWithoutTraversingOtherActors()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        RetailAssetRecord preset = catalog.AddFile("data/presets/actors.pre",
            "PresetDef(\"Character\") { Preset(\"ActorA\") { SetField(\"MeshName\", \"hero.msh\"); SetField(\"m_FaceMimicFile\", \"hero.fed\"); } " +
            "Preset(\"ActorB\") { SetField(\"MeshName\", \"other.msh\"); SetField(\"m_PhysicsScript\", \"other.phx\"); } }");
        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        Dl1CharacterDependencyFinding selected = Assert.Single(found.Roots);
        Assert.Equal(preset.Id.StableKey, selected.Selected!.Id.StableKey);
        Assert.Equal(Dl1CharacterDependencyStatus.Candidate, selected.Status);
        Assert.Equal(ReAnimated.Core.ModelAuthoring.CharacterSubsystem.Helpers, selected.Subsystem);
        Assert.Empty(found.References);
        Assert.Empty(found.VerifiedCompanionRoots);
    }

    [Fact]
    public async Task MeshNameFieldOutsidePresetDoesNotDeclareAnActor()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("data/presets/actors.pre", "SetField(\"MeshName\", \"hero.msh\");");
        var found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        Assert.Empty(found.Roots);
        Assert.Empty(found.References);
    }

    private const string ValidBel = "BodyElement(Head, 1, 0, 0, 0, \"Head\");";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ExplicitMeshDeclarationFindsWrapperAndItsExactInclude()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        RetailAssetRecord wrapper = catalog.AddFile("actors/character.scr", "Model(\"hero.msh\"); !include(\"shared.def\");");
        RetailAssetRecord include = catalog.AddFile("actors/shared.def", "Setting(1);");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);

        Dl1CharacterDependencyFinding linked = Assert.Single(found.Roots);
        Assert.Equal(Dl1CharacterDependencyBasis.ExplicitMeshDeclaration, linked.Basis);
        Assert.Equal(Dl1CharacterDependencyStatus.Candidate, linked.Status);
        Assert.Equal(wrapper.Id.StableKey, linked.Selected!.Id.StableKey);
        Assert.Equal(include.Id.StableKey, Assert.Single(found.References).Selected!.Id.StableKey);
        Assert.Empty(found.VerifiedCompanionRoots);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task AbsentRequestedBelUsesStandardHumanFallbackOnlyWhenOptedIn()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        RetailAssetRecord fallback = catalog.AddFile("default_elements.bel", ValidBel);

        Dl1CharacterDependencyDiscoveryResult unspecified = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        Dl1CharacterDependencyDiscoveryResult standard = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Assert.Empty(unspecified.Roots);
        Dl1CharacterDependencyFinding selected = Assert.Single(standard.Roots);
        Assert.Equal(Dl1CharacterDependencyBasis.StandardHumanBelFallback, selected.Basis);
        Assert.Equal(Dl1CharacterDependencyStatus.Verified, selected.Status);
        Assert.Equal(fallback.Id.LogicalId, Assert.Single(standard.VerifiedCompanionRoots));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task MalformedRequestedBelDoesNotUseAbsentFileFallback()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("hero.bel", "BodyElement(\"not-a-symbol\");");
        catalog.AddFile("default_elements.bel", ValidBel);

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding selected = Assert.Single(found.Roots);
        Assert.Equal(Dl1CharacterDependencyBasis.StandardHumanBel, selected.Basis);
        Assert.Equal(Dl1CharacterDependencyStatus.Malformed, selected.Status);
        Assert.Empty(found.VerifiedCompanionRoots);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task AmbiguousBasenameIsReportedWithoutSelectingAProvider()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", "Model(\"hero.msh\"); !include(\"common.def\");");
        catalog.AddFile("one/common.def", "Setting(1);");
        catalog.AddFile("two/common.def", "Setting(2);");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);

        Dl1CharacterDependencyFinding include = Assert.Single(found.References);
        Assert.Equal(Dl1CharacterDependencyStatus.Ambiguous, include.Status);
        Assert.Null(include.Selected);
        Assert.Equal(2, include.Candidates.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task UnrelatedScriptDoesNotBecomeACompanionByName()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("hero.scr", "Model(\"other.msh\");");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);

        Assert.Empty(found.Roots);
        Assert.Empty(found.References);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SourceHashChangeBlocksRequestedBelWithoutFallingBack()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("hero.bel", ValidBel, expectedHash: Sha("different source"));
        catalog.AddFile("default_elements.bel", ValidBel);

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding selected = Assert.Single(found.Roots);
        Assert.Equal(Dl1CharacterDependencyStatus.SourceChanged, selected.Status);
        Assert.Equal(Sha(ValidBel), selected.ObservedSha256);
        Assert.Empty(found.VerifiedCompanionRoots);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task UnverifiedFacialMentionAndClothPhysicsRemainReviewable()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr",
            "Model(\"hero.msh\"); FacialScript(\"face.fed\"); !include(\"MeshPartCloth.def\"); BehaviorSet(\"coat.phx\");");
        catalog.AddFile("actors/face.fed", "Setting(1);");
        catalog.AddFile("actors/MeshPartCloth.def", "Setting(1);");
        catalog.AddFile("actors/coat.phx", "Setting(1);");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);

        Assert.Contains(found.References, finding => finding.Basis == Dl1CharacterDependencyBasis.SourceMention &&
            finding.Subsystem == ReAnimated.Core.ModelAuthoring.CharacterSubsystem.FacialDefinitions &&
            finding.Status == Dl1CharacterDependencyStatus.Candidate);
        Assert.Contains(found.References, finding => finding.RequestedName == "coat.phx" &&
            finding.Subsystem == ReAnimated.Core.ModelAuthoring.CharacterSubsystem.Cloth);
        Assert.DoesNotContain(found.References, finding => finding.RequestedName == "hero.msh");
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ChangedWrapperSourceIsReportedButNotPromoted()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", "Model(\"hero.msh\");", expectedHash: Sha("old source"));

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);

        Assert.Equal(Dl1CharacterDependencyStatus.SourceChanged, Assert.Single(found.Roots).Status);
        Assert.Empty(found.VerifiedCompanionRoots);
        Assert.Empty(found.References);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task VerifiedSameProviderParentIncludeResolvesExactVirtualPath()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        const string provider = "shared-source";
        catalog.AddFile("hero.bel", ValidBel + " !include(\"data/characters/group/actor.bel\");", providerId: provider);
        RetailAssetRecord actor = catalog.AddFile("data/characters/group/actor.bel",
            ValidBel + " !include(\"..\\..\\surface.def\");", providerId: provider);
        RetailAssetRecord exact = catalog.AddFile("data/surface.def", "Setting(7);", providerId: provider);
        catalog.AddFile("elsewhere/surface.def", "Setting(99);");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(
            Mesh(), root, catalog, new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Assert.Equal(Dl1CharacterDependencyStatus.Verified, Assert.Single(found.Roots,
            row => row.Basis == Dl1CharacterDependencyBasis.StandardHumanBel).Status);
        Assert.Contains(found.References, row => row.Selected?.Id.StableKey == actor.Id.StableKey);
        Dl1CharacterDependencyFinding include = Assert.Single(found.References,
            row => row.RequestedName.Contains("surface.def", StringComparison.Ordinal));
        Assert.Equal(Dl1CharacterDependencyStatus.Verified, include.Status);
        Assert.Equal(exact.Id.StableKey, include.Selected!.Id.StableKey);
        Assert.Equal(actor.Id.StableKey, include.ReferencedBy);
        Assert.Single(include.Candidates);
        Assert.Equal("..\\..\\surface.def", include.RequestedName);
    }

    [Theory]
    [InlineData("..\\..\\..\\..\\surface.def")]
    [InlineData("/data/surface.def")]
    [Trait("ValidationTier", "Hermetic")]
    public async Task UnsafeParentIncludeStaysMalformedDespiteUniqueBasename(string includeName)
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        const string provider = "shared-source";
        catalog.AddFile("hero.bel", ValidBel + " !include(\"data/characters/group/actor.bel\");", providerId: provider);
        catalog.AddFile("data/characters/group/actor.bel",
            ValidBel + $" !include(\"{includeName}\");", providerId: provider);
        catalog.AddFile("data/surface.def", "Setting(7);", providerId: provider);

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(
            Mesh(), root, catalog, new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding include = Assert.Single(found.References,
            row => row.RequestedName == includeName);
        Assert.Equal(Dl1CharacterDependencyStatus.Malformed, include.Status);
        Assert.Null(include.Selected);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CrossProviderParentIncludeRemainsCandidateForReview()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        const string provider = "declaring-source";
        catalog.AddFile("hero.bel", ValidBel + " !include(\"data/characters/group/actor.bel\");", providerId: provider);
        catalog.AddFile("data/characters/group/actor.bel",
            ValidBel + " !include(\"../../surface.def\");", providerId: provider);
        RetailAssetRecord other = catalog.AddFile("data/surface.def", "Setting(7);", providerId: "other-source");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(
            Mesh(), root, catalog, new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding include = Assert.Single(found.References,
            row => row.RequestedName == "../../surface.def");
        Assert.Equal(Dl1CharacterDependencyStatus.Candidate, include.Status);
        Assert.Equal(other.Id.StableKey, include.Selected!.Id.StableKey);
        Assert.Contains("across", include.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SameCanonicalPathKeepsPhysicalAlternativesAndCrossProviderWinnerReviewable()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        const string provider = "declaring-source";
        catalog.AddFile("hero.bel", ValidBel + " !include(\"data/characters/group/actor.bel\");", providerId: provider);
        catalog.AddFile("data/characters/group/actor.bel",
            ValidBel + " !include(\"../../surface.def\");", providerId: provider);
        RetailAssetRecord shadowed = catalog.AddFile("data/surface.def", "Setting(1);",
            priority: 1, providerId: provider);
        RetailAssetRecord winner = catalog.AddFile("data/surface.def", "Setting(2);",
            priority: 10, providerId: "higher-priority-source");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(
            Mesh(), root, catalog, new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding include = Assert.Single(found.References,
            row => row.RequestedName == "../../surface.def");
        Assert.Equal(Dl1CharacterDependencyStatus.Candidate, include.Status);
        Assert.Equal(winner.Id.StableKey, include.Selected!.Id.StableKey);
        Assert.Equal(2, include.Candidates.Length);
        Assert.Contains(include.Candidates, row => row.Id.StableKey == shadowed.Id.StableKey);
    }

    [Theory]
    [InlineData("same-source", "other-source")]
    [InlineData("other-source", "same-source")]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ConditionalRelicMeshUsesExactCompiledStemOnlyAsCandidate(
        string shadowedProvider, string winningProvider)
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("hero.bel",
            ValidBel + " AddRelics(\"Arm\", PHYSICS_SINGLE, \"part.phx\", \"\", [0,0,0], [0,0,0]);",
            providerId: "same-source");
        RetailAssetRecord shadowed = catalog.AddCompiled("arm", Rp6lResourceTypes.Mesh,
            priority: 1, providerId: shadowedProvider);
        RetailAssetRecord winner = catalog.AddCompiled("arm", Rp6lResourceTypes.Mesh,
            priority: 10, providerId: winningProvider);
        catalog.AddCompiled("heroarm", Rp6lResourceTypes.Texture);
        catalog.AddCompiled("hero_arms", Rp6lResourceTypes.Mesh);
        catalog.AddFile("heroarm", "Unrelated(1);");

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(
            Mesh(), root, catalog, new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding[] branches = found.References
            .Where(row => row.Basis == Dl1CharacterDependencyBasis.ConditionalRelicMesh).ToArray();
        Assert.Equal(3, branches.Length);
        Dl1CharacterDependencyFinding standalone = Assert.Single(branches,
            row => row.RequestedName == "Arm.msh");
        Assert.Equal(Dl1CharacterDependencyStatus.Candidate, standalone.Status);
        Assert.Equal(Rp6lResourceTypes.Mesh, standalone.Selected!.Id.ResourceType);
        Assert.Equal(RetailAssetNamespace.RpackResource, standalone.Selected.Id.Namespace);
        Assert.Equal(winner.Id.StableKey, standalone.Selected.Id.StableKey);
        Assert.Equal(2, standalone.Candidates.Length);
        Assert.Contains(standalone.Candidates, row => row.Id.StableKey == shadowed.Id.StableKey);
        Assert.Contains("conditional", standalone.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.All(branches.Where(row => row.RequestedName != "Arm.msh"), row =>
        {
            Assert.Equal(Dl1CharacterDependencyStatus.Missing, row.Status);
            Assert.Null(row.Selected);
        });
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task LogicalPrecedenceRetainsShadowedPhysicalSource()
    {
        var catalog = new FakeCatalog();
        RetailAssetRecord root = catalog.AddMesh();
        catalog.AddFile("hero.bel", "BodyElement(Head, 1, 0, 0, 0, \"old\");", priority: 1);
        RetailAssetRecord winner = catalog.AddFile("hero.bel", ValidBel, priority: 10);

        Dl1CharacterDependencyDiscoveryResult found = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Dl1CharacterDependencyFinding selected = Assert.Single(found.Roots);
        Assert.Equal(winner.Id.StableKey, selected.Selected!.Id.StableKey);
        Assert.Equal(2, selected.Candidates.Length);
        Assert.Equal(Sha(ValidBel), selected.ObservedSha256);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task NestedEffectsAreTraversedWithoutScanningUnrelatedEffectDefinitions()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("default_elements.bel", ValidBel + " AddRelics(\"Part\", PHYSICS_SINGLE, \"part.phx\", \"sequence.fx\", [0,0,0], [0,0,0]);");
        catalog.AddFile("sequence.fx", "SequenceDef() { ParticleEmiter(\"emitter.fx\", \"particle.fx\"); }");
        catalog.AddFile("emitter.fx", "EmiterDef() { ParticleEmiter(\"nested_emitter.fx\", \"nested_particle.fx\"); }");
        catalog.AddFile("particle.fx", "ParticleDef() { Material(\"effect_material.mat\", \"effect_material.mat\"); }");
        catalog.AddFile("nested_emitter.fx", "EmiterDef() {}");
        var nested = catalog.AddFile("nested_particle.fx", "ParticleDef() {}");
        catalog.AddFile("unrelated.fx", "Model(\"hero.msh\"); Sequence(\"other.fx\");");

        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });

        Assert.DoesNotContain(result.Roots, finding => finding.RequestedName == "unrelated.fx");
        var particle = Assert.Single(result.References, finding => finding.RequestedName == "nested_particle.fx");
        Assert.Equal(nested.Id, particle.Selected!.Id);
        Assert.Equal(Dl1CharacterDependencyStatus.Verified, particle.Status);
        Assert.NotNull(particle.ObservedSha256);
        Assert.Contains(result.References, finding => finding.RequestedName == "effect_material.mat" &&
            finding.Subsystem == ReAnimated.Core.ModelAuthoring.CharacterSubsystem.Materials &&
            finding.Status == Dl1CharacterDependencyStatus.Missing);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("bound", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task PackedEffectNamesResolveGloballyWithoutDirectoryOrBasenameFallback()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("default_elements.bel", ValidBel + " AddRelics(\"Part\", PHYSICS_SINGLE, \"part.phx\", \"effects/sequence.fx\", [0,0,0], [0,0,0]);");
        catalog.AddPackedEffect("effects/sequence.fx", "SequenceDef() { ParticleEmiter(\"emitter.fx\", \"absent.fx\"); }");
        var expected = catalog.AddPackedEffect("emitter.fx", "EmiterDef() {}");
        catalog.AddPackedEffect("effects/emitter.fx", "Wrong();");
        catalog.AddPackedEffect("effects/absent.fx", "Wrong();");
        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { HostContext = Dl1CharacterHostContext.StandardHumanAiVis });
        var emitter = Assert.Single(result.References, finding => finding.RequestedName == "emitter.fx");
        Assert.Equal(expected.Id, emitter.Selected!.Id);
        Assert.Equal(Dl1CharacterDependencyStatus.Verified, emitter.Status);
        var absent = Assert.Single(result.References, finding => finding.RequestedName == "absent.fx");
        Assert.Equal(Dl1CharacterDependencyStatus.Missing, absent.Status);
        Assert.Null(absent.Selected);
    }

    [Fact]
    public async Task LiteralMeatPartFilenameResolvesExactCompiledMeshWithoutChoosingConditionalBranches()
    {
        var catalog=new FakeCatalog();var root=catalog.AddMesh();
        catalog.AddFile("hero.bel",ValidBel+" MeatParts() { AddMeatPart(\"generic_part.msh\", 1); }");
        var expected=catalog.AddCompiled("generic_part",Rp6lResourceTypes.Mesh);
        var result=await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(),root,catalog,new(){HostContext=Dl1CharacterHostContext.StandardHumanAiVis});
        var part=Assert.Single(result.References,row=>row.RequestedName=="generic_part.msh");
        Assert.Equal(expected.Id,part.Selected!.Id);Assert.Equal(Dl1CharacterDependencyStatus.Verified,part.Status);
        Assert.Equal(Dl1CharacterDependencyBasis.DeclaredResource,part.Basis);
    }

    [Theory]
    [InlineData(Dl1CharacterDependencyStatus.Missing,true)]
    [InlineData(Dl1CharacterDependencyStatus.SourceChanged,false)]
    [InlineData(Dl1CharacterDependencyStatus.Ambiguous,false)]
    public void OnlyMissingMaterialFindingsCanBeSatisfiedByExactRetainedPayloads(Dl1CharacterDependencyStatus status,bool expected)
    {
        var inventory=CharacterMaterialReceiptTests.Create().Document.CharacterResources!;
        var finding=new Dl1CharacterDependencyFinding(Dl1CharacterDependencyBasis.SourceMention,status,
            ReAnimated.Core.ModelAuthoring.CharacterSubsystem.Materials,"generic.mat","parent",null,[],null,"Catalog source absent.");
        Assert.Equal(expected,Dl1CharacterDependencyDiscovery.IsSatisfiedByRetainedMaterial(finding,inventory));
        Assert.False(Dl1CharacterDependencyDiscovery.IsSatisfiedByRetainedMaterial(finding with {RequestedName="other.mat"},inventory));
    }

    [Fact]
    public async Task FullScanRecordsExactRootCatalogAndSkippedSourceCoverage()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", "Model(\"hero.msh\");");
        catalog.AddFile("data/opaque.def", "Unparsed assignment = 17;");
        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        var scan = Assert.IsType<Dl1CharacterDependencyScanProvenance>(result.ScanProvenance);
        Assert.True(scan.IsComplete);
        Assert.Equal(root.Id.LogicalId.StableKey, scan.RootLogicalId);
        Assert.Equal(root.Id.SourceFingerprint, scan.RootSourceFingerprint);
        Assert.Equal(2, scan.EffectiveScriptCount);
        Assert.Equal(2, scan.AttemptedScriptCount);
        Assert.Equal(scan.CatalogSnapshotSha256, scan.FinalCatalogSnapshotSha256);
        Assert.Equal(Dl1CharacterHostContext.Unspecified, scan.HostContext);
        Assert.Contains(scan.Observations, observation => observation.Status == Dl1CharacterDependencyStatus.Malformed);
        Assert.All(scan.Observations, observation => Assert.Equal(64, observation.AssetIdentitySha256.Length));
    }

    [Fact]
    public async Task IncompleteScansCannotProduceCompletedCoverage()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", "Model(\"hero.msh\"); !include(\"next.def\");");
        catalog.AddFile("actors/next.def", "Setting(1);");
        var bounded = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { MaximumScannedScripts = 1 });
        Assert.False(bounded.ScanProvenance!.IsComplete);
        Assert.Equal(2, bounded.ScanProvenance.EffectiveScriptCount);
        Assert.Equal(1, bounded.ScanProvenance.AttemptedScriptCount);
        Assert.NotEmpty(bounded.ScanProvenance.LimitFindings);
        var shortRead = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog,
            new() { MaximumSourceBytes = 1, MaximumTotalSourceBytes = 1 });
        Assert.False(shortRead.ScanProvenance!.IsComplete);
        Assert.Contains(shortRead.ScanProvenance.Observations, observation => observation.Status == Dl1CharacterDependencyStatus.BoundExceeded);
    }

    [Fact]
    public async Task IndexedHeadPartsResolveConcreteNamesAndNeverTheTemplate()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", "Model(\"hero.msh\"); DestroyedHeadParts(\"part_XX.msh\", 2);");
        var first = catalog.AddCompiled("part_00", Rp6lResourceTypes.Mesh);
        var second = catalog.AddCompiled("part_01", Rp6lResourceTypes.Mesh);
        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        Assert.Contains(result.References, finding => finding.RequestedName == "part_00.msh" && finding.Selected?.Id == first.Id);
        Assert.Contains(result.References, finding => finding.RequestedName == "part_01.msh" && finding.Selected?.Id == second.Id);
        Assert.DoesNotContain(result.References, finding => finding.RequestedName == "part_XX.msh");
    }

    [Theory]
    [InlineData("part.msh", "2")]
    [InlineData("part_XX.msh", "1.5")]
    [InlineData("part_XX.msh", "-1")]
    public async Task InvalidIndexedDeclarationsRetainSourceFindingWithoutLiteralLookup(string template, string count)
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", $"Model(\"hero.msh\"); DestroyedHeadParts(\"{template}\", {count});");
        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        var finding = Assert.Single(result.References);
        Assert.Equal(Dl1CharacterDependencyStatus.Malformed, finding.Status);
        Assert.Equal(template, finding.RequestedName);
        Assert.Contains("Indexed detached mesh declaration", finding.Detail, StringComparison.Ordinal);
        Assert.Null(finding.Selected);
        Assert.Empty(finding.Candidates);
    }

    [Fact]
    public async Task GenericFlagUsesExactIncludedSymbolsAndRetainsExcludedAlternativesAsAdvisory()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        var body = catalog.AddFile("data/body/hero.bel",
            "!include(\"symbols.def\"); ForceGenericRelics(); BodyElement(_HEAD,1,0,0,0,\"root\"); " +
            "AddRelics(\"Head\",PHYSICS_SINGLE,\"part.phx\",\"\",[0,0,0],[0,0,0]); " +
            "BodyElement(_SPINE,1,0,0,0,\"root\"); AddRelics(\"Spine\",PHYSICS_SINGLE,\"part.phx\",\"\",[0,0,0],[0,0,0]);",
            providerId: "generic-provider");
        catalog.AddFile("data/body/symbols.def", "$_HEAD(i,0); $_SPINE(i,7);", providerId: "generic-provider");
        catalog.AddFile("data/body/part.phx", "PhysicsParams(){ QuickStepNumIterations(12); }", providerId: "generic-provider");
        var part = catalog.AddCompiled("head", Rp6lResourceTypes.Mesh);
        // The exact native standard-human rule asks for the root mesh stem BEL.
        var wrapper = catalog.AddFile("actors/character.scr", "Model(\"hero.msh\"); !include(\"data/body/hero.bel\");");
        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        var generic = Assert.Single(result.References, finding =>
            finding.Basis == Dl1CharacterDependencyBasis.GenericRelicPreload && finding.Required);
        Assert.Equal("head.msh", generic.RequestedName);
        Assert.Equal(part.Id, generic.Selected!.Id);
        Assert.Contains(result.References, finding => finding.Basis == Dl1CharacterDependencyBasis.GenericRelicPreload &&
            finding.RequestedName == "Spine" && !finding.Required);
        Assert.All(result.References.Where(finding => finding.Basis == Dl1CharacterDependencyBasis.ConditionalRelicMesh),
            finding => Assert.False(finding.Required));
        var proof = Assert.Single(result.ScanProvenance!.GenericRelicBranches);
        Assert.Equal(body.Id.LogicalId.StableKey, proof.BodyResourceId);
        Assert.Equal(7, proof.Symbols["_SPINE"]);
        Assert.Single(proof.SymbolSources);
        Assert.Contains(result.Roots, finding => finding.Selected?.Id == wrapper.Id);
    }

    [Fact]
    public async Task MissingGenericSymbolIncludeKeepsConditionalFindingsRequired()
    {
        var catalog = new FakeCatalog();
        var root = catalog.AddMesh();
        catalog.AddFile("actors/character.scr", "Model(\"hero.msh\"); !include(\"body.bel\");");
        catalog.AddFile("actors/body.bel", "!include(\"absent.def\"); ForceGenericRelics(); BodyElement(_HEAD,1,0,0,0,\"root\"); " +
            "AddRelics(\"Head\",PHYSICS_SINGLE,\"part.phx\",\"\",[0,0,0],[0,0,0]);");
        var result = await Dl1CharacterDependencyDiscovery.DiscoverAsync(Mesh(), root, catalog);
        Assert.Empty(result.ScanProvenance!.GenericRelicBranches);
        Assert.Contains(result.References, finding => finding.Basis == Dl1CharacterDependencyBasis.GenericRelicPreload &&
            finding.Status == Dl1CharacterDependencyStatus.Malformed && finding.Required);
        Assert.Contains(result.References, finding => finding.Basis == Dl1CharacterDependencyBasis.ConditionalRelicMesh && finding.Required);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Dl1MeshData Mesh() => new("hero", Dl1MeshContainerLayout.FiveItemSplitGpu,
        new CompactMeshDocument(2, 1, 0, [], []), null, [], [], [], [], [], []);

    private sealed class FakeCatalog : IRetailAssetCatalog
    {
        private readonly List<RetailAssetRecord> _assets = [];
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        private int _nextSource;

        public IReadOnlyList<RetailAssetRecord> Assets => _assets;
        public IReadOnlyList<RetailAssetConflict> Conflicts => [];

        public RetailAssetRecord AddMesh()
        {
            RetailAssetLogicalId logical = RetailAssetLogicalId.Rpack(272, "hero");
            return Add(logical, "hero", [1, 2, 3], 1, null);
        }

        public RetailAssetRecord AddFile(string name, string source, int priority = 1, string? expectedHash = null,
            string? providerId = null) =>
            Add(RetailAssetLogicalId.VirtualFile(name), name, Encoding.UTF8.GetBytes(source), priority,
                expectedHash ?? Sha(source), providerId);

        public RetailAssetRecord AddPackedEffect(string name, string source)
        {
            var asset = AddFile(name, source);
            var packed = asset with { Source = asset.Source with { Kind = RetailAssetSourceKind.RpackEmbeddedEffect } };
            _assets[_assets.IndexOf(asset)] = packed;
            return packed;
        }

        public RetailAssetRecord AddCompiled(string name, short resourceType, int priority = 1,
            string? providerId = null) =>
            Add(RetailAssetLogicalId.Rpack(resourceType, name), name, [1, 2, 3], priority, null, providerId);

        private RetailAssetRecord Add(RetailAssetLogicalId logical, string name, byte[] bytes, int priority,
            string? hash, string? providerId = null)
        {
            int index = ++_nextSource;
            string provider = providerId ?? "synthetic-" + index;
            var id = RetailAssetId.Create(logical, "synthetic", provider, index, priority,
                Sha("source-" + index), hash);
            var record = new RetailAssetRecord(id, name,
                new(provider, RetailAssetSourceKind.ZipPak, priority, "synthetic.pak", name,
                    index, bytes.Length, bytes.Length, DateTime.UnixEpoch));
            _assets.Add(record);
            _bytes.Add(id.StableKey, bytes);
            return record;
        }

        public RetailAssetRecord? Resolve(RetailAssetLogicalId id) => GetCandidates(id)
            .OrderByDescending(static asset => asset.Id.Precedence).FirstOrDefault();

        public IReadOnlyList<RetailAssetRecord> GetCandidates(RetailAssetLogicalId id) => _assets
            .Where(asset => asset.Id.LogicalId == id).ToArray();

        public IReadOnlyList<RetailAssetRecord> Search(string text, int maximumResults = 500) => _assets
            .Where(asset => asset.Id.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(maximumResults).ToArray();

        public ValueTask<Stream> OpenReadAsync(RetailAssetLogicalId id, CancellationToken cancellationToken = default) =>
            OpenReadAsync(Resolve(id) ?? throw new FileNotFoundException(), cancellationToken);

        public ValueTask<Stream> OpenReadAsync(RetailAssetId id, CancellationToken cancellationToken = default) =>
            OpenReadAsync(_assets.Single(asset => asset.Id == id), cancellationToken);

        public ValueTask<Stream> OpenReadAsync(RetailAssetRecord asset, CancellationToken cancellationToken = default) =>
            new(new MemoryStream(_bytes[asset.Id.StableKey], writable: false));
    }
}
