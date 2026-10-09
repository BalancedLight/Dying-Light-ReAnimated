using System.Collections.Concurrent;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class RigConformanceAsyncSolveTests
{
    [Fact]
    public async Task SetModelAsyncWaitsForItsInitialFitResult()
    {
        var (template, _, _) = RigGeometryCorrespondenceTests.Fixture();
        var scheduler = new DeferredRigConformanceSolveScheduler();
        var wizard = CreateWizard(template, scheduler);
        FbxModelAuthoringImportResult model = RigConformanceWizardTests.CreateModel();

        await wizard.ResolveTemplateCommand.ExecuteAsync(null);
        Task load = wizard.SetModelAsync(model);
        Assert.False(load.IsCompleted);
        RigConformanceSolveRequest request = await scheduler.WaitForSolveAsync(0);

        scheduler.CompleteSolve(0, request.Compute(CancellationToken.None));
        await load;

        Assert.NotNull(wizard.Fit);
        Assert.False(wizard.IsBusy);
    }

    [Fact]
    public async Task NumericFitChangesDebounceAndKeepTheLastPublishedFitUntilReplacement()
    {
        var (template, _, _) = RigConformanceGeometryFixture();
        var scheduler = new DeferredRigConformanceSolveScheduler { CompleteSolvesImmediately = true };
        var wizard = CreateWizard(template, scheduler);
        await wizard.SetModelAsync(RigConformanceWizardTests.CreateModel());
        await wizard.ResolveTemplateCommand.ExecuteAsync(null);
        RigConformanceResult previous = Assert.IsType<RigConformanceResult>(wizard.Fit);

        scheduler.CompleteSolvesImmediately = false;
        wizard.ConformanceStrength = 0.35;
        Assert.Same(previous, wizard.Fit);
        await scheduler.WaitForDelayAsync(0);
        wizard.ConformanceStrength = 0.65;
        RigConformanceSolveDelay delay = await scheduler.WaitForDelayAsync(1);

        Assert.Equal(TimeSpan.FromMilliseconds(200), delay.Duration);
        Assert.Same(previous, wizard.Fit);
        Assert.True(wizard.IsBusy);
        Assert.False(wizard.CanApply);

        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFitChanged(object? sender, EventArgs args) => published.TrySetResult();
        wizard.FitChanged += OnFitChanged;
        try
        {
            scheduler.CompleteDelay(1);
            RigConformanceSolveRequest request = await scheduler.WaitForSolveAsync(0);
            scheduler.CompleteSolve(0, request.Compute(CancellationToken.None));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            wizard.FitChanged -= OnFitChanged;
        }

        Assert.NotSame(previous, wizard.Fit);
        Assert.Equal(0.65, wizard.Fit!.ConformanceStrength, 6);
        Assert.False(wizard.IsBusy);
    }

    [Fact]
    public async Task OlderFitWorkerCompletionCannotReplaceANewerInputRevision()
    {
        var (template, _, _) = RigConformanceGeometryFixture();
        var scheduler = new DeferredRigConformanceSolveScheduler
        {
            CompleteSolvesImmediately = true,
            CompleteDelaysImmediately = true,
        };
        var wizard = CreateWizard(template, scheduler);
        await wizard.SetModelAsync(RigConformanceWizardTests.CreateModel());
        await wizard.ResolveTemplateCommand.ExecuteAsync(null);

        scheduler.CompleteSolvesImmediately = false;
        wizard.ConformanceStrength = 0.25;
        Assert.Equal(1, scheduler.SolveCount);
        RigConformanceSolveRequest older = await scheduler.WaitForSolveAsync(0);
        wizard.ConformanceStrength = 0.75;
        Assert.Equal(2, scheduler.SolveCount);
        RigConformanceSolveRequest newer = await scheduler.WaitForSolveAsync(1);

        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFitChanged(object? sender, EventArgs args) => published.TrySetResult();
        wizard.FitChanged += OnFitChanged;
        try
        {
            scheduler.CompleteSolve(1, newer.Compute(CancellationToken.None));
            await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RigConformanceResult current = Assert.IsType<RigConformanceResult>(wizard.Fit);
            Assert.Equal(0.75, current.ConformanceStrength);

            scheduler.CompleteSolve(0, older.Compute(CancellationToken.None));
            Assert.Same(current, wizard.Fit);
        }
        finally
        {
            wizard.FitChanged -= OnFitChanged;
        }
    }

    private static RigConformanceWizardViewModel CreateWizard(
        Dl1RigTemplate template,
        IRigConformanceSolveScheduler scheduler)
    {
        return new RigConformanceWizardViewModel(
            (profile, _) => Task.FromResult(new Dl1RigTemplateResolution(
                template, profile, "fixture", new string('b', 64), "generated rig")),
            static _ => { },
            scheduler);
    }

    private static (Dl1RigTemplate Template, ReAnimated.Core.Domain.RigDefinition Rig,
        ReAnimated.Core.Geometry.RigGeometryEvidence Geometry) RigConformanceGeometryFixture() =>
        RigGeometryCorrespondenceTests.Fixture();

    private sealed class DeferredRigConformanceSolveScheduler : IRigConformanceSolveScheduler
    {
        private readonly ConcurrentQueue<RigConformanceSolveDelay> _delays = new();
        private readonly ConcurrentQueue<TaskCompletionSource<RigConformanceSolveDelay>> _delayWaiters = new();
        private readonly ConcurrentQueue<RigConformanceSolveRequest> _requests = new();
        private readonly ConcurrentQueue<TaskCompletionSource<RigConformanceSolveRequest>> _solveWaiters = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<RigConformanceSolveResult>> _solveCompletions = new();

        public bool CompleteSolvesImmediately { get; set; }
        public bool CompleteDelaysImmediately { get; set; }
        public int SolveCount => _requests.Count;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var call = new RigConformanceSolveDelay(delay);
            _delays.Enqueue(call);
            if (_delayWaiters.TryDequeue(out TaskCompletionSource<RigConformanceSolveDelay>? waiter))
                waiter.TrySetResult(call);
            return CompleteDelaysImmediately ? Task.CompletedTask : call.WaitAsync(cancellationToken);
        }

        public Task<RigConformanceSolveResult> SolveAsync(
            RigConformanceSolveRequest request,
            CancellationToken cancellationToken)
        {
            if (CompleteSolvesImmediately)
                return Task.FromResult(request.Compute(cancellationToken));

            int index = _requests.Count;
            _requests.Enqueue(request);
            var completion = new TaskCompletionSource<RigConformanceSolveResult>();
            _solveCompletions[index] = completion;
            if (_solveWaiters.TryDequeue(out TaskCompletionSource<RigConformanceSolveRequest>? waiter))
                waiter.TrySetResult(request);
            return completion.Task;
        }

        public Task<RigConformanceSolveDelay> WaitForDelayAsync(int index)
        {
            if (_delays.Count > index) return Task.FromResult(_delays.ElementAt(index));
            var waiter = new TaskCompletionSource<RigConformanceSolveDelay>(TaskCreationOptions.RunContinuationsAsynchronously);
            _delayWaiters.Enqueue(waiter);
            return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public Task<RigConformanceSolveRequest> WaitForSolveAsync(int index)
        {
            if (_requests.Count > index) return Task.FromResult(_requests.ElementAt(index));
            var waiter = new TaskCompletionSource<RigConformanceSolveRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            _solveWaiters.Enqueue(waiter);
            return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void CompleteDelay(int index) => _delays.ElementAt(index).Complete();
        public void CompleteSolve(int index, RigConformanceSolveResult result) => _solveCompletions[index].TrySetResult(result);
    }
}
