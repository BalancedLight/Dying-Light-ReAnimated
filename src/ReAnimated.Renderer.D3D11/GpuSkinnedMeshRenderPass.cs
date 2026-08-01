using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Draws indexed static and four-influence skinned geometry. Skin matrices are
/// evaluated from the current skeleton pose; position and inverse-transpose
/// normal palettes are uploaded to a D3D11 constant buffer, and vertex
/// deformation is performed by the vertex shader.
/// </summary>
public sealed class GpuSkinnedMeshRenderPass : ID3D11RenderPass, IDisposable
{
    private static readonly int MeshVertexStride =
        Marshal.SizeOf<MeshVertex>();
    private const string ShaderSource =
        """
        cbuffer MeshConstants : register(b0)
        {
            row_major float4x4 LocalToWorld;
            row_major float4x4 LocalToWorldNormal;
            row_major float4x4 ViewProjection;
            float4 Tint;
            uint UseSkinning;
            uint BoneCount;
            uint UseBaseColorTexture;
            uint UseOutline;
            float OutlineExpansion;
            float3 MeshPadding;
        };

        cbuffer SkinPalette : register(b1)
        {
            row_major float4x4 SkinMatrices[256];
            row_major float4x4 SkinNormalMatrices[256];
        };

        cbuffer MorphConstants : register(b2)
        {
            float4 MorphWeights[16];
            uint4 MorphTargetIndices[16];
            uint MorphTargetCount;
            uint MorphVertexCount;
            float2 MorphPadding;
        };

        struct MorphDelta
        {
            float3 Position;
            float3 Normal;
        };

        StructuredBuffer<MorphDelta> MorphDeltas : register(t0);
        Texture2D BaseColorTexture : register(t0);
        SamplerState BaseColorSampler : register(s0);

        struct VertexInput
        {
            float3 Position : POSITION;
            float3 Normal : NORMAL;
            float2 TextureCoordinate : TEXCOORD0;
            float4 BoneWeights : BLENDWEIGHT;
            float4 BoneIndices : BLENDINDICES;
            uint VertexIndex : SV_VertexID;
        };

        struct PixelInput
        {
            float4 Position : SV_POSITION;
            float3 WorldNormal : NORMAL;
            float2 TextureCoordinate : TEXCOORD0;
        };

        PixelInput VSMain(VertexInput input)
        {
            PixelInput output;
            float3 morphedPosition = input.Position;
            float3 morphedNormal = input.Normal;
            [loop]
            for (uint activeIndex = 0;
                 activeIndex < MorphTargetCount;
                 activeIndex++)
            {
                uint packedIndex = activeIndex / 4;
                uint componentIndex = activeIndex % 4;
                float weight = MorphWeights[packedIndex][componentIndex];
                uint targetIndex =
                    MorphTargetIndices[packedIndex][componentIndex];
                uint deltaIndex =
                    targetIndex * MorphVertexCount
                    + input.VertexIndex;
                MorphDelta delta = MorphDeltas[deltaIndex];
                morphedPosition += delta.Position * weight;
                morphedNormal += delta.Normal * weight;
            }
            float4 localPosition = float4(morphedPosition, 1.0f);
            float4 localNormal = float4(morphedNormal, 0.0f);
            float4 worldPosition;
            float3 worldNormal;

            float weightSum =
                input.BoneWeights.x
                + input.BoneWeights.y
                + input.BoneWeights.z
                + input.BoneWeights.w;
            if (UseSkinning != 0 && weightSum > 0.000001f)
            {
                float4 weights = input.BoneWeights / weightSum;
                uint4 indexes = (uint4)round(input.BoneIndices);
                indexes = min(indexes, BoneCount - 1);
                worldPosition =
                    mul(localPosition, SkinMatrices[indexes.x]) * weights.x
                    + mul(localPosition, SkinMatrices[indexes.y]) * weights.y
                    + mul(localPosition, SkinMatrices[indexes.z]) * weights.z
                    + mul(localPosition, SkinMatrices[indexes.w]) * weights.w;
                worldNormal =
                    mul(localNormal, SkinNormalMatrices[indexes.x]).xyz
                    * weights.x
                    + mul(localNormal, SkinNormalMatrices[indexes.y]).xyz
                    * weights.y
                    + mul(localNormal, SkinNormalMatrices[indexes.z]).xyz
                    * weights.z
                    + mul(localNormal, SkinNormalMatrices[indexes.w]).xyz
                    * weights.w;
            }
            else
            {
                worldPosition = mul(localPosition, LocalToWorld);
                worldNormal =
                    mul(localNormal, LocalToWorldNormal).xyz;
            }

            float normalLength = length(worldNormal);
            float3 normalizedWorldNormal =
                normalLength > 0.000001f
                    ? worldNormal / normalLength
                    : float3(0.0f, 0.0f, 0.0f);
            if (UseOutline != 0)
            {
                if (normalLength > 0.000001f)
                {
                    worldPosition.xyz +=
                        normalizedWorldNormal
                        * OutlineExpansion;
                }
            }

            output.Position = mul(worldPosition, ViewProjection);
            output.WorldNormal = normalizedWorldNormal;
            output.TextureCoordinate = input.TextureCoordinate;
            return output;
        }

        float4 PSMain(PixelInput input) : SV_TARGET
        {
            if (UseOutline != 0)
            {
                return float4(1.0f, 0.62f, 0.06f, 1.0f);
            }

            float3 lightDirection = normalize(float3(0.38f, 0.78f, -0.50f));
            // Keep material inspection readable on surfaces facing away from
            // the single neutral key light. This is intentionally an editor
            // light, not a claim about DL1's scene lighting.
            float interpolatedNormalLength =
                length(input.WorldNormal);
            float3 shadedNormal =
                interpolatedNormalLength > 0.000001f
                    ? input.WorldNormal / interpolatedNormalLength
                    : float3(0.0f, 0.0f, 0.0f);
            float diffuse = 0.68f
                + 0.32f * saturate(dot(shadedNormal, lightDirection));
            float4 surfaceColor = Tint;
            if (UseBaseColorTexture != 0)
            {
                surfaceColor *= BaseColorTexture.Sample(
                    BaseColorSampler,
                    input.TextureCoordinate);
            }

            return float4(
                surfaceColor.rgb * diffuse,
                surfaceColor.a);
        }
        """;

