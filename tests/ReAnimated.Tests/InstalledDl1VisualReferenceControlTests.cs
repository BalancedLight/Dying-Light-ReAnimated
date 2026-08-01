using System.Numerics;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Renderer.D3D11;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

/// <summary>
/// Installed-only controls derived from resource labels visible in the user's
/// DL1 editor captures. The captures and retail payloads are not test inputs
/// and are never copied into the repository.
/// </summary>
public sealed partial class InstalledDl1VisualReferenceControlTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private static readonly string[] HumanoidLimbAnchors =
        ["l_upperarm", "r_upperarm", "l_thigh", "r_thigh"];

    private static readonly (string Name, int Index)[]
        ExpectedEmbeddedAnimatedPropMatches =
        [
            ("anim_circuit_box_door_b", 63),
            ("anim_fuse_boxes_lever", 66),
            ("anim_slums_door_a", 70),
            ("anim_valve", 76),
            ("basket_b_anm", 260),
            ("basket_c_anm", 265),
            ("book_anm", 386),
        ];

    private static readonly VisualControl[] Controls =
    [
        new(
            "player_1_tpp",
            "common_cod_1_PC.rpack",
            6,
            "45dff339c74711f55d21274030eb73ab18aba71c6949ef2352f696a6e6fd3b2e",
            "352e6251cd89250d40003e28885157336957b426a8009cd0cd01fff04d85bf1a",
            Dl1RigFamily.Player,
            Dl1MeshPerspective.ThirdPerson,
            new(19, 24_213, 92_157, 22_608, 69, 18, 87),
            new(69, 16, 2, 0),
            46,
            10,
            13,
            true),
        new(
            "player_1_fpp",
            "common_cod_1_PC.rpack",
            5,
            Dl1MeshPreviewAdapter.ValidatedPlayer1FppResourceSha256,
            "f81ae72a07883ab7fc56857213d43d2e403d56f8d09ffac57f9f89e12f474c0d",
            Dl1RigFamily.Player,
            Dl1MeshPerspective.FirstPerson,
            new(17, 27_461, 111_144, 19_721, 69, 18, 87),
            new(69, 16, 2, 0),
            46,
            10,
            10,
            true),
        new(
            "player_11_fpp",
            "common_cod_2_PC.rpack",
            5,
            "f5d67276cc9ce20be70767cdb1bf2fc357b74415dbe063462710663b89c5d363",
            "4b2c98162117186514860ea98c049cf65192d327bae87d2e769073d3a906ab25",
            Dl1RigFamily.Player,
            Dl1MeshPerspective.FirstPerson,
            new(5, 9_793, 48_009, 15_997, 65, 25, 90),
            new(65, 23, 2, 0),
            0,
            5,
            5,
            true),
        new(
            "player_11_tpp",
            "common_cod_2_PC.rpack",
            6,
            "89315c41806d721a0f058e1f21fec0a01e5c13e7343b91139fd0e370ed321b79",
            "dbef62f96e7e30e4449536b07e79dc58103b151af45b760733c4cb84d69970d1",
            Dl1RigFamily.Player,
            Dl1MeshPerspective.ThirdPerson,
            new(16, 20_815, 74_853, 19_465, 65, 25, 84),
            new(65, 17, 2, 0),
            46,
            9,
            9,
            true),
        new(
            "jade",
            "common_meshes_PC.rpack",
            2_022,
            "87c7c570918393d6bdc68c1ee4f53fbbe3c678e059009d7e81e87cfe6f03883f",
            "fe268144f8054f51e488db44dc5f49f05d81a57d7b2c7a4c18b0481d48354980",
            Dl1RigFamily.GenericNpc,
            Dl1MeshPerspective.Unknown,
            new(15, 18_306, 86_364, 20_247, 173, 18, 185),
            new(173, 10, 2, 0),
            46,
            10,
            15,
            true),
        new(
            "armored",
            "common_meshes_PC.rpack",
            159,
            "22f4001d0e8ddc3659ec2860dfbb8879fabb69a716a27f3353652b7e6278ea5e",
            "dc830e05f54d04e9b019f2839d45ac2c22d9ea2479e98aad2bd1f75be6aa8cda",
            Dl1RigFamily.Demolisher,
            Dl1MeshPerspective.Unknown,
            new(19, 16_277, 72_678, 19_330, 57, 20, 77),
            new(57, 18, 2, 0),
            15,
            7,
            18,
            true),
        new(
            "zombie_voleteile",
            "common_meshes_PC.rpack",
            5_200,
            "06ae029e4f22ba2fa098b28477c8f3012e34da232f903b010f514fe52f988471",
            null,
            Dl1RigFamily.Unknown,
            Dl1MeshPerspective.Unknown,
            new(25, 15_980, 69_204, 16_588, 87, 41, 0),
            new(87, 41, 0, 0),
            15,
            4,
            19,
            false),
        new(
            "zombie_screamer",
            "common_meshes_PC.rpack",
            5_198,
            "cd28ea77ef5d29d3461af577753dc7f08f08103d929b5ab3c0bc487f24ea6c6e",
            "dd27a9f327196524b84381656588478367bfd955d8e55dba7c40b187569ce28c",
            Dl1RigFamily.Screamer,
            Dl1MeshPerspective.Unknown,
            new(4, 13_263, 59_988, 11_760, 68, 12, 80),
            new(68, 10, 2, 0),
            0,
            3,
            3,
            true),
        new(
            "brecken_cin",
            "common_meshes_PC.rpack",
            428,
            "1cb6e4b3f8677095fdf00527e63b6da241c946366461d2722a88b0b2a38a60cf",
            "df075d1cceaee13c73ddb5d576b9dca51449293f835c2a83a5f40bf6d0ae9a19",
            Dl1RigFamily.Unknown,
            Dl1MeshPerspective.Unknown,
            new(12, 13_998, 66_759, 15_684, 142, 27, 163),
            new(142, 19, 2, 0),
            0,
            10,
            10,
            true),
        new(
            "anim_slums_door_a",
            "common_meshes_PC.rpack",
            70,
            "2bdeabeda4b3d6fd6b8408dcf4a26d4f96b536862e05d6a7fbf103ab6ce8f848",
            "358ddafbefd68ecad86faff06bdbe0bdaab2a008990384b23ec065cb59a3bd9e",
            Dl1RigFamily.Unknown,
            Dl1MeshPerspective.Unknown,
            new(1, 1_224, 3_420, 396, 4, 5, 10),
            new(0, 5, 0, 5),
            0,
            2,
            2,
            true),
    ];

    private static readonly string[] Player1FppRenderIds =
    [
        "player_1_fpp/cult_arm_belt/lod0/part0",
        "player_1_fpp/kevin_boots/lod0/part0",
        "player_1_fpp/kevin_shirt_fpp/lod0/part0",
        "player_1_fpp/kevin_trousers/lod0/part0",
        "player_1_fpp/kevin_trousers/lod0/part1",
        "player_1_fpp/player_1_hand_l_fpp/lod0/part0",
        "player_1_fpp/player_1_hand_r_fpp/lod0/part0",
        "player_1_fpp/player_1_hand_r_fpp/lod0/part1",
        "player_1_fpp/player_1_hip_bag/lod0/part0",
        "player_1_fpp/watch/lod0/part0",
        "player_1_fpp/watch/lod0/part1",
    ];

    private readonly ITestOutputHelper _output;

    public InstalledDl1VisualReferenceControlTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 600_000)]
    public async Task InstalledVisualControlsDecodeIntoCoherentPreviewPayloads()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        if (!string.Equals(
                build.BuildFingerprint,
                ValidatedBuildFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(
                $"Installed visual controls skipped for build {build.BuildFingerprint}.");
            return;
        }

        string materialPack = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        if (!File.Exists(materialPack))
        {
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 128L * 1024 * 1024,
                    MaximumMemoryEntryBytes = 32 * 1024 * 1024,
                    MaximumDiskBytes = 2L * 1024 * 1024 * 1024,
                });
            await using Dl1RetailProviderSet providers =
                Dl1RetailProviderSet.Create(
                    install.InstallPath,
                    cache);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    providers.Providers);
            var resolver = new Dl1MaterialTextureResolver(
                catalog,
                providers.RpackProvider,
                cache,
                materialPack);
            var classifier =
                new Dl1RetailMeshClassificationService();
            var visualMismatches = new List<string>();

            foreach (VisualControl control in Controls)
            {
                RetailAssetRecord asset =
                    Assert.IsType<RetailAssetRecord>(
                        catalog.Resolve(
                            RetailAssetLogicalId.Rpack(
                                Rp6lResourceTypes.Mesh,
                                control.Name)));
                Rp6lArchive archive =
                    await providers.RpackProvider.GetArchiveAsync(
                        asset.Source.ContainerPath);
                Rp6lResourceDescriptor resource =
                    archive.Resources[
                        Assert.IsType<int>(
                            asset.Source.ResourceIndex)];
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

                Dl1MeshData raw =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        cache);
                Dl1MeshData mesh =
                    await resolver.ResolveAsync(raw);
                Dl1RetailMeshProfile profile =
                    classifier.Classify(asset, mesh);
                Dl1MeshPreviewPayload preview =
                    Dl1MeshPreviewAdapter.Convert(
                        mesh,
                        resourceSha256);
                OrientationSummary orientation =
                    MeasureOrientation(preview);
                SkeletonRegistrationSummary registration =
                    MeasureSkeletonRegistration(preview);
                SkeletonRenderData? skeleton =
                    preview.Skeleton;
                int resolvedMaterials = mesh.MaterialSlots.Count(
                    static slot => slot.ResolvedMaterial is not null);
                int baseColors = mesh.MaterialSlots.Count(
                    static slot =>
                        slot.ResolvedMaterial?.BaseColorPreview is not null);
                int rendererTextures = preview.Meshes.Count(
                    static renderMesh =>
                        renderMesh.BaseColorTexture is not null);

                _output.WriteLine(
                    $"{control.Name}: pack={Path.GetFileName(asset.Source.ContainerPath)}, " +
                    $"index={asset.Source.ResourceIndex}, geometry={profile.GeometryKind}, " +
                    $"family={profile.RigFamily}, perspective={profile.Perspective}, " +
                    $"sha256={resourceSha256}, signature={profile.RigSignature}, " +
                    $"surfaces={mesh.Surfaces.Count}, vertices={mesh.Surfaces.Sum(static surface => surface.VertexCount):N0}, " +
                    $"indices={mesh.Surfaces.Sum(static surface => surface.IndexCount):N0}, " +
                    $"triangles={orientation.TriangleCount:N0}, alignment={orientation.Alignment:F6}, " +
                    $"opposed={orientation.OpposedAreaRatio:P3}, " +
                    $"registered={registration.InsideExpandedBoundsCount}/{registration.BoneCount}, " +
                    $"deformRegistered={registration.DeformInsideExpandedBoundsCount}/{registration.DeformBoneCount}, " +
                    $"maxOutside={registration.MaximumNormalizedOutsideDistance:F6}, " +
                    $"centerOffset={registration.NormalizedCenterOffset:F6}, " +
                    $"bilateralPairs={registration.BilateralDeformPairCount}, " +
                    $"mirrorResidual={registration.MaximumNormalizedMirrorResidual:F6}, " +
                    $"bones={mesh.Hierarchy.Bones.Count}, helpers={mesh.Hierarchy.Helpers.Count}, " +
                    $"rig={mesh.Rig?.BoneCount ?? 0}, " +
                    $"roles=[deform:{CountRole(skeleton, BoneRenderRole.Deform)},helper:{CountRole(skeleton, BoneRenderRole.Helper)},camera:{CountRole(skeleton, BoneRenderRole.Camera)},prop:{CountRole(skeleton, BoneRenderRole.Prop)}], " +
                    $"materials={mesh.MaterialSlots.Count}, resolved={resolvedMaterials}, baseColors={baseColors}, rendererTextures={rendererTextures}, " +
                    $"morphs={mesh.MorphTargets.Count}, skin={mesh.AppliedSkinName ?? "<none>"}, " +
                    $"skinHidden=[{string.Join(',', mesh.SkinHiddenEntityIndexes.Select(index => mesh.Hierarchy.Entities[index].Name))}], " +
                    $"valid={mesh.IsStructurallyValid}");
                _output.WriteLine(
                    $"  preview IDs: {string.Join(", ", preview.Meshes.Select(static item => item.Id))}");
                if (control.Name is
                    "player_1_fpp" or
                    "player_1_tpp" or
                    "player_11_fpp" or
                    "player_11_tpp" or
                    "zombie_screamer" or
                    "zombie_voleteile")
                {
                    _output.WriteLine(
                        "  material bindings: " +
                        string.Join(
                            ", ",
                            mesh.MaterialSlots.Select(
                                static slot =>
                                    $"{slot.Index}:{slot.DatabaseName}" +
                                    $"[{(slot.RawDatabaseLoadValue.HasValue ? $"0x{slot.RawDatabaseLoadValue.Value:X8}" : "none")}]" +
                                    $"->{slot.ResolvedMaterial?.ResourceName ?? "<unresolved>"}" +
                                    $"->{slot.ResolvedMaterial?.BaseColorPreview?.ResourceName ?? "<no-base-color>"}")));
                    _output.WriteLine(
                        "  texture bindings: " +
                        string.Join(
                            ", ",
                            mesh.MaterialSlots
                                .Where(static slot =>
                                    slot.ResolvedMaterial is not null)
                                .SelectMany(
                                    static slot =>
                                        slot.ResolvedMaterial!.TextureBindings.Select(
                                            texture =>
                                                $"{slot.DatabaseName}:0x{texture.TextureNameHash:X8}" +
                                                $"->{texture.ResourceName ?? "<unknown>"}" +
                                                $"({texture.Semantic})"))));
                    foreach (Dl1MeshSurface surface in mesh.Surfaces)
                    {
                        _output.WriteLine(
                            $"  surface: {surface.Name}/lod{surface.LodIndex}" +
                            $":surface={surface.MaterialSlotIndex}" +
                            $":parts=[{string.Join('|', surface.Submeshes.Select(static part => $"{part.Index}->{part.MaterialSlotIndex}"))}]");
                    }
                }

                Assert.Equal(
                    control.PackName,
                    Path.GetFileName(asset.Source.ContainerPath));
                Assert.Equal(
                    control.SourceIndex,
                    asset.Source.ResourceIndex);
                Assert.Equal(
                    control.ResourceSha256,
                    resourceSha256);
                Assert.Equal(
                    Dl1MeshGeometryKind.Skinned,
                    profile.GeometryKind);
                Assert.Equal(control.Family, profile.RigFamily);
                Assert.Equal(
                    control.Perspective,
                    profile.Perspective);
                Assert.Equal(
                    control.RigSignature,
                    profile.RigSignature);
                Assert.Equal(
                    control.Counts.SurfaceCount,
                    mesh.Surfaces.Count);
                Assert.Equal(
                    control.Counts.VertexCount,
                    mesh.Surfaces.Sum(static surface =>
                        surface.VertexCount));
                Assert.Equal(
                    control.Counts.IndexCount,
                    mesh.Surfaces.Sum(static surface =>
                        surface.IndexCount));
                if (control.Counts.PreviewTriangleCount !=
                    orientation.TriangleCount)
                {
                    visualMismatches.Add(
                        $"{control.Name} triangles: expected " +
                        $"{control.Counts.PreviewTriangleCount}, actual " +
                        $"{orientation.TriangleCount}");
                }
                Assert.Equal(
                    control.Counts.HierarchyBoneCount,
                    mesh.Hierarchy.Bones.Count);
                Assert.Equal(
                    control.Counts.HierarchyHelperCount,
                    mesh.Hierarchy.Helpers.Count);
                Assert.Equal(
                    control.Counts.RigNodeCount,
                    mesh.Rig?.BoneCount ?? 0);
                Assert.Equal(
                    control.Roles.Deform,
                    CountRole(skeleton, BoneRenderRole.Deform));
                Assert.Equal(
                    control.Roles.Helper,
                    CountRole(skeleton, BoneRenderRole.Helper));
                Assert.Equal(
                    control.Roles.Camera,
                    CountRole(skeleton, BoneRenderRole.Camera));
                Assert.Equal(
                    control.Roles.Prop,
                    CountRole(skeleton, BoneRenderRole.Prop));
                Assert.Equal(
                    control.MorphCount,
                    mesh.MorphTargets.Count);
                if (control.BaseColorCount != baseColors)
                {
                    visualMismatches.Add(
                        $"{control.Name} base colors: expected " +
                        $"{control.BaseColorCount}, actual {baseColors}");
                }

                if (control.RendererTextureCount != rendererTextures)
                {
                    visualMismatches.Add(
                        $"{control.Name} renderer textures: expected " +
                        $"{control.RendererTextureCount}, actual " +
                        $"{rendererTextures}");
                }
                Assert.Equal(
                    control.IsStructurallyValid,
                    mesh.IsStructurallyValid);
                Assert.NotEmpty(preview.Meshes);
                if (orientation.Alignment < 0.90)
                {
                    visualMismatches.Add(
                        $"{control.Name} visible winding alignment: " +
                        $"expected at least 0.900000, actual " +
                        $"{orientation.Alignment:F6}");
                }

                if (orientation.OpposedAreaRatio > 0.001)
                {
                    visualMismatches.Add(
                        $"{control.Name} visible opposed area: expected at " +
                        $"most 0.100%, actual " +
                        $"{orientation.OpposedAreaRatio:P3}");
                }
                Assert.True(registration.MeshSampleCount > 0);
                Assert.True(registration.BoneCount > 0);
                Assert.True(float.IsFinite(
                    registration.MaximumNormalizedOutsideDistance));
                Assert.True(float.IsFinite(
                    registration.NormalizedCenterOffset));
                Assert.True(float.IsFinite(
                    registration.MaximumNormalizedMirrorResidual));
                Assert.Equal(
                    registration.DeformBoneCount,
                    registration.DeformInsideExpandedBoundsCount);
                Assert.InRange(
                    registration.NormalizedCenterOffset,
                    0.0f,
                    0.23f);
                if (control.Name == "anim_slums_door_a")
                {
                    Assert.Equal(
                        0,
                        registration.BilateralDeformPairCount);
                    Assert.InRange(
                        registration.MaximumNormalizedOutsideDistance,
                        0.0f,
                        0.20f);
                }
                else
                {
                    Assert.InRange(
                        registration.BilateralDeformPairCount,
                        20,
                        int.MaxValue);
                    Assert.InRange(
                        registration.MaximumNormalizedMirrorResidual,
                        0.0f,
                        0.015f);
                    Assert.InRange(
                        registration.MaximumNormalizedOutsideDistance,
                        0.0f,
                        0.08f);
                }
                Assert.DoesNotContain(
                    preview.Meshes,
                    static renderMesh =>
                        renderMesh.Id.Contains(
                            "/lod1/",
                            StringComparison.Ordinal));

                if (control.Roles.Camera == 2)
                {
                    Assert.Equal(
                        ["eyecamera", "refcamera"],
                        skeleton!.Bones
                            .Where(static bone =>
                                bone.Role ==
                                    BoneRenderRole.Camera)
                            .Select(static bone =>
                                bone.Name.ToLowerInvariant())
                            .OrderBy(static name => name));
                }

                ValidateSpecialControl(
                    control,
                    mesh,
                    preview);
            }

            Assert.True(
                visualMismatches.Count == 0,
                string.Join(Environment.NewLine, visualMismatches));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact(Timeout = 600_000)]
    [Trait("Category", "Installed")]
    public async Task BoundedCommonMeshScanKeepsEmbeddedPropLayoutAwayFromHumanoids()
    {
        const int maximumResourceIndexExclusive = 512;
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        if (!string.Equals(
                build.BuildFingerprint,
                ValidatedBuildFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(
                $"Installed animated-prop scan skipped for build {build.BuildFingerprint}.");
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 128L * 1024 * 1024,
                    MaximumMemoryEntryBytes = 32 * 1024 * 1024,
                    MaximumDiskBytes = 512L * 1024 * 1024,
                });
            await using Dl1RetailProviderSet providers =
                Dl1RetailProviderSet.Create(
                    install.InstallPath,
                    cache);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    providers.Providers);
            string commonPack = Path.Combine(
                install.DataPath,
                "common_meshes_PC.rpack");
            Rp6lArchive archive =
                await providers.RpackProvider.GetArchiveAsync(
                    commonPack);
            RetailAssetRecord[] boundedAssets = catalog.Assets
                .Where(asset =>
                    asset.Id.ResourceType ==
                        Rp6lResourceTypes.Mesh &&
                    asset.Source.ResourceIndex is >= 0 and
                        < maximumResourceIndexExclusive &&
                    string.Equals(
                        asset.Source.ContainerPath,
                        commonPack,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(static asset =>
                    asset.Source.ResourceIndex)
                .ToArray();
            var classifier =
                new Dl1RetailMeshClassificationService();
            var matches =
                new List<(string Name, int Index)>();
            foreach (RetailAssetRecord asset in boundedAssets)
            {
                int resourceIndex =
                    Assert.IsType<int>(
                        asset.Source.ResourceIndex);
                Rp6lResourceDescriptor resource =
                    archive.Resources[resourceIndex];
                Dl1MeshData mesh =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        cache);
                int skeletonEntityCount = Math.Clamp(
                    mesh.Hierarchy.AnimationEntityCountCandidate,
                    0,
                    mesh.Hierarchy.Entities.Count);
                if (!Dl1MeshPreviewAdapter
                        .UsesEmbeddedAnimatedPropRig(
                            mesh,
                            skeletonEntityCount))
                {
                    continue;
                }

                Dl1RetailMeshProfile profile =
                    classifier.Classify(asset, mesh);
                Assert.Equal(
                    Dl1RigFamily.Unknown,
                    profile.RigFamily);
                Assert.False(
                    HasHumanoidRigAnchors(mesh),
                    $"{resource.Name} matched the embedded animated-prop layout despite carrying humanoid rig anchors.");
                SkeletonRenderData skeleton =
                    Assert.IsType<SkeletonRenderData>(
                        Dl1MeshPreviewAdapter
                            .Convert(mesh)
                            .Skeleton);
                Assert.DoesNotContain(
                    skeleton.Bones,
                    static bone =>
                        bone.Role ==
                            BoneRenderRole.Deform);
                matches.Add(
                    (resource.Name, resource.Index));
            }

            _output.WriteLine(
                $"Bounded common_meshes scan [0,{maximumResourceIndexExclusive}) checked {boundedAssets.Length} type-272 winning assets; embedded animated-prop matches={matches.Count}: {string.Join(", ", matches.Select(static match => $"{match.Name}[{match.Index}]"))}");
            Assert.Equal(
                ExpectedEmbeddedAnimatedPropMatches,
                matches,
                EqualityComparer<(string Name, int Index)>
                    .Default);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static int CountRole(
        SkeletonRenderData? skeleton,
        BoneRenderRole role) =>
        skeleton?.Bones.Count(bone => bone.Role == role) ?? 0;

    private static bool HasHumanoidRigAnchors(
        Dl1MeshData mesh)
    {
        if (mesh.Rig is null)
        {
            return false;
        }

        HashSet<string> names = mesh.Rig.Bones
            .Select(static bone => bone.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("bip01") &&
               names.Contains("pelvis") &&
               names.Contains("head") &&
               HumanoidLimbAnchors.Count(names.Contains) >= 2;
    }

    private static void ValidateSpecialControl(
        VisualControl control,
        Dl1MeshData mesh,
        Dl1MeshPreviewPayload preview)
    {
        if (control.Name == "player_1_fpp")
        {
            Assert.Equal(
                Player1FppRenderIds.OrderBy(
                    static id => id,
                    StringComparer.Ordinal),
                preview.Meshes
                    .Select(static renderMesh =>
                        renderMesh.Id)
                    .OrderBy(
                        static id => id,
                        StringComparer.Ordinal));
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "content-fingerprinted DL1 1.55 player_1_fpp",
                        StringComparison.Ordinal));
            Assert.DoesNotContain(
                preview.Meshes,
                static renderMesh =>
                    renderMesh.Id.Contains(
                        "head",
                        StringComparison.OrdinalIgnoreCase) ||
                    renderMesh.Id.Contains(
                        "beard",
                        StringComparison.OrdinalIgnoreCase) ||
                    renderMesh.Id.Contains(
                        "hair",
                        StringComparison.OrdinalIgnoreCase) ||
                    renderMesh.Id.Contains(
                        "_tpp",
                        StringComparison.OrdinalIgnoreCase));
        }
        else if (control.Name is "player_1_tpp" or "player_11_tpp")
        {
            Assert.Contains(
                preview.Meshes,
                static renderMesh =>
                    renderMesh.Id.Contains(
                        "/player_4_head/",
                        StringComparison.Ordinal));
        }
        else if (control.Name == "player_11_fpp")
        {
            Assert.DoesNotContain(
                preview.Meshes,
                static renderMesh =>
                    renderMesh.Id.Contains(
                        "head",
                        StringComparison.OrdinalIgnoreCase) ||
                    renderMesh.Id.Contains(
                        "beard",
                        StringComparison.OrdinalIgnoreCase) ||
                    renderMesh.Id.Contains(
                        "hair",
                        StringComparison.OrdinalIgnoreCase) ||
                    renderMesh.Id.Contains(
                        "_tpp",
                        StringComparison.OrdinalIgnoreCase));
        }
        else if (control.Name == "zombie_voleteile")
        {
            Assert.Null(mesh.Rig);
            CompactMeshEntity nonTrsEntity = Assert.Single(
                mesh.Hierarchy.Entities
                    .Take(
                        mesh.Hierarchy
                            .AnimationEntityCountCandidate),
                IsNonTrsEntity);
            Assert.Equal(36, nonTrsEntity.Index);
            Assert.Equal(
                "patch_elem_arm_left_a",
                nonTrsEntity.Name);
            Assert.True(
                nonTrsEntity.EntityType.HasFlag(
                    CompactMeshEntityType.Helper));
            Assert.DoesNotContain(
                mesh.Surfaces
                    .SelectMany(static surface =>
                        surface.Submeshes)
                    .SelectMany(static submesh =>
                        submesh.BonePaletteEntityIndexes),
                static index => index == 36);
            Assert.Contains(
                mesh.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "DL1MESH014" &&
                    diagnostic.Severity ==
                        Dl1MeshDiagnosticSeverity.Error);
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "raw bind-pose mesh preview",
                        StringComparison.Ordinal));
        }
        else if (control.Name == "zombie_screamer")
        {
            Assert.DoesNotContain(
                mesh.MaterialSlots,
                static slot =>
                    slot.DatabaseName.Equals(
                        "BODY.MAT",
                        StringComparison.OrdinalIgnoreCase) &&
                    slot.ResolvedMaterial is not null);
            Assert.DoesNotContain(
                mesh.MaterialSlots,
                static slot =>
                    slot.DatabaseName.Equals(
                        "HEAD.MAT",
                        StringComparison.OrdinalIgnoreCase) &&
                    slot.ResolvedMaterial is not null);
        }
        else if (control.Name == "anim_slums_door_a")
        {
            Assert.Equal(
                [
                    "bone_bolt01",
                    "bone_bolt02",
                    "bone_door",
                    "bone_handle",
                    "metal_door_a",
                ],
                preview.Skeleton!.Bones
                    .Where(static bone =>
                        bone.Role == BoneRenderRole.Prop)
                    .Select(static bone => bone.Name)
                    .OrderBy(static name => name));
            Assert.DoesNotContain(
                preview.Skeleton.Bones,
                static bone =>
                    bone.Role == BoneRenderRole.Deform);
            Assert.Equal(
                [0, 1, 2, 3],
                mesh.Surfaces
                    .SelectMany(static surface =>
                        surface.Submeshes)
                    .SelectMany(static submesh =>
                        submesh.BonePaletteEntityIndexes)
                    .Select(static index => (int)index)
                    .Distinct()
                    .Order());
            Assert.All(
                preview.Skeleton.Bones.Take(5),
                static bone =>
                {
                    Assert.Equal(
                        BoneRenderRole.Prop,
                        bone.Role);
                    Assert.False(
                        bone.IsHierarchyOverlayVisible);
                });
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "embedded skinned-mesh animated-prop layout",
                        StringComparison.Ordinal));
            Assert.Equal(
                ["metal_door_a", "metal_door_b"],
                mesh.MaterialSlots
                    .Select(static slot =>
                        slot.ResolvedMaterial
                            ?.BaseColorPreview
                            ?.ResourceName)
                    .Where(static name => name is not null)
                    .OrderBy(static name => name));
        }
    }

    private static bool IsNonTrsEntity(
        CompactMeshEntity entity)
    {
        CompactMatrix3x4 local = entity.LocalMatrix;
        var matrix = new TransformMatrix(
            local.M11,
            local.M12,
            local.M13,
            local.M14,
            local.M21,
            local.M22,
            local.M23,
            local.M24,
            local.M31,
            local.M32,
            local.M33,
            local.M34,
            0,
            0,
            0,
            1);
        try
        {
            _ = matrix.Decompose(1.0e-4);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static OrientationSummary MeasureOrientation(
        Dl1MeshPreviewPayload preview)
    {
        double alignedArea = 0.0;
        double opposedArea = 0.0;
        double weightedDot = 0.0;
        int triangleCount = 0;
        foreach (MeshRenderData mesh in preview.Meshes)
        {
            OrientationSummary summary =
                MeasureOrientation(
                    mesh,
                    preview.Skeleton);
            alignedArea +=
                summary.AlignedArea;
            opposedArea +=
                summary.OpposedArea;
            weightedDot +=
                summary.WeightedDot;
            triangleCount +=
                summary.TriangleCount;
        }

        double totalArea = alignedArea + opposedArea;
        return new OrientationSummary(
            triangleCount,
            totalArea <= 0.0 ? 0.0 : weightedDot / totalArea,
            totalArea <= 0.0 ? 1.0 : opposedArea / totalArea,
            alignedArea,
            opposedArea,
            weightedDot);
    }

    private static OrientationSummary MeasureOrientation(
        MeshRenderData mesh,
        SkeletonRenderData? previewSkeleton)
    {
        double alignedArea = 0.0;
        double opposedArea = 0.0;
        double weightedDot = 0.0;
        int triangleCount = 0;
        CpuDeformedVertex[] vertices =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                previewSkeleton,
                []);
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        for (int offset = 0; offset < indices.Length; offset += 3)
        {
            CpuDeformedVertex first =
                vertices[checked((int)indices[offset])];
            CpuDeformedVertex second =
                vertices[checked((int)indices[offset + 1])];
            CpuDeformedVertex third =
                vertices[checked((int)indices[offset + 2])];
            Vector3 cross = Vector3.Cross(
                second.Position - first.Position,
                third.Position - first.Position);
            float twiceArea = cross.Length();
            Vector3 stored =
                first.Normal + second.Normal + third.Normal;
            if (!float.IsFinite(twiceArea) ||
                twiceArea <= 1.0e-10f ||
                stored.LengthSquared() <= 1.0e-10f)
            {
                continue;
            }

            double dot = Vector3.Dot(
                cross / twiceArea,
                Vector3.Normalize(stored));
            weightedDot += dot * twiceArea;
            if (dot >= 0.0)
            {
                alignedArea += twiceArea;
            }
            else
            {
                opposedArea += twiceArea;
            }

            triangleCount++;
        }

        double totalArea = alignedArea + opposedArea;
        return new OrientationSummary(
            triangleCount,
            totalArea <= 0.0 ? 0.0 : weightedDot / totalArea,
            totalArea <= 0.0 ? 1.0 : opposedArea / totalArea,
            alignedArea,
            opposedArea,
            weightedDot);
    }

    private static SkeletonRegistrationSummary MeasureSkeletonRegistration(
        Dl1MeshPreviewPayload preview)
    {
        SkeletonRenderData skeleton =
            Assert.IsType<SkeletonRenderData>(preview.Skeleton);
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        int meshSampleCount = 0;
        foreach (MeshRenderData mesh in preview.Meshes)
        {
            foreach (CpuDeformedVertex vertex in
                     CpuMeshDeformationEvaluator.Evaluate(
                         mesh,
                         skeleton,
                         []))
            {
                if (!IsFiniteRegistrationVector(vertex.Position))
                {
                    continue;
                }

                minimum = Vector3.Min(minimum, vertex.Position);
                maximum = Vector3.Max(maximum, vertex.Position);
                meshSampleCount++;
            }
        }

        Assert.True(meshSampleCount > 0);
        Vector3 size = maximum - minimum;
        float diagonal = size.Length();
        Assert.True(float.IsFinite(diagonal));
        Assert.True(diagonal > 1.0e-6f);
        Vector3 margin = Vector3.Max(
            size * 0.05f,
            new Vector3(diagonal * 0.005f));
        Vector3 expandedMinimum = minimum - margin;
        Vector3 expandedMaximum = maximum + margin;
        int insideCount = 0;
        int deformInsideCount = 0;
        int deformCount = 0;
        float maximumOutsideDistance = 0.0f;
        Vector3 bonePositionSum = Vector3.Zero;
        var worldPositions = new Vector3[skeleton.Bones.Count];
        for (int boneIndex = 0;
             boneIndex < skeleton.Bones.Count;
             boneIndex++)
        {
            BoneRenderData bone = skeleton.Bones[boneIndex];
            Vector3 position = (
                bone.WorldTransform *
                skeleton.RootTransform).Translation;
            Assert.True(IsFiniteRegistrationVector(position));
            worldPositions[boneIndex] = position;
            bonePositionSum += position;
            bool inside = IsInside(
                position,
                expandedMinimum,
                expandedMaximum);
            if (inside)
            {
                insideCount++;
            }

            if (bone.Role == BoneRenderRole.Deform)
            {
                deformCount++;
                if (inside)
                {
                    deformInsideCount++;
                }
            }

            Vector3 outsideVector = new(
                AxisOutsideDistance(
                    position.X,
                    expandedMinimum.X,
                    expandedMaximum.X),
                AxisOutsideDistance(
                    position.Y,
                    expandedMinimum.Y,
                    expandedMaximum.Y),
                AxisOutsideDistance(
                    position.Z,
                    expandedMinimum.Z,
                    expandedMaximum.Z));
            maximumOutsideDistance = MathF.Max(
                maximumOutsideDistance,
                outsideVector.Length());
        }

        Vector3 meshCenter = (minimum + maximum) * 0.5f;
        Vector3 boneCenter =
            bonePositionSum / skeleton.Bones.Count;
        Dictionary<string, int> boneIndexes = skeleton.Bones
            .Select(static (bone, index) => (bone.Name, Index: index))
            .ToDictionary(
                static item => item.Name,
                static item => item.Index,
                StringComparer.OrdinalIgnoreCase);
        float mirrorCenterX = worldPositions[0].X;
        int bilateralDeformPairCount = 0;
        float maximumMirrorResidual = 0.0f;
        foreach ((string name, int leftIndex) in boneIndexes)
        {
            BoneRenderData leftBone = skeleton.Bones[leftIndex];
            if (leftBone.Role != BoneRenderRole.Deform ||
                !name.StartsWith(
                    "l_",
                    StringComparison.OrdinalIgnoreCase) ||
                !boneIndexes.TryGetValue(
                    $"r_{name[2..]}",
                    out int rightIndex) ||
                skeleton.Bones[rightIndex].Role !=
                    BoneRenderRole.Deform)
            {
                continue;
            }

            Vector3 left = worldPositions[leftIndex];
            Vector3 right = worldPositions[rightIndex];
            float residual = MathF.Max(
                MathF.Abs(
                    left.X + right.X -
                    (2.0f * mirrorCenterX)),
                MathF.Max(
                    MathF.Abs(left.Y - right.Y),
                    MathF.Abs(left.Z - right.Z)));
            maximumMirrorResidual = MathF.Max(
                maximumMirrorResidual,
                residual);
            bilateralDeformPairCount++;
        }

        return new SkeletonRegistrationSummary(
            meshSampleCount,
            skeleton.Bones.Count,
            deformCount,
            insideCount,
            deformInsideCount,
            maximumOutsideDistance / diagonal,
            Vector3.Distance(meshCenter, boneCenter) / diagonal,
            bilateralDeformPairCount,
            maximumMirrorResidual / diagonal);
    }

    private static bool IsInside(
        Vector3 value,
        Vector3 minimum,
        Vector3 maximum) =>
        value.X >= minimum.X &&
        value.X <= maximum.X &&
        value.Y >= minimum.Y &&
        value.Y <= maximum.Y &&
        value.Z >= minimum.Z &&
        value.Z <= maximum.Z;

    private static float AxisOutsideDistance(
        float value,
        float minimum,
        float maximum) =>
        value < minimum
            ? minimum - value
            : value > maximum
                ? value - maximum
                : 0.0f;

    private static bool IsFiniteRegistrationVector(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private sealed record OrientationSummary(
        int TriangleCount,
        double Alignment,
        double OpposedAreaRatio,
        double AlignedArea,
        double OpposedArea,
        double WeightedDot);

    private sealed record SkeletonRegistrationSummary(
        int MeshSampleCount,
        int BoneCount,
        int DeformBoneCount,
        int InsideExpandedBoundsCount,
        int DeformInsideExpandedBoundsCount,
        float MaximumNormalizedOutsideDistance,
        float NormalizedCenterOffset,
        int BilateralDeformPairCount,
        float MaximumNormalizedMirrorResidual);

    private sealed record VisualControl(
        string Name,
        string PackName,
        int SourceIndex,
        string ResourceSha256,
        string? RigSignature,
        Dl1RigFamily Family,
        Dl1MeshPerspective Perspective,
        MeshCounts Counts,
        RoleCounts Roles,
        int MorphCount,
        int BaseColorCount,
        int RendererTextureCount,
        bool IsStructurallyValid);

    private sealed record MeshCounts(
        int SurfaceCount,
        int VertexCount,
        int IndexCount,
        int PreviewTriangleCount,
        int HierarchyBoneCount,
        int HierarchyHelperCount,
        int RigNodeCount);

    private sealed record RoleCounts(
        int Deform,
        int Helper,
        int Camera,
        int Prop);
}
