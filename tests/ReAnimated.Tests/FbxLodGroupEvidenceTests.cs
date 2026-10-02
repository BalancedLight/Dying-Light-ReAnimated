using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using Xunit;

namespace ReAnimated.Tests;

public sealed class FbxLodGroupEvidenceTests
{
    [Fact]
    public void ReadsExplicitOrderedTwoLevelGeometryOwnership()
    {
        FbxSemanticScene scene = ParseFixture(
            includeSecondGeometry: true,
            duplicateGroupAttachment: false);

        FbxLodGroupEvidence group = Assert.Single(FbxLodGroupEvidenceReader.Read(scene));
        Assert.Equal("ship_lod_group", group.GroupName);
        Assert.Equal(10.0, group.RawThresholdProperties[4].Value);
        Assert.Collection(
            group.Levels,
            first =>
            {
                Assert.Equal(0, first.LevelIndex);
                Assert.Equal("ship_high", first.ModelName);
                Assert.Equal("ship_high_geo", Assert.Single(first.Geometries).GeometryName);
            },
            second =>
            {
                Assert.Equal(1, second.LevelIndex);
                Assert.Equal("ship_low", second.ModelName);
                Assert.Equal("ship_low_geo", Assert.Single(second.Geometries).GeometryName);
            });
    }

    [Fact]
    public void RejectsAmbiguousLodGroupOwnership()
    {
        FbxSemanticScene scene = ParseFixture(includeSecondGeometry: true, duplicateGroupAttachment: true);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => FbxLodGroupEvidenceReader.Read(scene));
        Assert.Contains("exactly one Model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesConnectionOrderInsteadOfObjectIdOrderAndIgnoresAttachments()
    {
        FbxSemanticScene scene = ParseFixture(reversedIds: true, includeAttachments: true);
        FbxLodGroupEvidence group = Assert.Single(FbxLodGroupEvidenceReader.Read(scene));
        Assert.Equal(new long[] { 300, 100 }, group.Levels.Select(level => level.ModelId));
    }

    [Fact]
    public void RejectsLevelWithoutExplicitGeometry()
    {
        FbxSemanticScene scene = ParseFixture(missingGeometry: true);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => FbxLodGroupEvidenceReader.Read(scene));
        Assert.Contains("no explicit geometry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsGeometryOwnedByTwoGroups()
    {
        FbxSemanticScene scene = ParseFixture(secondGroup: true);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => FbxLodGroupEvidenceReader.Read(scene));
        Assert.Contains("owned by more than one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainsMalformedThresholdPropertiesAsUnverifiedRawEvidence()
    {
        FbxSemanticScene scene = ParseFixture(malformedThreshold: true);
        FbxLodGroupEvidence group = Assert.Single(FbxLodGroupEvidenceReader.Read(scene));
        Assert.Equal("not-a-number", group.RawThresholdProperties[4].Value);
    }

    private static FbxSemanticScene ParseFixture(
        bool includeSecondGeometry = true,
        bool duplicateGroupAttachment = false,
        bool reversedIds = false,
        bool includeAttachments = false,
        bool missingGeometry = false,
        bool secondGroup = false,
        bool malformedThreshold = false)
    {
        long highId = reversedIds ? 300 : 2;
        long lowId = reversedIds ? 100 : 3;
        var objects = new List<FbxNode>
        {
            Object("Model", 1, "ship_lod_group", "Null"),
            Object("Model", highId, "ship_high", "Mesh"),
            Object("Model", lowId, "ship_low", "Mesh"),
            Object("NodeAttribute", 4, "LODGroup", "LodGroup", Properties70(malformedThreshold ? "not-a-number" : 10.0)),
            Object("Geometry", 20, "ship_high_geo", "Mesh"),
        };
        if (includeSecondGeometry)
            objects.Add(Object("Geometry", 21, "ship_low_geo", "Mesh"));
        if (includeAttachments)
        {
            objects.Add(Object("Material", 30, "ship_material", "Phong"));
            objects.Add(Object("NodeAttribute", 31, "mesh_attr", "Null"));
        }
        if (secondGroup)
        {
            objects.Add(Object("Model", 40, "second_lod_group", "Null"));
            objects.Add(Object("NodeAttribute", 41, "second_attr", "LodGroup"));
            objects.Add(Object("Model", 42, "second_level", "Mesh"));
        }

        var connections = new List<FbxNode>
        {
            Connection("OO", 4, 1),
            Connection("OO", highId, 1),
            Connection("OO", lowId, 1),
            Connection("OO", 20, highId),
        };
        if (includeSecondGeometry && !missingGeometry)
            connections.Add(Connection("OO", 21, lowId));
        if (includeAttachments)
        {
            connections.Add(Connection("OO", 30, highId));
            connections.Add(Connection("OO", 31, lowId));
        }
        if (duplicateGroupAttachment)
            connections.Add(Connection("OO", 4, 2));
        if (secondGroup)
        {
            connections.Add(Connection("OO", 41, 40));
            connections.Add(Connection("OO", 42, 40));
            connections.Add(Connection("OO", 20, 42));
        }

        var top = ImmutableArray.Create(
            new FbxNode("Objects", [], objects.ToImmutableArray(), 0, 0),
            new FbxNode("Connections", [], connections.ToImmutableArray(), 0, 0));
        return FbxSemanticScene.Parse(new FbxBinaryDocument(7, top));
    }

    private static FbxNode Object(string kind, long id, string name, string subtype, FbxNode? properties = null) =>
        new(kind,
            [new('L', id), new('S', name), new('S', subtype)],
            properties is null ? [] : [properties],
            0,
            0);

    private static FbxNode Properties70(object threshold) =>
        new("Properties70", [],
            [new FbxNode("P", [new('S', "Thresholds"), new('S', "double"), new('S', "Number"), new('S', ""), new('D', threshold)], [], 0, 0)],
            0,
            0);

    private static FbxNode Connection(string kind, long child, long parent) =>
        new("C", [new('S', kind), new('L', child), new('L', parent)], [], 0, 0);
}
