using System.Collections.Immutable;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxBindPoseInspection(
    ImmutableDictionary<long, ImmutableArray<double>>
        NodeMatrices);

public sealed record FbxAnimationStackInspection(
    long ObjectId,
    string Name,
    long StartTick,
    long StopTick,
    int LayerCount,
    int CurveCount,
    int BoneCurveCount,
    ImmutableHashSet<long> CurveModelIds,
    long? MinimumKeyTick,
    long? MaximumKeyTick);

public sealed record FbxClusterInspection(
    long ObjectId,
    long BoneModelId,
    int InfluenceCount,
    double MinimumWeight,
    double MaximumWeight);

public sealed record FbxSkinInspection(
    long ObjectId,
    ImmutableArray<FbxClusterInspection> Clusters,
    int CoveredVertexCount);

public sealed record FbxExternalFileReferenceInspection(
    long ObjectId,
    string ObjectType,
    string PropertyName,
    string Value);

public sealed record FbxMeshGeometryInspection(
    long ObjectId,
    string Name,
    int VertexCount,
    int PolygonVertexIndexCount,
    int PolygonCount,
    int NormalVectorCount,
    int NormalIndexCount,
    int TextureCoordinateCount,
    int TextureCoordinateIndexCount,
    long? MeshModelId,
    string? MeshModelName,
    ImmutableArray<FbxSkinInspection> Skins,
    ImmutableHashSet<long> MaterialIds,
    ImmutableHashSet<long> TextureIds,
    ImmutableHashSet<long> VideoIds,
    ImmutableArray<FbxExternalFileReferenceInspection>
        ExternalFileReferences,
    ImmutableHashSet<string> ReferencedFileNames);

public sealed record FbxStrictExportInspection(
    ImmutableArray<string> AnimationStackNames,
    ImmutableDictionary<string, FbxAnimationStackInspection>
        AnimationStacks,
    ImmutableDictionary<string, long> LimbModelIds,
    ImmutableDictionary<string, long?> LimbParentModelIds,
    ImmutableArray<FbxBindPoseInspection> BindPoses,
    int MeshModelCount,
    int MeshGeometryCount,
    ImmutableHashSet<string> MeshModelNames,
    ImmutableHashSet<string> MeshGeometryNames,
    ImmutableDictionary<string, FbxMeshGeometryInspection>
        MeshGeometries,
    int TextureObjectCount,
    int VideoObjectCount,
    ImmutableArray<FbxExternalFileReferenceInspection>
        ExternalFileReferences,
    ImmutableHashSet<string> ReferencedFileNames);

