using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using ReAnimated.Renderer.D3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererAuthoringOverlayTests
{
    private const int Width = 128;
    private const int Height = 128;

    [Fact]
    public void AuthoringOverlaysDefaultOffAndSceneBufferCopiesTrail()
    {
        RenderFrameSnapshot empty = RenderFrameSnapshot.Empty();
        Assert.Empty(
            AuthoringOverlayGeometryBuilder.BuildLines(empty));
        Assert.False(
            empty.AuthoringOverlays.Options.ShowRootMotionTrail);
        Assert.False(
            empty.AuthoringOverlays.Options.ShowDeformedBounds);
        Assert.False(
            empty.AuthoringOverlays.Options.ShowBoneLocalAxes);
        Assert.False(
            empty.AuthoringOverlays.Options.HighlightSelectedMeshes);

        Vector3[] mutablePositions =
        [
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.One,
        ];
        RenderSceneBuffer buffer = new();
        buffer.SetAuthoringOverlays(
            new RenderAuthoringOverlayState(
                new RenderAuthoringOverlayOptions(
                    ShowRootMotionTrail: true),
                new RootMotionTrailRenderData(
                    mutablePositions,
                    currentSampleIndex: 1)));
        mutablePositions[1] = new Vector3(99.0f);

        RenderFrameSnapshot captured =
            buffer.Capture(RenderCamera.Default);
        Assert.Equal(
            Vector3.UnitX,
            captured.AuthoringOverlays
                .RootMotionTrail!
                .WorldPositions
                .AsSpan()[1]);
        IReadOnlyList<AuthoringOverlayLine> lines =
            AuthoringOverlayGeometryBuilder.BuildLines(captured);
        Assert.Equal(
            2,
            lines.Count(line =>
                line.Primitive ==
                AuthoringOverlayPrimitive.RootMotionTrail));
        Assert.Equal(
            3,
            lines.Count(line =>
                line.Primitive ==
                AuthoringOverlayPrimitive.CurrentRootMarker));
    }

    [Fact]
    public void SceneBufferRetainsOwnedImmutableTrailAcrossPublications()
    {
        ImmutableArray<Vector3> positions =
            [Vector3.Zero, Vector3.UnitX, Vector3.One];
        var state = new RenderAuthoringOverlayState(
            new RenderAuthoringOverlayOptions(
                ShowRootMotionTrail: true),
            new RootMotionTrailRenderData(
                positions,
                currentSampleIndex: 1));
        RenderSceneBuffer buffer = new();

        buffer.SetAuthoringOverlays(state);
        RenderFrameSnapshot first =
            buffer.Capture(RenderCamera.Default);
        buffer.SetAuthoringOverlays(state);
        RenderFrameSnapshot second =
            buffer.Capture(RenderCamera.Default);

        Assert.True(
            positions.Equals(
                first.AuthoringOverlays
                    .RootMotionTrail!
                    .WorldPositions));
        Assert.True(
            positions.Equals(
                second.AuthoringOverlays
                    .RootMotionTrail!
                    .WorldPositions));
    }

    [Fact]
    public void RenderTraversalConsumesPublishedBoundsWithoutMeshDeformation()
    {
        var measured = new DeformedMeshBoundsRenderData(
            "precomputed",
            new Vector3(-2.0f, -1.0f, 3.0f),
            new Vector3(4.0f, 5.0f, 6.0f),
            IsSelected: true);
        RenderFrameSnapshot frame =
            RenderFrameSnapshot.Empty() with
            {
                Meshes = [],
                AuthoringOverlays =
                    new RenderAuthoringOverlayState(
                        new RenderAuthoringOverlayOptions(
                            ShowDeformedBounds: true,
                            HighlightSelectedMeshes: true),
                        null,
                        [measured]),
            };

        AuthoringOverlayLine[] lines =
            AuthoringOverlayGeometryBuilder
                .BuildLines(frame)
                .ToArray();

        Assert.Equal(24, lines.Length);
        Assert.Contains(
            lines,
            static line =>
                line.Primitive ==
                AuthoringOverlayPrimitive.DeformedBounds);
        Assert.Contains(
            lines,
            static line =>
                line.Primitive ==
                AuthoringOverlayPrimitive.SelectedMeshHighlight);
        Assert.Contains(
            lines.SelectMany(static line =>
                new[] { line.Start, line.End }),
            point => point == measured.Minimum);
        Assert.Contains(
            lines.SelectMany(static line =>
                new[] { line.Start, line.End }),
            point => point == measured.Maximum);
    }

    [Fact]
    public void SelectedMeshTintRequiresHighlightToggle()
    {
        MeshRenderData selected = CreateDoorMesh(Vector3.Zero);
        var disabled = new RenderAuthoringOverlayState(
            RenderAuthoringOverlayOptions.Disabled,
            null);
        var enabled = new RenderAuthoringOverlayState(
            new RenderAuthoringOverlayOptions(
                HighlightSelectedMeshes: true),
            null);

        Assert.False(
            MeshSelectionHighlightPolicy.ShouldRenderOutline(
                selected,
                disabled));
        Assert.True(
            MeshSelectionHighlightPolicy.ShouldRenderOutline(
                selected,
                enabled));
        Assert.False(
            MeshSelectionHighlightPolicy.ShouldRenderOutline(
                selected with { IsSelected = false },
                enabled));
        Assert.Equal(
            selected.Tint,
            MeshSelectionHighlightPolicy.ResolveTint(
                selected,
                disabled));
        Assert.NotEqual(
            selected.Tint,
            MeshSelectionHighlightPolicy.ResolveTint(
                selected,
                enabled));
        Assert.Equal(
            selected.Tint,
            MeshSelectionHighlightPolicy.ResolveTint(
                selected with { IsSelected = false },
                enabled));
    }

    [Fact]
    public void CurrentSkinnedMorphBoundsDriveSelectedMeshHighlight()
    {
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.CreateTranslation(5.0f, 0.0f, 0.0f),
                    false),
            ],
            Matrix4x4.CreateTranslation(0.0f, 3.0f, 0.0f));
        MeshRenderData mesh = new(
            "selected-skinned-morph",
            new MeshVertex[]
            {
                WeightedVertex(new Vector3(-1.0f, -1.0f, 0.0f)),
                WeightedVertex(new Vector3(1.0f, -1.0f, 0.0f)),
                WeightedVertex(new Vector3(0.0f, 1.0f, 0.0f)),
            },
            new uint[] { 0, 2, 1 },
            Matrix4x4.CreateTranslation(0.0f, 0.0f, 2.0f),
            new Matrix4x4[] { Matrix4x4.Identity },
            true)
        {
            IsSelected = true,
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "raise",
                    new Vector3[]
                    {
                        Vector3.Zero,
                        Vector3.Zero,
                        new Vector3(0.0f, 2.0f, 0.0f),
                    },
                    ReadOnlyMemory<Vector3>.Empty),
            ],
        };
        MorphWeight[] weights = [new("raise", 0.5f)];

        Assert.True(
            CpuMeshDeformationEvaluator.TryMeasureBounds(
                mesh,
                skeleton,
                weights,
                out CpuDeformedBounds bounds,
                out string? error),
            error);
        Assert.Equal(new Vector3(4.0f, 2.0f, 2.0f), bounds.Minimum);
        Assert.Equal(new Vector3(6.0f, 5.0f, 2.0f), bounds.Maximum);

        RenderFrameSnapshot frame =
            RenderFrameSnapshot.Empty() with
            {
                Meshes = [mesh],
                Skeleton = skeleton,
                MorphWeights = weights,
                AuthoringOverlays =
                    new RenderAuthoringOverlayState(
                        new RenderAuthoringOverlayOptions(
                            HighlightSelectedMeshes: true),
                        null),
            };
        frame = WithPrecomputedBounds(frame);
        IReadOnlyList<AuthoringOverlayLine> lines =
            AuthoringOverlayGeometryBuilder.BuildLines(frame);

        Assert.Equal(12, lines.Count);
        Assert.All(
            lines,
            line => Assert.Equal(
                AuthoringOverlayPrimitive.SelectedMeshHighlight,
                line.Primitive));
        Assert.Contains(
            lines.SelectMany(line => new[] { line.Start, line.End }),
            point => point == bounds.Minimum);
        Assert.Contains(
            lines.SelectMany(line => new[] { line.Start, line.End }),
            point => point == bounds.Maximum);
    }

    [Fact]
    public void SelectedRetailModelPartsShareOneUnionHighlight()
    {
        MeshVertex[] vertices =
        [
            StaticVertex(Vector3.Zero),
            StaticVertex(Vector3.UnitX),
            StaticVertex(Vector3.UnitY),
        ];
        var first = new MeshRenderData(
            "part-0",
            vertices,
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            false)
        {
            IsSelected = true,
        };
        MeshRenderData second = first with
        {
            Id = "part-1",
            LocalToWorld =
                Matrix4x4.CreateTranslation(10.0f, 2.0f, 3.0f),
        };
        RenderFrameSnapshot frame =
            RenderFrameSnapshot.Empty() with
            {
                Meshes = [first, second],
                AuthoringOverlays =
                    new RenderAuthoringOverlayState(
                        new RenderAuthoringOverlayOptions(
                            ShowDeformedBounds: true,
                            HighlightSelectedMeshes: true),
                        null),
            };
        frame = WithPrecomputedBounds(frame);

        AuthoringOverlayLine[] lines =
            AuthoringOverlayGeometryBuilder
                .BuildLines(frame)
                .ToArray();
        AuthoringOverlayLine[] highlight = lines
            .Where(static line =>
                line.Primitive ==
                    AuthoringOverlayPrimitive.SelectedMeshHighlight)
            .ToArray();

        Assert.Equal(36, lines.Length);
        Assert.Equal(12, highlight.Length);
        Assert.Contains(
            highlight.SelectMany(static line =>
                new[] { line.Start, line.End }),
            static point => point == Vector3.Zero);
        Assert.Contains(
            highlight.SelectMany(static line =>
                new[] { line.Start, line.End }),
            static point =>
                point == new Vector3(11.0f, 3.0f, 3.0f));
    }

    [Fact]
    public void LargeFinitePropRigProducesNormalizedLocalAxes()
    {
        Matrix4x4 propWorld =
            Matrix4x4.CreateScale(2.0f, 3.0f, 4.0f)
            * Matrix4x4.CreateRotationZ(MathF.PI * 0.5f)
            * Matrix4x4.CreateTranslation(10.0f, 20.0f, 30.0f);
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "door_prop",
                    -1,
                    Matrix4x4.Identity,
                    propWorld,
                    false)
                {
                    Role = BoneRenderRole.Prop,
                },
                new BoneRenderData(
                    "hidden_helper",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    false)
                {
                    Role = BoneRenderRole.Helper,
                },
            ],
            Matrix4x4.CreateScale(100.0f)
            * Matrix4x4.CreateTranslation(
                100_000.0f,
                -200_000.0f,
                300_000.0f))
        {
            ShowProps = true,
            ShowHelpers = false,
        };
        RenderFrameSnapshot frame =
            RenderFrameSnapshot.Empty() with
            {
                Camera = RenderCamera.Default,
                Skeleton = skeleton,
                AuthoringOverlays =
                    new RenderAuthoringOverlayState(
                        new RenderAuthoringOverlayOptions(
                            ShowBoneLocalAxes: true),
                        null),
            };

        AuthoringOverlayLine[] axes =
            AuthoringOverlayGeometryBuilder
                .BuildLines(frame)
                .ToArray();

        Assert.Equal(3, axes.Length);
        Assert.All(
            axes,
            axis =>
            {
                Assert.True(IsFinite(axis.Start));
                Assert.True(IsFinite(axis.End));
                Assert.InRange(
                    Vector3.Distance(axis.Start, axis.End),
                    0.33f,
                    0.37f);
            });
        Assert.DoesNotContain(
            axes,
            axis =>
                axis.Start == new Vector3(
                    100_000.0f,
                    -200_000.0f,
                    300_000.0f));
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpSelectedSkinnedMorphMeshUsesToggleableSilhouetteOutline()
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

            MeshRenderData mesh = CreateSelectedSkinnedMorphMesh();
            SkeletonRenderData skeleton = new(
                [
                    new BoneRenderData(
                        "root",
                        -1,
                        Matrix4x4.Identity,
                        Matrix4x4.CreateTranslation(0.15f, 0.05f, 0.0f),
                        false),
                ],
                Matrix4x4.Identity);
            RenderCamera camera = new(
                new Vector3(0.0f, 0.0f, 4.0f),
                Vector3.Zero,
                Vector3.UnitY,
                52.0f,
                0.02f,
                100.0f);
            RenderFrameSnapshot disabled =
                RenderFrameSnapshot.Empty() with
                {
                    Camera = camera,
                    Meshes = [mesh],
                    Skeleton = skeleton,
                    MorphWeights = [new MorphWeight("asymmetric", 0.65f)],
                    AuthoringOverlays =
                        new RenderAuthoringOverlayState(
                            RenderAuthoringOverlayOptions.Disabled,
                            null),
                };
            RenderFrameSnapshot enabled = disabled with
            {
                AuthoringOverlays =
                    new RenderAuthoringOverlayState(
                        new RenderAuthoringOverlayOptions(
                            HighlightSelectedMeshes: true),
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

            using GpuSkinnedMeshRenderPass meshPass = new();
            ClearTargets(context, renderTarget, depthStencil);
            meshPass.Render(in renderContext, disabled);
            byte[] disabledPixels = ReadBack(context, color, staging);

            ClearTargets(context, renderTarget, depthStencil);
            meshPass.Render(in renderContext, enabled);
            byte[] enabledPixels = ReadBack(context, color, staging);

            Assert.Equal(0, CountOutlinePixels(disabledPixels));
            Assert.True(
                CountOutlinePixels(enabledPixels) >= 20,
                "The selected skinned/morphed draw did not produce a visible silhouette outline.");
            Assert.True(
                CountChangedPixels(disabledPixels, enabledPixels) >= 50,
                "Enabling the selected-mesh outline did not produce a visible WARP readback delta.");
            Assert.True(device.DeviceRemovedReason.Success);
        }
    }

    [Fact]
    [Trait("Category", "Renderer")]
    public void WarpAuthoringOverlayStaysVisibleOnFiniteDoorScaleScene()
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

            Vector3 sceneCenter = new(1_000.0f, -2_000.0f, 3_000.0f);
            MeshRenderData door = CreateDoorMesh(sceneCenter);
            SkeletonRenderData skeleton = new(
                [
                    new BoneRenderData(
                        "door_prop",
                        -1,
                        Matrix4x4.Identity,
                        Matrix4x4.Identity,
                        false)
                    {
                        Role = BoneRenderRole.Prop,
                    },
                ],
                Matrix4x4.CreateScale(80.0f)
                * Matrix4x4.CreateTranslation(sceneCenter))
            {
                ShowProps = true,
            };
            RenderCamera camera = new(
                sceneCenter + new Vector3(2.8f, 1.8f, 4.5f),
                sceneCenter,
                Vector3.UnitY,
                55.0f,
                0.02f,
                100.0f);
            RenderFrameSnapshot frame =
                RenderFrameSnapshot.Empty() with
                {
                    Camera = camera,
                    Meshes = [door],
                    Skeleton = skeleton,
                    AuthoringOverlays =
                        new RenderAuthoringOverlayState(
                            new RenderAuthoringOverlayOptions(
                                ShowRootMotionTrail: true,
                                ShowDeformedBounds: true,
                                ShowBoneLocalAxes: true,
                                HighlightSelectedMeshes: true),
                            new RootMotionTrailRenderData(
                                new Vector3[]
                                {
                                    sceneCenter + new Vector3(-0.4f, 0.0f, 0.0f),
                                    sceneCenter,
                                    sceneCenter + new Vector3(0.4f, 0.0f, 0.2f),
                                },
                                currentSampleIndex: 1)),
                };
            frame = WithPrecomputedBounds(frame);
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
            byte[] meshPixels = ReadBack(context, color, staging);
            using AuthoringOverlayRenderPass overlayPass = new();
            overlayPass.Render(in renderContext, frame);
            byte[] overlayPixels = ReadBack(context, color, staging);

            Assert.True(
                CountChangedPixels(meshPixels, overlayPixels) >= 20,
                "The authoring overlay did not produce a visible WARP readback delta.");
            Assert.True(
                CountGoldPixels(overlayPixels) >= 5,
                "The selected door mesh did not receive a visible bounds highlight.");
            Assert.True(
                CountAxisPixels(overlayPixels) >= 3,
                "The finite prop-scale skeleton did not produce visible local axes.");
            Assert.True(device.DeviceRemovedReason.Success);
        }
    }

    private static MeshVertex WeightedVertex(Vector3 position) =>
        new(
            position,
            Vector3.UnitZ,
            Vector2.Zero,
            Vector4.UnitX,
            Vector4.Zero);

    private static MeshRenderData CreateSelectedSkinnedMorphMesh()
    {
        Vector3[] positions =
        [
            new(0.0f, 0.85f, 0.0f),
            new(0.0f, -0.85f, 0.0f),
            new(0.85f, 0.0f, 0.0f),
            new(0.0f, 0.0f, 0.85f),
            new(-0.85f, 0.0f, 0.0f),
            new(0.0f, 0.0f, -0.85f),
        ];
        MeshVertex[] vertices = positions
            .Select(static position =>
                new MeshVertex(
                    position,
                    Vector3.Normalize(position),
                    Vector2.Zero,
                    Vector4.UnitX,
                    Vector4.Zero))
            .ToArray();
        return new MeshRenderData(
            "selected-skinned-morph-outline",
            vertices,
            new uint[]
            {
                0, 3, 2,
                0, 4, 3,
                0, 5, 4,
                0, 2, 5,
                1, 2, 3,
                1, 3, 4,
                1, 4, 5,
                1, 5, 2,
            },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            true)
        {
            IsSelected = true,
            Tint = new Vector4(0.28f, 0.34f, 0.42f, 1.0f),
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "asymmetric",
                    new Vector3[]
                    {
                        new(0.0f, 0.08f, 0.0f),
                        Vector3.Zero,
                        new(0.12f, 0.0f, 0.0f),
                        Vector3.Zero,
                        Vector3.Zero,
                        Vector3.Zero,
                    },
                    ReadOnlyMemory<Vector3>.Empty),
            ],
        };
    }

    private static RenderFrameSnapshot WithPrecomputedBounds(
        RenderFrameSnapshot frame) =>
        frame with
        {
            AuthoringOverlays = frame.AuthoringOverlays with
            {
                DeformedMeshBounds =
                    AuthoringOverlayBoundsPrecomputer.Measure(frame),
            },
        };

    private static MeshRenderData CreateDoorMesh(Vector3 sceneCenter)
    {
        MeshVertex[] vertices =
        [
            StaticVertex(new Vector3(-0.75f, -1.0f, 0.0f)),
            StaticVertex(new Vector3(0.75f, -1.0f, 0.0f)),
            StaticVertex(new Vector3(0.75f, 1.0f, 0.0f)),
            StaticVertex(new Vector3(-0.75f, 1.0f, 0.0f)),
        ];
        return new MeshRenderData(
            "finite-door",
            vertices,
            new uint[] { 0, 2, 1, 0, 3, 2 },
            Matrix4x4.CreateTranslation(sceneCenter),
            ReadOnlyMemory<Matrix4x4>.Empty,
            false)
        {
            IsSelected = true,
            Tint = new Vector4(0.18f, 0.20f, 0.23f, 1.0f),
        };
    }

    private static MeshVertex StaticVertex(Vector3 position) =>
        new(
            position,
            -Vector3.UnitZ,
            Vector2.Zero,
            Vector4.Zero,
            Vector4.Zero);

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

    private static int CountChangedPixels(
        byte[] before,
        byte[] after)
    {
        int count = 0;
        for (int index = 0; index < before.Length; index += 4)
        {
            if (before[index] != after[index] ||
                before[index + 1] != after[index + 1] ||
                before[index + 2] != after[index + 2])
            {
                count++;
            }
        }

        return count;
    }

    private static int CountGoldPixels(byte[] pixels) =>
        CountPixels(
            pixels,
            static (blue, green, red) =>
                red > 210 && green > 145 && blue < 100);

    private static int CountOutlinePixels(byte[] pixels) =>
        CountPixels(
            pixels,
            static (blue, green, red) =>
                red >= 245
                && green is >= 145 and <= 170
                && blue <= 30);

    private static int CountAxisPixels(byte[] pixels) =>
        CountPixels(
            pixels,
            static (blue, green, red) =>
                (red > 190 && green < 100 && blue < 100)
                || (green > 180 && red < 110 && blue < 120)
                || (blue > 190 && red < 110 && green < 170));

    private static int CountPixels(
        byte[] pixels,
        Func<byte, byte, byte, bool> predicate)
    {
        int count = 0;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (predicate(
                    pixels[index],
                    pixels[index + 1],
                    pixels[index + 2]))
            {
                count++;
            }
        }

        return count;
    }

    private static void ClearTargets(
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

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);
}
