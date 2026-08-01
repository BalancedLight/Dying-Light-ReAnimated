using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ReAnimated.Renderer.D3D11;

public sealed class SkeletonRenderPass : D3D11LineRenderPassBase
{
    private const float DegenerateSegmentLengthSquared = 1.0e-12f;
    private const float DiamondRingPosition = 0.28f;
    private const float DiamondRadiusRatio = 0.13f;
    private const float MaximumDiamondRadius = 0.065f;
    private static readonly Vector4 DeformJointColor =
        new(0.40f, 0.92f, 0.44f, 1.0f);

    public SkeletonRenderPass()
        : base(renderAsOverlay: true)
    {
    }

    public override string Name => "Skeleton";

    public override RenderFeature Feature => RenderFeature.SkeletonPassHook;

    protected override void BuildVertices(
        RenderFrameSnapshot frame,
        int width,
        int height,
        List<LineRenderVertex> vertices)
    {
        if (frame.Skeleton is not { } skeleton)
        {
            return;
        }

        for (int index = 0; index < skeleton.Bones.Count; index++)
        {
            BoneRenderData bone = skeleton.Bones[index];
            if (!skeleton.IsVisible(bone))
            {
                continue;
            }

            Vector3 position = GetWorldPosition(bone, skeleton.RootTransform);
            Vector4 color = bone.IsSelected
                ? new Vector4(1.0f, 0.77f, 0.18f, 1.0f)
                : GetRoleColor(bone.Role);

            bool hasVisibleParent =
                bone.ParentIndex >= 0 &&
                bone.ParentIndex < skeleton.Bones.Count &&
                skeleton.IsVisible(
                    skeleton.Bones[bone.ParentIndex]);
            if (hasVisibleParent)
            {
                Vector3 parent = GetWorldPosition(
                    skeleton.Bones[bone.ParentIndex],
                    skeleton.RootTransform);
                AddBoneSegment(
                    vertices,
                    frame.Camera,
                    parent,
                    position,
                    color,
                    bone.Role);
            }
            else
            {
                float markerSize = GetMarkerSize(frame.Camera, position);
                AddMarker(
                    vertices,
                    position,
                    markerSize,
                    color);
                if (bone.Role == BoneRenderRole.Deform)
                {
                    AddMarker(
                        vertices,
                        position,
                        markerSize * 0.42f,
                        DeformJointColor);
                }
            }
        }
    }