    private readonly Dictionary<MeshRenderData, CachedMesh> _meshCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MeshRenderData, string> _invalidMeshErrors =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MeshRenderData> _missingHandsProjectionMeshes =
        new(ReferenceEqualityComparer.Instance);
    private readonly Matrix4x4[] _paletteStaging =
        new Matrix4x4[
            GpuSkinningPalette.MaximumBoneCount * 2];
    private ID3D11Device? _deviceIdentity;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11Buffer? _meshConstants;
    private ID3D11Buffer? _paletteConstants;
    private ID3D11Buffer? _morphConstants;
    private ID3D11SamplerState? _textureSampler;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11RasterizerState? _outlineRasterizerState;
    private bool _disposed;

    public string Name => "GPU-skinned mesh";

    public RenderFeature Feature => RenderFeature.GpuSkinning;

    internal int CachedMeshCount => _meshCache.Count;

    internal int MissingHandsProjectionMeshCount =>
        _missingHandsProjectionMeshes.Count;

    public void Render(
        in D3D11RenderFrameContext context,
        RenderFrameSnapshot frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Meshes.Count == 0)
        {
            if (_meshCache.Count > 0 ||
                _invalidMeshErrors.Count > 0 ||
                _missingHandsProjectionMeshes.Count > 0)
            {
                context.DeviceContext.VSSetShaderResources(
                    0,
                    new ID3D11ShaderResourceView[] { null! });
                context.DeviceContext.PSSetShaderResources(
                    0,
                    new ID3D11ShaderResourceView[] { null! });
                RemoveInactiveMeshes(
                    new HashSet<MeshRenderData>(
                        ReferenceEqualityComparer.Instance));
                _missingHandsProjectionMeshes.Clear();
            }

            return;
        }

        EnsureDeviceResources(context.Device);
        ID3D11DeviceContext deviceContext = context.DeviceContext;
        deviceContext.IASetInputLayout(_inputLayout);
        deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        deviceContext.VSSetShader(_vertexShader);
        deviceContext.PSSetShader(_pixelShader);
        deviceContext.PSSetSamplers(0, [_textureSampler!]);
        deviceContext.VSSetConstantBuffers(
            0,
            [_meshConstants!, _paletteConstants!, _morphConstants!]);
        deviceContext.PSSetConstantBuffers(
            0,
            [_meshConstants!]);
        deviceContext.OMSetDepthStencilState(null);
        deviceContext.RSSetState(_rasterizerState);
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

