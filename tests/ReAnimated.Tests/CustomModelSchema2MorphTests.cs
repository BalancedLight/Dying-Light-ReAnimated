using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class CustomModelSchema2MorphTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void PortableMorphInventoryReferencesOfficialCompilerPayload()
    {
        ImmutableArray<Dl1PortableMorphResource> inventory =
            MainWindowViewModel.CreateCompiledMorphInventory(
                CreateMorphModel());

        Dl1PortableMorphResource resource = Assert.Single(inventory);
        Assert.Equal("0000-generic_smile.morph.json", resource.RelativePath);
        using JsonDocument json = JsonDocument.Parse(resource.Payload);
        JsonElement root = json.RootElement;
        Assert.Equal("generic_smile", root.GetProperty("Name").GetString());
        Assert.Equal(
            $"0x{Dl1NameHash.Compute("generic_smile"):X8}",
            root.GetProperty("descriptorHash").GetString());
        Assert.Equal(
            "embedded-in-official-compiler-model-rpack",
            root.GetProperty("compiledPayload").GetString());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void SchemaOnePackageMigratesToSchemaTwoDefaults()
    {
        FbxModelAuthoringImportResult model = CreateMorphModel();
        CustomModelDocument legacySource = model.Package.Document with
        {
            MorphChannels = [],
            MorphSignature = CustomModelDocument.EmptyMorphSignature,
        };
        var package = new CustomModelPackage(
            legacySource,
            model.Package.SourceFbx,
            model.Package.TexturePayloads);
        byte[] currentPackage = CustomModelPackageSerializer.Serialize(package).ToArray();
        byte[] legacyManifest;
        using (var input = new ZipArchive(
                   new MemoryStream(currentPackage),
                   ZipArchiveMode.Read))
        {
            using Stream manifest = input.GetEntry(
                CustomModelPackage.ManifestEntryPath)!.Open();
            JsonObject root = JsonNode.Parse(manifest)!.AsObject();
            root["schemaVersion"] = 1;
            root.Remove("morphSignature");
            root.Remove("authoredHelpers");
            root.Remove("camera");
            root.Remove("morphChannels");
            legacyManifest = JsonSerializer.SerializeToUtf8Bytes(root);
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "generic-schema-one.dlrmodel");
            using (var output = new ZipArchive(
                       File.Create(path),
                       ZipArchiveMode.Create))
            {
                WriteEntry(
                    output,
                    CustomModelPackage.ManifestEntryPath,
                    legacyManifest);
                WriteEntry(
                    output,
                    CustomModelPackage.SourceFbxEntryPath,
                    package.SourceFbx.AsSpan());
            }

            CustomModelPackage migrated = CustomModelPackageSerializer.Load(path);

            Assert.Equal(
                CustomModelDocument.CurrentSchemaVersion,
                migrated.Document.SchemaVersion);
            Assert.Equal(
                CustomModelDocument.EmptyMorphSignature,
                migrated.Document.MorphSignature);
            Assert.Empty(migrated.Document.AuthoredHelpers);
            Assert.Empty(migrated.Document.MorphChannels);
            Assert.Null(migrated.Document.Camera.ActivePreviewNodeName);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelAuthoring")]
    public void HelpersRemainSeparateAndEyeCameraPromotionIsExplicit()
    {
        CustomModelDocument source = CreateMorphModel().Package.Document;
        ImmutableArray<CustomModelBone> importedBones = source.Bones;
        string importedSignature = source.RigSignature;

        CustomModelDocument withPreviewCamera =
            CustomModelHelperAuthoring.DuplicateAsHelper(
                source,
                sourceNodeIndex: 0,
                CustomModelAuthoredHelperKind.Camera,
                "PreviewCamera");
        CustomModelAuthoredHelper preview = Assert.Single(
            withPreviewCamera.AuthoredHelpers);

        Assert.Equal(importedBones, withPreviewCamera.Bones);
        Assert.Equal(0, preview.ParentNodeIndex);
        Assert.Equal(TransformTRS.Identity, preview.LocalTransform);
        Assert.NotEqual(importedSignature, withPreviewCamera.RigSignature);
        Assert.False(withPreviewCamera.HasGameReadyEyeCamera);

        var offset = new TransformTRS(
            new Vector3D(0.1, 0.2, 0.3),
            QuaternionD.FromAxisAngle(Vector3D.UnitY, 0.25),
            Vector3D.One);
        CustomModelDocument moved = CustomModelHelperAuthoring.SetLocalTransform(
            withPreviewCamera,
            preview.Id,
            offset);
        Assert.Equal(offset.Normalized(), moved.AuthoredHelpers[0].LocalTransform);

        CustomModelDocument promoted =
            CustomModelHelperAuthoring.CreateEyeCameraHelper(
                moved,
                sourceNodeIndex: source.Bones.Length);
        Assert.Equal("EyeCamera", promoted.Camera.ActivePreviewNodeName);
        Assert.True(promoted.HasGameReadyEyeCamera);
        Assert.Equal(
            source.Bones.Length,
            promoted.AuthoredHelpers[1].ParentNodeIndex);
        Assert.Equal(TransformTRS.Identity, promoted.AuthoredHelpers[1].LocalTransform);

        InvalidOperationException collision = Assert.Throws<InvalidOperationException>(
            () => CustomModelHelperAuthoring.CreateEyeCameraHelper(promoted, 0));
        Assert.Contains("Choose the existing helper", collision.Message, StringComparison.Ordinal);
        CustomModelDocument explicitlySelected =
            CustomModelHelperAuthoring.SelectPreviewCamera(promoted, "EyeCamera");
        Assert.Equal("EyeCamera", explicitlySelected.Camera.ActivePreviewNodeName);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelMorph")]
    public async Task SourceMshWritesNamedFloat3MorphChunkAndPreviewUsesExpandedDeltas()
    {
        FbxModelAuthoringImportResult model = CreateMorphModel();
        Dl1PreparedAuthoredRig authored = Dl1CustomModelRigPreparer.Prepare(model);
        MorphChannelDefinition authoredMorph = Assert.Single(
            authored.Contract.MorphChannels);
        Assert.Equal("generic_smile", authoredMorph.Name);
        Assert.Equal("generic_smile", Assert.Single(
            authored.PreviewRig.MorphChannels).Name);
        Assert.Equal(64, authored.Contract.MorphFingerprint.Length);

        CustomModelPreviewPayload preview = CustomModelPreviewAdapter.Create(
            model,
            clip: null,
            frame: 0,
            mode: CustomModelPreviewMode.Dl1Output);
        ReAnimated.Renderer.D3D11.MorphTargetRenderData previewMorph =
            Assert.Single(Assert.Single(preview.Meshes).MorphTargets);
        Assert.Equal("generic_smile", previewMorph.Name);
        Assert.Equal(
            new Vector3(0.12345f, 0.0f, 0.0f),
            previewMorph.PositionDeltas.Span[0]);
        Assert.Equal(
            new Vector3(0.0f, 0.25f, 0.0f),
            previewMorph.PositionDeltas.Span[1]);
        Assert.Equal(
            new Vector3(0.0f, 0.0f, -0.5f),
            previewMorph.PositionDeltas.Span[2]);

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1SourceModelBuildResult build = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = model,
                    OutputDirectory = directory,
                    ResourceName = "generic_morph_model",
                });
            byte[] msh = await File.ReadAllBytesAsync(build.SourceMshPath);
            SourceChunk lod = Assert.Single(ReadChunks(msh),
                static chunk => chunk.Id == 0x100);
            Assert.Equal(
                1u,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    msh.AsSpan(lod.PayloadOffset + 12, 4)));

            SourceChunk morphChunk = Assert.Single(ReadChunks(msh),
                static chunk => chunk.Id == 0x104);
            ReadOnlySpan<byte> payload = msh.AsSpan(
                morphChunk.PayloadOffset,
                morphChunk.PayloadSize);
            int nameLength = payload[..64].IndexOf((byte)0);
            Assert.Equal(
                "generic_smile",
                Encoding.UTF8.GetString(payload[..nameLength]));
            Assert.Equal(64 + (3 * 12), payload.Length);
            Assert.Equal(0.12345f, BinaryPrimitives.ReadSingleLittleEndian(payload[64..68]));
            Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(payload[80..84]));
            Assert.Equal(-0.5f, BinaryPrimitives.ReadSingleLittleEndian(payload[96..100]));

            using JsonDocument manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(build.ManifestPath));
            Assert.Equal(
                1,
                manifest.RootElement.GetProperty("counts")
                    .GetProperty("morphChannels").GetInt32());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public async Task SourceMshRejectsMorphDeltaOutsidePcHalfRange()
    {
        FbxModelAuthoringImportResult model = CreateMorphModel();
        FbxModelSurface surface = model.Surfaces[0] with
        {
            MorphTargets =
            [
                model.Surfaces[0].MorphTargets[0] with
                {
                    PositionDeltas =
                    [
                        new Vector3D(70_000.0, 0.0, 0.0),
                        Vector3D.Zero,
                        Vector3D.Zero,
                    ],
                },
            ],
        };
        FbxModelAuthoringImportResult invalid = model with
        {
            Surfaces = [surface],
        };
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => Dl1SourceModelWriter.WriteAsync(
                    new Dl1SourceModelBuildRequest
                    {
                        Model = invalid,
                        OutputDirectory = directory,
                        ResourceName = "generic_invalid_morph",
                    }));
            Assert.Contains("HALF4", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelMorph")]
    public void CompilerVerifierRequiresNamesAndPcHalfQuantizedDeltas()
    {
        FbxModelAuthoringImportResult source = CreateMorphModel();
        var target = new CompiledMorphTargetDeltas(
            0,
            0,
            [
                new Vector3((float)(Half)0.12345f, 0.0f, 0.0f),
                new Vector3(0.0f, 0.25f, 0.0f),
                new Vector3(0.0f, 0.0f, -0.5f),
            ]);
        CompiledMeshGeometryDocument valid = CreateCompiledMorphGeometry(target);

        Dl1OfficialModelCompiler.ValidateCompiledMorphOutput(source, valid);

        CompiledMeshGeometryDocument invalid = CreateCompiledMorphGeometry(
            target with
            {
                PositionDeltas =
                [
                new Vector3(0.12345f, 0.0f, 0.0f),
                    new Vector3(0.0f, 0.25f, 0.0f),
                    new Vector3(0.0f, 0.0f, -0.5f),
                ],
            });
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Dl1OfficialModelCompiler.ValidateCompiledMorphOutput(
                source,
                invalid));
        Assert.Contains("PC HALF4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void AuthoredHelpersReparentByStableNameAcrossReimport()
    {
        CustomModelDocument original = CreateMorphModel().Package.Document;
        original = CustomModelHelperAuthoring.CreateEyeCameraHelper(original, 0);
        CustomModelBone insertedRoot = original.Bones[0] with
        {
            Index = 0,
            FbxObjectId = 2,
            Name = "generic_parent",
            ParentIndex = -1,
            IsWeighted = false,
        };
        CustomModelBone remappedSource = original.Bones[0] with
        {
            Index = 1,
            ParentIndex = 0,
        };
        CustomModelDocument replacement = original with
        {
            Bones = [insertedRoot, remappedSource],
            AuthoredHelpers = [],
            Camera = new CustomModelCameraMetadata(),
            RigSignature = CustomModelContractSignatures.ComputeRig(
                [insertedRoot, remappedSource]),
            LastBuildReceipt = null,
        };

        CustomModelDocument layered =
            FbxModelAuthoringImporter.ReapplyAuthoredHierarchyLayer(
                original,
                replacement);

        CustomModelAuthoredHelper helper = Assert.Single(layered.AuthoredHelpers);
        Assert.Equal(1, helper.ParentNodeIndex);
        Assert.Equal("EyeCamera", helper.Name);
        Assert.Equal("EyeCamera", layered.Camera.ActivePreviewNodeName);
        Assert.Equal(
            CustomModelContractSignatures.ComputeRig(layered.CreateEffectiveBones()),
            layered.RigSignature);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void ReimportFailsPreviewWhenAuthoredHelperParentVanished()
    {
        CustomModelDocument original = CustomModelHelperAuthoring.DuplicateAsHelper(
            CreateMorphModel().Package.Document,
            0,
            CustomModelAuthoredHelperKind.Helper,
            "generic_attachment");
        CustomModelBone replacementRoot = original.Bones[0] with
        {
            FbxObjectId = 2,
            Name = "replacement_root",
        };
        CustomModelDocument replacement = original with
        {
            Bones = [replacementRoot],
            AuthoredHelpers = [],
            Camera = new CustomModelCameraMetadata(),
            RigSignature = CustomModelContractSignatures.ComputeRig(
                [replacementRoot]),
            LastBuildReceipt = null,
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            FbxModelAuthoringImporter.ReapplyAuthoredHierarchyLayer(
                original,
                replacement));

        Assert.Contains("generic_root", error.Message, StringComparison.Ordinal);
        Assert.Contains("absent", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 900_000)]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1MorphCompiler")]
    public async Task GenericMorphModelCompilesAndDecodesWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "DLR_RUN_INSTALLED_MODEL_COMPILER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string? compiler = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable();
        Assert.True(File.Exists(compiler),
            "The Dying Light Developer Tools compiler was not discovered.");
        Dl1InstallLocation? install = SteamInstallDiscovery.Discover()
            .FirstOrDefault(static candidate => candidate.IsValid);
        Assert.NotNull(install);
        string data0Pak = Path.Combine(install!.InstallPath, "DW", "Data0.pak");
        Assert.True(File.Exists(data0Pak),
            $"The retail compiler bootstrap is missing at {data0Pak}.");
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1OfficialModelCompilerResult result =
                await Dl1OfficialModelCompiler.CompileAsync(
                    new Dl1OfficialModelCompilerRequest
                    {
                        Model = CreateMorphModel(),
                        CompilerExecutablePath = compiler!,
                        RetailData0PakPath = data0Pak,
                        OutputRpackPath = Path.Combine(
                            directory,
                            "generic_morph_model_pc.rpack"),
                        ResourceName = "generic_morph_model",
                    });
            Assert.Equal(
                CustomModelBuildState.CompilerValidated,
                result.BuildReceipt.State);
            Assert.True(File.Exists(result.OutputRpackPath));
            Assert.True(File.Exists(result.CompiledMeshObjectPath));
            Assert.True(File.Exists(result.ReceiptPath));

            string receiptDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "DLReAnimated",
                "ValidationReceipts");
            Directory.CreateDirectory(receiptDirectory);
            File.Copy(
                result.ReceiptPath,
                Path.Combine(
                    receiptDirectory,
                    $"installed-generic-morph-compiler-{result.BuildReceipt.OutputManifestFingerprint}.json"),
                overwrite: true);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static FbxModelAuthoringImportResult CreateMorphModel()
    {
        byte[] sourceBytes = "generic custom-model source"u8.ToArray();
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes))
            .ToLowerInvariant();
        string morphSignature = Convert.ToHexString(SHA256.HashData(
                "generic-smile-morph-v1"u8))
            .ToLowerInvariant();
        Guid modelId = new("c75bd69b-0e6f-5cad-bfa2-338104036629");
        Guid materialId = new("87f89f0a-a594-5fb6-ac4d-10a693aa061e");
        uint descriptor = Dl1NameHash.Compute("generic_smile");
        var sourceIdentity = new CustomModelSourceIdentity
        {
            OriginalFileName = "generic-model.fbx",
            ContentSha256 = sourceSha256,
            FbxVersion = 7400,
        };
        var bone = new CustomModelBone
        {
            Index = 0,
            FbxObjectId = 1,
            Name = "generic_root",
            ParentIndex = -1,
            LocalBindTransform = TransformTRS.Identity,
            ExactLocalBindMatrix = TransformMatrix.Identity,
            Kind = BoneKind.Root,
            IsWeighted = true,
        };
        var morph = new CustomModelMorphChannel
        {
            Index = 0,
            BlendShapeChannelObjectId = 20,
            ShapeObjectId = 30,
            Name = "generic_smile",
            DescriptorHash = descriptor,
            GeometryObjectIds = [10],
        };
        var document = new CustomModelDocument
        {
            ModelId = modelId,
            Name = "Generic morph model",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = sourceIdentity,
            RigSignature = sourceSha256,
            MorphSignature = morphSignature,
            Bones = [bone],
            MorphChannels = [morph],
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "GenericMesh",
                    GeometryObjectId = 10,
                    ModelObjectId = 11,
                    ControlPointCount = 3,
                    PolygonCount = 1,
                    TriangleCount = 1,
                    ExpandedVertexCount = 3,
                    MaterialSlotCount = 1,
                    MaximumSourceInfluences = 1,
                    MaximumRetainedInfluences = 1,
                    RequiredPaletteSize = 1,
                    SourceMaterialIndices = [0],
                },
            ],
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = materialId,
                    Name = "GenericMaterial",
                },
            ],
        };
        RigDefinition rig = document.CreateRigDefinition();
        document = document with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(
                document.Bones),
        };
        rig = document.CreateRigDefinition();
        var vertices = ImmutableArray.Create(
            Vertex(new Vector3D(0.0, 0.0, 0.0), 0.0, 0.0),
            Vertex(new Vector3D(1.0, 0.0, 0.0), 1.0, 0.0),
            Vertex(new Vector3D(0.0, 1.0, 0.0), 0.0, 1.0));
        var surface = new FbxModelSurface(
            "generic-surface",
            "GenericMesh",
            materialId,
            vertices,
            [0u, 1u, 2u],
            [0],
            [TransformMatrix.Identity],
            IsSkinned: true)
        {
            MorphTargets =
            [
                new FbxModelMorphTarget(
                    "generic_smile",
                    descriptor,
                    20,
                    30,
                    [
                        new Vector3D(0.12345, 0.0, 0.0),
                        new Vector3D(0.0, 0.25, 0.0),
                        new Vector3D(0.0, 0.0, -0.5),
                    ]),
            ],
        };
        var package = new CustomModelPackage(
            document,
            sourceBytes.ToImmutableArray(),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
        package.Document.Validate();
        return new FbxModelAuthoringImportResult(
            package,
            rig,
            [surface],
            ImmutableDictionary<Guid, AnimationClip>.Empty,
            Inspection: null!);
    }

    private static CompiledMeshGeometryDocument CreateCompiledMorphGeometry(
        CompiledMorphTargetDeltas target)
    {
        var surface = new CompiledMeshSurface(
            EntityIndex: 2,
            Name: "generic_morph_model_GenericMesh_p00",
            LodIndex: 0,
            DeclarationGroupIndex: 0,
            VertexByteOffset: 0,
            IndexByteOffset: 0,
            VertexLayout: null!,
            Vertices: Array.Empty<CompiledVertex>(),
            Indices: Array.Empty<ushort>(),
            Submeshes: Array.Empty<CompiledMeshSubmesh>());
        var binding = new CompiledNodeMorphBinding(
            EntityIndex: 2,
            LodIndex: 0,
            VertexCount: 3,
            DeltaByteStride: 8,
            PayloadByteOffset: 0,
            DeltaFormat: CompiledMorphDeltaFormat.PcHalf4,
            MorphChannelIndexes: [0],
            TargetDeltas: [target]);
        return new CompiledMeshGeometryDocument(
            Array.Empty<CompiledVertexLayout>(),
            [surface],
            Array.Empty<string>(),
            CompiledMaterialDatabase.Empty,
            [new CompiledMorphChannel(0, "generic_smile")],
            [binding],
            Array.Empty<CompactMeshDiagnostic>());
    }

    private static FbxModelVertex Vertex(
        Vector3D position,
        double u,
        double v) =>
        new(
            position,
            Vector3D.UnitZ,
            u,
            v,
            [0],
            [1.0]);

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        ReadOnlySpan<byte> bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    private static ImmutableArray<SourceChunk> ReadChunks(byte[] data)
    {
        var result = ImmutableArray.CreateBuilder<SourceChunk>();
        ReadChunk(data, 0, result);
        return result.ToImmutable();
    }

    private static void ReadChunk(
        byte[] data,
        int offset,
        ImmutableArray<SourceChunk>.Builder result)
    {
        if (offset < 0 || offset + 16 > data.Length)
        {
            throw new InvalidDataException("Source-MSH chunk header is truncated.");
        }

        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            data.AsSpan(offset + 8, 4)));
        int payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            data.AsSpan(offset + 12, 4)));
        int end = checked(offset + size);
        int childOffset = checked(offset + 16 + payloadSize);
        if (size < 16 + payloadSize || end > data.Length)
        {
            throw new InvalidDataException("Source-MSH chunk bounds are invalid.");
        }

        result.Add(new SourceChunk(id, offset + 16, payloadSize));
        while (childOffset < end)
        {
            int childSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(childOffset + 8, 4)));
            ReadChunk(data, childOffset, result);
            childOffset = checked(childOffset + childSize);
        }

        if (childOffset != end)
        {
            throw new InvalidDataException("Source-MSH child chunks do not fill their parent.");
        }
    }

    private readonly record struct SourceChunk(
        uint Id,
        int PayloadOffset,
        int PayloadSize);
}