    private static void AddBoneSegment(
        List<LineRenderVertex> vertices,
        RenderCamera camera,
        Vector3 start,
        Vector3 end,
        Vector4 color,
        BoneRenderRole role)
    {
        if (!IsFinite(start) || !IsFinite(end))
        {
            return;
        }

        Vector3 segment = end - start;
        float lengthSquared = segment.LengthSquared();
        float markerSize = GetMarkerSize(camera, end);
        if (!float.IsFinite(lengthSquared)
            || lengthSquared <= DegenerateSegmentLengthSquared)
        {
            AddMarker(vertices, end, markerSize, color);
            if (role == BoneRenderRole.Deform)
            {
                AddMarker(
                    vertices,
                    end,
                    markerSize * 0.42f,
                    DeformJointColor);
            }
            return;
        }

        float length = MathF.Sqrt(lengthSquared);
        AddLine(vertices, start, end, color);
        if (role != BoneRenderRole.Deform)
        {
            AddMarker(
                vertices,
                end,
                markerSize * 0.55f,
                color);
            return;
        }

        // Very short hierarchy links otherwise collapse to a sub-pixel line.
        // Keep a bounded locator instead of constructing an unstable basis.
        if (length <= markerSize * 1.5f)
        {
            AddMarker(vertices, end, markerSize, color);
            AddMarker(
                vertices,
                end,
                markerSize * 0.42f,
                DeformJointColor);
            return;
        }

        Vector3 direction = segment / length;
        if (!TryCreatePerpendicularBasis(
                direction,
                out Vector3 side,
                out Vector3 depth))
        {
            AddMarker(vertices, end, markerSize, color);
            return;
        }

        float radius = Math.Clamp(
            length * DiamondRadiusRatio,
            markerSize * 0.35f,
            MaximumDiamondRadius);
        radius = Math.Min(radius, length * 0.28f);
        Vector3 ringCenter =
            start + (direction * length * DiamondRingPosition);
        Vector3 sideOffset = side * radius;
        Vector3 depthOffset = depth * radius;
        Vector3 sidePositive = ringCenter + sideOffset;
        Vector3 sideNegative = ringCenter - sideOffset;
        Vector3 depthPositive = ringCenter + depthOffset;
        Vector3 depthNegative = ringCenter - depthOffset;

        AddLine(vertices, start, sidePositive, color);
        AddLine(vertices, start, depthPositive, color);
        AddLine(vertices, start, sideNegative, color);
        AddLine(vertices, start, depthNegative, color);
        AddLine(vertices, sidePositive, end, color);
        AddLine(vertices, depthPositive, end, color);
        AddLine(vertices, sideNegative, end, color);
        AddLine(vertices, depthNegative, end, color);
        AddLine(vertices, sidePositive, depthPositive, color);
        AddLine(vertices, depthPositive, sideNegative, color);
        AddLine(vertices, sideNegative, depthNegative, color);
        AddLine(vertices, depthNegative, sidePositive, color);
        AddMarker(
            vertices,
            end,
            markerSize * 0.42f,
            DeformJointColor);
    }

    private static bool TryCreatePerpendicularBasis(
        Vector3 direction,
        out Vector3 side,
        out Vector3 depth)
    {
        float absoluteX = MathF.Abs(direction.X);
        float absoluteY = MathF.Abs(direction.Y);
        float absoluteZ = MathF.Abs(direction.Z);
        Vector3 reference = absoluteX <= absoluteY
            && absoluteX <= absoluteZ
                ? Vector3.UnitX
                : absoluteY <= absoluteZ
                    ? Vector3.UnitY
                    : Vector3.UnitZ;
        Vector3 sideCandidate = Vector3.Cross(direction, reference);
        float sideLengthSquared = sideCandidate.LengthSquared();
        if (!float.IsFinite(sideLengthSquared)
            || sideLengthSquared <= DegenerateSegmentLengthSquared)
        {
            side = Vector3.Zero;
            depth = Vector3.Zero;
            return false;
        }

        side = sideCandidate / MathF.Sqrt(sideLengthSquared);
        depth = Vector3.Cross(direction, side);
        float depthLengthSquared = depth.LengthSquared();
        if (!float.IsFinite(depthLengthSquared)
            || depthLengthSquared <= DegenerateSegmentLengthSquared)
        {
            side = Vector3.Zero;
            depth = Vector3.Zero;
            return false;
        }

        depth /= MathF.Sqrt(depthLengthSquared);
        return IsFinite(side) && IsFinite(depth);
    }

    private static void AddMarker(
        List<LineRenderVertex> vertices,
        Vector3 position,
        float size,
        Vector4 color)
    {
        if (!IsFinite(position)
            || !float.IsFinite(size)
            || size <= 0.0f)
        {
            return;
        }

        AddLine(
            vertices,
            position - (Vector3.UnitX * size),
            position + (Vector3.UnitX * size),
            color);
        AddLine(
            vertices,
            position - (Vector3.UnitY * size),
            position + (Vector3.UnitY * size),
            color);
        AddLine(
            vertices,
            position - (Vector3.UnitZ * size),
            position + (Vector3.UnitZ * size),
            color);
    }

    private static float GetMarkerSize(
        RenderCamera camera,
        Vector3 position)
    {
        float distance = Vector3.Distance(camera.Eye, position);
        if (!float.IsFinite(distance))
        {
            return 0.025f;
        }

        return Math.Clamp(distance * 0.006f, 0.018f, 0.06f);
    }

