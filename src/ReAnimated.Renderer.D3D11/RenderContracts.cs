using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// Supplies immutable scene snapshots to the render thread. Implementations must
/// be safe to call from the renderer's dedicated background thread.
/// </summary>
public interface IRenderSceneSource
{
    RenderFrameSnapshot CaptureFrame();
}

/// <summary>
/// Thread-safe producer/consumer buffer for editor scene data. Writers publish
/// stable array copies from the UI or asset worker; the render thread consumes
/// one immutable snapshot without touching WPF objects.
/// </summary>
public sealed class RenderSceneBuffer
{
    private RenderFrameSnapshot _frame;

    public RenderSceneBuffer(Vector4? clearColor = null)
    {
        _frame = RenderFrameSnapshot.Empty(clearColor);
    }

    public RenderFrameSnapshot Capture(RenderCamera camera)
    {
        RenderFrameSnapshot frame = Volatile.Read(ref _frame);
        return frame with { Camera = camera };
    }

    public void SetClearColor(Vector4 clearColor)
    {
        Update(frame => frame with { ClearColor = clearColor });
    }

    public void SetSkeleton(SkeletonRenderData? skeleton)
    {
        SkeletonRenderData? stableSkeleton = skeleton is null
            ? null
            : skeleton with
            {
                Bones = skeleton.Bones.ToArray(),
            };
        Update(frame => frame with
        {
            Skeleton = stableSkeleton,
            AuthoringOverlays =
                ClearPrecomputedDeformedBounds(
                    frame.AuthoringOverlays),
        });
    }

    public void SetMeshes(IEnumerable<MeshRenderData> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        MeshRenderData[] stableMeshes = meshes.ToArray();
        Update(frame => frame with
        {
            Meshes = stableMeshes,
            AuthoringOverlays =
                ClearPrecomputedDeformedBounds(
                    frame.AuthoringOverlays),
        });
    }

    public void SetGizmos(IEnumerable<GizmoRenderData> gizmos)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        GizmoRenderData[] stableGizmos = gizmos.ToArray();
        Update(frame => frame with { Gizmos = stableGizmos });
    }

    public void SetMorphWeights(IEnumerable<MorphWeight> morphWeights)
    {
        ArgumentNullException.ThrowIfNull(morphWeights);
        MorphWeight[] stableWeights = morphWeights.ToArray();
        Update(frame => frame with
        {
            MorphWeights = stableWeights,
            AuthoringOverlays =
                ClearPrecomputedDeformedBounds(
                    frame.AuthoringOverlays),
        });
    }

    public void SetFppProjectionState(
        RenderFppProjectionState? projectionState)
    {
        Update(frame => frame with
        {
            FppProjectionState = projectionState,
        });
    }

    public void SetAuthoringOverlays(
        RenderAuthoringOverlayState? overlayState)
    {
        RenderAuthoringOverlayState stableState =
            CreateStableOverlayState(overlayState);
        Update(frame => frame with
        {
            AuthoringOverlays = stableState,
        });
    }

    public void SetScene(
        IEnumerable<MeshRenderData> meshes,
        SkeletonRenderData? skeleton,
        IEnumerable<GizmoRenderData> gizmos,
        IEnumerable<MorphWeight>? morphWeights = null,
        long? generation = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(gizmos);

        MeshRenderData[] stableMeshes = meshes.ToArray();
        SkeletonRenderData? stableSkeleton = skeleton is null
            ? null
            : skeleton with
            {
                Bones = skeleton.Bones.ToArray(),
            };
        GizmoRenderData[] stableGizmos = gizmos.ToArray();
        MorphWeight[] stableWeights = morphWeights?.ToArray() ?? [];
        Update(frame => frame with
        {
            Meshes = stableMeshes,
            Skeleton = stableSkeleton,
            Gizmos = stableGizmos,
            MorphWeights = stableWeights,
            AuthoringOverlays =
                ClearPrecomputedDeformedBounds(
                    frame.AuthoringOverlays),
        }, generation);
    }

    private void Update(
        Func<RenderFrameSnapshot, RenderFrameSnapshot> transform,
        long? generation = null)
    {
        while (true)
        {
            RenderFrameSnapshot current = Volatile.Read(ref _frame);
            if (generation is { } requested &&
                requested < current.Generation)
            {
                return;
            }

            RenderFrameSnapshot next = transform(current);
            if (generation is { } accepted)
            {
                next = next with { Generation = accepted };
            }
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _frame, next, current),
                current))
            {
                return;
            }
        }
    }

    private static RenderAuthoringOverlayState CreateStableOverlayState(
        RenderAuthoringOverlayState? overlayState)
    {
        if (overlayState is null)
        {
            return RenderAuthoringOverlayState.Disabled;
        }

        ArgumentNullException.ThrowIfNull(overlayState.Options);
        RootMotionTrailRenderData? rootMotionTrail =
            overlayState.RootMotionTrail;
        if (rootMotionTrail is not null &&
            rootMotionTrail.WorldPositions.Length >
            RootMotionTrailRenderData.MaximumPointCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlayState),
                rootMotionTrail.WorldPositions.Length,
                $"A root-motion trail supports at most {RootMotionTrailRenderData.MaximumPointCount:N0} points.");
        }

        ImmutableArray<DeformedMeshBoundsRenderData> deformedMeshBounds =
            overlayState.DeformedMeshBounds.IsDefault
                ? []
                : overlayState.DeformedMeshBounds;
        if (deformedMeshBounds.Length >
            AuthoringOverlayBoundsPrecomputer.MaximumBoundsMeshCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlayState),
                deformedMeshBounds.Length,
                $"Authoring overlays support at most {AuthoringOverlayBoundsPrecomputer.MaximumBoundsMeshCount:N0} precomputed mesh bounds.");
        }

        return overlayState with
        {
            DeformedMeshBounds = deformedMeshBounds,
        };
    }

    private static RenderAuthoringOverlayState
        ClearPrecomputedDeformedBounds(
            RenderAuthoringOverlayState overlayState) =>
        overlayState.DeformedMeshBounds.IsDefaultOrEmpty
            ? overlayState
            : overlayState with
            {
                DeformedMeshBounds = [],
            };
}

