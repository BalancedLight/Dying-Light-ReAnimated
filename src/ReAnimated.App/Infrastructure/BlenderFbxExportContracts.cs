using ReAnimated.Core.Domain;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public sealed record BlenderFbxAssetIdentity(
    string StableKey,
    string ProviderId,
    string ResourceName,
    string ContentFingerprint);

public sealed record BlenderFbxExportRequest(
    string BlenderExecutablePath,
    string OutputFbxPath,
    BlenderFbxAssetIdentity Asset,
    RigDefinition? Rig,
    IReadOnlyList<MeshRenderData> Meshes,
    IReadOnlyList<string> Anm2Paths)
{
    /// <summary>
    /// Embeds decoded base-color images in the binary FBX instead of
    /// committing loose DDS dependencies next to it.
    /// </summary>
    public bool EmbedTextures { get; init; }
}

public sealed record BlenderFbxExportProgress(
    string Stage,
    double Percent,
    string Detail);

public sealed record BlenderFbxExportResult(
    string OutputFbxPath,
    string HandoffManifestPath,
    IReadOnlyList<string> TexturePaths,
    IReadOnlyList<string> AnimationStacks,
    int BoneCount,
    int MeshCount,
    IReadOnlyList<string> Warnings,
    string BlenderLog)
{
    public IReadOnlyList<string> HelperSidecarPaths { get; init; } =
        Array.Empty<string>();

    public bool TexturesEmbedded { get; init; }

    public IReadOnlyList<string> EmbeddedTextureFileNames { get; init; } =
        Array.Empty<string>();
}

public interface IBlenderFbxExportService
{
    Task<BlenderFbxExportResult> ExportAsync(
        BlenderFbxExportRequest request,
        IProgress<BlenderFbxExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record BlenderProcessRequest(
    string BlenderExecutablePath,
    string HelperScriptPath,
    string JobPath,
    TimeSpan Timeout);

public sealed record BlenderProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string CombinedLog =>
        string.Join(
            Environment.NewLine,
            new[] { StandardOutput, StandardError }
                .Where(static value =>
                    !string.IsNullOrWhiteSpace(value)));
}

public interface IBlenderProcessRunner
{
    Task<BlenderProcessResult> RunAsync(
        BlenderProcessRequest request,
        Action<string>? outputLine,
        CancellationToken cancellationToken);
}

public interface IBlenderFbxOutputValidator
{
    Task ValidateAsync(
        string outputFbxPath,
        IReadOnlyList<BlenderFbxJobBone> expectedBones,
        IReadOnlyList<BlenderFbxJobClip> expectedClips,
        IReadOnlyList<BlenderFbxJobMesh> expectedMeshes,
        IReadOnlyList<BlenderFbxJobTexture> expectedTextures,
        CancellationToken cancellationToken);
}

public sealed record BlenderFbxJob(
    string Format,
    int SchemaVersion,
    string OutputPath,
    double FbxOutputFps,
    string Fidelity,
    BlenderFbxJobAsset Asset,
    IReadOnlyList<BlenderFbxJobBone> Bones,
    IReadOnlyList<BlenderFbxJobMesh> Meshes,
    IReadOnlyList<BlenderFbxJobTexture> Textures,
    IReadOnlyList<BlenderFbxJobClip> Clips,
    IReadOnlyList<string> Warnings)
{
    public bool EmbedTextures { get; init; }
}

public sealed record BlenderFbxJobAsset(
    string StableKey,
    string ProviderId,
    string ResourceName,
    string ContentFingerprint);

public sealed record BlenderFbxJobBone(
    int Index,
    string Name,
    int ParentIndex,
    uint? Descriptor,
    IReadOnlyList<double> BindTranslation,
    IReadOnlyList<double> BindRotationWxyz,
    IReadOnlyList<double> BindScale,
    bool Root,
    bool Deform,
    bool Helper,
    string Semantic);

public sealed record BlenderFbxJobMesh(
    string Name,
    string BinaryPath,
    int VertexCount,
    int IndexCount,
    int VertexStrideFloats,
    bool Skinned,
    IReadOnlyList<float> LocalToWorld,
    string? TextureKey);

public sealed record BlenderFbxJobTexture(
    string Key,
    string ResourceName,
    string FilePath,
    string FileName,
    int Width,
    int Height,
    string Format,
    bool EmbeddedInFbx = false);

public sealed record BlenderFbxJobClip(
    string ActionName,
    string SourceFileName,
    string SourceSha256,
    string TimingMetadataStatus,
    double Anm2InputFps,
    double FbxOutputFps,
    int SourceFrameCount,
    int FbxFrameCount,
    string BinaryPath,
    IReadOnlyList<uint> SourceDescriptors,
    IReadOnlyList<BlenderFbxJobHelperTrack> HelperTracks,
    BlenderFbxJobMotionAccumulator MotionAccumulator);

public sealed record BlenderFbxJobHelperTrack(
    uint Descriptor,
    string NodeName,
    string Semantic,
    string? SidecarFile = null,
    string? SidecarSha256 = null,
    int? SidecarTrackIndex = null,
    int? FrameCount = null,
    double? SampleFps = null,
    string? Encoding = null);

public sealed record BlenderFbxJobMotionAccumulator(
    bool Present,
    bool Active,
    bool BakedIntoRoot,
    string? RootName);

public sealed record BlenderFbxHandoffManifest(
    string Format,
    int SchemaVersion,
    string Fidelity,
    string RedistributionWarning,
    BlenderFbxJobAsset Asset,
    string FbxFile,
    IReadOnlyList<string> TextureFiles,
    IReadOnlyList<BlenderFbxHandoffClip> Clips,
    string BasisMode,
    string BindPoseMode,
    IReadOnlyList<string> Limitations)
{
    public bool TexturesEmbedded { get; init; }

    public IReadOnlyList<string> EmbeddedTextureFiles { get; init; } =
        Array.Empty<string>();
}

public sealed record BlenderFbxHandoffClip(
    string ActionName,
    string SourceFileName,
    string SourceSha256,
    string TimingMetadataStatus,
    double Anm2InputFps,
    double FbxOutputFps,
    int SourceFrameCount,
    int FbxFrameCount,
    IReadOnlyList<uint> SourceDescriptors,
    IReadOnlyList<BlenderFbxJobHelperTrack> HelperTracks,
    BlenderFbxJobMotionAccumulator MotionAccumulator);
