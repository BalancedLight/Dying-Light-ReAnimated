using System.Buffers.Binary;
using System.Text;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1SourceMeshReaderSemanticTests
{
    private static readonly string[] ExpectedMaterialNames = ["material_a", "material_b"];
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void ReadsHelperRigidAndMultipleLodsWithoutCollapsingPerDrawPalettes()
    {
        var document = Dl1SourceMeshReader.Parse(Fixture());
        Assert.Equal(ExpectedMaterialNames, document.MaterialNames);
        Assert.Equal(4, document.Nodes.Length);
        Assert.Equal(8u, document.Nodes[0].Type);
        Assert.Equal(1, document.Nodes[0].DescendantCount);
        Assert.Equal(4u, document.Nodes[1].Type);
        Assert.Equal(0, document.Nodes[1].ParentIndex);
        Assert.Empty(document.Nodes[1].Lods);
        Assert.Equal(2, document.Nodes[2].Lods.Length);
        var shared = document.Nodes[2].Lods[0];
        Assert.Equal(2, shared.Subsets.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 1, 2 }, shared.Indices);
        Assert.Equal(0, shared.Subsets[0].MaterialIndex);
        Assert.Equal(1, shared.Subsets[1].MaterialIndex);
        Assert.Equal(new ushort[] { 0, 1 }, shared.Subsets[0].Palette);
        Assert.Equal(new ushort[] { 1, 0, 1 }, shared.Subsets[1].Palette);
        byte localIndex = shared.Skin[0].PaletteIndices[0];
        Assert.Equal((byte)1, localIndex);
        Assert.Equal((ushort)1, shared.Subsets[0].Palette[localIndex]);
        Assert.Equal((ushort)0, shared.Subsets[1].Palette[localIndex]);
        Assert.Equal(new short[] { 24575, 8192 }, shared.Skin[0].QuantizedWeights);
        Assert.Single(document.Nodes[2].Lods[1].Subsets);
        Assert.Equal(1u, document.Nodes[3].Type);
        Assert.Empty(Assert.Single(document.Nodes[3].Lods).Skin);
        Assert.Empty(Assert.Single(document.Nodes[3].Lods[0].Subsets).Palette);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void SharedVertexMustBeValidForEveryDrawThatUsesIt()
    {
        // Local index 1 is valid in the second palette but invalid in the first.
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(Fixture(invalidFirstPalette: true)));
    }

    [Theory]
    [InlineData(0x9999u)]
    [InlineData(0x131u)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void RejectsUnknownLodChunksAndEmptySkinBodies(uint extraChunk)
    {
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(Fixture(extraChunk: extraChunk)));
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    [InlineData(0, 4)]
    [InlineData(3, 1)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void RejectsUnskinnedSubsetOutsideIndexBuffer(int first, int count)
    {
        Assert.Throws<InvalidDataException>(() => Dl1SourceMeshReader.Parse(
            UnskinnedSubsetMesh((uint)first, (uint)count)));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "MandatoryCodec")]
    public void AcceptsUnskinnedSubsetAtIndexBufferBoundary(int first, int count)
    {
        var lod = Assert.Single(Assert.Single(Dl1SourceMeshReader.Parse(
            UnskinnedSubsetMesh((uint)first, (uint)count)).Nodes).Lods);
        Assert.Empty(lod.Skin);
        var subset = Assert.Single(lod.Subsets);
        Assert.Equal(first, subset.FirstIndex);
        Assert.Equal(count, subset.IndexCount);
    }

    private static byte[] UnskinnedSubsetMesh(uint first, uint count) =>
        Chunk(0x0048534D, UInts(1, 1, 1),
            Chunk(0x500, Names("material")), Chunk(0x700, Names("surface")),
            Node(1, "mesh", -1, 0, Lod(skinned: false, shared: false, firstIndex: first, indexCount: count)));

    private static byte[] Fixture(bool invalidFirstPalette = false, uint extraChunk = 0)
    {
        byte[] shared = Lod(skinned: true, shared: true, invalidFirstPalette, extraChunk);
        byte[] other = Lod(skinned: true, shared: false);
        byte[] rigid = Lod(skinned: false, shared: false);
        return Chunk(0x0048534D, UInts(4, 2, 1),
            Chunk(0x500, Names("material_a", "material_b")),
            Chunk(0x700, Names("surface")),
            Node(8, "root", -1, 1), Node(4, "helper", 0, 0),
            Node(2, "skinned", -1, 0, shared, other), Node(1, "rigid", -1, 0, rigid));
    }

    private static byte[] Lod(bool skinned, bool shared, bool invalidFirstPalette = false, uint extraChunk = 0,
        uint firstIndex = 0, uint indexCount = 3)
    {
        ushort[] indices = shared ? [0, 1, 2, 0, 1, 2] : [0, 1, 2];
        var children = new List<byte[]>
        {
            Chunk(0x160, new byte[60]),
            Chunk(0x101, Floats(0, 0, 0, 1, 0, 0, 0, 1, 0)),
            Chunk(0x102, Floats(0, 0, 1, 0, 0, 1, 0, 0, 1)),
            Chunk(0x103, Floats(1, 0, 0, 1, 0, 0, 1, 0, 0)),
            Chunk(0x195, Floats(0, 1, 0, 0, 1, 0, 0, 1, 0)),
            Chunk(0x120, Floats(0, 0, 1, 0, 0, 1)),
        };
        if (skinned)
        {
            byte[] skin = new byte[19];
            skin[0] = 2;
            for (int row = 0; row < 3; row++)
            {
                int offset = 1 + row * 6;
                skin[offset] = 1;
                skin[offset + 1] = 0;
                BinaryPrimitives.WriteInt16LittleEndian(skin.AsSpan(offset + 2), 24575);
                BinaryPrimitives.WriteInt16LittleEndian(skin.AsSpan(offset + 4), 8192);
            }
            if (extraChunk != 0x131) children.Add(Chunk(0x131, skin));
        }
        if (extraChunk != 0) children.Add(Chunk(extraChunk, []));
        children.Add(Chunk(0x140, Ushorts(indices)));
        ushort[] palette = skinned ? invalidFirstPalette ? [0] : [0, 1] : [];
        byte[] first = Subset(0, firstIndex, indexCount, palette);
        byte[] table = shared ? first.Concat(Subset(1, 3, 3, [1, 0, 1])).ToArray() : first;
        children.Add(Chunk(0x151, table));
        return Chunk(0x100, UInts(3, (uint)indices.Length, shared ? 2u : 1u, 0), children.ToArray());
    }

    private static byte[] Node(uint type, string name, short parent, ushort descendants, params byte[][] lods)
    {
        byte[] payload = new byte[208];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, type);
        Names(name).CopyTo(payload, 4);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(68), parent);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(70), descendants);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(72), (uint)lods.Length);
        byte[] identity = Floats(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);
        identity.CopyTo(payload, 76);
        identity.CopyTo(payload, 124);
        return Chunk(3, payload, lods);
    }

    private static byte[] Subset(ushort material, uint first, uint count, ushort[] palette)
    {
        byte[] payload = new byte[12 + palette.Length * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, material);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), first);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), count);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), (ushort)palette.Length);
        Ushorts(palette).CopyTo(payload, 12);
        return payload;
    }

    private static byte[] Names(params string[] names)
    {
        byte[] bytes = new byte[names.Length * 64];
        for (int i = 0; i < names.Length; i++) Encoding.UTF8.GetBytes(names[i]).CopyTo(bytes, i * 64);
        return bytes;
    }

    private static byte[] Floats(params float[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static byte[] UInts(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static byte[] Ushorts(params ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static byte[] Chunk(uint id, byte[] payload, params byte[][] children)
    {
        byte[] bytes = new byte[16 + payload.Length + children.Sum(child => child.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(bytes, 16);
        int offset = 16 + payload.Length;
        foreach (byte[] child in children) { child.CopyTo(bytes, offset); offset += child.Length; }
        return bytes;
    }
}
