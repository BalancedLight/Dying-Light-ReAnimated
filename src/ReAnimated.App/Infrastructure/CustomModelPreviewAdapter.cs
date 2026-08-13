using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        SkeletonPose previewPose = _authoredRig?.RebasePose(sourcePose) ?? sourcePose;
        int? previewSelectedBone = _authoredRig is null || selectedBoneIndex is not { } sourceIndex
            ? selectedBoneIndex
            : (uint)sourceIndex < (uint)_authoredRig.Contract.SourceToPhysicalIndices.Length
                ? _authoredRig.Contract.SourceToPhysicalIndices[sourceIndex]
                : null;
        return CorePreviewAdapter.ToRenderSkeleton(previewPose, previewSelectedBone);
    }

    public CustomModelPreviewPayload CreatePayload(
        AnimationClip? clip,
        int frame,
        int? selectedBoneIndex = null) =>
        new(
            Meshes,
            CreateSkeleton(clip, frame, selectedBoneIndex),
            Diagnostics);
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

        try
        {
            if (binding.MediaType.Equals("image/vnd-ms.dds", StringComparison.OrdinalIgnoreCase) ||
                payload.AsSpan().StartsWith("DDS "u8))
            {
                return DecodeDds(binding, payload.AsSpan());
            }

            return DecodeBitmap(binding, payload);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FileFormatException or
            InvalidDataException or
            NotSupportedException or
            OverflowException)
        {
            diagnostics.Add(
                $"Texture '{binding.DisplayName}' could not be decoded for preview ({exception.Message}); its original bytes remain packaged.");
            return null;
        }
    }

    private static TextureRenderData DecodeBitmap(
        CustomModelTextureBinding binding,
        ImmutableArray<byte> payload)
    {
        TextureRenderData? decoded = null;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                using var stream = new MemoryStream(payload.ToArray(), writable: false);
                BitmapDecoder decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                BitmapSource source = decoder.Frames[0];
                var converted = new FormatConvertedBitmap(
                    source,
                    PixelFormats.Bgra32,
                    null,
                    0.0);
                int rowPitch = checked(converted.PixelWidth * 4);
                byte[] pixels = new byte[checked(rowPitch * converted.PixelHeight)];
                converted.CopyPixels(pixels, rowPitch, 0);
                decoded = new TextureRenderData(
                    $"custom:{binding.Id:N}",
                    converted.PixelWidth,
                    converted.PixelHeight,
                    TextureRenderFormat.Bgra8Unorm,
                    rowPitch,
                    pixels);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true,
            Name = "DL ReAnimated custom texture decoder",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }

        return decoded ?? throw new InvalidDataException(
            "The image decoder produced no bitmap.");
    }

    private static TextureRenderData DecodeDds(
        CustomModelTextureBinding binding,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 128 ||
            !payload[..4].SequenceEqual("DDS "u8) ||
            ReadUInt32(payload, 4) != 124 ||
            ReadUInt32(payload, 76) != 32)
        {
            throw new InvalidDataException("DDS header is invalid or truncated.");
        }

        int height = checked((int)ReadUInt32(payload, 12));
        int width = checked((int)ReadUInt32(payload, 16));
        uint fourCc = ReadUInt32(payload, 84);
        (TextureRenderFormat format, int blockBytes) = fourCc switch
        {
            0x3154_5844 => (TextureRenderFormat.Bc1Unorm, 8),  // DXT1
            0x3354_5844 => (TextureRenderFormat.Bc2Unorm, 16), // DXT3
            0x3554_5844 => (TextureRenderFormat.Bc3Unorm, 16), // DXT5
            _ => throw new NotSupportedException(
                $"DDS FourCC 0x{fourCc:X8} is not a supported BC1/BC2/BC3 authoring preview."),
        };
        if (width <= 0 || height <= 0 || width > 32_768 || height > 32_768)
        {
            throw new InvalidDataException("DDS dimensions are outside the bounded preview contract.");
        }

        int rowPitch = checked(Math.Max(1, (width + 3) / 4) * blockBytes);
        int byteCount = checked(rowPitch * Math.Max(1, (height + 3) / 4));
        if (payload.Length < 128 + byteCount)
        {
            throw new InvalidDataException("DDS base mip is truncated.");
        }

        return new TextureRenderData(
            $"custom:{binding.Id:N}",
            width,
            height,
            format,
            rowPitch,
            payload.Slice(128, byteCount).ToArray());
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));

    private sealed record PreparedMeshSet(
        ImmutableArray<MeshRenderData> Meshes,
        ImmutableArray<string> Diagnostics);
}