    private static Vector4 GetRoleColor(
        BoneRenderRole role) =>
        role switch
        {
            BoneRenderRole.Deform =>
                new Vector4(0.96f, 0.97f, 0.98f, 0.98f),
            BoneRenderRole.Helper =>
                new Vector4(1.0f, 0.90f, 0.16f, 0.96f),
            BoneRenderRole.Camera =>
                new Vector4(0.95f, 0.60f, 0.20f, 0.96f),
            BoneRenderRole.Prop =>
                new Vector4(1.0f, 0.78f, 0.08f, 0.98f),
            _ =>
                new Vector4(0.75f, 0.75f, 0.75f, 0.90f),
        };
}

public sealed class SelectionRenderPass : D3D11LineRenderPassBase
{
    public SelectionRenderPass()
        : base(renderAsOverlay: true)
    {
    }

    public override string Name => "Bone selection";

    public override RenderFeature Feature => RenderFeature.SelectionHighlight;

    protected override void BuildVertices(
        RenderFrameSnapshot frame,
        int width,
        int height,
        List<LineRenderVertex> vertices)
    {
        if (frame.Skeleton is not { } skeleton)
        {
            return;
        }

        foreach (BoneRenderData bone in skeleton.Bones)
        {
            if (!bone.IsSelected)
            {
                continue;
            }

            Vector3 position = GetWorldPosition(
                bone,
                skeleton.RootTransform);
            float markerSize = GetSelectionMarkerSize(frame.Camera, position);
            Vector4 selectionColor =
                new(1.0f, 0.82f, 0.16f, 1.0f);
            AddLine(
                vertices,
                position - (Vector3.UnitX * markerSize),
                position + (Vector3.UnitX * markerSize),
                selectionColor);
            AddLine(
                vertices,
                position - (Vector3.UnitY * markerSize),
                position + (Vector3.UnitY * markerSize),
                selectionColor);
            AddLine(
                vertices,
                position - (Vector3.UnitZ * markerSize),
                position + (Vector3.UnitZ * markerSize),
                selectionColor);

            if (bone.ParentIndex >= 0
                && bone.ParentIndex < skeleton.Bones.Count)
            {
                AddLine(
                    vertices,
                    GetWorldPosition(
                        skeleton.Bones[bone.ParentIndex],
                        skeleton.RootTransform),
                    position,
                    selectionColor);
            }
        }
    }

    private static float GetSelectionMarkerSize(
        RenderCamera camera,
        Vector3 position)
    {
        float distance = Vector3.Distance(camera.Eye, position);
        return Math.Clamp(distance * 0.018f, 0.025f, 0.25f);
    }
}

public sealed class GizmoRenderPass : D3D11LineRenderPassBase
{
    public GizmoRenderPass()
        : base(renderAsOverlay: true)
    {
    }

    public override string Name => "Editor gizmos";

    public override RenderFeature Feature => RenderFeature.GizmoPassHook;

