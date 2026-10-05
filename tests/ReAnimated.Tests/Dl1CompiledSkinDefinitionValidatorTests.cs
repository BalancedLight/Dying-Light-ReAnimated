using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledSkinDefinitionValidatorTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NamesAndHighEntityFlagsSurviveCompilerIndexChanges()
    {
        Dl1SkinGenerationDefinition expected = Expected();
        CompactMeshDocument hierarchy = Hierarchy();
        CompiledMeshGeometryDocument compiled = Compiled();

        Dl1CompiledSkinValidationEvidence evidence = Dl1CompiledSkinDefinitionValidator.Validate(
            [expected], hierarchy, compiled);

        Assert.Equal(1, evidence.VerifiedSkinCount);
        Assert.Equal(64, evidence.ContractFingerprint.Length);
        Dl1CompiledSkinRowEvidence row = Assert.Single(evidence.Skins);
        Assert.Equal("default", row.SkinName);
        Assert.Equal(new Dl1CompiledSkinMaterialEvidence("base.mat", "variant.mat"),
            Assert.Single(row.MaterialReplacements));
        Assert.Contains(row.EntityOverrides, static entity => entity.EntityName == "arm" && entity.Hidden);
        Assert.Contains(row.EntityOverrides, static entity => entity.EntityName == "cap" && entity.Flag4000);

        CompiledMaterialDatabase reindexed = new(2, 4,
            [new(0, "base.mat", 0), new(1, "other.mat", 0),
             new(2, "variant.mat", 0), new(3, "unused.mat", 0)]);
        CompiledMeshGeometryDocument other = compiled with
        {
            MaterialDatabase = reindexed,
            SkinDefinitions = [compiled.SkinDefinitions[0] with
            {
                MaterialOverrides = [new(0, 2)],
            }],
        };
        Assert.Equal(evidence.ContractFingerprint,
            Dl1CompiledSkinDefinitionValidator.Validate([expected], hierarchy, other).ContractFingerprint);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void UnknownMissingDuplicateOrInconsistentRowsAreRejected()
    {
        Dl1SkinGenerationDefinition expected = Expected();
        CompactMeshDocument hierarchy = Hierarchy();
        CompiledMeshGeometryDocument compiled = Compiled();
        CompiledMeshSkinDefinition skin = Assert.Single(compiled.SkinDefinitions);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate(
            [expected], hierarchy, compiled with { SkinDefinitions = [skin with { RawFeatures = 0x8080 }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate(
            [expected], hierarchy, compiled with { SkinDefinitions = [skin with { SurfaceOverrideCount = 1 }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate(
            [expected], hierarchy, compiled with { SkinDefinitions = [skin with { EntityOverrides =
                [new(1, 0x8001), new(2, 0x0002)] }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate(
            [expected], hierarchy, compiled with { SkinDefinitions = [skin with { MaterialOverrides = [] }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate(
            [expected], hierarchy, compiled with { SkinDefinitions = [skin, skin with { Index = 1, Name = "Extra" }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate(
            [expected, expected with { Name = "DEFAULT" }], hierarchy, compiled));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SkinReadbackRejectsChangedColorTagSurfaceOrOptionalString()
    {
        byte[] colors=[0,0,0,255,0,0,0,255];byte[] tags=[0,0,0,0,0,0,0,0];
        var expected=Expected() with {SurfaceOverrideCount=1,ExpectedCompiledColors=[..colors],ExpectedTagBytes=[..tags],OptionalStringsAndGroupsVerifiedAbsent=true,
            VerifiedSurfaceReplacements=[new("Water","Flesh","",3,10,0)]};
        var skin=Compiled().SkinDefinitions[0] with {SurfaceOverrideCount=1,ColorBytes=colors,TagBytes=tags,SurfaceOverrides=[new(3,10,0)]};
        var compiled=Compiled() with {SkinDefinitions=[skin]};
        var evidence=Dl1CompiledSkinDefinitionValidator.Validate([expected],Hierarchy(),compiled);
        Assert.Single(Assert.Single(evidence.Skins).Surfaces);
        Assert.Throws<InvalidDataException>(()=>Dl1CompiledSkinDefinitionValidator.Validate([expected],Hierarchy(),compiled with {SkinDefinitions=[skin with {ColorBytes=[1,0,0,255,0,0,0,255]}]}));
        Assert.Throws<InvalidDataException>(()=>Dl1CompiledSkinDefinitionValidator.Validate([expected],Hierarchy(),compiled with {SkinDefinitions=[skin with {TagBytes=[1,0,0,0,0,0,0,0]}]}));
        Assert.Throws<InvalidDataException>(()=>Dl1CompiledSkinDefinitionValidator.Validate([expected],Hierarchy(),compiled with {SkinDefinitions=[skin with {SurfaceOverrides=[new(3,10,1)]}]}));
        Assert.Throws<InvalidDataException>(()=>Dl1CompiledSkinDefinitionValidator.Validate([expected],Hierarchy(),compiled with {SkinDefinitions=[skin with {MorphsPreset="unexpected"}]}));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DistinctCompiledMaterialInstancesRequireEveryIndexedReplacement()
    {
        var expected = Expected() with { MaterialReplacements = [new("base.mat", "base.mat")] };
        var compiled = Compiled() with
        {
            MaterialDatabase = new(3, 3, [new(0, "base.mat", 5), new(1, "other.mat", 1), new(2, "base.mat", 513)]),
            SkinDefinitions = [Compiled().SkinDefinitions[0] with { MaterialOverrides = [new(0, 0), new(2, 2)] }],
        };
        var evidence = Dl1CompiledSkinDefinitionValidator.Validate([expected], Hierarchy(), compiled);
        Assert.Single(Assert.Single(evidence.Skins).MaterialReplacements);
        Assert.Equal(2, Assert.Single(evidence.Skins).VerifiedMaterialSlotCount);
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate([expected], Hierarchy(), compiled with
            { SkinDefinitions = [compiled.SkinDefinitions[0] with { MaterialOverrides = [new(0, 0)] }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate([expected], Hierarchy(), compiled with
            { SkinDefinitions = [compiled.SkinDefinitions[0] with { MaterialOverrides = [new(0, 0), new(2, 0)] }] }));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinDefinitionValidator.Validate([expected], Hierarchy(), compiled with
            { MaterialDatabase = new(3, 3, [new(0, "base.mat", 5), new(1, "other.mat", 1), new(2, "base.mat", 5)]) }));
    }

    private static Dl1SkinGenerationDefinition Expected() => new(
        "Default", (ushort)Dl1SkinSourceFeatures.MaterialReplacements,
        [new("base.mat", "variant.mat")],
        [new("arm", true, 0x8005), new("cap", false, 0x4006)], 0, 0);

    private static CompactMeshDocument Hierarchy() => new(3, 1, 0,
        [Entity(0, "root"), Entity(1, "arm"), Entity(2, "cap")], []);

    private static CompactMeshEntity Entity(int index, string name) => new(
        index, name, 0, new(0, 0, 0, 1, 1, 1),
        (short)(index == 0 ? -1 : 0), CompactMeshEntityType.Mesh,
        0, 0, CompactMatrix3x4.Identity, CompactMatrix3x4.Identity, 0, 0);

    private static CompiledMeshGeometryDocument Compiled() => new(
        [], [], ["default"],
        new(2, 4,
            [new(0, "base.mat", 0), new(1, "other.mat", 0),
             new(2, "unused.mat", 0), new(3, "variant.mat", 0)]),
        [], [], [])
    {
        SkinDefinitions = [new(0, "DEFAULT", (ushort)Dl1SkinSourceFeatures.MaterialReplacements,
            [new(0, 3)], [new(1, 0x8001), new(2, 0x4002)], 0, 0)],
    };
}
