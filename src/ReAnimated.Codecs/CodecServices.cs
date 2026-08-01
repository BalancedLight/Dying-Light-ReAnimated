using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Fed;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Codecs;

/// <summary>
/// UI-facing boundary for binary FBX import. The decoder owns parsing and
/// semantic evaluation so presentation code never depends on FBX nodes.
/// </summary>
public interface IFbxAnimationDecoder
{
    FbxCoreAnimationImportResult Decode(
        ReadOnlyMemory<byte> payload,
        FbxCoreAnimationImportOptions? options = null,
        FbxReadLimits? limits = null);

    Task<FbxCoreAnimationImportResult> DecodeFileAsync(
        string path,
        FbxCoreAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default);
}

public sealed class FbxAnimationDecoder : IFbxAnimationDecoder
{
    public FbxCoreAnimationImportResult Decode(
        ReadOnlyMemory<byte> payload,
        FbxCoreAnimationImportOptions? options = null,
        FbxReadLimits? limits = null)
    {
        FbxBinaryDocument document = FbxBinaryReader.ReadWithOptions(
            payload.Span,
            FbxReadOptions.Animation,
            limits);
        return FbxCoreAnimationAdapter.Import(document, options);
    }

    public async Task<FbxCoreAnimationImportResult> DecodeFileAsync(
        string path,
        FbxCoreAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        FbxBinaryDocument document = await FbxBinaryReader.ReadFileWithOptionsAsync(
            path,
            FbxReadOptions.Animation,
            limits,
            cancellationToken).ConfigureAwait(false);
        return FbxCoreAnimationAdapter.Import(
            document,
            options,
            cancellationToken);
    }
}

/// <summary>
/// UI-facing boundary for bounded FBX BlendShapeChannel discovery and
/// DeformPercent sampling. Mesh and shape-delta payloads remain outside this
/// animation-domain decode.
/// </summary>
public interface IFbxFacialAnimationDecoder
{
    FbxFacialAnimationImportResult Decode(
        ReadOnlyMemory<byte> payload,
        FbxFacialAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default);

    Task<FbxFacialAnimationImportResult> DecodeFileAsync(
        string path,
        FbxFacialAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default);
}

public sealed class FbxFacialAnimationDecoder :
    IFbxFacialAnimationDecoder
{
    public FbxFacialAnimationImportResult Decode(
        ReadOnlyMemory<byte> payload,
        FbxFacialAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        FbxBinaryDocument document = FbxBinaryReader.ReadWithOptions(
            payload.Span,
            FbxReadOptions.Animation,
            limits,
            cancellationToken);
        return FbxFacialAnimationAdapter.Import(
            document,
            options,
            cancellationToken);
    }

    public async Task<FbxFacialAnimationImportResult> DecodeFileAsync(
        string path,
        FbxFacialAnimationImportOptions? options = null,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        FbxBinaryDocument document =
            await FbxBinaryReader.ReadFileWithOptionsAsync(
                path,
                FbxReadOptions.Animation,
                limits,
                cancellationToken).ConfigureAwait(false);
        return FbxFacialAnimationAdapter.Import(
            document,
            options,
            cancellationToken);
    }
}

/// <summary>
/// Stable boundary for DL1 ANM2 container decoding.
/// </summary>
public interface IAnm2Decoder
{
    Anm2Clip Decode(
        ReadOnlyMemory<byte> payload,
        string name = "",
        int maximumPayloadBytes = Anm2Reader.DefaultMaximumPayloadBytes);

    Task<Anm2Clip> DecodeFileAsync(
        string path,
        int maximumPayloadBytes = Anm2Reader.DefaultMaximumPayloadBytes,
        CancellationToken cancellationToken = default);
}

public sealed class Anm2Decoder : IAnm2Decoder
{
    public Anm2Clip Decode(
        ReadOnlyMemory<byte> payload,
        string name = "",
        int maximumPayloadBytes = Anm2Reader.DefaultMaximumPayloadBytes) =>
        Anm2Reader.Read(payload.Span, name, maximumPayloadBytes);

    public Task<Anm2Clip> DecodeFileAsync(
        string path,
        int maximumPayloadBytes = Anm2Reader.DefaultMaximumPayloadBytes,
        CancellationToken cancellationToken = default) =>
        Anm2Reader.ReadFileAsync(path, maximumPayloadBytes, cancellationToken);
}

/// <summary>
/// Stable boundary for DL1 facial-expression descriptor decoding.
/// </summary>
public interface IFedDecoder
{
    FedDocument Decode(Stream stream, string name, FedLimits? limits = null);

    FedDocument DecodeFile(string path, FedLimits? limits = null);
}

public sealed class FedDecoder : IFedDecoder
{
    public FedDocument Decode(
        Stream stream,
        string name,
        FedLimits? limits = null) =>
        FedReader.Read(stream, name, limits);

    public FedDocument DecodeFile(string path, FedLimits? limits = null) =>
        FedReader.Read(path, limits);
}

/// <summary>
/// Opens bounded RP6L archives without leaking archive construction into UI
/// or workflow code. Resource streams remain streaming and cancellable.
/// </summary>
public interface IRp6lArchiveDecoder
{
    Task<Rp6lArchive> OpenAsync(
        string path,
        Rp6lLimits? limits = null,
        CancellationToken cancellationToken = default);
}

public sealed class Rp6lArchiveDecoder : IRp6lArchiveDecoder
{
    public Task<Rp6lArchive> OpenAsync(
        string path,
        Rp6lLimits? limits = null,
        CancellationToken cancellationToken = default) =>
        Rp6lArchive.OpenAsync(path, limits, cancellationToken);
}
