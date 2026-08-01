using System.Numerics;
using System.Runtime.InteropServices;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererNormalMatrixWarpTests
{
    private const int Width = 96;
    private const int Height = 96;

    [Fact]
    [Trait("Category", "Renderer")]
    public void StaticAndCompactSkinnedNonUniformNormalsMatchCpuReference()
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

            byte staticRed = RenderAndReadCenterRed(
                context,
                color,
                depth,
                staging,
                pass,
                in renderContext,
                CreateCase(isSkinned: false));
            byte skinnedRed = RenderAndReadCenterRed(
                context,
                color,
                depth,
                staging,
                pass,
                in renderContext,
                CreateCase(isSkinned: true));

            (RenderFrameSnapshot staticFrame, MeshRenderData staticMesh) =
                CreateCase(isSkinned: false);
            Vector3 staticCpuNormal =
                CpuMeshDeformationEvaluator.Evaluate(
                    staticMesh,
                    staticFrame.Skeleton,
                    [])[0].Normal;
            byte staticExpectedRed =
                CalculateLitRed(staticCpuNormal);

            (RenderFrameSnapshot frame, MeshRenderData mesh) =
                CreateCase(isSkinned: true);
            Vector3 cpuNormal =
                CpuMeshDeformationEvaluator.Evaluate(
                    mesh,
                    frame.Skeleton,
                    [])[0].Normal;
            Vector3 expectedNormal = Vector3.Normalize(
                Vector3.TransformNormal(
                    new Vector3(
                        (0.25f / 0.8f) + (0.75f / 1.6f),
                        (0.25f / 1.2f) + (0.75f / 0.4f),
                        (0.25f / 1.6f) + (0.75f / 0.8f)),
                    Matrix4x4.CreateRotationZ(0.25f)));
            Vector3 positionMatrixNormal = Vector3.Normalize(
                Vector3.TransformNormal(
                    new Vector3(
                        (0.25f * 0.8f) + (0.75f * 1.6f),
                        (0.25f * 1.2f) + (0.75f * 0.4f),
                        (0.25f * 1.6f) + (0.75f * 0.8f)),
                    Matrix4x4.CreateRotationZ(0.25f)));
            byte expectedRed = CalculateLitRed(expectedNormal);
            byte positionMatrixRed =
                CalculateLitRed(positionMatrixNormal);

            AssertVector(expectedNormal, cpuNormal);
            Assert.InRange(
                Math.Abs(staticRed - staticExpectedRed),
                0,
                2);
            Assert.InRange(
                Math.Abs(skinnedRed - expectedRed),
                0,
                2);
            Assert.True(
                Math.Abs(skinnedRed - expectedRed) <
                Math.Abs(skinnedRed - positionMatrixRed),
                $"GPU red={skinnedRed}, inverse-transpose={expectedRed}, position-matrix={positionMatrixRed}.");
        }
    }

    private static byte RenderAndReadCenterRed(
        ID3D11DeviceContext context,
        ID3D11Texture2D color,
        ID3D11Texture2D depth,
        ID3D11Texture2D staging,
        GpuSkinnedMeshRenderPass pass,
        in D3D11RenderFrameContext renderContext,
        (RenderFrameSnapshot Frame, MeshRenderData Mesh) renderCase)
    {
        context.ClearRenderTargetView(
            renderContext.RenderTargetView,
            new Color4(0.03f, 0.05f, 0.09f, 1.0f));
        context.ClearDepthStencilView(
            renderContext.DepthStencilView,
            DepthStencilClearFlags.Depth,
            1.0f,
            0);
        pass.Render(in renderContext, renderCase.Frame);
        context.CopyResource(staging, color);
        context.Flush();
        context.Map(
            staging,
            0,
            MapMode.Read,
            Vortice.Direct3D11.MapFlags.None,
            out MappedSubresource mapped).CheckError();
        try
        {
            IntPtr center = IntPtr.Add(
                mapped.DataPointer,
                checked((int)(
                    (Height / 2 * mapped.RowPitch) +
                    (Width / 2 * 4) +
                    2)));
            return Marshal.ReadByte(center);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static (RenderFrameSnapshot Frame, MeshRenderData Mesh)
        CreateCase(bool isSkinned)
    {
        Vector3 sourceNormal =
            Vector3.Normalize(new Vector3(1.0f, 1.0f, 1.0f));
        Vector4 boneWeights = isSkinned
            ? new Vector4(0.25f, 0.75f, 0.0f, 0.0f)
            : Vector4.Zero;
        Vector4 boneIndices = isSkinned
            ? new Vector4(0.0f, 1.0f, 0.0f, 0.0f)
            : Vector4.Zero;
        MeshVertex[] vertices =
        [
            new(
                new Vector3(-0.6f, -0.5f, 0.0f),
                sourceNormal,
                Vector2.Zero,
                boneWeights,
                boneIndices),
            new(
                new Vector3(0.6f, -0.5f, 0.0f),
                sourceNormal,
                Vector2.UnitX,
                boneWeights,
                boneIndices),
            new(
                new Vector3(0.0f, 0.5f, 0.0f),
                sourceNormal,
                Vector2.UnitY,
                boneWeights,
                boneIndices),
        ];
        Matrix4x4 rotation =
            Matrix4x4.CreateRotationZ(0.25f);
        Matrix4x4 scale =
            Matrix4x4.CreateScale(1.6f, 0.4f, 0.8f)
            * rotation;
        Matrix4x4 secondaryScale =
            Matrix4x4.CreateScale(0.8f, 1.2f, 1.6f)
            * rotation;
        MeshRenderData mesh = new(
            isSkinned
                ? "normal-matrix-skinned"
                : "normal-matrix-static",
            vertices,
            new uint[] { 0, 1, 2 },
            isSkinned ? Matrix4x4.Identity : scale,
            isSkinned
                ? new Matrix4x4[]
                {
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                }
                : ReadOnlyMemory<Matrix4x4>.Empty,
            isSkinned)
        {
            Tint = Vector4.One,
            SkinBoneIndices = isSkinned
                ? new int[] { 257, 299 }
                : ReadOnlyMemory<int>.Empty,
        };
        SkeletonRenderData? skeleton = isSkinned
            ? new SkeletonRenderData(
                Enumerable.Range(0, 300)
                    .Select(index => new BoneRenderData(
                        $"bone_{index}",
                        -1,
                        Matrix4x4.Identity,
                        index switch
                        {
                            257 => secondaryScale,
                            299 => scale,
                            _ => Matrix4x4.Identity,
                        },
                        false))
                    .ToArray(),
                Matrix4x4.Identity)
            : null;
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
            []);
        return (frame, mesh);
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

    private static byte ToUnormByte(float value) =>
        checked((byte)Math.Clamp(
            (int)MathF.Round(value * byte.MaxValue),
            byte.MinValue,
            byte.MaxValue));

    private static byte CalculateLitRed(Vector3 normal)
    {
        Vector3 lightDirection =
            Vector3.Normalize(new Vector3(0.38f, 0.78f, -0.50f));
        return ToUnormByte(
            0.68f +
            (0.32f * MathF.Max(
                Vector3.Dot(normal, lightDirection),
                0.0f)));
    }

    private static void AssertVector(
        Vector3 expected,
        Vector3 actual)
    {
        Assert.InRange(
            Vector3.Distance(expected, actual),
            0.0f,
            1.0e-5f);
    }
}
