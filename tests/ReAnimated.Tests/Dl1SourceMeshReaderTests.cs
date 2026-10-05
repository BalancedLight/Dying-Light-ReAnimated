using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Dl1SourceMeshReaderTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void ReadsGenericSourceMeshStreamsAndSkinPalette()
    {
        Dl1SourceMeshDocument document = Dl1SourceMeshReader.Parse(BuildMesh());
        Assert.Equal("material", Assert.Single(document.MaterialNames));
        Assert.Equal("surface", Assert.Single(document.SurfaceNames));
        Assert.Equal(2, document.Nodes.Length);
        Dl1SourceMeshNode mesh = document.Nodes[1];
        Assert.Equal("mesh", mesh.Name);
        Assert.Equal(-1, mesh.ParentIndex);
        Dl1SourceMeshLod lod = Assert.Single(mesh.Lods);
        Assert.Equal(3, lod.Positions.Length);
        Assert.Equal(new uint[] { 0, 1, 2 }, lod.Indices.ToArray());
        Dl1SourceMeshSubset subset = Assert.Single(lod.Subsets);
        Assert.Equal(new ushort[] { 0, 0 }, subset.Palette.ToArray());
        Assert.Equal(3, subset.IndexCount);
        Assert.Equal(3, lod.Skin.Length);
        Dl1SourceMeshSkinVertex skin = lod.Skin[0];
        Assert.Equal(new byte[] { 0, 0 }, skin.PaletteIndices.ToArray());
        Assert.Equal(new short[] { 32767, 0 }, skin.QuantizedWeights.ToArray());
        Dl1SourceMeshMorphTarget morph = Assert.Single(lod.MorphTargets);
        Assert.Equal("smile", morph.Name);
        Assert.Equal(3, morph.PositionDeltas.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void RejectsTruncationUnknownChunksAndBadPalette()
    {
        byte[] valid = BuildMesh();
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(valid.AsSpan(0, valid.Length - 1).ToArray()));
        byte[] unknown = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(unknown.AsSpan(16, 4), 0x9999);
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(unknown));
        byte[] badPalette = BuildMesh(palette: 2);
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(badPalette));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void RejectsOverflowingAndZeroBodyChunkHeaders()
    {
        byte[] overflowing = BuildMesh();
        BinaryPrimitives.WriteUInt32LittleEndian(overflowing.AsSpan(8), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(overflowing));

        byte[] zeroBody = BuildMesh();
        BinaryPrimitives.WriteUInt32LittleEndian(zeroBody.AsSpan(12), 0);
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(zeroBody));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void ValidatesBranchingPreorderAndDeepChainsIteratively()
    {
        Dl1SourceMeshDocument branching = Dl1SourceMeshReader.Parse(BuildHierarchy(
            (-1, 3), (0, 1), (1, 0), (0, 0)));
        Assert.Equal(4, branching.Nodes.Length);

        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(BuildHierarchy(
            (-1, 1), (0, 2), (1, 0), (-1, 0))));

        const int nodeCount = Dl1SourceMeshReader.MaximumPhysicalNodes;
        var nodes = new List<byte[]>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
            nodes.Add(Node(2, $"n{i}", i == 0 ? -1 : i - 1, 0, [], nodeCount - i - 1));
        byte[] deep = Chunk(0x0048534D, RootPayload(nodeCount),
            new[] { Chunk(0x500, Name("material")), Chunk(0x700, Name("surface")) }.Concat(nodes).ToArray());
        Assert.Equal(nodeCount, Dl1SourceMeshReader.Parse(deep).Nodes.Length);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void RejectsWeightsAndHierarchyCycles()
    {
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(BuildMesh(weight: 1)));
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(BuildMesh(cycle: true)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void RejectsNegativeBoundsAndNonzeroReservedNodeBytes()
    {
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(BuildMesh(negativeBounds: true)));
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(BuildMesh(reservedNodeByte: true)));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void HonorsCancellationBeforeAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Dl1SourceMeshReader.Parse(BuildMesh(), cancellation.Token));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void ReadsAllEmittedInfluenceEncodings()
    {
        foreach (int influenceCount in new[] { 1, 2, 3, 4 })
        {
            Dl1SourceMeshLod lod = Assert.Single(
                Dl1SourceMeshReader.Parse(BuildMesh(influenceCount: influenceCount)).Nodes[1].Lods);
            Assert.Equal(3, lod.Skin.Length);
            Assert.Equal(influenceCount, lod.Skin[0].PaletteIndices.Length);
            Assert.Equal(32767, lod.Skin[0].QuantizedWeights.Sum(static weight => (int)weight));
        }
    }

    private static byte[] BuildMesh(ushort palette = 0, short weight = 32767, bool cycle = false, int influenceCount = 2, bool negativeBounds = false, bool reservedNodeByte = false)
    {
        byte[] format = new byte[60];
        byte[] positions = Vector3([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        byte[] normals = Vector3([new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)]);
        byte[] uvs = Vector2([(0, 0), (1, 0), (0, 1)]);
        byte[] indices = UInt16([0, 1, 2]);
        byte[] skin = influenceCount == 4 ? new byte[3 * 12] : new byte[1 + (3 * (influenceCount + (influenceCount > 1 ? influenceCount * 2 : 0)))];
        if (influenceCount == 4)
        {
            for (int row = 0; row < 3; row++)
            {
                int offset = row * 12;
                for (int i = 0; i < 4; i++) skin[offset + i] = 0;
                BinaryPrimitives.WriteInt16LittleEndian(skin.AsSpan(offset + 4), weight);
                for (int i = 1; i < 4; i++) BinaryPrimitives.WriteInt16LittleEndian(skin.AsSpan(offset + 4 + i * 2), (short)(weight == 1 && i == 1 ? 32767 : 0));
            }
        }
        else
        {
            skin[0] = (byte)influenceCount;
            for (int row = 0; row < 3; row++)
            {
                int offset = 1 + row * (influenceCount + (influenceCount > 1 ? influenceCount * 2 : 0));
                for (int i = 0; i < influenceCount; i++) skin[offset + i] = 0;
                if (influenceCount > 1)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(skin.AsSpan(offset + influenceCount), weight);
                    for (int i = 1; i < influenceCount; i++) BinaryPrimitives.WriteInt16LittleEndian(skin.AsSpan(offset + influenceCount + i * 2), (short)(weight == 1 && i == 1 ? 32767 : 0));
                }
            }
        }
        byte[] subset = new byte[12 + (influenceCount * 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(subset.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(subset.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(10), (ushort)influenceCount);
        for (int i = 0; i < influenceCount; i++) BinaryPrimitives.WriteUInt16LittleEndian(subset.AsSpan(12 + i * 2), i == 0 ? palette : (ushort)0);
        byte[] morph = new byte[64 + 36]; System.Text.Encoding.UTF8.GetBytes("smile").CopyTo(morph, 0);
        BinaryPrimitives.WriteInt32LittleEndian(morph.AsSpan(64), BitConverter.SingleToInt32Bits(0.1f));
        byte[] lodPayload = new byte[16]; BinaryPrimitives.WriteUInt32LittleEndian(lodPayload.AsSpan(0), 3); BinaryPrimitives.WriteUInt32LittleEndian(lodPayload.AsSpan(4), 3); BinaryPrimitives.WriteUInt32LittleEndian(lodPayload.AsSpan(8), 1); BinaryPrimitives.WriteUInt32LittleEndian(lodPayload.AsSpan(12), 1);
        byte[] lod = Chunk(0x100, lodPayload, Chunk(0x160, format), Chunk(0x101, positions), Chunk(0x104, morph), Chunk(0x102, normals), Chunk(0x103, normals), Chunk(0x195, normals), Chunk(0x120, uvs), Chunk(influenceCount == 4 ? 0x130u : 0x131u, skin), Chunk(0x140, indices), Chunk(0x151, subset));
        byte[] bone = Node(8, "bone", cycle ? 1 : -1, 0, []);
        byte[] mesh = Node(2, "mesh", cycle ? 0 : -1, 1, [lod], negativeBounds: negativeBounds, reservedNodeByte: reservedNodeByte);
        return Chunk(0x0048534D, [2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], Chunk(0x500, Name("material")), Chunk(0x700, Name("surface")), bone, mesh);
    }

    private static byte[] BuildHierarchy(params (int Parent, int Descendants)[] hierarchy)
    {
        byte[][] nodes = hierarchy.Select((entry, index) =>
            Node(2, $"node{index}", entry.Parent, 0, [], entry.Descendants)).ToArray();
        byte[][] children = [Chunk(0x500, Name("material")), Chunk(0x700, Name("surface"))];
        return Chunk(0x0048534D, RootPayload(hierarchy.Length), children.Concat(nodes).ToArray());
    }

    private static byte[] RootPayload(int nodeCount)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)nodeCount);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1);
        return payload;
    }

    private static byte[] Node(uint type, string name, int parent, int lodCount, byte[][] lods, int descendantCount = 0, bool negativeBounds = false, bool reservedNodeByte = false)
    {
        byte[] payload = new byte[0xD0]; BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(), type); Name(name).CopyTo(payload, 4); BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(68), (short)parent); BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(70), (ushort)descendantCount); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(72), (uint)lodCount); if (negativeBounds) BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(184), BitConverter.SingleToInt32Bits(-1f)); if (reservedNodeByte) payload[200] = 1; BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(196), 0); return Chunk(3, payload, lods);
    }
    private static byte[] Name(string value) { byte[] b = new byte[64]; System.Text.Encoding.UTF8.GetBytes(value).CopyTo(b, 0); return b; }
    private static byte[] Vector3(ImmutableArray<Vector3D> values) { byte[] b = new byte[values.Length * 12]; for (int i = 0; i < values.Length; i++) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 12), BitConverter.SingleToInt32Bits((float)values[i].X)); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 12 + 4), BitConverter.SingleToInt32Bits((float)values[i].Y)); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 12 + 8), BitConverter.SingleToInt32Bits((float)values[i].Z)); } return b; }
    private static byte[] Vector2((float X, float Y)[] values) { byte[] b = new byte[values.Length * 8]; for (int i = 0; i < values.Length; i++) { BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8), BitConverter.SingleToInt32Bits(values[i].X)); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 8 + 4), BitConverter.SingleToInt32Bits(values[i].Y)); } return b; }
    private static byte[] UInt16(ushort[] values) { byte[] b = new byte[values.Length * 2]; for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), values[i]); return b; }
    private static byte[] Chunk(uint id, byte[] payload, params byte[][] children) { int total = 16 + payload.Length + children.Sum(c => c.Length); byte[] b = new byte[total]; BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(), id); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)total); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)payload.Length); payload.CopyTo(b, 16); int o = 16 + payload.Length; foreach (byte[] child in children) { child.CopyTo(b, o); o += child.Length; } return b; }
}
