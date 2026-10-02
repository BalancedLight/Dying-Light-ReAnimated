using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using Xunit;

namespace ReAnimated.Tests;

/// <summary>
/// Binary-reader integration coverage for the retained FBX object/connection
/// source. The fixture deliberately contains only generic FBX objects and
/// ordered graph links; it does not claim writer or runtime LOD support.
/// </summary>
public sealed class FbxLodGroupBinaryImportIntegrationTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ImportAndPackageRoundTripRetainExplicitLodGeometryIdsAndPositions()
    {
        byte[] source = BlenderFbxStrictValidationTests.CreateValidModelFixture();
        FbxBinaryDocument document = FbxBinaryReader.Read(source);
        byte[] mutated = BlenderFbxStrictValidationTests.Serialize(MutateForTwoLevels(document));

        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(mutated, "generic-lod-source.fbx");
        FbxLodGroupEvidence group = Assert.Single(imported.SourceLodGroups);
        Assert.Equal(new long[] { 300, 100 }, group.Levels.Select(level => level.ModelId));
        Assert.Contains(imported.Surfaces, surface => surface.SourceGeometry?.Id == "fbx:300:10");
        Assert.Contains(imported.Surfaces, surface => surface.SourceGeometry?.Id == "fbx:100:700");

        FbxModelSurface high = Assert.Single(imported.Surfaces.Where(surface => surface.SourceGeometry?.Id == "fbx:300:10"));
        FbxModelSurface low = Assert.Single(imported.Surfaces.Where(surface => surface.SourceGeometry?.Id == "fbx:100:700"));
        Assert.NotEqual(high.Vertices[0].Position, low.Vertices[0].Position);
        Assert.True(high.IsSkinned);
        Assert.True(low.IsSkinned);
        Assert.Single(FbxModelLodLayout.Create(imported));

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string packagePath = Path.Combine(directory, "generic-lod-roundtrip.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(imported.Package, packagePath);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
                CustomModelPackageSerializer.Load(packagePath));
            Assert.Single(reopened.SourceLodGroups);
            Assert.Contains(reopened.Surfaces, surface => surface.SourceGeometry?.Id == "fbx:300:10");
            Assert.Contains(reopened.Surfaces, surface => surface.SourceGeometry?.Id == "fbx:100:700");
            Assert.Equal(low.Vertices[0].Position, reopened.Surfaces.Single(surface => surface.SourceGeometry?.Id == "fbx:100:700").Vertices[0].Position);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void BinaryReaderPreservesOrderedLevelsAndDistinctGeometryPayloadIdentities()
    {
        FbxSemanticScene scene = Parse(BinaryFixture(duplicateGeometryOwnership: false, nonMeshGeometry: false));
        FbxLodGroupEvidence group = Assert.Single(FbxLodGroupEvidenceReader.Read(scene));
        Assert.Equal(new long[] { 300, 100 }, group.Levels.Select(level => level.ModelId));
        Assert.Equal(new long[] { 700, 701 }, group.Levels.SelectMany(level => level.Geometries).Select(geometry => geometry.GeometryObjectId));
        Assert.Equal(new long[] { 300, 100 }, group.Levels.SelectMany(level => level.Geometries).Select(geometry => geometry.OwnerModelId));
        Assert.NotEqual(
            scene.Document.FindTopLevel("Objects")!.Children.Single(node => node.Properties.Length > 0 && node.Properties[0].Value.Equals(700L)).Children,
            scene.Document.FindTopLevel("Objects")!.Children.Single(node => node.Properties.Length > 0 && node.Properties[0].Value.Equals(701L)).Children);
    }

    [Fact]
    public void BinaryReaderRejectsNonMeshGeometryShape()
    {
        FbxSemanticScene scene = Parse(BinaryFixture(duplicateGeometryOwnership: false, nonMeshGeometry: true));
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => FbxLodGroupEvidenceReader.Read(scene));
        Assert.Contains("no explicit geometry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryReaderRejectsCrossGroupGeometryOwnership()
    {
        FbxSemanticScene scene = Parse(BinaryFixture(duplicateGeometryOwnership: true, nonMeshGeometry: false));
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => FbxLodGroupEvidenceReader.Read(scene));
        Assert.Contains("owned by more than one", exception.Message, StringComparison.Ordinal);
    }

    private static FbxSemanticScene Parse(byte[] bytes) =>
        FbxSemanticScene.Parse(FbxBinaryReader.Read(bytes));

    private static FbxBinaryDocument MutateForTwoLevels(FbxBinaryDocument document)
    {
        FbxNode objects = document.FindTopLevel("Objects") ?? throw new InvalidDataException("Fixture has no Objects node.");
        FbxNode connections = document.FindTopLevel("Connections") ?? throw new InvalidDataException("Fixture has no Connections node.");
        FbxNode geometry = objects.Children.Single(node => node.Name == "Geometry" && (long)node.Properties[0].Value == 10L);
        FbxNode clone = ScaleGeometry(geometry, 700, 0.8);
        FbxNode mesh = objects.Children.Single(node => node.Name == "Model" && (long)node.Properties[0].Value == 4L);
        FbxNode highModel = mesh with { Properties = mesh.Properties.SetItem(0, new('L', 300L)).SetItem(1, new('S', "Model::GenericHigh")) };
        FbxNode lowModel = mesh with { Properties = mesh.Properties.SetItem(0, new('L', 100L)).SetItem(1, new('S', "Model::GenericLow")) };
        FbxNode CloneObject(long sourceId, long cloneId) => objects.Children.Single(node => node.Properties[0].Value.Equals(sourceId)) with
        {
            Properties = objects.Children.Single(node => node.Properties[0].Value.Equals(sourceId)).Properties.SetItem(0, new('L', cloneId)),
        };
        ImmutableArray<FbxNode> addedObjects = objects.Children
            .Add(CloneObject(30, 730)).Add(CloneObject(31, 731)).Add(CloneObject(32, 732))
            .Add(Model(900, "generic_lod_group", "Null"))
            .Add(highModel)
            .Add(lowModel)
            .Add(new FbxNode("NodeAttribute", [new('L', 901L), new('S', "generic_lod_attribute"), new('S', "LodGroup")], [], 0, 0))
            .Add(clone);
        ImmutableArray<FbxNode> links = connections.Children.Where(link =>
                !(link.Properties.Length >= 3 && link.Properties[0].Value.Equals("OO") && link.Properties[1].Value.Equals(10L) && link.Properties[2].Value.Equals(4L)))
            .ToImmutableArray()
            .Add(Connection("OO", 40, 300)).Add(Connection("OO", 40, 100))
            .Add(Connection("OO", 730, 700)).Add(Connection("OO", 731, 730)).Add(Connection("OO", 732, 730))
            .Add(Connection("OO", 2, 731)).Add(Connection("OO", 3, 732))
            .Add(Connection("OO", 901, 900))
            .Add(Connection("OO", 300, 900))
            .Add(Connection("OO", 100, 900))
            .Add(Connection("OO", 10, 300))
            .Add(Connection("OO", 700, 100));
        return document with
        {
            Nodes = document.Nodes
                .Replace(objects, objects with { Children = addedObjects })
                .Replace(connections, connections with { Children = links }),
        };
    }

    private static FbxNode ScaleGeometry(FbxNode node, long objectId, double scale)
    {
        FbxNode positions = node.FindChild("Vertices")!;
        var values = Assert.IsType<ImmutableArray<double>>(positions.Properties[0].Value);
        FbxNode scaled = positions with { Properties = [new('d', values.Select(value => value * scale + 0.2).ToImmutableArray())] };
        return node with
        {
            Properties = node.Properties.SetItem(0, new('L', objectId)).SetItem(1, new('S', "Geometry::GenericLow")),
            Children = node.Children.Replace(positions, scaled),
        };
    }

    private static FbxNode Model(long id, string name, string subtype) =>
        new("Model", [new('L', id), new('S', name), new('S', subtype)], [], 0, 0);

    private static FbxNode Connection(string kind, long child, long parent) =>
        new("C", [new('S', kind), new('L', child), new('L', parent)], [], 0, 0);

    private static byte[] BinaryFixture(bool duplicateGeometryOwnership, bool nonMeshGeometry)
    {
        var objects = new List<FbxNode>
        {
            Object("Model", 10, "lod_group", "Null"),
            Object("Model", 300, "level_high", "Mesh"),
            Object("Model", 100, "level_low", "Mesh"),
            Object("NodeAttribute", 20, "lod_attribute", "LodGroup"),
            Geometry(700, "high_payload", "Mesh"),
            Geometry(701, "low_payload", nonMeshGeometry ? "Nurbs" : "Mesh"),
        };
        if (duplicateGeometryOwnership)
        {
            objects.Add(Object("Model", 30, "second_group", "Null"));
            objects.Add(Object("Model", 31, "second_level", "Mesh"));
            objects.Add(Object("NodeAttribute", 32, "second_attribute", "LodGroup"));
        }

        var connections = new List<FbxNode>
        {
            Connection(20, 10),
            Connection(300, 10),
            Connection(100, 10),
            Connection(700, 300),
            Connection(701, 100),
        };
        if (duplicateGeometryOwnership)
        {
            connections.Add(Connection(32, 30));
            connections.Add(Connection(31, 30));
            connections.Add(Connection(700, 31));
        }

        return WriteDocument(
            new FbxNode("Objects", [], objects.ToImmutableArray(), 0, 0),
            new FbxNode("Connections", [], connections.ToImmutableArray(), 0, 0));
    }

    private static FbxNode Geometry(long id, string name, string subtype) =>
        new("Geometry", [new('L', id), new('S', name), new('S', subtype)],
            [new FbxNode("Payload", [new('I', subtype == "Mesh" ? 3 : 0)], [], 0, 0)], 0, 0);

    private static FbxNode Object(string kind, long id, string name, string subtype) =>
        new(kind, [new('L', id), new('S', name), new('S', subtype)], [], 0, 0);

    private static FbxNode Connection(long child, long parent) =>
        new("C", [new('S', "OO"), new('L', child), new('L', parent)], [], 0, 0);

    private static byte[] WriteDocument(params FbxNode[] nodes)
    {
        using var stream = new MemoryStream();
        stream.Write("Kaydara FBX Binary  \0\u001a\0"u8);
        WriteUInt32(stream, 7400);
        foreach (FbxNode node in nodes)
            WriteNode(stream, node);
        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static void WriteNode(MemoryStream stream, FbxNode node)
    {
        byte[] name = Encoding.UTF8.GetBytes(node.Name);
        byte[] properties = node.Properties.SelectMany(WriteProperty).ToArray();
        long start = stream.Position;
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)node.Properties.Length));
        WriteUInt32(stream, checked((uint)properties.Length));
        stream.WriteByte(checked((byte)name.Length));
        stream.Write(name);
        stream.Write(properties);
        foreach (FbxNode child in node.Children)
            WriteNode(stream, child);
        stream.Write(new byte[13]);
        long end = stream.Position;
        stream.Position = start;
        WriteUInt32(stream, checked((uint)end));
        stream.Position = end;
    }

    private static byte[] WriteProperty(FbxProperty property)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(checked((byte)property.TypeCode));
        switch (property.Value)
        {
            case long value: WriteInt64(stream, value); break;
            case int value: WriteUInt32(stream, unchecked((uint)value)); break;
            case string value:
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                WriteUInt32(stream, checked((uint)bytes.Length));
                stream.Write(bytes);
                break;
            default: throw new ArgumentException("Unsupported generic FBX fixture property payload.");
        }
        return stream.ToArray();
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
