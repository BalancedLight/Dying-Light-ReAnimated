using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1SourceMeshDocument(
    ImmutableArray<string> MaterialNames,
    ImmutableArray<string> SurfaceNames,
    ImmutableArray<Dl1SourceMeshNode> Nodes);

public sealed record Dl1SourceMeshNode(
    uint Type,
    string Name,
    int ParentIndex,
    int DescendantCount,
    ImmutableArray<float> LocalMatrix,
    ImmutableArray<float> ReferenceMatrix,
    Vector3D BoundsCenter,
    Vector3D BoundsHalfExtents,
    uint Flags,
    ImmutableArray<Dl1SourceMeshLod> Lods);

public sealed record Dl1SourceMeshLod(
    int VertexCount,
    int IndexCount,
    int SubsetCount,
    int MorphCount,
    ImmutableArray<byte> VertexFormatDeclaration,
    ImmutableArray<Vector3D> Positions,
    ImmutableArray<Vector3D> Normals,
    ImmutableArray<Vector3D> Tangents,
    ImmutableArray<Vector3D> Bitangents,
    ImmutableArray<Dl1SourceMeshVector2> Uvs,
    ImmutableArray<uint> Indices,
    ImmutableArray<Dl1SourceMeshSubset> Subsets,
    ImmutableArray<Dl1SourceMeshSkinVertex> Skin,
    ImmutableArray<Dl1SourceMeshMorphTarget> MorphTargets);

public readonly record struct Dl1SourceMeshVector2(float X, float Y);

public sealed record Dl1SourceMeshSubset(
    int MaterialIndex,
    int FirstIndex,
    int IndexCount,
    ImmutableArray<ushort> Palette);

public sealed record Dl1SourceMeshSkinVertex(
    ImmutableArray<byte> PaletteIndices,
    ImmutableArray<short> QuantizedWeights);

public sealed record Dl1SourceMeshMorphTarget(
    string Name,
    ImmutableArray<Vector3D> PositionDeltas);

/// <summary>Bounded reader for the source MSH chunks emitted by Dl1SourceModelWriter.</summary>
public static class Dl1SourceMeshReader
{
    public const int MaximumInputBytes = 256 * 1024 * 1024;
    public const int MaximumPhysicalNodes = 32_768;
    public const int MaximumVerticesPerLod = 65_535;
    public const int MaximumPaletteEntries = 256;
    private const int MaximumChunks = 1_000_000;
    private const int MaximumDepth = 8;
    private const int MaximumAggregateVertices = 4_000_000;
    private const int MaximumAggregateIndices = 12_000_000;
    private const int MaximumAggregateMorphDeltas = 12_000_000;
    private const uint MshMagic = 0x0048_534D;

