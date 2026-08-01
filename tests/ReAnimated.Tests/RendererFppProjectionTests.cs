using System.Numerics;
using System.Runtime.InteropServices;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererFppProjectionTests
{
    private const int Width = 80;
    private const int Height = 60;

    [Fact]
    public void CapturedAspectProducesCenteredSafeViewport()
    {
        RenderCamera camera = RenderCamera.Default with
        {
            ProjectionAspectRatio = 2.0f,
        };

        RenderViewportRectangle viewport =
            RenderCameraMath.CreateSceneViewport(
                camera,
                Width,
                Height);

        Assert.Equal(0.0f, viewport.X, 4);
        Assert.Equal(10.0f, viewport.Y, 4);
        Assert.Equal(80.0f, viewport.Width, 4);
        Assert.Equal(40.0f, viewport.Height, 4);
    }

    [Fact]
    public void HorizontalHandsFovBuildsInfiniteProjection()
    {
        Matrix4x4 projection = RenderCameraMath.CreateProjection(
            new RenderProjectionParameters(
                90.0f,
                RenderProjectionFovAxis.Horizontal,
                2.0f,
                0.025f,
                RenderProjectionFarPlane.Infinite));

        Assert.Equal(1.0f, projection.M11, 5);
        Assert.Equal(2.0f, projection.M22, 5);
        Assert.Equal(-1.0f, projection.M33, 5);
        Assert.Equal(-1.0f, projection.M34, 5);
        Assert.Equal(-0.025f, projection.M43, 5);
        Assert.Equal(0.0f, projection.M44, 5);
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpSafeFrameRendersInsideD3dAirspace()
    {
        CreateDevice(
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        using (device)
        using (context)
        using (ID3D11Texture2D color = device.CreateTexture2D(
            CreateTextureDescription(
                ResourceUsage.Default,
                BindFlags.RenderTarget,
                CpuAccessFlags.None)))
        using (ID3D11RenderTargetView renderTarget =
            device.CreateRenderTargetView(color))
        using (ID3D11Texture2D depth = device.CreateTexture2D(
            CreateDepthDescription()))
        using (ID3D11DepthStencilView depthStencil =
            device.CreateDepthStencilView(depth))
        using (ID3D11Texture2D staging = device.CreateTexture2D(
            CreateTextureDescription(
                ResourceUsage.Staging,
                BindFlags.None,
                CpuAccessFlags.Read)))
        using (var pass = new FppSafeFrameRenderPass())
        {
            context.OMSetRenderTargets(renderTarget, depthStencil);
            context.ClearRenderTargetView(
                renderTarget,
                new Color4(0.02f, 0.03f, 0.04f, 1.0f));
            context.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);
            RenderFrameSnapshot frame =
                RenderFrameSnapshot.Empty() with
                {
                    FppProjectionState =
                        new RenderFppProjectionState(
                            true,
                            2.0f,
                            null),
                };
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                static _ => { });

            pass.Render(in renderContext, frame);
            byte[] pixels = ReadBack(context, color, staging);

            Assert.True(
                CountAmberPixels(pixels) >= 80,
                "The captured-aspect frame was not rendered inside the D3D target.");
            Assert.True(
                HasAmberNearRow(pixels, 10) ||
                HasAmberNearRow(pixels, 9),
                "The 2:1 safe frame did not letterbox at the expected boundary.");
        }
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void FppHandsMeshFailsClosedWithoutCapturedProjection()
    {
        CreateDevice(
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        using (device)
        using (context)
        using (ID3D11Texture2D color = device.CreateTexture2D(
            CreateTextureDescription(
                ResourceUsage.Default,
                BindFlags.RenderTarget,
                CpuAccessFlags.None)))
        using (ID3D11RenderTargetView renderTarget =
            device.CreateRenderTargetView(color))
        using (ID3D11Texture2D depth = device.CreateTexture2D(
            CreateDepthDescription()))
        using (ID3D11DepthStencilView depthStencil =
            device.CreateDepthStencilView(depth))
        using (var pass = new GpuSkinnedMeshRenderPass())
        {
            var diagnostics = new List<string>();
            MeshRenderData hands = CreateTriangle() with
            {
                ProjectionRole = MeshProjectionRole.FppHands,
            };
            RenderFrameSnapshot frame = new(
                Vector4.Zero,
                RenderCamera.Default,
                [hands],
                null,
                [],
                [])
            {
                FppProjectionState =
                    new RenderFppProjectionState(
                        true,
                        null,
                        null),
            };
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                diagnostics.Add);

            pass.Render(in renderContext, frame);

            Assert.Contains(
                diagnostics,
                message => message.Contains(
                    "no valid captured hands projection",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private static MeshRenderData CreateTriangle() =>
        new(
            "fpp-hands",
            new MeshVertex[]
            {
                new(
                    new Vector3(-0.5f, -0.5f, 0.0f),
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    new Vector3(0.0f, 0.5f, 0.0f),
                    Vector3.UnitZ,
                    Vector2.UnitY,
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    new Vector3(0.5f, -0.5f, 0.0f),
                    Vector3.UnitZ,
                    Vector2.UnitX,
                    Vector4.Zero,
                    Vector4.Zero),
            },
            new uint[] { 0, 2, 1 },
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            false);

    private static void CreateDevice(
        out ID3D11Device device,
        out ID3D11DeviceContext context)
    {
        D3D11CreateDevice(
            null,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out ID3D11Device? createdDevice,
            out _,
            out ID3D11DeviceContext? createdContext).CheckError();
        device = Assert.IsType<ID3D11Device>(createdDevice);
        context = Assert.IsType<ID3D11DeviceContext>(createdContext);
    }

    private static Texture2DDescription CreateTextureDescription(
        ResourceUsage usage,
        BindFlags bindFlags,
        CpuAccessFlags cpuAccessFlags) =>
        new()
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuAccessFlags,
            MiscFlags = ResourceOptionFlags.None,
        };

    private static Texture2DDescription CreateDepthDescription() =>
        new()
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
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
            byte[] result = new byte[Width * Height * 4];
            for (int row = 0; row < Height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(
                        mapped.DataPointer,
                        checked((int)(row * mapped.RowPitch))),
                    result,
                    row * Width * 4,
                    Width * 4);
            }

            return result;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static int CountAmberPixels(byte[] pixels)
    {
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 2] > 180 &&
                pixels[index + 1] > 110 &&
                pixels[index] < 110)
            {
                count++;
            }
        }

        return count;
    }

    private static bool HasAmberNearRow(
        byte[] pixels,
        int row)
    {
        int start = row * Width * 4;
        int end = start + (Width * 4);
        for (int index = start; index < end; index += 4)
        {
            if (pixels[index + 2] > 180 &&
                pixels[index + 1] > 110 &&
                pixels[index] < 110)
            {
                return true;
            }
        }

        return false;
    }
}
