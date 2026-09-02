using System.Collections.Immutable;
using System.Globalization;
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

    public required ImmutableArray<string> UnmappedDescriptors { get; init; }

    /// <summary>
    /// Bones the clip addressed but left at bind because it carried no usable
    /// track for them.
    /// </summary>
    public required int BindFallbackBones { get; init; }

    public required int FrameCount { get; init; }

    /// <summary>
    /// Largest distance any vertex travels from its bind position over the
    /// sampled frames. Large values mean the clip is stretching the mesh.
    /// </summary>
    public required double MaximumVertexDisplacementCentimetres { get; init; }

    public required ImmutableArray<string> PreparerDiagnostics { get; init; }

    public double Coverage => TotalDescriptors == 0
        ? 0.0
        : (double)ResolvedDescriptors / TotalDescriptors;

    public bool IsFullyResolved =>
        TotalDescriptors > 0 && ResolvedDescriptors == TotalDescriptors;

    public string Summary => string.Create(
        CultureInfo.CurrentCulture,
        $"{ClipName}: {ResolvedDescriptors} of {TotalDescriptors} track descriptors resolved ({Coverage:P0}); " +
        $"peak vertex displacement {MaximumVertexDisplacementCentimetres:F1} cm over {FrameCount} frames.");
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

        int total = payload.Clip.TrackDescriptors.Length;
        int resolved = total - imported.UnmappedDescriptors.Length;
        double displacement = MeasureDisplacement(
            conformed,
            imported.Clip,
            cancellationToken);

        ConformedModel = conformed;
        VerificationClip = imported.Clip;
        var verification = new RigConformanceVerification
        {
            ClipName = payload.Asset.DisplayName,
            TotalDescriptors = total,
            ResolvedDescriptors = resolved,
            UnmappedDescriptors = imported.UnmappedDescriptors
                .Select(static descriptor => $"0x{descriptor:X8}")
                .ToImmutableArray(),
            BindFallbackBones = imported.BindFallbackBoneIndices.Length,
            FrameCount = checked((int)Math.Min(int.MaxValue, imported.Clip.FrameCount)),
            MaximumVertexDisplacementCentimetres = displacement * 100.0,
            PreparerDiagnostics = prepared.Diagnostics
                .Select(static row => row.Message)
                .ToImmutableArray(),
        };

        return (verification, verification.Summary);
    }

    /// <summary>
    /// Largest distance any vertex moves away from its bind position across a
    /// bounded spread of the clip's frames.
    /// </summary>
    private static double MeasureDisplacement(
        FbxModelAuthoringImportResult conformed,
        AnimationClip clip,
        CancellationToken cancellationToken)
    {
        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            conformed,
            CustomModelPreviewMode.Dl1Output);
        SkeletonRenderData? bindSkeleton = session.CreateSkeleton(null, 0, null);
        if (bindSkeleton is null || session.Meshes.IsDefaultOrEmpty)
        {
            return 0.0;
        }

        var bindPositions = new Dictionary<int, CpuDeformedVertex[]>();
        for (int meshIndex = 0; meshIndex < session.Meshes.Length; meshIndex++)
        {
            bindPositions[meshIndex] = CpuMeshDeformationEvaluator.Evaluate(
                session.Meshes[meshIndex],
                bindSkeleton,
                []);
        }

        long frameCount = Math.Max(1, clip.FrameCount);
        double maximum = 0.0;
        for (int sample = 0; sample < DisplacementSampleCount; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frame = checked((int)Math.Min(
                frameCount - 1,
                (long)Math.Round(sample * (frameCount - 1.0) /
                    Math.Max(1, DisplacementSampleCount - 1))));
            SkeletonRenderData? posed = session.CreateSkeleton(clip, frame, null);
            if (posed is null)
            {
                continue;
            }

            for (int meshIndex = 0; meshIndex < session.Meshes.Length; meshIndex++)
            {
                CpuDeformedVertex[] bind = bindPositions[meshIndex];
                CpuDeformedVertex[] animated = CpuMeshDeformationEvaluator.Evaluate(
                    session.Meshes[meshIndex],
                    posed,
                    []);
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

        return maximum;
    }
}
