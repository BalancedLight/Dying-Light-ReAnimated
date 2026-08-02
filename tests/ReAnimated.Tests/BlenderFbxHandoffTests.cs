using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class BlenderFbxHandoffTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-BlenderFbx-{Guid.NewGuid():N}");

    [Fact]
    public async Task EmbeddedHelperExtractsTheMultiActionBindPoseContract()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string extractionDirectory = Path.Combine(
            _temporaryDirectory,
            "embedded-helper");
        var resource = new BlenderHelperResource();

        string resourceName = resource.ResolveResourceName();
        string path = await resource.ExtractAsync(
            extractionDirectory,
            CancellationToken.None);
        string helper = await File.ReadAllTextAsync(
            path,
            CancellationToken.None);

        Assert.EndsWith(
            BlenderHelperResource.ResourceSuffix,
            resourceName,
            StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(
                extractionDirectory,
                "export_dl1_retail_anm2_fbx.py"),
            path);
        Assert.Contains(
            "child_pivot_display_v1",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "bake_anim_use_all_actions=True",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "embed_textures=bool(job.get(\"embed_textures\", False))",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"COPY\"",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "DLR_ACTION_STACKS:",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "DLR_BIND_POSE:",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "DLR_ROOT_PARITY:",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "Failed to install decoded custom normals",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "active_bone_indices",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "weighted_clusters",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "animations_do_with_action_name",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "has no positive influence",
            helper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "keyframe_insert",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "keyframe_points.foreach_set(\"co\"",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "bake_anim_use_all_bones=False",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "bake_anim_force_startend_keying=False",
            helper,
            StringComparison.Ordinal);
        AssertOccursOnce(
            helper,
            "bpy.context.view_layer.update()");
        AssertOccursOnce(
            helper,
            "bpy.context.collection.objects.link(armature)");
    }

    [Fact]
    public void ResolverPersistsAndLoadsAConfiguredBlenderExecutable()
    {
        string executableDirectory = Path.Combine(
            _temporaryDirectory,
            "Configured Blender");
        Directory.CreateDirectory(executableDirectory);
        string executablePath = Path.Combine(
            executableDirectory,
            "blender.exe");
        File.WriteAllBytes(executablePath, []);
        string settingsPath = Path.Combine(
            _temporaryDirectory,
            "Settings",
            "blender.json");
        var resolver = new BlenderExecutableResolver(settingsPath);

        resolver.SaveConfiguredPath(
            $"\"{executablePath}\"");

        string expected = Path.GetFullPath(executablePath);
        Assert.Equal(expected, resolver.LoadConfiguredPath());
        Assert.Equal(expected, resolver.Resolve());
        string json = File.ReadAllText(settingsPath);
        Assert.Contains(
            "\"executablePath\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "blender.exe",
            json,
            StringComparison.OrdinalIgnoreCase);

        File.Delete(executablePath);
        Assert.Null(resolver.LoadConfiguredPath());
    }

    [Fact]
    public async Task ServiceCommitsTwoActionsTextureAndSchemaOneHandoff()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string firstClipPath = await WriteClipAsync(
            "Idle",
            CreateClip(
                "Idle",
                rig,
                frameCount: 3,
                rootDistance: 0.25),
            rig,
            sourceFbxFps: 30.0);
        string secondClipPath = await WriteClipAsync(
            "Vault",
            CreateClip(
                "Vault",
                rig,
                frameCount: 5,
                rootDistance: 1.5),
            rig,
            sourceFbxFps: 60.0);
        MeshRenderData mesh = CreateTexturedMesh();
        string blenderPath = Path.Combine(
            _temporaryDirectory,
            "blender.exe");
        File.WriteAllBytes(blenderPath, []);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Output",
            "retail-player.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result = await service.ExportAsync(
            new BlenderFbxExportRequest(
                blenderPath,
                outputPath,
                new BlenderFbxAssetIdentity(
                    "retail:player",
                    "Data0.pak",
                    "player_1_tpp.msh",
                    new string('a', 64)),
                rig,
                [mesh],
                [firstClipPath, secondClipPath]),
            cancellationToken:
                CancellationToken.None);

        Assert.Equal(outputPath, result.OutputFbxPath);
        Assert.Equal(
            ["Idle", "Vault"],
            result.AnimationStacks);
        Assert.Equal(rig.BoneCount, result.BoneCount);
        Assert.Equal(1, result.MeshCount);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(
            "FAKE-FBX",
            await File.ReadAllTextAsync(
                outputPath,
                CancellationToken.None));
        Assert.Single(result.TexturePaths);
        Assert.True(File.Exists(result.TexturePaths[0]));
        byte[] ddsHeader = new byte[4];
        await using (FileStream texture = File.OpenRead(
                         result.TexturePaths[0]))
        {
            int read = await texture.ReadAsync(
                ddsHeader,
                CancellationToken.None);
            Assert.Equal(ddsHeader.Length, read);
        }

        Assert.Equal("DDS "u8.ToArray(), ddsHeader);
        Assert.True(File.Exists(result.HandoffManifestPath));
        Assert.False(
            File.Exists(
                outputPath +
                ".dlrroundtrip.json"));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains(
                "shared multi-action rate",
                StringComparison.Ordinal));

        Assert.NotNull(runner.JobJson);
        using JsonDocument jobDocument =
            JsonDocument.Parse(runner.JobJson);
        JsonElement job = jobDocument.RootElement;
        Assert.Equal(
            BlenderFbxExportService.JobFormat,
            job.GetProperty("format").GetString());
        Assert.Equal(
            BlenderFbxExportService.JobSchemaVersion,
            job.GetProperty("schema_version").GetInt32());
        Assert.Equal(
            30.0,
            job.GetProperty("fbx_output_fps").GetDouble());
        Assert.Equal(
            ["Idle", "Vault"],
            job.GetProperty("clips")
                .EnumerateArray()
                .Select(clip =>
                    clip.GetProperty("action_name")
                        .GetString()!)
                .ToArray());
        Assert.Equal(2, runner.StagedClipCount);
        Assert.Equal(1, runner.StagedMeshCount);
        Assert.Equal(1, runner.StagedTextureCount);

        string manifestJson = await File.ReadAllTextAsync(
            result.HandoffManifestPath,
            CancellationToken.None);
        using JsonDocument manifestDocument =
            JsonDocument.Parse(manifestJson);
        JsonElement manifest = manifestDocument.RootElement;
        Assert.Equal(
            BlenderFbxExportService.HandoffFormat,
            manifest.GetProperty("format").GetString());
        Assert.Equal(
            BlenderFbxExportService.HandoffSchemaVersion,
            manifest.GetProperty("schema_version").GetInt32());
        Assert.Equal(
            "child_pivot_display_v1",
            manifest.GetProperty("basis_mode").GetString());
        Assert.Equal(
            "armature_edit_rest_with_roundtrip_guard",
            manifest.GetProperty("bind_pose_mode").GetString());
        Assert.Contains(
            "do not redistribute",
            manifest.GetProperty("redistribution_warning")
                .GetString()!,
            StringComparison.OrdinalIgnoreCase);
        JsonElement[] manifestClips = manifest
            .GetProperty("clips")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, manifestClips.Length);
        Assert.All(
            manifestClips,
            clip =>
            {
                Assert.Equal(
                    "valid",
                    clip.GetProperty(
                            "timing_metadata_status")
                        .GetString());
                Assert.Equal(
                    30.0,
                    clip.GetProperty("fbx_output_fps")
                        .GetDouble());
                Assert.Contains(
                    clip.GetProperty("helper_tracks")
                        .EnumerateArray(),
                    helper =>
                        helper.GetProperty("descriptor")
                            .GetUInt32() ==
                        HelperDescriptor);
            });
        Assert.DoesNotContain(
            "\"binary_path\"",
            manifestJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"bind_translation\"",
            manifestJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"vertices\"",
            manifestJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "legacy one-clip .fbx.dlrroundtrip.json",
            manifestJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceCommitsSelfContainedMeshOnlyFbx()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        MeshRenderData mesh = CreateTexturedMesh();
        string blenderPath = Path.Combine(
            _temporaryDirectory,
            "blender.exe");
        File.WriteAllBytes(blenderPath, []);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Output",
            "retail-player.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result = await service.ExportAsync(
            new BlenderFbxExportRequest(
                blenderPath,
                outputPath,
                new BlenderFbxAssetIdentity(
                    "retail:player",
                    "Data0.pak",
                    "player_1_tpp.msh",
                    new string('a', 64)),
                rig,
                [mesh],
                [])
            {
                EmbedTextures = true,
            },
            cancellationToken:
                CancellationToken.None);

        Assert.Equal(outputPath, result.OutputFbxPath);
        Assert.Empty(result.AnimationStacks);
        Assert.Equal(rig.BoneCount, result.BoneCount);
        Assert.Equal(1, result.MeshCount);
        Assert.True(result.TexturesEmbedded);
        Assert.Empty(result.TexturePaths);
        Assert.Single(result.EmbeddedTextureFileNames);
        Assert.True(File.Exists(outputPath));
        Assert.Empty(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(outputPath)!,
                "*.dds"));

        Assert.NotNull(runner.JobJson);
        using JsonDocument jobDocument =
            JsonDocument.Parse(runner.JobJson);
        JsonElement job = jobDocument.RootElement;
        Assert.True(
            job.GetProperty("embed_textures")
                .GetBoolean());
        Assert.Equal(
            0,
            job.GetProperty("clips")
                .GetArrayLength());
        Assert.Equal(
            rig.BoneCount,
            job.GetProperty("bones")
                .GetArrayLength());
        Assert.True(
            job.GetProperty("textures")[0]
                .GetProperty("embedded_in_fbx")
                .GetBoolean());

        string manifestJson = await File.ReadAllTextAsync(
            result.HandoffManifestPath,
            CancellationToken.None);
        using JsonDocument manifestDocument =
            JsonDocument.Parse(manifestJson);
        JsonElement manifest = manifestDocument.RootElement;
        Assert.True(
            manifest.GetProperty("textures_embedded")
                .GetBoolean());
        Assert.Equal(
            0,
            manifest.GetProperty("texture_files")
                .GetArrayLength());
        Assert.Single(
            manifest.GetProperty("embedded_texture_files")
                .EnumerateArray());
    }

    [Fact]
    public async Task ValidProvenanceDrivesReverseInputAndOutputRates()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "Timed",
            [RootDescriptor, HelperDescriptor],
            frameCount: 381,
            sampleFps: 30.0,
            sourceFbxFps: 24.0,
            playbackFps: 60.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Timed",
            "timed.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);

        Assert.Single(result.AnimationStacks);
        Assert.NotNull(runner.JobJson);
        using JsonDocument jobDocument =
            JsonDocument.Parse(runner.JobJson);
        JsonElement jobClip = jobDocument.RootElement
            .GetProperty("clips")[0];
        Assert.Equal(
            "valid",
            jobClip.GetProperty("timing_metadata_status")
                .GetString());
        Assert.Equal(
            30.0,
            jobClip.GetProperty("anm2_input_fps")
                .GetDouble());
        Assert.Equal(
            24.0,
            jobClip.GetProperty("fbx_output_fps")
                .GetDouble());
        Assert.Equal(
            381,
            jobClip.GetProperty("source_frame_count")
                .GetInt32());
        Assert.Equal(
            305,
            jobClip.GetProperty("fbx_frame_count")
                .GetInt32());

        byte[] binary = Assert.IsType<byte[]>(
            runner.FirstStagedClipBinary);
        Assert.Equal(
            "DLRANM1\0"u8.ToArray(),
            binary[..8]);
        Assert.Equal(305, BitConverter.ToInt32(binary, 8));
        int boneCount = BitConverter.ToInt32(binary, 12);
        Assert.Equal(rig.BoneCount, boneCount);
        const int transformBytes = 10 * sizeof(float);
        int firstRootOffset = 16;
        int lastRootOffset = checked(
            16 +
            ((305 - 1) * boneCount * transformBytes));
        Assert.Equal(
            0.0f,
            BitConverter.ToSingle(
                binary,
                firstRootOffset));
        Assert.Equal(
            380.0f,
            BitConverter.ToSingle(
                binary,
                lastRootOffset));
    }

    [Fact]
    public async Task ServiceRejectsMimicOnlyZeroOverlapBeforeStartingBlender()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "MimicOnly",
            [MimicDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Rejected",
            "mimic-only.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExportAsync(
                    CreateRequest(
                        rig,
                        outputPath,
                        [clipPath]),
                    cancellationToken:
                        CancellationToken.None));

        Assert.Contains(
            "0 of 1 non-motion descriptors match",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mimic-only and wrong-family clips",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ServiceRejectsUnresolvedLowOverlapWrongFamilyBeforeStartingBlender()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "WrongFamily",
            [RootDescriptor, UnresolvedDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Rejected",
            "wrong-family.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExportAsync(
                    CreateRequest(
                        rig,
                        outputPath,
                        [clipPath]),
                    cancellationToken:
                        CancellationToken.None));

        Assert.Contains(
            "1 of 2 non-motion descriptors match",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "strong character-rig signature",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "at least 12 matched Root/Deform tracks",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ServiceAcceptsExactKnownCameraHelperOnlyClip()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "CameraOnly",
            [HelperDescriptor],
            frameCount: 4,
            sampleFps: 25.0,
            sourceFbxFps: 25.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Camera",
            "camera-helper.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);

        Assert.Equal(1, runner.InvocationCount);
        Assert.Equal(["CameraOnly"], result.AnimationStacks);
        Assert.Equal(rig.BoneCount, result.BoneCount);
        Assert.Empty(result.HelperSidecarPaths);
        Assert.True(File.Exists(outputPath));
        Assert.NotNull(runner.JobJson);
        using JsonDocument jobDocument =
            JsonDocument.Parse(runner.JobJson);
        JsonElement job = jobDocument.RootElement;
        Assert.Equal(
            rig.BoneCount,
            job.GetProperty("bones").GetArrayLength());
        JsonElement helper = Assert.Single(
            job.GetProperty("clips")[0]
                .GetProperty("helper_tracks")
                .EnumerateArray());
        Assert.Equal(
            HelperDescriptor,
            helper.GetProperty("descriptor").GetUInt32());
        Assert.Equal(
            "CameraHelper",
            helper.GetProperty("node_name").GetString());
        Assert.Equal(
            "camera.reference",
            helper.GetProperty("semantic").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            helper.GetProperty("sidecar_file").ValueKind);
    }

    [Fact]
    public void DescriptorDecodePlanUsesSeparateSelectedTrackPasses()
    {
        const uint motionDescriptor = 0xCCC3CDDF;
        const uint secondUnknownDescriptor = 0xA0B0C0D0;
        ImmutableArray<uint> sourceDescriptors =
        [
            RootDescriptor,
            UnresolvedDescriptor,
            motionDescriptor,
            HelperDescriptor,
            secondUnknownDescriptor,
        ];

        BlenderFbxExportService.DescriptorDecodePlan plan =
            BlenderFbxExportService
                .BuildDescriptorDecodePlan(
                    sourceDescriptors,
                    [
                        RootDescriptor,
                        HelperDescriptor,
                        null,
                    ]);

        Assert.Equal(
            new uint[]
            {
                RootDescriptor,
                motionDescriptor,
                HelperDescriptor,
            },
            plan.ActionDescriptors.ToArray());
        Assert.Equal(
            new uint[]
            {
                UnresolvedDescriptor,
                motionDescriptor,
                secondUnknownDescriptor,
            },
            plan.SidecarDescriptors.ToArray());
        Assert.DoesNotContain(
            UnresolvedDescriptor,
            plan.ActionDescriptors);
        Assert.DoesNotContain(
            RootDescriptor,
            plan.SidecarDescriptors);
    }

    [Fact]
    public async Task ServiceBakesOnlyActiveMotionAndPreservesItsSidecar()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateCredibleRig();
        string activeClip = await WriteMotionAccumulatorClipAsync(
            "ActiveMotion",
            rig,
            [0.0f, 1.0f, 2.0f]);
        string staticClip = await WriteMotionAccumulatorClipAsync(
            "StaticMotion",
            rig,
            [0.0f, 0.0f, 0.0f]);

        var activeRunner = new RecordingBlenderRunner();
        var activeService = new BlenderFbxExportService(
            activeRunner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));
        BlenderFbxExportResult activeResult =
            await activeService.ExportAsync(
                CreateRequest(
                    rig,
                    Path.Combine(
                        _temporaryDirectory,
                        "ActiveMotion",
                        "active.fbx"),
                    [activeClip]),
                cancellationToken:
                    CancellationToken.None);

        Assert.NotNull(
            activeRunner.FirstStagedClipBinary);
        Assert.Equal(
            2.0f,
            ReadStagedTranslationX(
                activeRunner.FirstStagedClipBinary,
                frameIndex: 2,
                rig.BoneCount,
                boneIndex: 0));
        string activeSidecar =
            Assert.Single(
                activeResult.HelperSidecarPaths);
        Assert.Equal(
            2.0f,
            ReadSidecarTranslationX(
                activeSidecar,
                frameIndex: 2,
                trackCount: 1,
                trackIndex: 0));
        AssertMotionAccumulatorJob(
            activeRunner.JobJson,
            active: true,
            bakedIntoRoot: true,
            rootName: "Root");

        var staticRunner = new RecordingBlenderRunner();
        var staticService = new BlenderFbxExportService(
            staticRunner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));
        BlenderFbxExportResult staticResult =
            await staticService.ExportAsync(
                CreateRequest(
                    rig,
                    Path.Combine(
                        _temporaryDirectory,
                        "StaticMotion",
                        "static.fbx"),
                    [staticClip]),
                cancellationToken:
                    CancellationToken.None);

        Assert.NotNull(
            staticRunner.FirstStagedClipBinary);
        Assert.Equal(
            0.0f,
            ReadStagedTranslationX(
                staticRunner.FirstStagedClipBinary,
                frameIndex: 2,
                rig.BoneCount,
                boneIndex: 0));
        string staticSidecar =
            Assert.Single(
                staticResult.HelperSidecarPaths);
        Assert.Equal(
            0.0f,
            ReadSidecarTranslationX(
                staticSidecar,
                frameIndex: 2,
                trackCount: 1,
                trackIndex: 0));
        AssertMotionAccumulatorJob(
            staticRunner.JobJson,
            active: false,
            bakedIntoRoot: false,
            rootName: null);
    }

    [Fact]
    public async Task ServiceCommitsUnresolvedTrackSidecarWithoutSynthesizingBone()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        const int sourceFrameCount = 7;
        const double sampleFps = 24.0;
        RigDefinition rig = CreateCredibleRig();
        string clipPath = await WriteRawClipAsync(
            "BodyWithUnknown",
            CreateCredibleUnresolvedClipDescriptors(),
            sourceFrameCount,
            sampleFps,
            sourceFbxFps: 30.0);
        Anm2Clip sourceClip =
            await Anm2Reader.ReadFileAsync(
                clipPath,
                cancellationToken:
                    CancellationToken.None);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Unknown",
            "body-with-unknown.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);

        Assert.Equal(1, runner.InvocationCount);
        Assert.Equal(rig.BoneCount, result.BoneCount);
        Assert.Equal(
            rig.BoneCount,
            runner.StagedBoneCount);
        Assert.Equal(
            1,
            runner.StagedHelperSidecarCount);
        string sidecarPath =
            Assert.Single(result.HelperSidecarPaths);
        Assert.True(File.Exists(sidecarPath));
        Assert.EndsWith(
            ".dlrtracks",
            sidecarPath,
            StringComparison.Ordinal);

        Assert.NotNull(runner.JobJson);
        using JsonDocument jobDocument =
            JsonDocument.Parse(runner.JobJson);
        JsonElement job = jobDocument.RootElement;
        JsonElement[] jobBones = job
            .GetProperty("bones")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(rig.BoneCount, jobBones.Length);
        Assert.DoesNotContain(
            jobBones,
            bone =>
                bone.GetProperty("descriptor")
                    .ValueKind !=
                    JsonValueKind.Null &&
                bone.GetProperty("descriptor")
                    .GetUInt32() ==
                    UnresolvedDescriptor);
        Assert.DoesNotContain(
            jobBones,
            bone => string.Equals(
                bone.GetProperty("name").GetString(),
                $"DLR_Track_{UnresolvedDescriptor:X8}",
                StringComparison.Ordinal));

        string manifestJson = await File.ReadAllTextAsync(
            result.HandoffManifestPath,
            CancellationToken.None);
        using JsonDocument manifestDocument =
            JsonDocument.Parse(manifestJson);
        JsonElement manifestClip = manifestDocument
            .RootElement
            .GetProperty("clips")[0];
        JsonElement unresolved = Assert.Single(
            manifestClip
                .GetProperty("helper_tracks")
                .EnumerateArray());
        Assert.Equal(
            UnresolvedDescriptor,
            unresolved.GetProperty("descriptor")
                .GetUInt32());
        Assert.Equal(
            Path.GetFileName(sidecarPath),
            unresolved.GetProperty("sidecar_file")
                .GetString());
        Assert.Equal(
            0,
            unresolved.GetProperty("sidecar_track_index")
                .GetInt32());
        Assert.Equal(
            sourceFrameCount,
            unresolved.GetProperty("frame_count")
                .GetInt32());
        Assert.Equal(
            sampleFps,
            unresolved.GetProperty("sample_fps")
                .GetDouble());
        Assert.Equal(
            "dlr-helper-anm2-trs-f32-wxyz-v1",
            unresolved.GetProperty("encoding")
                .GetString());

        string actualSidecarSha256 =
            Convert.ToHexString(
                    SHA256.HashData(
                        await File.ReadAllBytesAsync(
                            sidecarPath,
                            CancellationToken.None)))
                .ToLowerInvariant();
        Assert.Equal(
            actualSidecarSha256,
            unresolved.GetProperty("sidecar_sha256")
                .GetString());

        using FileStream sidecarStream =
            File.OpenRead(sidecarPath);
        using var sidecar = new BinaryReader(
            sidecarStream,
            Encoding.UTF8,
            leaveOpen: false);
        Assert.Equal(
            "DLRHLPR1"u8.ToArray(),
            sidecar.ReadBytes(8));
        Assert.Equal(1, sidecar.ReadInt32());
        Assert.Equal(
            sourceFrameCount,
            sidecar.ReadInt32());
        Assert.Equal(sampleFps, sidecar.ReadDouble());
        Assert.Equal(1, sidecar.ReadInt32());
        Assert.Equal(
            Convert.FromHexString(sourceClip.Sha256),
            sidecar.ReadBytes(32));
        Assert.Equal(
            UnresolvedDescriptor,
            sidecar.ReadUInt32());
        Assert.Equal(100.0f, sidecar.ReadSingle());
        sidecarStream.Position =
            64L +
            ((sourceFrameCount - 1L) * 40L);
        Assert.Equal(106.0f, sidecar.ReadSingle());
    }

    [Fact]
    public async Task ChangedCadenceGetsANewContentAddressedHelperSidecar()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        const int frameCount = 7;
        RigDefinition rig = CreateCredibleRig();
        string clipPath = await WriteRawClipAsync(
            "CadenceIdentity",
            CreateCredibleUnresolvedClipDescriptors(),
            frameCount,
            sampleFps: 24.0,
            sourceFbxFps: 30.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "CadenceIdentity",
            "body-with-unknown.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult first =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);
        string firstSidecar =
            Assert.Single(first.HelperSidecarPaths);
        Anm2ProvenanceLoadResult loaded =
            Anm2ProvenanceCodec.Load(clipPath);
        Assert.True(loaded.IsValid);
        Anm2ProvenanceCodec.Write(
            clipPath,
            loaded.Document! with
            {
                SampleFps = 12.0,
                SourceDurationSeconds =
                    (frameCount - 1) / 12.0,
            });

        BlenderFbxExportResult second =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);
        string secondSidecar =
            Assert.Single(second.HelperSidecarPaths);

        Assert.Equal(2, runner.InvocationCount);
        Assert.NotEqual(
            firstSidecar,
            secondSidecar);
        Assert.True(File.Exists(firstSidecar));
        Assert.True(File.Exists(secondSidecar));
        string secondHash = Convert.ToHexString(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        secondSidecar,
                        CancellationToken.None)))
            .ToLowerInvariant();
        Assert.Contains(
            secondHash,
            Path.GetFileName(secondSidecar),
            StringComparison.Ordinal);
        Assert.False(
            File.Exists(
                BlenderFbxExportService
                    .GetBundleCommitJournalPath(
                        outputPath)));
    }

    [Fact]
    public async Task DefaultOutputValidatorRejectsMarkerOnlyFileBeforeCommit()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "ValidationControl",
            [HelperDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "Validation",
            "marker-only.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            timeout: TimeSpan.FromSeconds(5));

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExportAsync(
                    CreateRequest(
                        rig,
                        outputPath,
                        [clipPath]),
                    cancellationToken:
                        CancellationToken.None));

        Assert.Equal(1, runner.InvocationCount);
        Assert.Contains(
            "Only binary FBX",
            error.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
        Assert.False(
            File.Exists(
                outputPath +
                ".dlrahandoff.json"));
    }

    [Fact]
    public async Task ServiceSelectsTypedRootAfterParentlessHelper()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateHelperBeforeRootRig();
        string clipPath = await WriteRawClipAsync(
            "RootAfterHelper",
            [RootDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "RootSelection",
            "helper-before-root.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);

        Assert.Equal(rig.BoneCount, result.BoneCount);
        Assert.Equal(1, runner.InvocationCount);
        Assert.NotNull(runner.JobJson);
        using JsonDocument document =
            JsonDocument.Parse(runner.JobJson);
        JsonElement[] bones = document.RootElement
            .GetProperty("bones")
            .EnumerateArray()
            .ToArray();
        Assert.False(
            bones[0].GetProperty("root").GetBoolean());
        Assert.True(
            bones[1].GetProperty("root").GetBoolean());
    }

    [Fact]
    public async Task ServiceRejectsAmbiguousTypedRootsBeforeBlender()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateAmbiguousRootRig();
        string clipPath = await WriteRawClipAsync(
            "AmbiguousRoots",
            [RootDescriptor, HelperDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputPath = Path.Combine(
            _temporaryDirectory,
            "RootSelection",
            "ambiguous-roots.fbx");
        var runner = new RecordingBlenderRunner();
        var service = new BlenderFbxExportService(
            runner,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        InvalidDataException error =
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ExportAsync(
                    CreateRequest(
                        rig,
                        outputPath,
                        [clipPath]),
                    cancellationToken:
                        CancellationToken.None));

        Assert.Contains(
            "exactly one parentless Root bone",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, runner.InvocationCount);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ServiceAtomicallyReplacesExistingBundleAndRemovesBackups()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "AtomicReplacement",
            [HelperDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "AtomicReplacement");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            "retail-player.fbx");
        string manifestPath =
            outputPath + ".dlrahandoff.json";
        await File.WriteAllTextAsync(
            outputPath,
            "OLD-FBX",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            manifestPath,
            "OLD-MANIFEST",
            CancellationToken.None);
        var service = new BlenderFbxExportService(
            new RecordingBlenderRunner(),
            outputValidator:
                new AcceptingFbxOutputValidator(),
            timeout: TimeSpan.FromSeconds(5));

        BlenderFbxExportResult result =
            await service.ExportAsync(
                CreateRequest(
                    rig,
                    outputPath,
                    [clipPath]),
                cancellationToken:
                    CancellationToken.None);

        Assert.Equal(
            "FAKE-FBX",
            await File.ReadAllTextAsync(
                outputPath,
                CancellationToken.None));
        Assert.Equal(manifestPath, result.HandoffManifestPath);
        Assert.Contains(
            BlenderFbxExportService.HandoffFormat,
            await File.ReadAllTextAsync(
                manifestPath,
                CancellationToken.None),
            StringComparison.Ordinal);
        Assert.Empty(
            Directory.EnumerateDirectories(
                outputDirectory,
                ".dlr-blender-*",
                SearchOption.TopDirectoryOnly));
        Assert.Empty(
            Directory.EnumerateFiles(
                outputDirectory,
                "*.previous",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreparedJournalConvergesPrimariesToOldAndRetainsSharedDependency()
    {
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "PreparedRecovery");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            "retail-player.fbx");
        string manifestPath =
            outputPath + ".dlrahandoff.json";
        string stageDirectory = Path.Combine(
            outputDirectory,
            $".dlr-blender-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        string stageFbx = Path.Combine(
            stageDirectory,
            Path.GetFileName(outputPath));
        string stageManifest = Path.Combine(
            stageDirectory,
            Path.GetFileName(manifestPath));
        string manifestBackup =
            stageManifest + ".previous";
        string dependencyPath = Path.Combine(
            outputDirectory,
            "retail-player.new.dlrtracks");
        string stagedDependency = Path.Combine(
            stageDirectory,
            Path.GetFileName(dependencyPath));

        await File.WriteAllTextAsync(
            outputPath,
            "OLD-FBX");
        await File.WriteAllTextAsync(
            stageFbx,
            "NEW-FBX");
        await File.WriteAllTextAsync(
            manifestPath,
            "NEW-MANIFEST");
        await File.WriteAllTextAsync(
            manifestBackup,
            "OLD-MANIFEST");
        await File.WriteAllTextAsync(
            dependencyPath,
            "NEW-DEPENDENCY");
        await File.WriteAllTextAsync(
            stagedDependency,
            "NEW-DEPENDENCY");
        var journal = new BlenderBundleCommitJournal(
            BlenderFbxExportService.BundleCommitFormat,
            BlenderFbxExportService
                .BundleCommitSchemaVersion,
            BlenderFbxExportService
                .BundleCommitPreparedPhase,
            Path.GetFullPath(outputPath),
            Path.GetFullPath(stageDirectory),
            new BlenderBundleCommitFile(
                Path.GetFullPath(stageFbx),
                Path.GetFullPath(outputPath),
                Path.GetFullPath(
                    stageFbx + ".previous"),
                true,
                ComputeSha256(stageFbx),
                ComputeSha256(outputPath)),
            new BlenderBundleCommitFile(
                Path.GetFullPath(stageManifest),
                Path.GetFullPath(manifestPath),
                Path.GetFullPath(manifestBackup),
                true,
                ComputeSha256(manifestPath),
                ComputeSha256(manifestBackup)),
            [
                new BlenderBundleCommitFile(
                    Path.GetFullPath(stagedDependency),
                    Path.GetFullPath(dependencyPath),
                    null,
                    false,
                    ComputeSha256(dependencyPath),
                    null),
            ]);
        BlenderFbxExportService.WriteBundleCommitJournal(
            journal);

        bool recovered =
            BlenderFbxExportService
                .RecoverInterruptedBundle(outputPath);

        Assert.True(recovered);
        Assert.Equal(
            "OLD-FBX",
            await File.ReadAllTextAsync(outputPath));
        Assert.Equal(
            "OLD-MANIFEST",
            await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(
            "NEW-DEPENDENCY",
            await File.ReadAllTextAsync(
                dependencyPath));
        Assert.False(
            Directory.Exists(stageDirectory));
        Assert.False(
            File.Exists(
                BlenderFbxExportService
                    .GetBundleCommitJournalPath(
                        outputPath)));
    }

    [Fact]
    public async Task InstalledJournalConvergesToTheNewGeneration()
    {
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "InstalledRecovery");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            "retail-player.fbx");
        string manifestPath =
            outputPath + ".dlrahandoff.json";
        string stageDirectory = Path.Combine(
            outputDirectory,
            $".dlr-blender-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        string stageFbx = Path.Combine(
            stageDirectory,
            Path.GetFileName(outputPath));
        string stageManifest = Path.Combine(
            stageDirectory,
            Path.GetFileName(manifestPath));
        string fbxBackup =
            stageFbx + ".previous";
        string manifestBackup =
            stageManifest + ".previous";
        string dependencyPath = Path.Combine(
            outputDirectory,
            "retail-player.new.dlrtracks");
        string stagedDependency = Path.Combine(
            stageDirectory,
            Path.GetFileName(dependencyPath));

        await File.WriteAllTextAsync(
            outputPath,
            "NEW-FBX");
        await File.WriteAllTextAsync(
            fbxBackup,
            "OLD-FBX");
        await File.WriteAllTextAsync(
            manifestPath,
            "NEW-MANIFEST");
        await File.WriteAllTextAsync(
            manifestBackup,
            "OLD-MANIFEST");
        await File.WriteAllTextAsync(
            dependencyPath,
            "NEW-DEPENDENDENCY");
        var journal = new BlenderBundleCommitJournal(
            BlenderFbxExportService.BundleCommitFormat,
            BlenderFbxExportService
                .BundleCommitSchemaVersion,
            BlenderFbxExportService
                .BundleCommitInstalledPhase,
            Path.GetFullPath(outputPath),
            Path.GetFullPath(stageDirectory),
            new BlenderBundleCommitFile(
                Path.GetFullPath(stageFbx),
                Path.GetFullPath(outputPath),
                Path.GetFullPath(fbxBackup),
                true,
                ComputeSha256(outputPath),
                ComputeSha256(fbxBackup)),
            new BlenderBundleCommitFile(
                Path.GetFullPath(stageManifest),
                Path.GetFullPath(manifestPath),
                Path.GetFullPath(manifestBackup),
                true,
                ComputeSha256(manifestPath),
                ComputeSha256(manifestBackup)),
            [
                new BlenderBundleCommitFile(
                    Path.GetFullPath(stagedDependency),
                    Path.GetFullPath(dependencyPath),
                    null,
                    false,
                    ComputeSha256(dependencyPath),
                    null),
            ]);
        BlenderFbxExportService.WriteBundleCommitJournal(
            journal);

        bool recovered =
            BlenderFbxExportService
                .RecoverInterruptedBundle(outputPath);

        Assert.True(recovered);
        Assert.Equal(
            "NEW-FBX",
            await File.ReadAllTextAsync(outputPath));
        Assert.Equal(
            "NEW-MANIFEST",
            await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(
            "NEW-DEPENDENDENCY",
            await File.ReadAllTextAsync(
                dependencyPath));
        Assert.False(
            Directory.Exists(stageDirectory));
        Assert.False(
            File.Exists(
                BlenderFbxExportService
                    .GetBundleCommitJournalPath(
                        outputPath)));
    }

    [Fact]
    public void RecoveryJournalRejectsEscapingDependencyPath()
    {
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "PathSafety");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            "retail-player.fbx");
        string manifestPath =
            outputPath + ".dlrahandoff.json";
        string stageDirectory = Path.Combine(
            outputDirectory,
            $".dlr-blender-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        string stageFbx = Path.Combine(
            stageDirectory,
            Path.GetFileName(outputPath));
        string stageManifest = Path.Combine(
            stageDirectory,
            Path.GetFileName(manifestPath));
        string stagedDependency = Path.Combine(
            stageDirectory,
            "dependency.dlrtracks");
        string outsidePath = Path.Combine(
            _temporaryDirectory,
            "outside.dlrtracks");
        File.WriteAllText(stageFbx, "NEW-FBX");
        File.WriteAllText(
            stageManifest,
            "NEW-MANIFEST");
        File.WriteAllText(
            stagedDependency,
            "NEW-DEPENDENCY");
        File.WriteAllText(
            outsidePath,
            "DO-NOT-TOUCH");
        var journal = new BlenderBundleCommitJournal(
            BlenderFbxExportService.BundleCommitFormat,
            BlenderFbxExportService
                .BundleCommitSchemaVersion,
            BlenderFbxExportService
                .BundleCommitPreparedPhase,
            Path.GetFullPath(outputPath),
            Path.GetFullPath(stageDirectory),
            new BlenderBundleCommitFile(
                Path.GetFullPath(stageFbx),
                Path.GetFullPath(outputPath),
                Path.GetFullPath(
                    stageFbx + ".previous"),
                false,
                ComputeSha256(stageFbx),
                null),
            new BlenderBundleCommitFile(
                Path.GetFullPath(stageManifest),
                Path.GetFullPath(manifestPath),
                Path.GetFullPath(
                    stageManifest + ".previous"),
                false,
                ComputeSha256(stageManifest),
                null),
            [
                new BlenderBundleCommitFile(
                    Path.GetFullPath(stagedDependency),
                    Path.GetFullPath(outsidePath),
                    null,
                    false,
                    ComputeSha256(stagedDependency),
                    null),
            ]);

        Assert.Throws<InvalidDataException>(
            () => BlenderFbxExportService
                .WriteBundleCommitJournal(
                    journal));
        Assert.Equal(
            "DO-NOT-TOUCH",
            File.ReadAllText(outsidePath));
        Assert.False(
            File.Exists(
                BlenderFbxExportService
                    .GetBundleCommitJournalPath(
                        outputPath)));
    }

    [Fact]
    public void RecoveryRejectsOverDepthJournalWithoutTouchingOutput()
    {
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "DepthSafety");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            "retail-player.fbx");
        File.WriteAllText(outputPath, "OLD-FBX");
        string nested = "{}";
        for (var index = 0; index < 20; index++)
        {
            nested = $"{{\"nested\":{nested}}}";
        }

        File.WriteAllText(
            BlenderFbxExportService
                .GetBundleCommitJournalPath(
                    outputPath),
            nested);

        Assert.Throws<JsonException>(
            () => BlenderFbxExportService
                .RecoverInterruptedBundle(outputPath));
        Assert.Equal(
            "OLD-FBX",
            File.ReadAllText(outputPath));
    }

    [Fact]
    public async Task ServicePreservesRecoveryStageWhenCommitAndRestoreFail()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        RigDefinition rig = CreateRig();
        string clipPath = await WriteRawClipAsync(
            "RollbackRecovery",
            [HelperDescriptor],
            frameCount: 3,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
        string outputDirectory = Path.Combine(
            _temporaryDirectory,
            "RollbackRecovery");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            "retail-player.fbx");
        string manifestPath =
            outputPath + ".dlrahandoff.json";
        await File.WriteAllTextAsync(
            outputPath,
            "OLD-FBX",
            CancellationToken.None);
        await File.WriteAllTextAsync(
            manifestPath,
            "OLD-MANIFEST",
            CancellationToken.None);
        var fileSystem =
            new CommitAndRestoreFailingFileSystem(
                outputPath);
        var service = new BlenderFbxExportService(
            new RecordingBlenderRunner(),
            helperResource: null,
            outputValidator:
                new AcceptingFbxOutputValidator(),
            bundleFileSystem: fileSystem,
            timeout: TimeSpan.FromSeconds(5));

        BlenderBundleRecoveryException error =
            await Assert.ThrowsAsync<
                BlenderBundleRecoveryException>(
                () => service.ExportAsync(
                    CreateRequest(
                        rig,
                        outputPath,
                        [clipPath]),
                    cancellationToken:
                        CancellationToken.None));

        Assert.True(fileSystem.CommitMoveFailed);
        Assert.True(fileSystem.RestoreMoveFailed);
        Assert.Single(error.RollbackFailures);
        Assert.Contains(
            "automatic rollback was incomplete",
            error.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            error.RecoveryDirectory,
            error.Message,
            StringComparison.Ordinal);
        Assert.True(
            Directory.Exists(error.RecoveryDirectory));
        string fbxBackupPath = Path.Combine(
            error.RecoveryDirectory,
            Path.GetFileName(outputPath) +
            ".previous");
        Assert.True(File.Exists(fbxBackupPath));
        Assert.Equal(
            "OLD-FBX",
            await File.ReadAllTextAsync(
                fbxBackupPath,
                CancellationToken.None));
        Assert.True(File.Exists(outputPath));
        Assert.Equal(
            "FAKE-FBX",
            await File.ReadAllTextAsync(
                outputPath,
                CancellationToken.None));
        Assert.True(
            File.Exists(
                BlenderFbxExportService
                    .GetBundleCommitJournalPath(
                        outputPath)));
        Assert.Equal(
            "OLD-MANIFEST",
            await File.ReadAllTextAsync(
                manifestPath,
                CancellationToken.None));
        Assert.Single(
            Directory.EnumerateFiles(
                outputDirectory,
                "*.dds",
                SearchOption.TopDirectoryOnly));
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

    private const uint RootDescriptor = 0x11112222;
    private const uint HelperDescriptor = 0x33334444;
    private const uint MimicDescriptor = 0x55556666;
    private const uint UnresolvedDescriptor = 0xDEADBEEF;

    private static void AssertOccursOnce(
        string text,
        string value)
    {
        int first = text.IndexOf(
            value,
            StringComparison.Ordinal);
        Assert.True(
            first >= 0,
            $"Expected embedded Blender helper to contain '{value}'.");
        Assert.Equal(
            first,
            text.LastIndexOf(
                value,
                StringComparison.Ordinal));
    }

    private static RigDefinition CreateRig() =>
        new(
            "retail-player-rig",
            "Retail Player Rig",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: RootDescriptor),
                new BoneDefinition(
                    1,
                    "CameraHelper",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Helper,
                    descriptorHash: HelperDescriptor,
                    semanticRole: "camera.reference"),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "jaw_open",
                    descriptorHash: MimicDescriptor),
            ]);

    private static RigDefinition CreateHelperBeforeRootRig() =>
        new(
            "helper-before-root",
            "Helper Before Root",
            [
                new BoneDefinition(
                    0,
                    "DetachedHelper",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    descriptorHash: HelperDescriptor,
                    semanticRole: "camera.reference"),
                new BoneDefinition(
                    1,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: RootDescriptor),
            ]);

    private static RigDefinition CreateCredibleRig()
    {
        const int deformBoneCount = 12;
        var bones = new List<BoneDefinition>
        {
            new(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root,
                descriptorHash: RootDescriptor),
        };
        for (var index = 0;
             index < deformBoneCount;
             index++)
        {
            bones.Add(new BoneDefinition(
                index + 1,
                $"Deform_{index + 1:00}",
                index,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Deform,
                descriptorHash:
                    CredibleDeformDescriptor(index)));
        }

        return new RigDefinition(
            "credible-retail-character-rig",
            "Credible Retail Character Rig",
            bones);
    }

    private static List<uint>
        CreateCredibleUnresolvedClipDescriptors()
    {
        var descriptors = new List<uint>
        {
            RootDescriptor,
            UnresolvedDescriptor,
        };
        descriptors.AddRange(
            Enumerable.Range(0, 12)
                .Select(CredibleDeformDescriptor));
        return descriptors;
    }

    private static uint CredibleDeformDescriptor(
        int index) =>
        checked(0x60000000u + (uint)index);

    private static RigDefinition CreateAmbiguousRootRig() =>
        new(
            "ambiguous-roots",
            "Ambiguous Roots",
            [
                new BoneDefinition(
                    0,
                    "RootA",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: RootDescriptor),
                new BoneDefinition(
                    1,
                    "RootB",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: HelperDescriptor),
            ]);

    private static AnimationClip CreateClip(
        string name,
        RigDefinition rig,
        int frameCount,
        double rootDistance) =>
        new(
            name,
            new FrameRate(30, 1),
            frameCount,
            [
                new TransformTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            rig.Bones[0].LocalBindPose),
                        new TransformKeyframe(
                            frameCount - 1,
                            new TransformTRS(
                                new Vector3D(
                                    rootDistance,
                                    0.0,
                                    0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            rig.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            frameCount - 1,
                            new TransformTRS(
                                new Vector3D(
                                    0.0,
                                    1.0,
                                    0.05),
                                QuaternionD.FromAxisAngle(
                                    Vector3D.UnitX,
                                    0.1),
                                Vector3D.One)),
                    ]),
            ]);

    private async Task<string> WriteClipAsync(
        string fileName,
        AnimationClip clip,
        RigDefinition rig,
        double sourceFbxFps)
    {
        byte[] bytes = Anm2DomainAdapter.ExportBody(
            clip,
            rig,
            [RootDescriptor, HelperDescriptor]);
        return await WriteAnm2Async(
            fileName,
            bytes,
            sampleFps: 30.0,
            sourceFbxFps);
    }

    private async Task<string> WriteRawClipAsync(
        string fileName,
        IReadOnlyList<uint> descriptorValues,
        int frameCount,
        double sampleFps,
        double sourceFbxFps,
        double? playbackFps = null)
    {
        ImmutableArray<uint> descriptors =
            descriptorValues.ToImmutableArray();
        ImmutableArray<Anm2Frame> frames = Enumerable
            .Range(0, frameCount)
            .Select(frameIndex =>
                new Anm2Frame(
                    descriptors
                        .Select(
                            (_, trackIndex) =>
                                new Anm2TrackFrame(
                                    0.0f,
                                    0.0f,
                                    0.0f,
                                    (trackIndex * 100.0f) +
                                    frameIndex,
                                    trackIndex,
                                    0.0f,
                                    1.0f,
                                    1.0f,
                                    1.0f))
                        .ToImmutableArray()))
            .ToImmutableArray();
        byte[] bytes = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                checked((ushort)frameCount),
                checked((ushort)descriptors.Length),
                0,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            frames,
            Enumerable.Repeat(
                    Anm2PackedComponents.TranslationX,
                    descriptors.Length)
                .ToImmutableArray());
        return await WriteAnm2Async(
            fileName,
            bytes,
            sampleFps,
            sourceFbxFps,
            playbackFps);
    }

    private async Task<string>
        WriteMotionAccumulatorClipAsync(
            string fileName,
            RigDefinition rig,
            IReadOnlyList<float> motionX)
    {
        ImmutableArray<uint> descriptors =
            rig.Bones
                .Select(bone =>
                    bone.DescriptorHash ??
                    throw new InvalidOperationException(
                        $"Test bone '{bone.Name}' has no descriptor."))
                .Append(0xCCC3CDDF)
                .ToImmutableArray();
        ImmutableArray<Anm2Frame> frames =
            motionX
                .Select(value =>
                {
                    var tracks =
                        ImmutableArray
                            .CreateBuilder<
                                Anm2TrackFrame>(
                                descriptors.Length);
                    foreach (BoneDefinition bone in
                             rig.Bones)
                    {
                        TransformTRS bind =
                            bone.LocalBindPose;
                        Vector3D cayley =
                            Anm2DomainAdapter
                                .CayleyFromQuaternion(
                                    bind.Rotation);
                        tracks.Add(
                            new Anm2TrackFrame(
                                checked((float)cayley.X),
                                checked((float)cayley.Y),
                                checked((float)cayley.Z),
                                checked((float)
                                    bind.Translation.X),
                                checked((float)
                                    bind.Translation.Y),
                                checked((float)
                                    bind.Translation.Z),
                                checked((float)bind.Scale.X),
                                checked((float)bind.Scale.Y),
                                checked((float)bind.Scale.Z)));
                    }

                    tracks.Add(
                        new Anm2TrackFrame(
                            0.0f,
                            0.0f,
                            0.0f,
                            value,
                            0.0f,
                            0.0f,
                            1.0f,
                            1.0f,
                            1.0f));
                    return new Anm2Frame(
                        tracks.MoveToImmutable());
                })
                .ToImmutableArray();
        ImmutableArray<Anm2PackedComponents> packing =
            Enumerable
                .Repeat(
                    Anm2PackedComponents.None,
                    rig.BoneCount)
                .Append(
                    Anm2PackedComponents.TranslationX)
                .ToImmutableArray();
        byte[] bytes = Anm2PayloadWriter.Build(
            new Anm2Header(
                Anm2Header.Dl1FormatVersion,
                Anm2Header.Dl1SamplerVersion,
                checked((ushort)frames.Length),
                checked((ushort)descriptors.Length),
                0,
                0,
                0,
                1,
                0,
                0),
            descriptors,
            frames,
            packing);
        return await WriteAnm2Async(
            fileName,
            bytes,
            sampleFps: 30.0,
            sourceFbxFps: 30.0);
    }

    private async Task<string> WriteAnm2Async(
        string fileName,
        byte[] bytes,
        double sampleFps,
        double sourceFbxFps,
        double? playbackFps = null)
    {
        string path = Path.Combine(
            _temporaryDirectory,
            fileName + ".anm2");
        await File.WriteAllBytesAsync(
            path,
            bytes,
            CancellationToken.None);
        Anm2Clip decoded = Anm2Reader.Read(
            bytes,
            fileName);
        Anm2ProvenanceDocument provenance =
            Anm2ProvenanceCodec.Create(
                bytes,
                sourceFbx: fileName + ".fbx",
                sourceFbxSha256: new string('B', 64),
                sourceFbxFps,
                sampleFps,
                playbackFps ?? sampleFps,
                sourceDurationSeconds:
                    (decoded.Header.FrameCount - 1) /
                    sampleFps,
                frameCount: decoded.Header.FrameCount,
                rootMotionMode: "in_place",
                rootHeadingMode:
                    "lock_initial_heading");
        Anm2ProvenanceCodec.Write(path, provenance);
        return path;
    }

    private BlenderFbxExportRequest CreateRequest(
        RigDefinition rig,
        string outputPath,
        IReadOnlyList<string> clipPaths)
    {
        string blenderPath = Path.Combine(
            _temporaryDirectory,
            "blender.exe");
        if (!File.Exists(blenderPath))
        {
            File.WriteAllBytes(blenderPath, []);
        }

        return new BlenderFbxExportRequest(
            blenderPath,
            outputPath,
            new BlenderFbxAssetIdentity(
                "retail:player",
                "Data0.pak",
                "player_1_tpp.msh",
                new string('a', 64)),
            rig,
            [CreateTexturedMesh()],
            clipPaths);
    }

    private static MeshRenderData CreateTexturedMesh()
    {
        MeshVertex[] vertices =
        [
            new(
                Vector3.Zero,
                Vector3.UnitY,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                Vector3.UnitX,
                Vector3.UnitY,
                Vector2.UnitX,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                Vector3.UnitZ,
                Vector3.UnitY,
                Vector2.UnitY,
                Vector4.UnitX,
                Vector4.Zero),
        ];
        return new MeshRenderData(
            "PlayerMesh",
            vertices,
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            new Matrix4x4[]
            {
                Matrix4x4.Identity,
                Matrix4x4.CreateTranslation(
                    0.0f,
                    -1.0f,
                    0.0f),
            },
            true)
        {
            BaseColorTexture = new TextureRenderData(
                "player_base_color",
                4,
                4,
                TextureRenderFormat.Bc1Unorm,
                8,
                new byte[]
                {
                    0x00, 0xF8,
                    0x00, 0x00,
                    0x00, 0x00,
                    0x00, 0x00,
                }),
        };
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
                SHA256.HashData(stream))
            .ToLowerInvariant();
    }

    private static float ReadStagedTranslationX(
        byte[] data,
        int frameIndex,
        int boneCount,
        int boneIndex)
    {
        int offset = checked(
            16 +
            (((frameIndex * boneCount) +
              boneIndex) *
             10 *
             sizeof(float)));
        return BinaryPrimitives.ReadSingleLittleEndian(
            data.AsSpan(offset, sizeof(float)));
    }

    private static float ReadSidecarTranslationX(
        string path,
        int frameIndex,
        int trackCount,
        int trackIndex)
    {
        byte[] data = File.ReadAllBytes(path);
        int headerBytes = checked(
            60 +
            (trackCount * sizeof(uint)));
        int offset = checked(
            headerBytes +
            (((frameIndex * trackCount) +
              trackIndex) *
             10 *
             sizeof(float)));
        return BinaryPrimitives.ReadSingleLittleEndian(
            data.AsSpan(offset, sizeof(float)));
    }

    private static void AssertMotionAccumulatorJob(
        string? jobJson,
        bool active,
        bool bakedIntoRoot,
        string? rootName)
    {
        Assert.NotNull(jobJson);
        using JsonDocument document =
            JsonDocument.Parse(jobJson);
        JsonElement motion = document.RootElement
            .GetProperty("clips")[0]
            .GetProperty("motion_accumulator");
        Assert.True(
            motion.GetProperty("present")
                .GetBoolean());
        Assert.Equal(
            active,
            motion.GetProperty("active")
                .GetBoolean());
        Assert.Equal(
            bakedIntoRoot,
            motion.GetProperty("baked_into_root")
                .GetBoolean());
        if (rootName is null)
        {
            Assert.Equal(
                JsonValueKind.Null,
                motion.GetProperty("root_name")
                    .ValueKind);
        }
        else
        {
            Assert.Equal(
                rootName,
                motion.GetProperty("root_name")
                    .GetString());
        }
    }

    private sealed class RecordingBlenderRunner :
        IBlenderProcessRunner
    {
        public string? JobJson { get; private set; }

        public int InvocationCount { get; private set; }

        public int StagedBoneCount { get; private set; }

        public int StagedClipCount { get; private set; }

        public int StagedMeshCount { get; private set; }

        public int StagedTextureCount { get; private set; }

        public int StagedHelperSidecarCount { get; private set; }

        public byte[]? FirstStagedClipBinary { get; private set; }

        public async Task<BlenderProcessResult> RunAsync(
            BlenderProcessRequest request,
            Action<string>? outputLine,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            Assert.True(
                File.Exists(request.HelperScriptPath));
            Assert.True(File.Exists(request.JobPath));
            JobJson = await File.ReadAllTextAsync(
                request.JobPath,
                cancellationToken);
            using JsonDocument document =
                JsonDocument.Parse(JobJson);
            JsonElement root = document.RootElement;
            JsonElement[] clips = root
                .GetProperty("clips")
                .EnumerateArray()
                .ToArray();
            JsonElement[] meshes = root
                .GetProperty("meshes")
                .EnumerateArray()
                .ToArray();
            JsonElement[] textures = root
                .GetProperty("textures")
                .EnumerateArray()
                .ToArray();
            StagedBoneCount = root
                .GetProperty("bones")
                .GetArrayLength();
            StagedClipCount = clips.Length;
            StagedMeshCount = meshes.Length;
            StagedTextureCount = textures.Length;
            FirstStagedClipBinary = clips.Length == 0
                ? null
                : await File.ReadAllBytesAsync(
                    clips[0]
                        .GetProperty("binary_path")
                        .GetString()!,
                    cancellationToken);
            Assert.All(
                clips,
                clip => Assert.True(
                    File.Exists(
                        clip.GetProperty("binary_path")
                            .GetString())));
            Assert.All(
                meshes,
                mesh => Assert.True(
                    File.Exists(
                        mesh.GetProperty("binary_path")
                            .GetString())));
            Assert.All(
                textures,
                texture => Assert.True(
                    File.Exists(
                        texture.GetProperty("file_path")
                            .GetString())));

            string outputPath = root
                .GetProperty("output_path")
                .GetString()!;
            string stageDirectory =
                Path.GetDirectoryName(outputPath)!;
            string[] sidecars = clips
                .SelectMany(clip =>
                    clip.GetProperty("helper_tracks")
                        .EnumerateArray())
                .Where(helper =>
                    helper.GetProperty("sidecar_file")
                        .ValueKind ==
                    JsonValueKind.String)
                .Select(helper =>
                    helper.GetProperty("sidecar_file")
                        .GetString()!)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            StagedHelperSidecarCount =
                sidecars.Length;
            Assert.All(
                sidecars,
                fileName => Assert.True(
                    File.Exists(
                        Path.Combine(
                            stageDirectory,
                            fileName))));
            await File.WriteAllTextAsync(
                outputPath,
                "FAKE-FBX",
                cancellationToken);
            string[] actions = clips
                .Select(clip =>
                    clip.GetProperty("action_name")
                        .GetString()!)
                .ToArray();
            string log = string.Join(
                Environment.NewLine,
                "DLR_PROGRESS:actions|2|2",
                "DLR_ACTION_STACKS:" +
                JsonSerializer.Serialize(actions),
                "DLR_BIND_POSE:" +
                JsonSerializer.Serialize(
                    new
                    {
                        exported = true,
                        bone_count = root
                            .GetProperty("bones")
                            .GetArrayLength(),
                    }),
                "DLR_ROOT_PARITY:" +
                JsonSerializer.Serialize(
                    new
                    {
                        max_angular_error_degrees =
                            0.0,
                        max_translation_error_m =
                            0.0,
                    }),
                "DLR_EXPORT_COMPLETE:" + outputPath);
            outputLine?.Invoke(
                "DLR_PROGRESS:actions|2|2");
            return new BlenderProcessResult(
                0,
                log,
                string.Empty);
        }
    }

    private sealed class AcceptingFbxOutputValidator :
        IBlenderFbxOutputValidator
    {
        public Task ValidateAsync(
            string outputFbxPath,
            IReadOnlyList<BlenderFbxJobBone> expectedBones,
            IReadOnlyList<BlenderFbxJobClip> expectedClips,
            IReadOnlyList<BlenderFbxJobMesh> expectedMeshes,
            IReadOnlyList<BlenderFbxJobTexture> expectedTextures,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(outputFbxPath));
            Assert.NotEmpty(expectedBones);
            Assert.NotEmpty(expectedMeshes);
            Assert.NotEmpty(expectedTextures);
            return Task.CompletedTask;
        }
    }

    private sealed class CommitAndRestoreFailingFileSystem :
        IBlenderBundleFileSystem
    {
        private readonly string _outputPath;

        public CommitAndRestoreFailingFileSystem(
            string outputPath)
        {
            _outputPath = Path.GetFullPath(outputPath);
        }

        public bool CommitMoveFailed
        {
            get;
            private set;
        }

        public bool RestoreMoveFailed
        {
            get;
            private set;
        }

        public bool FileExists(string path) =>
            File.Exists(path);

        public void MoveFile(
            string source,
            string destination)
        {
            string fullSource = Path.GetFullPath(source);
            string fullDestination =
                Path.GetFullPath(destination);
            File.Move(fullSource, fullDestination);
        }

        public void ReplaceFile(
            string source,
            string destination,
            string backup)
        {
            string fullSource = Path.GetFullPath(source);
            string fullDestination =
                Path.GetFullPath(destination);
            string fullBackup = Path.GetFullPath(backup);
            if (PathsEqual(
                    fullDestination,
                    _outputPath) &&
                fullSource.EndsWith(
                    ".previous",
                    StringComparison.OrdinalIgnoreCase))
            {
                RestoreMoveFailed = true;
                throw new IOException(
                    "Injected previous-FBX restore failure.");
            }

            if (PathsEqual(
                    fullDestination,
                    _outputPath) &&
                Path.GetFileName(
                    Path.GetDirectoryName(
                        fullSource)!)
                    .StartsWith(
                        ".dlr-blender-",
                        StringComparison.OrdinalIgnoreCase))
            {
                File.Replace(
                    fullSource,
                    fullDestination,
                    fullBackup,
                    ignoreMetadataErrors: true);
                CommitMoveFailed = true;
                throw new IOException(
                    "Injected post-replace FBX commit failure.");
            }

            File.Replace(
                fullSource,
                fullDestination,
                fullBackup,
                ignoreMetadataErrors: true);
        }

        public void DeleteFile(string path) =>
            File.Delete(path);

        public bool FilesEqual(
            string left,
            string right)
        {
            using FileStream leftStream =
                File.OpenRead(left);
            using FileStream rightStream =
                File.OpenRead(right);
            return SHA256.HashData(leftStream)
                .AsSpan()
                .SequenceEqual(
                    SHA256.HashData(rightStream));
        }

        private static bool PathsEqual(
            string left,
            string right) =>
            string.Equals(
                left,
                right,
                StringComparison.OrdinalIgnoreCase);
    }
}
