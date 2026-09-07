using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public enum CustomModelPreviewMode
{
    SourceFbx,
    Dl1Output,
}

public sealed record CustomModelPreviewPayload(
    IReadOnlyList<MeshRenderData> Meshes,
    SkeletonRenderData? Skeleton,
    ImmutableArray<string> Diagnostics);

/// <summary>
/// One immutable prepared preview. Mesh conversion, texture decoding, and DL1
/// rig authoring happen once; animation playback samples only the skeleton.
/// </summary>
public sealed class CustomModelPreviewSession
{
    private readonly FbxModelAuthoringImportResult _model;
    private readonly Dl1PreparedAuthoredRig? _authoredRig;
    private readonly bool _configuredFlipTextureCoordinateV;

    internal CustomModelPreviewSession(
        FbxModelAuthoringImportResult model,
        CustomModelPreviewMode requestedMode,
        CustomModelPreviewMode effectiveMode,
        bool configuredFlipTextureCoordinateV,
        bool appliesTextureCoordinateVFlip,
        Dl1PreparedAuthoredRig? authoredRig,
        ImmutableArray<MeshRenderData> meshes,
        ImmutableArray<string> diagnostics)
    {
        _model = model;
        _authoredRig = authoredRig;
        _configuredFlipTextureCoordinateV = configuredFlipTextureCoordinateV;
        RequestedMode = requestedMode;
        EffectiveMode = effectiveMode;
        AppliesTextureCoordinateVFlip = appliesTextureCoordinateVFlip;
        Meshes = meshes;
        Diagnostics = diagnostics;
    }

    public CustomModelPreviewMode RequestedMode { get; }

    public CustomModelPreviewMode EffectiveMode { get; }

    public bool IsSourceFallback => RequestedMode != EffectiveMode;

    public bool AppliesTextureCoordinateVFlip { get; }

    public ImmutableArray<MeshRenderData> Meshes { get; }

    public ImmutableArray<string> Diagnostics { get; }

    public CustomModelDocument Document => _model.Package.Document;

    public CustomModelPackage Package => _model.Package;

    /// <summary>
    /// Converts an evaluated runtime-rig pose into the exact hierarchy and
    /// bind bases used by the prepared DL1-output meshes. Source FBX mode and
    /// an explicitly diagnosed DL1-output fallback keep the runtime pose
    /// unchanged.
    /// </summary>
    public SkeletonPose CreatePresentationPose(SkeletonPose runtimePose)
    {
        ArgumentNullException.ThrowIfNull(runtimePose);
        return _authoredRig?.RebasePose(runtimePose) ?? runtimePose;
    }

    public int GetPresentationBoneIndex(int sourceBoneIndex)
    {
        if (_model.Rig is null || sourceBoneIndex < 0 || sourceBoneIndex >= _model.Rig.BoneCount)
            throw new ArgumentOutOfRangeException(nameof(sourceBoneIndex));
        int? index = MapSourceBoneIndex(sourceBoneIndex);
        if (index is not { } mapped || mapped < 0)
            throw new InvalidOperationException("The source bone has no emitted preview counterpart.");
        return mapped;
    }

    public SkeletonRenderData CreateSkeleton(
        SkeletonPose runtimePose,
        int? selectedBoneIndex = null,
        TransformMatrix? actorWorldTransform = null)
    {
        SkeletonPose previewPose = CreatePresentationPose(runtimePose);
        int? previewSelectedBone = MapSourceBoneIndex(selectedBoneIndex);
        return CorePreviewAdapter.ToRenderSkeleton(
            previewPose,
            previewSelectedBone,
            actorWorldTransform);
    }

