using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1MultiModelPortableExporterTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task WritesSeparateLibrariesAndNeverCopiesRetailTargetBytes()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] compiledModelRpack = BuildCompiledModelRpack();
            Dl1MultiModelPortableExportResult result =
                await Dl1MultiModelPortableExporter.ExportAsync(new()
                {
                    ParentOutputDirectory = directory,
                    PackageName = "generic_multi_model",
                    Models =
                    [
                        new Dl1PortableModelOutputRequest
                        {
                            ModelId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                            ModelName = "AuthoredCharacter",
                            TargetKind = Dl1PortableTargetKind.CustomModel,
                            TargetFingerprint = new string('1', 64),
                            AnimationLibraryName = "AuthoredLibrary",
                            Animations =
                            [
                                Animation(
                                    "authored_idle",
                                    Dl1PortableAnimationRole.Body,
                                    '2'),
                                Animation(
                                    "authored_face",
                                    Dl1PortableAnimationRole.Facial,
                                    '3'),
                            ],
                            CompiledCustomModelRpack = compiledModelRpack,
                            OfficialCompilerEvidence = CompilerEvidence(
                                compiledModelRpack,
                                verifiedMorphChannelCount: 1,
                                verifiedMorphBindingCount: 1,
                                morphDeltaFormat:
                                    CompiledMorphDeltaFormat.PcHalf4),
                            CompiledMorphResources =
                            [
                                new Dl1PortableMorphResource
                                {
                                    RelativePath =
                                        "0000-generic_smile.morph.json",
                                    Payload = BuildMorphInventoryReference(),
                                },
                            ],
                        },
                        new Dl1PortableModelOutputRequest
                        {
                            ModelId = Guid.Parse("20000000-0000-0000-0000-000000000002"),
                            ModelName = "ReferencedCharacter",
                            TargetKind = Dl1PortableTargetKind.RetailReference,
                            TargetFingerprint = new string('4', 64),
                            RetailResourceReference = "retail:model:generic_reference",
                            AnimationLibraryName = "ReferencedLibrary",
                            Animations =
                            [
                                Animation(
                                    "reference_idle",
                                    Dl1PortableAnimationRole.Body,
                                    '5'),
                            ],
                        },
                    ],
                });

            Assert.Equal(2, result.Models.Length);
            Assert.True(File.Exists(result.ManifestPath));
            Dl1PortableModelOutputResult custom = Assert.Single(
                result.Models,
                static model => model.TargetKind == Dl1PortableTargetKind.CustomModel);
            Dl1PortableModelOutputResult retail = Assert.Single(
                result.Models,
                static model => model.TargetKind == Dl1PortableTargetKind.RetailReference);
            Assert.True(File.Exists(custom.CustomModelRpackPath));
            Assert.Single(custom.MorphResourcePaths);
            Assert.Null(retail.CustomModelRpackPath);
            Assert.Empty(retail.MorphResourcePaths);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(retail.DirectoryPath, "*", SearchOption.AllDirectories),
                static path => path.EndsWith(".msh_obj", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".morphs_obj", StringComparison.OrdinalIgnoreCase));

            foreach (Dl1PortableModelOutputResult model in result.Models)
            {
                Rp6lAnimationLibrary reopened =
                    await Rp6lAnimationLibraryCodec.ExtractAsync(model.AnimationRpackPath);
                Assert.True(reopened.AnimationScripts.ContainsKey(model.AnimationLibraryName));
                Assert.Equal(
                    model.BodyAnimations.Length + model.FacialAnimations.Length,
                    reopened.Animations.Count);
            }

            using JsonDocument manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(result.ManifestPath));
            Assert.Equal(2, manifest.RootElement.GetProperty("modelCount").GetInt32());
            Assert.True(
                manifest.RootElement
                    .GetProperty("safety")
                    .GetProperty("retailTargetsContainReferencesOnly")
                    .GetBoolean());
            Assert.False(
                manifest.RootElement
                    .GetProperty("safety")
                    .GetProperty("liveGameProof")
                    .GetBoolean());
            JsonElement customManifest = manifest.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Single(model => model
                    .GetProperty("targetKind")
                    .GetString() ==
                        nameof(Dl1PortableTargetKind.CustomModel));
            JsonElement compilerEvidence = customManifest.GetProperty(
                "officialCompilerEvidence");
            Assert.Equal(
                Convert.ToHexStringLower(
                    SHA256.HashData(
                        await File.ReadAllBytesAsync(
                            custom.CustomModelRpackPath!))),
                compilerEvidence
                    .GetProperty("outputRpackSha256")
                    .GetString());
            Assert.Equal(
                1,
                compilerEvidence
                    .GetProperty("verifiedMorphChannelCount")
                    .GetInt32());
            Assert.Equal(
                nameof(CompiledMorphDeltaFormat.PcHalf4),
                compilerEvidence
                    .GetProperty("morphDeltaFormat")
                    .GetString());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task RetailTargetRejectsAnyModelOrMorphPayloadBeforePublishing()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Dl1MultiModelPortableExporter.ExportAsync(new()
                {
                    ParentOutputDirectory = directory,
                    Models =
                    [
                        new Dl1PortableModelOutputRequest
                        {
                            ModelId = Guid.NewGuid(),
                            ModelName = "ReferencedCharacter",
                            TargetKind = Dl1PortableTargetKind.RetailReference,
                            TargetFingerprint = new string('6', 64),
                            RetailResourceReference = "retail:model:generic_reference",
                            AnimationLibraryName = "ReferencedLibrary",
                            Animations =
                            [
                                Animation(
                                    "reference_idle",
                                    Dl1PortableAnimationRole.Body,
                                    '7'),
                            ],
                            CompiledCustomModelRpack = [1, 2, 3],
                        },
                    ],
                }));

            Assert.Contains("retail model or morph bytes are forbidden", error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task ExistingUnownedDirectoryIsNeverReplaced()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string existing = Path.Combine(directory, "generic_package");
            Directory.CreateDirectory(existing);
            string preserved = Path.Combine(existing, "preserved.txt");
            await File.WriteAllTextAsync(preserved, "user data");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Dl1MultiModelPortableExporter.ExportAsync(new()
                {
                    ParentOutputDirectory = directory,
                    PackageName = "generic_package",
                    Models =
                    [
                        new Dl1PortableModelOutputRequest
                        {
                            ModelId = Guid.NewGuid(),
                            ModelName = "ReferencedCharacter",
                            TargetKind = Dl1PortableTargetKind.RetailReference,
                            TargetFingerprint = new string('8', 64),
                            RetailResourceReference = "retail:model:generic_reference",
                            AnimationLibraryName = "ReferencedLibrary",
                            Animations =
                            [
                                Animation(
                                    "reference_idle",
                                    Dl1PortableAnimationRole.Body,
                                    '9'),
                            ],
                        },
                    ],
                }));

            Assert.Contains("not owned", error.Message, StringComparison.Ordinal);
            Assert.Equal("user data", await File.ReadAllTextAsync(preserved));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task InvalidCustomCompilerOutputFailsBeforePublishing()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] invalidRpack = [0x52, 0x50, 0x36, 0x4c];
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => Dl1MultiModelPortableExporter.ExportAsync(new()
                {
                    ParentOutputDirectory = directory,
                    Models =
                    [
                        new Dl1PortableModelOutputRequest
                        {
                            ModelId = Guid.NewGuid(),
                            ModelName = "AuthoredCharacter",
                            TargetKind = Dl1PortableTargetKind.CustomModel,
                            TargetFingerprint = new string('a', 64),
                            AnimationLibraryName = "AuthoredLibrary",
                            Animations =
                            [
                                Animation(
                                    "authored_idle",
                                    Dl1PortableAnimationRole.Body,
                                    'b'),
                            ],
                            CompiledCustomModelRpack = invalidRpack,
                            OfficialCompilerEvidence =
                                CompilerEvidence(invalidRpack),
                        },
                    ],
                }));

            Assert.Contains("RP6L", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task CustomCompilerEvidenceRejectsHashCountAndEncodingMismatches()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] compiledModelRpack = BuildCompiledModelRpack();
            Dl1PortableMorphResource morphReference = new()
            {
                RelativePath = "0000-generic_smile.morph.json",
                Payload = BuildMorphInventoryReference(),
            };

            InvalidDataException hashError =
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    Dl1MultiModelPortableExporter.ExportAsync(new()
                    {
                        ParentOutputDirectory = directory,
                        PackageName = "generic_hash_mismatch",
                        Models =
                        [
                            CustomModel(
                                compiledModelRpack,
                                CompilerEvidence(compiledModelRpack) with
                                {
                                    OutputRpackSha256 = new string('0', 64),
                                }),
                        ],
                    }));
            Assert.Contains(
                "does not match",
                hashError.Message,
                StringComparison.OrdinalIgnoreCase);

            InvalidDataException countError =
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    Dl1MultiModelPortableExporter.ExportAsync(new()
                    {
                        ParentOutputDirectory = directory,
                        PackageName = "generic_count_mismatch",
                        Models =
                        [
                            CustomModel(
                                compiledModelRpack,
                                CompilerEvidence(
                                    compiledModelRpack,
                                    verifiedMorphChannelCount: 2,
                                    verifiedMorphBindingCount: 2,
                                    morphDeltaFormat:
                                        CompiledMorphDeltaFormat.PcHalf4),
                                [morphReference]),
                        ],
                    }));
            Assert.Contains(
                "morph inventory",
                countError.Message,
                StringComparison.OrdinalIgnoreCase);

            InvalidDataException encodingError =
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    Dl1MultiModelPortableExporter.ExportAsync(new()
                    {
                        ParentOutputDirectory = directory,
                        PackageName = "generic_encoding_mismatch",
                        Models =
                        [
                            CustomModel(
                                compiledModelRpack,
                                CompilerEvidence(
                                    compiledModelRpack,
                                    verifiedMorphChannelCount: 1,
                                    verifiedMorphBindingCount: 1,
                                    morphDeltaFormat:
                                        CompiledMorphDeltaFormat
                                            .X360SignedShort4Scale16384),
                                [morphReference]),
                        ],
                    }));
            Assert.Contains(
                nameof(CompiledMorphDeltaFormat.PcHalf4),
                encodingError.Message,
                StringComparison.Ordinal);

            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task BodyAndFacialArtifactsFromOneVariantRoundTripTogether()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        Guid variantId = Guid.Parse(
            "30000000-0000-0000-0000-000000000003");
        try
        {
            Dl1MultiModelPortableExportResult result =
                await Dl1MultiModelPortableExporter.ExportAsync(new()
                {
                    ParentOutputDirectory = directory,
                    PackageName = "generic_body_facial_pair",
                    Models =
                    [
                        new Dl1PortableModelOutputRequest
                        {
                            ModelId = Guid.Parse(
                                "40000000-0000-0000-0000-000000000004"),
                            ModelName = "ReferencedCharacter",
                            TargetKind =
                                Dl1PortableTargetKind.RetailReference,
                            TargetFingerprint = new string('c', 64),
                            RetailResourceReference =
                                "retail:model:generic_reference",
                            AnimationLibraryName = "PairedLibrary",
                            Animations =
                            [
                                Animation(
                                    "paired_body",
                                    Dl1PortableAnimationRole.Body,
                                    'd') with
                                {
                                    VariantId = variantId,
                                    FramesPerSecond = 23.976f,
                                },
                                Animation(
                                    "paired_mimic",
                                    Dl1PortableAnimationRole.Facial,
                                    'd') with
                                {
                                    VariantId = variantId,
                                    FramesPerSecond = 23.976f,
                                },
                            ],
                        },
                    ],
                });

            Dl1PortableModelOutputResult model =
                Assert.Single(result.Models);
            Assert.Single(model.BodyAnimations);
            Assert.Equal("paired_body", model.BodyAnimations[0]);
            Assert.Single(model.FacialAnimations);
            Assert.Equal("paired_mimic", model.FacialAnimations[0]);
            Rp6lAnimationLibrary reopened =
                await Rp6lAnimationLibraryCodec.ExtractAsync(
                    model.AnimationRpackPath);
            Assert.Equal(2, reopened.Animations.Count);
            ParsedAnimationScr parsed = AnimationScrCodec.Parse(
                new AnimationScrSections(
                    reopened.AnimationScripts[model.AnimationLibraryName]
                        .HeaderSection,
                    reopened.AnimationScripts[model.AnimationLibraryName]
                        .BodySection));
            Assert.Equal(23.976f,
                Assert.Single(parsed.Sequences).FramesPerSecond);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Dl1PortableAnimationResource Animation(
        string name,
        Dl1PortableAnimationRole role,
        char fingerprintDigit) => new()
    {
        VariantId = Guid.NewGuid(),
        Name = name,
        Role = role,
        Payload = BuildAnimationPayload(),
        FrameCount = 2,
        FramesPerSecond = 30,
        SourceFingerprint = new string(fingerprintDigit, 64),
    };

    private static Dl1PortableModelOutputRequest CustomModel(
        byte[] compiledModelRpack,
        Dl1OfficialModelCompilerEvidence compilerEvidence,
        ImmutableArray<Dl1PortableMorphResource> morphResources = default) =>
        new()
        {
            ModelId = Guid.NewGuid(),
            ModelName = "AuthoredCharacter",
            TargetKind = Dl1PortableTargetKind.CustomModel,
            TargetFingerprint = new string('a', 64),
            AnimationLibraryName = "AuthoredLibrary",
            Animations =
            [
                Animation(
                    "authored_idle",
                    Dl1PortableAnimationRole.Body,
                    'b'),
            ],
            CompiledCustomModelRpack = compiledModelRpack,
            OfficialCompilerEvidence = compilerEvidence,
            CompiledMorphResources = morphResources.IsDefault
                ? []
                : morphResources,
        };

    private static Dl1OfficialModelCompilerEvidence CompilerEvidence(
        byte[] compiledModelRpack,
        int verifiedMorphChannelCount = 0,
        int verifiedMorphBindingCount = 0,
        CompiledMorphDeltaFormat? morphDeltaFormat = null) => new()
        {
            OutputRpackSha256 = Convert.ToHexStringLower(
                SHA256.HashData(compiledModelRpack)),
            CompilerFingerprint = new string('c', 64),
            ToolFingerprint =
                Dl1OfficialModelCompiler.CurrentToolFingerprint,
            BuildReceiptInputFingerprint = new string('d', 64),
            BuildReceiptOutputManifestFingerprint = new string('e', 64),
            BuildState = CustomModelBuildState.CompilerValidated,
            VerifiedMorphChannelCount = verifiedMorphChannelCount,
            VerifiedMorphBindingCount = verifiedMorphBindingCount,
            MorphDeltaFormat = morphDeltaFormat,
        };

    private static byte[] BuildCompiledModelRpack() =>
        RpackTestData.BuildArchive(
            "AuthoredCharacter",
            Rp6lResourceTypes.Mesh,
            [new RpackTestItem(0, "generic compiled mesh payload"u8.ToArray())],
            RpackTestCompression.None);

    private static byte[] BuildMorphInventoryReference() =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            format =
                "dl-reanimated-compiled-morph-inventory-reference",
            schemaVersion = 1,
            index = 0,
            name = "generic_smile",
            descriptorHash = "0x12345678",
            blendShapeChannelObjectId = 101,
            shapeObjectId = 102,
            geometryObjectIds = ImmutableArray.Create(103L),
            compiledPayload =
                "embedded-in-official-compiler-model-rpack",
        });

    private static byte[] BuildAnimationPayload()
    {
        ImmutableArray<uint> descriptors = [0x12345678u];
        ImmutableArray<Anm2Frame> frames =
        [
            new Anm2Frame(
            [
                new Anm2TrackFrame(0, 0, 0, 0, 0, 0, 1, 1, 1),
            ]),
            new Anm2Frame(
            [
                new Anm2TrackFrame(0.1f, 0, 0, 0, 0, 0, 1, 1, 1),
            ]),
        ];
        return Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                2,
                1,
                0,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            frames,
            [Anm2PackedComponents.RotationX]);
    }
}