    public static Dl1SourceMeshDocument Parse(
        ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken = default)
    {
        try { return ParseCore(payload, cancellationToken); }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The source MSH contains an overflowing count or size.", exception);
        }
    }

    private static Dl1SourceMeshDocument ParseCore(
        ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is < 16 or > MaximumInputBytes)
            throw new InvalidDataException("The source MSH payload exceeds its bounded input limits.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = payload.ToArray();
        var parser = new Parser(bytes, cancellationToken);
        Chunk root = parser.ReadChunk(0, bytes.Length, 0);
        if (root.Id != MshMagic || root.PayloadLength != 12 || root.End != bytes.Length)
            throw new InvalidDataException("The source MSH root chunk is invalid.");
        int nodeCount = parser.U32(root.PayloadOffset);
        int materialCount = parser.U32(root.PayloadOffset + 4);
        int surfaceCount = parser.U32(root.PayloadOffset + 8);
        if (nodeCount is < 1 or > MaximumPhysicalNodes)
            throw new InvalidDataException("The source MSH physical-node count is outside its bound.");
        ImmutableArray<string> materials = parser.ReadNameTable(
            root.Children.SingleOrDefault(c => c.Id == 0x500), materialCount);
        ImmutableArray<string> surfaces = parser.ReadNameTable(
            root.Children.SingleOrDefault(c => c.Id == 0x700), surfaceCount);
        Chunk[] nodeChunks = root.Children.Where(c => c.Id == 0x0003).ToArray();
        if (nodeChunks.Length != nodeCount || root.Children.Count != nodeCount + 2)
            throw new InvalidDataException("The source MSH root tables or node count is inconsistent.");
        var nodes = ImmutableArray.CreateBuilder<Dl1SourceMeshNode>(nodeCount);
        for (int index = 0; index < nodeChunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes.Add(parser.ReadNode(nodeChunks[index], index, materialCount, nodeCount));
        }
        ValidateHierarchy(nodes.ToImmutable(), cancellationToken);
        return new(materials, surfaces, nodes.MoveToImmutable());
    }

    private static void ValidateHierarchy(
        ImmutableArray<Dl1SourceMeshNode> nodes,
        CancellationToken cancellationToken)
    {
        var openSubtrees = new Stack<(int Index, int End)>();
        for (int index = 0; index < nodes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (openSubtrees.Count > 0 && index >= openSubtrees.Peek().End)
                openSubtrees.Pop();

            int parent = nodes[index].ParentIndex;
            if (parent < -1 || parent >= index)
                throw new InvalidDataException("A source MSH node has an invalid parent index.");
            if (parent < 0)
            {
                if (openSubtrees.Count != 0)
                    throw new InvalidDataException("A source MSH root interrupts an open preorder subtree.");
            }
            else if (openSubtrees.Count == 0 || openSubtrees.Peek().Index != parent)
            {
                throw new InvalidDataException("A source MSH node is outside its parent's preorder subtree.");
            }

            int end = checked(index + nodes[index].DescendantCount + 1);
            if (end > nodes.Length)
                throw new InvalidDataException("A source MSH descendant count exceeds the node table.");
            if (parent >= 0 && end > openSubtrees.Peek().End)
                throw new InvalidDataException("A source MSH node extends beyond its parent's preorder subtree.");
            openSubtrees.Push((index, end));
        }

        while (openSubtrees.Count > 0 && nodes.Length >= openSubtrees.Peek().End)
            openSubtrees.Pop();
        if (openSubtrees.Count > 0)
            throw new InvalidDataException("A source MSH hierarchy has an incomplete preorder subtree.");
    }

    private sealed class Parser
    {
        private readonly byte[] _bytes;
        private readonly CancellationToken _token;
        private int _chunks;
        private int _aggregateVertices;
        private int _aggregateIndices;
        private int _aggregateMorphDeltas;

        public Parser(byte[] bytes, CancellationToken token) { _bytes = bytes; _token = token; }
        public int U32(int offset)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset, 4));
            return checked((int)value);
        }
        public short I16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(offset, 2));
        public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(offset, 2));
        public float F32(int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(offset, 4)));

        public Chunk ReadChunk(int offset, int limit, int depth)
        {
            _token.ThrowIfCancellationRequested();
            if (++_chunks > MaximumChunks || depth > MaximumDepth || offset < 0 || limit - offset < 16)
                throw new InvalidDataException("The source MSH chunk tree exceeds its bounded limits.");
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset, 4));
            uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset + 4, 4));
            int total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset + 8, 4)));
            int payload = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(offset + 12, 4)));
            if (reserved != 0 || total < 16 || payload > total - 16 || total > limit - offset)
                throw new InvalidDataException("The source MSH chunk header is invalid or truncated.");
            int payloadOffset = offset + 16;
            int childOffset = payloadOffset + payload;
            int end = offset + total;
            var children = new List<Chunk>();
            while (childOffset < end)
            {
                _token.ThrowIfCancellationRequested();
                Chunk child = ReadChunk(childOffset, end, depth + 1);
                children.Add(child);
                childOffset = child.End;
            }
            if (childOffset != end) throw new InvalidDataException("The source MSH chunk children do not fill their parent.");
            return new(id, payloadOffset, payload, end, children);
        }

        public ImmutableArray<string> ReadNameTable(Chunk? chunk, int count)
        {
            if (chunk is null || chunk.Children.Count != 0 || chunk.PayloadLength != checked(count * 64))
                throw new InvalidDataException("A source MSH name table has an inconsistent size.");
            var names = ImmutableArray.CreateBuilder<string>(count);
            for (int i = 0; i < count; i++)
            {
                _token.ThrowIfCancellationRequested();
                names.Add(ReadFixedName(chunk.PayloadOffset + i * 64));
            }
            return names.MoveToImmutable();
        }

        public Dl1SourceMeshNode ReadNode(Chunk chunk, int index, int materialCount, int nodeCount)
        {
            if (chunk.PayloadLength != 0xD0 || chunk.Children.Any(c => c.Id != 0x100))
                throw new InvalidDataException("A source MSH node chunk is malformed.");
            for (int i = 200; i < 208; i++)
                if (_bytes[chunk.PayloadOffset + i] != 0)
                    throw new InvalidDataException("A source MSH node contains nonzero reserved bytes.");
            uint type = checked((uint)U32(chunk.PayloadOffset));
            if (type is not (1 or 2 or 4 or 8))
                throw new InvalidDataException("A source MSH node type is unsupported.");
            int lodCount = U32(chunk.PayloadOffset + 72);
            if (lodCount != chunk.Children.Count || lodCount > 1024) throw new InvalidDataException("A source MSH LOD count is invalid.");
            return new(
                type, ReadFixedName(chunk.PayloadOffset + 4), I16(chunk.PayloadOffset + 68),
                U16(chunk.PayloadOffset + 70), ReadFloats(chunk.PayloadOffset + 76, 12), ReadFloats(chunk.PayloadOffset + 124, 12),
                ReadVectorCheckedBounds(chunk.PayloadOffset + 172), ReadVectorCheckedHalfExtents(chunk.PayloadOffset + 184), checked((uint)U32(chunk.PayloadOffset + 196)),
                chunk.Children.Select(c => ReadLod(c, materialCount, nodeCount)).ToImmutableArray());
        }

        private Dl1SourceMeshLod ReadLod(Chunk chunk, int materialCount, int nodeCount)
        {
            if (chunk.PayloadLength != 16) throw new InvalidDataException("A source MSH LOD payload is malformed.");
            int vertices = U32(chunk.PayloadOffset), indices = U32(chunk.PayloadOffset + 4), subsets = U32(chunk.PayloadOffset + 8), morphs = U32(chunk.PayloadOffset + 12);
            if (vertices is < 1 or > MaximumVerticesPerLod || indices < 3 || indices % 3 != 0 || subsets < 1 || subsets > 65535 || morphs > 1024)
                throw new InvalidDataException("A source MSH LOD count is outside its bound.");
            var allowed = new HashSet<uint> { 0x160, 0x101, 0x104, 0x102, 0x103, 0x195, 0x120, 0x130, 0x131, 0x140, 0x151 };
            if (chunk.Children.Any(child => !allowed.Contains(child.Id))) throw new InvalidDataException("The source MSH LOD contains an unsupported child chunk.");
            Chunk format = One(chunk, 0x160); if (format.PayloadLength != 60 || format.Children.Count != 0) throw new InvalidDataException("The source MSH vertex format declaration is invalid.");
            Reserve(ref _aggregateVertices, vertices, MaximumAggregateVertices, "vertex");
            Reserve(ref _aggregateIndices, indices, MaximumAggregateIndices, "index");
            Reserve(ref _aggregateMorphDeltas, checked(vertices * morphs), MaximumAggregateMorphDeltas, "morph delta");
            ImmutableArray<Vector3D> positions = V3(One(chunk, 0x101), vertices);
            ImmutableArray<Vector3D> normals = V3(One(chunk, 0x102), vertices);
            ImmutableArray<Vector3D> tangents = V3(One(chunk, 0x103), vertices);
            ImmutableArray<Vector3D> bitangents = V3(One(chunk, 0x195), vertices);
            ImmutableArray<Dl1SourceMeshVector2> uvs = V2(One(chunk, 0x120), vertices);
            Chunk indexChunk = One(chunk, 0x140); if (indexChunk.PayloadLength != checked(indices * 2)) throw new InvalidDataException("The source MSH index stream size is invalid.");
            var indexValues = ImmutableArray.CreateBuilder<uint>(indices);
            for (int i = 0; i < indices; i++)
            {
                if ((i & 1023) == 0) _token.ThrowIfCancellationRequested();
                ushort value = U16(indexChunk.PayloadOffset + i * 2);
                if (value >= vertices) throw new InvalidDataException("A source MSH index is outside its LOD vertex range.");
                indexValues.Add(value);
            }
            ImmutableArray<uint> indexArray = indexValues.MoveToImmutable();
            Chunk subsetChunk = One(chunk, 0x151); var subsetValues = ReadSubsets(subsetChunk, subsets, indices, materialCount, nodeCount);
            ImmutableArray<Dl1SourceMeshSkinVertex> skin = ReadSkin(chunk.Children.SingleOrDefault(c => c.Id is 0x130 or 0x131), vertices, nodeCount);
            ValidateSkinPalettes(skin, indexArray, subsetValues);
            ImmutableArray<Dl1SourceMeshMorphTarget> targets = ReadMorphs(chunk.Children.SingleOrDefault(c => c.Id == 0x104), morphs, vertices);
            return new(vertices, indices, subsets, morphs, _bytes.AsSpan(format.PayloadOffset, format.PayloadLength).ToArray().ToImmutableArray(), positions, normals, tangents, bitangents, uvs, indexArray, subsetValues, skin, targets);
        }

        private ImmutableArray<Dl1SourceMeshSubset> ReadSubsets(Chunk chunk, int count, int indexCount, int materialCount, int nodeCount)
        {
            if (chunk.Children.Count != 0)
                throw new InvalidDataException("Source MSH subset chunk contains children.");
            int offset = chunk.PayloadOffset;
            var result = ImmutableArray.CreateBuilder<Dl1SourceMeshSubset>(count);
            for (int i = 0; i < count; i++)
            {
                if ((i & 255) == 0) _token.ThrowIfCancellationRequested();
                if (offset + 12 > chunk.End)
                    throw new InvalidDataException("The source MSH subset table is truncated.");
                int material = U16(offset);
                int first = U32(offset + 2);
                int length = U32(offset + 6);
                int paletteCount = U16(offset + 10);
                int bytes = checked(12 + paletteCount * 2);
                if (material >= materialCount || (long)first + length > indexCount || paletteCount > MaximumPaletteEntries || offset + bytes > chunk.End)
                    throw new InvalidDataException("A source MSH subset is invalid.");
                var palette = ImmutableArray.CreateBuilder<ushort>(paletteCount);
                for (int p = 0; p < paletteCount; p++)
                {
                    ushort value = U16(offset + 12 + p * 2);
                    if (value >= nodeCount)
                        throw new InvalidDataException("A source MSH palette index is outside the physical-node range.");
                    palette.Add(value);
                }
                result.Add(new(material, first, length, palette.MoveToImmutable()));
                offset += bytes;
            }
            if (offset != chunk.End)
                throw new InvalidDataException("The source MSH subset table has trailing bytes.");
            return result.MoveToImmutable();
        }

        private ImmutableArray<Dl1SourceMeshSkinVertex> ReadSkin(Chunk? chunk, int vertices, int nodeCount)
        {
            if (chunk is null) return [];
            if (chunk.Children.Count != 0)
                throw new InvalidDataException("Source MSH skin chunk contains children.");
            if (chunk.PayloadLength == 0)
                throw new InvalidDataException("Source MSH skin chunk is empty.");
            if (chunk.Id == 0x130)
            {
                if (chunk.PayloadLength != checked(vertices * 12))
                    throw new InvalidDataException("The source MSH full skin stream has an invalid size.");
                var rows = ImmutableArray.CreateBuilder<Dl1SourceMeshSkinVertex>(vertices);
                for (int i = 0; i < vertices; i++)
                {
                    if ((i & 1023) == 0) _token.ThrowIfCancellationRequested();
                    int offset = chunk.PayloadOffset + i * 12;
                    var ids = ImmutableArray.CreateBuilder<byte>(4);
                    var weights = ImmutableArray.CreateBuilder<short>(4);
                    for (int j = 0; j < 4; j++)
                    {
                        byte id = _bytes[offset + j];
                        if (id >= nodeCount) throw new InvalidDataException("A source MSH skin bone index is invalid.");
                        ids.Add(id);
                        short weight = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(offset + 4 + j * 2, 2));
                        if (weight < 0) throw new InvalidDataException("A source MSH skin weight is negative.");
                        weights.Add(weight);
                    }
                    if (weights.Sum(static x => (int)x) != 32767)
                        throw new InvalidDataException("A source MSH skin row does not sum to 32767.");
                    rows.Add(new(ids.MoveToImmutable(), weights.MoveToImmutable()));
                }
                return rows.MoveToImmutable();
            }
            int influenceCount = _bytes[chunk.PayloadOffset];
            int rowSize = checked(influenceCount + (influenceCount > 1 ? influenceCount * 2 : 0));
            if (influenceCount is < 1 or > 3 || chunk.PayloadLength != checked(1 + vertices * rowSize))
                throw new InvalidDataException("The source MSH compact skin stream is invalid.");
            var compact = ImmutableArray.CreateBuilder<Dl1SourceMeshSkinVertex>(vertices);
            int cursor = chunk.PayloadOffset + 1;
            for (int i = 0; i < vertices; i++)
            {
                if ((i & 1023) == 0) _token.ThrowIfCancellationRequested();
                var ids = ImmutableArray.CreateBuilder<byte>(influenceCount);
                for (int j = 0; j < influenceCount; j++)
                {
                    byte id = _bytes[cursor++];
                    if (id >= nodeCount) throw new InvalidDataException("A source MSH skin bone index is invalid.");
                    ids.Add(id);
                }
                var weights = ImmutableArray.CreateBuilder<short>(influenceCount);
                if (influenceCount == 1)
                    weights.Add(32767);
                else
                    for (int j = 0; j < influenceCount; j++)
                    {
                        short weight = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(cursor, 2));
                        cursor += 2;
                        if (weight < 0) throw new InvalidDataException("A source MSH skin weight is negative.");
                        weights.Add(weight);
                    }
                if (weights.Sum(static x => (int)x) != 32767)
                    throw new InvalidDataException("A source MSH skin row does not sum to 32767.");
                compact.Add(new(ids.MoveToImmutable(), weights.MoveToImmutable()));
            }
            return compact.MoveToImmutable();
        }

        private static void Reserve(ref int aggregate, int amount, int limit, string label)
        {
            if (amount < 0 || aggregate > limit - amount)
                throw new InvalidDataException($"The source MSH aggregate {label} count exceeds its bound.");
            aggregate += amount;
        }

        private static void ValidateSkinPalettes(
            ImmutableArray<Dl1SourceMeshSkinVertex> skin,
            ImmutableArray<uint> indices,
            ImmutableArray<Dl1SourceMeshSubset> subsets)
        {
            if (skin.IsEmpty) return;
            var referenced = new bool[skin.Length];
            foreach (Dl1SourceMeshSubset subset in subsets)
            {
                int end = checked(subset.FirstIndex + subset.IndexCount);
                for (int i = subset.FirstIndex; i < end; i++)
                {
                    int vertex = checked((int)indices[i]);
                    referenced[vertex] = true;
                    foreach (byte paletteIndex in skin[vertex].PaletteIndices)
                        if (paletteIndex >= subset.Palette.Length)
                            throw new InvalidDataException("A source MSH skin palette-local index is invalid for a referenced subset.");
                }
            }
            if (referenced.Any(value => !value))
                throw new InvalidDataException("A source MSH skinned vertex is not referenced by any subset.");
        }

        private ImmutableArray<Dl1SourceMeshMorphTarget> ReadMorphs(Chunk? chunk, int count, int vertices)
        {
            if (count == 0) { if (chunk is not null) throw new InvalidDataException("A source MSH morph chunk is unexpected."); return []; }
            if (chunk is null || chunk.Children.Count != 0 || chunk.PayloadLength != checked(count * (64 + vertices * 12)))
                throw new InvalidDataException("The source MSH morph stream is invalid.");
            var result = ImmutableArray.CreateBuilder<Dl1SourceMeshMorphTarget>(count);
            int offset = chunk.PayloadOffset;
            for (int i = 0; i < count; i++)
            {
                _token.ThrowIfCancellationRequested();
                string name = ReadFixedName(offset);
                offset += 64;
                var deltas = ImmutableArray.CreateBuilder<Vector3D>(vertices);
                for (int v = 0; v < vertices; v++)
                {
                    if ((v & 1023) == 0) _token.ThrowIfCancellationRequested();
                    deltas.Add(ReadVector(offset));
                    offset += 12;
                }
                result.Add(new(name, deltas.MoveToImmutable()));
            }
            return result.MoveToImmutable();
        }

        private ImmutableArray<Vector3D> V3(Chunk c, int count) { if (c.PayloadLength != checked(count*12)) throw new InvalidDataException("A source MSH vector stream has an invalid size."); var a=ImmutableArray.CreateBuilder<Vector3D>(count);for(int i=0;i<count;i++){if((i&1023)==0)_token.ThrowIfCancellationRequested();a.Add(ReadVector(c.PayloadOffset+i*12));}return a.MoveToImmutable(); }
        private ImmutableArray<Dl1SourceMeshVector2> V2(Chunk c, int count) { if(c.PayloadLength!=checked(count*8))throw new InvalidDataException("A source MSH UV stream has an invalid size.");var a=ImmutableArray.CreateBuilder<Dl1SourceMeshVector2>(count);for(int i=0;i<count;i++){if((i&1023)==0)_token.ThrowIfCancellationRequested();float x=F32(c.PayloadOffset+i*8),y=F32(c.PayloadOffset+i*8+4);if(!float.IsFinite(x)||!float.IsFinite(y))throw new InvalidDataException("A source MSH UV is non-finite.");a.Add(new(x,y));}return a.MoveToImmutable(); }
        private Vector3D ReadVector(int o){double x=F32(o),y=F32(o+4),z=F32(o+8);if(!double.IsFinite(x)||!double.IsFinite(y)||!double.IsFinite(z))throw new InvalidDataException("A source MSH vector is non-finite.");return new(x,y,z);}
        private Vector3D ReadVectorCheckedBounds(int o) => ReadVector(o);
        private Vector3D ReadVectorCheckedHalfExtents(int o){Vector3D value=ReadVector(o);if(value.X<0||value.Y<0||value.Z<0)throw new InvalidDataException("A source MSH bound half extent is negative.");return value;}
        private ImmutableArray<float> ReadFloats(int o,int count){var a=ImmutableArray.CreateBuilder<float>(count);for(int i=0;i<count;i++){float v=F32(o+i*4);if(!float.IsFinite(v))throw new InvalidDataException("A source MSH matrix contains a non-finite value.");a.Add(v);}return a.MoveToImmutable();}
        private string ReadFixedName(int o){ReadOnlySpan<byte> field=_bytes.AsSpan(o,64);int nul=field.IndexOf((byte)0);if(nul<1)throw new InvalidDataException("A source MSH name is empty or unterminated.");for(int i=nul+1;i<field.Length;i++)if(field[i]!=0)throw new InvalidDataException("A source MSH name has nonzero bytes after its terminator.");try{return new UTF8Encoding(false,true).GetString(field[..nul]);}catch(DecoderFallbackException e){throw new InvalidDataException("A source MSH name is not strict UTF-8.",e);}}
        private static Chunk One(Chunk parent,uint id){Chunk[] a=parent.Children.Where(c=>c.Id==id).ToArray();if(a.Length!=1)throw new InvalidDataException($"Source MSH requires exactly one chunk 0x{id:X}.");if(id is 0x160 or 0x101 or 0x102 or 0x103 or 0x195 or 0x120 or 0x130 or 0x131 or 0x140 or 0x151 or 0x104){if(a[0].Children.Count!=0)throw new InvalidDataException($"Source MSH leaf chunk 0x{id:X} contains children.");}return a[0];}
    }

    private sealed record Chunk(uint Id,int PayloadOffset,int PayloadLength,int End,List<Chunk> Children);
}
