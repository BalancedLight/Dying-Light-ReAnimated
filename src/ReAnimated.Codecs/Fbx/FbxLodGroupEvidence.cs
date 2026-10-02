using System.Collections.Immutable;

namespace ReAnimated.Codecs.Fbx;

/// <summary>
/// Explicit FBX scene-graph evidence for one LOD group. The parser preserves
/// source connection order and does not infer levels from names or geometry
/// suffixes. Threshold properties remain raw source evidence; their serialized
/// units and runtime meaning are intentionally unverified.
/// </summary>
public sealed record FbxLodGroupEvidence(
    long AttributeObjectId,
    long GroupModelId,
    string GroupName,
    ImmutableArray<FbxProperty> RawThresholdProperties,
    ImmutableArray<FbxLodLevelEvidence> Levels);

public sealed record FbxLodLevelEvidence(
    int LevelIndex,
    long ModelId,
    string ModelName,
    ImmutableArray<FbxLodGeometryEvidence> Geometries);

public sealed record FbxLodGeometryEvidence(
    long GeometryObjectId,
    string GeometryName,
    long OwnerModelId);

/// <summary>
/// Reads the generic FBX object/connection graph retained by
/// <see cref="FbxSemanticScene"/>. Autodesk's FBX SDK defines LOD groups as
/// an FbxLODGroup node attribute whose ordered child nodes are the levels;
/// this reader accepts only that explicit graph shape.
/// </summary>
public static class FbxLodGroupEvidenceReader
{
    private const string NodeAttributeObjectName = "NodeAttribute";
    private const string LodGroupSubtype = "LodGroup";
    private const string ModelObjectName = "Model";
    private const string GeometryObjectName = "Geometry";
    private const string ObjectObjectConnection = "OO";

    public static ImmutableArray<FbxLodGroupEvidence> Read(FbxSemanticScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var groups = ImmutableArray.CreateBuilder<FbxLodGroupEvidence>();
        var groupIds = new HashSet<long>();
        var groupModelIds = new HashSet<long>();
        var levelIds = new HashSet<long>();
        var geometryIds = new HashSet<long>();
        foreach (FbxNode node in scene.Document.FindTopLevel("Objects")?.Children ?? [])
        {
            if (!IsLodGroupAttribute(node, out long attributeId, out _))
                continue;
            if (!groupIds.Add(attributeId))
                throw new InvalidDataException($"LODGroup attribute {attributeId} is duplicated.");

            ImmutableArray<FbxConnection> attached = scene.Connections
                .Where(connection => connection.Kind == ObjectObjectConnection &&
                    connection.ChildId == attributeId)
                .ToImmutableArray();
            if (attached.Length != 1 || !scene.Models.TryGetValue(attached[0].ParentId, out FbxModelObject? groupModel))
            {
                throw new InvalidDataException(
                    $"LODGroup attribute {attributeId} must attach to exactly one Model node.");
            }
            if (!groupModelIds.Add(groupModel.ObjectId))
                throw new InvalidDataException($"LODGroup model '{groupModel.Name}' is owned by more than one LODGroup attribute.");

            ImmutableArray<FbxConnection> levelLinks = scene.GetChildren(groupModel.ObjectId)
                .Where(connection => connection.Kind == ObjectObjectConnection &&
                    scene.Models.ContainsKey(connection.ChildId))
                .ToImmutableArray();
            if (levelLinks.IsDefaultOrEmpty)
                throw new InvalidDataException($"LODGroup model '{groupModel.Name}' has no ordered level children.");

            var levels = ImmutableArray.CreateBuilder<FbxLodLevelEvidence>(levelLinks.Length);
            for (int levelIndex = 0; levelIndex < levelLinks.Length; levelIndex++)
            {
                long modelId = levelLinks[levelIndex].ChildId;
                if (!levelIds.Add(modelId) || !scene.Models.TryGetValue(modelId, out FbxModelObject? levelModel))
                    throw new InvalidDataException($"LODGroup '{groupModel.Name}' has an ambiguous or non-Model level link at index {levelIndex}.");


                ImmutableArray<FbxConnection> geometryLinks = scene.GetChildren(modelId)
                    .Where(connection => connection.Kind == ObjectObjectConnection &&
                        TryGetObject(scene, connection.ChildId, GeometryObjectName, "Mesh", out _))
                    .ToImmutableArray();
                if (geometryLinks.IsDefaultOrEmpty)
                    throw new InvalidDataException($"LODGroup level '{levelModel.Name}' has no explicit geometry child.");

                var geometries = ImmutableArray.CreateBuilder<FbxLodGeometryEvidence>(geometryLinks.Length);
                foreach (FbxConnection link in geometryLinks)
                {
                    if (!geometryIds.Add(link.ChildId))
                        throw new InvalidDataException($"Geometry {link.ChildId} is owned by more than one LOD group or level.");
                    if (!TryGetObject(scene, link.ChildId, GeometryObjectName, "Mesh", out FbxNode? geometry) || geometry is null)
                    {
                        throw new InvalidDataException(
                            $"LODGroup level '{levelModel.Name}' has an ambiguous or non-Geometry child link.");
                    }

                    string name = geometry.Properties.Length > 1
                        ? FbxBinaryDocument.CleanObjectName(FbxSemanticValues.ConvertString(geometry.Properties[1].Value))
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidDataException($"LODGroup level '{levelModel.Name}' has a geometry child without a stable name.");
                    geometries.Add(new(link.ChildId, name, modelId));
                }

                levels.Add(new(levelIndex, modelId, levelModel.Name, geometries.ToImmutable()));
            }

            groups.Add(new(attributeId, groupModel.ObjectId, groupModel.Name, ReadRawThresholdProperties(node), levels.ToImmutable()));
        }

        return groups.ToImmutable();
    }

