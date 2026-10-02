using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledSkinningReadBackValidatorTests
{
    private const int CompiledEntityCount = 4;

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AcceptsVertexAndPaletteReorderingWhenEntityInfluencesAreEquivalent()
    {
        ImmutableArray<Dl1PreparedSkinningSurfaceExpectation> expected = [ExpectedSurface()];
        CompiledMeshGeometryDocument compiled = CompiledSurface(
            palette: [2, 1],
            vertices:
            [
                Vertex(0, 1, 0.5f, 0.5f, 1, 0), // source vertex 2
                Vertex(0, 0, 0.75f, 0.25f, 1, 0), // source vertex 0
                Vertex(1, 0, 1.0f, 0.0f, 0, 0), // source vertex 1
            ],
            indices: [0, 1, 2]);

        Dl1CompiledSkinningReadBackEvidence evidence =
            Dl1CompiledSkinningReadBackValidator.Validate(
                expected,
                compiled,
                CompiledEntityCount);

        Assert.Equal(1, evidence.VerifiedSurfaceCount);
        Assert.Equal(1, evidence.VerifiedSubsetCount);
        Assert.Equal(3, evidence.VerifiedVertexCount);
        Assert.Equal(5, evidence.VerifiedInfluenceCount);
        Assert.Matches("^[0-9a-f]{64}$", evidence.ContractFingerprint);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AcceptsOnlyTheCorpusValidatedRigidIndexedPaletteShape()
    {
        Dl1PreparedSkinningSurfaceExpectation expected = new(
            "generic_rigid_surface",
            LodIndex: 0,
            IsSkinned: true,
            VertexCount: 3,
            UsedVertexIndexes: [0, 1, 2],
            Subsets: [new Dl1PreparedSkinSubsetExpectation([1], 3)],
            Vertices:
            [
                new(new Vector3(0, 0, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
                new(new Vector3(1, 0, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
                new(new Vector3(0, 1, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
            ]);
        CompiledMeshGeometryDocument baseGeometry = CompiledSurface(
            palette: [1],
            vertices:
            [
                Vertex(0, 0, 0.0f, 0.0f, 0, 0),
                Vertex(1, 0, 0.0f, 0.0f, 0, 0),
                Vertex(0, 1, 0.0f, 0.0f, 0, 0),
            ],
            indices: [0, 1, 2]);
        CompiledMeshSurface baseSurface = Assert.Single(baseGeometry.Surfaces);
        CompiledMeshGeometryDocument compiled = baseGeometry with
        {
            Surfaces = [baseSurface with { Name = "generic_rigid_surface" }],
        };
        Assert.Equal(
            Dl1SkinBindingMode.RigidIndexedPalette,
            ClassifySkinBinding(Assert.Single(compiled.Surfaces), [1]));

        Dl1CompiledSkinningReadBackEvidence evidence =
            Dl1CompiledSkinningReadBackValidator.Validate(
                [expected],
                compiled,
                CompiledEntityCount);

        Assert.Equal(3, evidence.VerifiedInfluenceCount);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsMalformedZeroWeightRigidIndexedPaletteShape()
    {
        CompiledMeshGeometryDocument baseGeometry = CompiledSurface(
            palette: [1],
            vertices:
            [
                Vertex(0, 0, 0.0f, 0.0f, 0, 1),
                Vertex(1, 0, 0.0f, 0.0f, 0, 0),
                Vertex(0, 1, 0.0f, 0.0f, 0, 0),
            ],
            indices: [0, 1, 2]);
        CompiledMeshSurface baseSurface = Assert.Single(baseGeometry.Surfaces);
        CompiledMeshGeometryDocument compiled = baseGeometry with
        {
            Surfaces = [baseSurface with { Name = "generic_rigid_surface" }],
        };
        Assert.Equal(
            Dl1SkinBindingMode.ExplicitVertexWeights,
            ClassifySkinBinding(Assert.Single(compiled.Surfaces), [1]));
        Dl1PreparedSkinningSurfaceExpectation expected = new(
            "generic_rigid_surface",
            LodIndex: 0,
            IsSkinned: true,
            VertexCount: 3,
            UsedVertexIndexes: [0, 1, 2],
            Subsets: [new Dl1PreparedSkinSubsetExpectation([1], 3)],
            Vertices:
            [
                new(new Vector3(0, 0, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
                new(new Vector3(1, 0, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
                new(new Vector3(0, 1, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
            ]);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [expected],
            compiled,
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsRigidIndexedPaletteIndexOutsidePalette()
    {
        CompiledMeshGeometryDocument baseGeometry = CompiledSurface(
            palette: [1],
            vertices:
            [
                Vertex(0, 0, 0.0f, 0.0f, 1, 0),
                Vertex(1, 0, 0.0f, 0.0f, 0, 0),
                Vertex(0, 1, 0.0f, 0.0f, 0, 0),
            ],
            indices: [0, 1, 2]);
        CompiledMeshSurface baseSurface = Assert.Single(baseGeometry.Surfaces);
        CompiledMeshGeometryDocument compiled = baseGeometry with
        {
            Surfaces = [baseSurface with { Name = "generic_rigid_surface" }],
        };
        Assert.Equal(
            Dl1SkinBindingMode.ExplicitVertexWeights,
            ClassifySkinBinding(Assert.Single(compiled.Surfaces), [1]));
        Dl1PreparedSkinningSurfaceExpectation expected = new(
            "generic_rigid_surface",
            LodIndex: 0,
            IsSkinned: true,
            VertexCount: 3,
            UsedVertexIndexes: [0, 1, 2],
            Subsets: [new Dl1PreparedSkinSubsetExpectation([1], 3)],
            Vertices:
            [
                new(new Vector3(0, 0, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
                new(new Vector3(1, 0, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
                new(new Vector3(0, 1, 0), [new Dl1PreparedSkinInfluenceExpectation(1, 1.0)]),
            ]);

        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [expected],
            compiled,
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsRigidIndexedPaletteForExpectedMultipleBoneInfluence()
    {
        CompiledMeshGeometryDocument baseGeometry = CompiledSurface(
            palette: [1, 2],
            vertices:
            [
                Vertex(0, 0, 0.0f, 0.0f, 0, 0),
                Vertex(1, 0, 0.0f, 0.0f, 0, 0),
                Vertex(0, 1, 0.0f, 0.0f, 0, 0),
            ],
            indices: [0, 1, 2]);
        CompiledMeshSurface baseSurface = Assert.Single(baseGeometry.Surfaces);
        CompiledMeshGeometryDocument compiled = baseGeometry with
        {
            Surfaces = [baseSurface with { Name = "generic_surface" }],
        };
        Assert.Equal(
            Dl1SkinBindingMode.RigidIndexedPalette,
            ClassifySkinBinding(Assert.Single(compiled.Surfaces), [1, 2]));

        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [ExpectedSurface()],
            compiled,
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void ResolvesLocalBlendIndexesThroughEachSubsetPalette()
    {
        Dl1PreparedSkinningSurfaceExpectation source = ExpectedSurface() with
        {
            VertexCount = 6,
            UsedVertexIndexes = [0, 1, 2, 3, 4, 5],
            Subsets =
            [
                new Dl1PreparedSkinSubsetExpectation([1, 2], 3),
                new Dl1PreparedSkinSubsetExpectation([1, 2], 3),
            ],
        };
        ImmutableArray<Dl1PreparedSkinVertexExpectation> sourceVertices = source.Vertices;
        source = source with { Vertices = sourceVertices.AddRange(sourceVertices) };
        CompiledMeshGeometryDocument baseGeometry = CompiledSurface(
            palette: [2, 1],
            vertices:
            [
                Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                Vertex(1, 0, 1.0f, 0.0f, 0, 0),
                Vertex(0, 1, 0.5f, 0.5f, 1, 0),
                Vertex(0, 0, 0.75f, 0.25f, 0, 1),
                Vertex(1, 0, 1.0f, 0.0f, 1, 0),
                Vertex(0, 1, 0.5f, 0.5f, 0, 1),
            ],
            indices: [0, 1, 2]);
        CompiledMeshSurface baseSurface = Assert.Single(baseGeometry.Surfaces);
        CompiledMeshGeometryDocument compiled = baseGeometry with
        {
            Surfaces =
            [
                baseSurface with
                {
                    Indices = [0, 1, 2, 3, 4, 5],
                    Submeshes =
                    [
                        new CompiledMeshSubmesh(0, 0, 3, (ushort)1, [2, 1]),
                        new CompiledMeshSubmesh(1, 3, 3, (ushort)1, [1, 2]),
                    ],
                },
            ],
        };

        Dl1CompiledSkinningReadBackEvidence evidence =
            Dl1CompiledSkinningReadBackValidator.Validate(
                [source],
                compiled,
                CompiledEntityCount);

        Assert.Equal(2, evidence.VerifiedSubsetCount);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsPaletteThatSubstitutesAnotherCompiledEntity()
    {
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [ExpectedSurface()],
            CompiledSurface(
                palette: [2, 3],
                vertices:
                [
                    Vertex(0, 1, 0.5f, 0.5f, 1, 0),
                    Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                    Vertex(1, 0, 1.0f, 0.0f, 0, 0),
                ],
                indices: [0, 1, 2]),
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsActiveBlendIndexOutsideSubsetPalette()
    {
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [ExpectedSurface()],
            CompiledSurface(
                palette: [2, 1],
                vertices:
                [
                    Vertex(0, 1, 0.5f, 0.5f, 2, 0),
                    Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                    Vertex(1, 0, 1.0f, 0.0f, 0, 0),
                ],
                indices: [0, 1, 2]),
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsChangedEntityIndexedWeight()
    {
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [ExpectedSurface()],
            CompiledSurface(
                palette: [2, 1],
                vertices:
                [
                    Vertex(0, 1, 0.1f, 0.9f, 1, 0),
                    Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                    Vertex(1, 0, 1.0f, 0.0f, 0, 0),
                ],
                indices: [0, 1, 2]),
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsMissingExpectedLodOrSubset()
    {
        var twoLods = ImmutableArray.Create(
            ExpectedSurface(),
            ExpectedSurface() with { LodIndex = 1 });
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            twoLods,
            CompiledSurface(
                palette: [2, 1],
                vertices:
                [
                    Vertex(0, 1, 0.5f, 0.5f, 1, 0),
                    Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                    Vertex(1, 0, 1.0f, 0.0f, 0, 0),
                ],
                indices: [0, 1, 2]),
            CompiledEntityCount));

        CompiledMeshSurface withoutSubset = Assert.Single(CompiledSurface(
            palette: [2, 1],
            vertices:
            [
                Vertex(0, 1, 0.5f, 0.5f, 1, 0),
                Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                Vertex(1, 0, 1.0f, 0.0f, 0, 0),
            ],
            indices: [0, 1, 2]).Surfaces);
        CompiledMeshGeometryDocument missingSubset = CompiledSurface(
            palette: [2, 1],
            vertices: withoutSubset.Vertices,
            indices: [0, 1, 2]) with
        {
            Surfaces = [withoutSubset with { Submeshes = [] }],
        };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [ExpectedSurface()],
            missingSubset,
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsUnexpectedCompiledSurface()
    {
        CompiledMeshSurface surface = Assert.Single(CompiledSurface(
            palette: [2, 1],
            vertices:
            [
                Vertex(0, 1, 0.5f, 0.5f, 1, 0),
                Vertex(0, 0, 0.75f, 0.25f, 1, 0),
                Vertex(1, 0, 1.0f, 0.0f, 0, 0),
            ],
            indices: [0, 1, 2]).Surfaces);
        CompiledMeshGeometryDocument withExtraSurface = CompiledSurface(
            palette: [2, 1],
            vertices: surface.Vertices,
            indices: [0, 1, 2]) with
        {
            Surfaces =
            [
                surface,
                surface with { EntityIndex = 3, Name = "generic_unexpected_surface" },
            ],
        };

        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [ExpectedSurface()],
            withExtraSurface,
            CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task SourceWriterExportsPreparedPhysicalPaletteAndQuantizedInfluences()
    {
        string output = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult model = RigConformanceWizardTests.CreateModel();
            Dl1PreparedAuthoredRig authored = Dl1CustomModelRigPreparer.Prepare(model);
            Dl1SourceModelBuildResult build = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = model,
                    OutputDirectory = output,
                    ResourceName = "generic_skinning_model",
                });

            Dl1PreparedSkinningSurfaceExpectation expected =
                Assert.Single(build.PreparedSkinningExpectations);
            Dl1PreparedSkinSurface preparedSurface = Assert.Single(authored.Surfaces);
            Assert.Equal(preparedSurface.PhysicalPalette.ToArray(), expected.Subsets[0].PaletteEntityIndexes.ToArray());
            Assert.Equal(model.Surfaces[0].Vertices.Length, expected.VertexCount);
            Assert.Equal(
                model.Surfaces[0].Indices.Select(static index => checked((int)index)).Distinct().Order().ToArray(),
                expected.UsedVertexIndexes.ToArray());

            FbxModelVertex sourceVertex = model.Surfaces[0].Vertices[0];
            short[] quantized = Dl1SkinWeightQuantization.Encode(sourceVertex.BoneWeights.AsSpan());
            var expectedByEntity = sourceVertex.BoneIndices
                .Select((local, index) => (Entity: preparedSurface.PhysicalPalette[local], Weight: quantized[index] / 32767.0))
                .Where(static influence => influence.Weight > 0)
                .GroupBy(static influence => influence.Entity)
                .OrderBy(static group => group.Key)
                .Select(static group => new Dl1PreparedSkinInfluenceExpectation(
                    group.Key,
                    group.Sum(static influence => influence.Weight)))
                .ToArray();
            Assert.Equal(expectedByEntity, expected.Vertices[0].Influences.ToArray());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsChangedOrMissingPreparedSubsetMaterial(bool missing)
    {
        Dl1PreparedSkinningSurfaceExpectation expected = ExpectedSurface();
        expected = expected with { Subsets = expected.Subsets.Select(subset => subset with { DeclaredMaterialSlotIndex = 7 }).ToImmutableArray() };
        CompiledMeshGeometryDocument compiled = CompiledSurface(palette: [2, 1],
            vertices: [Vertex(0, 0, .75f, .25f, 1, 0), Vertex(1, 0, 1f, 0f, 0, 0), Vertex(0, 1, .5f, .5f, 1, 0)], indices: [0, 1, 2]);
        if (missing)
        {
            CompiledMeshSurface surface = Assert.Single(compiled.Surfaces);
            compiled = compiled with { Surfaces = [surface with { Submeshes = [surface.Submeshes[0] with { DeclaredMaterialSlotIndex = null }] }] };
        }
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [expected], compiled, CompiledEntityCount));
        Assert.Contains("subset material", error.Message, StringComparison.Ordinal);
    }

    private static Dl1PreparedSkinningSurfaceExpectation ExpectedSurface() => new(
        "generic_surface",
        LodIndex: 0,
        IsSkinned: true,
        VertexCount: 3,
        UsedVertexIndexes: [0, 1, 2],
        Subsets: [new Dl1PreparedSkinSubsetExpectation([1, 2], 3)],
        Vertices:
        [
            ExpectedVertex(0, 0, (1, 0.75), (2, 0.25)),
            ExpectedVertex(1, 0, (2, 1.0)),
            ExpectedVertex(0, 1, (1, 0.5), (2, 0.5)),
        ]);

    private static Dl1PreparedSkinVertexExpectation ExpectedVertex(
        float x,
        float y,
        params (int Entity, double Weight)[] raw)
    {
        short[] quantized = Dl1SkinWeightQuantization.Encode(raw.Select(static influence => influence.Weight).ToArray());
        ImmutableArray<Dl1PreparedSkinInfluenceExpectation> influences = raw
            .Select((influence, index) => new Dl1PreparedSkinInfluenceExpectation(
                influence.Entity,
                quantized[index] / 32767.0))
            .Where(static influence => influence.Weight > 0)
            .ToImmutableArray();
        return new(new Vector3(x, y, 0), influences);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void AcceptsMaterialSlotReassignmentOnlyWhenReferencesMatch()
    {
        var expected = ExpectedSurface() with { Subsets = [new([1, 2], 3) { DeclaredMaterialSlotIndex = 7, DeclaredMaterialReference = "generic_material.mat" }] };
        var compiled = MaterialGeometry("generic_material.mat", 517);
        var evidence = Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, CompiledEntityCount);
        var material = Assert.Single(evidence.MaterialSlots);
        Assert.True(material.MatchedByReference);
        Assert.Equal((ushort)7, material.SourceSlotIndex);
        Assert.Equal((ushort)1, material.CompiledSlotIndex);
        Assert.Equal((uint)517, material.RawCompiledLoadValue);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsChangedMaterialReferenceEvenWhenSlotNumbersMatch()
    {
        var expected = ExpectedSurface() with { Subsets = [new([1, 2], 3) { DeclaredMaterialSlotIndex = 1, DeclaredMaterialReference = "generic_expected.mat" }] };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate(
            [expected], MaterialGeometry("generic_replacement.mat", 513), CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RetainsNumericMaterialGateWhenSlotNamesAreUnavailable()
    {
        var expected = ExpectedSurface() with { Subsets = [new([1, 2], 3) { DeclaredMaterialSlotIndex = 7, DeclaredMaterialReference = "generic_material.mat" }] };
        var unknown = MaterialGeometry("generic_material.mat", 517) with { MaterialDatabase = CompiledMaterialDatabase.Empty };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate([expected], unknown, CompiledEntityCount));
        expected = expected with { Subsets = [expected.Subsets[0] with { DeclaredMaterialSlotIndex = 1 }] };
        Assert.False(Assert.Single(Dl1CompiledSkinningReadBackValidator.Validate([expected], unknown, CompiledEntityCount).MaterialSlots).MatchedByReference);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsMissingMaterialSlotWithKnownSourceReference()
    {
        var expected = ExpectedSurface() with { Subsets = [new([1, 2], 3) { DeclaredMaterialSlotIndex = 1, DeclaredMaterialReference = "generic_material.mat" }] };
        var compiled = MaterialGeometry("generic_material.mat", 517);
        var surface = Assert.Single(compiled.Surfaces);
        compiled = compiled with { Surfaces = [surface with { Submeshes = [surface.Submeshes[0] with { DeclaredMaterialSlotIndex = null }] }] };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, CompiledEntityCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("ValidationTier", "Focused")]
    public void AcceptsOneResidualLaneFromThreeOrFourInfluenceByteQuantization(bool fourLanes)
    {
        var (expected, compiled) = ResidualGeometry(fourLanes);
        Assert.Equal(3, Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, CompiledEntityCount).VerifiedVertexCount);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsWeightChangesBeyondTheBoundedResidual()
    {
        var (expected, compiled) = ResidualGeometry(true);
        var surface = Assert.Single(compiled.Surfaces);
        compiled = compiled with { Surfaces = [surface with { Vertices = surface.Vertices.Select(v =>
            v with { BlendWeights = new Vector4(100f / 255, 51f / 255, 51f / 255, 53f / 255) }).ToArray() }] };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, CompiledEntityCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsAnUnexpectedEntityDespiteValidResidualWeightsAndSum()
    {
        var (expected, compiled) = ResidualGeometry(false);
        expected = expected with { Subsets = [new([0, 1, 2, 3], 3)] };
        var surface = Assert.Single(compiled.Surfaces);
        compiled = compiled with { Surfaces = [surface with
        {
            Submeshes = [surface.Submeshes[0] with { BonePaletteEntityIndexes = new short[] { 0, 1, 2, 3 } }],
            Vertices = surface.Vertices.Select(v => v with { LocalBlendIndices = new(0, 2, 3, 0) }).ToArray(),
        }] };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, CompiledEntityCount));
    }

    private static (Dl1PreparedSkinningSurfaceExpectation, CompiledMeshGeometryDocument) ResidualGeometry(bool four)
    {
        var influences = four
            ? ImmutableArray.Create(new Dl1PreparedSkinInfluenceExpectation(0, 13434.0 / 32767),
                new(1, 6881.0 / 32767), new(2, 6881.0 / 32767), new(3, 5571.0 / 32767))
            : ImmutableArray.Create(new Dl1PreparedSkinInfluenceExpectation(1, 13434.0 / 32767),
                new(2, 5898.0 / 32767), new(3, 13435.0 / 32767));
        ImmutableArray<int> palette = four ? [0, 1, 2, 3] : [1, 2, 3];
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        var expected = new Dl1PreparedSkinningSurfaceExpectation("generic_surface", 0, true, 3, [0, 1, 2],
            [new(palette, 3)], positions.Select(p => new Dl1PreparedSkinVertexExpectation(p, influences)).ToImmutableArray());
        Vector4 weights = four ? new(104f / 255, 53f / 255, 53f / 255, 45f / 255) : new(104f / 255, 47f / 255, 104f / 255, 0);
        var compiled = CompiledSurface(palette.Select(i => (short)i).ToArray(),
            positions.Select(p => Vertex(p.X, p.Y, 1, 0, 0, 0) with
            { BlendWeights = weights, LocalBlendIndices = four ? new(0, 1, 2, 3) : new(0, 1, 2, 0) }).ToArray(), [0, 1, 2]);
        return (expected, compiled);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void AcceptsTwoExpectedSubByteLanesThatQuantizeToZero()
    {
        var (expected, compiled) = SubByteGeometry(false);
        Assert.Equal(3, Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, 5).VerifiedVertexCount);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsLossOfAnExpectedNontrivialLane()
    {
        var (expected, compiled) = SubByteGeometry(true);
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, 5));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsNewEntityWhenExpectedSubByteLanesDisappear()
    {
        var (expected, compiled) = SubByteGeometry(false);
        var surface = Assert.Single(compiled.Surfaces);
        compiled = compiled with { Surfaces = [surface with { Vertices = surface.Vertices.Select(v =>
            v with { LocalBlendIndices = new(4, 3, 0, 0) }).ToArray() }] };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledSkinningReadBackValidator.Validate([expected], compiled, 5));
    }

    private static (Dl1PreparedSkinningSurfaceExpectation, CompiledMeshGeometryDocument) SubByteGeometry(bool nontrivial)
    {
        double small = nontrivial ? 0.01 : 15.0 / 32767;
        var influences = ImmutableArray.Create(new Dl1PreparedSkinInfluenceExpectation(0, small),
            new(1, 7.0 / 32767), new(2, (1 - small - 7.0 / 32767) * 2 / 3), new(3, (1 - small - 7.0 / 32767) / 3));
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        var expected = new Dl1PreparedSkinningSurfaceExpectation("generic_surface", 0, true, 3, [0, 1, 2],
            [new([0, 1, 2, 3, 4], 3)], positions.Select(p => new Dl1PreparedSkinVertexExpectation(p, influences)).ToImmutableArray());
        var compiled = CompiledSurface(new short[] { 0, 1, 2, 3, 4 },
            positions.Select(p => Vertex(p.X, p.Y, 1, 0, 0, 0) with
            { BlendWeights = new(170f / 255, 85f / 255, 0, 0), LocalBlendIndices = new(2, 3, 0, 0) }).ToArray(), [0, 1, 2]);
        return (expected, compiled);
    }

    private static CompiledMeshGeometryDocument MaterialGeometry(string reference, uint flags) =>
        CompiledSurface([2, 1],
            [Vertex(0, 1, 0.5f, 0.5f, 1, 0), Vertex(0, 0, 0.75f, 0.25f, 1, 0), Vertex(1, 0, 1.0f, 0.0f, 0, 0)],
            [0, 1, 2]) with
        {
            MaterialDatabase = new(2, 2,
            [new CompiledMaterialDatabaseEntry(0, reference, 513), new CompiledMaterialDatabaseEntry(1, reference, flags)]),
        };

    private static CompiledMeshGeometryDocument CompiledSurface(
        IReadOnlyList<short> palette,
        IReadOnlyList<CompiledVertex> vertices,
        IReadOnlyList<ushort> indices,
        int lodIndex = 0) => new(
        [new CompiledVertexLayout(0, 24,
        [
            new((byte)CompiledVertexFormat.Float3, (byte)CompiledVertexSemantic.Position, 0, 0, 12),
            new((byte)CompiledVertexFormat.Half2, (byte)CompiledVertexSemantic.TextureCoordinate, 0, 12, 4),
            new((byte)CompiledVertexFormat.Byte4, (byte)CompiledVertexSemantic.BlendWeights, 0, 16, 4),
            new((byte)CompiledVertexFormat.Byte4, (byte)CompiledVertexSemantic.BlendIndices, 0, 20, 4),
        ])],
        [new CompiledMeshSurface(
            EntityIndex: 2,
            Name: "generic_surface",
            LodIndex: lodIndex,
            DeclarationGroupIndex: 0,
            VertexByteOffset: 0,
            IndexByteOffset: 0,
            VertexLayout: new CompiledVertexLayout(0, 24,
            [
                new((byte)CompiledVertexFormat.Float3, (byte)CompiledVertexSemantic.Position, 0, 0, 12),
                new((byte)CompiledVertexFormat.Half2, (byte)CompiledVertexSemantic.TextureCoordinate, 0, 12, 4),
                new((byte)CompiledVertexFormat.Byte4, (byte)CompiledVertexSemantic.BlendWeights, 0, 16, 4),
                new((byte)CompiledVertexFormat.Byte4, (byte)CompiledVertexSemantic.BlendIndices, 0, 20, 4),
            ]),
            Vertices: vertices,
            Indices: indices,
            Submeshes: [new CompiledMeshSubmesh(0, 0, indices.Count, (ushort)1, palette)])],
        ["default"],
        CompiledMaterialDatabase.Empty,
        [],
        [],
        []);

    private static CompiledVertex Vertex(
        float x,
        float y,
        float firstWeight,
        float secondWeight,
        byte firstIndex,
        byte secondIndex)
    {
        Vector2 uv = new((float)(Half)x, (float)(Half)y);
        return new(
            new Vector3(x, y, 0),
            Vector3.UnitY,
            new Vector4(1, 0, 0, 1),
            uv,
            Vector2.Zero,
            Vector4.One,
            new Vector4(firstWeight, secondWeight, 0, 0),
            new CompiledBoneIndex4(firstIndex, secondIndex, 0, 0));
    }

    private static Dl1SkinBindingMode ClassifySkinBinding(
        CompiledMeshSurface surface,
        IReadOnlyList<short> palette)
    {
        var layout = new Dl1VertexLayout(
            surface.VertexLayout.Stride,
            [
                new Dl1VertexElement(
                    Dl1VertexSemantic.BlendWeights,
                    0,
                    Dl1VertexElementFormat.Byte4Normalized,
                    0,
                    0),
                new Dl1VertexElement(
                    Dl1VertexSemantic.BlendIndices,
                    0,
                    Dl1VertexElementFormat.Byte4,
                    0,
                    4),
            ]);
        Dl1MeshVertex[] vertices = surface.Vertices.Select(vertex => new Dl1MeshVertex(
            vertex.Position,
            vertex.Normal,
            vertex.Tangent,
            vertex.TextureCoordinate0,
            vertex.TextureCoordinate1,
            vertex.Color,
            vertex.BlendWeights,
            new Dl1BoneIndex4(
                vertex.LocalBlendIndices.X,
                vertex.LocalBlendIndices.Y,
                vertex.LocalBlendIndices.Z,
                vertex.LocalBlendIndices.W))).ToArray();
        var submesh = new Dl1MeshSubmesh(
            0,
            0,
            surface.Indices.Count,
            0,
            palette);
        return Dl1SkinBindingPolicy.Classify(layout, vertices, surface.Indices, submesh);
    }
}
