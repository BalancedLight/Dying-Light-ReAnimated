using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Fbx;

namespace ReAnimated.Tests;

public sealed class FbxBinaryReaderTests
{
    [Fact]
    public void ReadsBinary7400NodeAndCompressedArray()
    {
        var stringProperty = PropertyString("Model::Bip01");
        var arrayProperty = PropertyFloatArray([1f, 2.5f, -3f, 4f], compressed: true);
        var node = Node7400("Synthetic", [stringProperty, arrayProperty]);
        var bytes = Document7400([node]);

        var document = FbxBinaryReader.Read(bytes);

        var parsed = Assert.Single(document.Nodes);
        Assert.Equal((uint)7400, document.Version);
        Assert.Equal("Synthetic", parsed.Name);
        Assert.Equal("Model::Bip01", parsed.Properties[0].Get<string>());
        Assert.True(
            ImmutableArray.Create(1f, 2.5f, -3f, 4f).AsSpan().SequenceEqual(
                parsed.Properties[1].Get<ImmutableArray<float>>().AsSpan()));
        Assert.Equal("Bip01", FbxBinaryDocument.CleanObjectName("Model::Bip01\0ignored"));
    }

    [Fact]
    public void PreservesLegacyReaderOverloadAndRecordDeconstructionShape()
    {
        byte[] bytes = Document7400(
            [Node7400("Synthetic", [PropertyString("Value")])]);

        FbxBinaryDocument document = FbxBinaryReader.Read(bytes, null);
        (uint version, ImmutableArray<FbxNode> nodes) = document;
        FbxNode node = Assert.Single(nodes);
        (
            string name,
            ImmutableArray<FbxProperty> properties,
            ImmutableArray<FbxNode> children,
            long startOffset,
            long endOffset) = node;

        Assert.Equal((uint)7400, version);
        Assert.Equal("Synthetic", name);
        Assert.Single(properties);
        Assert.Empty(children);
        Assert.True(endOffset > startOffset);
        Assert.Equal(FbxReadPurpose.CompleteDocument, document.ReadPurpose);
        Assert.False(node.ChildPayloadSkipped);
    }

