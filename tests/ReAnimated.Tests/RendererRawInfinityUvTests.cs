using System.Numerics;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererRawInfinityUvTests
{
    [Fact(Timeout = 30_000)]
    [Trait("Category", "Renderer")]
    public async Task WarpAcceptsTexturedTriangleWithRawHalfInfinityUv()
    {
        await Task.Yield();
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
                CreateTargetDescription(
                    Format.B8G8R8A8_UNorm,
                    BindFlags.RenderTarget));
            using ID3D11RenderTargetView renderTarget =
                device.CreateRenderTargetView(color);
            using ID3D11Texture2D depth = device.CreateTexture2D(
                CreateTargetDescription(
                    Format.D24_UNorm_S8_UInt,
                    BindFlags.DepthStencil));
            using ID3D11DepthStencilView depthStencil =
                device.CreateDepthStencilView(depth);

            MeshVertex[] vertices =
            [
                new(
                    new Vector3(-0.6f, -0.5f, 0.0f),
                    Vector3.UnitZ,
                    new Vector2(
                        float.PositiveInfinity,
                        float.PositiveInfinity),
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    new Vector3(0.6f, -0.5f, 0.0f),
                    Vector3.UnitZ,
                    new Vector2(
                        float.NegativeInfinity,
                        float.NegativeInfinity),
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    new Vector3(0.0f, 0.6f, 0.0f),
                    Vector3.UnitZ,
                    new Vector2(
                        float.PositiveInfinity,
                        float.PositiveInfinity),
                    Vector4.Zero,
                    Vector4.Zero),
            ];
            MeshRenderData mesh = new(
                "raw-half-infinity-uv",
                vertices,
                new uint[] { 0, 1, 2 },
                Matrix4x4.Identity,
                ReadOnlyMemory<Matrix4x4>.Empty,
                false)
            {
                Tint = Vector4.One,
                BaseColorTexture = new TextureRenderData(
                    "synthetic-red-bc1",
                    4,
                    4,
                    TextureRenderFormat.Bc1Unorm,
                    8,
                    new byte[]
                    {
                        0x00, 0xF8,
                        0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                    }),
            };
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
                null,
                [],
                []);
            Assert.True(
                RenderMeshValidation.TryValidate(
                    mesh,
                    frame.Skeleton,
                    out string? validationError),
                validationError);

            List<string> diagnostics = [];
            context.OMSetRenderTargets(renderTarget, depthStencil);
            context.RSSetViewport(0, 0, 64, 64);
            context.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                64,
                64,
                1,
                diagnostics.Add);
            using GpuSkinnedMeshRenderPass pass = new();

            // DL1 format 15 is SConvFloat16Vec2 in the named runtime and is
            // converted through Half2Float. The four retail controls contain
            // exact 0x7C00/0xFC00 values, so this deliberately preserves
            // infinities instead of replacing or clamping authored bytes.
            pass.Render(in renderContext, frame);
            context.Flush();

            Assert.Empty(diagnostics);
            Assert.True(device.DeviceRemovedReason.Success);
        }
    }

    private static Texture2DDescription CreateTargetDescription(
        Format format,
        BindFlags bindFlags) =>
        new()
        {
            Width = 64,
            Height = 64,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = bindFlags,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };
}
