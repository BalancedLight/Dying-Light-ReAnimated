using System.Numerics;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererD3D11SmokeTests
{
    [Fact]
    [Trait("Category", "Renderer")]
    public void MeshPassRebuildsCounterClockwiseRasterizerStateForNewDevice()
    {
        using var first = new WarpRenderHarness();
        using var second = new WarpRenderHarness();
        using var meshPass = new GpuSkinnedMeshRenderPass();
        RenderFrameSnapshot frame = CreateFrame();

        first.Render(meshPass, frame, 1);
        using ID3D11RasterizerState? firstState =
            first.Context.RSGetState();
        Assert.NotNull(firstState);
        RasterizerDescription firstDescription =
            firstState.Description;
        Assert.Equal(CullMode.Back, firstDescription.CullMode);
        Assert.Equal(FillMode.Solid, firstDescription.FillMode);
        Assert.True(firstDescription.FrontCounterClockwise);
        Assert.True(firstDescription.DepthClipEnable);
        IntPtr firstStatePointer = firstState.NativePointer;

        second.Render(meshPass, frame, 2);
        using ID3D11RasterizerState? secondState =
            second.Context.RSGetState();
        Assert.NotNull(secondState);
        RasterizerDescription secondDescription =
            secondState.Description;
        Assert.True(secondDescription.FrontCounterClockwise);
        Assert.NotEqual(
            firstStatePointer,
            secondState.NativePointer);
        Assert.True(first.Device.DeviceRemovedReason.Success);
        Assert.True(second.Device.DeviceRemovedReason.Success);
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpDeviceExecutesBuiltInEditorPasses()
    {
        D3D11CreateDevice(
            null,
            DriverType.Warp,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0],
            out ID3D11Device? device,
            out _,
            out ID3D11DeviceContext? deviceContext).CheckError();
        using (device)
        using (deviceContext)
        {
            Assert.NotNull(device);
            Assert.NotNull(deviceContext);
            using ID3D11Texture2D colorTexture = device.CreateTexture2D(
                CreateTargetDescription(
                    Format.B8G8R8A8_UNorm,
                    BindFlags.RenderTarget));
            using ID3D11RenderTargetView renderTarget =
                device.CreateRenderTargetView(colorTexture);
            using ID3D11Texture2D depthTexture = device.CreateTexture2D(
                CreateTargetDescription(
                    Format.D24_UNorm_S8_UInt,
                    BindFlags.DepthStencil));
            using ID3D11DepthStencilView depthStencil =
                device.CreateDepthStencilView(depthTexture);

            deviceContext.OMSetRenderTargets(renderTarget, depthStencil);
            deviceContext.RSSetViewport(0, 0, 64, 64);
            deviceContext.ClearDepthStencilView(
                depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);

            RenderFrameSnapshot frame = CreateFrame();
            D3D11RenderFrameContext renderContext = new(
                device,
                deviceContext,
                renderTarget,
                depthStencil,
                64,
                64,
                1,
                static _ => { });
            using GpuSkinnedMeshRenderPass meshPass = new();
            using SkeletonRenderPass skeletonPass = new();
            using SelectionRenderPass selectionPass = new();
            using GizmoRenderPass gizmoPass = new();
            meshPass.Render(in renderContext, frame);
            skeletonPass.Render(in renderContext, frame);
            selectionPass.Render(in renderContext, frame);
            gizmoPass.Render(in renderContext, frame);
            deviceContext.Flush();

            Assert.True(device.DeviceRemovedReason.Success);
        }
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpDrawUsesCompactPaletteAgainstLargeSkeleton()
    {
        using var harness = new WarpRenderHarness();
        using var meshPass = new GpuSkinnedMeshRenderPass();
        RenderFrameSnapshot frame =
            CreateLargeSkeletonFrame();

        harness.Render(meshPass, frame, 1);

        Assert.Empty(harness.Diagnostics);
        Assert.True(
            harness.Device.DeviceRemovedReason.Success);
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpMeshCacheStaysBoundedAcrossRepeatedAssetSwitching()
    {
        using var harness = new WarpRenderHarness();
        using var meshPass = new GpuSkinnedMeshRenderPass();

        for (int assetIndex = 0;
             assetIndex < 256;
             assetIndex++)
        {
            harness.Render(
                meshPass,
                CreateFrame($"switch-{assetIndex:N0}"),
                assetIndex);
            Assert.Equal(1, meshPass.CachedMeshCount);
        }

        harness.Render(
            meshPass,
            RenderFrameSnapshot.Empty(),
            frameNumber: 256);

        Assert.Equal(0, meshPass.CachedMeshCount);
        RenderFrameSnapshot handsFrame =
            CreateFrame("switch-fpp-hands");
        handsFrame = handsFrame with
        {
            Meshes =
            [
                handsFrame.Meshes[0] with
                {
                    ProjectionRole =
                        MeshProjectionRole.FppHands,
                },
            ],
            FppProjectionState = new RenderFppProjectionState(
                RouteHandsMeshes: true,
                SceneAspectRatio: null,
                HandsProjection: null),
        };
        harness.Render(
            meshPass,
            handsFrame,
            frameNumber: 257);
        Assert.Equal(
            1,
            meshPass.MissingHandsProjectionMeshCount);

        harness.Render(
            meshPass,
            RenderFrameSnapshot.Empty(),
            frameNumber: 258);

        Assert.Equal(
            0,
            meshPass.MissingHandsProjectionMeshCount);
        Assert.Empty(harness.Diagnostics);
        Assert.True(
            harness.Device.DeviceRemovedReason.Success);
    }

    private static Texture2DDescription CreateTargetDescription(
        Format format,
        BindFlags bindFlags)
    {
        return new Texture2DDescription
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

    private static RenderFrameSnapshot CreateFrame(
        string meshId = "warp-smoke-triangle")
    {
        MeshVertex[] vertices =
        [
            new(
                new Vector3(-0.5f, 0.0f, 0.0f),
                Vector3.UnitZ,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.0f, 0.7f, 0.0f),
                Vector3.UnitZ,
                Vector2.UnitY,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.5f, 0.0f, 0.0f),
                Vector3.UnitZ,
                Vector2.UnitX,
                Vector4.UnitX,
                Vector4.Zero),
        ];
        MeshRenderData mesh = new(
            meshId,
            vertices,
            new uint[] { 0, 2, 1 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            true)
        {
            MorphTargets = Enumerable.Range(0, 70)
                .Select(index => new MorphTargetRenderData(
                    $"morph_{index}",
                    new Vector3[]
                    {
                        Vector3.Zero,
                        new Vector3(
                            0.0f,
                            -0.0005f * (index + 1),
                            0.0f),
                        Vector3.Zero,
                    },
                    ReadOnlyMemory<Vector3>.Empty))
                .ToArray(),
        };
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    true),
            ],
            Matrix4x4.Identity);
        GizmoRenderData[] gizmos =
        [
            new(
                GizmoKind.Axis,
                Vector3.Zero,
                Vector3.UnitY,
                Vector4.UnitY,
                1.0f),
        ];
        return new RenderFrameSnapshot(
            new Vector4(0.02f, 0.03f, 0.04f, 1.0f),
            RenderCamera.Default with
            {
                Eye = new Vector3(0.0f, 0.5f, 3.0f),
                Target = new Vector3(0.0f, 0.35f, 0.0f),
            },
            [mesh],
            skeleton,
            gizmos,
            Enumerable.Range(0, 70)
                .Select(index => new MorphWeight(
                    $"morph_{index}",
                    0.75f))
                .ToArray());
    }

    private static RenderFrameSnapshot CreateLargeSkeletonFrame()
    {
        MeshVertex[] vertices =
        [
            new(
                new Vector3(-0.5f, 0.0f, 0.0f),
                Vector3.UnitZ,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.0f, 0.7f, 0.0f),
                Vector3.UnitZ,
                Vector2.UnitY,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.5f, 0.0f, 0.0f),
                Vector3.UnitZ,
                Vector2.UnitX,
                Vector4.UnitX,
                Vector4.Zero),
        ];
        MeshRenderData mesh = new(
            "warp-large-skeleton-triangle",
            vertices,
            new uint[] { 0, 2, 1 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            true)
        {
            SkinBoneIndices = new int[] { 299 },
        };
        BoneRenderData[] bones = Enumerable.Range(0, 300)
            .Select(index => new BoneRenderData(
                $"bone_{index}",
                -1,
                Matrix4x4.Identity,
                index == 299
                    ? Matrix4x4.CreateTranslation(
                        0.1f,
                        0.0f,
                        0.0f)
                    : Matrix4x4.Identity,
                false))
            .ToArray();
        return new RenderFrameSnapshot(
            new Vector4(0.02f, 0.03f, 0.04f, 1.0f),
            RenderCamera.Default with
            {
                Eye = new Vector3(0.0f, 0.5f, 3.0f),
                Target = new Vector3(0.0f, 0.35f, 0.0f),
            },
            [mesh],
            new SkeletonRenderData(
                bones,
                Matrix4x4.Identity),
            [],
            []);
    }

    private sealed class WarpRenderHarness : IDisposable
    {
        private readonly ID3D11Texture2D _colorTexture;
        private readonly ID3D11RenderTargetView _renderTarget;
        private readonly ID3D11Texture2D _depthTexture;
        private readonly ID3D11DepthStencilView _depthStencil;

        public WarpRenderHarness()
        {
            D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0],
                out ID3D11Device? device,
                out _,
                out ID3D11DeviceContext? context).CheckError();
            Device = Assert.IsType<ID3D11Device>(device);
            Context = Assert.IsType<ID3D11DeviceContext>(context);
            _colorTexture = Device.CreateTexture2D(
                CreateTargetDescription(
                    Format.B8G8R8A8_UNorm,
                    BindFlags.RenderTarget));
            _renderTarget =
                Device.CreateRenderTargetView(_colorTexture);
            _depthTexture = Device.CreateTexture2D(
                CreateTargetDescription(
                    Format.D24_UNorm_S8_UInt,
                    BindFlags.DepthStencil));
            _depthStencil =
                Device.CreateDepthStencilView(_depthTexture);
        }

        public ID3D11Device Device { get; }

        public ID3D11DeviceContext Context { get; }

        public List<string> Diagnostics { get; } = [];

        public void Render(
            GpuSkinnedMeshRenderPass pass,
            RenderFrameSnapshot frame,
            long frameNumber)
        {
            Diagnostics.Clear();
            Context.OMSetRenderTargets(
                _renderTarget,
                _depthStencil);
            Context.ClearDepthStencilView(
                _depthStencil,
                DepthStencilClearFlags.Depth,
                1.0f,
                0);
            D3D11RenderFrameContext renderContext = new(
                Device,
                Context,
                _renderTarget,
                _depthStencil,
                64,
                64,
                frameNumber,
                Diagnostics.Add);
            pass.Render(in renderContext, frame);
            Context.Flush();
        }

        public void Dispose()
        {
            Context.ClearState();
            _depthStencil.Dispose();
            _depthTexture.Dispose();
            _renderTarget.Dispose();
            _colorTexture.Dispose();
            Context.Dispose();
            Device.Dispose();
        }
    }
}