public sealed record RenderFrameSnapshot(
    Vector4 ClearColor,
    RenderCamera Camera,
    IReadOnlyList<MeshRenderData> Meshes,
    SkeletonRenderData? Skeleton,
    IReadOnlyList<GizmoRenderData> Gizmos,
    IReadOnlyList<MorphWeight> MorphWeights)
{
    /// <summary>
    /// Monotonic editor publication generation. Scene buffers reject an older
    /// generated scene after a newer scrub, cancellation, or clip switch has
    /// already been published.
    /// </summary>
    public long Generation { get; init; }

    /// <summary>
    /// Optional FPP routing state. It is present only while the target viewport
    /// is using the FPP authoring camera; scene data remains unchanged when the
    /// orbit camera is restored.
    /// </summary>
    public RenderFppProjectionState? FppProjectionState { get; init; }

    /// <summary>
    /// Optional authoring overlays. All built-in switches default off.
    /// </summary>
    public RenderAuthoringOverlayState AuthoringOverlays { get; init; } =
        RenderAuthoringOverlayState.Disabled;

    public static RenderFrameSnapshot Empty(Vector4? clearColor = null)
    {
        return new RenderFrameSnapshot(
            clearColor ?? new Vector4(0.095f, 0.105f, 0.125f, 1.0f),
            RenderCamera.Default,
            Array.Empty<MeshRenderData>(),
            null,
            Array.Empty<GizmoRenderData>(),
            Array.Empty<MorphWeight>());
    }
}

public readonly record struct RenderCamera(
    Vector3 Eye,
    Vector3 Target,
    Vector3 Up,
    float VerticalFieldOfViewDegrees,
    float NearPlane,
    float FarPlane)
{
    /// <summary>
    /// When present, projection and the centered render viewport preserve this
    /// captured scene aspect instead of stretching to the host control.
    /// </summary>
    public float? ProjectionAspectRatio { get; init; }

    public static RenderCamera Default { get; } = new(
        // Retail DL1 character meshes face +Z in their decoded bind pose.
        // Start squarely in front of that plane so left/right bone symmetry
        // and material orientation are immediately readable. Orbit remains
        // available for depth inspection.
        new Vector3(0.0f, 1.45f, 4.5f),
        new Vector3(0.0f, 1.0f, 0.0f),
        Vector3.UnitY,
        60.0f,
        0.02f,
        2_000.0f);
}