/// <summary>
/// Strict, read-only inspection of binary FBX objects written by an external
/// exporter. This keeps raw FBX AST interpretation inside the codec boundary.
/// </summary>
public static class FbxStrictExportInspector
{
    public static async Task<FbxStrictExportInspection>
        InspectFileAsync(
            string path,
            FbxReadLimits? limits = null,
            CancellationToken cancellationToken = default)
    {
        FbxBinaryDocument document =
            await FbxBinaryReader.ReadFileAsync(
                path,
                limits,
                cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Inspect(
            document,
            cancellationToken);
    }

    public static FbxStrictExportInspection Inspect(
        FbxBinaryDocument document) =>
        Inspect(
            document,
            CancellationToken.None);

    public static FbxStrictExportInspection Inspect(
        FbxBinaryDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        FbxSemanticScene scene =
            FbxSemanticScene.Parse(document, cancellationToken);
        FbxNode objects = document.FindTopLevel("Objects")
            ?? throw new InvalidDataException(
                "FBX has no Objects section.");
        ImmutableDictionary<long, FbxNode> objectsById =
            ReadObjectsById(objects);
        ImmutableArray<FbxExternalFileReferenceInspection>
            externalFileReferences =
                CollectExternalFileReferences(
                    objects,
                    cancellationToken);
        ImmutableDictionary<string, long> limbIds =
            ReadUniqueLimbIds(scene);
        ImmutableArray<FbxBindPoseInspection> bindPoses =
            objects.FindChildren("Pose")
                .Where(IsBindPose)
                .Select(ReadBindPose)
                .ToImmutableArray();
        ImmutableDictionary<string, long?> limbParentIds =
            scene.Models.Values
                .Where(static model =>
                    model.IsLimb)
                .ToImmutableDictionary(
                    static model =>
                        model.Name,
                    model =>
                        scene.GetModelParentId(
                            model.ObjectId),
                    StringComparer.Ordinal);
        int meshModelCount = scene.Models.Values.Count(
            static model =>
                string.Equals(
                    model.Subtype,
                    "Mesh",
                    StringComparison.Ordinal));
        FbxNode[] geometryNodes = objects
            .FindChildren("Geometry")
            .Where(IsMeshGeometry)
            .ToArray();
        var geometryBuilder =
            ImmutableDictionary.CreateBuilder<
                string,
                FbxMeshGeometryInspection>(
                StringComparer.Ordinal);
        foreach (FbxNode geometry in geometryNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxMeshGeometryInspection inspection =
                InspectGeometry(
                    geometry,
                    scene,
                    objectsById,
                    externalFileReferences);
            if (!geometryBuilder.TryAdd(
                    inspection.Name,
                    inspection))
            {
                throw new InvalidDataException(
                    $"FBX repeats mesh Geometry name '{inspection.Name}'.");
            }
        }

        var stackBuilder =
            ImmutableDictionary.CreateBuilder<
                string,
                FbxAnimationStackInspection>(
                StringComparer.Ordinal);
        foreach (FbxAnimationStackInfo stack in
                 scene.AnimationStacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxAnimationStackInspection inspection =
                InspectAnimationStack(
                    stack,
                    scene);
            if (!stackBuilder.TryAdd(
                    inspection.Name,
                    inspection))
            {
                throw new InvalidDataException(
                    $"FBX repeats AnimationStack name '{inspection.Name}'.");
            }
        }

        ImmutableHashSet<string> meshModelNames =
            scene.Models.Values
                .Where(static model =>
                    string.Equals(
                        model.Subtype,
                        "Mesh",
                        StringComparison.Ordinal))
                .Select(static model =>
                    model.Name)
                .ToImmutableHashSet(
                    StringComparer.Ordinal);
        var referencedFiles =
            ImmutableHashSet.CreateBuilder<string>(
                StringComparer.OrdinalIgnoreCase);
        AddReferencedFileNames(
            externalFileReferences,
            referencedFiles);
        ImmutableDictionary<string, FbxMeshGeometryInspection>
            meshGeometries = geometryBuilder.ToImmutable();
        ImmutableDictionary<string, FbxAnimationStackInspection>
            animationStacks = stackBuilder.ToImmutable();
        return new FbxStrictExportInspection(
            scene.AnimationStacks
                .Select(static stack =>
                    stack.Name)
                .ToImmutableArray(),
            animationStacks,
            limbIds,
            limbParentIds,
            bindPoses,
            meshModelCount,
            geometryNodes.Length,
            meshModelNames,
            meshGeometries.Keys.ToImmutableHashSet(
                StringComparer.Ordinal),
            meshGeometries,
            objects.FindChildren("Texture").Count(),
            objects.FindChildren("Video").Count(),
            externalFileReferences,
            referencedFiles.ToImmutable());
    }

    private static ImmutableDictionary<long, FbxNode>
        ReadObjectsById(FbxNode objects)
    {
        var result =
            ImmutableDictionary.CreateBuilder<long, FbxNode>();
        foreach (FbxNode node in objects.Children)
        {
            if (node.Properties.IsEmpty ||
                !FbxSemanticValues.TryConvertInt64(
                    node.Properties[0].Value,
                    out long objectId))
            {
                continue;
            }

            if (!result.TryAdd(objectId, node))
            {
                throw new InvalidDataException(
                    $"FBX object id {objectId} is duplicated.");
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableDictionary<string, long>
        ReadUniqueLimbIds(FbxSemanticScene scene)
    {
        var result =
            ImmutableDictionary.CreateBuilder<string, long>(
                StringComparer.Ordinal);
        foreach (FbxModelObject model in scene.Models.Values
                     .Where(static model =>
                         model.IsLimb))
        {
            if (!result.TryAdd(
                    model.Name,
                    model.ObjectId))
            {
                throw new InvalidDataException(
                    $"FBX repeats LimbNode name '{model.Name}'.");
            }
        }

        return result.ToImmutable();
    }

    private static FbxAnimationStackInspection
        InspectAnimationStack(
            FbxAnimationStackInfo stack,
            FbxSemanticScene scene)
    {
        ImmutableArray<FbxAnimationCurveBinding> bindings =
            scene.ReadAnimationBindings(stack);
        FbxAnimationCurveBinding[] boneBindings = bindings
            .Where(binding =>
                scene.Models.TryGetValue(
                    binding.ModelId,
                    out FbxModelObject? model) &&
                model.IsLimb)
            .ToArray();
        long? minimum = bindings.IsEmpty
            ? null
            : bindings.Min(static binding =>
                binding.Curve.KeyTimes[0]);
        long? maximum = bindings.IsEmpty
            ? null
            : bindings.Max(static binding =>
                binding.Curve.KeyTimes[^1]);
        return new FbxAnimationStackInspection(
            stack.ObjectId,
            stack.Name,
            stack.StartTick,
            stack.StopTick,
            stack.LayerIds.Length,
            bindings.Length,
            boneBindings.Length,
            boneBindings
                .Select(static binding =>
                    binding.ModelId)
                .ToImmutableHashSet(),
            minimum,
            maximum);
    }

    private static FbxMeshGeometryInspection InspectGeometry(
        FbxNode geometry,
        FbxSemanticScene scene,
        ImmutableDictionary<long, FbxNode> objectsById,
        ImmutableArray<FbxExternalFileReferenceInspection>
            externalFileReferences)
    {
        long objectId = ReadObjectId(
            geometry,
            "Geometry");
        string name = ReadObjectName(
            geometry,
            "Geometry");
        ImmutableArray<double> vertices =
            FbxSemanticValues.ReadDoubleArray(
                geometry.FindChild("Vertices"),
                $"Geometry '{name}' Vertices");
        ValidateVectorArray(
            vertices,
            3,
            $"Geometry '{name}' Vertices",
            requireNonZeroVectors: false,
            allowEmpty: false);
        int vertexCount = vertices.Length / 3;
        ImmutableArray<long> polygonIndices =
            FbxSemanticValues.ReadInt64Array(
                geometry.FindChild("PolygonVertexIndex"),
                $"Geometry '{name}' PolygonVertexIndex");
        int polygonCount = ValidatePolygonIndices(
            polygonIndices,
            vertexCount,
            name);

        (int normalCount, int normalIndexCount) =
            InspectNormals(
                geometry,
                name);
        (int uvCount, int uvIndexCount) =
            InspectTextureCoordinates(
                geometry,
                name);
        (long? modelId, string? modelName) =
            ReadMeshModel(
                objectId,
                scene);
        ImmutableArray<FbxSkinInspection> skins =
            InspectSkins(
                objectId,
                vertexCount,
                scene,
                objectsById);
        (
            ImmutableHashSet<long> materialIds,
            ImmutableHashSet<long> textureIds,
            ImmutableHashSet<long> videoIds,
            ImmutableArray<FbxExternalFileReferenceInspection>
                materialFileReferences,
            ImmutableHashSet<string> fileNames) =
            InspectMaterialGraph(
                modelId,
                scene,
                objectsById,
                externalFileReferences);
        return new FbxMeshGeometryInspection(
            objectId,
            name,
            vertexCount,
            polygonIndices.Length,
            polygonCount,
            normalCount,
            normalIndexCount,
            uvCount,
            uvIndexCount,
            modelId,
            modelName,
            skins,
            materialIds,
            textureIds,
            videoIds,
            materialFileReferences,
            fileNames);
    }

    private static int ValidatePolygonIndices(
        ImmutableArray<long> indices,
        int vertexCount,
        string geometryName)
    {
        int polygonCount = 0;
        int polygonStart = 0;
        for (int index = 0;
             index < indices.Length;
             index++)
        {
            long raw = indices[index];
            long decoded = raw < 0
                ? ~raw
                : raw;
            if (decoded < 0 ||
                decoded >= vertexCount)
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' polygon vertex {decoded} is outside its {vertexCount:N0}-vertex buffer.");
            }

            if (raw >= 0)
            {
                continue;
            }

            if (index - polygonStart + 1 < 3)
            {
                throw new InvalidDataException(
                    $"Geometry '{geometryName}' contains a polygon with fewer than three vertices.");
            }

            polygonCount++;
            polygonStart = index + 1;
        }

        if (polygonStart != indices.Length)
        {
            throw new InvalidDataException(
                $"Geometry '{geometryName}' PolygonVertexIndex does not terminate its final polygon.");
        }

        return polygonCount;
    }

    private static (int VectorCount, int IndexCount)
        InspectNormals(
            FbxNode geometry,
            string geometryName)
    {
        int vectorCount = 0;
        int indexCount = 0;
        foreach (FbxNode layer in
                 geometry.FindChildren("LayerElementNormal"))
        {
            ImmutableArray<double> values =
                FbxSemanticValues.ReadDoubleArray(
                    layer.FindChild("Normals"),
                    $"Geometry '{geometryName}' Normals");
            ValidateVectorArray(
                values,
                3,
                $"Geometry '{geometryName}' Normals",
                requireNonZeroVectors: true,
                allowEmpty: false);
            vectorCount = checked(
                vectorCount +
                (values.Length / 3));
            ImmutableArray<long> indices =
                FbxSemanticValues.ReadInt64Array(
                    layer.FindChild("NormalsIndex"),
                    $"Geometry '{geometryName}' NormalsIndex");
            ValidateDirectIndices(
                indices,
                values.Length / 3,
                $"Geometry '{geometryName}' NormalsIndex");
            indexCount = checked(
                indexCount +
                indices.Length);
        }

        return (vectorCount, indexCount);
    }

    private static (int CoordinateCount, int IndexCount)
        InspectTextureCoordinates(
            FbxNode geometry,
            string geometryName)
    {
        int coordinateCount = 0;
        int indexCount = 0;
        foreach (FbxNode layer in
                 geometry.FindChildren("LayerElementUV"))
        {
            ImmutableArray<double> values =
                FbxSemanticValues.ReadDoubleArray(
                    layer.FindChild("UV"),
                    $"Geometry '{geometryName}' UV");
            ValidateVectorArray(
                values,
                2,
                $"Geometry '{geometryName}' UV",
                requireNonZeroVectors: false,
                allowEmpty: false);
            coordinateCount = checked(
                coordinateCount +
                (values.Length / 2));
            ImmutableArray<long> indices =
                FbxSemanticValues.ReadInt64Array(
                    layer.FindChild("UVIndex"),
                    $"Geometry '{geometryName}' UVIndex");
            ValidateDirectIndices(
                indices,
                values.Length / 2,
                $"Geometry '{geometryName}' UVIndex");
            indexCount = checked(
                indexCount +
                indices.Length);
        }

        return (coordinateCount, indexCount);
    }

    private static void ValidateVectorArray(
        ImmutableArray<double> values,
        int componentCount,
        string label,
        bool requireNonZeroVectors,
        bool allowEmpty)
    {
        if ((!allowEmpty && values.IsEmpty) ||
            values.Length % componentCount != 0 ||
            values.Any(static value =>
                !double.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"{label} must contain complete finite {componentCount}-component values.");
        }

        if (!requireNonZeroVectors)
        {
            return;
        }

        for (int offset = 0;
             offset < values.Length;
             offset += componentCount)
        {
            double magnitudeSquared = 0.0;
            for (int component = 0;
                 component < componentCount;
                 component++)
            {
                magnitudeSquared +=
                    values[offset + component] *
                    values[offset + component];
            }

            if (!double.IsFinite(magnitudeSquared) ||
                magnitudeSquared <= 1.0e-20)
            {
                throw new InvalidDataException(
                    $"{label} contains an empty vector.");
            }
        }
    }

    private static void ValidateDirectIndices(
        ImmutableArray<long> indices,
        int valueCount,
        string label)
    {
        foreach (long index in indices)
        {
            if (index < 0 ||
                index >= valueCount)
            {
                throw new InvalidDataException(
                    $"{label} contains index {index} outside its {valueCount:N0}-value table.");
            }
        }
    }

    private static (long? ModelId, string? ModelName)
        ReadMeshModel(
            long geometryId,
            FbxSemanticScene scene)
    {
        FbxModelObject[] models = scene.Connections
            .Where(connection =>
                string.Equals(
                    connection.Kind,
                    "OO",
                    StringComparison.Ordinal) &&
                connection.ChildId == geometryId &&
                scene.Models.TryGetValue(
                    connection.ParentId,
                    out FbxModelObject? model) &&
                string.Equals(
                    model.Subtype,
                    "Mesh",
                    StringComparison.Ordinal))
            .Select(connection =>
                scene.Models[connection.ParentId])
            .DistinctBy(static model =>
                model.ObjectId)
            .ToArray();
        return models.Length switch
        {
            0 => (null, null),
            1 => (
                models[0].ObjectId,
                models[0].Name),
            _ => throw new InvalidDataException(
                $"FBX Geometry {geometryId} is connected to multiple Mesh Models."),
        };
    }

    private static ImmutableArray<FbxSkinInspection>
        InspectSkins(
            long geometryId,
            int vertexCount,
            FbxSemanticScene scene,
            ImmutableDictionary<long, FbxNode> objectsById)
    {
        long[] skinIds = scene.Connections
            .Where(connection =>
                string.Equals(
                    connection.Kind,
                    "OO",
                    StringComparison.Ordinal) &&
                connection.ParentId == geometryId &&
                IsObjectSubtype(
                    objectsById,
                    connection.ChildId,
                    "Deformer",
                    "Skin"))
            .Select(static connection =>
                connection.ChildId)
            .Distinct()
            .ToArray();
        var result =
            ImmutableArray.CreateBuilder<FbxSkinInspection>(
                skinIds.Length);
        foreach (long skinId in skinIds)
        {
            long[] clusterIds = scene.Connections
                .Where(connection =>
                    string.Equals(
                        connection.Kind,
                        "OO",
                        StringComparison.Ordinal) &&
                    connection.ParentId == skinId &&
                    IsObjectSubtype(
                        objectsById,
                        connection.ChildId,
                        "Deformer",
                        "Cluster"))
                .Select(static connection =>
                    connection.ChildId)
                .Distinct()
                .ToArray();
            var clusters =
                ImmutableArray.CreateBuilder<
                    FbxClusterInspection>(
                    clusterIds.Length);
            var coveredVertices = new HashSet<long>();
            foreach (long clusterId in clusterIds)
            {
                FbxNode cluster = objectsById[clusterId];
                ImmutableArray<long> indices =
                    FbxSemanticValues.ReadInt64Array(
                        cluster.FindChild("Indexes"),
                        $"Cluster {clusterId} Indexes");
                ImmutableArray<double> weights =
                    FbxSemanticValues.ReadDoubleArray(
                        cluster.FindChild("Weights"),
                        $"Cluster {clusterId} Weights");
                if (indices.IsEmpty ||
                    indices.Length != weights.Length)
                {
                    throw new InvalidDataException(
                        $"FBX Cluster {clusterId} must contain equal non-empty Indexes and Weights arrays.");
                }

                var localIndices = new HashSet<long>();
                for (int influence = 0;
                     influence < indices.Length;
                     influence++)
                {
                    long vertexIndex = indices[influence];
                    double weight = weights[influence];
                    if (vertexIndex < 0 ||
                        vertexIndex >= vertexCount ||
                        !localIndices.Add(vertexIndex))
                    {
                        throw new InvalidDataException(
                            $"FBX Cluster {clusterId} contains an invalid or repeated vertex index {vertexIndex}.");
                    }

                    if (!double.IsFinite(weight) ||
                        weight <= 0.0 ||
                        weight > 1.000001)
                    {
                        throw new InvalidDataException(
                            $"FBX Cluster {clusterId} contains invalid weight {weight}.");
                    }

                    coveredVertices.Add(vertexIndex);
                }

                ReadFiniteMatrix(
                    cluster.FindChild("Transform"),
                    $"FBX Cluster {clusterId} Transform");
                ReadFiniteMatrix(
                    cluster.FindChild("TransformLink"),
                    $"FBX Cluster {clusterId} TransformLink");
                long[] boneIds = scene.Connections
                    .Where(connection =>
                        string.Equals(
                            connection.Kind,
                            "OO",
                            StringComparison.Ordinal) &&
                        connection.ParentId == clusterId &&
                        scene.Models.TryGetValue(
                            connection.ChildId,
                            out FbxModelObject? model) &&
                        model.IsLimb)
                    .Select(static connection =>
                        connection.ChildId)
                    .Distinct()
                    .ToArray();
                if (boneIds.Length != 1)
                {
                    throw new InvalidDataException(
                        $"FBX Cluster {clusterId} must connect to exactly one LimbNode.");
                }

                clusters.Add(
                    new FbxClusterInspection(
                        clusterId,
                        boneIds[0],
                        indices.Length,
                        weights.Min(),
                        weights.Max()));
            }

            result.Add(
                new FbxSkinInspection(
                    skinId,
                    clusters.ToImmutable(),
                    coveredVertices.Count));
        }

        return result.ToImmutable();
    }

    private static (
        ImmutableHashSet<long> MaterialIds,
        ImmutableHashSet<long> TextureIds,
        ImmutableHashSet<long> VideoIds,
        ImmutableArray<FbxExternalFileReferenceInspection>
            ExternalFileReferences,
        ImmutableHashSet<string> FileNames)
        InspectMaterialGraph(
            long? modelId,
            FbxSemanticScene scene,
            ImmutableDictionary<long, FbxNode> objectsById,
            ImmutableArray<FbxExternalFileReferenceInspection>
                externalFileReferences)
    {
        if (modelId is null)
        {
            return (
                ImmutableHashSet<long>.Empty,
                ImmutableHashSet<long>.Empty,
                ImmutableHashSet<long>.Empty,
                ImmutableArray<
                    FbxExternalFileReferenceInspection>.Empty,
                ImmutableHashSet.Create<string>(
                    StringComparer.OrdinalIgnoreCase));
        }

        ImmutableHashSet<long> materialIds = scene.Connections
            .Where(connection =>
                connection.ParentId == modelId.Value &&
                IsObjectName(
                    objectsById,
                    connection.ChildId,
                    "Material"))
            .Select(static connection =>
                connection.ChildId)
            .ToImmutableHashSet();
        ImmutableHashSet<long> textureIds = scene.Connections
            .Where(connection =>
                materialIds.Contains(
                    connection.ParentId) &&
                IsObjectName(
                    objectsById,
                    connection.ChildId,
                    "Texture"))
            .Select(static connection =>
                connection.ChildId)
            .ToImmutableHashSet();
        ImmutableHashSet<long> videoIds = scene.Connections
            .Where(connection =>
                textureIds.Contains(
                    connection.ParentId) &&
                IsObjectName(
                    objectsById,
                    connection.ChildId,
                    "Video"))
            .Select(static connection =>
                connection.ChildId)
            .ToImmutableHashSet();
        var fileNames =
            ImmutableHashSet.CreateBuilder<string>(
                StringComparer.OrdinalIgnoreCase);
        ImmutableHashSet<long> materialFileObjectIds =
            textureIds.Union(videoIds);
        ImmutableArray<FbxExternalFileReferenceInspection>
            materialFileReferences = externalFileReferences
                .Where(reference =>
                    materialFileObjectIds.Contains(
                        reference.ObjectId))
                .ToImmutableArray();
        AddReferencedFileNames(
            materialFileReferences,
            fileNames);

        return (
            materialIds,
            textureIds,
            videoIds,
            materialFileReferences,
            fileNames.ToImmutable());
    }

    private static bool IsObjectName(
        ImmutableDictionary<long, FbxNode> objectsById,
        long objectId,
        string name) =>
        objectsById.TryGetValue(
            objectId,
            out FbxNode? node) &&
        string.Equals(
            node.Name,
            name,
            StringComparison.Ordinal);

    private static bool IsObjectSubtype(
        ImmutableDictionary<long, FbxNode> objectsById,
        long objectId,
        string name,
        string subtype) =>
        objectsById.TryGetValue(
            objectId,
            out FbxNode? node) &&
        string.Equals(
            node.Name,
            name,
            StringComparison.Ordinal) &&
        node.Properties.Length >= 3 &&
        node.Properties[2].Value is string value &&
        string.Equals(
            value,
            subtype,
            StringComparison.Ordinal);

    private static bool IsMeshGeometry(FbxNode node) =>
        node.Properties.Length >= 3 &&
        node.Properties[2].Value is string subtype &&
        string.Equals(
            subtype,
            "Mesh",
            StringComparison.Ordinal);

    private static long ReadObjectId(
        FbxNode node,
        string type)
    {
        if (node.Properties.IsEmpty ||
            !FbxSemanticValues.TryConvertInt64(
                node.Properties[0].Value,
                out long objectId))
        {
            throw new InvalidDataException(
                $"FBX {type} has an invalid object id.");
        }

        return objectId;
    }

    private static string ReadObjectName(
        FbxNode node,
        string type)
    {
        if (node.Properties.Length < 2 ||
            node.Properties[1].Value is not string rawName)
        {
            throw new InvalidDataException(
                $"FBX {type} has no object name.");
        }

        string name =
            FbxBinaryDocument.CleanObjectName(
                rawName);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException(
                $"FBX {type} has an empty object name.");
        }

        return name;
    }

    private static bool IsBindPose(FbxNode pose)
    {
        bool propertyTyped = pose.Properties
            .Skip(1)
            .Any(property =>
                property.Value is string value &&
                string.Equals(
                    FbxBinaryDocument.CleanObjectName(value),
                    "BindPose",
                    StringComparison.Ordinal));
        string? type = pose.FindChild("Type")
            ?.FirstString();
        return propertyTyped ||
            string.Equals(
                type,
                "BindPose",
                StringComparison.Ordinal);
    }

    private static FbxBindPoseInspection ReadBindPose(
        FbxNode pose)
    {
        var matrices =
            ImmutableDictionary.CreateBuilder<
                long,
                ImmutableArray<double>>();
        foreach (FbxNode poseNode in
                 pose.FindChildren("PoseNode"))
        {
            FbxNode node = poseNode.FindChild("Node")
                ?? throw new InvalidDataException(
                    "FBX BindPose PoseNode has no Node id.");
            if (node.Properties.IsEmpty ||
                !TryConvertInt64(
                    node.Properties[0].Value,
                    out long id))
            {
                throw new InvalidDataException(
                    "FBX BindPose PoseNode has an invalid Node id.");
            }

            ImmutableArray<double> matrix =
                ReadFiniteMatrix(
                    poseNode.FindChild("Matrix"),
                    $"FBX BindPose PoseNode {id} Matrix");
            if (!matrices.TryAdd(id, matrix))
            {
                throw new InvalidDataException(
                    $"FBX BindPose repeats PoseNode id {id}.");
            }
        }

        if (matrices.Count == 0)
        {
            throw new InvalidDataException(
                "FBX BindPose contains no PoseNode matrices.");
        }

        return new FbxBindPoseInspection(
            matrices.ToImmutable());
    }

    private static ImmutableArray<double> ReadFiniteMatrix(
        FbxNode? node,
        string label)
    {
        if (node is null ||
            node.Properties.Length != 1)
        {
            throw new InvalidDataException(
                $"{label} is missing or malformed.");
        }

        ImmutableArray<double> values =
            node.Properties[0].Value switch
            {
                ImmutableArray<double> typed => typed,
                ImmutableArray<float> typed =>
                    typed.Select(static value =>
                            (double)value)
                        .ToImmutableArray(),
                _ => throw new InvalidDataException(
                    $"{label} has an unsupported value type."),
            };
        if (values.Length != 16 ||
            values.Any(static value =>
                !double.IsFinite(value)))
        {
            throw new InvalidDataException(
                $"{label} must contain 16 finite values.");
        }

        double determinant = Determinant(values);
        if (!double.IsFinite(determinant) ||
            Math.Abs(determinant) <= 1.0e-12)
        {
            throw new InvalidDataException(
                $"{label} is singular.");
        }

        return values;
    }

    private static double Determinant(
        ImmutableArray<double> value)
    {
        double a0 =
            value[0] * value[5] -
            value[1] * value[4];
        double a1 =
            value[0] * value[6] -
            value[2] * value[4];
        double a2 =
            value[0] * value[7] -
            value[3] * value[4];
        double a3 =
            value[1] * value[6] -
            value[2] * value[5];
        double a4 =
            value[1] * value[7] -
            value[3] * value[5];
        double a5 =
            value[2] * value[7] -
            value[3] * value[6];
        double b0 =
            value[8] * value[13] -
            value[9] * value[12];
        double b1 =
            value[8] * value[14] -
            value[10] * value[12];
        double b2 =
            value[8] * value[15] -
            value[11] * value[12];
        double b3 =
            value[9] * value[14] -
            value[10] * value[13];
        double b4 =
            value[9] * value[15] -
            value[11] * value[13];
        double b5 =
            value[10] * value[15] -
            value[11] * value[14];
        return
            a0 * b5 -
            a1 * b4 +
            a2 * b3 +
            a3 * b2 -
            a4 * b1 +
            a5 * b0;
    }

    private static ImmutableArray<
        FbxExternalFileReferenceInspection>
        CollectExternalFileReferences(
            FbxNode objects,
            CancellationToken cancellationToken)
    {
        const int maximumReferences = 65_536;
        var result = ImmutableArray.CreateBuilder<
            FbxExternalFileReferenceInspection>();
        foreach (FbxNode objectNode in objects.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    objectNode.Name,
                    "Texture",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    objectNode.Name,
                    "Video",
                    StringComparison.Ordinal))
            {
                continue;
            }

            long objectId = ReadObjectId(
                objectNode,
                objectNode.Name);
            var nodes = new Stack<FbxNode>();
            foreach (FbxNode child in objectNode.Children)
            {
                nodes.Push(child);
            }

            while (nodes.TryPop(out FbxNode? node))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsExternalFilePropertyName(
                        node.Name))
                {
                    foreach (FbxProperty property in
                             node.Properties)
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        if (property.Value is not string value ||
                            string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        if (result.Count >= maximumReferences)
                        {
                            throw new InvalidDataException(
                                $"FBX contains more than {maximumReferences:N0} Texture/Video external-file references.");
                        }

                        result.Add(
                            new FbxExternalFileReferenceInspection(
                                objectId,
                                objectNode.Name,
                                node.Name,
                                value));
                    }
                }

                foreach (FbxNode child in node.Children)
                {
                    nodes.Push(child);
                }
            }
        }

        return result.ToImmutable();
    }

    private static bool IsExternalFilePropertyName(
        string nodeName) =>
        string.Equals(
            nodeName,
            "FileName",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            nodeName,
            "RelativeFilename",
            StringComparison.OrdinalIgnoreCase);

    private static void AddReferencedFileNames(
        IEnumerable<FbxExternalFileReferenceInspection>
            references,
        ImmutableHashSet<string>.Builder result)
    {
        foreach (FbxExternalFileReferenceInspection reference in
                 references)
        {
            string fileName =
                GetPortableFileName(reference.Value);
            if (!string.IsNullOrWhiteSpace(fileName) &&
                Path.HasExtension(fileName))
            {
                result.Add(fileName);
            }
        }
    }

    private static string GetPortableFileName(
        string value)
    {
        int slash = value.LastIndexOf('/');
        int backslash = value.LastIndexOf('\\');
        int separator = Math.Max(
            slash,
            backslash);
        return separator < 0
            ? value
            : value[(separator + 1)..];
    }

    private static bool TryConvertInt64(
        object value,
        out long result)
    {
        switch (value)
        {
            case long typed:
                result = typed;
                return true;
            case int typed:
                result = typed;
                return true;
            case short typed:
                result = typed;
                return true;
            case byte typed:
                result = typed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