        HashSet<MeshRenderData> activeMeshes =
            new(ReferenceEqualityComparer.Instance);
        foreach (MeshRenderData mesh in frame.Meshes)
        {
            activeMeshes.Add(mesh);
            RenderProjectionParameters? projectionOverride = null;
            if (mesh.ProjectionRole == MeshProjectionRole.FppHands &&
                frame.FppProjectionState is
                {
                    RouteHandsMeshes: true,
                } fppState)
            {
                if (fppState.HandsProjection is not { } handsProjection)
                {
                    if (_missingHandsProjectionMeshes.Add(mesh))
                    {
                        context.ReportDiagnostic(
                            $"FPP hands mesh '{mesh.Id}' was skipped because no valid captured hands projection is available.");
                    }

                    continue;
                }

                _missingHandsProjectionMeshes.Remove(mesh);
                projectionOverride = handsProjection;
            }
            else
            {
                _missingHandsProjectionMeshes.Remove(mesh);
            }

            if (!RenderMeshValidation.TryValidate(
                    mesh,
                    frame.Skeleton,
                    out string? validationError))
            {
                if (!_invalidMeshErrors.TryGetValue(mesh, out string? priorError)
                    || !string.Equals(
                        priorError,
                        validationError,
                        StringComparison.Ordinal))
                {
                    _invalidMeshErrors[mesh] = validationError!;
                    context.ReportDiagnostic(
                        $"Mesh preview skipped: {validationError}");
                }

                continue;
            }

            _invalidMeshErrors.Remove(mesh);
            if (!_meshCache.TryGetValue(mesh, out CachedMesh? cachedMesh))
            {
                cachedMesh = CachedMesh.Create(context.Device, mesh);
                _meshCache.Add(mesh, cachedMesh);
            }

            bool useSkinning = mesh.IsSkinned && frame.Skeleton is not null;
            ActiveMorphTarget[] activeMorphTargets =
                MorphTargetSelection.Select(mesh, frame.MorphWeights);
            uint paletteCount = 1;
            if (useSkinning)
            {
                Matrix4x4[] palette =
                    GpuSkinningPalette.Build(mesh, frame.Skeleton!);
                paletteCount = (uint)palette.Length;
                Array.Copy(palette, _paletteStaging, palette.Length);
                for (int paletteIndex = 0;
                     paletteIndex < palette.Length;
                     paletteIndex++)
                {
                    _paletteStaging[
                        GpuSkinningPalette.MaximumBoneCount
                        + paletteIndex] =
                        NormalTransformMatrix.CreateOrZero(
                            palette[paletteIndex]);
                }
            }
            else
            {
                _paletteStaging[0] = Matrix4x4.Identity;
                _paletteStaging[
                    GpuSkinningPalette.MaximumBoneCount] =
                    Matrix4x4.Identity;
            }

            MorphShaderConstants morphConstants =
                CreateMorphConstants(
                    activeMorphTargets,
                    mesh.Vertices.Length);
            deviceContext.UpdateSubresource(
                in morphConstants,
                _morphConstants!);
            deviceContext.VSSetShaderResources(
                0,
                [cachedMesh.MorphShaderResourceView]);

            deviceContext.UpdateSubresource(
                _paletteStaging,
                _paletteConstants!);
            MeshShaderConstants constants = new()
            {
                LocalToWorld = mesh.LocalToWorld,
                LocalToWorldNormal =
                    NormalTransformMatrix.CreateOrZero(
                        mesh.LocalToWorld),
                ViewProjection = RenderCameraMath.CreateViewProjection(
                    frame.Camera,
                    context.Width,
                    context.Height,
                    projectionOverride),
                Tint = MeshSelectionHighlightPolicy.ResolveTint(
                    mesh,
                    frame.AuthoringOverlays),
                UseSkinning = useSkinning ? 1u : 0u,
                BoneCount = paletteCount,
                UseBaseColorTexture =
                    cachedMesh.BaseColorShaderResourceView is null
                        ? 0u
                        : 1u,
                UseOutline = 0,
                OutlineExpansion = 0.0f,
            };
            deviceContext.UpdateSubresource(
                in constants,
                _meshConstants!);
            deviceContext.IASetVertexBuffer(
                0,
                cachedMesh.VertexBuffer,
                checked((uint)MeshVertexStride));
            deviceContext.IASetIndexBuffer(
                cachedMesh.IndexBuffer,
                Format.R32_UInt,
                0);
            deviceContext.PSSetShaderResources(
                0,
                [cachedMesh.BaseColorShaderResourceView!]);
            deviceContext.DrawIndexed(
                (uint)mesh.Indices.Length,
                0,
                0);
            if (MeshSelectionHighlightPolicy
                .ShouldRenderOutline(
                    mesh,
                    frame.AuthoringOverlays))
            {
                constants.UseBaseColorTexture = 0;
                constants.UseOutline = 1;
                constants.OutlineExpansion =
                    ResolveOutlineExpansion(frame.Camera);
                deviceContext.UpdateSubresource(
                    in constants,
                    _meshConstants!);
                deviceContext.RSSetState(
                    _outlineRasterizerState);
                deviceContext.DrawIndexed(
                    (uint)mesh.Indices.Length,
                    0,
                    0);
                deviceContext.RSSetState(
                    _rasterizerState);
            }
        }