    internal bool Matches(
        FbxModelAuthoringImportResult model,
        CustomModelPreviewMode mode)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ReferenceEquals(_model, model) &&
            RequestedMode == mode &&
            _configuredFlipTextureCoordinateV ==
                model.Package.Document.BuildSettings.FlipTextureCoordinateV;
    }

    public SkeletonRenderData? CreateSkeleton(
        AnimationClip? clip,
        int frame,
        int? selectedBoneIndex = null)
    {
        if (_model.Rig is not { } rig)
        {
            return null;
        }

        SkeletonPose sourcePose = clip is null
            ? rig.CreateBindPose()
            : clip.SamplePose(
                rig,
                clip.FrameRate.SecondsForFrame(
                    Math.Clamp(
                        frame,
                        0,
                        checked((int)Math.Min(int.MaxValue, clip.FrameCount - 1)))),
                PlaybackMode.Clamp);
        return CreateSkeleton(sourcePose, selectedBoneIndex);
    }

    public RenderCamera? CreatePreviewCamera(
        AnimationClip? clip,
        int frame,
        string? nodeName)
    {
        if (_model.Rig is not { } rig || string.IsNullOrWhiteSpace(nodeName))
        {
            return null;
        }

        SkeletonPose sourcePose = clip is null
            ? rig.CreateBindPose()
            : clip.SamplePose(
                rig,
                clip.FrameRate.SecondsForFrame(
                    Math.Clamp(
                        frame,
                        0,
                        checked((int)Math.Min(int.MaxValue, clip.FrameCount - 1)))),
                PlaybackMode.Clamp);
        SkeletonPose previewPose = _authoredRig?.RebasePose(sourcePose) ?? sourcePose;
        int nodeIndex = previewPose.Rig.GetBoneIndex(nodeName);
        if (nodeIndex < 0)
        {
            return null;
        }

        TransformMatrix world = previewPose.GlobalMatrices[nodeIndex];
        Vector3 eye = ToVector3(world.Translation);
        Vector3 forward = ToVector3(world.TransformDirection(Vector3D.UnitZ));
        Vector3 up = ToVector3(world.TransformDirection(-Vector3D.UnitY));
        if (!IsUsableDirection(forward) || !IsUsableDirection(up))
        {
            return null;
        }

        return new RenderCamera(
            eye,
            eye + Vector3.Normalize(forward),
            Vector3.Normalize(up),
            60.0f,
            0.02f,
            2_000.0f);
    }

    public CustomModelPreviewPayload CreatePayload(
        AnimationClip? clip,
        int frame,
        int? selectedBoneIndex = null) =>
        new(
            Meshes,
            CreateSkeleton(clip, frame, selectedBoneIndex),
            Diagnostics);

    private int? MapSourceBoneIndex(int? selectedBoneIndex) =>
        _authoredRig is null || selectedBoneIndex is not { } sourceIndex
            ? selectedBoneIndex
            : (uint)sourceIndex <
                (uint)_authoredRig.Contract.SourceToPhysicalIndices.Length
                ? _authoredRig.Contract.SourceToPhysicalIndices[sourceIndex]
                : null;

    private static Vector3 ToVector3(Vector3D value) => new(
        checked((float)value.X),
        checked((float)value.Y),
        checked((float)value.Z));

    private static bool IsUsableDirection(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        value.LengthSquared() > 1.0e-8f;
}

/// <summary>
/// Converts the immutable Models-workspace import into the same renderer
/// contracts used by retail DL1 assets. Texture interpretation remains a
/// neutral authoring preview; source bytes stay unchanged in the package.
/// </summary>
public static class CustomModelPreviewAdapter
{
    public static CustomModelPreviewPayload Create(
        FbxModelAuthoringImportResult imported,
        AnimationClip? clip,
        int frame,
        int? selectedBoneIndex = null,
        CustomModelPreviewMode mode = CustomModelPreviewMode.SourceFbx)
    {
        CustomModelPreviewSession session = CreateSession(imported, mode);
        return session.CreatePayload(clip, frame, selectedBoneIndex);
    }

    public static CustomModelPreviewSession CreateSession(
        FbxModelAuthoringImportResult imported,
        CustomModelPreviewMode mode = CustomModelPreviewMode.SourceFbx)
    {
        ArgumentNullException.ThrowIfNull(imported);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported custom-model preview mode.");
        }

        CustomModelPackage package = imported.Package;
        package.Document.Validate();
        bool configuredFlipTextureCoordinateV =
            package.Document.BuildSettings.FlipTextureCoordinateV;
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        CustomModelPreviewMode effectiveMode = mode;
        Dl1PreparedAuthoredRig? authoredRig = null;
        PreparedMeshSet preparedMeshes;
        if (mode == CustomModelPreviewMode.Dl1Output)
        {
            try
            {
                authoredRig = imported.Rig is null
                    ? null
                    : Dl1CustomModelRigPreparer.Prepare(imported);
                if (authoredRig is not null)
                {
                    diagnostics.AddRange(authoredRig.Diagnostics.Select(
                        static diagnostic => diagnostic.Message));
                }

                preparedMeshes = CreateMeshes(
                    imported,
                    authoredRig,
                    configuredFlipTextureCoordinateV);
            }
            catch (Exception exception) when (IsRecoverableDl1PreviewFailure(exception))
            {
                effectiveMode = CustomModelPreviewMode.SourceFbx;
                authoredRig = null;
                diagnostics.Add(
                    $"DL1 Output preview preparation failed ({exception.Message}). Showing the Source FBX preview with unmodified texture coordinates; DL1 build and export remain blocked until preparation succeeds.");
                preparedMeshes = CreateMeshes(
                    imported,
                    authoredRig: null,
                    flipTextureCoordinateV: false);
            }
        }
        else
        {
            preparedMeshes = CreateMeshes(
                imported,
                authoredRig: null,
                flipTextureCoordinateV: false);
        }

