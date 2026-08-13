using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class CustomModelAuthoringTests
{
    private static readonly string[] LegacyDdsFourCcValues = ["DXT1", "DXT3", "DXT5"];

    private const string FbxControlId = "custom-model-fbx-control";
    private const string GlbControlId = "custom-model-glb-orientation-control";
    private readonly ITestOutputHelper _output;

    public CustomModelAuthoringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ModelsWorkspaceImportCommandsOpenTheirPickersAndReportFailures()
    {
        var dialogs = new ModelPickerProbe();
        var statuses = new List<string>();
        using var viewModel = new ModelsWorkspaceViewModel(
            dialogs,
            statuses.Add,
            static _ => Task.CompletedTask,
            static () => null);

        Assert.True(viewModel.ImportFbxCommand.CanExecute(null));
        await viewModel.ImportFbxCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.FbxPickerCalls);
        Assert.Contains(
            "selection canceled",
            viewModel.BuildStatus,
            StringComparison.OrdinalIgnoreCase);

        dialogs.PackagePickerException =
            new InvalidOperationException("simulated picker failure");
        Assert.True(viewModel.OpenPackageCommand.CanExecute(null));
        await viewModel.OpenPackageCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.PackagePickerCalls);
        Assert.Contains(
            "File picker failed: simulated picker failure",
            viewModel.BuildStatus,
            StringComparison.Ordinal);
        Assert.Contains(
            statuses,
            static status => status.Contains(
                "Choose a binary FBX model",
                StringComparison.Ordinal));
        Assert.Contains(
            statuses,
            static status => status.Contains(
                "File picker failed",
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void ModelPackageRoundTripsDeterministicallyAndRejectsTamperedSource()
    {
        byte[] fbx = "Kaydara FBX Binary  synthetic-user-source"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(fbx)).ToLowerInvariant();
        Guid modelId = new("e98fc7a5-a297-5e02-a384-d43538623509");
        var document = new CustomModelDocument
        {
            ModelId = modelId,
            Name = "Synthetic prop",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            RigSignature = hash,
            Bones =
            [
                new CustomModelBone
                {
                    Index = 0,
                    FbxObjectId = 17,
                    Name = "custom_root",
                    ParentIndex = -1,
                    LocalBindTransform = new TransformTRS(
                        new Vector3D(1.5, 2.5, 3.5),
                        QuaternionD.Identity,
                        Vector3D.One),
                    ExactLocalBindMatrix = TransformMatrix.CreateTranslation(
                        new Vector3D(1.5, 2.5, 3.5)),
                    Kind = BoneKind.Root,
                    IsWeighted = true,
                },
            ],
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "Synthetic",
                    ControlPointCount = 3,
                    PolygonCount = 1,
                    TriangleCount = 1,
                    ExpandedVertexCount = 3,
                    MaterialSlotCount = 1,
                },
            ],
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = new Guid("22c852ee-2cbd-579a-8a08-73a9335738fd"),
                    Name = "Default",
                },
            ],
            AnimationClips =
            [
                new CustomModelAnimationClip
                {
                    Id = new Guid("5826e63d-9fde-5098-8d46-a6da45fca314"),
                    FbxObjectId = 99,
                    SourceName = "Take 001",
                    DisplayName = "custom_idle",
                    FrameRate = new FrameRate(30000, 1001),
                    StartFrame = 0,
                    FrameCount = 42,
                    RootMotionMode = Dl1RootMotionMode.Bip01,
                    RootBoneName = "custom_root",
                    SourceFingerprint = hash,
                },
            ],
        };
        var package = new CustomModelPackage(
            document,
            fbx.ToImmutableArray(),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string first = Path.Combine(directory, "first.dlrmodel");
            string second = Path.Combine(directory, "second.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(package, first);
            CustomModelPackage loaded = CustomModelPackageSerializer.Load(first);
            CustomModelPackageSerializer.SaveAtomic(loaded, second);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
            Assert.Equal(modelId, loaded.Document.ModelId);
            Assert.Equal(hash, loaded.Document.Source.ContentSha256);
            Assert.Equal(new Vector3D(1.5, 2.5, 3.5),
                loaded.Document.Bones[0].LocalBindTransform.Translation);
            Assert.Equal(30_000, loaded.Document.AnimationClips[0].FrameRate.Numerator);
            Assert.Equal(1_001, loaded.Document.AnimationClips[0].FrameRate.Denominator);
            Assert.Equal(Dl1RootMotionMode.Bip01, loaded.Document.AnimationClips[0].RootMotionMode);
            Assert.Equal("custom_model", loaded.Document.BuildSettings.ResourceName);
            Assert.Equal("default", loaded.Document.BuildSettings.SurfaceName);
            Assert.True(loaded.Document.BuildSettings.FlipTextureCoordinateV);

            Dl1PreparedMaterialSet materials = Dl1CustomMaterialWriter.Prepare(
                loaded,
                "custom_model",
                CancellationToken.None);
            Assert.Equal("custom_model_Default.mat", Assert.Single(materials.MaterialReferences));
            Assert.Equal(2, materials.Files.Count);
            string dmt = System.Text.Encoding.UTF8.GetString(
                materials.Files["custom_model_Default.dmt"]);
            Assert.Equal(
                "<MaterialData>\r\n" +
                "<TemplateData>\r\n" +
                "<template>standard</template>\r\n" +
                "<nrm_0_tex>\"\"</nrm_0_tex>\r\n" +
                "<spc_0_tex>\"\"</spc_0_tex>\r\n" +
                "<dif_0_tex>\"custom_model_Default.dds\"</dif_0_tex>\r\n" +
                "</TemplateData>\r\n" +
                "</MaterialData>\r\n",
                dmt);
            AssertLegacyDds(
                materials.Files["custom_model_Default.dds"],
                "DXT1");
            Assert.DoesNotContain("custom_model_Default_nrm.dds", materials.Files.Keys);
            Assert.DoesNotContain("custom_model_Default_shn.dds", materials.Files.Keys);

            using var archive = ZipFile.OpenRead(first);
            ZipArchiveEntry manifestEntry = Assert.Single(
                archive.Entries,
                static entry => entry.FullName == CustomModelPackage.ManifestEntryPath);
            using var manifest = JsonDocument.Parse(manifestEntry.Open());
            Assert.Equal(CustomModelDocument.CurrentFormat,
                manifest.RootElement.GetProperty("format").GetString());
            JsonElement serializedBone = manifest.RootElement.GetProperty("bones")[0];
            JsonElement serializedTranslation = serializedBone
                .GetProperty("localBindTransform")
                .GetProperty("translation");
            Assert.Equal(1.5, serializedTranslation.GetProperty("x").GetDouble());
            Assert.False(serializedTranslation.TryGetProperty("length", out _));
            Assert.False(serializedTranslation.TryGetProperty("isFinite", out _));
            JsonElement serializedMatrix = serializedBone.GetProperty("exactLocalBindMatrix");
            Assert.Equal(1.5, serializedMatrix.GetProperty("m14").GetDouble());
            Assert.Equal(2.5, serializedMatrix.GetProperty("m24").GetDouble());
            Assert.Equal(3.5, serializedMatrix.GetProperty("m34").GetDouble());
            Assert.False(serializedMatrix.TryGetProperty("translation", out _));
            Assert.False(serializedMatrix.TryGetProperty("linearDeterminant", out _));
            JsonElement serializedRate = manifest.RootElement
                .GetProperty("animationClips")[0]
                .GetProperty("frameRate");
            Assert.Equal(30_000, serializedRate.GetProperty("numerator").GetInt32());
            Assert.Equal(1_001, serializedRate.GetProperty("denominator").GetInt32());
            Assert.False(serializedRate.TryGetProperty("framesPerSecond", out _));
            Assert.True(manifest.RootElement
                .GetProperty("buildSettings")
                .GetProperty("flipTextureCoordinateV")
                .GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void ModelDocumentRejectsNegativeSourceMaterialIndices()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var document = new CustomModelDocument
        {
            Name = "Synthetic invalid material index",
            RigMode = CustomModelRigMode.StaticProp,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            RigSignature = hash,
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "Synthetic",
                    SourceMaterialIndices = [-1],
                },
            ],
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(document.Validate);
        Assert.Contains("source material indices", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void ModelCompilerContractUsesNarrowIsolatedResourceScripts()
    {
        const string resourceName = "external_model_control_player";
        const string virtualDirectory = "data/characters/dl_reanimated/imported/external_model_control_player";
        string resourceScript = Dl1OfficialModelCompiler.CreateResourceScript(
            resourceName,
            $"{virtualDirectory}/{resourceName}.msh",
            virtualDirectory,
            ["external_model_control_player_ControlMaterial.mat"],
            [
                "external_model_control_player_ControlMaterial.dds",
                "external_model_control_player_ControlMaterial_nrm.dds",
                "external_model_control_player_ControlMaterial_shn.dds",
            ]);
        Assert.Contains("configuration(cfg_common)", resourceScript, StringComparison.Ordinal);
        Assert.Contains("_MESH_", resourceScript, StringComparison.Ordinal);
        Assert.DoesNotContain("_MATERIAL_", resourceScript, StringComparison.Ordinal);
        Assert.Contains(
            "res( _TEXTURE_, \"external_model_control_player_ControlMaterial\", \"data/characters/dl_reanimated/imported/external_model_control_player/external_model_control_player_ControlMaterial.dds\", \"\", false);",
            resourceScript,
            StringComparison.Ordinal);
        Assert.Contains(resourceName, resourceScript, StringComparison.Ordinal);
        Assert.DoesNotContain(".chr", resourceScript, StringComparison.OrdinalIgnoreCase);

        string jobDirectory = TestPaths.Combine("model-compiler", "deadbeef");
        string workshopDirectory = Path.Combine(jobDirectory, "Workshop");
        string projectDirectory = Path.Combine(workshopDirectory, "project");
        ImmutableArray<ImmutableArray<string>> commands =
            Dl1OfficialModelCompiler.CreateCompilerCommands(
                TestPaths.Combine("tools", "ResPackCompilerConsole_x64_rwdi.exe"),
                "_DLReAnimatedModelImporter_deadbeef",
                workshopDirectory,
                Path.Combine(jobDirectory, "CompilerOutput"),
                Path.Combine(projectDirectory, "model_resource.rules"),
                Path.Combine(projectDirectory, "model_resources.rsrc"),
                virtualDirectory,
                "external_model_control_player_pc.rpack");
        Assert.Single(commands);
        Assert.Contains(commands[0], static value => value.StartsWith("-updatefromrscr=", StringComparison.Ordinal));
        Assert.Contains($"{virtualDirectory}/*.*", commands[0]);
        Assert.DoesNotContain(commands.SelectMany(static command => command), static value =>
            value.Contains("Dying Light Developer Tools\\DWData", StringComparison.OrdinalIgnoreCase));

        string resourceRules = Dl1OfficialModelCompiler.CreateResourceRules();
        Assert.DoesNotContain("*.mat", resourceRules, StringComparison.Ordinal);
        Assert.Contains("ResourceRule(\"*.dds\")", resourceRules, StringComparison.Ordinal);

        ImmutableArray<string> materialCommand =
            Dl1OfficialModelCompiler.CreateMaterialCompilerCommand(
                projectDirectory,
                workshopDirectory,
                virtualDirectory);
        Assert.Contains("local", materialCommand);
        Assert.Contains("dx11", materialCommand);
        Assert.Contains("project", materialCommand);
        Assert.DoesNotContain(projectDirectory, materialCommand);
        Assert.Contains($"{virtualDirectory}/*.dmt", materialCommand);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void ModelCompilerRejectsMissingTextureResources()
    {
        Rp6lResourceDescriptor[] complete =
        [
            new(0, "material_a", Rp6lResourceTypes.Texture, 0, 0, 0, []),
            new(1, "material_a_nrm", Rp6lResourceTypes.Texture, 0, 0, 0, []),
            new(2, "material_a_shn", Rp6lResourceTypes.Texture, 0, 0, 0, []),
        ];
        Dl1OfficialModelCompiler.ValidateCompiledTextureDependencies(
            complete,
            ["material_a.dds", "material_a_nrm.dds", "material_a_shn.dds"]);

        InvalidDataException missingTexture = Assert.Throws<InvalidDataException>(() =>
            Dl1OfficialModelCompiler.ValidateCompiledTextureDependencies(
                complete.Where(static resource => !string.Equals(
                    resource.Name,
                    "material_a_nrm",
                    StringComparison.OrdinalIgnoreCase)).ToArray(),
                ["material_a.dds", "material_a_nrm.dds", "material_a_shn.dds"]));
        Assert.Contains("material_a_nrm", missingTexture.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void Dl1NormalPackingUsesAlphaForTangentXAndGreenForTangentY()
    {
        byte[] rgbOpenGl =
        [
            128, 128, 255, 255,
            255, 128, 255, 255,
            128, 255, 255, 255,
        ];

        byte[] packed = Dl1CustomMaterialWriter.RepackNormalForDl1(
            rgbOpenGl,
            CustomModelNormalMapConvention.RgbOpenGl);

        Assert.Equal(new byte[] { 0, 128, 255, 128 }, packed[..4]);
        Assert.Equal(new byte[] { 0, 128, 255, 255 }, packed[4..8]);
        Assert.Equal(new byte[] { 0, 255, 255, 128 }, packed[8..12]);

        byte[] directX = Dl1CustomMaterialWriter.RepackNormalForDl1(
            rgbOpenGl,
            CustomModelNormalMapConvention.RgbDirectX);
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, directX[8..12]);

        Assert.Equal(
            packed,
            Dl1CustomMaterialWriter.RepackNormalForDl1(
                packed,
                CustomModelNormalMapConvention.Dl1AlphaGreen));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Dl1CustomMaterialWriter.RepackNormalForDl1(
                rgbOpenGl,
                (CustomModelNormalMapConvention)99));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void TextureSemanticMetadataRejectsInvalidColorAndNormalConventions()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var normal = new CustomModelTextureBinding
        {
            Id = new Guid("e1df8c7c-1f82-52de-94b6-b81116952a76"),
            Semantic = CustomModelTextureSemantic.Normal,
            SourceKind = CustomModelTextureSourceKind.UserOverride,
            ColorSpace = CustomModelTextureColorSpace.Linear,
            NormalMapConvention = CustomModelNormalMapConvention.RgbDirectX,
            DisplayName = "synthetic_normal.png",
            PackageEntryPath = $"textures/{hash}.png",
            OriginalReference = "synthetic_normal.png",
            ContentSha256 = hash,
            MediaType = "image/png",
        };

        CreateDocumentWithTexture(normal).Validate();

        Assert.Throws<ArgumentException>(() =>
            CreateDocumentWithTexture(normal with
            {
                ColorSpace = CustomModelTextureColorSpace.Srgb,
            }).Validate());
        Assert.Throws<ArgumentException>(() =>
            CreateDocumentWithTexture(normal with
            {
                Semantic = CustomModelTextureSemantic.BaseColor,
                ColorSpace = CustomModelTextureColorSpace.Srgb,
            }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateDocumentWithTexture(normal with
            {
                NormalMapConvention = (CustomModelNormalMapConvention)99,
            }).Validate());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void ModelCompilerValidatesMaterialAndTextureRecordsInsideAbdm()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            const string materialName = "synthetic_surface.mat";
            const string diffuseName = "synthetic_surface_Diffuse.dds";
            const string normalName = "synthetic_surface_Normal.dds";
            string databasePath = Path.Combine(directory, "local_dx11.mp");
            File.WriteAllBytes(
                databasePath,
                BuildSyntheticMaterialDatabase(
                    new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        [materialName] = [
                            diffuseName,
                            normalName,
                        ],
                    }));

            Dl1OfficialModelCompiler.ValidateCompiledMaterialDatabase(
                databasePath,
                [materialName],
                [diffuseName, normalName]);

            InvalidDataException missingMaterial = Assert.Throws<InvalidDataException>(() =>
                Dl1OfficialModelCompiler.ValidateCompiledMaterialDatabase(
                    databasePath,
                    ["missing_surface.mat"],
                    [diffuseName]));
            Assert.Contains("missing_surface.mat", missingMaterial.Message, StringComparison.Ordinal);

            InvalidDataException missingTexture = Assert.Throws<InvalidDataException>(() =>
                Dl1OfficialModelCompiler.ValidateCompiledMaterialDatabase(
                    databasePath,
                    [materialName],
                    [diffuseName, "missing_surface_nrm.dds"]));
            Assert.Contains("missing_surface_nrm.dds", missingTexture.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0.25, true, 0.75)]
    [InlineData(0.25, false, 0.25)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void SourceModelWriterConvertsFbxTextureOriginExactlyOnce(
        double sourceV,
        bool flip,
        double expectedV)
    {
        Assert.Equal(
            expectedV,
            Dl1SourceModelWriter.ConvertTextureCoordinateV(sourceV, flip),
            precision: 12);
        Assert.Throws<InvalidDataException>(() =>
            Dl1SourceModelWriter.ConvertTextureCoordinateV(double.NaN, flip));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void EmbeddedPngSignatureIsClassifiedWithoutUtf8Corruption()
    {
        ImmutableArray<byte> png =
        [
            0x89, 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A,
        ];
        Assert.Equal(".png", FbxModelAuthoringImporter.DetectTextureExtension(png, null));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPackage")]
    public void AUniqueEmbeddedBaseColorAtlasIsInferredOnlyForUntexturedMaterialSlots()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var source = new CustomModelTextureBinding
        {
            Id = new Guid("36cd3e74-d3f6-55fc-9737-793cd63c8cc4"),
            Semantic = CustomModelTextureSemantic.BaseColor,
            SourceKind = CustomModelTextureSourceKind.EmbeddedFbx,
            DisplayName = "shared_atlas",
            PackageEntryPath = $"textures/{hash}.png",
            ContentSha256 = hash,
            MediaType = "image/png",
        };
        var normal = source with
        {
            Id = new Guid("3a110df8-1968-5c4e-87f3-1897ad92d53f"),
            Semantic = CustomModelTextureSemantic.Normal,
        };
        var materials = ImmutableArray.CreateBuilder<CustomModelMaterial>();
        materials.Add(new CustomModelMaterial
        {
            Id = new Guid("42ac30ba-83c0-54f4-af90-47c629f31ddd"),
            Name = "Connected",
            Textures = [source],
        });
        materials.Add(new CustomModelMaterial
        {
            Id = new Guid("89bb0145-e9e5-5a6c-8e56-d1df41e8d2b5"),
            Name = "Untextured",
        });
        materials.Add(new CustomModelMaterial
        {
            Id = new Guid("59a68300-bb67-5cc0-aece-85661f95404f"),
            Name = "Normal only",
            Textures = [normal],
        });
        var diagnostics = ImmutableArray.CreateBuilder<CustomModelImportDiagnostic>();

        FbxModelAuthoringImporter.InferSingleEmbeddedBaseColorAtlas(materials, diagnostics);

        CustomModelTextureBinding inferred = Assert.Single(materials[1].Textures);
        Assert.Equal(CustomModelTextureSemantic.BaseColor, inferred.Semantic);
        Assert.Equal(source.ContentSha256, inferred.ContentSha256);
        Assert.Equal(source.PackageEntryPath, inferred.PackageEntryPath);
        Assert.NotEqual(source.Id, inferred.Id);
        Assert.Equal(normal, Assert.Single(materials[2].Textures));
        CustomModelImportDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("model_shared_embedded_base_color_atlas_inferred", diagnostic.Code);
        Assert.Equal("Untextured", diagnostic.Subject);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task CompilerObjectNormalizerLinksOpaquePayloadAndClearsOnlyCompilerTypeBit()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string objectPath = Path.Combine(directory, "synthetic.msh_obj");
            string outputPath = Path.Combine(directory, "synthetic_pc.rpack");
            byte[] compilerObject = BuildSyntheticCompilerObject();
            await File.WriteAllBytesAsync(objectPath, compilerObject);

            Rp6lCompilerObjectNormalizationResult result =
                await Rp6lCompilerObjectNormalizer.NormalizeAtomicAsync(
                    objectPath,
                    outputPath);
            Assert.Equal(1, result.ConvertedResourceCount);
            Assert.Equal("synthetic_mesh", Assert.Single(result.ResourceNames));

            Rp6lArchive archive = await Rp6lArchive.OpenAsync(outputPath);
            Rp6lResourceDescriptor resource = Assert.Single(archive.Resources);
            Assert.Equal(Rp6lResourceTypes.Mesh, resource.ResourceType);
            Assert.Equal("synthetic_mesh", resource.Name);
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 1024 * 1024,
                    MaximumMemoryEntryBytes = 1024 * 1024,
                    MaximumDiskBytes = 4 * 1024 * 1024,
                });
            Assert.Equal(
                new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
                await archive.ReadItemBytesAsync(
                    Assert.Single(resource.Items),
                    cache,
                    maximumBytes: 4));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task CompilerObjectLinkerCombinesCompilerMeshAndOrdinaryTextureUnits()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string meshObjectPath = Path.Combine(directory, "synthetic.msh_obj");
            string textureObjectPath = Path.Combine(directory, "synthetic.dds_obj");
            string outputPath = Path.Combine(directory, "synthetic_pc.rpack");
            await File.WriteAllBytesAsync(meshObjectPath, BuildSyntheticCompilerObject());
            await File.WriteAllBytesAsync(
                textureObjectPath,
                BuildSyntheticStandaloneObject(
                    "synthetic_texture",
                    Rp6lResourceTypes.Texture,
                    [0x12, 0x34, 0x56, 0x78]));

            Rp6lCompilerObjectNormalizationResult result =
                await Rp6lCompilerObjectNormalizer.LinkAtomicAsync(
                    [meshObjectPath, textureObjectPath],
                    outputPath);
            Assert.Equal(1, result.ConvertedResourceCount);

            Rp6lArchive archive = await Rp6lArchive.OpenAsync(outputPath);
            Rp6lResourceDescriptor mesh = Assert.Single(
                archive.Resources,
                static resource => resource.ResourceType == Rp6lResourceTypes.Mesh);
            Rp6lResourceDescriptor texture = Assert.Single(
                archive.Resources,
                static resource => resource.ResourceType == Rp6lResourceTypes.Texture);
            Assert.Equal("synthetic_mesh", mesh.Name);
            Assert.Equal("synthetic_texture", texture.Name);
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "link-cache"),
                    MaximumMemoryBytes = 1024 * 1024,
                    MaximumMemoryEntryBytes = 1024 * 1024,
                    MaximumDiskBytes = 4 * 1024 * 1024,
                });
            Assert.Equal(
                new byte[] { 0x12, 0x34, 0x56, 0x78 },
                await archive.ReadItemBytesAsync(
                    Assert.Single(texture.Items),
                    cache,
                    maximumBytes: 4));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [ExternalCorpusFact(Timeout = 240_000)]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCorpus")]
    public async Task ExternalModelControlImportsCustomRigTexturesAndEveryStack()
    {
        ExternalCorpusControl fbxControl = await RequireVerifiedControlAsync(FbxControlId);
        ExternalCorpusControl glbControl = await RequireVerifiedControlAsync(GlbControlId);
        string corpusPath = fbxControl.RequireExistingFile();
        string sourceHash = fbxControl.Sha256;
        FbxModelAuthoringImportResult result = await FbxModelAuthoringImporter.ImportFileAsync(corpusPath);

        Assert.NotNull(result.Rig);
        Assert.Equal(fbxControl.RequireInt32("rigBoneCount"), result.Rig!.BoneCount);
        Assert.Equal(fbxControl.RequireInt32("meshCount"), result.Package.Document.Meshes.Length);
        Assert.Equal(fbxControl.RequireInt32("animationClipCount"), result.Package.Document.AnimationClips.Length);
        Assert.Equal(
            fbxControl.RequireInt32("animationClipCount"),
            result.Package.Document.AnimationClips.Count(static clip => clip.Included));
        Assert.Equal(fbxControl.RequireInt32("animationClipCount"), result.AnimationClips.Count);
        Assert.NotEmpty(result.Surfaces);
        Assert.All(result.Surfaces, static surface =>
        {
            Assert.InRange(surface.Vertices.Length, 1, 65_535);
            Assert.InRange(surface.PaletteBoneIndices.Length, 0, 256);
            Assert.Equal(surface.PaletteBoneIndices.Length, surface.InverseBindMatrices.Length);
        });
        Assert.Contains(result.Package.Document.Diagnostics, static diagnostic =>
            diagnostic.Code == "model_skin_weights_reduced_to_top4");
        Assert.Contains(result.Package.Document.Materials.SelectMany(static material => material.Textures),
            static texture => texture.SourceKind == CustomModelTextureSourceKind.EmbeddedFbx);
        CustomModelTextureBinding[] embeddedBaseColors = result.Package.Document.Materials
            .SelectMany(static material => material.Textures)
            .Where(static texture =>
                texture.SourceKind == CustomModelTextureSourceKind.EmbeddedFbx &&
                texture.Semantic == CustomModelTextureSemantic.BaseColor)
            .ToArray();
        Assert.Equal(result.Package.Document.Materials.Length, embeddedBaseColors.Length);
        Assert.Single(embeddedBaseColors.Select(static texture => texture.ContentSha256).Distinct());
        Assert.Single(embeddedBaseColors.Select(static texture => texture.PackageEntryPath).Distinct());
        Assert.All(embeddedBaseColors, static embeddedBaseColor =>
        {
            Assert.Equal("image/png", embeddedBaseColor.MediaType);
            Assert.EndsWith(".png", embeddedBaseColor.PackageEntryPath, StringComparison.Ordinal);
        });
        Assert.Contains(result.Package.Document.Diagnostics, static diagnostic =>
            diagnostic.Code == "model_shared_embedded_base_color_atlas_inferred" &&
            string.Equals(diagnostic.Subject, "Material", StringComparison.Ordinal));
        Assert.True(result.Package.Document.BuildSettings.FlipTextureCoordinateV);

        CustomModelDocument preRepairDocument = result.Package.Document with
        {
            Materials = result.Package.Document.Materials
                .Select(static material => string.Equals(
                    material.Name,
                    "Material",
                    StringComparison.Ordinal)
                        ? material with { Textures = [] }
                        : material)
                .ToImmutableArray(),
            Diagnostics = result.Package.Document.Diagnostics
                .Where(static diagnostic =>
                    diagnostic.Code != "model_shared_embedded_base_color_atlas_inferred")
                .ToImmutableArray(),
        };
        FbxModelAuthoringImportResult normalizedPackage = FbxModelAuthoringImporter.ImportPackage(
            new CustomModelPackage(
                preRepairDocument,
                result.Package.SourceFbx,
                result.Package.TexturePayloads));
        Assert.All(normalizedPackage.Package.Document.Materials, static material =>
            Assert.Contains(
                material.Textures,
                static texture => texture.Semantic == CustomModelTextureSemantic.BaseColor));
        Assert.Contains(normalizedPackage.Package.Document.Diagnostics, static diagnostic =>
            diagnostic.Code == "model_shared_embedded_base_color_atlas_inferred");

        await AssertExternalModelControlIndependentGlbContractAsync(result, glbControl);
        CustomModelPreviewPayload preview = CustomModelPreviewAdapter.Create(
            result,
            clip: null,
            frame: 0,
            mode: CustomModelPreviewMode.Dl1Output);
        Assert.Equal(result.Surfaces.Length, preview.Meshes.Count);
        Assert.All(preview.Meshes, static mesh => Assert.NotNull(mesh.BaseColorTexture));
        Assert.All(result.Surfaces.Select((surface, surfaceIndex) => (surface, surfaceIndex)), item =>
        {
            ReadOnlySpan<ReAnimated.Renderer.D3D11.MeshVertex> previewVertices =
                preview.Meshes[item.surfaceIndex].Vertices.Span;
            Assert.Equal(item.surface.Vertices.Length, previewVertices.Length);
            for (int vertexIndex = 0; vertexIndex < item.surface.Vertices.Length; vertexIndex++)
            {
                Assert.Equal(
                    item.surface.Vertices[vertexIndex].TextureCoordinateU,
                    previewVertices[vertexIndex].TextureCoordinate.X,
                    precision: 5);
                Assert.Equal(
                    1.0 - item.surface.Vertices[vertexIndex].TextureCoordinateV,
                    previewVertices[vertexIndex].TextureCoordinate.Y,
                    precision: 5);
            }
        });

        string outputDirectory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CustomModelDocument authoredDocument = result.Package.Document with
            {
                BuildSettings = new CustomModelBuildSettings
                {
                    ResourceName = "external_model_control_player",
                    SurfaceName = "external_model_control_surface",
                    AnimationScriptAlias = "external_model_control_anim_script",
                },
                AnimationClips = result.Package.Document.AnimationClips
                    .Select((clip, index) => clip with
                    {
                        Included = index != fbxControl.RequireInt32("excludedClipIndex"),
                        DisplayName = $"external_model_control_stack_{index:00}",
                        FrameRate = index == 0 ? new FrameRate(24_000, 1_001) : clip.FrameRate,
                    })
                    .ToImmutableArray(),
            };
            var authoredPackage = new CustomModelPackage(
                authoredDocument,
                result.Package.SourceFbx,
                result.Package.TexturePayloads);
            string packagePath = Path.Combine(outputDirectory, "external_model_control.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(authoredPackage, packagePath);
            CustomModelPackage reopenedPackage = CustomModelPackageSerializer.Load(packagePath);
            FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(reopenedPackage);
            Assert.Equal(
                fbxControl.RequireInt32("animationClipCount"),
                reopened.Package.Document.AnimationClips.Length);
            Assert.Equal(
                fbxControl.RequireInt32("animationClipCount") - 1,
                reopened.Package.Document.AnimationClips.Count(static clip => clip.Included));
            Assert.Equal("external_model_control_stack_00", reopened.Package.Document.AnimationClips[0].DisplayName);
            Assert.Equal(new FrameRate(24_000, 1_001), reopened.Package.Document.AnimationClips[0].FrameRate);
            Assert.Equal("external_model_control_player", reopened.Package.Document.BuildSettings.ResourceName);
            Assert.Equal("external_model_control_surface", reopened.Package.Document.BuildSettings.SurfaceName);
            Assert.Equal("external_model_control_anim_script", reopened.Package.Document.BuildSettings.AnimationScriptAlias);

            string modelDirectory = Path.Combine(outputDirectory, "model");
            Dl1SourceModelBuildResult modelBuild = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = result,
                    OutputDirectory = modelDirectory,
                    ResourceName = "external_model_control_player",
                });
            byte[] sourceMsh = await File.ReadAllBytesAsync(modelBuild.SourceMshPath);
            Assert.Equal(0x0048_534Du, BinaryPrimitives.ReadUInt32LittleEndian(sourceMsh));
            Assert.Equal((uint)sourceMsh.Length, BinaryPrimitives.ReadUInt32LittleEndian(sourceMsh.AsSpan(8, 4)));
            Assert.Contains("SetBoneAnimTrans", await File.ReadAllTextAsync(modelBuild.BoneScriptPath));
            Dl1ChrV4Document character = Dl1ChrV4Codec.Parse(
                await File.ReadAllBytesAsync(modelBuild.CharacterDefinitionPath));
            Assert.Equal(result.Package.Document.Bones.Length + result.Surfaces.Length + 1, character.ObjectNames.Length);
            Assert.Equal("default", Assert.Single(character.Variants).Name);
            Assert.DoesNotContain(".chr", await File.ReadAllTextAsync(modelBuild.BlockedOutputsPath));
            string[] materialSources = Directory.GetFiles(modelDirectory, "*.dmt");
            Assert.Equal(result.Package.Document.Materials.Length, materialSources.Length);
            Assert.All(materialSources, materialSource =>
            {
                string text = File.ReadAllText(materialSource);
                Assert.Contains("<template>standard</template>", text, StringComparison.Ordinal);
                Assert.Contains("<dif_0_tex>", text, StringComparison.Ordinal);
                Assert.Contains("<nrm_0_tex>", text, StringComparison.Ordinal);
                Assert.Contains("<spc_0_tex>", text, StringComparison.Ordinal);
            });
            string[] textures = Directory.GetFiles(modelDirectory, "*.dds");
            int expectedTextureCount = result.Package.Document.Materials.Sum(material =>
                1 +
                (material.Textures.Any(static texture => texture.Semantic == CustomModelTextureSemantic.Normal) ? 1 : 0) +
                (material.Textures.Any(static texture => texture.Semantic == CustomModelTextureSemantic.Specular) ? 1 : 0));
            Assert.Equal(expectedTextureCount, textures.Length);
            Assert.All(textures, texture => AssertLegacyDds(File.ReadAllBytes(texture)));
            Assert.True(File.Exists(Path.Combine(modelDirectory, "external_model_control_player.chr")));
            Assert.False(File.Exists(Path.Combine(modelDirectory, "external_model_control_player.skn")));

            ImmutableArray<CustomModelAnimationClip> selected = result.Package.Document.AnimationClips
                .Take(2)
                .Select(static clip => clip with { Included = true })
                .ToImmutableArray();
            string animationPath = Path.Combine(outputDirectory, "external_model_control_animations.rpack");
            CustomModelAnimationLibraryResult animationBuild =
                await CustomModelAnimationLibraryExporter.ExportAsync(
                    new CustomModelAnimationLibraryRequest
                    {
                        Model = result,
                        OutputPath = animationPath,
                        Selections = selected,
                        AnimationScriptAlias = "external_model_control_anim_script",
                    });
            Rp6lAnimationLibrary library = await Rp6lAnimationLibraryCodec.ExtractAsync(animationBuild.OutputPath);
            Assert.Equal(2, library.Animations.Count);
            Assert.Equal(2, animationBuild.AnimationNames.Length);
            Rp6lAnimationScript script = Assert.Single(library.AnimationScripts).Value;
            ParsedAnimationScr parsedScript = AnimationScrCodec.Parse(
                new AnimationScrSections(script.HeaderSection, script.BodySection));
            Assert.Equal("external_model_control_anim_script", animationBuild.AnimationScriptName);
            Assert.Equal(2, parsedScript.DeclaredSequenceCount);
            Assert.Equal(2, parsedScript.Sequences.Length);
            Assert.All(parsedScript.Sequences, sequence =>
            {
                Assert.Equal(0, sequence.StartFrame);
                Assert.Equal(0, sequence.EventCount);
            });
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        string inputFingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"custom-model-corpus-v1\0{sourceHash}\0{result.Package.Document.RigSignature}"))).ToLowerInvariant();
        string receiptDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DLReAnimated",
            "ValidationReceipts");
        Directory.CreateDirectory(receiptDirectory);
        string receiptPath = Path.Combine(receiptDirectory, $"custom-model-{inputFingerprint}.json");
        string temporaryPath = receiptPath + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            gate = "CustomModelCorpus",
            passed = true,
            inputFingerprint,
            sourceSha256 = sourceHash,
            bones = result.Rig.BoneCount,
            meshes = result.Package.Document.Meshes.Length,
            surfaces = result.Surfaces.Length,
            animationStacks = result.Package.Document.AnimationClips.Length,
            embeddedTexturePayloads = result.Package.TexturePayloads.Count,
        }));
        File.Move(temporaryPath, receiptPath, overwrite: true);
        _output.WriteLine($"Acceptance receipt: {receiptPath}");
    }

    [ExternalCorpusFact(Timeout = 900_000)]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1ModelCompiler")]
    public async Task ExternalModelControlCompilesWithInstalledDeveloperToolsWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DLR_RUN_INSTALLED_MODEL_COMPILER"),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine("NOT EXERCISED: set DLR_RUN_INSTALLED_MODEL_COMPILER=1 for the explicit offline compiler acceptance gate.");
            return;
        }

        ExternalCorpusControl fbxControl = await RequireVerifiedControlAsync(FbxControlId);
        string corpusPath = fbxControl.RequireExistingFile();
        string? compiler = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable();
        Assert.True(File.Exists(compiler), "The Dying Light Developer Tools compiler was not discovered.");
        Dl1InstallLocation? install = SteamInstallDiscovery.Discover()
            .FirstOrDefault(static candidate => candidate.IsValid);
        Assert.NotNull(install);
        string data0Pak = Path.Combine(install!.InstallPath, "DW", "Data0.pak");
        Assert.True(File.Exists(data0Pak), $"The retail compiler bootstrap is missing at {data0Pak}.");
        FbxModelAuthoringImportResult model = await FbxModelAuthoringImporter.ImportFileAsync(corpusPath);
        string outputDirectory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string rpackPath = Path.Combine(outputDirectory, "external_model_control_player_pc.rpack");
            Dl1OfficialModelCompilerResult result = await Dl1OfficialModelCompiler.CompileAsync(
                new Dl1OfficialModelCompilerRequest
                {
                    Model = model,
                    CompilerExecutablePath = compiler!,
                    RetailData0PakPath = data0Pak,
                    OutputRpackPath = rpackPath,
                    ResourceName = "external_model_control_player",
                });
            Assert.Equal(CustomModelBuildState.CompilerValidated, result.BuildReceipt.State);
            Assert.True(File.Exists(result.OutputRpackPath));
            Assert.True(File.Exists(result.CompiledMeshObjectPath));
            Assert.True(File.Exists(result.MaterialDatabasePath));
            Assert.True(File.Exists(result.ReceiptPath));
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(result.OutputRpackPath);
            Assert.Contains(archive.Resources, static resource =>
                resource.ResourceType == Rp6lResourceTypes.Mesh &&
                string.Equals(resource.Name, "external_model_control_player", StringComparison.OrdinalIgnoreCase));
            CustomModelMaterial[] authoredMaterials = model.Package.Document.Materials
                .Where(static material => string.IsNullOrWhiteSpace(material.ExistingDl1MaterialReference))
                .ToArray();
            Assert.NotEmpty(authoredMaterials);
            await using Dl1MaterialPackReader materialDatabase =
                await Dl1MaterialPackReader.OpenAsync(result.MaterialDatabasePath!);
            foreach (CustomModelMaterial authoredMaterial in authoredMaterials)
            {
                string materialName = Dl1SourceModelWriter.SanitizeName(
                    $"external_model_control_player_{authoredMaterial.Name}",
                    55);
                Dl1MaterialPackMaterialRecord? material =
                    await materialDatabase.ReadMaterialAsync($"{materialName}.mat");
                Assert.NotNull(material);
                string[] expectedTextures =
                [
                    materialName,
                    .. authoredMaterial.Textures.Any(static texture =>
                        texture.Semantic == CustomModelTextureSemantic.Normal)
                            ? [$"{materialName}_nrm"]
                            : Array.Empty<string>(),
                    .. authoredMaterial.Textures.Any(static texture =>
                        texture.Semantic == CustomModelTextureSemantic.Specular)
                            ? [$"{materialName}_shn"]
                            : Array.Empty<string>(),
                ];
                Assert.InRange(material!.Textures.Count, expectedTextures.Length, 256);
                foreach (string textureName in expectedTextures)
                {
                    Assert.Contains(archive.Resources, resource =>
                        resource.ResourceType == Rp6lResourceTypes.Texture &&
                        string.Equals(resource.Name, textureName, StringComparison.OrdinalIgnoreCase));
                }

                if (!authoredMaterial.Textures.Any(static texture =>
                        texture.Semantic == CustomModelTextureSemantic.Normal))
                {
                    Assert.DoesNotContain(archive.Resources, resource =>
                        resource.ResourceType == Rp6lResourceTypes.Texture &&
                        string.Equals(resource.Name, $"{materialName}_nrm", StringComparison.OrdinalIgnoreCase));
                }

                if (!authoredMaterial.Textures.Any(static texture =>
                        texture.Semantic == CustomModelTextureSemantic.Specular))
                {
                    Assert.DoesNotContain(archive.Resources, resource =>
                        resource.ResourceType == Rp6lResourceTypes.Texture &&
                        string.Equals(resource.Name, $"{materialName}_shn", StringComparison.OrdinalIgnoreCase));
                }
            }

            string receiptDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DLReAnimated",
                "ValidationReceipts");
            Directory.CreateDirectory(receiptDirectory);
            string receiptPath = Path.Combine(
                receiptDirectory,
                $"installed-model-compiler-{result.BuildReceipt.OutputManifestFingerprint}.json");
            File.Copy(result.ReceiptPath, receiptPath, overwrite: true);
            _output.WriteLine($"Installed compiler acceptance receipt: {receiptPath}");
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static async Task<ExternalCorpusControl> RequireVerifiedControlAsync(string controlId)
    {
        ExternalCorpusManifest manifest = ExternalCorpusManifest.LoadOptional()
            ?? throw new InvalidOperationException(
                "External model controls are not configured. Copy tests/local-external-corpora.example.json to tests/local-external-corpora.json or set DLR_EXTERNAL_CORPORA_MANIFEST.");
        ExternalCorpusControl control = manifest.RequireControl(controlId);
        await control.VerifySha256Async(control.RequireExistingFile());
        return control;
    }

    private static async Task AssertExternalModelControlIndependentGlbContractAsync(
        FbxModelAuthoringImportResult imported,
        ExternalCorpusControl glbControl)
    {
        string glbPath = glbControl.RequireExistingFile();
        await glbControl.VerifySha256Async(glbPath);
        Assert.True(
            File.Exists(glbPath),
            $"The independent GLB texture-orientation control is missing at {glbPath}.");
        byte[] glb = await File.ReadAllBytesAsync(glbPath);
        Assert.True(glb.Length >= 28);
        Assert.Equal(0x4654_6C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));

        (int jsonOffset, int jsonLength, int binaryOffset, int binaryLength) =
            FindGlbChunks(glb);
        using JsonDocument json = JsonDocument.Parse(glb.AsMemory(jsonOffset, jsonLength));
        JsonElement root = json.RootElement;
        JsonElement glbMaterials = root.GetProperty("materials");
        Assert.Equal(2, glbMaterials.GetArrayLength());
        Assert.Equal(1, root.GetProperty("images").GetArrayLength());
        JsonElement textures = root.GetProperty("textures");
        Assert.All(
            glbMaterials.EnumerateArray(),
            material =>
            {
                int textureIndex = material
                    .GetProperty("pbrMetallicRoughness")
                    .GetProperty("baseColorTexture")
                    .GetProperty("index")
                    .GetInt32();
                Assert.Equal(0, textures[textureIndex].GetProperty("source").GetInt32());
            });

        var expected = new Dictionary<(string Mesh, int Primitive), GlbSurfaceControl>
        {
            [("Plane", 0)] = new("Body", "Lit", 531),
            [("Plane", 1)] = new("Body", "Material", 35),
            [("Plane_002", 0)] = new("Hairback", "Lit", 30),
            [("Plane_001", 0)] = new("HairFront", "Lit", 14),
            [("Cube_002", 0)] = new("Hammer", "Lit", 60),
            [("Cube_001", 0)] = new("Helmet", "Lit", 122),
            [("Cylinder_001", 0)] = new("HelmetAntennae", "Lit", 47),
            [("Cylinder", 0)] = new("HelmetDish", "Lit", 40),
            [("Cube", 0)] = new("Shoes", "Lit", 55),
        };
        JsonElement accessors = root.GetProperty("accessors");
        JsonElement bufferViews = root.GetProperty("bufferViews");
        int compared = 0;
        foreach (JsonElement mesh in root.GetProperty("meshes").EnumerateArray())
        {
            string meshName = mesh.GetProperty("name").GetString() ?? string.Empty;
            int primitiveIndex = 0;
            foreach (JsonElement primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                Assert.True(
                    expected.TryGetValue((meshName, primitiveIndex), out GlbSurfaceControl control),
                    $"The independent GLB contains an unexpected primitive {meshName}/{primitiveIndex}.");
                CustomModelMaterial material = Assert.Single(
                    imported.Package.Document.Materials,
                    candidate => string.Equals(
                        candidate.Name,
                        control.MaterialName,
                        StringComparison.Ordinal));
                FbxModelSurface surface = Assert.Single(
                    imported.Surfaces,
                    candidate =>
                        string.Equals(candidate.MeshName, control.FbxMeshName, StringComparison.Ordinal) &&
                        candidate.MaterialId == material.Id);
                int accessorIndex = primitive
                    .GetProperty("attributes")
                    .GetProperty("TEXCOORD_0")
                    .GetInt32();
                GlbUv[] referenceUvs = ReadGlbVector2Accessor(
                    glb,
                    binaryOffset,
                    binaryLength,
                    accessors[accessorIndex],
                    bufferViews);
                (double U, double V)[] sourceUvs = surface.Vertices
                    .Select(static vertex => (
                        vertex.TextureCoordinateU,
                        vertex.TextureCoordinateV))
                    .Distinct()
                    .ToArray();
                Assert.Equal(control.UniqueUvCount, sourceUvs.Length);
                Assert.All(sourceUvs, sourceUv =>
                {
                    double flippedError = referenceUvs.Min(reference =>
                        Math.Abs(sourceUv.U - reference.U) +
                        Math.Abs((1.0 - sourceUv.V) - reference.V));
                    Assert.InRange(flippedError, 0.0, 1e-6);
                });
                compared++;
                primitiveIndex++;
            }
        }

        Assert.Equal(expected.Count, compared);
    }

    private static (int JsonOffset, int JsonLength, int BinaryOffset, int BinaryLength)
        FindGlbChunks(ReadOnlySpan<byte> glb)
    {
        int cursor = 12;
        int jsonOffset = -1;
        int jsonLength = 0;
        int binaryOffset = -1;
        int binaryLength = 0;
        while (cursor < glb.Length)
        {
            Assert.True(cursor <= glb.Length - 8);
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.Slice(cursor, 4)));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(glb.Slice(cursor + 4, 4));
            cursor = checked(cursor + 8);
            Assert.InRange(length, 0, glb.Length - cursor);
            if (type == 0x4E4F_534A)
            {
                Assert.Equal(-1, jsonOffset);
                jsonOffset = cursor;
                jsonLength = length;
            }
            else if (type == 0x004E_4942)
            {
                Assert.Equal(-1, binaryOffset);
                binaryOffset = cursor;
                binaryLength = length;
            }

            cursor = checked(cursor + length);
        }

        Assert.Equal(glb.Length, cursor);
        Assert.True(jsonOffset >= 0);
        Assert.True(binaryOffset >= 0);
        return (jsonOffset, jsonLength, binaryOffset, binaryLength);
    }

    private static GlbUv[] ReadGlbVector2Accessor(
        ReadOnlySpan<byte> glb,
        int binaryOffset,
        int binaryLength,
        JsonElement accessor,
        JsonElement bufferViews)
    {
        Assert.Equal(5126, accessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC2", accessor.GetProperty("type").GetString());
        int count = accessor.GetProperty("count").GetInt32();
        JsonElement view = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
        Assert.False(view.TryGetProperty("buffer", out JsonElement buffer) && buffer.GetInt32() != 0);
        int viewOffset = view.TryGetProperty("byteOffset", out JsonElement viewOffsetValue)
            ? viewOffsetValue.GetInt32()
            : 0;
        int accessorOffset = accessor.TryGetProperty("byteOffset", out JsonElement accessorOffsetValue)
            ? accessorOffsetValue.GetInt32()
            : 0;
        int stride = view.TryGetProperty("byteStride", out JsonElement strideValue)
            ? strideValue.GetInt32()
            : 8;
        Assert.InRange(stride, 8, 2_048);
        var result = new GlbUv[count];
        for (int index = 0; index < count; index++)
        {
            int localOffset = checked(viewOffset + accessorOffset + (index * stride));
            Assert.InRange(localOffset, 0, binaryLength - 8);
            int offset = checked(binaryOffset + localOffset);
            result[index] = new GlbUv(
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(glb.Slice(offset, 4))),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(glb.Slice(offset + 4, 4))));
        }

        return result;
    }

    private static byte[] BuildSyntheticCompilerObject()
    {
        byte[] name = "synthetic_mesh\0"u8.ToArray();
        const int headerSize = 36;
        const int chunkRowSize = 20;
        const int itemRowSize = 16;
        const int resourceRowSize = 12;
        int payloadOffset = checked(
            headerSize +
            chunkRowSize +
            itemRowSize +
            resourceRowSize +
            sizeof(int) +
            name.Length);
        byte[] output = new byte[payloadOffset + 4];
        "RP6L"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(32), 1);

        int cursor = headerSize;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor), 0x0010);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 2), 0x2104);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 8), 4);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 12), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 18), 15);
        cursor += chunkRowSize;

        output[cursor] = 0;
        output[cursor + 1] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(cursor + 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(cursor + 4),
            checked((uint)payloadOffset));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 8), 4);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 12), 0);
        cursor += itemRowSize;

        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(cursor), 1);
        BinaryPrimitives.WriteInt16LittleEndian(
            output.AsSpan(cursor + 2),
            unchecked((short)0x8110));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 8), 0);
        cursor += resourceRowSize;

        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor), 0);
        cursor += sizeof(int);
        name.CopyTo(output.AsSpan(cursor));
        new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(output.AsSpan(payloadOffset));
        return output;
    }

    private static byte[] BuildSyntheticStandaloneObject(
        string resourceName,
        short resourceType,
        ReadOnlySpan<byte> payload)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes(resourceName + "\0");
        const int headerSize = 36;
        const int chunkRowSize = 20;
        const int itemRowSize = 16;
        const int resourceRowSize = 12;
        int payloadOffset = checked(
            headerSize +
            chunkRowSize +
            itemRowSize +
            resourceRowSize +
            sizeof(int) +
            name.Length);
        byte[] output = new byte[checked(payloadOffset + payload.Length)];
        "RP6L"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(32), 1);

        int cursor = headerSize;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor), 0x0010);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 2), 0x2104);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 4), checked((uint)payloadOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 8), checked((uint)payload.Length));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 12), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(cursor + 18), 15);
        cursor += chunkRowSize;

        output[cursor] = 0;
        output[cursor + 1] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(cursor + 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(cursor + 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 8), payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 12), 0);
        cursor += itemRowSize;

        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(cursor), 1);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(cursor + 2), resourceType);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor + 8), 0);
        cursor += resourceRowSize;

        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(cursor), 0);
        cursor += sizeof(int);
        name.CopyTo(output.AsSpan(cursor));
        payload.CopyTo(output.AsSpan(payloadOffset));
        return output;
    }

    private static byte[] BuildSyntheticMaterialDatabase(
        IReadOnlyDictionary<string, string[]> materials)
    {
        const int headerSize = 16;
        const int containerRowSize = 48;
        const int materialRowSize = 16;
        const int textureRowSize = 12;
        const int materialTableOffset = headerSize + containerRowSize;

        (uint Hash, uint[] TextureHashes)[] records = materials
            .Select(static entry => (
                Dl1ResourceNameHash.Compute(entry.Key),
                entry.Value.Select(ComputeCompiledTextureReferenceHash).ToArray()))
            .OrderBy(static record => record.Item1)
            .ToArray();
        int payloadOffset = checked(materialTableOffset + records.Length * materialRowSize);
        int payloadBytes = records.Sum(static record =>
            checked(24 + record.TextureHashes.Length * textureRowSize));
        byte[] output = new byte[checked(payloadOffset + payloadBytes)];
        "ABDM"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), headerSize);

        "materials"u8.CopyTo(output.AsSpan(headerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(headerSize + 32),
            checked((uint)records.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(headerSize + 36),
            checked((uint)records.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(headerSize + 40),
            materialTableOffset);

        int payloadCursor = payloadOffset;
        for (int index = 0; index < records.Length; index++)
        {
            (uint hash, uint[] textureHashes) = records[index];
            int payloadLength = checked(24 + textureHashes.Length * textureRowSize);
            int rowOffset = materialTableOffset + index * materialRowSize;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(rowOffset), hash);
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(rowOffset + 4),
                checked((uint)payloadCursor));
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(rowOffset + 8),
                checked((uint)payloadLength));
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(rowOffset + 12),
                checked((uint)payloadLength));

            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(payloadCursor), hash);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(payloadCursor + 16), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(
                output.AsSpan(payloadCursor + 18),
                checked((ushort)textureHashes.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(payloadCursor + 22), 2);
            for (int textureIndex = 0; textureIndex < textureHashes.Length; textureIndex++)
            {
                int textureOffset = payloadCursor + 24 + textureIndex * textureRowSize;
                BinaryPrimitives.WriteUInt32LittleEndian(
                    output.AsSpan(textureOffset),
                    checked((uint)(textureIndex + 1)));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    output.AsSpan(textureOffset + 4),
                    textureHashes[textureIndex]);
            }

            payloadCursor += payloadLength;
        }

        return output;
    }

    private static uint ComputeCompiledTextureReferenceHash(string resourceName)
    {
        uint crc = 0x811C9DC5 ^ uint.MaxValue;
        foreach (byte value in Encoding.ASCII.GetBytes(resourceName))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320U & mask);
            }
        }

        return crc ^ uint.MaxValue;
    }

    private static CustomModelDocument CreateDocumentWithTexture(
        CustomModelTextureBinding texture)
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        return new CustomModelDocument
        {
            ModelId = new Guid("09718b62-9bf5-5aa2-8dd6-59035cd8bd5e"),
            Name = "Synthetic texture contract",
            RigMode = CustomModelRigMode.StaticProp,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            RigSignature = hash,
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = new Guid("e8f7f630-d758-50eb-8693-3a04f1084672"),
                    Name = "Synthetic material",
                    Textures = [texture],
                },
            ],
        };
    }

    private static void AssertLegacyDds(byte[] bytes, string? expectedFourCc = null)
    {
        Assert.True(bytes.AsSpan().StartsWith("DDS "u8));
        Assert.True(bytes.Length >= 128);
        string fourCc = System.Text.Encoding.ASCII.GetString(bytes, 84, 4);
        Assert.Contains(fourCc, LegacyDdsFourCcValues);
        if (expectedFourCc is not null)
        {
            Assert.Equal(expectedFourCc, fourCc);
        }
    }

    private readonly record struct GlbSurfaceControl(
        string FbxMeshName,
        string MaterialName,
        int UniqueUvCount);

    private readonly record struct GlbUv(float U, float V);

    private sealed class ModelPickerProbe : IProjectFileDialogService
    {
        public int FbxPickerCalls { get; private set; }

        public int PackagePickerCalls { get; private set; }

        public Exception? PackagePickerException { get; set; }

        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;

        public string? ShowOpenCustomModelFbxDialog(string? initialPath)
        {
            FbxPickerCalls++;
            return null;
        }

        public string? ShowOpenCustomModelPackageDialog(string? initialPath)
        {
            PackagePickerCalls++;
            if (PackagePickerException is not null)
            {
                throw PackagePickerException;
            }

            return null;
        }
    }
}
