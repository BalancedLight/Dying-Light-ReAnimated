using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledChrIdentityValidatorTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AcceptsCaseNormalizedObjectAndVariantJoinsAndPreservesRawEvidence()
    {
        Dl1ChrV4Document source = SourceChr("root", "mesh");
        CompactMeshDocument hierarchy = Hierarchy("ROOT", "MESH");
        CompiledMeshGeometryDocument geometry = Geometry(
            [Skin("DEFAULT", rawFeatures: 0x5A3C, entityOverrides: [new(1, 0xC001)], surfaceCount: 2, randomizedChildCount: 3)],
            ["DEFAULT"]);

        Dl1CompiledChrIdentityReadBackEvidence evidence =
            Dl1CompiledChrIdentityValidator.Validate(source, hierarchy, geometry);

        Assert.Equal("root", evidence.Objects[0].SourceObjectName);
        Assert.Equal("ROOT", evidence.Objects[0].CompiledObjectName);
        Assert.Equal(0, evidence.Objects[0].SourceObjectIndex);
        Assert.Equal(0, evidence.Objects[0].CompiledEntityIndex);
        Assert.Equal("mesh", evidence.Objects[1].SourceObjectName);
        Assert.Equal(1, evidence.Objects[1].SourceObjectIndex);
        Assert.Equal(1, evidence.Objects[1].CompiledEntityIndex);
        Assert.True(evidence.ObjectOrderMatches);
        Dl1CompiledChrVariantIdentityReadBack variant = Assert.Single(evidence.Variants);
        Assert.Equal("default", variant.SourceVariantName);
        Assert.Equal("DEFAULT", variant.CompiledVariantName);
        Assert.Equal((ushort)0x5A3C, variant.RawFeatures);
        Assert.Equal(2, variant.SurfaceOverrideCount);
        Assert.Equal(3, variant.RandomizedChildCount);
        Dl1CompiledChrEntityOverrideIdentityReadBack entityOverride = Assert.Single(variant.EntityOverrides);
        Assert.Equal(1, entityOverride.CompiledEntityIndex);
        Assert.Equal("MESH", entityOverride.CompiledObjectName);
        Assert.Equal((ushort)0xC001, entityOverride.RawValue);
        Assert.Matches("^[0-9a-f]{64}$", evidence.ContractFingerprint);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsCompiledObjectOrderThatDiffersFromTheGeneratedChrOrder()
    {
        Dl1ChrV4Document source = SourceChr("root", "mesh");
        CompactMeshDocument hierarchy = Hierarchy("MESH", "ROOT");
        CompiledMeshGeometryDocument geometry = Geometry([Skin("default")], ["default"]);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, hierarchy, geometry));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsCompiledEntityIndexesThatAreNotContiguousAndUnique()
    {
        Dl1ChrV4Document source = SourceChr("root", "mesh");
        CompactMeshDocument validHierarchy = Hierarchy("root", "mesh");
        CompactMeshDocument invalidHierarchy = validHierarchy with
        {
            Entities =
            [
                validHierarchy.Entities[0],
                validHierarchy.Entities[1] with { Index = 0 },
            ],
        };
        CompiledMeshGeometryDocument geometry = Geometry([Skin("default")], ["default"]);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, invalidHierarchy, geometry));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsMissingAndExtraCompiledObjects()
    {
        Dl1ChrV4Document source = SourceChr("root", "mesh");
        CompiledMeshGeometryDocument geometry = Geometry([Skin("default")], ["default"]);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, Hierarchy("root"), geometry));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, Hierarchy("root", "mesh", "extra"), geometry));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsMissingDuplicateAndUndecodedVariantInventories()
    {
        Dl1ChrV4Document source = SourceChr("root");
        CompactMeshDocument hierarchy = Hierarchy("root");

        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, hierarchy, Geometry([], [])));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, hierarchy, Geometry([Skin("other")], ["other"])));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, hierarchy, Geometry([Skin("default"), Skin("DEFAULT", index: 1)], ["default", "DEFAULT"])));
        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, hierarchy, Geometry([Skin("default")], ["other"])));
    }

    [Theory]
    [InlineData(9, 0xC009)]
    [InlineData(0, 0xC001)]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsEntityOverridesThatCannotJoinToTheCompiledObjectTable(int entityIndex, int rawValue)
    {
        Dl1ChrV4Document source = SourceChr("root");
        CompiledMeshGeometryDocument geometry = Geometry(
            [Skin("default", entityOverrides: [new(entityIndex, checked((ushort)rawValue))])],
            ["default"]);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledChrIdentityValidator.Validate(
            source, Hierarchy("root"), geometry));
    }

    private static Dl1ChrV4Document SourceChr(params string[] objectNames)
    {
        ImmutableArray<string> names = [.. objectNames];
        ImmutableArray<TransformMatrix> transforms = names
            .Select(static _ => TransformMatrix.Identity)
            .ToImmutableArray();
        return new Dl1ChrV4Document(
            names,
            [new Dl1ChrV4Variant("default", new Vector3D(1, 1, 1), transforms)]);
    }

    private static CompactMeshDocument Hierarchy(params string[] names)
    {
        CompactMeshEntity[] entities = names
            .Select((name, index) => new CompactMeshEntity(
                index, name, 0, new CompactBounds(0, 0, 0, 1, 1, 1), -1,
                CompactMeshEntityType.Mesh, 0, 1, CompactMatrix3x4.Identity,
                CompactMatrix3x4.Identity, 0, 0))
            .ToArray();
        return new CompactMeshDocument(entities.Length, entities.Length, 0, entities, []);
    }

    private static CompiledMeshGeometryDocument Geometry(
        IReadOnlyList<CompiledMeshSkinDefinition> skins,
        IReadOnlyList<string> variantNames) =>
        new(
            Array.Empty<CompiledVertexLayout>(),
            Array.Empty<CompiledMeshSurface>(),
            variantNames,
            CompiledMaterialDatabase.Empty,
            Array.Empty<CompiledMorphChannel>(),
            Array.Empty<CompiledNodeMorphBinding>(),
            Array.Empty<CompactMeshDiagnostic>())
        {
            SkinDefinitions = skins,
        };

    private static CompiledMeshSkinDefinition Skin(
        string name,
        ushort rawFeatures = 0,
        IReadOnlyList<CompiledMeshSkinEntityOverride>? entityOverrides = null,
        int surfaceCount = 0,
        int randomizedChildCount = 0,
        int index = 0) =>
        new(
            index, name, rawFeatures, Array.Empty<CompiledMeshSkinMaterialOverride>(),
            entityOverrides ?? Array.Empty<CompiledMeshSkinEntityOverride>(),
            surfaceCount, randomizedChildCount);
}
