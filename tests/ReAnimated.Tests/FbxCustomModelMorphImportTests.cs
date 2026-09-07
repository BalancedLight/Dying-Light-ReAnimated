using System.Buffers.Binary;
using System.Text;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

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
    public void ImportsUniformFullWeightsPerAffectedPointWithoutChangingSourceOrDeltas()
    {
        byte[] fbx = CreateMorphFbx(["generic_smile"], fullWeights: [100.0, 100.0]);
        var imported = FbxModelAuthoringImporter.Import(fbx, "uniform-point-weights.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp });
        var target = Assert.Single(Assert.Single(imported.Surfaces).MorphTargets);
        Assert.Equal(0.005, target.PositionDeltas[0].X, precision: 9);
        Assert.Equal(0.0, target.PositionDeltas[1].Length, precision: 9);
        Assert.Equal(-0.0025, target.PositionDeltas[2].Y, precision: 9);
        Assert.True(fbx.AsSpan().SequenceEqual(imported.Package.SourceFbx.AsSpan()));
        var reopened = FbxModelAuthoringImporter.ImportPackage(imported.Package);
        Assert.Equal(imported.Package.Document.MorphSignature, reopened.Package.Document.MorphSignature);
        Assert.True(target.PositionDeltas.SequenceEqual(Assert.Single(Assert.Single(reopened.Surfaces).MorphTargets).PositionDeltas));
    }

    [Theory]
    [InlineData(100.0, 50.0)]
    [InlineData(50.0, 50.0)]
    [InlineData(100.0, double.NaN)]
    [InlineData(100.0, double.PositiveInfinity)]
    public void RejectsMaskedOrNonfinitePerPointFullWeights(double first, double second)
    {
        var error = Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.Import(
            CreateMorphFbx(["generic_smile"], fullWeights: [first, second]), "unsupported-weights.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp }));
        Assert.Contains("FullWeights", error.Message);
    }

    [Fact]
    public void RejectsUniformWeightsWithWrongPointCountAndNonFullScalar()
    {
        Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.Import(
            CreateMorphFbx(["generic_smile"], fullWeights: [100.0, 100.0, 100.0]), "wrong-count.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp }));
        Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.Import(
            CreateMorphFbx(["generic_smile"], fullWeights: [50.0]), "non-full-scalar.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp }));
    }

    [Fact]
    public void UniformWeightsDoNotPermitMultipleConnectedShapes()
    {
        var error = Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.Import(
            CreateMorphFbx(["generic_smile"], addSecondShapeToFirstChannel: true, fullWeights: [100.0, 100.0]), "multiple-shapes.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp }));
        Assert.Contains("progressive or multiple", error.Message);
    }

    internal static byte[] CreateNormalMorphFbx() => CreateMorphFbx(["generic_smile"],
        normalDeltas: [0, 1, -1, 1, 0, -1]);

    [Fact]
    public void AuthoredNormalsReachExpandedVerticesPreviewAndCpuDeformation()
    {
        var imported = FbxModelAuthoringImporter.Import(CreateNormalMorphFbx(), "normal-morph.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp });
        var surface = Assert.Single(imported.Surfaces);
        var target = Assert.Single(surface.MorphTargets);
        Assert.Equal(3, target.NormalDeltas.Length);
        Assert.True((surface.Vertices[0].Normal + target.NormalDeltas[0] - Vector3D.UnitY).Length < 1e-9);
        Assert.Equal(Vector3D.Zero, target.NormalDeltas[1]);
        Assert.True((surface.Vertices[2].Normal + target.NormalDeltas[2] - Vector3D.UnitX).Length < 1e-9);
        var preview = CustomModelPreviewAdapter.Create(imported, null, 0, mode: CustomModelPreviewMode.SourceFbx);
        var mesh = Assert.Single(preview.Meshes);
        Assert.False(Assert.Single(mesh.MorphTargets).NormalDeltas.IsEmpty);
        var full = CpuMeshDeformationEvaluator.Evaluate(mesh, preview.Skeleton, [new MorphWeight("generic_smile",1)]);
        Assert.True(System.Numerics.Vector3.Distance(System.Numerics.Vector3.UnitY,full[0].Normal) < 1e-6);
        var reopened = FbxModelAuthoringImporter.ImportPackage(imported.Package);
        Assert.True(target.NormalDeltas.SequenceEqual(Assert.Single(Assert.Single(reopened.Surfaces).MorphTargets).NormalDeltas));
    }

    [Fact]
    public void NormalDeltasUseInverseTransposeAndRemainIndependentOfLengthUnits()
    {
        double n = Math.Sqrt(.5);
        var imported = FbxModelAuthoringImporter.Import(CreateMorphFbx(["generic_smile"],
                normalDeltas: [-n, 1-n, 0, -n, 1-n, 0],
                baseNormals: [n,n,0,n,n,0,n,n,0], scaleMesh: true),
            "scaled-normal-morph.fbx",new FbxModelAuthoringImportOptions { RigMode=CustomModelRigMode.StaticProp });
        var surface=Assert.Single(imported.Surfaces);var target=Assert.Single(surface.MorphTargets);
        Assert.Equal(.5,surface.Vertices[0].Normal.X/surface.Vertices[0].Normal.Y,precision:9);
        Assert.True((surface.Vertices[0].Normal+target.NormalDeltas[0]-Vector3D.UnitY).Length<1e-9);
    }

    [Fact]
    public void SameControlPointNormalDeltaUsesEachDistinctBaseCornerNormal()
    {
        var imported=FbxModelAuthoringImporter.Import(CreateMorphFbx(["generic_smile"], normalDeltas:[0,1,0,0,0,0],
            baseNormals:[0,0,1,0,0,1,0,0,1,1,0,0,1,0,0,1,0,0], splitCorners:true),
            "split-normal-morph.fbx",new FbxModelAuthoringImportOptions { RigMode=CustomModelRigMode.StaticProp });
        var surface=Assert.Single(imported.Surfaces);var target=Assert.Single(surface.MorphTargets);
        Assert.Equal(6,target.NormalDeltas.Length);
        Assert.True((target.NormalDeltas[0]-target.NormalDeltas[3]).Length>.3);
        Assert.True((surface.Vertices[0].Normal+target.NormalDeltas[0]-new Vector3D(0,Math.Sqrt(.5),Math.Sqrt(.5))).Length<1e-9);
        Assert.True((surface.Vertices[3].Normal+target.NormalDeltas[3]-new Vector3D(Math.Sqrt(.5),Math.Sqrt(.5),0)).Length<1e-9);
    }

    [Fact]
    public void RejectsMalformedNonfiniteAndCollapsedShapeNormals()
    {
        Assert.Throws<InvalidDataException>(()=>FbxModelAuthoringImporter.Import(CreateMorphFbx(["generic_smile"],normalDeltas:[1,0,0]),"short.fbx"));
        Assert.Throws<InvalidDataException>(()=>FbxModelAuthoringImporter.Import(CreateMorphFbx(["generic_smile"],normalDeltas:[double.NaN,0,0,0,0,0]),"nan.fbx"));
        Assert.Throws<InvalidDataException>(()=>FbxModelAuthoringImporter.Import(CreateMorphFbx(["generic_smile"],normalDeltas:[0,0,-1,0,0,0]),"collapsed.fbx"));
    }

    [Fact]
    public void NormalOnlyTargetChangesInvalidateTheMorphContract()
    {
        var a=FbxModelAuthoringImporter.Import(CreateNormalMorphFbx(),"original.fbx");
        var b=FbxModelAuthoringImporter.PreviewReimport(a.Package,CreateMorphFbx(["generic_smile"],normalDeltas:[1,0,-1,1,0,-1]),"normals-changed.fbx");
        Assert.True(b.FacialMappingsBecomeStale);
        Assert.False(b.BoneAndHelperMappingsBecomeStale);
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
    public void ImportsMeshWhenUserExplicitlySkipsInvalidMorphChannels()
    {
        byte[] fbx = CreateMorphFbx(["generic_smile", "generic_smile"]);

        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            fbx,
            "generic-invalid-morphs.fbx",
            new FbxModelAuthoringImportOptions
            {
                RigMode = CustomModelRigMode.StaticProp,
                IgnoreMorphChannels = true,
            });

        Assert.Empty(imported.Package.Document.MorphChannels);
        Assert.True(imported.Package.Document.IgnoreMorphChannels);
        Assert.Empty(Assert.Single(imported.Surfaces).MorphTargets);
        CustomModelImportDiagnostic diagnostic = Assert.Single(
            imported.Package.Document.Diagnostics,
            static diagnostic => diagnostic.Code == "model_morph_channels_skipped");
        Assert.Equal(CustomModelImportSeverity.Warning, diagnostic.Severity);

        FbxModelAuthoringImportResult reopened =
            FbxModelAuthoringImporter.ImportPackage(imported.Package);
        Assert.True(reopened.Package.Document.IgnoreMorphChannels);
        Assert.Empty(reopened.Package.Document.MorphChannels);
        Assert.Empty(Assert.Single(reopened.Surfaces).MorphTargets);

        CustomModelPackage legacyPackage = new(
            imported.Package.Document with { IgnoreMorphChannels = false },
            imported.Package.SourceFbx,
            imported.Package.TexturePayloads);
        FbxModelAuthoringImportResult reopenedLegacy =
            FbxModelAuthoringImporter.ImportPackage(legacyPackage);
        Assert.True(reopenedLegacy.Package.Document.IgnoreMorphChannels);
        Assert.Empty(reopenedLegacy.Package.Document.MorphChannels);
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

    [Fact]
    public void ChangedMorphContractCannotSilentlyDropAuthoredModelFeatures()
    {
        var original = FbxModelAuthoringImporter.Import(CreateMorphFbx(["generic_smile"]),
            "generic.fbx", new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp });
        var package = original.Package with
        {
            Document = original.Package.Document with
            {
                FacialPresets = new FacialPresetLibrary
                {
                    Presets = [new FacialPresetDefinition { Name = "Smile",
                        Weights = System.Collections.Immutable.ImmutableDictionary<string, double>.Empty.Add("generic_smile", 1) }],
                },
            },
        };
        var error = Assert.Throws<InvalidDataException>(() => FbxModelAuthoringImporter.PreviewReimport(
            package, CreateMorphFbx(["generic_smile"], firstShapeDeltaX: 0.75), "changed.fbx"));
        Assert.Contains("review those feature bindings", error.Message);
        Assert.Equal("Smile", Assert.Single(package.Document.FacialPresets.Presets).Name);
    }

    private static byte[] CreateMorphFbx(
        IReadOnlyList<string> channelNames,
        bool addSecondShapeToFirstChannel = false,
        double firstShapeDeltaX = 0.5,
        double[]? fullWeights = null,
        double[]? normalDeltas = null,
        double[]? baseNormals = null,
        bool scaleMesh = false,
        bool splitCorners = false)
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
                [
                Node(
                    "Vertices",
                    [DoubleArray([
                        0.0, 0.0, 0.0,
                        1.0, 0.0, 0.0,
                        0.0, 1.0, 0.0,
                    ])]),
                Node(
                    "PolygonVertexIndex",
                    [Int64Array(splitCorners ? [0,1,-3,0,2,-2] : [0, 1, -3])]),
                ..(normalDeltas is null ? Array.Empty<FbxTreeNode>() : new[] { Node("LayerElementNormal", [ScalarInt64(0)],
                    Node("MappingInformationType", [ScalarString("ByPolygonVertex")]),
                    Node("ReferenceInformationType", [ScalarString("Direct")]),
                    Node("Normals", [DoubleArray(baseNormals ?? [0,0,1,0,0,1,0,0,1])])) })]),
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
        if (scaleMesh)
        {
            objects.Add(Node("Model",[ScalarInt64(70),ScalarString("Model::ScaledMesh"),ScalarString("Mesh")],
                Node("Properties70",[],Node("P",[ScalarString("Lcl Scaling"),ScalarString("Lcl Scaling"),ScalarString(""),ScalarString("A"),ScalarDouble(2),ScalarDouble(1),ScalarDouble(3)]))));
            connections.Add(Connection(baseGeometryId,70));
        }

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
                Node("FullWeights", [DoubleArray(fullWeights ?? [100.0])])));
            objects.Add(CreateShape(
                shapeId,
                channelName,
                index == 0 ? firstShapeDeltaX : 0.5, normalDeltas));
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
        double firstDeltaX,
        double[]? normalDeltas = null) =>
        Node(
            "Geometry",
            [
                ScalarInt64(shapeId),
                ScalarString($"Geometry::{name}"),
                ScalarString("Shape"),
            ],
            [
            Node("Indexes", [Int64Array([0, 2])]),
            Node("Vertices", [DoubleArray([
                firstDeltaX, 0.0, 0.0,
                0.0, -0.25, 0.0,
            ])]),
            ..(normalDeltas is null ? Array.Empty<FbxTreeNode>() : new[] { Node("Normals",[DoubleArray(normalDeltas)]) })]);

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

    private static byte[] DoubleArray(double[] values)
    {
        byte[] raw = new byte[checked(values.Length * sizeof(double))];
        for (int index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                raw.AsSpan(index * sizeof(double)),
                BitConverter.DoubleToInt64Bits(values[index]));
        }

        return ArrayProperty('d', values.Length, raw);
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