        diagnostics.AddRange(preparedMeshes.Diagnostics);
        if (effectiveMode == CustomModelPreviewMode.Dl1Output)
        {
            diagnostics.Add(imported.Rig is null
                ? "DL1 Output preview uses the static source geometry and DL1 build-boundary texture coordinates."
                : "DL1 Output preview uses the emitted Chrome hierarchy, +X bone frames, inverse references, and segment bounds.");
        }
        else if (mode == CustomModelPreviewMode.SourceFbx)
        {
            diagnostics.Add(
                "Source FBX preview uses the imported hierarchy, bind references, and unmodified FBX texture coordinates.");
        }

        bool appliesTextureCoordinateVFlip =
            effectiveMode == CustomModelPreviewMode.Dl1Output &&
            configuredFlipTextureCoordinateV;
        return new CustomModelPreviewSession(
            imported,
            mode,
            effectiveMode,
            configuredFlipTextureCoordinateV,
            appliesTextureCoordinateVFlip,
            authoredRig,
            preparedMeshes.Meshes,
            diagnostics.ToImmutable());
    }

    private static PreparedMeshSet CreateMeshes(
        FbxModelAuthoringImportResult imported,
        Dl1PreparedAuthoredRig? authoredRig,
        bool flipTextureCoordinateV)
    {
        CustomModelPackage package = imported.Package;
        Dictionary<Guid, CustomModelMaterial> materials = package.Document.Materials
            .ToDictionary(static material => material.Id);
        var textureCache = new Dictionary<string, TextureRenderData?>(
            StringComparer.OrdinalIgnoreCase);
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (authoredRig is not null && authoredRig.Surfaces.Length != imported.Surfaces.Length)
        {
            throw new InvalidDataException(
                "The prepared DL1 rig does not match the imported draw-surface table.");
        }

        var meshes = ImmutableArray.CreateBuilder<MeshRenderData>(imported.Surfaces.Length);
        for (int surfaceIndex = 0; surfaceIndex < imported.Surfaces.Length; surfaceIndex++)
        {
            FbxModelSurface surface = imported.Surfaces[surfaceIndex];
            MeshVertex[] vertices = surface.Vertices
                .Select(vertex => ToRenderVertex(vertex, flipTextureCoordinateV))
                .ToArray();
            ImmutableArray<TransformMatrix> preparedInverseBinds = authoredRig is null
                ? surface.InverseBindMatrices
                : authoredRig.Surfaces[surfaceIndex].InverseBindMatrices;
            Matrix4x4[] inverseBinds = preparedInverseBinds
                .Select(static matrix => CorePreviewAdapter.ToSystemMatrix(matrix))
                .ToArray();
            MorphTargetRenderData[] morphTargets = surface.MorphTargets
                .Select(target => ToRenderMorphTarget(surface, target))
                .ToArray();
            TextureRenderData? baseColor = null;
            if (materials.TryGetValue(surface.MaterialId, out CustomModelMaterial? material))
            {
                CustomModelTextureBinding? binding = material.Textures.FirstOrDefault(
                    static texture => texture.Semantic == CustomModelTextureSemantic.BaseColor);
                if (binding is not null)
                {
                    if (!textureCache.TryGetValue(binding.ContentSha256, out baseColor))
                    {
                        baseColor = TryDecodeTexture(package, binding, diagnostics);
                        textureCache.Add(binding.ContentSha256, baseColor);
                    }
                }
            }

            meshes.Add(new MeshRenderData(
                surface.Id,
                vertices,
                surface.Indices.ToArray(),
                Matrix4x4.Identity,
                inverseBinds,
                surface.IsSkinned)
            {
                SkinBoneIndices = authoredRig is null
                    ? surface.PaletteBoneIndices.ToArray()
                    : authoredRig.Surfaces[surfaceIndex].PhysicalPalette.ToArray(),
                BaseColorTexture = baseColor,
                Tint = baseColor is null
                    ? new Vector4(0.66f, 0.69f, 0.72f, 1.0f)
                    : Vector4.One,
                MorphTargets = morphTargets,
            });
        }

        return new PreparedMeshSet(
            meshes.MoveToImmutable(),
            diagnostics.ToImmutable());
    }

    private static bool IsRecoverableDl1PreviewFailure(Exception exception) =>
        exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            KeyNotFoundException or
            NotSupportedException or
            OverflowException or
            IndexOutOfRangeException;

    private static MeshVertex ToRenderVertex(
        FbxModelVertex vertex,
        bool flipTextureCoordinateV)
    {
        float[] weights = new float[4];
        float[] indices = new float[4];
        int count = Math.Min(
            4,
            Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length));
        for (int index = 0; index < count; index++)
        {
            indices[index] = vertex.BoneIndices[index];
            weights[index] = checked((float)vertex.BoneWeights[index]);
        }

        return new MeshVertex(
            new Vector3(
                checked((float)vertex.Position.X),
                checked((float)vertex.Position.Y),
                checked((float)vertex.Position.Z)),
            new Vector3(
                checked((float)vertex.Normal.X),
                checked((float)vertex.Normal.Y),
                checked((float)vertex.Normal.Z)),
            new Vector2(
                checked((float)vertex.TextureCoordinateU),
                checked((float)(flipTextureCoordinateV
                    ? 1.0 - vertex.TextureCoordinateV
                    : vertex.TextureCoordinateV))),
            new Vector4(weights[0], weights[1], weights[2], weights[3]),
            new Vector4(indices[0], indices[1], indices[2], indices[3]));
    }

    private static MorphTargetRenderData ToRenderMorphTarget(
        FbxModelSurface surface,
        FbxModelMorphTarget target)
    {
        if (target.PositionDeltas.Length != surface.Vertices.Length ||
            target.PositionDeltas.Any(static delta => !delta.IsFinite) ||
            (!target.NormalDeltas.IsDefaultOrEmpty &&
             (target.NormalDeltas.Length != surface.Vertices.Length || target.NormalDeltas.Any(static delta => !delta.IsFinite))))
        {
            throw new InvalidDataException(
                $"Morph target '{target.Name}' does not match surface '{surface.Id}'s expanded vertex buffer.");
        }

        Vector3[] positions = target.PositionDeltas
            .Select(static delta => new Vector3(
                checked((float)delta.X),
                checked((float)delta.Y),
                checked((float)delta.Z)))
            .ToArray();
        return new MorphTargetRenderData(
            target.Name,
            positions,
            target.NormalDeltas.IsDefaultOrEmpty ? ReadOnlyMemory<Vector3>.Empty : target.NormalDeltas
                .Select(static delta => new Vector3(checked((float)delta.X), checked((float)delta.Y), checked((float)delta.Z))).ToArray());
    }

    private static TextureRenderData? TryDecodeTexture(
        CustomModelPackage package,
        CustomModelTextureBinding binding,
        ImmutableArray<string>.Builder diagnostics)
    {
        if (binding.PackageEntryPath is not { } entryPath ||
            !package.TexturePayloads.TryGetValue(entryPath, out ImmutableArray<byte> payload))
        {
            diagnostics.Add(
                $"Material texture '{binding.DisplayName}' has no embedded or selected bytes; the mesh uses neutral shading.");
            return null;
        }

        if (CustomModelTextureDecoder.TryReadLegacyDdsPassthrough(
                payload.AsSpan(),
                binding.DisplayName,
                out CustomModelLegacyDdsInfo legacy,
                out _))
        {
            (TextureRenderFormat format, int blockBytes) = legacy.Format switch
            {
                BCnEncoder.Shared.CompressionFormat.Bc1 => (TextureRenderFormat.Bc1Unorm, 8),
                BCnEncoder.Shared.CompressionFormat.Bc2 => (TextureRenderFormat.Bc2Unorm, 16),
                BCnEncoder.Shared.CompressionFormat.Bc3 => (TextureRenderFormat.Bc3Unorm, 16),
                _ => throw new InvalidDataException("Legacy DDS passthrough reported an unsupported format."),
            };
            int rowPitch = checked(Math.Max(1, (legacy.Width + 3) / 4) * blockBytes);
            return new TextureRenderData(
                $"custom:{binding.Id:N}",
                legacy.Width,
                legacy.Height,
                format,
                rowPitch,
                payload.AsSpan(128, legacy.BaseMipByteCount).ToArray());
        }

        if (!CustomModelTextureDecoder.TryDecode(
                payload.AsSpan(),
                binding.DisplayName,
                out CustomModelDecodedTexture? decoded,
                out string failureReason) ||
            decoded is null)
        {
            diagnostics.Add(
                $"{failureReason} Its original bytes remain packaged and the mesh uses neutral shading.");
            return null;
        }

        byte[] bgra = decoded.Rgba8.ToArray();
        for (int offset = 0; offset < bgra.Length; offset += 4)
        {
            (bgra[offset], bgra[offset + 2]) = (bgra[offset + 2], bgra[offset]);
        }

        return new TextureRenderData(
            $"custom:{binding.Id:N}",
            decoded.Width,
            decoded.Height,
            TextureRenderFormat.Bgra8Unorm,
            checked(decoded.Width * 4),
            bgra);
    }

    private sealed record PreparedMeshSet(
        ImmutableArray<MeshRenderData> Meshes,
        ImmutableArray<string> Diagnostics);
}
