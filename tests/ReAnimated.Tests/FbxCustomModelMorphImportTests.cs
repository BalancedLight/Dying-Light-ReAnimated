using System.Buffers.Binary;
using System.Text;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxCustomModelMorphImportTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelMorph")]
    public void ImportsOneShapeChannelAndRemapsDeltasToExpandedVertices()
    {
        byte[] fbx = CreateMorphFbx(["generic_smile"]);

        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            fbx,
            "generic-morph.fbx",
            new FbxModelAuthoringImportOptions
            {
                RigMode = CustomModelRigMode.StaticProp,
            });

        CustomModelMorphChannel channel = Assert.Single(
            imported.Package.Document.MorphChannels);
        Assert.Equal("generic_smile", channel.Name);
        Assert.Equal(Dl1NameHash.Compute("generic_smile"), channel.DescriptorHash);
        Assert.NotEqual(
            CustomModelDocument.EmptyMorphSignature,
            imported.Package.Document.MorphSignature);
        FbxModelMorphTarget target = Assert.Single(
            Assert.Single(imported.Surfaces).MorphTargets);
        Assert.Equal(3, target.PositionDeltas.Length);
        Assert.Equal(0.005, target.PositionDeltas[0].X, precision: 9);
        Assert.Equal(0.0, target.PositionDeltas[1].Length, precision: 9);
        Assert.Equal(-0.0025, target.PositionDeltas[2].Y, precision: 9);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public void RejectsProgressiveOrMultipleShapesWithBakeGuidance()
    {
        byte[] fbx = CreateMorphFbx(
            ["generic_smile"],
            addSecondShapeToFirstChannel: true);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxModelAuthoringImporter.Import(
                fbx,
                "generic-progressive.fbx",
                new FbxModelAuthoringImportOptions
                {
                    RigMode = CustomModelRigMode.StaticProp,
                }));

        Assert.Contains("progressive or multiple", error.Message, StringComparison.Ordinal);
        Assert.Contains("bake", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public void EnforcesAffectedControlPointBudget()
    {
        byte[] fbx = CreateMorphFbx(["generic_smile"]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxModelAuthoringImporter.Import(
                fbx,
                "generic-bounded.fbx",
                new FbxModelAuthoringImportOptions
                {
                    RigMode = CustomModelRigMode.StaticProp,
                    MaximumMorphAffectedControlPoints = 1,
                }));

        Assert.Contains("bounded allocation budget", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public void RejectsDl1DescriptorCollisions()
    {
        const string first = "generic_o779z2dmjq";
        const string second = "generic_2hlxq5ztcy";
        Assert.NotEqual(first, second);
        Assert.Equal(Dl1NameHash.Compute(first), Dl1NameHash.Compute(second));
        byte[] fbx = CreateMorphFbx([first, second]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FbxModelAuthoringImporter.Import(
                fbx,
                "generic-collision.fbx",
                new FbxModelAuthoringImportOptions
                {
                    RigMode = CustomModelRigMode.StaticProp,
                }));

        Assert.Contains("collide", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public void ReimportPreviewSeparatesStableRigFromChangedMorphContract()
    {
        byte[] originalBytes = CreateMorphFbx(["generic_smile"]);
        FbxModelAuthoringImportResult original =
            FbxModelAuthoringImporter.Import(
                originalBytes,
                "generic-original.fbx",
                new FbxModelAuthoringImportOptions
                {
                    RigMode = CustomModelRigMode.StaticProp,
                });

        CustomModelReimportPreview unchanged =
            FbxModelAuthoringImporter.PreviewReimport(
                original.Package,
                originalBytes,
                "generic-unchanged.fbx");
        Assert.Equal(CustomModelReimportContractChange.None, unchanged.Changes);
        Assert.True(unchanged.CanPreserveVariantReviews);

        byte[] changedBytes = CreateMorphFbx(
            ["generic_smile"],
            firstShapeDeltaX: 0.75);
        CustomModelReimportPreview changed =
            FbxModelAuthoringImporter.PreviewReimport(
                original.Package,
                changedBytes,
                "generic-changed.fbx");
        Assert.False(changed.BoneAndHelperMappingsBecomeStale);
        Assert.True(changed.FacialMappingsBecomeStale);
        Assert.False(changed.CanPreserveVariantReviews);
        Assert.Equal(
            changed.ExistingSourceRigSignature,
            changed.ReplacementSourceRigSignature);
        Assert.NotEqual(
            changed.ExistingMorphSignature,
            changed.ReplacementMorphSignature);
    }

    private static byte[] CreateMorphFbx(
        IReadOnlyList<string> channelNames,
        bool addSecondShapeToFirstChannel = false,
        double firstShapeDeltaX = 0.5)
    {
        const long baseGeometryId = 10;
        const long blendShapeId = 40;
        var objects = new List<FbxTreeNode>
        {
            Node(
                "Geometry",
                [
                    ScalarInt64(baseGeometryId),
                    ScalarString("Geometry::GenericMesh"),
                    ScalarString("Mesh"),
                ],
                Node(
                    "Vertices",
                    [DoubleArray([
                        0.0, 0.0, 0.0,
                        1.0, 0.0, 0.0,
                        0.0, 1.0, 0.0,
                    ])]),
                Node(
                    "PolygonVertexIndex",
                    [Int64Array([0, 1, -3])])),
            Node(
                "Deformer",
                [
                    ScalarInt64(blendShapeId),
                    ScalarString("Deformer::GenericBlendShape"),
                    ScalarString("BlendShape"),
                ]),
        };
        var connections = new List<FbxTreeNode>
        {
            Connection(blendShapeId, baseGeometryId),
        };

        for (int index = 0; index < channelNames.Count; index++)
        {
            long channelId = 20 + index;
            long shapeId = 30 + (index * 2);
            string channelName = channelNames[index];
            objects.Add(Node(
                "Deformer",
                [
                    ScalarInt64(channelId),
                    ScalarString($"SubDeformer::{channelName}"),
                    ScalarString("BlendShapeChannel"),
                ],
                Node("DeformPercent", [ScalarDouble(0.0)]),
                Node("FullWeights", [DoubleArray([100.0])])));
            objects.Add(CreateShape(
                shapeId,
                channelName,
                index == 0 ? firstShapeDeltaX : 0.5));
            connections.Add(Connection(shapeId, channelId));
            connections.Add(Connection(channelId, blendShapeId));

            if (index == 0 && addSecondShapeToFirstChannel)
            {
                long progressiveShapeId = shapeId + 1;
                objects.Add(CreateShape(
                    progressiveShapeId,
                    $"{channelName}_progressive",
                    firstShapeDeltaX));
                connections.Add(Connection(progressiveShapeId, channelId));
            }
        }

        return Document(
        [
            Node("Objects", [], [.. objects]),
            Node("Connections", [], [.. connections]),
        ]);
    }

    private static FbxTreeNode CreateShape(
        long shapeId,
        string name,
        double firstDeltaX) =>
        Node(
            "Geometry",
            [
                ScalarInt64(shapeId),
                ScalarString($"Geometry::{name}"),
                ScalarString("Shape"),
            ],
            Node("Indexes", [Int64Array([0, 2])]),
            Node("Vertices", [DoubleArray([
                firstDeltaX, 0.0, 0.0,
                0.0, -0.25, 0.0,
            ])]));

    private static FbxTreeNode Connection(long childId, long parentId) =>
        Node(
            "C",
            [
                ScalarString("OO"),
                ScalarInt64(childId),
                ScalarInt64(parentId),
            ]);

    private static byte[] Document(IReadOnlyList<FbxTreeNode> nodes)
    {
        using var stream = new MemoryStream();
        stream.Write("Kaydara FBX Binary  \0\u001a\0"u8);
        WriteUInt32(stream, 7400);
        foreach (FbxTreeNode node in nodes)
        {
            WriteNode(stream, node);
        }

        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static void WriteNode(MemoryStream stream, FbxTreeNode node)
    {
        byte[] name = Encoding.UTF8.GetBytes(node.Name);
        byte[] properties = node.Properties.SelectMany(static value => value).ToArray();
        long start = stream.Position;
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)node.Properties.Count));
        WriteUInt32(stream, checked((uint)properties.Length));
        stream.WriteByte(checked((byte)name.Length));
        stream.Write(name);
        stream.Write(properties);
        foreach (FbxTreeNode child in node.Children)
        {
            WriteNode(stream, child);
        }

        stream.Write(new byte[13]);
        long end = stream.Position;
        stream.Position = start;
        WriteUInt32(stream, checked((uint)end));
        stream.Position = end;
    }

    private static FbxTreeNode Node(
        string name,
        IReadOnlyList<byte[]> properties,
        params FbxTreeNode[] children) =>
        new(name, properties, children);

    private static byte[] ScalarInt64(long value)
    {
        byte[] result = new byte[9];
        result[0] = (byte)'L';
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(1), value);
        return result;
    }

    private static byte[] ScalarDouble(double value)
    {
        byte[] result = new byte[9];
        result[0] = (byte)'D';
        BinaryPrimitives.WriteInt64LittleEndian(
            result.AsSpan(1),
            BitConverter.DoubleToInt64Bits(value));
        return result;
    }

    private static byte[] ScalarString(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream();
        stream.WriteByte((byte)'S');
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
        return stream.ToArray();
    }

    private static byte[] Int64Array(IReadOnlyList<long> values)
    {
        byte[] raw = new byte[checked(values.Count * sizeof(long))];
        for (int index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                raw.AsSpan(index * sizeof(long)),
                values[index]);
        }

        return ArrayProperty('l', values.Count, raw);
    }

    private static byte[] DoubleArray(IReadOnlyList<double> values)
    {
        byte[] raw = new byte[checked(values.Count * sizeof(double))];
        for (int index = 0; index < values.Count; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                raw.AsSpan(index * sizeof(double)),
                BitConverter.DoubleToInt64Bits(values[index]));
        }

        return ArrayProperty('d', values.Count, raw);
    }

    private static byte[] ArrayProperty(
        char type,
        int count,
        byte[] raw)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(checked((byte)type));
        WriteUInt32(stream, checked((uint)count));
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)raw.Length));
        stream.Write(raw);
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
