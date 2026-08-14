using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectExportSchema2CliTests
{
    private static readonly long FrameTick =
        FbxBinaryDocument.TicksPerSecond / 30;

    [Fact]
    public void CustomRuntimeRigHashesBonesAndHelpersInOneNamespace()
    {
        string hash = Convert.ToHexString(SHA256.HashData(
                "generic descriptor model"u8))
            .ToLowerInvariant();
        var root = new CustomModelBone
        {
            Index = 0,
            FbxObjectId = 1,
            Name = "ba",
            ParentIndex = -1,
            LocalBindTransform =
                ReAnimated.Core.Mathematics.TransformTRS.Identity,
            ExactLocalBindMatrix =
                ReAnimated.Core.Mathematics.TransformMatrix.Identity,
            Kind = BoneKind.Root,
            IsWeighted = true,
        };
        var helper = new CustomModelAuthoredHelper
        {
            Id = Guid.NewGuid(),
            Name = "generic_helper",
            ParentNodeIndex = 0,
            LocalTransform =
                ReAnimated.Core.Mathematics.TransformTRS.Identity,
            ExactLocalMatrix =
                ReAnimated.Core.Mathematics.TransformMatrix.Identity,
            Kind = CustomModelAuthoredHelperKind.Helper,
        };
        var document = new CustomModelDocument
        {
            ModelId = Guid.NewGuid(),
            Name = "Generic descriptor model",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "generic.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            RigSignature = CustomModelContractSignatures.ComputeRig([root]),
            Bones = [root],
            AuthoredHelpers = [helper],
        };

        RigDefinition rig = document.CreateRigDefinition();
        Assert.Equal(
            Dl1NameHash.Compute("ba"),
            rig.Bones[0].DescriptorHash);
        Assert.Equal(
            Dl1NameHash.Compute("generic_helper"),
            rig.Bones[1].DescriptorHash);

        CustomModelDocument collision = document with
        {
            AuthoredHelpers =
            [
                // Chrome's base-41 hash intentionally collides for these
                // distinct ASCII names: 41*'b'+'a' == 41*'c'+'8'.
                helper with { Name = "c8" },
            ],
        };
        RigDefinition sourcePreviewRig = collision.CreateRigDefinition();
        Assert.Equal(
            sourcePreviewRig.Bones[0].DescriptorHash,
            sourcePreviewRig.Bones[1].DescriptorHash);
        Assert.Throws<InvalidDataException>(
            collision.CreateDl1AnimationRigDefinition);
    }

    [Fact]
    public async Task ExportProjectUsesEmbeddedStackAndCustomTargetDirectly()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string projectDirectory = Path.Combine(directory, "project");
            string modelDirectory = Path.Combine(projectDirectory, "Models");
            string installDirectory = Path.Combine(directory, "install");
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(installDirectory);

            FbxModelAuthoringImportResult initial =
                FbxModelAuthoringImporter.Import(
                    CreateAnimatedModelFbx(),
                    "generic_animated_model.fbx",
                    new FbxModelAuthoringImportOptions
                    {
                        RigMode = CustomModelRigMode.ExactFbxRig,
                    });
            string packagePath = Path.Combine(
                modelDirectory,
                "generic_model.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(
                initial.Package,
                packagePath);
            FbxModelAuthoringImportResult reopened =
                FbxModelAuthoringImporter.ImportPackage(
                    CustomModelPackageSerializer.Load(packagePath));
            RigDefinition rig = reopened.Rig ??
                throw new InvalidDataException(
                    "Generated custom-model fixture has no rig.");
            CustomModelAnimationClip stack = Assert.Single(
                reopened.Package.Document.AnimationClips);
            AnimationClip sourceClip = reopened.AnimationClips[stack.Id];
            Assert.True(stack.HasSkeletalTracks);

            Guid assetId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            Guid sourceId = Guid.NewGuid();
            Guid variantId = Guid.NewGuid();
            string packageHash = await ComputeSha256Async(packagePath);
            string runtimeRigSignature = RigSignature.Compute(rig);
            var asset = new ProjectAssetReference
            {
                Id = assetId,
                Kind = ProjectAssetKind.CustomModelSource,
                RelativePath = "Models/generic_model.dlrmodel",
                ResourceId =
                    $"custom-model:{reopened.Package.Document.ModelId:N}:generic_model",
                ContentSha256 = packageHash,
            };
            var model = new ProjectModelEntry
            {
                Id = modelId,
                AssetId = assetId,
                Name = "Generic model",
                RigSignature = reopened.Package.Document.RigSignature,
                MorphSignature = reopened.Package.Document.MorphSignature,
            };
            var source = new ProjectAnimationSource
            {
                Id = sourceId,
                Name = "Generic stack source",
                SourceAssetId = assetId,
                EmbeddedCustomModelStack =
                    new ProjectEmbeddedAnimationStackIdentity
                    {
                        ClipId = stack.Id,
                        FbxObjectId = stack.FbxObjectId,
                        StackFingerprint = stack.SourceFingerprint,
                        SourceRigSignature = runtimeRigSignature,
                        Roles = AnimationSourceRoles.Body,
                        FacialSourceValueUnit =
                            ProjectMorphSourceValueUnit.Percent,
                    },
                FrameRate = stack.FrameRate,
                FrameCount = sourceClip.FrameCount,
            };
            var variant = new ProjectAnimationVariant
            {
                Id = variantId,
                SourceId = sourceId,
                Name = "Generic embedded direct",
                TargetModelId = modelId,
                TargetRigId = rig.Id,
                TargetRigSignature = runtimeRigSignature,
            };
            string projectPath = Path.Combine(
                projectDirectory,
                "generic.dlraproj");
            ProjectSerializer.SaveAtomic(
                DlraProject.Create("Generic schema 2 export") with
                {
                    Assets = [asset],
                    Models = [model],
                    AnimationSources = [source],
                    AnimationVariants = [variant],
                    ActiveAnimationId = variantId,
                    Workflow = new ProjectWorkflowState
                    {
                        SelectedModelId = modelId,
                        SelectedAnimationSourceId = sourceId,
                        SelectedAnimationVariantId = variantId,
                    },
                },
                projectPath);
            DlraProject saved = ProjectSerializer.Load(projectPath);
            Assert.Empty(saved.Animations);

            string outputDirectory = Path.Combine(directory, "output");
            int result = await ProjectExportCommand.RunAsync(
                [
                    projectPath,
                    installDirectory,
                    outputDirectory,
                    source.Name,
                    "body",
                ],
                new JsonSerializerOptions(),
                CancellationToken.None);

            Assert.Equal(0, result);
            string outputPath = Path.Combine(
                outputDirectory,
                "Generic embedded direct.anm2");
            Assert.True(File.Exists(outputPath));
            AnimationClip exported = Anm2DomainAdapter.ImportBody(
                await Anm2Reader.ReadFileAsync(outputPath),
                rig,
                stack.FrameRate).Clip;
            double expected = sourceClip.SamplePose(
                    rig,
                    stack.FrameRate.SecondsForFrame(1))
                .LocalTransforms[0].Translation.X;
            double actual = exported.SamplePose(
                    rig,
                    stack.FrameRate.SecondsForFrame(1))
                .LocalTransforms[0].Translation.X;
            Assert.InRange(Math.Abs(expected - actual), 0, 0.0001);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportProjectRejectsChangedEmbeddedStackFingerprint()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string projectDirectory = Path.Combine(directory, "project");
            string modelDirectory = Path.Combine(projectDirectory, "Models");
            string installDirectory = Path.Combine(directory, "install");
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(installDirectory);
            FbxModelAuthoringImportResult imported =
                FbxModelAuthoringImporter.Import(
                    CreateAnimatedModelFbx(),
                    "generic_stack_source.fbx",
                    new FbxModelAuthoringImportOptions
                    {
                        RigMode = CustomModelRigMode.ExactFbxRig,
                    });
            string packagePath = Path.Combine(
                modelDirectory,
                "fingerprint_source.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(
                imported.Package,
                packagePath);
            FbxModelAuthoringImportResult reopened =
                FbxModelAuthoringImporter.ImportPackage(
                    CustomModelPackageSerializer.Load(packagePath));
            RigDefinition rig = reopened.Rig!;
            CustomModelAnimationClip stack = Assert.Single(
                reopened.Package.Document.AnimationClips);
            string runtimeSignature = RigSignature.Compute(rig);
            Guid assetId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            Guid sourceId = Guid.NewGuid();
            Guid variantId = Guid.NewGuid();
            var asset = new ProjectAssetReference
            {
                Id = assetId,
                Kind = ProjectAssetKind.CustomModelSource,
                RelativePath = "Models/fingerprint_source.dlrmodel",
                ResourceId =
                    $"custom-model:{reopened.Package.Document.ModelId:N}:fingerprint_source",
                ContentSha256 = await ComputeSha256Async(packagePath),
            };
            var model = new ProjectModelEntry
            {
                Id = modelId,
                AssetId = assetId,
                Name = "Fingerprint target",
                RigSignature = reopened.Package.Document.RigSignature,
                MorphSignature = reopened.Package.Document.MorphSignature,
            };
            var source = new ProjectAnimationSource
            {
                Id = sourceId,
                Name = "Fingerprint source",
                SourceAssetId = assetId,
                EmbeddedCustomModelStack =
                    new ProjectEmbeddedAnimationStackIdentity
                    {
                        ClipId = stack.Id,
                        FbxObjectId = stack.FbxObjectId,
                        StackFingerprint = new string('f', 64),
                        SourceRigSignature = runtimeSignature,
                        Roles = AnimationSourceRoles.Body,
                    },
                FrameRate = stack.FrameRate,
                FrameCount = stack.FrameCount,
            };
            var variant = new ProjectAnimationVariant
            {
                Id = variantId,
                SourceId = sourceId,
                Name = "Fingerprint variant",
                TargetModelId = modelId,
                TargetRigId = rig.Id,
                TargetRigSignature = runtimeSignature,
            };
            string projectPath = Path.Combine(
                projectDirectory,
                "fingerprint.dlraproj");
            ProjectSerializer.SaveAtomic(
                DlraProject.Create("Fingerprint validation") with
                {
                    Assets = [asset],
                    Models = [model],
                    AnimationSources = [source],
                    AnimationVariants = [variant],
                },
                projectPath);

            InvalidDataException error = await Assert.ThrowsAsync<
                InvalidDataException>(() =>
                    ProjectExportCommand.RunAsync(
                        [
                            projectPath,
                            installDirectory,
                            Path.Combine(directory, "output"),
                            variantId.ToString(),
                            "body",
                        ],
                        new JsonSerializerOptions(),
                        CancellationToken.None));
            Assert.Contains(
                "fingerprint",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }

    private static byte[] CreateAnimatedModelFbx()
    {
        FbxNode root = Model(1, "generic_root", "LimbNode");
        FbxNode meshModel = Model(2, "generic_mesh", "Mesh");
        FbxNode geometry = Node(
            "Geometry",
            [10L, "Geometry::GenericMesh", "Mesh"],
            Node(
                "Vertices",
                [ImmutableArray.Create(
                    0.0, 0.0, 0.0,
                    1.0, 0.0, 0.0,
                    0.0, 1.0, 0.0)]),
            Node(
                "PolygonVertexIndex",
                [ImmutableArray.Create(0, 1, -3)]));
        FbxNode stack = Node(
            "AnimationStack",
            [40L, "AnimStack::GenericTake", string.Empty],
            Node(
                "Properties70",
                [],
                Property70("LocalStart", 0L),
                Property70("LocalStop", FrameTick)));
        FbxNode layer = Node(
            "AnimationLayer",
            [100L, "AnimLayer::Base", string.Empty]);
        FbxNode curveNode = Node(
            "AnimationCurveNode",
            [20L, "AnimationCurveNode::Translation", string.Empty]);
        FbxNode curve = Node(
            "AnimationCurve",
            [30L, "AnimationCurve::X", string.Empty],
            Node(
                "KeyTime",
                [ImmutableArray.Create(0L, FrameTick)]),
            Node(
                "KeyValueFloat",
                [ImmutableArray.Create(0.0f, 2.0f)]));
        return WriteDocument(
        [
            Node(
                "GlobalSettings",
                [],
                Node(
                    "Properties70",
                    [],
                    Property70("UnitScaleFactor", 100.0),
                    Property70("CoordAxis", 0),
                    Property70("CoordAxisSign", 1),
                    Property70("UpAxis", 1),
                    Property70("UpAxisSign", 1),
                    Property70("FrontAxis", 2),
                    Property70("FrontAxisSign", 1),
                    Property70("TimeMode", 6))),
            Node(
                "Objects",
                [],
                root,
                meshModel,
                geometry,
                stack,
                layer,
                curveNode,
                curve),
            Node(
                "Connections",
                [],
                Connection("OO", 10, 2),
                Connection("OO", 2, 1),
                Connection("OO", 100, 40),
                Connection("OO", 20, 100),
                Connection("OP", 20, 1, "Lcl Translation"),
                Connection("OP", 30, 20, "d|X")),
        ]);
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
                Property70("Lcl Translation", 0.0, 0.0, 0.0),
                Property70("Lcl Rotation", 0.0, 0.0, 0.0),
                Property70("Lcl Scaling", 1.0, 1.0, 1.0)));

    private static FbxNode Property70(
        string name,
        params object[] values) =>
        Node("P", [name, name, string.Empty, "A", .. values]);

    private static FbxNode Connection(
        string kind,
        long childId,
        long parentId,
        params object[] metadata) =>
        Node("C", [kind, childId, parentId, .. metadata]);

    private static FbxNode Node(
        string name,
        object[] properties,
        params FbxNode[] children) =>
        new(
            name,
            properties.Select(Property).ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);

    private static FbxProperty Property(object value) =>
        new(
            value switch
            {
                long => 'L',
                int => 'I',
                float => 'F',
                double => 'D',
                string => 'S',
                ImmutableArray<int> => 'i',
                ImmutableArray<float> => 'f',
                ImmutableArray<long> => 'l',
                ImmutableArray<double> => 'd',
                _ => throw new ArgumentException(
                    $"Unsupported generic FBX fixture property {value.GetType().Name}."),
            },
            value);

    private static byte[] WriteDocument(IReadOnlyList<FbxNode> nodes)
    {
        using var stream = new MemoryStream();
        stream.Write("Kaydara FBX Binary  \0\u001a\0"u8);
        WriteUInt32(stream, 7400);
        foreach (FbxNode node in nodes)
        {
            WriteNode(stream, node);
        }
        stream.Write(new byte[13]);
        return stream.ToArray();
    }

    private static void WriteNode(MemoryStream stream, FbxNode node)
    {
        byte[] name = Encoding.UTF8.GetBytes(node.Name);
        byte[] properties = node.Properties
            .SelectMany(WriteProperty)
            .ToArray();
        long start = stream.Position;
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)node.Properties.Length));
        WriteUInt32(stream, checked((uint)properties.Length));
        stream.WriteByte(checked((byte)name.Length));
        stream.Write(name);
        stream.Write(properties);
        foreach (FbxNode child in node.Children)
        {
            WriteNode(stream, child);
        }
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
            case long value:
                WriteInt64(stream, value);
                break;
            case int value:
                WriteUInt32(stream, unchecked((uint)value));
                break;
            case float value:
                WriteUInt32(
                    stream,
                    BitConverter.SingleToUInt32Bits(value));
                break;
            case double value:
                WriteInt64(stream, BitConverter.DoubleToInt64Bits(value));
                break;
            case string value:
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                WriteUInt32(stream, checked((uint)bytes.Length));
                stream.Write(bytes);
                break;
            case ImmutableArray<int> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.SelectMany(static value =>
                    {
                        byte[] bytes = new byte[sizeof(int)];
                        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                        return bytes;
                    }).ToArray());
                break;
            case ImmutableArray<float> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.SelectMany(static value =>
                        BitConverter.GetBytes(value)).ToArray());
                break;
            case ImmutableArray<long> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.SelectMany(static value =>
                        BitConverter.GetBytes(value)).ToArray());
                break;
            case ImmutableArray<double> values:
                WriteArray(
                    stream,
                    values.Length,
                    values.SelectMany(static value =>
                        BitConverter.GetBytes(value)).ToArray());
                break;
            default:
                throw new ArgumentException(
                    "Unsupported generic FBX fixture property payload.");
        }
        return stream.ToArray();
    }

    private static void WriteArray(
        Stream stream,
        int count,
        byte[] payload)
    {
        WriteUInt32(stream, checked((uint)count));
        WriteUInt32(stream, 0);
        WriteUInt32(stream, checked((uint)payload.Length));
        stream.Write(payload);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
