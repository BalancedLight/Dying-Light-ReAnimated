using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;

namespace ReAnimated.Tests;

public sealed class BlenderFbxStrictValidationTests :
    IDisposable
{
    private const long FrameTick =
        FbxBinaryDocument.TicksPerSecond / 30;
    private const string TextureFileName =
        "DLR_BaseColor_0123456789abcdef.dds";
    private readonly string _temporaryDirectory =
        Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-FbxStrict-{Guid.NewGuid():N}");

    [Fact]
    public async Task
        AcceptsCompleteHierarchyMeshSkinMaterialAndAnimation()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.None);
        FbxStrictExportInspection inspection =
            await FbxStrictExportInspector
                .InspectFileAsync(path);
        var validator =
            new BlenderFbxOutputValidator();

        await validator.ValidateAsync(
            path,
            ExpectedBones(),
            ExpectedClips(),
            ExpectedMeshes(),
            ExpectedTextures(),
            CancellationToken.None);

        FbxMeshGeometryInspection geometry =
            inspection.MeshGeometries[
                "RetailMesh_Mesh"];
        Assert.Equal(3, geometry.VertexCount);
        Assert.Equal(3, geometry.PolygonVertexIndexCount);
        Assert.Equal(3, geometry.NormalVectorCount);
        Assert.Equal(3, geometry.TextureCoordinateCount);
        Assert.Equal(3, geometry.TextureCoordinateIndexCount);
        FbxSkinInspection skin =
            Assert.Single(geometry.Skins);
        Assert.Equal(3, skin.CoveredVertexCount);
        Assert.Equal(2, skin.Clusters.Length);
        Assert.Contains(
            TextureFileName,
            geometry.ReferencedFileNames);
        FbxExternalFileReferenceInspection reference =
            Assert.Single(
                geometry.ExternalFileReferences);
        Assert.Equal("Video", reference.ObjectType);
        Assert.Equal(
            "RelativeFilename",
            reference.PropertyName);
        Assert.Equal(TextureFileName, reference.Value);
        FbxAnimationStackInspection stack =
            inspection.AnimationStacks["Idle"];
        Assert.Equal(2, stack.BoneCurveCount);
        Assert.Equal(2 * FrameTick, stack.MaximumKeyTick);
    }

    [Fact]
    public async Task
        RejectsWrongNonRootHierarchyDespiteMatchingNamesAndBindPose()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.WrongChildParent);
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    ExpectedMeshes(),
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "wrong hierarchy parent",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsNamedGeometryWhoseTopologyDoesNotMatchTheJob()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.None);
        BlenderFbxJobMesh mesh =
            ExpectedMeshes()[0] with
            {
                VertexCount = 4,
            };
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    [mesh],
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "topology",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsNonFiniteGeometryNormalsInTheCodecBoundary()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.NonFiniteNormal);

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => FbxStrictExportInspector
                    .InspectFileAsync(path));

        Assert.Contains(
            "finite",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Normals",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsSkinnedGeometryWithoutConnectedClusters()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.MissingClusters);
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    ExpectedMeshes(),
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "Cluster",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsNonFiniteClusterWeightsInTheCodecBoundary()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.NonFiniteWeight);

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => FbxStrictExportInspector
                    .InspectFileAsync(path));

        Assert.Contains(
            "invalid weight",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsNamedGeometryWithoutTextureCoordinates()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.MissingUv);
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    ExpectedMeshes(),
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "normals and UVs",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsGlobalTextureReferenceWithoutMeshMaterialChain()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.DisconnectedVideo);
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    ExpectedMeshes(),
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "Material/Texture/Video chain",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        AcceptsSafeRelativeSiblingReferenceFromTextureObject()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.TextureRelativeFilenameOnly);
        FbxStrictExportInspection inspection =
            await FbxStrictExportInspector
                .InspectFileAsync(path);
        var validator =
            new BlenderFbxOutputValidator();

        await validator.ValidateAsync(
            path,
            ExpectedBones(),
            ExpectedClips(),
            ExpectedMeshes(),
            ExpectedTextures(),
            CancellationToken.None);

        FbxExternalFileReferenceInspection reference =
            Assert.Single(
                inspection.ExternalFileReferences);
        Assert.Equal("Texture", reference.ObjectType);
        Assert.Equal(
            "RelativeFilename",
            reference.PropertyName);
        Assert.Equal(TextureFileName, reference.Value);
    }

    [Fact]
    public async Task
        RejectsAbsoluteOnlyTextureReferenceWithMatchingBasename()
    {
        InvalidDataException error =
            await ValidateCorruptTexturePathAsync(
                FixtureCorruption.AbsoluteTexturePath);

        Assert.Contains(
            "absolute",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            TextureFileName,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        AcceptsSafeRelativeSiblingAlongsideExporterAbsolutePath()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.AbsoluteAndRelativeTexturePaths);
        var validator =
            new BlenderFbxOutputValidator();

        await validator.ValidateAsync(
            path,
            ExpectedBones(),
            ExpectedClips(),
            ExpectedMeshes(),
            ExpectedTextures(),
            CancellationToken.None);
    }

    [Fact]
    public async Task
        RejectsParentTraversalTextureReference()
    {
        InvalidDataException error =
            await ValidateCorruptTexturePathAsync(
                FixtureCorruption.ParentTraversalTexturePath);

        Assert.Contains(
            "parent-traversal",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task
        RejectsRelativeStagingDirectoryTextureReference()
    {
        InvalidDataException error =
            await ValidateCorruptTexturePathAsync(
                FixtureCorruption.StagingTexturePath);

        Assert.Contains(
            "staging-directory",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task
        RejectsBoneCurvesOutsideTheRequestedClipSpan()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.LongAnimation);
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    ExpectedMeshes(),
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "key range",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task
        RejectsAnimationStackWithoutBoneBoundCurves()
    {
        string path = await WriteFixtureAsync(
            FixtureCorruption.NoBoneCurves);
        var validator =
            new BlenderFbxOutputValidator();

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => validator.ValidateAsync(
                    path,
                    ExpectedBones(),
                    ExpectedClips(),
                    ExpectedMeshes(),
                    ExpectedTextures(),
                    CancellationToken.None));

        Assert.Contains(
            "bone curves",
            error.Message,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(
                _temporaryDirectory,
                recursive: true);
        }
    }

    private async Task<string> WriteFixtureAsync(
        FixtureCorruption corruption)
    {
        Directory.CreateDirectory(
            _temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            $"{corruption}.fbx");
        byte[] bytes = Serialize(
            BuildFixture(corruption));
        await File.WriteAllBytesAsync(
            path,
            bytes);
        return path;
    }

    private async Task<InvalidDataException>
        ValidateCorruptTexturePathAsync(
            FixtureCorruption corruption)
    {
        string path = await WriteFixtureAsync(
            corruption);
        var validator =
            new BlenderFbxOutputValidator();

        return await Assert.ThrowsAsync<InvalidDataException>(
            () => validator.ValidateAsync(
                path,
                ExpectedBones(),
                ExpectedClips(),
                ExpectedMeshes(),
                ExpectedTextures(),
                CancellationToken.None));
    }

    private static FbxBinaryDocument BuildFixture(
        FixtureCorruption corruption)
    {
        long animationStop =
            corruption ==
                FixtureCorruption.LongAnimation
                ? 4 * FrameTick
                : 2 * FrameTick;
        double normalZ =
            corruption ==
                FixtureCorruption.NonFiniteNormal
                ? double.NaN
                : 1.0;
        ImmutableArray<double> textureCoordinates =
            corruption ==
                FixtureCorruption.MissingUv
                ? []
                : DoubleArray(
                    0.0, 0.0,
                    1.0, 0.0,
                    0.0, 1.0);
        ImmutableArray<long> textureCoordinateIndices =
            textureCoordinates.IsEmpty
                ? []
                : LongArray(0, 1, 2);
        double rootSecondWeight =
            corruption ==
                FixtureCorruption.NonFiniteWeight
                ? double.NaN
                : 0.5;
        FbxNode texture = corruption ==
                FixtureCorruption.TextureRelativeFilenameOnly
            ? Node(
                "Texture",
                [41L, "Texture::DL1_BaseColor_tex", string.Empty],
                Node(
                    "RelativeFilename",
                    [TextureFileName]))
            : Node(
                "Texture",
                [41L, "Texture::DL1_BaseColor_tex", string.Empty]);
        FbxNode video = corruption switch
        {
            FixtureCorruption.TextureRelativeFilenameOnly =>
                Node(
                    "Video",
                    [42L, $"Video::{TextureFileName}", "Clip"]),
            FixtureCorruption.AbsoluteTexturePath =>
                Node(
                    "Video",
                    [42L, $"Video::{TextureFileName}", "Clip"],
                    Node(
                        "FileName",
                        [@$"C:\Users\Tester\AppData\Local\Temp\dlr-stage\{TextureFileName}"])),
            FixtureCorruption.AbsoluteAndRelativeTexturePaths =>
                Node(
                    "Video",
                    [42L, $"Video::{TextureFileName}", "Clip"],
                    Node(
                        "FileName",
                        [@$"C:\Users\Tester\AppData\Local\Temp\dlr-stage\{TextureFileName}"]),
                    Node(
                        "RelativeFilename",
                        [TextureFileName])),
            FixtureCorruption.ParentTraversalTexturePath =>
                Node(
                    "Video",
                    [42L, $"Video::{TextureFileName}", "Clip"],
                    Node(
                        "RelativeFilename",
                        [$"..\\{TextureFileName}"])),
            FixtureCorruption.StagingTexturePath =>
                Node(
                    "Video",
                    [42L, $"Video::{TextureFileName}", "Clip"],
                    Node(
                        "RelativeFilename",
                        [$".dlr-stage-1234\\{TextureFileName}"])),
            _ =>
                Node(
                    "Video",
                    [42L, $"Video::{TextureFileName}", "Clip"],
                    Node(
                        "RelativeFilename",
                        [TextureFileName])),
        };
        var objects = new List<FbxNode>
        {
            Model(1, "DL1_Retail_Armature", "Null"),
            Model(2, "Root", "LimbNode"),
            Model(3, "Child", "LimbNode"),
            Model(4, "RetailMesh", "Mesh"),
            Model(5, "DLR_BindPoseGuard", "Mesh"),
            Geometry(
                10,
                "RetailMesh_Mesh",
                DoubleArray(
                    0.0, 0.0, 0.0,
                    1.0, 0.0, 0.0,
                    0.0, 1.0, 0.0),
                LongArray(0, 1, -3),
                DoubleArray(
                    0.0, 0.0, normalZ,
                    0.0, 0.0, 1.0,
                    0.0, 0.0, 1.0),
                textureCoordinates,
                textureCoordinateIndices),
            Geometry(
                11,
                "DLR_BindPoseGuard_Mesh",
                DoubleArray(
                    0.0, 0.0, 0.0,
                    0.0, 1.0, 0.0),
                [],
                [],
                [],
                []),
            BindPose(20, [1, 2, 3]),
            Deformer(30, "RetailSkin", "Skin"),
            Cluster(
                31,
                "RootCluster",
                LongArray(0, 1),
                DoubleArray(
                    1.0,
                    rootSecondWeight)),
            Cluster(
                32,
                "ChildCluster",
                LongArray(1, 2),
                DoubleArray(0.5, 1.0)),
            Node(
                "Material",
                [40L, "Material::DL1_BaseColor_tex", string.Empty]),
            texture,
            video,
            Stack(
                50,
                "Idle",
                0,
                animationStop),
            Layer(51, "Idle"),
            CurveNode(60, "Lcl Translation"),
            CurveNode(61, "Lcl Translation"),
            Curve(
                70,
                [0, animationStop / 2, animationStop],
                [0.0, 0.1, 0.2]),
            Curve(
                71,
                [0, animationStop / 2, animationStop],
                [0.0, 0.0, 0.0]),
        };
        var connections = new List<FbxNode>
        {
            Connection("OO", 2, 1),
            Connection(
                "OO",
                3,
                corruption ==
                    FixtureCorruption.WrongChildParent
                    ? 1
                    : 2),
            Connection("OO", 10, 4),
            Connection("OO", 11, 5),
            Connection("OO", 40, 4),
            Connection("OP", 41, 40, "DiffuseColor"),
            Connection("OO", 30, 10),
            Connection("OO", 51, 50),
            Connection("OO", 60, 51),
            Connection("OP", 70, 60, "d|X"),
            Connection("OO", 61, 51),
            Connection("OP", 71, 61, "d|X"),
        };
        if (corruption !=
            FixtureCorruption.NoBoneCurves)
        {
            connections.AddRange(
            [
                Connection(
                    "OP",
                    60,
                    2,
                    "Lcl Translation"),
                Connection(
                    "OP",
                    61,
                    3,
                    "Lcl Translation"),
            ]);
        }

        if (corruption !=
            FixtureCorruption.DisconnectedVideo)
        {
            connections.Add(
                Connection("OO", 42, 41));
        }

        if (corruption !=
            FixtureCorruption.MissingClusters)
        {
            connections.AddRange(
            [
                Connection("OO", 31, 30),
                Connection("OO", 32, 30),
                Connection("OO", 2, 31),
                Connection("OO", 3, 32),
            ]);
        }

        return Document(
            objects,
            connections,
            GlobalSettings(
                Property70("TimeMode", 6)));
    }

    private static IReadOnlyList<BlenderFbxJobBone>
        ExpectedBones() =>
    [
        new(
            0,
            "Root",
            -1,
            0x11111111,
            [0.0, 0.0, 0.0],
            [1.0, 0.0, 0.0, 0.0],
            [1.0, 1.0, 1.0],
            true,
            true,
            false,
            "root"),
        new(
            1,
            "Child",
            0,
            0x22222222,
            [0.0, 1.0, 0.0],
            [1.0, 0.0, 0.0, 0.0],
            [1.0, 1.0, 1.0],
            false,
            true,
            false,
            "child"),
    ];

    private static IReadOnlyList<BlenderFbxJobClip>
        ExpectedClips() =>
    [
        new(
            "Idle",
            "idle.anm2",
            new string('a', 64),
            "exact",
            30.0,
            30.0,
            3,
            3,
            "unused.bin",
            [0x11111111, 0x22222222],
            [],
            new BlenderFbxJobMotionAccumulator(
                false,
                false,
                false,
                null)),
    ];

    private static IReadOnlyList<BlenderFbxJobMesh>
        ExpectedMeshes() =>
    [
        new(
            "RetailMesh",
            "unused.bin",
            3,
            3,
            16,
            true,
            [
                1.0f, 0.0f, 0.0f, 0.0f,
                0.0f, 1.0f, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                0.0f, 0.0f, 0.0f, 1.0f,
            ],
            "tex"),
    ];

    private static IReadOnlyList<BlenderFbxJobTexture>
        ExpectedTextures() =>
    [
        new(
            "tex",
            "retail_base_color",
            "unused.dds",
            TextureFileName,
            4,
            4,
            "Bc1Unorm"),
    ];

    private static FbxNode Geometry(
        long objectId,
        string name,
        ImmutableArray<double> vertices,
        ImmutableArray<long> polygonIndices,
        ImmutableArray<double> normals,
        ImmutableArray<double> uvs,
        ImmutableArray<long> uvIndices)
    {
        var children = new List<FbxNode>
        {
            Node("Vertices", [vertices]),
            Node(
                "PolygonVertexIndex",
                [polygonIndices]),
        };
        if (!normals.IsEmpty)
        {
            children.Add(
                Node(
                    "LayerElementNormal",
                    [0],
                    Node("Normals", [normals])));
        }

        if (!uvs.IsEmpty)
        {
            children.Add(
                Node(
                    "LayerElementUV",
                    [0],
                    Node("UV", [uvs]),
                    Node("UVIndex", [uvIndices])));
        }

        return Node(
            "Geometry",
            [objectId, $"Geometry::{name}", "Mesh"],
            children.ToArray());
    }

    private static FbxNode Model(
        long objectId,
        string name,
        string subtype) =>
        Node(
            "Model",
            [objectId, $"Model::{name}", subtype],
            Node(
                "Properties70",
                [],
                Property70(
                    "Lcl Translation",
                    0.0,
                    0.0,
                    0.0),
                Property70(
                    "Lcl Rotation",
                    0.0,
                    0.0,
                    0.0),
                Property70(
                    "Lcl Scaling",
                    1.0,
                    1.0,
                    1.0)));

    private static FbxNode Deformer(
        long objectId,
        string name,
        string subtype) =>
        Node(
            "Deformer",
            [
                objectId,
                $"Deformer::{name}",
                subtype,
            ]);

    private static FbxNode Cluster(
        long objectId,
        string name,
        ImmutableArray<long> indices,
        ImmutableArray<double> weights) =>
        Node(
            "Deformer",
            [
                objectId,
                $"SubDeformer::{name}",
                "Cluster",
            ],
            Node("Indexes", [indices]),
            Node("Weights", [weights]),
            Node("Transform", [IdentityMatrix()]),
            Node(
                "TransformLink",
                [IdentityMatrix()]));

    private static FbxNode BindPose(
        long objectId,
        IReadOnlyList<long> nodeIds) =>
        Node(
            "Pose",
            [
                objectId,
                "Pose::BindPose",
                "BindPose",
            ],
            [
                Node("Type", ["BindPose"]),
                .. nodeIds.Select(PoseNode),
            ]);

    private static FbxNode PoseNode(long nodeId) =>
        Node(
            "PoseNode",
            [],
            Node("Node", [nodeId]),
            Node("Matrix", [IdentityMatrix()]));

    private static FbxNode Layer(
        long objectId,
        string name) =>
        Node(
            "AnimationLayer",
            [
                objectId,
                $"AnimLayer::{name}",
                string.Empty,
            ]);

    private static FbxNode Stack(
        long objectId,
        string name,
        long start,
        long stop) =>
        Node(
            "AnimationStack",
            [
                objectId,
                $"AnimStack::{name}",
                string.Empty,
            ],
            Node(
                "Properties70",
                [],
                Property70("LocalStart", start),
                Property70("LocalStop", stop)));

    private static FbxNode CurveNode(
        long objectId,
        string propertyName) =>
        Node(
            "AnimationCurveNode",
            [
                objectId,
                $"AnimationCurveNode::{propertyName}",
                string.Empty,
            ]);

    private static FbxNode Curve(
        long objectId,
        long[] times,
        double[] values) =>
        Node(
            "AnimationCurve",
            [
                objectId,
                $"AnimationCurve::{objectId}",
                string.Empty,
            ],
            Node(
                "KeyTime",
                [times.ToImmutableArray()]),
            Node(
                "KeyValueFloat",
                [values.ToImmutableArray()]));

    private static FbxNode GlobalSettings(
        params FbxNode[] properties) =>
        Node(
            "GlobalSettings",
            [],
            Node(
                "Properties70",
                [],
                properties));

    private static FbxNode Property70(
        string name,
        params object[] values) =>
        Node(
            "P",
            [
                name,
                name,
                string.Empty,
                "A",
                .. values,
            ]);

    private static FbxNode Connection(
        string kind,
        long childId,
        long parentId,
        params object[] metadata) =>
        Node(
            "C",
            [
                kind,
                childId,
                parentId,
                .. metadata,
            ]);

    private static FbxBinaryDocument Document(
        IReadOnlyList<FbxNode> objects,
        IReadOnlyList<FbxNode> connections,
        FbxNode globalSettings) =>
        new(
            7400,
            [
                globalSettings,
                Node(
                    "Objects",
                    [],
                    objects.ToArray()),
                Node(
                    "Connections",
                    [],
                    connections.ToArray()),
            ]);

    private static FbxNode Node(
        string name,
        object[] properties,
        params FbxNode[] children) =>
        new(
            name,
            properties
                .Select(Property)
                .ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);

    private static FbxProperty Property(
        object value) =>
        new(
            value switch
            {
                long => 'L',
                int => 'I',
                float => 'F',
                double => 'D',
                string => 'S',
                ImmutableArray<long> => 'l',
                ImmutableArray<int> => 'i',
                ImmutableArray<double> => 'd',
                ImmutableArray<float> => 'f',
                _ => throw new InvalidDataException(
                    $"Unsupported synthetic FBX value type '{value.GetType().Name}'."),
            },
            value);

    private static ImmutableArray<double>
        IdentityMatrix() =>
        DoubleArray(
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static ImmutableArray<double>
        DoubleArray(params double[] values) =>
        values.ToImmutableArray();

    private static ImmutableArray<long>
        LongArray(params long[] values) =>
        values.ToImmutableArray();

    private static byte[] Serialize(
        FbxBinaryDocument document)
    {
        using var stream = new MemoryStream();
        stream.Write(
            "Kaydara FBX Binary  \0\u001a\0"u8);
        WriteUInt32(
            stream,
            document.Version);
        foreach (FbxNode node in document.Nodes)
        {
            WriteNode(
                stream,
                node);
        }

        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static void WriteNode(
        Stream output,
        FbxNode node)
    {
        byte[] nameBytes =
            Encoding.UTF8.GetBytes(node.Name);
        using var propertyStream =
            new MemoryStream();
        foreach (FbxProperty property in
                 node.Properties)
        {
            WriteProperty(
                propertyStream,
                property);
        }

        byte[] propertyBytes =
            propertyStream.ToArray();
        long start = output.Position;
        WriteUInt32(output, 0);
        WriteUInt32(
            output,
            checked((uint)node.Properties.Length));
        WriteUInt32(
            output,
            checked((uint)propertyBytes.Length));
        output.WriteByte(
            checked((byte)nameBytes.Length));
        output.Write(nameBytes);
        output.Write(propertyBytes);
        foreach (FbxNode child in node.Children)
        {
            WriteNode(
                output,
                child);
        }

        output.Write(new byte[13]);
        long end = output.Position;
        output.Position = start;
        WriteUInt32(
            output,
            checked((uint)end));
        output.Position = end;
    }

    private static void WriteProperty(
        Stream stream,
        FbxProperty property)
    {
        stream.WriteByte(
            checked((byte)property.TypeCode));
        switch (property.Value)
        {
            case int value:
                WriteInt32(stream, value);
                break;
            case long value:
                WriteInt64(stream, value);
                break;
            case float value:
                WriteInt32(
                    stream,
                    BitConverter.SingleToInt32Bits(
                        value));
                break;
            case double value:
                WriteInt64(
                    stream,
                    BitConverter.DoubleToInt64Bits(
                        value));
                break;
            case string value:
                byte[] bytes =
                    Encoding.UTF8.GetBytes(value);
                WriteUInt32(
                    stream,
                    checked((uint)bytes.Length));
                stream.Write(bytes);
                break;
            case ImmutableArray<int> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.Length * sizeof(int),
                    () =>
                    {
                        foreach (int value in values)
                        {
                            WriteInt32(stream, value);
                        }
                    });
                break;
            case ImmutableArray<long> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.Length * sizeof(long),
                    () =>
                    {
                        foreach (long value in values)
                        {
                            WriteInt64(stream, value);
                        }
                    });
                break;
            case ImmutableArray<float> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.Length * sizeof(float),
                    () =>
                    {
                        foreach (float value in values)
                        {
                            WriteInt32(
                                stream,
                                BitConverter
                                    .SingleToInt32Bits(
                                        value));
                        }
                    });
                break;
            case ImmutableArray<double> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.Length * sizeof(double),
                    () =>
                    {
                        foreach (double value in values)
                        {
                            WriteInt64(
                                stream,
                                BitConverter
                                    .DoubleToInt64Bits(
                                        value));
                        }
                    });
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported synthetic FBX property '{property.Value.GetType().Name}'.");
        }
    }

    private static void WriteArray(
        Stream stream,
        int elementCount,
        int byteCount,
        Action writeValues)
    {
        WriteUInt32(
            stream,
            checked((uint)elementCount));
        WriteUInt32(stream, 0);
        WriteUInt32(
            stream,
            checked((uint)byteCount));
        writeValues();
    }

    private static void WriteUInt32(
        Stream stream,
        uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            value);
        stream.Write(bytes);
    }

    private static void WriteInt32(
        Stream stream,
        int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes,
            value);
        stream.Write(bytes);
    }

    private static void WriteInt64(
        Stream stream,
        long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes,
            value);
        stream.Write(bytes);
    }

    private enum FixtureCorruption
    {
        None,
        WrongChildParent,
        NonFiniteNormal,
        MissingClusters,
        NonFiniteWeight,
        MissingUv,
        DisconnectedVideo,
        LongAnimation,
        NoBoneCurves,
        TextureRelativeFilenameOnly,
        AbsoluteTexturePath,
        AbsoluteAndRelativeTexturePaths,
        ParentTraversalTexturePath,
        StagingTexturePath,
    }
}
