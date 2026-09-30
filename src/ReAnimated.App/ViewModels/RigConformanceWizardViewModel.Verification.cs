using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

/// <summary>
/// What a stock DL1 clip actually does on the conformed rig.
/// </summary>
/// <remarks>
/// Descriptor coverage is the check that matters: Chrome resolves animation
/// tracks by name hash, so an unresolved descriptor is a bone the game will not
/// drive. Vertex displacement then answers the separate question of whether the
/// mesh survives the pose.
/// </remarks>
public sealed record RigConformanceVerification
{
    public required string ClipName { get; init; }

    public required int TotalDescriptors { get; init; }

    public required int ResolvedDescriptors { get; init; }

    /// <summary>Motion accumulator and other auxiliary tracks are reported separately from rig coverage.</summary>
    public required int AuxiliaryDescriptorCount { get; init; }

    public required ImmutableArray<string> UnmappedDescriptors { get; init; }

    /// <summary>
    /// Bones the clip addressed but left at bind because it carried no usable
    /// track for them.
    /// </summary>
    public required int BindFallbackBones { get; init; }

    public required int FrameCount { get; init; }

    /// <summary>
    /// Largest distance any vertex travels from its bind position over the
    /// sampled frames. This includes ordinary movement and is not a stretch
    /// measure by itself.
    /// </summary>
    public required double MaximumVertexDisplacementCentimetres { get; init; }

    /// <summary>
    /// Largest connected deform-bone length change in the raw stock-clip
    /// preview. Native BSCR component masks are not applied by this check.
    /// </summary>
    public required double MaximumSegmentLengthDriftPercent { get; init; }

    public string? WorstSegmentName { get; init; }

    public required int MeasuredSegmentCount { get; init; }

    public required int SampledFrameCount { get; init; }

    /// <summary>
    /// Optional authoring comparison after reviewed BSCR POS/ROT/SCL masks.
    /// This is not a measurement of native Player animation composition.
    /// </summary>
    public double? ReviewedPolicySegmentLengthDriftPercent { get; init; }

    public string? ReviewedPolicyWorstSegmentName { get; init; }

    public int? ReviewedPolicyMeasuredSegmentCount { get; init; }

    public double? ReviewedPolicyVertexDisplacementCentimetres { get; init; }

    public string? ReviewedPolicyUnavailableReason { get; init; }

    public required ImmutableArray<string> PreparerDiagnostics { get; init; }

    public double Coverage => TotalDescriptors == 0
        ? 0.0
        : (double)ResolvedDescriptors / TotalDescriptors;

    public bool IsFullyResolved =>
        TotalDescriptors > 0 && ResolvedDescriptors == TotalDescriptors;

    private string SegmentDriftSummary => MeasuredSegmentCount == 0
        ? "connected deform-segment drift unavailable"
        : $"raw-preview peak segment-length drift {MaximumSegmentLengthDriftPercent:F1}%" +
          (WorstSegmentName is null ? "" : $" at {WorstSegmentName}") +
          $" across {MeasuredSegmentCount} connected deform segments";

    private string PolicyComparisonSummary => ReviewedPolicySegmentLengthDriftPercent is { } drift
        ? (ReviewedPolicyMeasuredSegmentCount > 0
            ? $" Reviewed BSCR-mask preview peak connected deform-segment drift {drift:F1}%" +
              (ReviewedPolicyWorstSegmentName is null ? "" : $" at {ReviewedPolicyWorstSegmentName}")
            : " Reviewed BSCR-mask preview connected deform-segment drift unavailable") +
          $"; peak vertex travel {ReviewedPolicyVertexDisplacementCentimetres.GetValueOrDefault():F1} cm. Helper motion, mesh distortion, native LOD, blending and runtime composition require separate review."
        : $" Reviewed BSCR-mask comparison unavailable: {ReviewedPolicyUnavailableReason ?? "no reviewed policy"}.";

    public string Summary =>
        $"{ClipName}: {ResolvedDescriptors} of {TotalDescriptors} bone/morph descriptors resolved ({Coverage:P0}); " +
        $"{AuxiliaryDescriptorCount} auxiliary track(s); " +
        $"{SegmentDriftSummary} over {SampledFrameCount} of {FrameCount} sampled frames; " +
        $"peak raw-preview vertex travel {MaximumVertexDisplacementCentimetres:F1} cm." + PolicyComparisonSummary;
}

public sealed partial class RigConformanceWizardViewModel
{
    /// <summary>
    /// Frames sampled when measuring displacement. A clip is checked at a
    /// spread of poses rather than every frame so verification stays bounded on
    /// long clips.
    /// </summary>
    private const int DisplacementSampleCount = 12;

    private Func<CancellationToken, Task<Dl1RetailAnimationPayload?>>? _pickRetailClip;

