using ReAnimated.App.ViewModels;

namespace ReAnimated.Tests;

internal sealed class ImmediateRigConformanceSolveScheduler : IRigConformanceSolveScheduler
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay < TimeSpan.Zero
            ? Task.FromException(new ArgumentOutOfRangeException(nameof(delay)))
            : cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    public Task<RigConformanceSolveResult> SolveAsync(
        RigConformanceSolveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request.Compute(cancellationToken));
    }
}

internal sealed class ImmediateConformancePreviewPreparationScheduler : IConformancePreviewPreparationScheduler
{
    public Task<PreparedConformancePreview> PrepareAsync(
        Func<CancellationToken, PreparedConformancePreview> prepare,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(prepare(cancellationToken));
    }
}

internal static class RigConformanceTestSchedulers
{
    public static void UseImmediate(RigConformanceWizardViewModel wizard) =>
        wizard.SetSolveScheduler(new ImmediateRigConformanceSolveScheduler());

    public static void UseImmediate(ModelsWorkspaceViewModel workspace)
    {
        UseImmediate(workspace.Conformance);
        workspace.ConformancePreviewPreparationScheduler =
            new ImmediateConformancePreviewPreparationScheduler();
    }
}