    private static bool IsLodGroupAttribute(FbxNode node, out long objectId, out string name)
    {
        objectId = 0;
        name = string.Empty;
        if (!string.Equals(node.Name, NodeAttributeObjectName, StringComparison.Ordinal) ||
            node.Properties.Length < 3 ||
            !FbxSemanticValues.TryConvertInt64(node.Properties[0].Value, out objectId))
            return false;
        name = FbxBinaryDocument.CleanObjectName(FbxSemanticValues.ConvertString(node.Properties[1].Value));
        string subtype = FbxSemanticValues.ConvertString(node.Properties[2].Value);
        return string.Equals(subtype, LodGroupSubtype, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetObject(FbxSemanticScene scene, long objectId, string expectedName, string? expectedSubtype, out FbxNode? node)
    {
        node = scene.Document.FindTopLevel("Objects")?.Children.FirstOrDefault(candidate =>
            candidate.Properties.Length > 0 &&
            FbxSemanticValues.TryConvertInt64(candidate.Properties[0].Value, out long id) &&
            id == objectId && string.Equals(candidate.Name, expectedName, StringComparison.Ordinal) &&
            (expectedSubtype is null || candidate.Properties.Length > 2 &&
                string.Equals(FbxSemanticValues.ConvertString(candidate.Properties[2].Value), expectedSubtype, StringComparison.OrdinalIgnoreCase)));
        return node is not null;
    }

    private static ImmutableArray<FbxProperty> ReadRawThresholdProperties(FbxNode node)
    {
        FbxNode? properties = node.FindChild("Properties70");
        FbxNode? threshold = properties?.FindChildren("P")
            .FirstOrDefault(property => property.Properties.Length > 0 &&
                string.Equals(FbxSemanticValues.ConvertString(property.Properties[0].Value), "Thresholds", StringComparison.OrdinalIgnoreCase));
        if (threshold is null)
            return [];
        return threshold.Properties;
    }
}
