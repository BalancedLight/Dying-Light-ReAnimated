using System.Collections.Immutable;
using System.IO;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed record FacialFbxProjectReviewImportResult(
    ProjectAnimation UpdatedAnimation,
    AnimationClip SourceClip,
    int SourceChannelCount,
    int SuggestedBindingCount,
    ImmutableArray<string> UnmappedAnimatedChannels);

/// <summary>
/// WPF boundary for importing and reopening one facial FBX take. The sampled
/// scalar clip is retained beside its review state so preview and export share
/// the same authoritative body timeline.
/// </summary>
public interface IFacialFbxProjectReviewImporter
{
    Task<FacialFbxProjectReviewImportResult> ImportAsync(
        string sourcePath,
        ProjectMorphSourceValueUnit sourceValueUnit,
        ProjectAnimation bodyAnimation,
        RigDefinition exactTargetRig,
        CancellationToken cancellationToken = default);

    Task<AnimationClip> DecodeSourceAsync(
        string sourcePath,
        ProjectMorphSourceValueUnit sourceValueUnit,
        ProjectAnimation bodyAnimation,
        CancellationToken cancellationToken = default);
}

public sealed class FacialFbxProjectReviewImporter :
    IFacialFbxProjectReviewImporter
{
    private readonly IFbxFacialAnimationDecoder _decoder;

    public FacialFbxProjectReviewImporter(
        IFbxFacialAnimationDecoder? decoder = null)
    {
        _decoder = decoder ?? new FbxFacialAnimationDecoder();
    }

    public async Task<FacialFbxProjectReviewImportResult> ImportAsync(
        string sourcePath,
        ProjectMorphSourceValueUnit sourceValueUnit,
        ProjectAnimation bodyAnimation,
        RigDefinition exactTargetRig,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(bodyAnimation);
        ArgumentNullException.ThrowIfNull(exactTargetRig);
        if (!Enum.IsDefined(sourceValueUnit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceValueUnit));
        }

        FbxFacialAnimationImportResult import =
            await DecodeImportAsync(
                    sourcePath,
                    sourceValueUnit,
                    bodyAnimation,
                    cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        FbxFacialProjectReview review =
            FbxFacialProjectReviewService.Create(
                new FbxFacialProjectReviewRequest
                {
                    SourcePath = sourcePath,
                    Import = import,
                    BodyTiming = new AnimationTiming(
                        bodyAnimation.FrameRate,
                        bodyAnimation.FrameCount),
                    Profile = profile,
                    ExactTargetRig = exactTargetRig,
                },
                cancellationToken);

        return new FacialFbxProjectReviewImportResult(
            review.ApplyTo(bodyAnimation),
            import.Clip,
            review.SourceChannels.Length,
            review.SuggestedBindings.Length,
            review.UnmappedAnimatedChannels);
    }

    public async Task<AnimationClip> DecodeSourceAsync(
        string sourcePath,
        ProjectMorphSourceValueUnit sourceValueUnit,
        ProjectAnimation bodyAnimation,
        CancellationToken cancellationToken = default)
    {
        FbxFacialAnimationImportResult import =
            await DecodeImportAsync(
                    sourcePath,
                    sourceValueUnit,
                    bodyAnimation,
                    cancellationToken)
                .ConfigureAwait(false);
        AnimationTiming timing = new(
            bodyAnimation.FrameRate,
            bodyAnimation.FrameCount);
        if (!timing.IsCompatibleWith(import.Clip))
        {
            throw new InvalidDataException(
                $"FBX facial take '{import.Clip.Name}' does not match the " +
                "saved body animation's rational frame rate and frame count.");
        }

        return import.Clip;
    }

    private async Task<FbxFacialAnimationImportResult> DecodeImportAsync(
        string sourcePath,
        ProjectMorphSourceValueUnit sourceValueUnit,
        ProjectAnimation bodyAnimation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(bodyAnimation);
        if (!Enum.IsDefined(sourceValueUnit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceValueUnit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        FbxFacialSourceValueUnit fbxUnit = sourceValueUnit switch
        {
            ProjectMorphSourceValueUnit.Normalized =>
                FbxFacialSourceValueUnit.Normalized,
            ProjectMorphSourceValueUnit.Percent =>
                FbxFacialSourceValueUnit.Percent,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceValueUnit)),
        };
        return await _decoder.DecodeFileAsync(
                sourcePath,
                new FbxFacialAnimationImportOptions
                {
                    SamplingFrameRate = bodyAnimation.FrameRate,
                    DefaultSourceValueUnit = fbxUnit,
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
