using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;
using CoreVector3 = ReAnimated.Core.Mathematics.Vector3D;

namespace ReAnimated.Tests;

/// <summary>Small synthetic contracts for coincident vertices and attributed triangle preservation.</summary>
public sealed class Dl1CompiledSkinningTopologyTests
{
    private const string SurfaceName = "generic_seam_surface";
    private const string ExpressionName = "generic_expression";
    private const int EntityCount = 5;
    private static readonly int[] ReorderedVertices = [1, 5, 0, 3, 2, 4];

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AcceptsCoincidentDifferentWeightsWithConsistentVertexPaletteAndTriangleReordering()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        CompiledMeshGeometryDocument compiled = ReorderedGeometry(source);

        Dl1CompiledSkinningReadBackEvidence evidence = Validate(source, compiled);

        Assert.True(evidence.TriangleAssociationsVerified);
        Assert.Equal(6, evidence.VerifiedVertexCount);
        Assert.Equal(7, evidence.VerifiedInfluenceCount);
        Assert.Equal(6, evidence.VertexCorrespondence.Length);
        Assert.Contains(evidence.VertexCorrespondence,
            row => row.SourceVertexIndex == 0 && row.CompiledVertexIndex == 2);
        Assert.Contains(evidence.VertexCorrespondence,
            row => row.SourceVertexIndex == 1 && row.CompiledVertexIndex == 0);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsWeightsSwappedOntoTrianglesWithDifferentNeighborPositions()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        CompiledVertex[] vertices = CompileVertices(source.Vertices, [1, 2, 3]);
        SwapSkin(vertices, 0, 1);
        CompiledMeshGeometryDocument compiled = Geometry(source, vertices, [0, 2, 3, 1, 4, 5]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Validate(source, compiled));
        Assert.Contains("triangle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void FindsAugmentingPathWhenTheFirstExpectedWeightRowHasTwoCandidates()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        source = source with
        {
            Vertices = source.Vertices
                .SetItem(0, source.Vertices[0] with { Influences = Blend(16384.0 / 32767) })
                .SetItem(1, source.Vertices[1] with { Influences = Blend(16548.0 / 32767) }),
            Subsets = [new([1, 2], 6) { FirstIndex = 0, DeclaredMaterialSlotIndex = 0 }],
        };
        CompiledVertex[] vertices = CompileVertices(source.Vertices, [1, 2]);
        vertices[0] = vertices[0] with
        {
            BlendWeights = new(128f / 255, 127f / 255, 0, 0),
            LocalBlendIndices = new(0, 1, 0, 0),
        };
        vertices[1] = vertices[1] with
        {
            BlendWeights = new(127f / 255, 128f / 255, 0, 0),
            LocalBlendIndices = new(0, 1, 0, 0),
        };
        CompiledMeshGeometryDocument compiled = Geometry(source, vertices, [1, 2, 3, 0, 4, 5], [1, 2]);

        Dl1CompiledSkinningReadBackEvidence evidence = Validate(source, compiled);

        Assert.True(evidence.TriangleAssociationsVerified);
        Assert.Contains(evidence.VertexCorrespondence,
            row => row.SourceVertexIndex == 0 && row.CompiledVertexIndex == 1);
        Assert.Contains(evidence.VertexCorrespondence,
            row => row.SourceVertexIndex == 1 && row.CompiledVertexIndex == 0);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsCoincidentSkinRowsTransferredAcrossUnchangedMaterialSubsets()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface() with
        {
            Subsets =
            [
                new([1, 2, 3], 3)
                {
                    FirstIndex = 0, DeclaredMaterialSlotIndex = 0,
                    DeclaredMaterialReference = "generic_first.mat",
                },
                new([1, 2, 3], 3)
                {
                    FirstIndex = 3, DeclaredMaterialSlotIndex = 1,
                    DeclaredMaterialReference = "generic_second.mat",
                },
            ],
        };
        CompiledVertex[] vertices = CompileVertices(source.Vertices, [1, 2, 3]);
        SwapSkin(vertices, 0, 1);
        CompiledMeshGeometryDocument compiled = Geometry(source, vertices, [0, 2, 3, 1, 4, 5]) with
        {
            MaterialDatabase = new(2, 2,
            [new(0, "generic_first.mat", 513), new(1, "generic_second.mat", 513)]),
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Validate(source, compiled));
        Assert.Contains("triangle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsReversedTriangleWinding()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        CompiledMeshGeometryDocument compiled = Geometry(source,
            CompileVertices(source.Vertices, [1, 2, 3]), [0, 3, 2, 1, 4, 5]);

        Assert.Throws<InvalidDataException>(() => Validate(source, compiled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("ValidationTier", "Focused")]
    public void RejectsAttributesSwappedIndependentlyOfCoincidentSkinRows(bool swapNormal)
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        source = source with
        {
            Vertices = source.Vertices
                .SetItem(0, source.Vertices[0] with
                { Normal = Vector3.UnitX, TextureCoordinate0 = new(.125f, .25f) })
                .SetItem(1, source.Vertices[1] with
                { Normal = Vector3.UnitY, TextureCoordinate0 = new(.75f, .875f) }),
        };
        CompiledVertex[] vertices = CompileVertices(source.Vertices, [1, 2, 3]);
        if (swapNormal)
        {
            Vector3 first = vertices[0].Normal;
            vertices[0] = vertices[0] with { Normal = vertices[1].Normal };
            vertices[1] = vertices[1] with { Normal = first };
        }
        else
        {
            Vector2 first = vertices[0].TextureCoordinate0;
            vertices[0] = vertices[0] with { TextureCoordinate0 = vertices[1].TextureCoordinate0 };
            vertices[1] = vertices[1] with { TextureCoordinate0 = first };
        }

        CompiledMeshGeometryDocument compiled = Geometry(source, vertices, [0, 2, 3, 1, 4, 5]);
        Assert.Throws<InvalidDataException>(() => Validate(source, compiled));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void AcceptsHardNormalAndUvSeamWhenCompleteAttributedRowsAreReordered()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        source = source with
        {
            Vertices = source.Vertices
                .SetItem(0, source.Vertices[0] with
                { Normal = Vector3.UnitX, TextureCoordinate0 = new(.125f, .25f) })
                .SetItem(1, source.Vertices[1] with
                { Normal = Vector3.UnitY, TextureCoordinate0 = new(.75f, .875f) }),
        };

        Assert.True(Validate(source, ReorderedGeometry(source)).TriangleAssociationsVerified);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RejectsMorphDeltasSwappedIndependentlyOfCoincidentSkinRows()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        ImmutableArray<CoreVector3> deltas = MorphDeltas();
        Vector3[] actualDeltas = deltas.Select(QuantizedDelta).ToArray();
        (actualDeltas[0], actualDeltas[1]) = (actualDeltas[1], actualDeltas[0]);
        CompiledMeshGeometryDocument compiled = WithMorph(Geometry(source,
            CompileVertices(source.Vertices, [1, 2, 3]), [0, 2, 3, 1, 4, 5]), actualDeltas);

        Assert.Throws<InvalidDataException>(() =>
            Dl1CompiledSkinningReadBackValidator.Validate([source], compiled, EntityCount,
                [new(SurfaceName, 0, 6, [new(ExpressionName, deltas)])]));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void AcceptsMorphAndSkinReorderingWhenTheSameCorrespondenceIsPreserved()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        ImmutableArray<CoreVector3> deltas = MorphDeltas();
        CompiledMeshGeometryDocument compiled = WithMorph(ReorderedGeometry(source),
            ReorderedVertices.Select(index => QuantizedDelta(deltas[index])).ToArray());

        Dl1CompiledSkinningReadBackEvidence evidence =
            Dl1CompiledSkinningReadBackValidator.Validate([source], compiled, EntityCount,
                [new(SurfaceName, 0, 6, [new(ExpressionName, deltas)])]);

        Assert.True(evidence.TriangleAssociationsVerified);
        Assert.Contains(evidence.VertexCorrespondence,
            row => row.SourceVertexIndex == 0 && row.CompiledVertexIndex == 2);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void AcceptsDifferentSourcePositionsThatQuantizeToOneHalfPositionSignature()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface();
        source = source with
        {
            Vertices = source.Vertices
                .SetItem(0, source.Vertices[0] with { Position = new(1.0001f, 0, 0) })
                .SetItem(1, source.Vertices[1] with { Position = new(1.0002f, 0, 0) })
                .SetItem(2, source.Vertices[2] with { Position = new(2, 0, 0) })
                .SetItem(3, source.Vertices[3] with { Position = new(1, 1, 0) })
                .SetItem(4, source.Vertices[4] with { Position = new(0, 0, 0) })
                .SetItem(5, source.Vertices[5] with { Position = new(1, -1, 0) }),
        };

        Assert.True(Validate(source, ReorderedGeometry(source, halfPositions: true))
            .TriangleAssociationsVerified);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    public void RefusesLegacyMixedCoincidentGroupWithoutSourceTriangleAssociation()
    {
        Dl1PreparedSkinningSurfaceExpectation source = SourceSurface() with { IndexBuffer = [] };
        CompiledMeshGeometryDocument compiled = Geometry(source,
            CompileVertices(source.Vertices, [1, 2, 3]), [0, 2, 3, 1, 4, 5]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Validate(source, compiled));
        Assert.Contains("triangle association", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Dl1CompiledSkinningReadBackEvidence Validate(
        Dl1PreparedSkinningSurfaceExpectation source, CompiledMeshGeometryDocument compiled) =>
        Dl1CompiledSkinningReadBackValidator.Validate([source], compiled, EntityCount);

    private static ImmutableArray<Dl1PreparedSkinInfluenceExpectation> Blend(double firstWeight) =>
        [new(1, firstWeight), new(2, 1 - firstWeight)];

    private static Dl1PreparedSkinVertexExpectation SourceVertex(Vector3 position,
        ImmutableArray<Dl1PreparedSkinInfluenceExpectation> influences) => new(position, influences)
        { Normal = Vector3.UnitZ, TextureCoordinate0 = Vector2.Zero };

    private static Dl1PreparedSkinningSurfaceExpectation SourceSurface() => new(
        SurfaceName, 0, true, 6, [0, 1, 2, 3, 4, 5],
        [new([1, 2, 3], 6) { FirstIndex = 0, DeclaredMaterialSlotIndex = 0 }],
        [
            SourceVertex(Vector3.Zero, Blend(.6)),
            SourceVertex(Vector3.Zero, [new(3, 1)]),
            SourceVertex(new(1, 0, 0), [new(1, 1)]),
            SourceVertex(new(0, 1, 0), [new(1, 1)]),
            SourceVertex(new(-1, 0, 0), [new(1, 1)]),
            SourceVertex(new(0, -1, 0), [new(1, 1)]),
        ]) { IndexBuffer = [0, 2, 3, 1, 4, 5] };

    private static CompiledVertex[] CompileVertices(
        ImmutableArray<Dl1PreparedSkinVertexExpectation> vertices, int[] palette,
        bool halfPositions = false) => vertices.Select(source =>
    {
        float[] weights = [0, 0, 0, 0];
        byte[] indexes = [0, 0, 0, 0];
        for (int lane = 0; lane < source.Influences.Length; lane++)
        {
            Dl1PreparedSkinInfluenceExpectation influence = source.Influences[lane];
            weights[lane] = (float)influence.Weight;
            indexes[lane] = checked((byte)Array.IndexOf(palette, influence.EntityIndex));
        }
        Vector3 position = halfPositions ? new((float)(Half)source.Position.X,
            (float)(Half)source.Position.Y, (float)(Half)source.Position.Z) : source.Position;
        Vector2 uv = source.TextureCoordinate0 ?? Vector2.Zero;
        uv = new((float)(Half)uv.X, (float)(Half)uv.Y);
        return new CompiledVertex(position, source.Normal ?? Vector3.UnitZ,
            new(1, 0, 0, 1), uv, Vector2.Zero, Vector4.One,
            new(weights[0], weights[1], weights[2], weights[3]),
            new(indexes[0], indexes[1], indexes[2], indexes[3]));
    }).ToArray();

    private static CompiledMeshGeometryDocument ReorderedGeometry(
        Dl1PreparedSkinningSurfaceExpectation source, bool halfPositions = false)
    {
        int[] palette = [3, 2, 1];
        CompiledVertex[] original = CompileVertices(source.Vertices, palette, halfPositions);
        CompiledVertex[] reordered = ReorderedVertices.Select(index => original[index]).ToArray();
        return Geometry(source, reordered, [0, 5, 1, 4, 3, 2], palette, halfPositions);
    }

    private static CompiledMeshGeometryDocument Geometry(
        Dl1PreparedSkinningSurfaceExpectation source, CompiledVertex[] vertices,
        ushort[] indices, int[]? palette = null, bool halfPositions = false)
    {
        palette ??= [1, 2, 3];
        int positionBytes = halfPositions ? 8 : 12;
        var layout = new CompiledVertexLayout(0, positionBytes + 16,
        [
            new((byte)(halfPositions ? CompiledVertexFormat.Half4 : CompiledVertexFormat.Float3),
                (byte)CompiledVertexSemantic.Position, 0, 0, positionBytes),
            new((byte)CompiledVertexFormat.SignedNormalizedByte4,
                (byte)CompiledVertexSemantic.Normal, 0, positionBytes, 4),
            new((byte)CompiledVertexFormat.Half2,
                (byte)CompiledVertexSemantic.TextureCoordinate, 0, positionBytes + 4, 4),
            new((byte)CompiledVertexFormat.Byte4,
                (byte)CompiledVertexSemantic.BlendWeights, 0, positionBytes + 8, 4),
            new((byte)CompiledVertexFormat.Byte4,
                (byte)CompiledVertexSemantic.BlendIndices, 0, positionBytes + 12, 4),
        ]);
        int cursor = 0;
        CompiledMeshSubmesh[] subsets = source.Subsets.Select((subset, index) =>
        {
            int first = cursor;
            cursor += subset.IndexCount;
            return new CompiledMeshSubmesh(index, first, subset.IndexCount,
                subset.DeclaredMaterialSlotIndex, palette.Select(value => checked((short)value)).ToArray());
        }).ToArray();
        return new([layout],
            [new(4, SurfaceName, 0, 0, 0, 0, layout, vertices, indices, subsets)],
            ["default"], CompiledMaterialDatabase.Empty, [], [], []);
    }

    private static void SwapSkin(CompiledVertex[] vertices, int first, int second)
    {
        Vector4 weights = vertices[first].BlendWeights;
        CompiledBoneIndex4 indexes = vertices[first].LocalBlendIndices;
        vertices[first] = vertices[first] with
        { BlendWeights = vertices[second].BlendWeights, LocalBlendIndices = vertices[second].LocalBlendIndices };
        vertices[second] = vertices[second] with { BlendWeights = weights, LocalBlendIndices = indexes };
    }

    private static ImmutableArray<CoreVector3> MorphDeltas() =>
        [new(.01, 0, 0), new(0, .02, 0), CoreVector3.Zero, CoreVector3.Zero, CoreVector3.Zero, CoreVector3.Zero];

    private static Vector3 QuantizedDelta(CoreVector3 delta) =>
        new((float)(Half)(float)delta.X, (float)(Half)(float)delta.Y, (float)(Half)(float)delta.Z);

    private static CompiledMeshGeometryDocument WithMorph(
        CompiledMeshGeometryDocument compiled, Vector3[] deltas) => compiled with
        {
            MorphChannels = [new(0, ExpressionName)],
            MorphBindings =
            [new(4, 0, deltas.Length, 8, 0, CompiledMorphDeltaFormat.PcHalf4,
                new ushort[] { 0 }, [new(0, 0, deltas)])],
        };
}

