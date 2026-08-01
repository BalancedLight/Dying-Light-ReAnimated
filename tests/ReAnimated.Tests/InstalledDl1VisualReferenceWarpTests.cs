using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed partial class InstalledDl1VisualReferenceControlTests
{
    private const int WarpWidth = 128;
    private const int WarpHeight = 128;
    private const int VisualCaptureWidth = 512;
    private const int VisualCaptureHeight = 512;
    private const string VisualCaptureDirectoryEnvironmentVariable =
        "DLR_INSTALLED_VISUAL_CAPTURE_DIR";

    private static readonly JsonSerializerOptions
        VisualCaptureJsonOptions = new()
        {
            WriteIndented = true,
            IncludeFields = true,
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
        };

    private static readonly HashSet<string> WarpControlNames =
        new(StringComparer.Ordinal)
        {
            "player_1_tpp",
            "player_1_fpp",
            "player_11_tpp",
            "player_11_fpp",
            "jade",
            "armored",
            "zombie_voleteile",
            "zombie_screamer",
            "brecken_cin",
            "anim_slums_door_a",
        };

    [Fact(Timeout = 600_000)]
    [Trait("Category", "Renderer")]
    [Trait("Category", "Installed")]
    public async Task InstalledVisualControlsRenderLitMeshesAndSkeletonsOnWarp()
    {
        string? visualCaptureDirectory =
            ResolveVisualCaptureDirectory();
        int renderWidth = visualCaptureDirectory is null
            ? WarpWidth
            : VisualCaptureWidth;
        int renderHeight = visualCaptureDirectory is null
            ? WarpHeight
            : VisualCaptureHeight;
        var visualCaptures = new List<object>();
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            _output.WriteLine(
                "Installed WARP visual controls skipped because no valid DL1 installation was discovered.");
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
                $"Installed WARP visual controls skipped for unvalidated build {build.BuildFingerprint}.");
            return;
        }

        string materialPack = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        if (!File.Exists(materialPack))
        {
            _output.WriteLine(
                $"Installed WARP visual controls skipped because '{materialPack}' is unavailable.");
            return;
        }

        VisualControl[] controls = Controls
            .Where(control => WarpControlNames.Contains(control.Name))
            .ToArray();
        Assert.Equal(WarpControlNames.Count, controls.Length);

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
                            CpuAccessFlags.None,
                            renderWidth,
                            renderHeight));
                using ID3D11RenderTargetView renderTarget =
                    device.CreateRenderTargetView(color);
                using ID3D11Texture2D depth =
                    device.CreateTexture2D(
                        CreateWarpTextureDescription(
                            Format.D24_UNorm_S8_UInt,
                            ResourceUsage.Default,
                            BindFlags.DepthStencil,
                            CpuAccessFlags.None,
                            renderWidth,
                            renderHeight));
                using ID3D11DepthStencilView depthStencil =
                    device.CreateDepthStencilView(depth);
                using ID3D11Texture2D staging =
                    device.CreateTexture2D(
                        CreateWarpTextureDescription(
                            Format.B8G8R8A8_UNorm,
                            ResourceUsage.Staging,
                            BindFlags.None,
                            CpuAccessFlags.Read,
                            renderWidth,
                            renderHeight));
                using var meshPass = new GpuSkinnedMeshRenderPass();
                using var skeletonPass = new SkeletonRenderPass();
                List<string> rendererDiagnostics = [];
                var renderContext = new D3D11RenderFrameContext(
                    device,
                    context,
                    renderTarget,
                    depthStencil,
                    renderWidth,
                    renderHeight,
                    1,
                    rendererDiagnostics.Add);
                var clear = new Color4(
                    0.115f,
                    0.155f,
                    0.215f,
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
                        staging,
                        renderWidth,
                        renderHeight);

                foreach (VisualControl control in controls)
                {
                    RetailAssetRecord asset =
                        Assert.IsType<RetailAssetRecord>(
                            catalog.Resolve(
                                RetailAssetLogicalId.Rpack(
                                    Rp6lResourceTypes.Mesh,
                                    control.Name)));
                    Assert.Equal(
                        control.PackName,
                        Path.GetFileName(
                            asset.Source.ContainerPath));
                    Assert.Equal(
                        control.SourceIndex,
                        asset.Source.ResourceIndex);

                    Rp6lArchive archive =
                        await providers.RpackProvider
                            .GetArchiveAsync(
                                asset.Source.ContainerPath);
                    Rp6lResourceDescriptor resource =
                        archive.Resources[
                            Assert.IsType<int>(
                                asset.Source.ResourceIndex)];
                    string resourceSha256;
                    await using (Stream stream =
                                 await archive
                                     .OpenResourceStreamAsync(
                                         resource,
                                         cache))
                    {
                        resourceSha256 = Convert.ToHexString(
                                await SHA256.HashDataAsync(stream))
                            .ToLowerInvariant();
                    }

                    Assert.Equal(
                        control.ResourceSha256,
                        resourceSha256);

                    Dl1MeshData raw =
                        await Dl1MeshResourceDecoder.DecodeAsync(
                            archive,
                            resource,
                            cache);
                    Dl1MeshData mesh =
                        await resolver.ResolveAsync(raw);
                    Dl1MeshPreviewPayload preview =
                        Dl1MeshPreviewAdapter.Convert(
                            mesh,
                            resourceSha256);
                    if (control.Name == "player_1_fpp")
                    {
                        Assert.DoesNotContain(
                            preview.Meshes,
                            static renderMesh =>
                                renderMesh.Id.Equals(
                                    "player_1_fpp/player_1_hand_l_fpp_decal/lod0/part0",
                                    StringComparison.Ordinal));
                        Assert.Contains(
                            preview.Diagnostics,
                            static diagnostic =>
                                diagnostic.Contains(
                                    "player_1_hand_l_fpp_decal",
                                    StringComparison.Ordinal) &&
                                diagnostic.Contains(
                                    "null.mat",
                                    StringComparison.OrdinalIgnoreCase));
                    }

                    Assert.NotEmpty(preview.Meshes);
                    SkeletonRenderData skeleton =
                        Assert.IsType<SkeletonRenderData>(
                            preview.Skeleton) with
                        {
                            ShowDeformBones = true,
                            ShowHelpers = true,
                            ShowCameraHelpers = true,
                            ShowProps = true,
                        };
                    var roleCounts = skeleton.Bones
                        .GroupBy(static bone => bone.Role)
                        .ToDictionary(
                            static group => group.Key.ToString(),
                            static group => group.Count(),
                            StringComparer.Ordinal);

                    FiniteBounds bounds =
                        MeasureFiniteBounds(
                            preview.Meshes,
                            skeleton);
                    MeshVisualSummary[] meshVisuals =
                        preview.Meshes
                            .Select(mesh =>
                                BuildMeshVisualSummary(
                                    mesh,
                                    skeleton))
                            .ToArray();
                    Assert.True(
                        bounds.SampleCount > 0,
                        $"{control.Name} published no finite preview samples.");
                    Assert.Equal(
                        bounds.SampleCount,
                        bounds.FiniteSampleCount);
                    Assert.True(IsFinite(bounds.Minimum));
                    Assert.True(IsFinite(bounds.Maximum));
                    Assert.True(
                        Vector3.Distance(
                            bounds.Minimum,
                            bounds.Maximum) > 1.0e-4f,
                        $"{control.Name} collapsed to degenerate preview bounds.");

                    RenderFrameSnapshot frame =
                        RenderFrameSnapshot.Empty(
                            new Vector4(
                                clear.R,
                                clear.G,
                                clear.B,
                                clear.A)) with
                        {
                            Meshes = preview.Meshes,
                            Skeleton = skeleton,
                        };
                    Assert.True(
                        RenderCameraFraming.TryFrame(
                            frame,
                            out RenderCamera camera),
                        $"{control.Name} could not be framed.");
                    Assert.True(IsFinite(camera.Eye));
                    Assert.True(IsFinite(camera.Target));
                    Assert.True(IsFinite(camera.Up));
                    Assert.True(
                        float.IsFinite(
                            camera.VerticalFieldOfViewDegrees));
                    Assert.True(float.IsFinite(camera.NearPlane));
                    Assert.True(float.IsFinite(camera.FarPlane));
                    frame = frame with { Camera = camera };

                    rendererDiagnostics.Clear();
                    ClearWarpTarget(
                        context,
                        renderTarget,
                        depthStencil,
                        clear);
                    meshPass.Render(in renderContext, frame);
                    byte[] meshPixels =
                        ReadWarpPixels(
                            context,
                            color,
                            staging,
                            renderWidth,
                            renderHeight);
                    PixelReadbackSummary meshSummary =
                        MeasureReadback(
                            clearPixels,
                            meshPixels);
                    Assert.True(
                        meshSummary.ChangedPixelCount >= 16,
                        $"{control.Name} produced only {meshSummary.ChangedPixelCount} non-background pixels.");
                    Assert.True(
                        meshSummary.NonBlackChangedPixelCount >= 8,
                        $"{control.Name} produced no readable lit surface pixels.");
                    Assert.True(
                        device.DeviceRemovedReason.Success,
                        $"{control.Name} removed the WARP device: {device.DeviceRemovedReason}.");

                    skeletonPass.Render(
                        in renderContext,
                        frame);
                    byte[] overlayPixels =
                        ReadWarpPixels(
                            context,
                            color,
                            staging,
                            renderWidth,
                            renderHeight);
                    int overlayPixelCount =
                        CountChangedPixels(
                            meshPixels,
                            overlayPixels);
                    int whiteDeformOverlayPixelCount =
                        CountChangedPixels(
                            meshPixels,
                            overlayPixels,
                            static (blue, green, red) =>
                                blue > 200 &&
                                green > 200 &&
                                red > 200);
                    int goldPivotOverlayPixelCount =
                        CountChangedPixels(
                            meshPixels,
                            overlayPixels,
                            static (blue, green, red) =>
                                red > 200 &&
                                green > 180 &&
                                blue < 100);
                    Assert.True(
                        overlayPixelCount >= 4,
                        $"{control.Name} skeleton overlay changed only {overlayPixelCount} pixels.");
                    if (control.Name == "anim_slums_door_a")
                    {
                        Assert.Equal(
                            0,
                            whiteDeformOverlayPixelCount);
                        Assert.InRange(
                            goldPivotOverlayPixelCount,
                            4,
                            96);
                        Assert.Equal(
                            goldPivotOverlayPixelCount,
                            overlayPixelCount);
                    }
                    else if (skeleton.Bones.Any(bone =>
                                 bone.Role ==
                                     BoneRenderRole.Deform &&
                                 skeleton.IsVisible(bone)))
                    {
                        Assert.True(
                            whiteDeformOverlayPixelCount >= 4,
                            $"{control.Name} retained deform roles but published only {whiteDeformOverlayPixelCount} white deform-overlay pixels.");
                    }

                    Assert.True(
                        device.DeviceRemovedReason.Success,
                        $"{control.Name} skeleton overlay removed the WARP device: {device.DeviceRemovedReason}.");
                    Assert.Empty(rendererDiagnostics);

                    if (visualCaptureDirectory is not null)
                    {
                        string meshFileName =
                            $"{control.Name}-mesh.bmp";
                        string skeletonFileName =
                            $"{control.Name}-skeleton.bmp";
                        byte[] meshBitmap =
                            BuildTopDownBgraBitmap(
                                meshPixels,
                                renderWidth,
                                renderHeight);
                        byte[] skeletonBitmap =
                            BuildTopDownBgraBitmap(
                                overlayPixels,
                                renderWidth,
                                renderHeight);
                        WriteAtomic(
                            Path.Combine(
                                visualCaptureDirectory,
                                meshFileName),
                            meshBitmap);
                        WriteAtomic(
                            Path.Combine(
                                visualCaptureDirectory,
                                skeletonFileName),
                            skeletonBitmap);
                        visualCaptures.Add(
                            new
                            {
                                control.Name,
                                ResourceSha256 = resourceSha256,
                                MeshFile = meshFileName,
                                MeshBmpSha256 =
                                    ComputeSha256(meshBitmap),
                                SkeletonFile = skeletonFileName,
                                SkeletonBmpSha256 =
                                    ComputeSha256(skeletonBitmap),
                                BoneRoles = roleCounts,
                                Meshes = meshVisuals,
                                Camera = new
                                {
                                    Eye = new[]
                                    {
                                        camera.Eye.X,
                                        camera.Eye.Y,
                                        camera.Eye.Z,
                                    },
                                    Target = new[]
                                    {
                                        camera.Target.X,
                                        camera.Target.Y,
                                        camera.Target.Z,
                                    },
                                    Up = new[]
                                    {
                                        camera.Up.X,
                                        camera.Up.Y,
                                        camera.Up.Z,
                                    },
                                    camera.VerticalFieldOfViewDegrees,
                                    camera.NearPlane,
                                    camera.FarPlane,
                                },
                            });
                    }

                    _output.WriteLine(
                        $"{control.Name}: sha256={resourceSha256}, meshes={preview.Meshes.Count}, " +
                        $"samples={bounds.FiniteSampleCount:N0}, bounds={bounds.Minimum}..{bounds.Maximum}, " +
                        $"meshPixels={meshSummary.ChangedPixelCount:N0}, litPixels={meshSummary.NonBlackChangedPixelCount:N0}, " +
                        $"skeletonPixels={overlayPixelCount:N0}, roles={string.Join('/', roleCounts.Select(static pair => $"{pair.Key}:{pair.Value}"))}, diagnostics={rendererDiagnostics.Count}");
                }

                if (visualCaptureDirectory is not null)
                {
                    byte[] manifest =
                        JsonSerializer.SerializeToUtf8Bytes(
                            new
                            {
                                Format =
                                    "dl-reanimated-installed-visual-captures-v1",
                                Width = renderWidth,
                                Height = renderHeight,
                                BuildFingerprint =
                                    build.BuildFingerprint,
                                Controls = visualCaptures,
                            },
                            VisualCaptureJsonOptions);
                    WriteAtomic(
                        Path.Combine(
                            visualCaptureDirectory,
                            "manifest.json"),
                        manifest);
                    _output.WriteLine(
                        $"Installed visual captures: {visualCaptureDirectory}");
                }
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static string? ResolveVisualCaptureDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable(
            VisualCaptureDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(configured);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static FiniteBounds MeasureFiniteBounds(
        IReadOnlyList<MeshRenderData> meshes,
        SkeletonRenderData skeleton)
    {
        int sampleCount = 0;
        int finiteSampleCount = 0;
        Vector3 minimum =
            new(float.PositiveInfinity);
        Vector3 maximum =
            new(float.NegativeInfinity);
        foreach (MeshRenderData mesh in meshes)
        {
            CpuDeformedVertex[] vertices =
                CpuMeshDeformationEvaluator.Evaluate(
                    mesh,
                    skeleton,
                    []);
            foreach (CpuDeformedVertex vertex in vertices)
            {
                sampleCount++;
                if (!IsFinite(vertex.Position) ||
                    !IsFinite(vertex.Normal))
                {
                    continue;
                }

                finiteSampleCount++;
                minimum = Vector3.Min(
                    minimum,
                    vertex.Position);
                maximum = Vector3.Max(
                    maximum,
                    vertex.Position);
            }
        }

        foreach (BoneRenderData bone in skeleton.Bones)
        {
            sampleCount++;
            Matrix4x4 world =
                bone.WorldTransform *
                skeleton.RootTransform;
            Vector3 position = world.Translation;
            if (!IsFinite(position))
            {
                continue;
            }

            finiteSampleCount++;
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        return new FiniteBounds(
            sampleCount,
            finiteSampleCount,
            minimum,
            maximum);
    }

    private static MeshVisualSummary BuildMeshVisualSummary(
        MeshRenderData mesh,
        SkeletonRenderData skeleton)
    {
        CpuDeformedVertex[] vertices =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                skeleton,
                []);
        Vector3 minimum =
            new(float.PositiveInfinity);
        Vector3 maximum =
            new(float.NegativeInfinity);
        foreach (CpuDeformedVertex vertex in vertices)
        {
            if (!IsFinite(vertex.Position))
            {
                continue;
            }

            minimum = Vector3.Min(minimum, vertex.Position);
            maximum = Vector3.Max(maximum, vertex.Position);
        }

        return new MeshVisualSummary(
            mesh.Id,
            mesh.Vertices.Length,
            mesh.Indices.Length,
            mesh.IsSkinned,
            mesh.BaseColorTexture?.Id,
            minimum,
            maximum);
    }

    private static PixelReadbackSummary MeasureReadback(
        byte[] background,
        byte[] rendered)
    {
        Assert.Equal(background.Length, rendered.Length);
        int changedPixelCount = 0;
        int nonBlackChangedPixelCount = 0;
        for (int offset = 0;
             offset < rendered.Length;
             offset += 4)
        {
            bool changed =
                rendered[offset] != background[offset] ||
                rendered[offset + 1] != background[offset + 1] ||
                rendered[offset + 2] != background[offset + 2];
            if (!changed)
            {
                continue;
            }

            changedPixelCount++;
            int brightness =
                rendered[offset] +
                rendered[offset + 1] +
                rendered[offset + 2];
            if (brightness >= 24)
            {
                nonBlackChangedPixelCount++;
            }
        }

        return new PixelReadbackSummary(
            changedPixelCount,
            nonBlackChangedPixelCount);
    }

    private static int CountChangedPixels(
        byte[] before,
        byte[] after)
    {
        Assert.Equal(before.Length, after.Length);
        int count = 0;
        for (int offset = 0;
             offset < after.Length;
             offset += 4)
        {
            if (before[offset] != after[offset] ||
                before[offset + 1] != after[offset + 1] ||
                before[offset + 2] != after[offset + 2])
            {
                count++;
            }
        }

        return count;
    }

    private static int CountChangedPixels(
        byte[] before,
        byte[] after,
        Func<byte, byte, byte, bool> predicate)
    {
        Assert.Equal(before.Length, after.Length);
        int count = 0;
        for (int offset = 0;
             offset < after.Length;
             offset += 4)
        {
            if ((before[offset] != after[offset] ||
                 before[offset + 1] != after[offset + 1] ||
                 before[offset + 2] != after[offset + 2]) &&
                predicate(
                    after[offset],
                    after[offset + 1],
                    after[offset + 2]))
            {
                count++;
            }
        }

        return count;
    }

    private static void ClearWarpTarget(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView renderTarget,
        ID3D11DepthStencilView depthStencil,
        Color4 clear)
    {
        context.ClearRenderTargetView(renderTarget, clear);
        context.ClearDepthStencilView(
            depthStencil,
            DepthStencilClearFlags.Depth,
            1.0f,
            0);
    }

    private static Texture2DDescription
        CreateWarpTextureDescription(
            Format format,
            ResourceUsage usage,
            BindFlags bindFlags,
            CpuAccessFlags cpuAccessFlags,
            int width = WarpWidth,
            int height = WarpHeight) =>
        new()
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuAccessFlags,
            MiscFlags = ResourceOptionFlags.None,
        };

    private static byte[] ReadWarpPixels(
        ID3D11DeviceContext context,
        ID3D11Texture2D source,
        ID3D11Texture2D staging,
        int width = WarpWidth,
        int height = WarpHeight)
    {
        context.CopyResource(staging, source);
        context.Flush();
        context.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None,
            out MappedSubresource mapped).CheckError();
        try
        {
            byte[] pixels =
                new byte[checked(width * height * 4)];
            int rowBytes = checked(width * 4);
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(
                        mapped.DataPointer,
                        checked((int)(row * mapped.RowPitch))),
                    pixels,
                    row * rowBytes,
                    rowBytes);
            }

            return pixels;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static byte[] BuildTopDownBgraBitmap(
        byte[] pixels,
        int width,
        int height)
    {
        int pixelBytes = checked(width * height * 4);
        if (pixels.Length != pixelBytes)
        {
            throw new InvalidDataException(
                $"Capture contains {pixels.Length:N0} bytes; expected {pixelBytes:N0}.");
        }

        const int headerBytes = 14 + 40;
        byte[] bitmap = new byte[checked(headerBytes + pixelBytes)];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(
            bitmap.AsSpan(2, 4),
            bitmap.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            bitmap.AsSpan(10, 4),
            headerBytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            bitmap.AsSpan(14, 4),
            40);
        BinaryPrimitives.WriteInt32LittleEndian(
            bitmap.AsSpan(18, 4),
            width);
        BinaryPrimitives.WriteInt32LittleEndian(
            bitmap.AsSpan(22, 4),
            -height);
        BinaryPrimitives.WriteInt16LittleEndian(
            bitmap.AsSpan(26, 2),
            1);
        BinaryPrimitives.WriteInt16LittleEndian(
            bitmap.AsSpan(28, 2),
            32);
        BinaryPrimitives.WriteInt32LittleEndian(
            bitmap.AsSpan(34, 4),
            pixelBytes);
        pixels.CopyTo(bitmap, headerBytes);
        return bitmap;
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static void WriteAtomic(
        string path,
        byte[] bytes)
    {
        string temporaryPath =
            path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record MeshVisualSummary(
        string Id,
        int VertexCount,
        int IndexCount,
        bool IsSkinned,
        string? TextureId,
        Vector3 Minimum,
        Vector3 Maximum);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private readonly record struct FiniteBounds(
        int SampleCount,
        int FiniteSampleCount,
        Vector3 Minimum,
        Vector3 Maximum);

    private readonly record struct PixelReadbackSummary(
        int ChangedPixelCount,
        int NonBlackChangedPixelCount);
}