    [Fact]
    public void RejectsAsciiAndTruncatedInput()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => FbxBinaryReader.Read("Kaydara FBX ASCII"u8));

        Assert.Contains("binary FBX", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsArrayExpansionPastLimit()
    {
        var property = PropertyFloatArray([1f, 2f, 3f, 4f], compressed: false);
        var bytes = Document7400([Node7400("Values", [property])]);

        var error = Assert.Throws<InvalidDataException>(() =>
            FbxBinaryReader.Read(bytes, new FbxReadLimits
            {
                MaximumArrayBytes = 8,
            }));

        Assert.Contains("expands", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAggregateDecodedAllocationPastLimit()
    {
        var first = PropertyFloatArray(
            Enumerable.Range(0, 32)
                .Select(static value => (float)value)
                .ToArray(),
            compressed: false);
        var second = PropertyFloatArray(
            Enumerable.Range(32, 32)
                .Select(static value => (float)value)
                .ToArray(),
            compressed: false);
        var bytes = Document7400(
            [Node7400("Values", [first, second])]);

        InvalidDataException error =
            Assert.Throws<InvalidDataException>(() =>
                FbxBinaryReader.Read(
                    bytes,
                    new FbxReadLimits
                    {
                        MaximumDecodedAllocationBytes =
                            500,
                    }));

        Assert.Contains(
            "aggregate decoded-allocation budget",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HonorsCancellationBeforeBinaryParsing()
    {
        var bytes = Document7400(
            [Node7400("Synthetic", [PropertyString("Value")])]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            FbxBinaryReader.Read(
                bytes,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void AnimationPurposeSkipsGeometryPayloadButKeepsObjectInventory()
    {
        byte[] oversizedVertices = PropertyFloatArray(
            Enumerable.Range(0, 96)
                .Select(static value => (float)value)
                .ToArray(),
            compressed: false);
        byte[] bytes = Document7400Tree(
        [
            TreeNode(
                "Objects",
                [],
                TreeNode(
                    "Geometry",
                    [
                        PropertyInt64(10),
                        PropertyString("Geometry::DisplayMesh"),
                        PropertyString("Mesh"),
                    ],
                    TreeNode(
                        "Vertices",
                        [oversizedVertices])),
                TreeNode(
                    "Deformer",
                    [
                        PropertyInt64(11),
                        PropertyString("SubDeformer::SkinCluster"),
                        PropertyString("Cluster"),
                    ],
                    TreeNode(
                        "Weights",
                        [oversizedVertices])),
                TreeNode(
                    "Deformer",
                    [
                        PropertyInt64(12),
                        PropertyString("SubDeformer::Smile"),
                        PropertyString("BlendShapeChannel"),
                    ],
                    TreeNode(
                        "DeformPercent",
                        [PropertyInt64(25)])),
                TreeNode(
                    "Model",
                    [
                        PropertyInt64(1),
                        PropertyString("Model::Bip01"),
                        PropertyString("LimbNode"),
                    ])),
        ]);
        var limits = new FbxReadLimits
        {
            MaximumArrayBytes = 64,
        };

        Assert.Throws<InvalidDataException>(
            () => FbxBinaryReader.Read(bytes, limits));

        FbxBinaryDocument animationDocument = FbxBinaryReader.ReadWithOptions(
            bytes,
            FbxReadOptions.Animation,
            limits);

        Assert.Equal(
            FbxReadPurpose.Animation,
            animationDocument.ReadPurpose);
        FbxNode objects = Assert.Single(animationDocument.Nodes);
        FbxNode geometry = Assert.Single(objects.FindChildren("Geometry"));
        Assert.True(geometry.ChildPayloadSkipped);
        Assert.Equal(2, animationDocument.SkippedObjectPayloads.Length);
        Assert.Contains(
            geometry,
            animationDocument.SkippedObjectPayloads);
        Assert.Empty(geometry.Children);
        Assert.Equal(10L, geometry.Properties[0].Get<long>());
        Assert.Equal(
            "Geometry::DisplayMesh",
            geometry.Properties[1].Get<string>());
        FbxNode model = Assert.Single(objects.FindChildren("Model"));
        Assert.False(model.ChildPayloadSkipped);
        Assert.Equal(
            "Model::Bip01",
            model.Properties[1].Get<string>());
        FbxNode cluster = Assert.Single(
            objects.FindChildren("Deformer"),
            static node =>
                node.Properties[2].Get<string>() ==
                "Cluster");
        Assert.True(cluster.ChildPayloadSkipped);
        Assert.Empty(cluster.Children);
        FbxNode blendShapeChannel = Assert.Single(
            objects.FindChildren("Deformer"),
            static node =>
                node.Properties[2].Get<string>() ==
                "BlendShapeChannel");
        Assert.False(blendShapeChannel.ChildPayloadSkipped);
        Assert.NotEmpty(blendShapeChannel.Children);
    }

    [Fact]
    public void AnimationPurposeRejectsSkippedNodeEndOffsetThatSwallowsSibling()
    {
        byte[] bytes = Document7400Tree(
        [
            TreeNode(
                "Objects",
                [],
                TreeNode(
                    "Geometry",
                    [
                        PropertyInt64(10),
                        PropertyString("Geometry::DisplayMesh"),
                        PropertyString("Mesh"),
                    ],
                    TreeNode(
                        "Vertices",
                        [PropertyFloatArray([0f, 1f, 2f], compressed: false)])),
                TreeNode(
                    "Model",
                    [
                        PropertyInt64(1),
                        PropertyString("Model::Bip01"),
                        PropertyString("LimbNode"),
                    ])),
        ]);
        int geometryName = bytes.AsSpan().IndexOf("Geometry"u8);
        int modelName = bytes.AsSpan().IndexOf("Model"u8);
        Assert.True(geometryName >= 13);
        Assert.True(modelName >= 13);
        int geometryStart = geometryName - 13;
        int modelStart = modelName - 13;
        uint modelEnd = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(modelStart));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(geometryStart),
            modelEnd);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxBinaryReader.ReadWithOptions(
                bytes,
                FbxReadOptions.Animation));

        Assert.Contains(
            "terminates its child list",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnimationPurposeRejectsSkippedChildListWithoutTerminator()
    {
        byte[] bytes = Document7400Tree(
        [
            TreeNode(
                "Objects",
                [],
                TreeNode(
                    "Geometry",
                    [
                        PropertyInt64(10),
                        PropertyString("Geometry::DisplayMesh"),
                        PropertyString("Mesh"),
                    ],
                    TreeNode(
                        "Vertices",
                        [PropertyFloatArray([0f, 1f, 2f], compressed: false)]))),
        ]);
        int geometryName = bytes.AsSpan().IndexOf("Geometry"u8);
        Assert.True(geometryName >= 13);
        int geometryStart = geometryName - 13;
        uint geometryEnd = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(geometryStart));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(geometryStart),
            checked(geometryEnd - 13));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxBinaryReader.ReadWithOptions(
                bytes,
                FbxReadOptions.Animation));

        Assert.Contains(
            "no terminal null record",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnimationDecoderIgnoresUnrequestedMalformedMeshArrays()
    {
        byte[] oversizedVertices = PropertyFloatArray(
            Enumerable.Range(0, 96)
                .Select(static value => (float)value)
                .ToArray(),
            compressed: false);
        byte[] bytes = Document7400Tree(
        [
            TreeNode(
                "Objects",
                [],
                TreeNode(
                    "Geometry",
                    [
                        PropertyInt64(10),
                        PropertyString("Geometry::DisplayMesh"),
                        PropertyString("Mesh"),
                    ],
                    TreeNode(
                        "Vertices",
                        [oversizedVertices])),
                TreeNode(
                    "Deformer",
                    [
                        PropertyInt64(11),
                        PropertyString("SubDeformer::SkinCluster"),
                        PropertyString("Cluster"),
                    ],
                    TreeNode(
                        "Weights",
                        [oversizedVertices])),
                TreeNode(
                    "Model",
                    [
                        PropertyInt64(1),
                        PropertyString("Model::Bip01"),
                        PropertyString("LimbNode"),
                    ]),
                TreeNode(
                    "AnimationStack",
                    [
                        PropertyInt64(40),
                        PropertyString("AnimStack::Take"),
                        PropertyString(string.Empty),
                    ]),
                TreeNode(
                    "AnimationLayer",
                    [
                        PropertyInt64(100),
                        PropertyString("AnimLayer::Base"),
                        PropertyString(string.Empty),
                    ])),
            TreeNode(
                "Connections",
                [],
                TreeNode(
                    "C",
                    [
                        PropertyString("OO"),
                        PropertyInt64(100),
                        PropertyInt64(40),
                    ])),
        ]);
        var decoder = new FbxAnimationDecoder();

        FbxCoreAnimationImportResult result = decoder.Decode(
            bytes,
            limits: new FbxReadLimits
            {
                MaximumArrayBytes = 64,
            });

        Assert.Equal("Take", result.Clip.Name);
        Assert.Equal(1, result.Clip.FrameCount);
        Assert.Equal("Bip01", Assert.Single(result.Rig.Bones).Name);
        FbxNode geometry = Assert.Single(
            result.SkippedGeometryPayloads);
        Assert.True(geometry.ChildPayloadSkipped);
        Assert.Equal(2, result.SkippedModelDomainPayloads.Length);
        FbxAnimationImportNotice notice =
            Assert.Single(result.DomainNotices);
        Assert.Equal(
            "fbx_model_domains_excluded_from_animation_import",
            notice.Code);
        Assert.Equal(2, notice.AffectedObjectCount);
        Assert.Contains(
            "whole-document inspection",
            notice.Detail,
            StringComparison.Ordinal);
    }

    private static byte[] Document7400(IReadOnlyList<byte[]> nodes)
    {
        using var stream = new MemoryStream();
        stream.Write("Kaydara FBX Binary  \0\u001a\0"u8);
        WriteUInt32(stream, 7400);
        foreach (var node in nodes)
        {
            stream.Write(node);
        }

        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static byte[] Document7400Tree(
        IReadOnlyList<FbxTreeNode> nodes)
    {
        using var stream = new MemoryStream();
        stream.Write("Kaydara FBX Binary  \0\u001a\0"u8);
        WriteUInt32(stream, 7400);
        foreach (FbxTreeNode node in nodes)
        {
            WriteTreeNode7400(stream, node);
        }

        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static void WriteTreeNode7400(
        MemoryStream stream,
        FbxTreeNode node)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(node.Name);
        byte[] propertyBytes = node.Properties
            .SelectMany(static value => value)
            .ToArray();
        long nodeStart = stream.Position;
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)node.Properties.Count));
        WriteUInt32(stream, checked((uint)propertyBytes.Length));
        stream.WriteByte(checked((byte)nameBytes.Length));
        stream.Write(nameBytes);
        stream.Write(propertyBytes);
        foreach (FbxTreeNode child in node.Children)
        {
            WriteTreeNode7400(stream, child);
        }

        stream.Write(new byte[13]);
        long nodeEnd = stream.Position;
        stream.Position = nodeStart;
        WriteUInt32(stream, checked((uint)nodeEnd));
        stream.Position = nodeEnd;
    }

    private static FbxTreeNode TreeNode(
        string name,
        IReadOnlyList<byte[]> properties,
        params FbxTreeNode[] children) =>
        new(name, properties, children);

    private static byte[] Node7400(string name, IReadOnlyList<byte[]> properties)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var propertyBytes = properties.SelectMany(static value => value).ToArray();
        const int documentHeaderLength = 27;
        var nodeLength = 13 + nameBytes.Length + propertyBytes.Length + 13;
        var endOffset = documentHeaderLength + nodeLength;

        using var stream = new MemoryStream();
        WriteUInt32(stream, checked((uint)endOffset));
        WriteUInt32(stream, checked((uint)properties.Count));
        WriteUInt32(stream, checked((uint)propertyBytes.Length));
        stream.WriteByte(checked((byte)nameBytes.Length));
        stream.Write(nameBytes);
        stream.Write(propertyBytes);
        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static byte[] PropertyInt64(long value)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'L');
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
        return stream.ToArray();
    }

    private static byte[] PropertyString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'S');
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
        return stream.ToArray();
    }

    private static byte[] PropertyFloatArray(float[] values, bool compressed)
    {
        var raw = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                raw.AsSpan(index * 4),
                BitConverter.SingleToInt32Bits(values[index]));
        }

        byte[] stored;
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            stored = output.ToArray();
        }
        else
        {
            stored = raw;
        }

        using var stream = new MemoryStream();
        stream.WriteByte((byte)'f');
        WriteUInt32(stream, checked((uint)values.Length));
        WriteUInt32(stream, compressed ? 1u : 0u);
        WriteUInt32(stream, checked((uint)stored.Length));
        stream.Write(stored);
        return stream.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record FbxTreeNode(
        string Name,
        IReadOnlyList<byte[]> Properties,
        IReadOnlyList<FbxTreeNode> Children);
}