/// <summary>
/// Immutable CPU-side geometry consumed by the D3D11 mesh pass. The caller owns
/// the backing memory and must not mutate it after publication. For skinned
/// meshes, inverse-bind matrices and optional per-draw skeleton bone indexes
/// share the same order. An empty map preserves the legacy identity mapping;
/// a populated map allows a bounded draw palette to address a larger rig.
/// </summary>
public sealed record MeshRenderData(
    string Id,
    ReadOnlyMemory<MeshVertex> Vertices,
    ReadOnlyMemory<uint> Indices,
    Matrix4x4 LocalToWorld,
    ReadOnlyMemory<Matrix4x4> InverseBindMatrices,
    bool IsSkinned)
{
    public Vector4 Tint { get; init; } =
        new(0.62f, 0.69f, 0.78f, 1.0f);

    public bool IsSelected { get; init; }

    public IReadOnlyList<MorphTargetRenderData> MorphTargets { get; init; } =
        Array.Empty<MorphTargetRenderData>();

    /// <summary>
    /// Optional per-draw palette mapping. Entry <c>i</c> identifies the full
    /// <see cref="SkeletonRenderData.Bones"/> row paired with inverse bind
    /// matrix <c>i</c>. Vertex bone indexes remain local to this bounded draw
    /// palette. Empty preserves the legacy one-entry-per-skeleton-row layout.
    /// </summary>
    public ReadOnlyMemory<int> SkinBoneIndices { get; init; }

    /// <summary>
    /// Optional immutable base mip decoded from a retail DL1 texture resource.
    /// Exact DL1 material techniques and shader parameters are outside this
    /// neutral preview contract.
    /// </summary>
    public TextureRenderData? BaseColorTexture { get; init; }

    /// <summary>
    /// Selects the scene or FPP-hands lens without coupling retail mesh
    /// decoding to preview state. FPP hands routing is enabled only by an
    /// explicit target-viewport projection state.
    /// </summary>
    public MeshProjectionRole ProjectionRole { get; init; } =
        MeshProjectionRole.Scene;
}

public enum MeshProjectionRole
{
    Scene,
    FppHands,
}

public enum RenderProjectionFovAxis
{
    Horizontal,
    Vertical,
}

public enum RenderProjectionFarPlane
{
    Finite,
    Infinite,
}

/// <summary>
/// Renderer-local projection values. This mirrors the evaluated DL1 capture
/// without making the D3D11 project own game-format contracts.
/// </summary>
public readonly record struct RenderProjectionParameters(
    float FieldOfViewDegrees,
    RenderProjectionFovAxis FieldOfViewAxis,
    float AspectRatio,
    float NearPlane,
    RenderProjectionFarPlane FarPlane,
    float? FarClip = null);

public sealed record RenderFppProjectionState(
    bool RouteHandsMeshes,
    float? SceneAspectRatio,
    RenderProjectionParameters? HandsProjection);

public enum TextureRenderFormat
{
    Bc1Unorm,
    Bc2Unorm,
    Bc3Unorm,
}

public sealed record TextureRenderData(
    string Id,
    int Width,
    int Height,
    TextureRenderFormat Format,
    int RowPitch,
    ReadOnlyMemory<byte> BaseMipBytes);

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct MeshVertex
{
    public MeshVertex(
        Vector3 position,
        Vector3 normal,
        Vector2 textureCoordinate,
        Vector4 boneWeights,
        Vector4 boneIndices)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = textureCoordinate;
        BoneWeights = boneWeights;
        BoneIndices = boneIndices;
    }

    public Vector3 Position { get; }

    public Vector3 Normal { get; }

    public Vector2 TextureCoordinate { get; }

    public Vector4 BoneWeights { get; }

    public Vector4 BoneIndices { get; }
}

public sealed record SkeletonRenderData(
    IReadOnlyList<BoneRenderData> Bones,
    Matrix4x4 RootTransform)
{
    public bool ShowDeformBones { get; init; } = true;

    public bool ShowHelpers { get; init; }

    public bool ShowCameraHelpers { get; init; } = true;

    public bool ShowProps { get; init; }

    public bool IsVisible(BoneRenderData bone) =>
        bone.IsSelected ||
        (bone.IsHierarchyOverlayVisible &&
         bone.Role switch
         {
             BoneRenderRole.Deform => ShowDeformBones,
             BoneRenderRole.Helper => ShowHelpers,
             BoneRenderRole.Camera => ShowCameraHelpers,
             BoneRenderRole.Prop => ShowProps,
             _ => false,
         });
}

public enum BoneRenderRole
{
    Deform,
    Helper,
    Camera,
    Prop,
}

