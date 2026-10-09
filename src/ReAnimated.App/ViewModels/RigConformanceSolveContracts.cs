using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

internal interface IRigConformanceSolveScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);

    Task<RigConformanceSolveResult> SolveAsync(
        RigConformanceSolveRequest request,
        CancellationToken cancellationToken);
}

internal sealed record RigConformanceSolveRequest(
    long Revision,
    FbxModelAuthoringImportResult? Model,
    Func<CancellationToken, RigConformanceSolveResult> Compute);

internal sealed record RigConformanceSolveResult(
    RigCorrespondence? Correspondence,
    RigLandmarkSolution? Landmark,
    RigConformanceResult? Fit,
    RigGeometryEvidence? GeometryEvidence,
    bool GeometryEvidenceCaptured,
    string Status);

internal sealed class RigConformanceSolveDelay(TimeSpan duration)
{
    private readonly TaskCompletionSource _completion = new();

    public TimeSpan Duration { get; } = duration;

    public Task WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    public void Complete() => _completion.TrySetResult();
}

internal sealed class ThreadPoolRigConformanceSolveScheduler : IRigConformanceSolveScheduler
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    public Task<RigConformanceSolveResult> SolveAsync(
        RigConformanceSolveRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(() => request.Compute(cancellationToken), cancellationToken);
}

internal interface IConformancePreviewPreparationScheduler
{
    Task<PreparedConformancePreview> PrepareAsync(
        Func<CancellationToken, PreparedConformancePreview> prepare,
        CancellationToken cancellationToken);
}

internal sealed record PreparedConformancePreview(
    string TopologyKey,
    CustomModelPreviewSession Session,
    ImmutableArray<int> FitToEffective,
    SkeletonRenderData? Skeleton);

internal sealed class ThreadPoolConformancePreviewPreparationScheduler : IConformancePreviewPreparationScheduler
{
    public Task<PreparedConformancePreview> PrepareAsync(
        Func<CancellationToken, PreparedConformancePreview> prepare,
        CancellationToken cancellationToken) =>
        Task.Run(() => prepare(cancellationToken), cancellationToken);
}
