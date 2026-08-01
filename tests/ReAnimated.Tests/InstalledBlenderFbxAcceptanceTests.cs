using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

/// <summary>
/// Opt-in acceptance against the user's installed DL1 retail data and a real
/// local Blender. All generated retail-derived output remains in a temporary
/// directory and is removed when the test finishes.
/// </summary>
public sealed class InstalledBlenderFbxAcceptanceTests
{
    private const string AcceptanceEnvironmentVariable =
        "DLR_RUN_INSTALLED_BLENDER_ACCEPTANCE";
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";
    private const string RetailControlName =
        "zombie_voleteile_blue";
    private const string RetailPackName =
        "common_meshes_PC.rpack";
    private const int RetailResourceIndex = 5201;
    private const string RetailResourceSha256 =
        "c6ed07a38942faa6c45865e28952ede1c4afd72def645e81a9e54ca3c48c6fbb";

    private readonly ITestOutputHelper _output;

    public InstalledBlenderFbxAcceptanceTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 900_000)]
    [Trait("Gate", "DL1InstalledBlenderFbx")]
    public async Task InstalledBlenderExportsTexturedRetailVolatileWithTwoActions()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    AcceptanceEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"NOT EXERCISED: set {AcceptanceEnvironmentVariable}=1 and BLENDER_EXECUTABLE to run the installed Blender/DL1 acceptance.");
            return;
        }

        string blenderExecutable =
            Environment.GetEnvironmentVariable(
                "BLENDER_EXECUTABLE")
            ?? throw new InvalidOperationException(
                "BLENDER_EXECUTABLE must name the installed blender.exe.");
        if (!BlenderExecutableResolver.TryValidate(
                blenderExecutable,
                out string? resolvedBlender))
        {
            throw new FileNotFoundException(
                "BLENDER_EXECUTABLE does not name a usable blender.exe.",
                blenderExecutable);
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid)
            ?? throw new InvalidOperationException(
                "A complete Steam Dying Light installation is required.");
        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        Assert.Equal(
            ValidatedBuildFingerprint,
            build.BuildFingerprint,
            ignoreCase: true);

        string materialPackPath = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        if (!File.Exists(materialPackPath))
        {
            throw new FileNotFoundException(
                "The installed DL1 material pack is missing.",
                materialPackPath);
        }

        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await RunAcceptanceAsync(
                install,
                resolvedBlender!,
                materialPackPath,
                directory);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private async Task RunAcceptanceAsync(
        Dl1InstallLocation install,
        string blenderExecutable,
        string materialPackPath,
        string directory)
    {
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory =
                    Path.Combine(directory, "cache"),
                MaximumMemoryBytes =
                    128L * 1024 * 1024,
                MaximumMemoryEntryBytes =
                    32 * 1024 * 1024,
                MaximumDiskBytes =
                    2L * 1024 * 1024 * 1024,
            });
        await using Dl1RetailProviderSet providers =
            Dl1RetailProviderSet.Create(
                install.InstallPath,
                cache);
        RetailAssetCatalog catalog =
            await RetailAssetCatalog.BuildAsync(
                providers.Providers);
        RetailAssetRecord asset =
            catalog.Resolve(
                RetailAssetLogicalId.Rpack(
                    Rp6lResourceTypes.Mesh,
                    RetailControlName))
            ?? throw new InvalidDataException(
                $"Installed DL1 has no '{RetailControlName}' retail mesh.");
        Assert.Equal(
            RetailPackName,
            Path.GetFileName(asset.Source.ContainerPath));
        Rp6lArchive archive =
            await providers.RpackProvider.GetArchiveAsync(
                asset.Source.ContainerPath);
        int resourceIndex =
            asset.Source.ResourceIndex
            ?? throw new InvalidDataException(
                "The retail catalog identity has no resource index.");
        Assert.Equal(RetailResourceIndex, resourceIndex);
        Rp6lResourceDescriptor resource =
            archive.Resources[resourceIndex];
        string resourceSha256;
        await using (Stream stream =
                     await archive.OpenResourceStreamAsync(
                         resource,
                         cache))
        {
            resourceSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream))
                .ToLowerInvariant();
        }
        Assert.Equal(
            RetailResourceSha256,
            resourceSha256,
            ignoreCase: true);

        Dl1MeshData decoded =
            await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache);
        var materialResolver =
            new Dl1MaterialTextureResolver(
                catalog,
                providers.RpackProvider,
                cache,
                materialPackPath);
        Dl1MeshData textured =
            await materialResolver.ResolveAsync(decoded);
        Dl1MeshPreviewPayload preview =
            Dl1MeshPreviewAdapter.Convert(
                textured,
                resourceSha256);
        RigDefinition rig =
            textured.Rig
            ?? throw new InvalidDataException(
                "The installed volatile control has no validated authoring rig.");
        Assert.True(textured.IsStructurallyValid);
        Assert.True(textured.IsSkinned);
        Assert.NotEmpty(preview.Meshes);
        Assert.Equal(97, rig.BoneCount);
        Assert.Equal(6, preview.Meshes.Count);
        Assert.Contains(
            preview.Meshes,
            static mesh =>
                mesh.BaseColorTexture is not null);

        BoneDefinition root = Assert.Single(
            rig.Bones,
            static bone =>
                bone.ParentIndex < 0 &&
                bone.Kind == BoneKind.Root &&
                bone.DescriptorHash.HasValue);
        BoneDefinition deform = rig.Bones.First(
            static bone =>
                bone.Kind == BoneKind.Deform &&
                bone.DescriptorHash.HasValue);
        string firstAnm2 = Path.Combine(
            directory,
            "volatile_pose_a.anm2");
        string secondAnm2 = Path.Combine(
            directory,
            "volatile_pose_b.anm2");
        await WriteClipAsync(
            firstAnm2,
            CreateClip(
                "volatile_pose_a",
                rig,
                root,
                deform,
                4,
                0.035,
                0.08),
            rig,
            root,
            deform);
        await WriteClipAsync(
            secondAnm2,
            CreateClip(
                "volatile_pose_b",
                rig,
                root,
                deform,
                6,
                -0.025,
                -0.06),
            rig,
            root,
            deform);

        string outputDirectory =
            Path.Combine(directory, "output");
        string outputFbx = Path.Combine(
            outputDirectory,
            "volatile_multi_action.fbx");
        var progress = new InlineProgress<BlenderFbxExportProgress>(
            value => _output.WriteLine(
                $"{value.Percent,6:F1}% {value.Stage}: {value.Detail}"));
        var service = new BlenderFbxExportService(
            timeout: TimeSpan.FromMinutes(10));
        BlenderFbxExportResult result =
            await service.ExportAsync(
                new BlenderFbxExportRequest(
                    blenderExecutable,
                    outputFbx,
                    new BlenderFbxAssetIdentity(
                        asset.Id.StableKey,
                        asset.Source.ProviderId,
                        asset.DisplayName,
                        resourceSha256),
                    rig,
                    preview.Meshes,
                    [firstAnm2, secondAnm2]),
                progress);

        Assert.Equal(
            ["volatile_pose_a", "volatile_pose_b"],
            result.AnimationStacks);
        Assert.Equal(rig.BoneCount, result.BoneCount);
        Assert.Equal(preview.Meshes.Count, result.MeshCount);
        Assert.Equal(5, result.TexturePaths.Count);
        Assert.All(
            result.TexturePaths,
            static path => Assert.True(File.Exists(path)));
        Assert.Empty(result.HelperSidecarPaths);
        Assert.True(File.Exists(outputFbx));
        Assert.InRange(
            new FileInfo(outputFbx).Length,
            1,
            256L * 1024 * 1024);
        Assert.Contains(
            "DLR_EXPORT_COMPLETE:",
            result.BlenderLog,
            StringComparison.Ordinal);
        Assert.Contains(
            "DLR_ROOT_PARITY:",
            result.BlenderLog,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(
                outputDirectory),
            path => Path.GetFileName(path).StartsWith(
                ".dlr-blender-",
                StringComparison.Ordinal));

        using JsonDocument manifest =
            JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    result.HandoffManifestPath));
        JsonElement manifestRoot = manifest.RootElement;
        Assert.Equal(
            BlenderFbxExportService.HandoffFormat,
            manifestRoot.GetProperty("format").GetString());
        Assert.Equal(
            BlenderFbxExportService.HandoffSchemaVersion,
            manifestRoot.GetProperty("schema_version").GetInt32());
        Assert.Equal(
            RetailControlName,
            manifestRoot
                .GetProperty("asset")
                .GetProperty("resource_name")
                .GetString());
        Assert.Equal(
            ["volatile_pose_a", "volatile_pose_b"],
            manifestRoot
                .GetProperty("clips")
                .EnumerateArray()
                .Select(static clip =>
                    clip
                        .GetProperty("action_name")
                        .GetString()
                    ?? throw new InvalidDataException(
                        "The handoff manifest contains an unnamed Action."))
                .ToArray());

        string fbxSha256;
        await using (FileStream output = File.OpenRead(outputFbx))
        {
            fbxSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(output))
                .ToLowerInvariant();
        }

        _output.WriteLine(
            $"EXERCISED: Blender={blenderExecutable}");
        _output.WriteLine(
            $"EXERCISED: DL1 build={ValidatedBuildFingerprint}");
        _output.WriteLine(
            $"EXERCISED: asset={asset.DisplayName}, pack={Path.GetFileName(asset.Source.ContainerPath)}, index={resourceIndex}, resource_sha256={resourceSha256}");
        _output.WriteLine(
            $"EXERCISED: bones={rig.BoneCount}, meshes={preview.Meshes.Count}, textures={result.TexturePaths.Count}, actions={string.Join(",", result.AnimationStacks)}");
        _output.WriteLine(
            $"EXERCISED: fbx_bytes={new FileInfo(outputFbx).Length}, fbx_sha256={fbxSha256}");
        _output.WriteLine(
            "EXERCISED: retail-derived FBX and textures were confined to the disposable test directory.");
    }

    private static AnimationClip CreateClip(
        string name,
        RigDefinition rig,
        BoneDefinition root,
        BoneDefinition deform,
        int frameCount,
        double rootOffset,
        double deformAngle)
    {
        TransformTRS rootEnd = root.LocalBindPose with
        {
            Translation =
                root.LocalBindPose.Translation +
                new Vector3D(rootOffset, 0, 0),
        };
        TransformTRS deformEnd = deform.LocalBindPose with
        {
            Rotation =
                (deform.LocalBindPose.Rotation *
                 QuaternionD.FromAxisAngle(
                     Vector3D.UnitZ,
                     deformAngle))
                .Normalized(),
        };
        return new AnimationClip(
            name,
            new FrameRate(30, 1),
            frameCount,
            [
                new TransformTrack(
                    root.Index,
                    [
                        new TransformKeyframe(
                            0,
                            root.LocalBindPose),
                        new TransformKeyframe(
                            frameCount - 1,
                            rootEnd),
                    ]),
                new TransformTrack(
                    deform.Index,
                    [
                        new TransformKeyframe(
                            0,
                            deform.LocalBindPose),
                        new TransformKeyframe(
                            frameCount - 1,
                            deformEnd),
                    ]),
            ]);
    }

    private static async Task WriteClipAsync(
        string path,
        AnimationClip clip,
        RigDefinition rig,
        BoneDefinition root,
        BoneDefinition deform)
    {
        byte[] bytes = Anm2DomainAdapter.ExportBody(
            clip,
            rig,
            [
                root.DescriptorHash!.Value,
                deform.DescriptorHash!.Value,
            ]);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report ??
                throw new ArgumentNullException(nameof(report));
        }

        public void Report(T value) => _report(value);
    }
}
