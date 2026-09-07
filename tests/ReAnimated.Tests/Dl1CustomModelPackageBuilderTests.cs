using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class Dl1CustomModelPackageBuilderTests
{
    private const string OwnershipMarker = ".dl-reanimated-package-owned";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingBankPackageUsesSavedModeWithoutExportingReviewClips(bool includedReviewClip)
    {
        string parent = CreateTemporaryDirectory();
        try
        {
            string archive = Path.Combine(parent, "Data0.pak");
            using (var zip = System.IO.Compression.ZipFile.Open(archive, System.IO.Compression.ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("data/characters/animations/animscripts/existing_bank.scr").Open());
                writer.Write("SeqTrack(\"idle\",\"stock_idle\",0,2,30,1,0)\n");
            }
            StockAnimationReferenceTests.WriteCompiledBank(archive, "existing_bank");
            var model = CreateSyntheticModel(includedReviewClip);
            model = model with { Package = model.Package with { Document = model.Package.Document with
            {
                BuildSettings = model.Package.Document.BuildSettings with { ReferenceExistingAnimationLibrary = true,
                    AnimationScriptAlias = "existing_bank" },
            } } };
            var request = CreateRequest(parent, model) with
            {
                AnimationScriptAlias = "existing_bank",
                RetailData0PakPath = archive,
                AnimationExporterOverride = (_, _) => throw new InvalidOperationException("A stock-reference build must never export authored clips."),
                ModelCompilerOverride = async (compiler, token) =>
                {
                    Assert.True(compiler.Model.Package.Document.BuildSettings.ReferenceExistingAnimationLibrary);
                    Assert.Null(compiler.AnimationLibrary);
                    return await WriteSyntheticCompiledAsync(compiler, token);
                },
            };
            var result = await Dl1CustomModelPackageBuilder.BuildAsync(request);
            Assert.Null(result.AnimationLibrary);
            Assert.Equal("existing_bank", result.StockAnimationReference!.BankName);
            Assert.False(Directory.Exists(Path.Combine(result.PackageDirectory, "animations")));
            Assert.DoesNotContain(result.OutputSha256.Keys, path => path.EndsWith(".scr", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("existing_bank.scr", await File.ReadAllTextAsync(result.SourceModel.AnimationScriptPath!));
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
            Assert.True(manifest.RootElement.GetProperty("referenceExistingAnimationLibrary").GetBoolean());
            Assert.False(manifest.RootElement.GetProperty("runtimeAnimationBindingVerified").GetBoolean());
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    public async Task PackageRemapsCompanionTextureAndDependencyPathsAfterAtomicPublish()
    {
        string parent = CreateTemporaryDirectory();
        try
        {
            var request = CreateRequest(parent) with
            {
                ModelCompilerOverride = async (compiler, token) =>
                {
                    var compiled = await WriteSyntheticCompiledAsync(compiler, token);
                    string directory = Path.GetDirectoryName(compiled.OutputRpackPath)!;
                    string companion = Path.Combine(directory, "native-companions", "data", "characters", "sample", "sample.fed");
                    Directory.CreateDirectory(Path.GetDirectoryName(companion)!);
                    string raw = Path.Combine(directory, "sample.msh_compiler_obj");
                    string texture = Path.Combine(directory, "sample.dds_obj");
                    string dependency = Path.Combine(directory, "sample.msh.deps");
                    foreach (string path in new[] { companion, raw, texture, dependency }) await File.WriteAllTextAsync(path, "fixture", token);
                    return compiled with { NativeCompanionPaths = [companion], RawCompiledMeshObjectPath = raw,
                        CompiledTextureObjectPaths = [texture], DependencySidecars = [new("sample.msh_obj", "sample.msh.deps", dependency, new string('a', 64))] };
                },
            };
            var result = await Dl1CustomModelPackageBuilder.BuildAsync(request);
            foreach (string path in new[] { result.CompiledModel.NativeCompanionPaths.Single(), result.CompiledModel.RawCompiledMeshObjectPath!,
                result.CompiledModel.CompiledTextureObjectPaths.Single(), result.CompiledModel.DependencySidecars.Single().Path })
            {
                Assert.StartsWith(result.PackageDirectory + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(path));
                Assert.Contains(Path.GetRelativePath(result.PackageDirectory, path).Replace('\\', '/'), result.OutputSha256.Keys);
            }
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "PackageSmoke")]
    public async Task BuildPublishesCompleteValidatedPackageAndManifest()
    {
        string parent = CreateTemporaryDirectory();
        try
        {
            Dl1CustomModelPackageResult result = await Dl1CustomModelPackageBuilder.BuildAsync(
                CreateRequest(parent));

            Assert.Equal(
                Path.Combine(parent, "transaction_model_dl1_package"),
                result.PackageDirectory);
            Assert.True(File.Exists(Path.Combine(result.PackageDirectory, OwnershipMarker)));
            Assert.True(File.Exists(result.SourceModel.SourceMshPath));
            Assert.True(File.Exists(result.SourceModel.CharacterDefinitionPath));
            Assert.True(File.Exists(result.SourceModel.BoneScriptPath));
            Assert.True(File.Exists(result.CompiledModel.OutputRpackPath));
            Assert.True(File.Exists(result.CompiledModel.CompiledMeshObjectPath));
            Assert.Null(result.AnimationLibrary);

            Assert.Contains("loose/transaction_model.msh", result.OutputSha256.Keys);
            Assert.Contains("loose/transaction_model.chr", result.OutputSha256.Keys);
            Assert.Contains("loose/transaction_model.bscr", result.OutputSha256.Keys);
            Assert.Contains("compiled/transaction_model_pc.rpack", result.OutputSha256.Keys);
            Assert.Contains("compiled/transaction_model.msh_obj", result.OutputSha256.Keys);
            Assert.Contains("dl1-model-package.json", result.OutputSha256.Keys);

            using JsonDocument manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(result.ManifestPath));
            Assert.Equal(
                "dl-reanimated-csharp-dl1-model-package",
                manifest.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("transaction_model", manifest.RootElement.GetProperty("resourceName").GetString());
            Assert.NotEmpty(manifest.RootElement.GetProperty("outputs").EnumerateArray());
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "PackageSmoke")]
    public async Task BuildPublishesExactAnimationAliasAndLibraryAsOnePackage()
    {
        const string alias = "transaction_model_anim_script";
        string parent = CreateTemporaryDirectory();
        try
        {
            Dl1CustomModelPackageRequest request = CreateRequest(
                parent,
                CreateSyntheticModel(includeAnimation: true)) with
            {
                AnimationScriptAlias = alias,
                AnimationExporterOverride = WriteSyntheticAnimationsAsync,
            };

            Dl1CustomModelPackageResult result = await
                Dl1CustomModelPackageBuilder.BuildAsync(request);

            string ascr = Assert.IsType<string>(result.SourceModel.AnimationScriptPath);
            Assert.Equal(
                $"AnimScriptAlias(\"{alias}.scr\")",
                (await File.ReadAllTextAsync(ascr)).Trim());
            Assert.StartsWith(result.PackageDirectory, ascr, StringComparison.OrdinalIgnoreCase);

            CustomModelAnimationLibraryResult animations = Assert.IsType<CustomModelAnimationLibraryResult>(
                result.AnimationLibrary);
            Assert.Equal(alias, animations.AnimationScriptName);
            Assert.Equal(["generic_motion"], animations.AnimationNames.ToArray());
            Assert.True(File.Exists(animations.OutputPath));
            Assert.True(File.Exists(animations.ManifestPath));
            Assert.StartsWith(result.PackageDirectory, animations.OutputPath, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(result.PackageDirectory, animations.ManifestPath, StringComparison.OrdinalIgnoreCase);

            string animationRpackRelative = $"animations/{alias}_pc.rpack";
            string animationManifestRelative = $"animations/{alias}_pc.animations.json";
            Assert.Equal(await Sha256Async(ascr), result.OutputSha256["loose/transaction_model.ascr"]);
            Assert.Equal(
                await Sha256Async(animations.OutputPath),
                result.OutputSha256[animationRpackRelative]);
            Assert.Equal(
                await Sha256Async(animations.ManifestPath),
                result.OutputSha256[animationManifestRelative]);
            Assert.Equal(
                await Sha256Async(result.ManifestPath),
                result.OutputSha256["dl1-model-package.json"]);

            using JsonDocument manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(result.ManifestPath));
            Assert.Equal(alias, manifest.RootElement.GetProperty("animationScriptAlias").GetString());
            Assert.Equal(
                "generic_motion",
                Assert.Single(
                    manifest.RootElement.GetProperty("animations").EnumerateArray()).GetString());
            string[] outputPaths = manifest.RootElement.GetProperty("outputs")
                .EnumerateArray()
                .Select(static value => value.GetProperty("path").GetString()!)
                .ToArray();
            Assert.Contains("loose/transaction_model.ascr", outputPaths);
            Assert.Contains(animationRpackRelative, outputPaths);
            Assert.Contains(animationManifestRelative, outputPaths);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "PackageSmoke")]
    public async Task BuildRejectsAnimationAliasWithoutIncludedAnimationBeforeStaging()
    {
        string parent = CreateTemporaryDirectory();
        try
        {
            Dl1CustomModelPackageRequest request = CreateRequest(parent) with
            {
                AnimationScriptAlias = "orphan_animation_script",
            };

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Dl1CustomModelPackageBuilder.BuildAsync(request));

            Assert.Contains(
                "cannot be packaged without at least one included animation stack",
                error.Message,
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(parent, "transaction_model_dl1_package")));
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                ".transaction_model_dl1_package.*.staging",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "PackageSmoke")]
    public async Task FailedCompilerLeavesExistingOwnedPackageUntouched()
    {
        string parent = CreateTemporaryDirectory();
        string target = Path.Combine(parent, "transaction_model_dl1_package");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, OwnershipMarker), "owned\n");
        string sentinel = Path.Combine(target, "previous-package.bin");
        byte[] expected = [0x12, 0x34, 0x56, 0x78];
        await File.WriteAllBytesAsync(sentinel, expected);

        try
        {
            Dl1CustomModelPackageRequest request = CreateRequest(parent) with
            {
                ModelCompilerOverride = static (_, _) =>
                    Task.FromException<Dl1OfficialModelCompilerResult>(
                        new InvalidOperationException("Synthetic compiler failure.")),
            };

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Dl1CustomModelPackageBuilder.BuildAsync(request));

            Assert.Equal("Synthetic compiler failure.", error.Message);
            Assert.Equal(expected, await File.ReadAllBytesAsync(sentinel));
            Assert.True(File.Exists(Path.Combine(target, OwnershipMarker)));
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                ".transaction_model_dl1_package.*.staging",
                SearchOption.TopDirectoryOnly));
            Assert.Empty(Directory.EnumerateDirectories(
                parent,
                ".transaction_model_dl1_package.*.backup",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ReopenedModelInvalidatesHistoricalOrOlderCompilerReceipts()
    {
        FbxModelAuthoringImportResult model = CreateSyntheticModel();
        string fingerprint = new('a', 64);

        FbxModelAuthoringImportResult current = WithReceipt(
            model,
            CustomModelBuildState.CompilerValidated,
            Dl1OfficialModelCompiler.CurrentToolFingerprint,
            useCurrentInputFingerprint: true);
        FbxModelAuthoringImportResult currentResult =
            ModelsWorkspaceViewModel.NormalizeBuildReceipt(current, out string currentStatus);
        Assert.NotNull(currentResult.Package.Document.LastBuildReceipt);
        Assert.Contains("compiler-validated", currentStatus, StringComparison.OrdinalIgnoreCase);

        FbxModelAuthoringImportResult oldCompiler = WithReceipt(
            model,
            CustomModelBuildState.CompilerValidated,
            fingerprint);
        FbxModelAuthoringImportResult normalizedOld =
            ModelsWorkspaceViewModel.NormalizeBuildReceipt(oldCompiler, out string oldStatus);
        Assert.Null(normalizedOld.Package.Document.LastBuildReceipt);
        Assert.Contains("older model-output contract", oldStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(normalizedOld.Package.Document.Diagnostics, static diagnostic =>
            diagnostic.Code == "model_build_receipt_invalidated");

        FbxModelAuthoringImportResult staleInput = WithReceipt(
            model,
            CustomModelBuildState.CompilerValidated,
            Dl1OfficialModelCompiler.CurrentToolFingerprint);
        FbxModelAuthoringImportResult normalizedStaleInput =
            ModelsWorkspaceViewModel.NormalizeBuildReceipt(staleInput, out string staleInputStatus);
        Assert.Null(normalizedStaleInput.Package.Document.LastBuildReceipt);
        Assert.Contains("does not match", staleInputStatus, StringComparison.OrdinalIgnoreCase);

        FbxModelAuthoringImportResult legacyGameReady = WithReceipt(
            model,
            CustomModelBuildState.GameReady,
            Dl1OfficialModelCompiler.CurrentToolFingerprint);
        FbxModelAuthoringImportResult normalizedLegacy =
            ModelsWorkspaceViewModel.NormalizeBuildReceipt(legacyGameReady, out string legacyStatus);
        Assert.Null(normalizedLegacy.Package.Document.LastBuildReceipt);
        Assert.Contains("evidence boundary", legacyStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static FbxModelAuthoringImportResult WithReceipt(
        FbxModelAuthoringImportResult model,
        CustomModelBuildState state,
        string toolFingerprint,
        bool useCurrentInputFingerprint = false)
    {
        string fingerprint = new('b', 64);
        CustomModelBuildSettings settings = model.Package.Document.BuildSettings;
        string inputFingerprint = useCurrentInputFingerprint
            ? Dl1OfficialModelCompiler.CalculateInputFingerprint(
                model,
                settings.ResourceName,
                settings.SurfaceName,
                settings.AnimationScriptAlias)
            : fingerprint;
        CustomModelDocument document = model.Package.Document with
        {
            LastBuildReceipt = new CustomModelBuildReceipt
            {
                InputFingerprint = inputFingerprint,
                ToolFingerprint = toolFingerprint,
                CompilerFingerprint = fingerprint,
                OutputManifestFingerprint = fingerprint,
                State = state,
                CompletedUtc = DateTimeOffset.UnixEpoch,
            },
        };
        document.Validate();
        return model with
        {
            Package = new CustomModelPackage(
                document,
                model.Package.SourceFbx,
                model.Package.TexturePayloads),
        };
    }

    private static Dl1CustomModelPackageRequest CreateRequest(
        string parent,
        FbxModelAuthoringImportResult? model = null) => new()
    {
        Model = model ?? CreateSyntheticModel(),
        ParentOutputDirectory = parent,
        CompilerExecutablePath = "synthetic-compiler.exe",
        ResourceName = "transaction_model",
        SourceWriterOverride = WriteSyntheticSourceAsync,
        ModelCompilerOverride = WriteSyntheticCompiledAsync,
    };

    private static async Task<Dl1SourceModelBuildResult> WriteSyntheticSourceAsync(
        Dl1SourceModelBuildRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        string msh = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.msh");
        string chr = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.chr");
        string bscr = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.bscr");
        string manifest = Path.Combine(request.OutputDirectory, "source-model.json");
        string blocked = Path.Combine(request.OutputDirectory, "blocked-outputs.json");
        string? ascr = string.IsNullOrWhiteSpace(request.AnimationScriptAlias)
            ? null
            : Path.Combine(request.OutputDirectory, $"{request.ResourceName}.ascr");
        await WriteAsync(msh, "msh", cancellationToken);
        await WriteAsync(chr, "chr-v4", cancellationToken);
        await WriteAsync(bscr, "bscr", cancellationToken);
        if (ascr is not null)
        {
            await WriteAsync(
                ascr,
                $"AnimScriptAlias(\"{request.AnimationScriptAlias}.scr\")\n",
                cancellationToken);
        }
        await WriteAsync(manifest, "{}", cancellationToken);
        await WriteAsync(blocked, "{}", cancellationToken);
        return new Dl1SourceModelBuildResult(
            request.ResourceName,
            msh,
            chr,
            bscr,
            ascr,
            manifest,
            blocked,
            CustomModelBuildState.CompilerReady,
            [],
            [],
            ImmutableDictionary<string, string>.Empty,
            []);
    }

    private static async Task<Dl1OfficialModelCompilerResult> WriteSyntheticCompiledAsync(
        Dl1OfficialModelCompilerRequest request,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(request.OutputRpackPath)!;
        Directory.CreateDirectory(directory);
        string meshObject = Path.Combine(directory, $"{request.ResourceName}.msh_obj");
        string receipt = Path.Combine(directory, "compiler-receipt.json");
        await WriteAsync(request.OutputRpackPath, "RP6L", cancellationToken);
        await WriteAsync(meshObject, "mesh-object", cancellationToken);
        await WriteAsync(receipt, "{}", cancellationToken);
        string fingerprint = new('a', 64);
        string toolFingerprint =
            Dl1OfficialModelCompiler.CurrentToolFingerprint;
        string outputRpackSha256 = Convert.ToHexStringLower(
            SHA256.HashData(await File.ReadAllBytesAsync(
                request.OutputRpackPath,
                cancellationToken)));
        return new Dl1OfficialModelCompilerResult(
            request.ResourceName,
            request.OutputRpackPath,
            meshObject,
            null,
            receipt,
            fingerprint,
            [request.ResourceName],
            new CustomModelBuildReceipt
            {
                InputFingerprint = fingerprint,
                ToolFingerprint = toolFingerprint,
                CompilerFingerprint = fingerprint,
                OutputManifestFingerprint = fingerprint,
                State = CustomModelBuildState.CompilerValidated,
                CompletedUtc = DateTimeOffset.UnixEpoch,
            },
            "Synthetic compiler completed.")
        {
            CompilerEvidence = new Dl1OfficialModelCompilerEvidence
            {
                OutputRpackSha256 = outputRpackSha256,
                CompilerFingerprint = fingerprint,
                ToolFingerprint = toolFingerprint,
                BuildReceiptInputFingerprint = fingerprint,
                BuildReceiptOutputManifestFingerprint = fingerprint,
                BuildState = CustomModelBuildState.CompilerValidated,
                VerifiedMorphChannelCount = 0,
                VerifiedMorphBindingCount = 0,
                MorphDeltaFormat = null,
            },
        };
    }

    private static async Task<CustomModelAnimationLibraryResult> WriteSyntheticAnimationsAsync(
        CustomModelAnimationLibraryRequest request,
        CancellationToken cancellationToken)
    {
        string output = Path.GetFullPath(request.OutputPath);
        if (!string.Equals(Path.GetExtension(output), ".rpack", StringComparison.OrdinalIgnoreCase))
        {
            output += ".rpack";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string manifest = Path.ChangeExtension(output, ".animations.json");
        await WriteAsync(output, "synthetic-animation-rpack", cancellationToken);
        await WriteAsync(manifest, "{\"format\":\"synthetic-animation-library\"}", cancellationToken);
        return new CustomModelAnimationLibraryResult(
            output,
            manifest,
            await Sha256Async(output, cancellationToken),
            request.AnimationScriptAlias!,
            ["generic_motion"],
            []);
    }

    private static FbxModelAuthoringImportResult CreateSyntheticModel(bool includeAnimation = false)
    {
        byte[] source = "generic binary fbx fixture"u8.ToArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        Guid animationId = new("248c2ba0-f1cd-56c5-a6a4-f72188736796");
        ImmutableArray<CustomModelAnimationClip> animationMetadata = includeAnimation
            ?
            [
                new CustomModelAnimationClip
                {
                    Id = animationId,
                    FbxObjectId = 2,
                    SourceName = "Generic Motion",
                    DisplayName = "generic_motion",
                    Included = true,
                    FrameRate = new FrameRate(30, 1),
                    StartFrame = 0,
                    FrameCount = 2,
                    RootMotionMode = Dl1RootMotionMode.Recorded,
                    RootBoneName = "root",
                    SourceFingerprint = fingerprint,
                },
            ]
            : [];
        var document = new CustomModelDocument
        {
            Name = "Generic transaction fixture",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "generic.fbx",
                ContentSha256 = fingerprint,
                FbxVersion = 7400,
            },
            RigSignature = fingerprint,
            Bones =
            [
                new CustomModelBone
                {
                    Index = 0,
                    FbxObjectId = 1,
                    Name = "root",
                    ParentIndex = -1,
                    LocalBindTransform = TransformTRS.Identity,
                    ExactLocalBindMatrix = TransformMatrix.Identity,
                    Kind = BoneKind.Root,
                    IsWeighted = true,
                },
            ],
            Meshes = [],
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = new Guid("ed987817-b29c-5ce9-aa29-f7f76161fd44"),
                    Name = "existing material",
                    ExistingDl1MaterialReference = "data/materials/generic_existing.mat",
                },
            ],
            AnimationClips = animationMetadata,
            Diagnostics = [],
        };
        document.Validate();
        var package = new CustomModelPackage(
            document,
            source.ToImmutableArray(),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
        ImmutableDictionary<Guid, AnimationClip> clips = includeAnimation
            ? ImmutableDictionary<Guid, AnimationClip>.Empty.Add(
                animationId,
                new AnimationClip(
                    "Generic Motion",
                    new FrameRate(30, 1),
                    2,
                    [
                        new TransformTrack(
                            0,
                            [
                                new TransformKeyframe(0, TransformTRS.Identity),
                                new TransformKeyframe(1, TransformTRS.Identity),
                            ]),
                    ]))
            : ImmutableDictionary<Guid, AnimationClip>.Empty;
        return new FbxModelAuthoringImportResult(
            package,
            document.CreateRigDefinition(),
            [],
            clips,
            new FbxStrictExportInspection(
                [],
                ImmutableDictionary<string, FbxAnimationStackInspection>.Empty,
                ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, long?>.Empty,
                [],
                0,
                0,
                ImmutableHashSet<string>.Empty,
                ImmutableHashSet<string>.Empty,
                ImmutableDictionary<string, FbxMeshGeometryInspection>.Empty,
                0,
                0,
                [],
                [],
                ImmutableHashSet<string>.Empty));
    }

    private static Task WriteAsync(string path, string contents, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dlr-package-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