public readonly record struct BoneRenderData(
    string Name,
    int ParentIndex,
    Matrix4x4 LocalTransform,
    Matrix4x4 WorldTransform,
    bool IsSelected)
{
    /// <summary>
    /// Renderer-facing structural role. The default preserves prior callers,
    /// where every row represented a deform skeleton bone.
    /// </summary>
    public BoneRenderRole Role { get; init; } =
        BoneRenderRole.Deform;

    /// <summary>
    /// Whether the hierarchy row contributes a locator or parent-child link to
    /// the ordinary skeleton overlay. Retail animated props can retain
    /// palette-driving animation rows for skinning and editing while exposing
    /// only their compact prop/helper pivot rig. Selection still wins through
    /// <see cref="SkeletonRenderData.IsVisible"/>.
    /// </summary>
    public bool IsHierarchyOverlayVisible { get; init; } = true;
}

public enum GizmoKind
{
    Line,
    Axis,
    Bone,
    RotationArc,
    TranslationHandle,
    RotationHandle,
    ScaleHandle,
}

public readonly record struct GizmoRenderData(
    GizmoKind Kind,
    Vector3 Start,
    Vector3 End,
    Vector4 Color,
    float Thickness,
    TranslationGizmoBinding? TranslationBinding = null,
    RenderTransformGizmoBinding? TransformBinding = null,
    Vector3? InteractionAxisWorld = null);

public readonly record struct MorphWeight(string Name, float Weight);

public sealed record MorphTargetRenderData(
    string Name,
    ReadOnlyMemory<Vector3> PositionDeltas,
    ReadOnlyMemory<Vector3> NormalDeltas);

public readonly record struct ActiveMorphTarget(
    int TargetIndex,
    MorphTargetRenderData Target,
    float Weight);

public static class MorphTargetSelection
{
    public const int MaximumActiveTargetCount = 64;

    public const float ActivityThreshold = 0.001f;

    public const long MaximumGpuDeltaBytes =
        512L * 1024 * 1024;

    public static ActiveMorphTarget[] Select(
        MeshRenderData mesh,
        IReadOnlyList<MorphWeight> weights)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(weights);

        Dictionary<string, float> weightsByName =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (MorphWeight morphWeight in weights)
        {
            if (string.IsNullOrWhiteSpace(morphWeight.Name)
                || !float.IsFinite(morphWeight.Weight))
            {
                continue;
            }

            weightsByName[morphWeight.Name] =
                morphWeight.Weight;
        }

        return mesh.MorphTargets
            .Select(static (target, index) => (Target: target, Index: index))
            .Where(item =>
                weightsByName.TryGetValue(
                    item.Target.Name,
                    out float weight) &&
                MathF.Abs(weight) >
                ActivityThreshold)
            .Select(item => new ActiveMorphTarget(
                item.Index,
                item.Target,
                weightsByName[item.Target.Name]))
            .Take(MaximumActiveTargetCount)
            .ToArray();
    }
}

public static class GpuSkinningPalette
{
    public const int MaximumBoneCount = 256;

    /// <summary>
    /// Builds mesh-local to world skin matrices using the row-vector convention
    /// used by System.Numerics and the renderer's row-major HLSL shaders.
    /// </summary>
    public static Matrix4x4[] Build(
        MeshRenderData mesh,
        SkeletonRenderData skeleton)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(skeleton);

        int inverseBindCount = mesh.InverseBindMatrices.Length;
        int skeletonBoneCount = skeleton.Bones.Count;
        ReadOnlySpan<int> skinBoneIndices =
            mesh.SkinBoneIndices.Span;
        if (inverseBindCount == 0)
        {
            throw new ArgumentException(
                "A skinned mesh must provide at least one inverse-bind matrix.",
                nameof(mesh));
        }

        if (skinBoneIndices.IsEmpty &&
            inverseBindCount != skeletonBoneCount)
        {
            throw new ArgumentException(
                $"The mesh has {inverseBindCount} inverse-bind matrices but the skeleton has {skeletonBoneCount} bones.",
                nameof(skeleton));
        }

        if (!skinBoneIndices.IsEmpty &&
            skinBoneIndices.Length != inverseBindCount)
        {
            throw new ArgumentException(
                $"The mesh has {inverseBindCount} inverse-bind matrices but {skinBoneIndices.Length} per-draw skeleton bone indexes.",
                nameof(mesh));
        }

