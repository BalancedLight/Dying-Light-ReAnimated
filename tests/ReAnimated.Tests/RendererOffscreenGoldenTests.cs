using System.Numerics;
using System.Runtime.InteropServices;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit.Abstractions;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererOffscreenGoldenTests
{
    private const int Width = 96;
    private const int Height = 96;
    private readonly ITestOutputHelper _output;

    public RendererOffscreenGoldenTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpMorphAndSkinSilhouetteMatchesCpuReferenceProjection()
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

            Color4 clear = new(0.03f, 0.05f, 0.09f, 1.0f);
            context.OMSetRenderTargets(renderTarget, depthStencil);
            context.RSSetViewport(0, 0, Width, Height);
            context.ClearRenderTargetView(renderTarget, clear);
            context.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);

            (RenderFrameSnapshot frame, MeshRenderData mesh) = CreateFrame();
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                static _ => { });
            using GpuSkinnedMeshRenderPass pass = new();
            pass.Render(in renderContext, frame);
            context.Flush();

            byte[] pixels = ReadBack(context, color, staging);
            PixelBounds gpuBounds = FindChangedPixelBounds(pixels);
            byte brightestSurfaceRed = 0;
            for (int index = 2; index < pixels.Length; index += 4)
            {
                brightestSurfaceRed =
                    Math.Max(brightestSurfaceRed, pixels[index]);
            }
            CpuDeformedVertex[] cpuVertices =
                CpuMeshDeformationEvaluator.Evaluate(
                    mesh,
                    frame.Skeleton,
                    frame.MorphWeights);
            PixelBounds cpuBounds = ProjectBounds(
                cpuVertices,
                frame.Camera);

            Assert.True(
                gpuBounds.PixelCount > 200,
                $"The offscreen mesh covered only {gpuBounds.PixelCount} pixels.");
            Assert.True(
                brightestSurfaceRed >= 85,
                $"The neutral inspection light is too dark for an away-facing surface: red={brightestSurfaceRed}.");
            Assert.InRange(
                Math.Abs(gpuBounds.Left - cpuBounds.Left),
                0,
                2);
            Assert.InRange(
                Math.Abs(gpuBounds.Top - cpuBounds.Top),
                0,
                2);
            Assert.InRange(
                Math.Abs(gpuBounds.Right - cpuBounds.Right),
                0,
                2);
            Assert.InRange(
                Math.Abs(gpuBounds.Bottom - cpuBounds.Bottom),
                0,
                2);

            double cpuTriangleArea = ProjectedTriangleArea(
                cpuVertices,
                frame.Camera);
            Assert.InRange(
                gpuBounds.PixelCount / cpuTriangleArea,
                0.90,
                1.10);
        }
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpBc1BaseColorTextureReachesPixelShader()
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

            (RenderFrameSnapshot sourceFrame, MeshRenderData sourceMesh) =
                CreateFrame();
            MeshRenderData texturedMesh = sourceMesh with
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
            RenderFrameSnapshot frame = sourceFrame with
            {
                Meshes = [texturedMesh],
            };
            List<string> diagnostics = [];
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                diagnostics.Add);
            using GpuSkinnedMeshRenderPass pass = new();
            pass.Render(in renderContext, frame);
            context.Flush();

            byte[] pixels = ReadBack(context, color, staging);
            int center = ((Height / 2) * Width + Width / 2) * 4;
            byte blue = pixels[center];
            byte green = pixels[center + 1];
            byte red = pixels[center + 2];
            byte maximumBlue = 0;
            byte maximumGreen = 0;
            byte maximumRed = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                maximumBlue = Math.Max(maximumBlue, pixels[index]);
                maximumGreen = Math.Max(maximumGreen, pixels[index + 1]);
                maximumRed = Math.Max(maximumRed, pixels[index + 2]);
            }

            Assert.True(
                red > green * 3 && red > blue * 3,
                $"Expected a BC1-red center pixel, got B={blue}, G={green}, R={red}; maxima B={maximumBlue}, G={maximumGreen}, R={maximumRed}; diagnostics: {string.Join(" | ", diagnostics)}.");
        }
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpKeepsOutwardDl1WindingAndCullsItsReverse()
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

            Color4 clear = new(0.03f, 0.05f, 0.09f, 1.0f);
            context.OMSetRenderTargets(renderTarget, depthStencil);
            context.RSSetViewport(0, 0, Width, Height);
            (RenderFrameSnapshot outwardFrame, MeshRenderData outwardMesh) =
                CreateFrame();
            D3D11RenderFrameContext renderContext = new(
                device,
                context,
                renderTarget,
                depthStencil,
                Width,
                Height,
                1,
                static _ => { });
            using GpuSkinnedMeshRenderPass pass = new();

            context.ClearRenderTargetView(renderTarget, clear);
            context.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);
            pass.Render(in renderContext, outwardFrame);
            int outwardPixelCount = FindChangedPixelBounds(
                ReadBack(context, color, staging)).PixelCount;

            MeshRenderData reversedMesh = outwardMesh with
            {
                Id = "offscreen-reversed",
                Indices = new uint[] { 0, 2, 1 },
            };
            RenderFrameSnapshot reversedFrame = outwardFrame with
            {
                Meshes = [reversedMesh],
            };
            context.ClearRenderTargetView(renderTarget, clear);
            context.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);
            pass.Render(in renderContext, reversedFrame);
            int reversedPixelCount = FindChangedPixelBounds(
                ReadBack(context, color, staging)).PixelCount;
            _output.WriteLine(
                $"normal-aligned outward pixels={outwardPixelCount:N0}; reversed pixels={reversedPixelCount:N0}");

            Assert.True(
                outwardPixelCount > 200,
                $"The normal-aligned DL1 winding covered only {outwardPixelCount} pixels.");
            Assert.Equal(0, reversedPixelCount);
        }
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

    private static (RenderFrameSnapshot Frame, MeshRenderData Mesh)
        CreateFrame()
    {
        MeshVertex[] vertices =
        [
            new(
                new Vector3(-0.7f, -0.55f, 0.0f),
                Vector3.UnitZ,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.7f, -0.55f, 0.0f),
                Vector3.UnitZ,
                Vector2.UnitX,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.0f, 0.55f, 0.0f),
                Vector3.UnitZ,
                Vector2.UnitY,
                Vector4.UnitX,
                Vector4.Zero),
        ];
        MeshRenderData mesh = new(
            "offscreen-golden",
            vertices,
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            true)
        {
            Tint = new Vector4(0.78f, 0.36f, 0.18f, 1.0f),
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "raise_top",
                    new Vector3[]
                    {
                        Vector3.Zero,
                        Vector3.Zero,
                        new Vector3(0.0f, 0.35f, 0.0f),
                    },
                    ReadOnlyMemory<Vector3>.Empty),
            ],
        };
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.CreateTranslation(0.08f, 0.02f, 0.0f),
                    false),
            ],
            Matrix4x4.Identity);
        RenderCamera camera = new(
            new Vector3(0.0f, 0.0f, 3.0f),
            Vector3.Zero,
            Vector3.UnitY,
            55.0f,
            0.02f,
            100.0f);
        RenderFrameSnapshot frame = new(
            new Vector4(0.03f, 0.05f, 0.09f, 1.0f),
            camera,
            [mesh],
            skeleton,
            [],
            [new MorphWeight("raise_top", 0.75f)]);
        return (frame, mesh);
    }

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
                IntPtr rowPointer = IntPtr.Add(
                    mapped.DataPointer,
                    checked((int)(row * mapped.RowPitch)));
                Marshal.Copy(
                    rowPointer,
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

    private static PixelBounds FindChangedPixelBounds(byte[] pixels)
    {
        ReadOnlySpan<byte> clear = pixels.AsSpan(0, 4);
        int left = Width;
        int top = Height;
        int right = -1;
        int bottom = -1;
        int count = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                ReadOnlySpan<byte> pixel =
                    pixels.AsSpan((y * Width + x) * 4, 4);
                if (pixel.SequenceEqual(clear))
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                count++;
            }
        }

        return new PixelBounds(left, top, right, bottom, count);
    }

    private static PixelBounds ProjectBounds(
        CpuDeformedVertex[] vertices,
        RenderCamera camera)
    {
        Vector2[] projected = vertices
            .Select(vertex => Project(vertex.Position, camera))
            .ToArray();
        return new PixelBounds(
            (int)MathF.Floor(projected.Min(static value => value.X)),
            (int)MathF.Floor(projected.Min(static value => value.Y)),
            (int)MathF.Ceiling(projected.Max(static value => value.X)),
            (int)MathF.Ceiling(projected.Max(static value => value.Y)),
            0);
    }

    private static double ProjectedTriangleArea(
        CpuDeformedVertex[] vertices,
        RenderCamera camera)
    {
        Vector2 first = Project(vertices[0].Position, camera);
        Vector2 second = Project(vertices[1].Position, camera);
        Vector2 third = Project(vertices[2].Position, camera);
        return Math.Abs(
            (first.X * (second.Y - third.Y)
             + second.X * (third.Y - first.Y)
             + third.X * (first.Y - second.Y))
            * 0.5);
    }

    private static Vector2 Project(Vector3 position, RenderCamera camera)
    {
        Matrix4x4 viewProjection =
            RenderCameraMath.CreateViewProjection(
                camera,
                Width,
                Height);
        Vector4 clip = Vector4.Transform(
            new Vector4(position, 1.0f),
            viewProjection);
        Vector2 normalized = new(clip.X / clip.W, clip.Y / clip.W);
        return new Vector2(
            (normalized.X * 0.5f + 0.5f) * Width,
            (-normalized.Y * 0.5f + 0.5f) * Height);
    }

    private readonly record struct PixelBounds(
        int Left,
        int Top,
        int Right,
        int Bottom,
        int PixelCount);
}