        deviceContext.VSSetShaderResources(
            0,
            new ID3D11ShaderResourceView[] { null! });
        deviceContext.PSSetShaderResources(
            0,
            new ID3D11ShaderResourceView[] { null! });
        _missingHandsProjectionMeshes.RemoveWhere(
            mesh => !activeMeshes.Contains(mesh));
        RemoveInactiveMeshes(activeMeshes);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseDeviceResources();
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
            "GpuSkinnedMesh.hlsl");
        byte[] pixelShaderBytecode = D3D11ShaderCompiler.Compile(
            ShaderSource,
            "PSMain",
            "ps_5_0",
            "GpuSkinnedMesh.hlsl");

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
                    "NORMAL",
                    0,
                    Format.R32G32B32_Float,
                    12,
                    0),
                new InputElementDescription(
                    "TEXCOORD",
                    0,
                    Format.R32G32_Float,
                    24,
                    0),
                new InputElementDescription(
                    "BLENDWEIGHT",
                    0,
                    Format.R32G32B32A32_Float,
                    32,
                    0),
                new InputElementDescription(
                    "BLENDINDICES",
                    0,
                    Format.R32G32B32A32_Float,
                    48,
                    0),
            ],
            vertexShaderBytecode);
        _meshConstants = device.CreateBuffer(
            checked((uint)Marshal.SizeOf<MeshShaderConstants>()),
            BindFlags.ConstantBuffer);
        _paletteConstants = device.CreateBuffer(
            checked((uint)(
                Marshal.SizeOf<Matrix4x4>()
                * GpuSkinningPalette.MaximumBoneCount
                * 2)),
            BindFlags.ConstantBuffer);
        _morphConstants = device.CreateBuffer(
            checked((uint)Marshal.SizeOf<MorphShaderConstants>()),
            BindFlags.ConstantBuffer);
        _textureSampler = device.CreateSamplerState(
            new SamplerDescription(
                Filter.MinMagMipLinear,
                TextureAddressMode.Wrap,
                mipLODBias: 0.0f,
                maxAnisotropy: 1,
                ComparisonFunction.Never,
                minLOD: 0.0f,
                maxLOD: float.MaxValue));
        _rasterizerState = device.CreateRasterizerState(
            new RasterizerDescription(
                CullMode.Back,
                FillMode.Solid)
            {
                // DL1 retail indices agree with their outward normals. With
                // the renderer's right-handed view/projection convention,
                // those exterior faces are counter-clockwise on the render
                // target. D3D11's implicit default is clockwise, which culls
                // the exterior and makes closed meshes look inside-out.
                FrontCounterClockwise = true,
                DepthClipEnable = true,
            });
        _outlineRasterizerState =
            device.CreateRasterizerState(
                new RasterizerDescription(
                    CullMode.Front,
                    FillMode.Solid)
                {
                    FrontCounterClockwise = true,
                    DepthClipEnable = true,
                });
        _deviceIdentity = device;
    }

    private static float ResolveOutlineExpansion(
        RenderCamera camera)
    {
        float distance = Vector3.Distance(
            camera.Eye,
            camera.Target);
        return float.IsFinite(distance)
            ? Math.Clamp(
                distance * 0.012f,
                0.004f,
                0.12f)
            : 0.02f;
    }

    private void RemoveInactiveMeshes(
        HashSet<MeshRenderData> activeMeshes)
    {
        foreach (MeshRenderData mesh in _meshCache.Keys
                     .Where(mesh => !activeMeshes.Contains(mesh))
                     .ToArray())
        {
            _meshCache.Remove(mesh, out CachedMesh? cachedMesh);
            cachedMesh?.Dispose();
        }

        foreach (MeshRenderData mesh in _invalidMeshErrors.Keys
                     .Where(mesh => !activeMeshes.Contains(mesh))
                     .ToArray())
        {
            _invalidMeshErrors.Remove(mesh);
        }
    }

    private static unsafe MorphShaderConstants CreateMorphConstants(
        ActiveMorphTarget[] activeTargets,
        int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(activeTargets);
        if (activeTargets.Length >
            MorphTargetSelection.MaximumActiveTargetCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activeTargets));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        MorphShaderConstants constants = new()
        {
            MorphTargetCount =
                checked((uint)activeTargets.Length),
            MorphVertexCount = checked((uint)vertexCount),
        };
        for (int index = 0;
             index < activeTargets.Length;
             index++)
        {
            constants.MorphWeights[index] =
                activeTargets[index].Weight;
            constants.MorphTargetIndices[index] =
                checked((uint)activeTargets[index].TargetIndex);
        }

        return constants;
    }

    private void ReleaseDeviceResources()
    {
        foreach (CachedMesh mesh in _meshCache.Values)
        {
            mesh.Dispose();
        }

        _meshCache.Clear();
        _invalidMeshErrors.Clear();
        _missingHandsProjectionMeshes.Clear();
        _outlineRasterizerState?.Dispose();
        _outlineRasterizerState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _textureSampler?.Dispose();
        _textureSampler = null;
        _morphConstants?.Dispose();
        _morphConstants = null;
        _paletteConstants?.Dispose();
        _paletteConstants = null;
        _meshConstants?.Dispose();
        _meshConstants = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _deviceIdentity = null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MeshShaderConstants
    {
        public Matrix4x4 LocalToWorld;
        public Matrix4x4 LocalToWorldNormal;
        public Matrix4x4 ViewProjection;
        public Vector4 Tint;
        public uint UseSkinning;
        public uint BoneCount;
        public uint UseBaseColorTexture;
        public uint UseOutline;
        public float OutlineExpansion;
        public Vector3 Padding;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private unsafe struct MorphShaderConstants
    {
        public fixed float MorphWeights[
            MorphTargetSelection.MaximumActiveTargetCount];
        public fixed uint MorphTargetIndices[
            MorphTargetSelection.MaximumActiveTargetCount];
        public uint MorphTargetCount;
        public uint MorphVertexCount;
        public Vector2 Padding;
    }

    private sealed class CachedMesh : IDisposable
    {
        private CachedMesh(
            ID3D11Buffer vertexBuffer,
            ID3D11Buffer indexBuffer,
            ID3D11Buffer morphBuffer,
            ID3D11ShaderResourceView morphShaderResourceView,
            ID3D11Texture2D? baseColorTexture,
            ID3D11ShaderResourceView? baseColorShaderResourceView)
        {
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            MorphBuffer = morphBuffer;
            MorphShaderResourceView = morphShaderResourceView;
            BaseColorTexture = baseColorTexture;
            BaseColorShaderResourceView =
                baseColorShaderResourceView;
        }

        public ID3D11Buffer VertexBuffer { get; }

        public ID3D11Buffer IndexBuffer { get; }

        public ID3D11Buffer MorphBuffer { get; }

        public ID3D11ShaderResourceView MorphShaderResourceView { get; }

        public ID3D11Texture2D? BaseColorTexture { get; }

        public ID3D11ShaderResourceView? BaseColorShaderResourceView { get; }

        public static unsafe CachedMesh Create(
            ID3D11Device device,
            MeshRenderData mesh)
        {
            ID3D11Buffer? vertexBuffer = null;
            ID3D11Buffer? indexBuffer = null;
            ID3D11Buffer? morphBuffer = null;
            ID3D11ShaderResourceView? morphView = null;
            ID3D11Texture2D? baseColorTexture = null;
            ID3D11ShaderResourceView? baseColorView = null;
            try
            {
                vertexBuffer = device.CreateBuffer(
                    mesh.Vertices.Span,
                    BindFlags.VertexBuffer,
                    ResourceUsage.Immutable);
                indexBuffer = device.CreateBuffer(
                    mesh.Indices.Span,
                    BindFlags.IndexBuffer,
                    ResourceUsage.Immutable);
                MorphDeltaVertex[] morphDeltas =
                    BuildMorphDeltaInventory(mesh);
                morphBuffer = device.CreateBuffer(
                    morphDeltas,
                    BindFlags.ShaderResource,
                    ResourceUsage.Immutable,
                    CpuAccessFlags.None,
                    ResourceOptionFlags.BufferStructured,
                    structureByteStride:
                    checked((uint)MorphDeltaVertexStride));
                morphView = device.CreateShaderResourceView(
                    morphBuffer,
                    new ShaderResourceViewDescription(
                        morphBuffer,
                        Format.Unknown,
                        0,
                        checked((uint)morphDeltas.Length)));
                if (mesh.BaseColorTexture is { } texture)
                {
                    Format format = texture.Format switch
                    {
                        TextureRenderFormat.Bc1Unorm =>
                            Format.BC1_UNorm,
                        TextureRenderFormat.Bc2Unorm =>
                            Format.BC2_UNorm,
                        TextureRenderFormat.Bc3Unorm =>
                            Format.BC3_UNorm,
                        _ => throw new InvalidDataException(
                            $"Texture '{texture.Id}' uses an unsupported renderer format."),
                    };
                    Texture2DDescription description = new(
                        format,
                        checked((uint)texture.Width),
                        checked((uint)texture.Height),
                        arraySize: 1,
                        mipLevels: 1,
                        BindFlags.ShaderResource,
                        ResourceUsage.Immutable,
                        CpuAccessFlags.None,
                        sampleCount: 1,
                        sampleQuality: 0,
                        ResourceOptionFlags.None);
                    fixed (byte* data = texture.BaseMipBytes.Span)
                    {
                        SubresourceData initialData = new(
                            data,
                            checked((uint)texture.RowPitch),
                            checked((uint)texture.BaseMipBytes.Length));
                        baseColorTexture = device.CreateTexture2D(
                            description,
                            initialData);
                    }

                    baseColorView =
                        device.CreateShaderResourceView(
                            baseColorTexture,
                            new ShaderResourceViewDescription(
                                baseColorTexture,
                                ShaderResourceViewDimension.Texture2D,
                                format,
                                mostDetailedMip: 0,
                                mipLevels: 1,
                                firstArraySlice: 0,
                                arraySize: 1));
                }

                return new CachedMesh(
                    vertexBuffer,
                    indexBuffer,
                    morphBuffer,
                    morphView,
                    baseColorTexture,
                    baseColorView);
            }
            catch
            {
                baseColorView?.Dispose();
                baseColorTexture?.Dispose();
                morphView?.Dispose();
                morphBuffer?.Dispose();
                indexBuffer?.Dispose();
                vertexBuffer?.Dispose();
                throw;
            }
        }

        private static MorphDeltaVertex[] BuildMorphDeltaInventory(
            MeshRenderData mesh)
        {
            int vertexCount = mesh.Vertices.Length;
            int elementCount = checked(
                Math.Max(
                    1,
                    vertexCount * mesh.MorphTargets.Count));
            MorphDeltaVertex[] deltas =
                new MorphDeltaVertex[elementCount];
            for (int targetIndex = 0;
                 targetIndex < mesh.MorphTargets.Count;
                 targetIndex++)
            {
                MorphTargetRenderData target =
                    mesh.MorphTargets[targetIndex];
                ReadOnlySpan<Vector3> positionDeltas =
                    target.PositionDeltas.Span;
                ReadOnlySpan<Vector3> normalDeltas =
                    target.NormalDeltas.Span;
                int targetOffset = checked(
                    targetIndex * vertexCount);
                for (int vertexIndex = 0;
                     vertexIndex < vertexCount;
                     vertexIndex++)
                {
                    deltas[targetOffset + vertexIndex] =
                        new MorphDeltaVertex(
                            positionDeltas[vertexIndex],
                            normalDeltas.IsEmpty
                                ? Vector3.Zero
                                : normalDeltas[vertexIndex]);
                }
            }

            return deltas;
        }

        public void Dispose()
        {
            BaseColorShaderResourceView?.Dispose();
            BaseColorTexture?.Dispose();
            MorphShaderResourceView.Dispose();
            MorphBuffer.Dispose();
            IndexBuffer.Dispose();
            VertexBuffer.Dispose();
        }
    }

    private static readonly int MorphDeltaVertexStride =
        Marshal.SizeOf<MorphDeltaVertex>();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct MorphDeltaVertex(
        Vector3 PositionDelta,
        Vector3 NormalDelta);
}
