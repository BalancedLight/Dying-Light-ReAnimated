using System.IO;
using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.App.Infrastructure;

internal sealed record AuthoritativeRootMotionTrailRequest(
    RigDefinition SourceRig,
    RigDefinition TargetRig,
    AnimationClip Clip,
    RetargetMap? Mapping,
    IReadOnlyList<BoneEditLayer> EditLayers,
    IReadOnlyList<AttachmentBinding> Attachments,
    Dl1AuthoringPolicy AuthoringPolicy,
    IReadOnlyList<MorphChannelBinding> MorphBindings,
    IReadOnlyList<MorphEditLayer> MorphEditLayers,
    IReadOnlyList<IkConstraintLayer> IkLayers,
    int SampleCount,
    bool PreviewMotionAccumulationEnabled = false);

/// <summary>
/// Samples root positions from the same authored/export evaluation path used
/// by ANM2 output. Raw/export purpose deliberately excludes every preview-only
/// DL1 procedural layer.
/// </summary>
internal static class AuthoritativeRootMotionTrailSampler
{
    public const int MaximumSampleCount = 2_048;

    public static Vector3[] Evaluate(
        AuthoritativeRootMotionTrailRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceRig);
        ArgumentNullException.ThrowIfNull(request.TargetRig);
        ArgumentNullException.ThrowIfNull(request.Clip);
        ArgumentNullException.ThrowIfNull(request.EditLayers);
        ArgumentNullException.ThrowIfNull(request.Attachments);
        ArgumentNullException.ThrowIfNull(request.AuthoringPolicy);
        ArgumentNullException.ThrowIfNull(request.MorphBindings);
        ArgumentNullException.ThrowIfNull(request.MorphEditLayers);
        ArgumentNullException.ThrowIfNull(request.IkLayers);
        request.AuthoringPolicy.ValidateFor(
            request.SourceRig,
            request.TargetRig);

        if (request.SampleCount is < 1 or > MaximumSampleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.SampleCount,
                $"Root-motion trails require 1-{MaximumSampleCount:N0} samples.");
        }

        var positions = new Vector3[request.SampleCount];
        var sampleTimesSeconds = new double[request.SampleCount];
        for (int index = 0; index < positions.Length; index++)
        {
            double frame = positions.Length == 1
                ? 0.0
                : (double)index *
                (request.Clip.FrameCount - 1) /
                (positions.Length - 1);
            sampleTimesSeconds[index] =
                request.Clip.FrameRate.SecondsForFrame(frame);
        }

        var evaluationRequest = new EvaluationRequest(
            request.SourceRig,
            request.TargetRig,
            request.Clip,
            0.0,
            PreviewProfile.RawAuthoring,
            request.Mapping,
            request.EditLayers,
            playbackMode: PlaybackMode.Clamp,
            purpose: EvaluationPurpose.Export,
            attachments: request.Attachments,
            dl1AuthoringPolicy: request.AuthoringPolicy,
            morphBindings: request.MorphBindings,
            morphEditLayers: request.MorphEditLayers,
            ikLayers: request.IkLayers,
            dl1PreviewInputs: Dl1PreviewInputs.Empty);
        Dl1RootMotionPolicy rootMotion =
            request.AuthoringPolicy.RootMotion;
        AuxiliaryTransformTrack? auxiliaryMotionTrack =
            request.PreviewMotionAccumulationEnabled
                ? request.Clip.AuxiliaryTransformTracks.FirstOrDefault(
                    static track => track.Descriptor ==
                        Dl1RootMotionPolicy.MotionAccumulatorDescriptor)
                : null;
        AnimationEvaluator.EvaluateAuthoredPoseBatch(
            evaluationRequest,
            sampleTimesSeconds,
            (index, authoredPose) =>
            {
                Vector3D translation =
                    authoredPose
                        .GlobalMatrices[rootMotion.TargetRootBoneIndex]
                        .Translation;
                if (rootMotion.Mode ==
                        AnimationRootMode.MotionAccumulator &&
                    rootMotion.MotionAccumulatorBoneIndex is
                        int accumulatorBoneIndex)
                {
                    translation +=
                        authoredPose
                            .GlobalMatrices[accumulatorBoneIndex]
                            .Translation;
                }

                if (auxiliaryMotionTrack is not null)
                {
                    double sampleFrame = request.Clip.ResolveFrame(
                        sampleTimesSeconds[index],
                        PlaybackMode.Clamp);
                    translation = ActorMotionEvaluator.Evaluate(
                            auxiliaryMotionTrack,
                            sampleFrame,
                            rootMotion.WorldUpAxis)
                        .TransformPoint(translation);
                }

                positions[index] = new Vector3(
                    checked((float)translation.X),
                    checked((float)translation.Y),
                    checked((float)translation.Z));
                if (!IsFinite(positions[index]))
                {
                    throw new InvalidDataException(
                        $"Root-motion sample {index:N0} is not finite.");
                }

                if ((index & 31) == 0 ||
                    index == positions.Length - 1)
                {
                    progress?.Report(
                        100.0 * (index + 1) /
                        positions.Length);
                }
            },
            cancellationToken);

        return positions;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
