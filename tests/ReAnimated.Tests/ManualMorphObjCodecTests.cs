using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class ManualMorphObjCodecTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NeutralExportUsesCurrentControlPointPositionsAndRoundTripsSplitCorners()
    {
        FbxModelSurface target = Face();
        byte[] obj = ManualMorphObjCodec.EncodeNeutral(target);
        string text = Encoding.UTF8.GetString(obj);
        Assert.StartsWith("# DL ReAnimated neutral face sculpt; positions in metres;", text, StringComparison.Ordinal);
        Assert.Equal(4, text.Split('\n').Count(line => line.StartsWith("v ", StringComparison.Ordinal)));
        Assert.Contains("f 1 2 3\n", text, StringComparison.Ordinal);
        Assert.Contains("f 1 3 4\n", text, StringComparison.Ordinal);
        Assert.All(ManualMorphObjCodec.ComputePositionDeltas(target, obj), delta => Assert.Equal(Vector3D.Zero, delta));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void OneSourcePointSculptMovesOnlyItsMappedRenderCorners()
    {
        FbxModelSurface target = Face();
        string neutral = Encoding.UTF8.GetString(ManualMorphObjCodec.EncodeNeutral(target));
        string sculpt = neutral.Replace("v 1 0 0\n", "v 1.25 0 0\n", StringComparison.Ordinal);
        ImmutableArray<Vector3D> deltas = ManualMorphObjCodec.ComputePositionDeltas(target, Encoding.UTF8.GetBytes(sculpt));
        Assert.Equal(target.Vertices.Length, deltas.Length);
        Assert.Equal(new Vector3D(0.25, 0, 0), deltas[1]);
        Assert.All(deltas.Where((_, i) => i != 1), delta => Assert.Equal(Vector3D.Zero, delta));
        Assert.Equal(Vector3D.Zero, deltas[4]); // split corner for control point zero
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ConflictingCornersAndNonfinitePositionsAreRefused()
    {
        FbxModelSurface target = Face();
        FbxModelSurface conflicting = target with { Vertices = target.Vertices.SetItem(4,
            target.Vertices[4] with { Position = new Vector3D(0.001, 0, 0) }) };
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.EncodeNeutral(conflicting));
        FbxModelSurface nonfinite = target with { Vertices = target.Vertices.SetItem(1,
            target.Vertices[1] with { Position = new Vector3D(double.NaN, 0, 0) }) };
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.EncodeNeutral(nonfinite));
        FbxModelSurface unused = target with { SourceGeometry = target.SourceGeometry! with
            { ControlPoints = target.SourceGeometry!.ControlPoints.Add(new Vector3D(double.NaN, 2, 0)) } };
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.EncodeNeutral(unused));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void UsedCurrentNeutralOverridesSourceMetadataWhileUnusedPointRemainsInMetres()
    {
        FbxModelSurface target = Face();
        ImmutableArray<Vector3D> sourcePoints = target.SourceGeometry!.ControlPoints
            .SetItem(0, new Vector3D(-10, -10, 0))
            .Add(new Vector3D(2, 2, 0));
        target = target with { SourceGeometry = target.SourceGeometry! with { ControlPoints = sourcePoints } };

        byte[] obj = ManualMorphObjCodec.EncodeNeutral(target);
        string[] rows = Encoding.UTF8.GetString(obj).Split('\n');
        Assert.Contains("v 0 0 0", rows);
        Assert.Contains("v 2 2 0", rows);
        Assert.DoesNotContain("v -10 -10 0", rows);
        Assert.Equal(5, rows.Count(row => row.StartsWith("v ", StringComparison.Ordinal)));
        Assert.All(ManualMorphObjCodec.ComputePositionDeltas(target, obj),
            delta => Assert.Equal(Vector3D.Zero, delta));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ExactFaceOrderAndPointInventoryRejectReindexingOrTopologyChanges()
    {
        FbxModelSurface target = Face();
        string neutral = Encoding.UTF8.GetString(ManualMorphObjCodec.EncodeNeutral(target));
        Assert.Throws<InvalidDataException>(() => Decode(neutral.Replace("f 1 3 4\n", "f 1 4 3\n", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Decode(neutral.Replace("f 1 2 3\n", "f 2 1 3\n", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Decode(neutral.Replace("v 0 1 0\n", string.Empty, StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Decode(neutral.Replace("f 1 2 3\n", "f 1 2 3 4\n", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Decode(neutral.Replace("f 1 2 3\n", "f 1 2 99\n", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Decode(neutral.Replace("f 1 2 3\n", "f -1 2 3\n", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => Decode(neutral + "o another\n"));
        Assert.Throws<InvalidDataException>(() => Decode("g first\ng second\n" + neutral));

        void Decode(string text) => _ = ManualMorphObjCodec.ComputePositionDeltas(target, Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void TextureAndNormalFaceIndicesAreCheckedButDoNotChangeMorphTopology()
    {
        FbxModelSurface target = Face();
        string neutral = Encoding.UTF8.GetString(ManualMorphObjCodec.EncodeNeutral(target));
        string decorated = neutral.Replace("f 1 2 3\n",
            "o Face\ng Surface\nmtllib face.mtl\nusemtl skin\ns off\nvt 0 0\nvn 0 0 1\nf 1/1/1 2/1/1 3/1/1\n",
            StringComparison.Ordinal).Replace("f 1 3 4\n", "s 0\nf 1 3 4\n", StringComparison.Ordinal);
        Assert.All(ManualMorphObjCodec.ComputePositionDeltas(target, Encoding.UTF8.GetBytes(decorated)),
            delta => Assert.Equal(Vector3D.Zero, delta));
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.ComputePositionDeltas(target,
            Encoding.UTF8.GetBytes(decorated.Replace("2/1/1", "2/2/1", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.ComputePositionDeltas(target,
            Encoding.UTF8.GetBytes(decorated.Replace("mtllib face.mtl", "mtllib ../other.mtl", StringComparison.Ordinal))));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void MalformedNonfiniteAndOversizedObjInputsFailClosed()
    {
        FbxModelSurface target = Face();
        string neutral = Encoding.UTF8.GetString(ManualMorphObjCodec.EncodeNeutral(target));
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.ComputePositionDeltas(target,
            Encoding.UTF8.GetBytes(neutral.Replace("v 1 0 0", "v NaN 0 0", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.ComputePositionDeltas(target,
            Encoding.UTF8.GetBytes(neutral.Replace("v 1 0 0", "v 1e999 0 0", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.ComputePositionDeltas(target, [0xff]));
        Assert.Throws<InvalidDataException>(() => ManualMorphObjCodec.ComputePositionDeltas(target,
            new byte[ManualMorphObjCodec.MaximumFileBytes + 1]));
    }

    private static FbxModelSurface Face()
    {
        ImmutableArray<Vector3D> positions =
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
        ImmutableArray<FbxModelVertex> vertices =
        [
            Vertex(positions[0]), Vertex(positions[1]), Vertex(positions[2]), Vertex(positions[3]),
            Vertex(positions[0]),
        ];
        return new FbxModelSurface("face", "Face", Guid.NewGuid(), vertices,
            [0, 1, 2, 4, 2, 3], [], [], IsSkinned: false)
        {
            SourceGeometry = new GeometrySourceComponent("source-face", positions),
            SourceCorners = [new(0, 0), new(1, 1), new(2, 2), new(3, 3), new(0, 4)],
            SourceTriangles = [new(0, 0), new(1, 0)],
        };
    }

    private static FbxModelVertex Vertex(Vector3D position) =>
        new(position, Vector3D.UnitZ, 0, 0, [], []);
}