    protected override void BuildVertices(
        RenderFrameSnapshot frame,
        int width,
        int height,
        List<LineRenderVertex> vertices)
    {
        foreach (GizmoRenderData gizmo in frame.Gizmos)
        {
            Vector4 color = Vector4.Clamp(
                gizmo.Color,
                Vector4.Zero,
                Vector4.One);
            AddLine(vertices, gizmo.Start, gizmo.End, color);

            if (gizmo.Kind == GizmoKind.RotationHandle)
            {
                // Rotation rings are already published as contiguous arc
                // segments. Decorating every segment would turn the ring into
                // a lopsided chain of arrowheads.
                continue;
            }

            if (gizmo.Kind is not (
                    GizmoKind.Axis
                    or GizmoKind.TranslationHandle
                    or GizmoKind.ScaleHandle))
            {
                continue;
            }

            Vector3 segment = gizmo.End - gizmo.Start;
            float length = segment.Length();
            if (length <= 1.0e-6f)
            {
                continue;
            }

            Vector3 direction = segment / length;
            Vector3 reference =
                MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) < 0.92f
                    ? Vector3.UnitY
                    : Vector3.UnitX;
            Vector3 perpendicular = Vector3.Normalize(
                Vector3.Cross(direction, reference));
            float thicknessScale = Math.Clamp(
                gizmo.Thickness,
                0.5f,
                4.0f);
            float arrowLength = Math.Min(
                length * 0.22f,
                0.075f * thicknessScale);
            if (gizmo.Kind == GizmoKind.ScaleHandle)
            {
                Vector3 binormal = Vector3.Normalize(
                    Vector3.Cross(direction, perpendicular));
                float halfSize = Math.Min(
                    length * 0.12f,
                    0.025f * thicknessScale);
                Vector3 side = perpendicular * halfSize;
                Vector3 depth = binormal * halfSize;
                Vector3 first = gizmo.End + side + depth;
                Vector3 second = gizmo.End - side + depth;
                Vector3 third = gizmo.End - side - depth;
                Vector3 fourth = gizmo.End + side - depth;
                AddLine(vertices, first, second, color);
                AddLine(vertices, second, third, color);
                AddLine(vertices, third, fourth, color);
                AddLine(vertices, fourth, first, color);
                continue;
            }

            Vector3 arrowBase = gizmo.End - (direction * arrowLength);
            Vector3 wing = perpendicular * arrowLength * 0.45f;
            AddLine(vertices, gizmo.End, arrowBase + wing, color);
            AddLine(vertices, gizmo.End, arrowBase - wing, color);
        }
    }
}

/// <summary>
/// Draws the captured scene aspect as an in-airspace overlay. The mesh,
/// skeleton, and gizmo passes use the same centered viewport rectangle, so the
/// frame is a real projection boundary rather than a WPF decoration floating
/// over an independently stretched D3D surface.
/// </summary>
public sealed class FppSafeFrameRenderPass : D3D11LineRenderPassBase
{
    public FppSafeFrameRenderPass()
        : base(renderAsOverlay: true)
    {
    }

    public override string Name => "FPP safe frame";

    public override RenderFeature Feature => RenderFeature.FppSafeFrame;

    protected override bool UseFullViewport => true;

    protected override Matrix4x4 CreateLineTransform(
        RenderFrameSnapshot frame,
        int width,
        int height) =>
        Matrix4x4.Identity;

    protected override void BuildVertices(
        RenderFrameSnapshot frame,
        int width,
        int height,
        List<LineRenderVertex> vertices)
    {
        if (frame.FppProjectionState?.SceneAspectRatio is not
            float sceneAspect ||
            !float.IsFinite(sceneAspect) ||
            sceneAspect <= 0.0f)
        {
            return;
        }

        RenderCamera frameCamera = frame.Camera with
        {
            ProjectionAspectRatio = sceneAspect,
        };
        RenderViewportRectangle safeFrame =
            RenderCameraMath.CreateSceneViewport(
                frameCamera,
                width,
                height);
        float safeWidth = Math.Max(1, width);
        float safeHeight = Math.Max(1, height);
        float left = ((safeFrame.X / safeWidth) * 2.0f) - 1.0f;
        float right =
            (((safeFrame.X + safeFrame.Width) / safeWidth) * 2.0f) -
            1.0f;
        float top = 1.0f - ((safeFrame.Y / safeHeight) * 2.0f);
        float bottom =
            1.0f -
            (((safeFrame.Y + safeFrame.Height) / safeHeight) * 2.0f);
        Vector4 color = new(0.96f, 0.73f, 0.24f, 0.9f);
        Vector3 topLeft = new(left, top, 0.0f);
        Vector3 topRight = new(right, top, 0.0f);
        Vector3 bottomRight = new(right, bottom, 0.0f);
        Vector3 bottomLeft = new(left, bottom, 0.0f);
        AddLine(vertices, topLeft, topRight, color);
        AddLine(vertices, topRight, bottomRight, color);
        AddLine(vertices, bottomRight, bottomLeft, color);
        AddLine(vertices, bottomLeft, topLeft, color);
    }
}

