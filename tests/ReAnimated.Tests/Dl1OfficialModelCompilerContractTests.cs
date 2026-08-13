using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.Tests;

public sealed class Dl1OfficialModelCompilerContractTests
{
    [Theory]
    [InlineData(
        "// AnimScriptAlias(\"GenericLibrary.scr\")\nAnimScriptAlias(\"WrongLibrary.scr\")\n")]
    [InlineData(
        "AnimScriptAlias(\"WrongLibrary.scr\")\nAnimScriptAlias(\"GenericLibrary.scr\")\n")]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task DeploymentRejectsExpectedAscrAliasOutsideTheSingleEffectiveDirective(
        string stagedAscr)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1DeveloperToolsDeploymentRequest request = CreateDeploymentRequest(
                directory,
                stagedAscr);

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Dl1DeveloperToolsProjectDeployer.DeployAsync(request));

            Assert.Contains("ASCR", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(request.ProjectRoot));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AnimationCompilerUsesIsolatedOneResourceUpdateContract()
    {
        PreparedCustomModelAnimation[] animations =
        [
            CreateAnimation("synthetic_idle"),
            CreateAnimation("synthetic_walk"),
        ];

        string firstResourceScript = Dl1OfficialModelCompiler.CreateAnimationResourceScript([animations[0]]);
        Assert.Contains(
            "res( _ANIMATION_, \"synthetic_idle\", \"data/characters/animations/synthetic_idle.anm2\", \"\", true, \"\");",
            firstResourceScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic_walk", firstResourceScript, StringComparison.Ordinal);

        string secondResourceScript = Dl1OfficialModelCompiler.CreateAnimationResourceScript([animations[1]]);
        Assert.Contains(
            "res( _ANIMATION_, \"synthetic_walk\", \"data/characters/animations/synthetic_walk.anm2\", \"\", true, \"\");",
            secondResourceScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic_idle", secondResourceScript, StringComparison.Ordinal);
        Assert.DoesNotContain("_MESH_", firstResourceScript, StringComparison.Ordinal);
        Assert.DoesNotContain("*.anm2", firstResourceScript, StringComparison.Ordinal);

        string foldersScript = Dl1OfficialModelCompiler.CreateAnimationResourcePackFoldersScript();
        Assert.Contains("resources(_ANIMATION_);", foldersScript, StringComparison.Ordinal);
        Assert.DoesNotContain("_MESH_", foldersScript, StringComparison.Ordinal);
        Assert.Equal("ResourceRule(\"*.anm2\")\n", Dl1OfficialModelCompiler.CreateAnimationResourceRules());

        string jobDirectory = TestPaths.Combine("animation-compiler", "contract");
        string workshopDirectory = Path.Combine(jobDirectory, "Workshop");
        string outputDirectory = Path.Combine(jobDirectory, "CompilerAnimations");
        string rulesPath = Path.Combine(jobDirectory, "animation_resource.rules");
        string resourceScriptPath = Path.Combine(jobDirectory, "animation_resources.rsrc");
        ImmutableArray<string> command = Dl1OfficialModelCompiler.CreateAnimationCompilerCommand(
            "_DLReAnimatedAnimationImporter_contract",
            workshopDirectory,
            outputDirectory,
            rulesPath,
            resourceScriptPath,
            "SyntheticLibrary_PC.rpack");

        Assert.Contains("*.*", command);
        Assert.DoesNotContain("data/characters/animations/*.*", command);
        Assert.Contains(command, static argument => argument.StartsWith("-updatefromrscr=", StringComparison.Ordinal));
        Assert.Contains(command, static argument => argument.StartsWith("/ScriptRules=", StringComparison.Ordinal));
        Assert.Contains("output=SyntheticLibrary_PC.rpack", command);
        Assert.Contains($"out={outputDirectory}", command);
        Assert.Contains("/Verbose", command);
        Assert.Contains("/ShowFiles", command);
        Assert.Contains("/ShowMissingFiles", command);
        Assert.Contains("/SaveDependencies", command);
        Assert.Contains("/LooseResources", command);
        Assert.Contains("/LooseGpuResources", command);
        Assert.DoesNotContain("/Silent", command);
        Assert.DoesNotContain("/Spawned", command);
        Assert.DoesNotContain("/NoLogs", command);
        Assert.DoesNotContain("/MP", command);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AnimationCompilerAdmitsOnlyKnownPostEmissionCrashForOneIsolatedObject()
    {
        Dl1OfficialModelCompiler.ValidateAnimationCompilerExitCode(0, emittedObjectCount: 2);

        Dl1OfficialModelCompiler.ValidateAnimationCompilerExitCode(
            unchecked((int)0xC0000005),
            emittedObjectCount: 1,
            animationName: "synthetic_walk");
        Dl1OfficialModelCompiler.ValidateAnimationCompilerExitCode(
            unchecked((int)0xC000001D),
            emittedObjectCount: 1,
            animationName: "synthetic_walk");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Dl1OfficialModelCompiler.ValidateAnimationCompilerExitCode(
                exitCode: 7,
                emittedObjectCount: 1,
                animationName: "synthetic_walk"));

        Assert.Contains("code 7", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 emitted animation object", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic_walk", error.Message, StringComparison.Ordinal);
        Assert.Contains("rejected", error.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException missingObject = Assert.Throws<InvalidDataException>(() =>
            Dl1OfficialModelCompiler.ValidateAnimationCompilerExitCode(
                unchecked((int)0xC0000005),
                emittedObjectCount: 0,
                animationName: "synthetic_walk"));
        Assert.Contains("-1073741819", missingObject.Message, StringComparison.Ordinal);
        Assert.Contains("0 emitted animation object", missingObject.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task AnimationCompilerValidatesAbsoluteAddressedObjectPayloadBeforeAdmission()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            PreparedCustomModelAnimation animation = CreateAnimation("synthetic_walk");
            string compilerObjectPath = Path.Combine(directory, "synthetic_walk.anm2_obj");
            await File.WriteAllBytesAsync(
                compilerObjectPath,
                BuildSyntheticAnimationCompilerObject(
                    animation.Name,
                    animation.Payload));

            string contract = await Dl1OfficialModelCompiler.ValidateCompiledAnimationObjectAsync(
                compilerObjectPath,
                animation,
                Path.Combine(directory, "normalized.rpack"));

            Assert.Contains("absolute-addressed", contract, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "normalized.rpack")));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task AnimationCompilerRejectsAbsoluteAddressedObjectWithDifferentPayload()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            PreparedCustomModelAnimation animation = CreateAnimation("synthetic_walk");
            string compilerObjectPath = Path.Combine(directory, "synthetic_walk.anm2_obj");
            byte[] differentPayload = animation.Payload.ToArray();
            differentPayload[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(
                compilerObjectPath,
                BuildSyntheticAnimationCompilerObject(
                    animation.Name,
                    differentPayload));

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Dl1OfficialModelCompiler.ValidateCompiledAnimationObjectAsync(
                    compilerObjectPath,
                    animation,
                    Path.Combine(directory, "normalized.rpack")));

            Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void IncrementalMaterialCompilerCommandOmitsRebuild()
    {
        string projectDirectory = TestPaths.Combine("material-compiler", "contract", "project");
        string workshopDirectory = TestPaths.Combine("material-compiler", "contract", "Workshop");
        const string virtualDirectory = "data/characters/synthetic_character";

        ImmutableArray<string> command = Dl1OfficialModelCompiler.CreateMaterialCompilerCommand(
            projectDirectory,
            workshopDirectory,
            virtualDirectory,
            forceRebuild: false);

        Assert.DoesNotContain("-rebuild", command);
        Assert.Contains("data/characters/synthetic_character/*.dmt", command);
        Assert.Contains("-game", command);
        Assert.Contains("project", command);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void MaterialDatabasePreservationChecksTheWholeMaterialPayload()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string originalPath = Path.Combine(directory, "original.mp");
            string unchangedPath = Path.Combine(directory, "unchanged.mp");
            string changedPath = Path.Combine(directory, "changed.mp");
            byte[] original = BuildSyntheticMaterialDatabase(fixedFieldValue: 0x1020_3040);
            File.WriteAllBytes(originalPath, original);
            File.WriteAllBytes(unchangedPath, original);
            File.WriteAllBytes(changedPath, BuildSyntheticMaterialDatabase(fixedFieldValue: 0x5060_7080));

            Dl1OfficialModelCompiler.ValidateCompiledMaterialDatabasePreserves(
                originalPath,
                unchangedPath);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                Dl1OfficialModelCompiler.ValidateCompiledMaterialDatabasePreserves(
                    originalPath,
                    changedPath));
            Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PreparedCustomModelAnimation CreateAnimation(string name) =>
        new(
            name,
            $"{name}.anm2",
            [0x41, 0x4E, 0x4D, 0x32],
            FrameCount: 31,
            FramesPerSecond: 30,
            SourceName: "Synthetic Stack",
            SourceFingerprint: new string('a', 64),
            RootMotionMode: Dl1RootMotionMode.Recorded,
            RootBoneName: null);

    private static Dl1DeveloperToolsDeploymentRequest CreateDeploymentRequest(
        string directory,
        string stagedAscr)
    {
        string projectRoot = Path.Combine(directory, "project");
        string toolsRoot = Path.Combine(directory, "tools");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(toolsRoot);
        string compilerPath = Path.Combine(toolsRoot, "compiler.exe");
        string retailDataPath = Path.Combine(toolsRoot, "Data0.pak");
        File.WriteAllBytes(compilerPath, "synthetic compiler marker"u8.ToArray());
        File.WriteAllBytes(retailDataPath, "synthetic retail marker"u8.ToArray());
        return new Dl1DeveloperToolsDeploymentRequest
        {
            Model = CreateSyntheticAnimatedModel(),
            ProjectRoot = projectRoot,
            CompilerExecutablePath = compilerPath,
            RetailData0PakPath = retailDataPath,
            CharacterId = "generic_character",
            ModelResourceName = "GenericModel",
            SurfaceName = "default",
            AnimationLibraryName = "GenericLibrary",
            InstallLooseAnm2 = false,
            ExportPortableAnimationRpack = false,
            SourceWriterOverride = (request, cancellationToken) =>
                WriteSyntheticSourceAsync(request, stagedAscr, cancellationToken),
            ModelCompilerOverride = WriteSyntheticModelCompilerAsync,
            AnimationCompilerOverride = WriteSyntheticAnimationsAsync,
        };
    }

    private static FbxModelAuthoringImportResult CreateSyntheticAnimatedModel()
    {
        byte[] source = "Kaydara FBX Binary  generic ASCR fixture"u8.ToArray();
        string sourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        Guid clipId = new("be85daa7-2310-5a0d-931e-024823163906");
        var document = new CustomModelDocument
        {
            ModelId = new Guid("49037a1f-c6dd-52f7-b035-df34db579849"),
            Name = "Generic animated model",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "generic.fbx",
                ContentSha256 = sourceHash,
                FbxVersion = 7400,
            },
            RigSignature = sourceHash,
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
            Materials = [],
            AnimationClips =
            [
                new CustomModelAnimationClip
                {
                    Id = clipId,
                    FbxObjectId = 2,
                    SourceName = "Idle Take",
                    DisplayName = "idle_loop",
                    FrameRate = new FrameRate(30, 1),
                    StartFrame = 0,
                    FrameCount = 3,
                    RootMotionMode = Dl1RootMotionMode.Recorded,
                    RootBoneName = "root",
                    SourceFingerprint = sourceHash,
                    Included = true,
                },
            ],
            Diagnostics = [],
            BuildSettings = new CustomModelBuildSettings
            {
                CharacterId = "generic_character",
                ResourceName = "GenericModel",
                SurfaceName = "default",
                AnimationScriptAlias = "GenericLibrary",
            },
        };
        document.Validate();
        var package = new CustomModelPackage(
            document,
            source.ToImmutableArray(),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
        var animation = new AnimationClip(
            "Idle Take",
            new FrameRate(30, 1),
            3,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            2,
                            new TransformTRS(
                                new Vector3D(0.1, 0.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);
        return new FbxModelAuthoringImportResult(
            package,
            document.CreateRigDefinition(),
            [],
            ImmutableDictionary<Guid, AnimationClip>.Empty.Add(clipId, animation),
            new FbxStrictExportInspection(
                [],
                ImmutableDictionary<string, FbxAnimationStackInspection>.Empty,
                ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, long?>.Empty,
                [],
                0,
                0,
                [],
                [],
                ImmutableDictionary<string, FbxMeshGeometryInspection>.Empty,
                0,
                0,
                [],
                [],
                []));
    }

    private static async Task<Dl1SourceModelBuildResult> WriteSyntheticSourceAsync(
        Dl1SourceModelBuildRequest request,
        string stagedAscr,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        string msh = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.msh");
        string chr = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.chr");
        string bscr = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.bscr");
        string ascr = Path.Combine(request.OutputDirectory, $"{request.ResourceName}.ascr");
        string manifest = Path.Combine(request.OutputDirectory, "source-model.json");
        string blocked = Path.Combine(request.OutputDirectory, "blocked-outputs.json");
        await WriteTextAsync(msh, "synthetic msh", cancellationToken);
        await WriteTextAsync(chr, "synthetic chr", cancellationToken);
        await WriteTextAsync(bscr, "synthetic bscr", cancellationToken);
        await WriteTextAsync(ascr, stagedAscr, cancellationToken);
        await WriteTextAsync(manifest, "{}", cancellationToken);
        await WriteTextAsync(blocked, "{}", cancellationToken);
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

    private static async Task<Dl1OfficialModelCompilerResult> WriteSyntheticModelCompilerAsync(
        Dl1OfficialModelCompilerRequest request,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(request.OutputRpackPath)!;
        Directory.CreateDirectory(directory);
        string meshObject = Path.Combine(directory, $"{request.ResourceName}.msh_obj");
        string receipt = Path.Combine(directory, "compiler-receipt.json");
        await WriteTextAsync(request.OutputRpackPath, "synthetic rpack", cancellationToken);
        await WriteTextAsync(meshObject, "synthetic mesh object", cancellationToken);
        await WriteTextAsync(receipt, "{}", cancellationToken);
        string fingerprint = new('a', 64);
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
                ToolFingerprint = fingerprint,
                CompilerFingerprint = fingerprint,
                OutputManifestFingerprint = fingerprint,
                State = CustomModelBuildState.CompilerValidated,
                CompletedUtc = DateTimeOffset.UnixEpoch,
            },
            "Synthetic compiler completed.");
    }

    private static async Task<Dl1OfficialAnimationCompilerResult> WriteSyntheticAnimationsAsync(
        Dl1OfficialAnimationCompilerRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(request.OutputDirectory);
        var paths = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PreparedCustomModelAnimation animation in request.Library.Animations)
        {
            string path = Path.Combine(request.OutputDirectory, $"{animation.Name}.anm2_obj");
            await WriteTextAsync(path, $"compiled {animation.Name}", cancellationToken);
            paths.Add(animation.Name, path);
        }

        return new Dl1OfficialAnimationCompilerResult(
            paths.ToImmutable(),
            new string('b', 64),
            "Synthetic animation compiler completed.");
    }

    private static async Task WriteTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents, cancellationToken);
    }

    private static byte[] BuildSyntheticMaterialDatabase(uint fixedFieldValue)
    {
        const int headerSize = 16;
        const int containerRowSize = 48;
        const int materialRowSize = 16;
        const int materialPayloadSize = 24;
        const int materialTableOffset = headerSize + containerRowSize;
        const int materialPayloadOffset = materialTableOffset + materialRowSize;
        uint materialHash = Dl1ResourceNameHash.Compute("synthetic_surface.mat");

        byte[] output = new byte[materialPayloadOffset + materialPayloadSize];
        "ABDM"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), headerSize);

        "materials"u8.CopyTo(output.AsSpan(headerSize));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(headerSize + 32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(headerSize + 36), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(headerSize + 40), materialTableOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(materialTableOffset), materialHash);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(materialTableOffset + 4), materialPayloadOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(materialTableOffset + 8), materialPayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(materialTableOffset + 12), materialPayloadSize);

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(materialPayloadOffset), materialHash);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(materialPayloadOffset + 4), fixedFieldValue);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(materialPayloadOffset + 16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(materialPayloadOffset + 18), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(materialPayloadOffset + 22), 2);
        return output;
    }

    private static byte[] BuildSyntheticAnimationCompilerObject(
        string resourceName,
        byte[] animationPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(animationPayload);

        byte[] builderPayload = Encoding.UTF8.GetBytes($"+{resourceName}\n");
        byte[] names = Encoding.UTF8.GetBytes($"_ANIMATION_\0{resourceName}\0");
        const int chunkCount = 2;
        const int itemCount = 2;
        const int resourceCount = 2;
        const int nameCount = 2;
        int tableSize = checked(
            36 +
            (20 * chunkCount) +
            (16 * itemCount) +
            (12 * resourceCount) +
            (4 * nameCount) +
            names.Length);
        int animationOffset = tableSize;
        int builderOffset = checked(animationOffset + animationPayload.Length);
        byte[] output = new byte[checked(builderOffset + builderPayload.Length)];
        Span<byte> data = output;
        "RP6L"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(data[8..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(data[12..], itemCount);
        BinaryPrimitives.WriteInt32LittleEndian(data[16..], chunkCount);
        BinaryPrimitives.WriteInt32LittleEndian(data[20..], resourceCount);
        BinaryPrimitives.WriteInt32LittleEndian(data[24..], names.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data[28..], nameCount);
        BinaryPrimitives.WriteInt32LittleEndian(data[32..], 1);

        int cursor = 36;
        WriteChunk(
            data,
            ref cursor,
            flags: 64,
            category: 258,
            offset: animationOffset,
            size: animationPayload.Length,
            unknown1: 1,
            unknown2: 2);
        WriteChunk(
            data,
            ref cursor,
            flags: 255,
            category: 4,
            offset: builderOffset,
            size: builderPayload.Length,
            unknown1: 1,
            unknown2: 1);

        WriteItem(data, ref cursor, chunkIndex: 1, logicalType: 0, builderPayload.Length);
        WriteItem(data, ref cursor, chunkIndex: 0, logicalType: 1, animationPayload.Length);

        WriteResource(
            data,
            ref cursor,
            Rp6lResourceTypes.BuilderInformation,
            nameIndex: 0,
            firstItemIndex: 0);
        WriteResource(
            data,
            ref cursor,
            Rp6lResourceTypes.Animation,
            nameIndex: 1,
            firstItemIndex: 1);

        BinaryPrimitives.WriteInt32LittleEndian(data[cursor..], 0);
        cursor += 4;
        BinaryPrimitives.WriteInt32LittleEndian(
            data[cursor..],
            Encoding.UTF8.GetByteCount("_ANIMATION_") + 1);
        cursor += 4;
        names.CopyTo(data[cursor..]);
        animationPayload.CopyTo(data[animationOffset..]);
        builderPayload.CopyTo(data[builderOffset..]);
        return output;

        static void WriteChunk(
            Span<byte> destination,
            ref int cursor,
            ushort flags,
            ushort category,
            int offset,
            int size,
            ushort unknown1,
            ushort unknown2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination[cursor..], flags);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(cursor + 2)..], category);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(cursor + 4)..], checked((uint)offset));
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(cursor + 8)..], checked((uint)size));
            BinaryPrimitives.WriteInt32LittleEndian(destination[(cursor + 12)..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(cursor + 16)..], unknown1);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[(cursor + 18)..], unknown2);
            cursor += 20;
        }

        static void WriteItem(
            Span<byte> destination,
            ref int cursor,
            byte chunkIndex,
            short logicalType,
            int size)
        {
            destination[cursor] = chunkIndex;
            destination[cursor + 1] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(destination[(cursor + 2)..], logicalType);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(cursor + 4)..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(cursor + 8)..], size);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(cursor + 12)..], 0);
            cursor += 16;
        }

        static void WriteResource(
            Span<byte> destination,
            ref int cursor,
            short resourceType,
            int nameIndex,
            int firstItemIndex)
        {
            BinaryPrimitives.WriteInt16LittleEndian(destination[cursor..], 1);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(cursor + 2)..], resourceType);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(cursor + 4)..], nameIndex);
            BinaryPrimitives.WriteInt32LittleEndian(destination[(cursor + 8)..], firstItemIndex);
            cursor += 12;
        }
    }
}