        if (inverseBindCount > MaximumBoneCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mesh),
                inverseBindCount,
                $"The D3D11 preview supports at most {MaximumBoneCount} bones per draw.");
        }

        ReadOnlySpan<Matrix4x4> inverseBindMatrices =
            mesh.InverseBindMatrices.Span;
        Matrix4x4[] palette = new Matrix4x4[inverseBindCount];
        for (int index = 0; index < palette.Length; index++)
        {
            int skeletonBoneIndex = skinBoneIndices.IsEmpty
                ? index
                : skinBoneIndices[index];
            if ((uint)skeletonBoneIndex >=
                (uint)skeletonBoneCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mesh),
                    skeletonBoneIndex,
                    $"Per-draw palette entry {index} references skeleton bone {skeletonBoneIndex} outside {skeletonBoneCount} rows.");
            }

            palette[index] =
                inverseBindMatrices[index]
                * skeleton.Bones[skeletonBoneIndex].WorldTransform
                * skeleton.RootTransform
                * mesh.LocalToWorld;
        }

        return palette;
    }
}

public static class RenderMeshValidation
{
    public static bool TryValidate(
        MeshRenderData mesh,
        SkeletonRenderData? skeleton,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        if (string.IsNullOrWhiteSpace(mesh.Id))
        {
            error = "Mesh ID cannot be empty.";
            return false;
        }

        if (mesh.Vertices.IsEmpty)
        {
            error = $"Mesh '{mesh.Id}' has no vertices.";
            return false;
        }

        if (mesh.Indices.IsEmpty || mesh.Indices.Length % 3 != 0)
        {
            error = $"Mesh '{mesh.Id}' must have a non-empty triangle index buffer.";
            return false;
        }

        foreach (uint index in mesh.Indices.Span)
        {
            if (index >= mesh.Vertices.Length)
            {
                error = $"Mesh '{mesh.Id}' contains an out-of-range vertex index.";
                return false;
            }
        }

        if (mesh.BaseColorTexture is { } texture
            && !TryValidateTexture(texture, out error))
        {
            error =
                $"Mesh '{mesh.Id}' has an invalid base-color texture: {error}";
            return false;
        }

        HashSet<string> morphNames =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (MorphTargetRenderData morphTarget in mesh.MorphTargets)
        {
            if (string.IsNullOrWhiteSpace(morphTarget.Name)
                || !morphNames.Add(morphTarget.Name))
            {
                error =
                    $"Mesh '{mesh.Id}' contains an empty or duplicate morph-target name.";
                return false;
            }

            if (morphTarget.PositionDeltas.Length != mesh.Vertices.Length)
            {
                error =
                    $"Morph target '{morphTarget.Name}' on mesh '{mesh.Id}' must contain one position delta per vertex.";
                return false;
            }

            if (!morphTarget.NormalDeltas.IsEmpty
                && morphTarget.NormalDeltas.Length != mesh.Vertices.Length)
            {
                error =
                    $"Morph target '{morphTarget.Name}' on mesh '{mesh.Id}' must contain zero or one normal delta per vertex.";
                return false;
            }
        }

        long morphCount =
            Math.Max(1, mesh.MorphTargets.Count);
        const long morphDeltaStride =
            sizeof(float) * 6L;
        long maximumVertices =
            MorphTargetSelection.MaximumGpuDeltaBytes /
            (morphCount * morphDeltaStride);
        if (mesh.Vertices.Length > maximumVertices)
        {
            error =
                $"Mesh '{mesh.Id}' exceeds the bounded {MorphTargetSelection.MaximumGpuDeltaBytes:N0}-byte GPU morph-delta preview limit.";
            return false;
        }

        if (!mesh.IsSkinned)
        {
            if (!mesh.InverseBindMatrices.IsEmpty ||
                !mesh.SkinBoneIndices.IsEmpty)
            {
                error =
                    $"Non-skinned mesh '{mesh.Id}' cannot publish skin-palette data.";
                return false;
            }

            error = null;
            return true;
        }

        if (skeleton is null)
        {
            error = $"Skinned mesh '{mesh.Id}' has no skeleton.";
            return false;
        }

        int boneCount = mesh.InverseBindMatrices.Length;
        int mappedBoneCount = mesh.SkinBoneIndices.Length;
        if (boneCount == 0 ||
            boneCount > GpuSkinningPalette.MaximumBoneCount)
        {
            error =
                $"Skinned mesh '{mesh.Id}' needs between 1 and {GpuSkinningPalette.MaximumBoneCount} inverse-bind matrices per draw.";
            return false;
        }

        if (mappedBoneCount == 0)
        {
            if (boneCount != skeleton.Bones.Count)
            {
                error =
                    $"Skinned mesh '{mesh.Id}' has no per-draw bone map, so its {boneCount} inverse-bind matrices must match all {skeleton.Bones.Count} skeleton rows.";
                return false;
            }
        }
        else
        {
            if (mappedBoneCount != boneCount)
            {
                error =
                    $"Skinned mesh '{mesh.Id}' has {boneCount} inverse-bind matrices but {mappedBoneCount} per-draw skeleton bone indexes.";
                return false;
            }

            ReadOnlySpan<int> skinBoneIndices =
                mesh.SkinBoneIndices.Span;
            for (int paletteIndex = 0;
                 paletteIndex < skinBoneIndices.Length;
                 paletteIndex++)
            {
                int skeletonBoneIndex =
                    skinBoneIndices[paletteIndex];
                if ((uint)skeletonBoneIndex >=
                    (uint)skeleton.Bones.Count)
                {
                    error =
                        $"Skinned mesh '{mesh.Id}' palette entry {paletteIndex} references skeleton bone {skeletonBoneIndex} outside {skeleton.Bones.Count} rows.";
                    return false;
                }
            }
        }

        foreach (MeshVertex vertex in mesh.Vertices.Span)
        {
            if (!HasValidInfluence(vertex.BoneWeights.X, vertex.BoneIndices.X, boneCount)
                || !HasValidInfluence(vertex.BoneWeights.Y, vertex.BoneIndices.Y, boneCount)
                || !HasValidInfluence(vertex.BoneWeights.Z, vertex.BoneIndices.Z, boneCount)
                || !HasValidInfluence(vertex.BoneWeights.W, vertex.BoneIndices.W, boneCount))
            {
                error =
                    $"Skinned mesh '{mesh.Id}' contains a non-integral or out-of-range weighted bone index.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool HasValidInfluence(
        float weight,
        float boneIndex,
        int boneCount)
    {
        if (MathF.Abs(weight) <= 1.0e-6f)
        {
            return true;
        }

        return float.IsFinite(weight)
            && weight >= 0.0f
            && float.IsFinite(boneIndex)
            && boneIndex >= 0.0f
            && boneIndex < boneCount
            && MathF.Abs(boneIndex - MathF.Round(boneIndex)) <= 1.0e-4f;
    }

    private static bool TryValidateTexture(
        TextureRenderData texture,
        out string? error)
    {
        const int maximumDimension = 8192;
        const int maximumBytes = 128 * 1024 * 1024;
        if (string.IsNullOrWhiteSpace(texture.Id)
            || texture.Width <= 0
            || texture.Height <= 0
            || texture.Width > maximumDimension
            || texture.Height > maximumDimension)
        {
            error = "its identity or dimensions are outside the preview bounds.";
            return false;
        }

        int blockBytes = texture.Format switch
        {
            TextureRenderFormat.Bc1Unorm => 8,
            TextureRenderFormat.Bc2Unorm
                or TextureRenderFormat.Bc3Unorm => 16,
            _ => 0,
        };
        if (blockBytes == 0)
        {
            error = "its compressed format is unsupported.";
            return false;
        }

        int expectedRowPitch;
        int expectedBytes;
        try
        {
            expectedRowPitch = checked(
                ((texture.Width + 3) / 4) * blockBytes);
            expectedBytes = checked(
                expectedRowPitch * ((texture.Height + 3) / 4));
        }
        catch (OverflowException)
        {
            error = "its compressed extent overflowed.";
            return false;
        }

        if (texture.RowPitch != expectedRowPitch
            || texture.BaseMipBytes.Length != expectedBytes
            || expectedBytes > maximumBytes)
        {
            error = "its row pitch or base-mip length is inconsistent.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Extension point for future skeleton, mesh, grid, and gizmo passes. Passes run
/// on the render thread and must not access WPF DispatcherObjects.
/// </summary>
public interface ID3D11RenderPass
{
    string Name { get; }

    RenderFeature Feature { get; }

    void Render(in D3D11RenderFrameContext context, RenderFrameSnapshot frame);
}

public readonly record struct D3D11RenderFrameContext(
    ID3D11Device Device,
    ID3D11DeviceContext DeviceContext,
    ID3D11RenderTargetView RenderTargetView,
    ID3D11DepthStencilView DepthStencilView,
    int Width,
    int Height,
    long FrameNumber,
    Action<string> ReportDiagnostic);
