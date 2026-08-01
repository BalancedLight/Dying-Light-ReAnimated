using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed partial class InstalledDl1VisualReferenceControlTests
{
    private static readonly OversizedRigControl[]
        OversizedRigControls =
    [
        new(
            @"DW\Data\common_meshes_PC.rpack",
            38,
            "acolyte"),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            2023,
            "jade_cin"),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            4327,
            "survivor_a"),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            4357,
            "survivor_woman_a"),
        new(
            @"DW\Data\common_meshes_PC.rpack",
            5202,
            "zombie_woman"),
        new(
            @"DW_DLC17\Data\wasteland_final_PC.rpack",
            36,
            "mother"),
        new(
            @"DW_DLC17\Data\wasteland_final_PC.rpack",
            43,
            "survivor_b"),
        new(
            @"DW_DLC17\Data\wasteland_final_PC.rpack",
            45,
            "survivor_woman_b"),
        new(
            @"DW_DLC17\Data\wasteland_PC.rpack",
            767,
            "mother"),
        new(
            @"DW_DLC17\Data\wasteland_PC.rpack",
            852,
            "survivor_b"),
        new(
            @"DW_DLC17\Data\wasteland_PC.rpack",
            867,
            "survivor_woman_b"),
        new(
            @"DW_DLC17\Data\wasteland_PC.rpack",
            1732,
            "zombie_man_b"),
        new(
            @"DW_DLC17\Data\wasteland_PC.rpack",
            1747,
            "zombie_woman_b"),
        new(
            @"DW_DLC49\Data\hellraid_PC.rpack",
            722,
            "zombie_man_b"),
    ];

    [Fact(Timeout = 1_200_000)]
    [Trait("Category", "Renderer")]
    [Trait("Category", "Installed")]
    public async Task InstalledOversizedPhysicalRigRowsUseCompactPalettesOnWarp()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location =>
                location.IsValid);
        if (install is null)
        {
            _output.WriteLine(
                "Installed oversized-rig WARP controls skipped because no valid DL1 installation was discovered.");
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
                $"Installed oversized-rig WARP controls skipped for unvalidated build {build.BuildFingerprint}.");
            return;
        }

        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder
                    .LocalApplicationData),
            "DLReAnimated",
            "Cache",
            "Rp6lCorpus");
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = cacheDirectory,
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes =
                    16L * 1024 * 1024 * 1024,
                CopyBufferBytes = 256 * 1024,
            });

        D3D11CreateDevice(
            null,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? context).CheckError();
        using (device)
        using (context)
        {
            Assert.NotNull(device);
            Assert.NotNull(context);
            using ID3D11Texture2D color =
                device.CreateTexture2D(
                    CreateWarpTextureDescription(
                        Format.B8G8R8A8_UNorm,
                        ResourceUsage.Default,
                        BindFlags.RenderTarget,
                        CpuAccessFlags.None));
            using ID3D11RenderTargetView renderTarget =
                device.CreateRenderTargetView(color);
            using ID3D11Texture2D depth =
                device.CreateTexture2D(
                    CreateWarpTextureDescription(
                        Format.D24_UNorm_S8_UInt,
                        ResourceUsage.Default,
                        BindFlags.DepthStencil,
                        CpuAccessFlags.None));
            using ID3D11DepthStencilView depthStencil =
                device.CreateDepthStencilView(depth);
            using ID3D11Texture2D staging =
                device.CreateTexture2D(
                    CreateWarpTextureDescription(
                        Format.B8G8R8A8_UNorm,
                        ResourceUsage.Staging,
                        BindFlags.None,
                        CpuAccessFlags.Read));
            using var meshPass =
                new GpuSkinnedMeshRenderPass();
            List<string> rendererDiagnostics = [];
            var renderContext =
                new D3D11RenderFrameContext(
                    device,
                    context,
                    renderTarget,
                    depthStencil,
                    WarpWidth,
                    WarpHeight,
                    1,
                    rendererDiagnostics.Add);
            var clear = new Color4(
                0.03f,
                0.05f,
                0.09f,
                1.0f);
            context.OMSetRenderTargets(
                renderTarget,
                depthStencil);
            ClearWarpTarget(
                context,
                renderTarget,
                depthStencil,
                clear);
            byte[] clearPixels =
                ReadWarpPixels(
                    context,
                    color,
                    staging);
            int oversizedPublishedSkeletonCount = 0;

            foreach (OversizedRigControl control in
                     OversizedRigControls)
            {
                string packPath = Path.Combine(
                    install.InstallPath,
                    control.PackRelativePath);
                Assert.True(
                    File.Exists(packPath),
                    $"Missing validated pack '{packPath}'.");
                Rp6lArchive archive =
                    await Rp6lArchive.OpenAsync(packPath);
                Rp6lResourceDescriptor resource =
                    archive.Resources[control.ResourceIndex];
                Assert.Equal(
                    control.ResourceIndex,
                    resource.Index);
                Assert.Equal(
                    Rp6lResourceTypes.Mesh,
                    resource.ResourceType);
                Assert.Equal(
                    control.ResourceName,
                    resource.Name);

                Dl1MeshData decoded =
                    await Dl1MeshResourceDecoder.DecodeAsync(
                        archive,
                        resource,
                        cache);
                int physicalRigRowCount =
                    decoded.Hierarchy.Bones.Count +
                    decoded.Hierarchy.Helpers.Count;
                Assert.True(
                    physicalRigRowCount >
                    GpuSkinningPalette.MaximumBoneCount,
                    $"{control.ResourceName} no longer has an oversized decoded bone/helper inventory.");
                Dl1MeshPreviewPayload preview =
                    Dl1MeshPreviewAdapter.Convert(decoded);
                SkeletonRenderData skeleton =
                    Assert.IsType<SkeletonRenderData>(
                        preview.Skeleton);
                if (skeleton.Bones.Count >
                    GpuSkinningPalette.MaximumBoneCount)
                {
                    oversizedPublishedSkeletonCount++;
                }

                MeshRenderData[] skinnedMeshes =
                    preview.Meshes
                        .Where(static mesh =>
                            mesh.IsSkinned)
                        .ToArray();
                Assert.NotEmpty(skinnedMeshes);
                foreach (MeshRenderData mesh in
                         preview.Meshes)
                {
                    Assert.True(
                        RenderMeshValidation.TryValidate(
                            mesh,
                            skeleton,
                            out string? validationError),
                        $"{mesh.Id}: {validationError}");
                }

                foreach (MeshRenderData mesh in
                         skinnedMeshes)
                {
                    Assert.NotEmpty(
                        mesh.SkinBoneIndices.ToArray());
                    Assert.Equal(
                        mesh.InverseBindMatrices.Length,
                        mesh.SkinBoneIndices.Length);
                    Assert.InRange(
                        mesh.SkinBoneIndices.Length,
                        1,
                        GpuSkinningPalette
                            .MaximumBoneCount);
                    Assert.All(
                        mesh.SkinBoneIndices.ToArray(),
                        skeletonBoneIndex =>
                            Assert.InRange(
                                skeletonBoneIndex,
                                0,
                                skeleton.Bones.Count - 1));
                }

                MeshRenderData[] warpMeshes =
                    preview.Meshes
                        .Select(static mesh => mesh with
                        {
                            BaseColorTexture = null,
                            MorphTargets =
                                Array.Empty<
                                    MorphTargetRenderData>(),
                        })
                        .ToArray();
                RenderFrameSnapshot frame =
                    RenderFrameSnapshot.Empty(
                        new Vector4(
                            clear.R,
                            clear.G,
                            clear.B,
                            clear.A)) with
                    {
                        Meshes = warpMeshes,
                        Skeleton = skeleton,
                    };
                Assert.True(
                    RenderCameraFraming.TryFrame(
                        frame,
                        out RenderCamera camera),
                    $"{control.ResourceName} could not be framed.");
                frame = frame with
                {
                    Camera = camera,
                };

                rendererDiagnostics.Clear();
                ClearWarpTarget(
                    context,
                    renderTarget,
                    depthStencil,
                    clear);
                meshPass.Render(
                    in renderContext,
                    frame);
                context.Flush();
                byte[] renderedPixels =
                    ReadWarpPixels(
                        context,
                        color,
                        staging);
                int changedPixels =
                    CountChangedPixels(
                        clearPixels,
                        renderedPixels);
                Assert.True(
                    changedPixels >= 4,
                    $"{control.ResourceName} produced only {changedPixels} changed pixels.");
                Assert.Empty(rendererDiagnostics);
                Assert.True(
                    device.DeviceRemovedReason.Success,
                    $"{control.ResourceName} removed the WARP device: {device.DeviceRemovedReason}.");

                RenderFrameSnapshot empty =
                    RenderFrameSnapshot.Empty();
                meshPass.Render(
                    in renderContext,
                    empty);
                _output.WriteLine(
                    $"{Path.GetFileName(packPath)}#{resource.Index} {resource.Name}: physicalRows={physicalRigRowCount}, skeleton={skeleton.Bones.Count}, draws={warpMeshes.Length}, skinned={skinnedMeshes.Length}, maxPalette={skinnedMeshes.Max(static mesh => mesh.SkinBoneIndices.Length)}, pixels={changedPixels}");
            }

            Assert.True(
                oversizedPublishedSkeletonCount > 0,
                "The validated oversized physical-rig set no longer contains a published skeleton above the 256-matrix per-draw limit.");
        }
    }

    private sealed record OversizedRigControl(
        string PackRelativePath,
        int ResourceIndex,
        string ResourceName);
}