    /// <summary>
    /// Supplies the retail-clip picker. Left unset the wizard still conforms
    /// and applies; only the playback check is unavailable.
    /// </summary>
    public void SetRetailClipPicker(
        Func<CancellationToken, Task<Dl1RetailAnimationPayload?>>? picker)
    {
        _pickRetailClip = picker;
        OnPropertyChanged(nameof(CanVerifyWithRetailClip));
        VerifyWithRetailClipCommand.NotifyCanExecuteChanged();
    }

    public bool CanVerifyWithRetailClip => _pickRetailClip is not null && CanApply;

    /// <summary>The most recent verification result, if any.</summary>
    public RigConformanceVerification? Verification { get; private set; }

    public string VerificationHeadline => Verification is { } result
        ? $"{result.ClipName} · {result.ResolvedDescriptors}/{result.TotalDescriptors} tracks matched" +
          (result.MeasuredSegmentCount > 0
              ? $" · {result.MaximumSegmentLengthDriftPercent:F1}% raw length drift"
              : string.Empty)
        : "No stock animation checked.";

    /// <summary>
    /// The conformed model produced by the last verification, ready to preview
    /// or commit without re-running the weight transfer.
    /// </summary>
    public FbxModelAuthoringImportResult? ConformedModel { get; private set; }

    /// <summary>The verified clip, rebound to the conformed rig for playback.</summary>
    public AnimationClip? VerificationClip { get; private set; }

    public IAsyncRelayCommand VerifyWithRetailClipCommand => _verifyCommand ??=
        new AsyncRelayCommand(
            VerifyWithRetailClipAsync,
            () => CanVerifyWithRetailClip && !IsBusy);

    private AsyncRelayCommand? _verifyCommand;

