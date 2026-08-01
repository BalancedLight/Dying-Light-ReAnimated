using System.Numerics;
using System.Runtime.InteropServices;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererSkeletonOverlayGoldenTests
{
    private const int Width = 128;
    private const int Height = 128;

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpSkeletonOverlayRemainsVisibleThroughOpaqueMesh()
    {
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
            using ID3D11Texture2D color = device.CreateTexture2D(
                CreateTextureDescription(
                    Format.B8G8R8A8_UNorm,
                    ResourceUsage.Default,
                    BindFlags.RenderTarget,
                    CpuAccessFlags.None));
            using ID3D11RenderTargetView renderTarget =
                device.CreateRenderTargetView(color);
            using ID3D11Texture2D depth = device.CreateTexture2D(
                CreateTextureDescription(
                    Format.D24_UNorm_S8_UInt,
                    ResourceUsage.Default,
                    BindFlags.DepthStencil,
                    CpuAccessFlags.None));
            using ID3D11DepthStencilView depthStencil =
                device.CreateDepthStencilView(depth);
            using ID3D11Texture2D staging = device.CreateTexture2D(
                CreateTextureDescription(
                    Format.B8G8R8A8_UNorm,
                    ResourceUsage.Staging,
                    BindFlags.None,
                    CpuAccessFlags.Read));

            context.OMSetRenderTargets(renderTarget, depthStencil);
            context.RSSetViewport(0, 0, Width, Height);
            context.ClearRenderTargetView(
                renderTarget,
                new Color4(0.03f, 0.05f, 0.09f, 1.0f));
            context.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);

            MeshRenderData mesh = new(
                "skeleton-occluder",
                new MeshVertex[]
                {
                    new MeshVertex(
                        new Vector3(-0.9f, -0.9f, 0.0f),
                        Vector3.UnitZ,
                        Vector2.Zero,
                        Vector4.Zero,
                        Vector4.Zero),
                    new MeshVertex(
                        new Vector3(0.9f, -0.9f, 0.0f),
                        Vector3.UnitZ,
                        Vector2.UnitX,
                        Vector4.Zero,
                        Vector4.Zero),
                    new MeshVertex(
                        new Vector3(0.0f, 0.9f, 0.0f),
                        Vector3.UnitZ,
                        Vector2.UnitY,
                        Vector4.Zero,
                        Vector4.Zero),
                },
                new uint[] { 0, 1, 2 },
                Matrix4x4.Identity,
                ReadOnlyMemory<Matrix4x4>.Empty,
                false)
            {
                Tint = new Vector4(0.08f, 0.07f, 0.07f, 1.0f),
            };
            SkeletonRenderData skeleton = new(
                [
                    new BoneRenderData(
                        "root",
                        -1,
                        Matrix4x4.Identity,
                        Matrix4x4.CreateTranslation(-0.45f, 0.0f, -0.4f),
                        false),
                    new BoneRenderData(
                        "child",
                        0,
                        Matrix4x4.Identity,
                        Matrix4x4.CreateTranslation(0.45f, 0.0f, -0.4f),
                        false),
                ],
                Matrix4x4.Identity);
            RenderFrameSnapshot frame = new(
                new Vector4(0.03f, 0.05f, 0.09f, 1.0f),
                new RenderCamera(
                    new Vector3(0.0f, 0.0f, 3.0f),
                    Vector3.Zero,
                    Vector3.UnitY,
                    55.0f,
                    0.02f,
                    100.0f),
                [mesh],
                skeleton,
                [],
                []);
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                static _ => { });

            using GpuSkinnedMeshRenderPass meshPass = new();
            meshPass.Render(in renderContext, frame);
            byte[] opaquePixels = ReadBack(context, color, staging);
            Assert.Equal(0, CountWhiteDeformPixels(opaquePixels));

            using SkeletonRenderPass skeletonPass = new();
            skeletonPass.Render(in renderContext, frame);
            byte[] overlayPixels = ReadBack(context, color, staging);
            Assert.True(
                CountWhiteDeformPixels(overlayPixels) >= 3,
                "The skeleton line was hidden by the opaque mesh instead of rendering as an editor overlay.");
        }
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpTaperedDeformBoneHasSymmetricScreenSpaceSides()
    {
        SkeletonRenderData skeleton = new(
            [
                RoleBone(
                    "root",
                    -1,
                    new Vector3(0.0f, -0.5f, 0.0f),
                    BoneRenderRole.Deform),
                RoleBone(
                    "child",
                    0,
                    new Vector3(0.0f, 0.5f, 0.0f),
                    BoneRenderRole.Deform),
            ],
            Matrix4x4.Identity);

        byte[] pixels = RenderSkeleton(
            skeleton,
            CreateInspectionCamera());
        PixelBounds bounds = FindPixelBounds(
            pixels,
            IsWhiteDeformPixel);

        Assert.True(bounds.Count >= 12);
        double screenCenter = (Width - 1) * 0.5;
        double leftExtent = screenCenter - bounds.MinimumX;
        double rightExtent = bounds.MaximumX - screenCenter;
        Assert.True(
            leftExtent >= 2.5 && rightExtent >= 2.5,
            $"Expected visible diamond sides, but the white bounds were {bounds.MinimumX}..{bounds.MaximumX}.");
        Assert.InRange(
            Math.Abs(leftExtent - rightExtent),
            0.0,
            1.0);
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpDeformBonesIncludeReadableGreenJointMarkers()
    {
        SkeletonRenderData skeleton = new(
            [
                RoleBone(
                    "root",
                    -1,
                    new Vector3(0.0f, -0.5f, 0.0f),
                    BoneRenderRole.Deform),
                RoleBone(
                    "child",
                    0,
                    new Vector3(0.0f, 0.5f, 0.0f),
                    BoneRenderRole.Deform),
            ],
            Matrix4x4.Identity);

        byte[] pixels = RenderSkeleton(
            skeleton,
            CreateInspectionCamera());

        Assert.True(
            CountGreenJointPixels(pixels) >= 2,
            "Deform bones should retain the retail-editor-style green joint locators.");
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpNearZeroDeformBoneRetainsFiniteMarker()
    {
        SkeletonRenderData skeleton = new(
            [
                RoleBone(
                    "hidden-helper-parent",
                    -1,
                    new Vector3(0.0f, 0.0f, 0.0f),
                    BoneRenderRole.Helper),
                RoleBone(
                    "near-zero-child",
                    0,
                    new Vector3(1.0e-8f, 0.0f, 0.0f),
                    BoneRenderRole.Deform),
            ],
            Matrix4x4.Identity);

        byte[] pixels = RenderSkeleton(
            skeleton,
            CreateInspectionCamera());

        Assert.True(
            CountWhiteDeformPixels(pixels) >= 3,
            "A finite near-zero deform link should retain a bounded white locator.");
        Assert.Equal(0, CountGoldPivotPixels(pixels));
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpSkeletonRolesRespectVisibilityAndKeepCameraHelpersDistinct()
    {
        var skeleton = new SkeletonRenderData(
            [
                RoleBone(
                    "root",
                    -1,
                    new Vector3(-0.65f, 0.0f, 0.0f),
                    BoneRenderRole.Deform),
                RoleBone(
                    "deform",
                    0,
                    new Vector3(-0.15f, 0.0f, 0.0f),
                    BoneRenderRole.Deform),
                RoleBone(
                    "helper",
                    1,
                    new Vector3(0.35f, 0.0f, 0.0f),
                    BoneRenderRole.Helper),
                RoleBone(
                    "camera",
                    1,
                    new Vector3(-0.15f, 0.55f, 0.0f),
                    BoneRenderRole.Camera),
                RoleBone(
                    "prop",
                    1,
                    new Vector3(-0.15f, -0.55f, 0.0f),
                    BoneRenderRole.Prop),
            ],
            Matrix4x4.Identity);
        RenderCamera camera = CreateInspectionCamera();

        byte[] defaults = RenderSkeleton(skeleton, camera);
        Assert.Equal(0, CountGoldPivotPixels(defaults));
        Assert.True(CountOrangeCameraPixels(defaults) >= 2);

        byte[] helpersVisible = RenderSkeleton(
            skeleton with
            {
                ShowHelpers = true,
            },
            camera);
        Assert.True(CountGoldPivotPixels(helpersVisible) >= 2);

        byte[] propsVisible = RenderSkeleton(
            skeleton with
            {
                ShowProps = true,
            },
            camera);
        Assert.True(CountGoldPivotPixels(propsVisible) >= 2);

        byte[] camerasHidden = RenderSkeleton(
            skeleton with
            {
                ShowCameraHelpers = false,
            },
            camera);
        Assert.Equal(0, CountOrangeCameraPixels(camerasHidden));
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpVisibleChildOfHiddenParentUsesStandaloneMarker()
    {
        var skeleton = new SkeletonRenderData(
            [
                RoleBone(
                    "hidden-prop-parent",
                    -1,
                    new Vector3(-0.75f, 0.0f, 0.0f),
                    BoneRenderRole.Prop),
                RoleBone(
                    "visible-helper-child",
                    0,
                    new Vector3(0.75f, 0.0f, 0.0f),
                    BoneRenderRole.Helper),
            ],
            Matrix4x4.Identity)
        {
            ShowHelpers = true,
            ShowProps = false,
        };

        byte[] pixels = RenderSkeleton(
            skeleton,
            CreateInspectionCamera());
        PixelBounds bounds = FindPixelBounds(
            pixels,
            IsGoldPivotPixel);

        Assert.True(
            bounds.Count >= 2,
            "The visible helper child should retain a gold locator.");
        Assert.True(
            bounds.MinimumX > Width / 2,
            $"The hidden parent leaked into the helper overlay: {bounds.MinimumX}..{bounds.MaximumX}.");
        Assert.InRange(
            bounds.MaximumX - bounds.MinimumX,
            0,
            6);
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpAnimatedPropPivotRigUsesGoldWireframeWithoutDeformDiamonds()
    {
        var skeleton = new SkeletonRenderData(
            [
                RoleBone(
                    "animated-prop-root",
                    -1,
                    new Vector3(0.0f, -0.55f, 0.0f),
                    BoneRenderRole.Prop),
                RoleBone(
                    "left-pivot",
                    0,
                    new Vector3(-0.65f, 0.18f, 0.0f),
                    BoneRenderRole.Helper),
                RoleBone(
                    "right-pivot",
                    0,
                    new Vector3(0.65f, 0.18f, 0.0f),
                    BoneRenderRole.Helper),
            ],
            Matrix4x4.Identity)
        {
            ShowDeformBones = false,
            ShowHelpers = true,
            ShowCameraHelpers = false,
            ShowProps = true,
        };

        byte[] pixels = RenderSkeleton(
            skeleton,
            CreateInspectionCamera());
        PixelBounds bounds = FindPixelBounds(
            pixels,
            IsGoldPivotPixel);

        Assert.True(
            bounds.Count >= 12,
            $"Expected a readable gold pivot rig, but found only {bounds.Count} gold pixels.");
        Assert.True(
            bounds.MaximumX - bounds.MinimumX >= 30,
            $"Expected a door-like pivot wedge, but its gold bounds were only {bounds.MinimumX}..{bounds.MaximumX}.");
        Assert.Equal(0, CountWhiteDeformPixels(pixels));
        Assert.Equal(0, CountOrangeCameraPixels(pixels));
    }

    private static byte[] RenderSkeleton(
        SkeletonRenderData skeleton,
        RenderCamera camera)
    {
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
            using ID3D11Texture2D color = device.CreateTexture2D(
                CreateTextureDescription(
                    Format.B8G8R8A8_UNorm,
                    ResourceUsage.Default,
                    BindFlags.RenderTarget,
                    CpuAccessFlags.None));
            using ID3D11RenderTargetView renderTarget =
                device.CreateRenderTargetView(color);
            using ID3D11Texture2D depth = device.CreateTexture2D(
                CreateTextureDescription(
                    Format.D24_UNorm_S8_UInt,
                    ResourceUsage.Default,
                    BindFlags.DepthStencil,
                    CpuAccessFlags.None));
            using ID3D11DepthStencilView depthStencil =
                device.CreateDepthStencilView(depth);
            using ID3D11Texture2D staging = device.CreateTexture2D(
                CreateTextureDescription(
                    Format.B8G8R8A8_UNorm,
                    ResourceUsage.Staging,
                    BindFlags.None,
                    CpuAccessFlags.Read));
            context.OMSetRenderTargets(renderTarget, depthStencil);
            ClearRoleTarget(context, renderTarget, depthStencil);
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                static _ => { });
            using var pass = new SkeletonRenderPass();
            pass.Render(
                in renderContext,
                RenderFrameSnapshot.Empty() with
                {
                    Camera = camera,
                    Skeleton = skeleton,
                });
            byte[] pixels = ReadBack(context, color, staging);
            Assert.True(device.DeviceRemovedReason.Success);
            return pixels;
        }
    }

    private static RenderCamera CreateInspectionCamera() =>
        new(
            new Vector3(0.0f, 0.0f, 3.0f),
            Vector3.Zero,
            Vector3.UnitY,
            55.0f,
            0.02f,
            100.0f);

    private static BoneRenderData RoleBone(
        string name,
        int parentIndex,
        Vector3 position,
        BoneRenderRole role) =>
        new(
            name,
            parentIndex,
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(position),
            false)
        {
            Role = role,
        };

    private static void ClearRoleTarget(
        ID3D11DeviceContext context,
        ID3D11RenderTargetView renderTarget,
        ID3D11DepthStencilView depthStencil)
    {
        context.ClearRenderTargetView(
            renderTarget,
            new Color4(0.03f, 0.05f, 0.09f, 1.0f));
        context.ClearDepthStencilView(
            depthStencil,
            DepthStencilClearFlags.Depth,
            1.0f,
            0);
    }

    private static Texture2DDescription CreateTextureDescription(
        Format format,
        ResourceUsage usage,
        BindFlags bindFlags,
        CpuAccessFlags cpuAccessFlags) =>
        new()
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuAccessFlags,
            MiscFlags = ResourceOptionFlags.None,
        };

    private static byte[] ReadBack(
        ID3D11DeviceContext context,
        ID3D11Texture2D source,
        ID3D11Texture2D staging)
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
            byte[] pixels = new byte[Width * Height * 4];
            int rowBytes = Width * 4;
            for (int row = 0; row < Height; row++)
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

    private static int CountWhiteDeformPixels(byte[] pixels)
    {
        return CountPixels(pixels, IsWhiteDeformPixel);
    }

    private static bool IsWhiteDeformPixel(
        byte blue,
        byte green,
        byte red) =>
        blue > 200 && green > 200 && red > 200;

    private static int CountGoldPivotPixels(byte[] pixels)
    {
        return CountPixels(pixels, IsGoldPivotPixel);
    }

    private static bool IsGoldPivotPixel(
        byte blue,
        byte green,
        byte red) =>
        red > 200 && green > 180 && blue < 100;

    private static int CountOrangeCameraPixels(byte[] pixels)
    {
        return CountPixels(
            pixels,
            static (blue, green, red) =>
                red > 190
                && green is > 100 and < 180
                 && blue < 100);
    }

    private static int CountGreenJointPixels(byte[] pixels)
    {
        return CountPixels(
            pixels,
            static (blue, green, red) =>
                green > 190
                && red < 170
                && blue < 180);
    }

    private static int CountPixels(
        byte[] pixels,
        Func<byte, byte, byte, bool> predicate)
    {
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte blue = pixels[index];
            byte green = pixels[index + 1];
            byte red = pixels[index + 2];
            if (predicate(blue, green, red))
            {
                count++;
            }
        }

        return count;
    }

    private static PixelBounds FindPixelBounds(
        byte[] pixels,
        Func<byte, byte, byte, bool> predicate)
    {
        int count = 0;
        int minimumX = Width;
        int maximumX = -1;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte blue = pixels[index];
            byte green = pixels[index + 1];
            byte red = pixels[index + 2];
            if (predicate(blue, green, red))
            {
                count++;
                int x = (index / 4) % Width;
                minimumX = Math.Min(minimumX, x);
                maximumX = Math.Max(maximumX, x);
            }
        }

        return new PixelBounds(count, minimumX, maximumX);
    }

    private readonly record struct PixelBounds(
        int Count,
        int MinimumX,
        int MaximumX);
}
