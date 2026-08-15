using System.IO;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Samples the authoritative export pose of one active project variant into
/// the bounded, renderer-independent contract consumed by the Blender handoff.
/// </summary>
internal static class BlenderFbxActiveVariantEvaluator
{
    internal const long MaximumEvaluatedTransforms = 1_000_000;

    public static BlenderFbxEvaluatedClip Evaluate(
        ProjectAnimation animation,
        string sourceName,
        string sourceFingerprint,
        EvaluationRequest template,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(template);
        if (template.Purpose != EvaluationPurpose.Export ||
            template.PlaybackMode != PlaybackMode.Clamp)
        {
            throw new ArgumentException(
                "An active-variant FBX must use the authoritative clamped export evaluation path.",
                nameof(template));
        }

        if (!template.Attachments.IsEmpty ||
            !animation.Attachments.IsEmpty)
        {
            throw new InvalidOperationException(
                "Self-contained active-variant FBX export is blocked while the variant has attachments because attachment mesh/material payloads are not part of the Blender handoff contract.");
        }

        if (template.Clip.FrameRate != animation.FrameRate ||
            template.Clip.FrameCount != animation.FrameCount)
        {
            throw new InvalidDataException(
                "The loaded source/synchronized facial timeline no longer matches the active variant's persisted cadence.");
        }

        if (animation.FrameCount > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Active variant '{animation.Name}' has too many frames for a bounded Blender handoff.");
        }

        long transformCount = checked(
            animation.FrameCount * template.TargetRig.BoneCount);
        if (transformCount > MaximumEvaluatedTransforms)
        {
            throw new InvalidDataException(
                $"Active variant '{animation.Name}' requires {transformCount:N0} evaluated target transforms; the safety limit is {MaximumEvaluatedTransforms:N0}.");
        }

        var evaluator = new AnimationEvaluator();
        var frames = new BlenderFbxEvaluatedFrame[
            checked((int)animation.FrameCount)];
        for (var frameIndex = 0;
             frameIndex < frames.Length;
             frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double seconds = animation.FrameRate.SecondsForFrame(
                frameIndex);
            var request = new EvaluationRequest(
                template.SourceRig,
                template.TargetRig,
                template.Clip,
                seconds,
                template.PreviewProfile,
                template.RetargetMap,
                template.EditLayers,
                template.IkConstraints,
                template.PlaybackMode,
                template.Purpose,
                template.Attachments,
                template.Dl1AuthoringPolicy,
                template.MorphBindings,
                template.MorphEditLayers,
                template.IkLayers,
                template.Dl1PreviewInputs,
                template.PreviewMotionAccumulationEnabled,
                template.DirectRigBinding);
            EvaluationFrame evaluated = evaluator.Evaluate(request);
            if (evaluated.Diagnostics.Any(static diagnostic =>
                    diagnostic.Severity ==
                    EvaluationDiagnosticSeverity.Error))
            {
                string detail = string.Join(
                    "; ",
                    evaluated.Diagnostics
                        .Where(static diagnostic =>
                            diagnostic.Severity ==
                            EvaluationDiagnosticSeverity.Error)
                        .Select(static diagnostic =>
                            $"{diagnostic.Code}: {diagnostic.Message}"));
                throw new InvalidDataException(
                    $"Active variant '{animation.Name}' produced an export-evaluation error at frame {frameIndex:N0}: {detail}");
            }

            if (evaluated.AuthoredPose.Rig.BoneCount !=
                template.TargetRig.BoneCount)
            {
                throw new InvalidDataException(
                    $"Active variant '{animation.Name}' produced a pose for the wrong target rig at frame {frameIndex:N0}.");
            }

            frames[frameIndex] = new BlenderFbxEvaluatedFrame(
                evaluated.AuthoredPose.LocalTransforms,
                evaluated.AuthoredMorphWeights);
        }

        return new BlenderFbxEvaluatedClip(
            animation.Name,
            sourceName,
            sourceFingerprint,
            animation.FrameRate,
            frames);
    }
}