    /// <summary>
    /// Applies the current fit and reports how a chosen stock clip behaves on
    /// it. Every failure is reported rather than leaving a stale verification
    /// on screen.
    /// </summary>
    private async Task VerifyWithRetailClipAsync(CancellationToken cancellationToken)
    {
        if (_pickRetailClip is not { } picker ||
            _model is not { } model ||
            Fit is not { } fit)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Dl1RetailAnimationPayload? payload =
                await picker(cancellationToken).ConfigureAwait(true);
            if (payload is null)
            {
                return;
            }

            (RigConformanceVerification? verification, string status) =
                await Task.Run(
                    () => RunVerification(model, fit, payload, cancellationToken),
                    cancellationToken).ConfigureAwait(true);
            Verification = verification;
            SolveStatus = status;
            _setStatus(status);
        }
        catch (OperationCanceledException)
        {
            SolveStatus = "Verification was cancelled.";
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException or
            ArgumentException)
        {
            Verification = null;
            SolveStatus = $"Verification failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Verification));
            OnPropertyChanged(nameof(VerificationHeadline));
            OnPropertyChanged(nameof(ConformedModel));
            OnPropertyChanged(nameof(VerificationClip));
            NotifyStateChanged();
            FitChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private (RigConformanceVerification? Verification, string Status) RunVerification(
        FbxModelAuthoringImportResult model,
        RigConformanceResult fit,
        Dl1RetailAnimationPayload payload,
        CancellationToken cancellationToken)
    {
        FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
            model,
            fit,
            CreateSettings(),
            cancellationToken);
        Dl1PreparedAuthoredRig prepared =
            Dl1CustomModelRigPreparer.Prepare(conformed, cancellationToken);
        RigDefinition rig = conformed.Rig
            ?? throw new InvalidDataException("The conformed model has no rig to verify.");

        Anm2DomainImportResult imported = Anm2DomainAdapter.ImportBody(
            payload.Clip,
            rig,
            payload.Timing.FrameRate);

        Anm2TrackPartition partition = imported.Partition ??
            throw new InvalidDataException("The stock clip has no descriptor partition for the fitted rig.");
        // A motion accumulator is not a bone. Counting it as a resolved rig
        // descriptor makes a completely incompatible character look nonzero.
        int resolved = partition.BodyDescriptors.Length + partition.MorphDescriptors.Length;
        int total = resolved + partition.UnresolvedDescriptors.Length;
        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            conformed, CustomModelPreviewMode.Dl1Output);
        PreviewMotionMetrics motion = MeasureMotion(session, imported.Clip, false, cancellationToken);
        PreviewMotionMetrics? reviewedMotion = null;
        string? policyUnavailable = null;
        try
        {
            reviewedMotion = MeasureMotion(session, imported.Clip, true, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
        {
            policyUnavailable = exception.Message;
        }

        ConformedModel = conformed;
        VerificationClip = imported.Clip;
        var verification = new RigConformanceVerification
        {
            ClipName = payload.Asset.DisplayName,
            TotalDescriptors = total,
            ResolvedDescriptors = resolved,
            AuxiliaryDescriptorCount = partition.AuxiliaryDescriptors.Length,
            UnmappedDescriptors = imported.UnmappedDescriptors
                .Select(static descriptor => $"0x{descriptor:X8}")
                .ToImmutableArray(),
            BindFallbackBones = imported.BindFallbackBoneIndices.Length,
            FrameCount = checked((int)Math.Min(int.MaxValue, imported.Clip.FrameCount)),
            MaximumVertexDisplacementCentimetres = motion.MaximumVertexDisplacementMetres * 100.0,
            MaximumSegmentLengthDriftPercent = motion.MaximumSegmentLengthDriftPercent,
            WorstSegmentName = motion.WorstSegmentName,
            MeasuredSegmentCount = motion.MeasuredSegmentCount,
            SampledFrameCount = motion.SampledFrameCount,
            ReviewedPolicySegmentLengthDriftPercent = reviewedMotion?.MaximumSegmentLengthDriftPercent,
            ReviewedPolicyWorstSegmentName = reviewedMotion?.WorstSegmentName,
            ReviewedPolicyMeasuredSegmentCount = reviewedMotion?.MeasuredSegmentCount,
            ReviewedPolicyVertexDisplacementCentimetres = reviewedMotion?.MaximumVertexDisplacementMetres * 100.0,
            ReviewedPolicyUnavailableReason = policyUnavailable,
            PreparerDiagnostics = prepared.Diagnostics
                .Select(static row => row.Message)
                .ToImmutableArray(),
        };

        return (verification, verification.Summary);
    }

    /// <summary>
    /// Measures ordinary vertex travel and connected-bone length drift across
    /// a bounded spread of raw or reviewed-policy preview frames.
    /// </summary>
    private static PreviewMotionMetrics MeasureMotion(
        CustomModelPreviewSession session,
        AnimationClip clip,
        bool reviewedPolicy,
        CancellationToken cancellationToken)
    {
        SkeletonRenderData? bindSkeleton = session.CreateSkeleton(null, 0, null);
        if (bindSkeleton is null || session.Meshes.IsDefaultOrEmpty)
        {
            return new PreviewMotionMetrics(0.0, 0.0, null, 0, 0);
        }

        var bindPositions = new Dictionary<int, CpuDeformedVertex[]>();
        for (int meshIndex = 0; meshIndex < session.Meshes.Length; meshIndex++)
        {
            bindPositions[meshIndex] = CpuMeshDeformationEvaluator.Evaluate(
                session.Meshes[meshIndex],
                bindSkeleton,
                [], cancellationToken);
        }

        long frameCount = Math.Max(1, clip.FrameCount);
        double maximum = 0.0;
        double maximumDrift = 0.0;
        string? worstSegment = null;
        int measuredSegments = 0;
        int samples = checked((int)Math.Min(frameCount, DisplacementSampleCount));
        for (int sample = 0; sample < samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frame = checked((int)Math.Min(
                frameCount - 1,
                (long)Math.Round(sample * (frameCount - 1.0) /
                    Math.Max(1, samples - 1))));
            SkeletonRenderData? posed = reviewedPolicy
                ? session.CreateReviewedPolicySkeleton(clip, frame)
                : session.CreateSkeleton(clip, frame, null);
            if (posed is null)
            {
                continue;
            }

            SkeletonSegmentLengthDriftReport segmentDrift =
                SkeletonSegmentLengthDriftEvaluator.Compare(bindSkeleton, posed);
            measuredSegments = segmentDrift.ComparableSegments;
            if (segmentDrift.MaximumAbsoluteDriftPercent > maximumDrift)
            {
                maximumDrift = segmentDrift.MaximumAbsoluteDriftPercent;
                worstSegment = segmentDrift.WorstBoneName;
            }

            for (int meshIndex = 0; meshIndex < session.Meshes.Length; meshIndex++)
            {
                CpuDeformedVertex[] bind = bindPositions[meshIndex];
                CpuDeformedVertex[] animated = CpuMeshDeformationEvaluator.Evaluate(
                    session.Meshes[meshIndex],
                    posed,
                    [], cancellationToken);
                int count = Math.Min(bind.Length, animated.Length);
                for (int vertex = 0; vertex < count; vertex++)
                {
                    float distance = Vector3.Distance(
                        bind[vertex].Position,
                        animated[vertex].Position);
                    if (float.IsFinite(distance) && distance > maximum)
                    {
                        maximum = distance;
                    }
                }
            }
        }

        return new PreviewMotionMetrics(maximum, maximumDrift, worstSegment, measuredSegments, samples);
    }

    private readonly record struct PreviewMotionMetrics(
        double MaximumVertexDisplacementMetres,
        double MaximumSegmentLengthDriftPercent,
        string? WorstSegmentName,
        int MeasuredSegmentCount,
        int SampledFrameCount);
}