public abstract class D3D11LineRenderPassBase :
    ID3D11RenderPass,
    IDisposable
{
    private static readonly int LineVertexStride =
        Marshal.SizeOf<LineRenderVertex>();
    private const string ShaderSource =
        """
        cbuffer FrameConstants : register(b0)
        {
            row_major float4x4 ViewProjection;
        };

        struct VertexInput
        {
            float3 Position : POSITION;
            float4 Color : COLOR0;
        };

        struct PixelInput
        {
            float4 Position : SV_POSITION;
            float4 Color : COLOR0;
        };

        PixelInput VSMain(VertexInput input)
        {
            PixelInput output;
            output.Position = mul(float4(input.Position, 1.0f), ViewProjection);
            output.Color = input.Color;
            return output;
        }

        float4 PSMain(PixelInput input) : SV_TARGET
        {
            return input.Color;
        }
        """;

    private readonly bool _renderAsOverlay;
    private readonly List<LineRenderVertex> _vertices = [];
    private ID3D11Device? _deviceIdentity;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _frameConstants;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11DepthStencilState? _overlayDepthState;
    private LineRenderVertex[] _vertexStaging = [];
    private bool _disposed;

    protected D3D11LineRenderPassBase(bool renderAsOverlay = false)
    {
        _renderAsOverlay = renderAsOverlay;
    }

    public abstract string Name { get; }

    public abstract RenderFeature Feature { get; }

    public void Render(
        in D3D11RenderFrameContext context,
        RenderFrameSnapshot frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _vertices.Clear();
        BuildVertices(
            frame,
            context.Width,
            context.Height,
            _vertices);
        if (_vertices.Count == 0)
        {
            return;
        }

        EnsureDeviceResources(context.Device);
        EnsureVertexCapacity(context.Device, _vertices.Count);
        _vertices.CopyTo(_vertexStaging);
        if (_vertices.Count < _vertexStaging.Length)
        {
            Array.Clear(
                _vertexStaging,
                _vertices.Count,
                _vertexStaging.Length - _vertices.Count);
        }

        MatrixShaderConstants constants = new()
        {
            ViewProjection = CreateLineTransform(
                frame,
                context.Width,
                context.Height),
        };
        ID3D11DeviceContext deviceContext = context.DeviceContext;
        if (UseFullViewport)
        {
            deviceContext.RSSetViewport(new Viewport(
                0.0f,
                0.0f,
                context.Width,
                context.Height,
                0.0f,
                1.0f));
        }
        else
        {
            RenderViewportRectangle sceneViewport =
                RenderCameraMath.CreateSceneViewport(
                    frame.Camera,
                    context.Width,
                    context.Height);
            deviceContext.RSSetViewport(new Viewport(
                sceneViewport.X,
                sceneViewport.Y,
                sceneViewport.Width,
                sceneViewport.Height,
                0.0f,
                1.0f));
        }

        deviceContext.UpdateSubresource(
            in constants,
            _frameConstants!);
        deviceContext.UpdateSubresource(
            _vertexStaging,
            _vertexBuffer!);
        deviceContext.IASetInputLayout(_inputLayout);
        deviceContext.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        deviceContext.IASetVertexBuffer(
            0,
            _vertexBuffer!,
            checked((uint)LineVertexStride));
        deviceContext.VSSetShader(_vertexShader);
        deviceContext.VSSetConstantBuffer(0, _frameConstants);
        deviceContext.PSSetShader(_pixelShader);
        deviceContext.OMSetDepthStencilState(
            _renderAsOverlay ? _overlayDepthState : null);
        deviceContext.Draw((uint)_vertices.Count, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseDeviceResources();
        GC.SuppressFinalize(this);
    }

    protected virtual bool UseFullViewport => false;

    protected virtual Matrix4x4 CreateLineTransform(
        RenderFrameSnapshot frame,
        int width,
        int height) =>
        RenderCameraMath.CreateViewProjection(
            frame.Camera,
            width,
            height);

    protected abstract void BuildVertices(
        RenderFrameSnapshot frame,
        int width,
        int height,
        List<LineRenderVertex> vertices);

    protected static void AddLine(
        List<LineRenderVertex> vertices,
        Vector3 start,
        Vector3 end,
        Vector4 color)
    {
        if (!IsFinite(start)
            || !IsFinite(end)
            || !IsFinite(color))
        {
            return;
        }

        vertices.Add(new LineRenderVertex(start, color));
        vertices.Add(new LineRenderVertex(end, color));
    }

    protected static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z);

    protected static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X)
        && float.IsFinite(value.Y)
        && float.IsFinite(value.Z)
        && float.IsFinite(value.W);

    protected static Vector3 GetWorldPosition(
        BoneRenderData bone,
        Matrix4x4 rootTransform)
    {
        Vector3 skeletonPosition = new(
            bone.WorldTransform.M41,
            bone.WorldTransform.M42,
            bone.WorldTransform.M43);
        return Vector3.Transform(skeletonPosition, rootTransform);
    }

    private void EnsureDeviceResources(ID3D11Device device)
    {
        if (ReferenceEquals(_deviceIdentity, device)
            && _vertexShader is not null)
        {
            return;
        }

        ReleaseDeviceResources();
        byte[] vertexShaderBytecode = D3D11ShaderCompiler.Compile(
            ShaderSource,
            "VSMain",
            "vs_5_0",
            "EditorLines.hlsl");
        byte[] pixelShaderBytecode = D3D11ShaderCompiler.Compile(
            ShaderSource,
            "PSMain",
            "ps_5_0",
            "EditorLines.hlsl");
        _vertexShader = device.CreateVertexShader(vertexShaderBytecode);
        _pixelShader = device.CreatePixelShader(pixelShaderBytecode);
        _inputLayout = device.CreateInputLayout(
            [
                new InputElementDescription(
                    "POSITION",
                    0,
                    Format.R32G32B32_Float,
                    0,
                    0),
                new InputElementDescription(
                    "COLOR",
                    0,
                    Format.R32G32B32A32_Float,
                    12,
                    0),
            ],
            vertexShaderBytecode);
        _frameConstants = device.CreateBuffer(
            checked((uint)Marshal.SizeOf<MatrixShaderConstants>()),
            BindFlags.ConstantBuffer);
        _overlayDepthState = device.CreateDepthStencilState(
            new DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.Always,
                StencilEnable = false,
            });
        _deviceIdentity = device;
    }

    private void EnsureVertexCapacity(
        ID3D11Device device,
        int requiredVertexCount)
    {
        if (_vertexBuffer is not null
            && _vertexStaging.Length >= requiredVertexCount)
        {
            return;
        }

        int capacity = 32;
        while (capacity < requiredVertexCount)
        {
            capacity = checked(capacity * 2);
        }

        _vertexBuffer?.Dispose();
        _vertexStaging = new LineRenderVertex[capacity];
        _vertexBuffer = device.CreateBuffer(
            checked((uint)(capacity * LineVertexStride)),
            BindFlags.VertexBuffer);
    }

    private void ReleaseDeviceResources()
    {
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _vertexStaging = [];
        _overlayDepthState?.Dispose();
        _overlayDepthState = null;
        _frameConstants?.Dispose();
        _frameConstants = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _deviceIdentity = null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    protected readonly record struct LineRenderVertex(
        Vector3 Position,
        Vector4 Color);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MatrixShaderConstants
    {
        public Matrix4x4 ViewProjection;
    }
}
