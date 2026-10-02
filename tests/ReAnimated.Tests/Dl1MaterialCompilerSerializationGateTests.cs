using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1MaterialCompilerSerializationGateTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task SerializesConcurrentJobsTargetingTheSameTemplateOutput()
    {
        string sharedOutput = Path.Combine(Path.GetTempPath(), $"meconv-template-{Guid.NewGuid():N}.dll");
        int activeJobs = 0;
        int maximumActiveJobs = 0;

        async Task<int> FakeMaterialStage(TimeSpan timeout, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref activeJobs);
            UpdateMaximum(ref maximumActiveJobs, active);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(60), cancellationToken);
                Assert.True(timeout > TimeSpan.Zero);
                return active;
            }
            finally
            {
                Interlocked.Decrement(ref activeJobs);
            }
        }

        Task<int> first = Dl1MaterialCompilerSerializationGate.RunAsync(
            sharedOutput, TimeSpan.FromSeconds(3), FakeMaterialStage);
        Task<int> second = Dl1MaterialCompilerSerializationGate.RunAsync(
            sharedOutput, TimeSpan.FromSeconds(3), FakeMaterialStage);

        int[] results = await Task.WhenAll(first, second);

        Assert.Equal(1, maximumActiveJobs);
        Assert.All(results, static activeCount => Assert.Equal(1, activeCount));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task TimesOutWhileWaitingForTheSharedTemplateOutput()
    {
        string sharedOutput = Path.Combine(Path.GetTempPath(), $"meconv-template-{Guid.NewGuid():N}.dll");
        var stageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = Dl1MaterialCompilerSerializationGate.RunAsync(
            sharedOutput,
            TimeSpan.FromSeconds(3),
            async (_, cancellationToken) =>
            {
                stageStarted.SetResult();
                await releaseStage.Task.WaitAsync(cancellationToken);
                return true;
            });

        await stageStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => Dl1MaterialCompilerSerializationGate.RunAsync(
            sharedOutput,
            TimeSpan.FromMilliseconds(250),
            static (_, _) => Task.FromResult(true)));
        releaseStage.SetResult();
        Assert.True(await first);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task PassesOnlyTheRemainingTimeoutToTheCompilerStage()
    {
        string sharedOutput = Path.Combine(Path.GetTempPath(), $"meconv-template-{Guid.NewGuid():N}.dll");
        var stageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> first = Dl1MaterialCompilerSerializationGate.RunAsync(
            sharedOutput,
            TimeSpan.FromSeconds(3),
            async (_, cancellationToken) =>
            {
                stageStarted.SetResult();
                await releaseStage.Task.WaitAsync(cancellationToken);
                return true;
            });

        await stageStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<TimeSpan> second = Dl1MaterialCompilerSerializationGate.RunAsync(
            sharedOutput,
            TimeSpan.FromSeconds(2),
            static (remaining, _) => Task.FromResult(remaining));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        releaseStage.SetResult();

        TimeSpan remaining = await second;
        Assert.True(await first);
        Assert.True(remaining > TimeSpan.Zero);
        Assert.True(remaining < TimeSpan.FromSeconds(1.9));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void MutexIdentityNormalizesCaseAndRelativeSegments()
    {
        string root = Path.Combine(Path.GetTempPath(), "meconv-lock-identity");
        string first = Path.Combine(root, "job", "..", "MeConvTemplates_dx11.dll");
        string normalized = Path.Combine(root, "MeConvTemplates_dx11.dll");

        Assert.Equal(
            Dl1MaterialCompilerSerializationGate.CreateMutexName(first),
            Dl1MaterialCompilerSerializationGate.CreateMutexName(normalized.ToUpperInvariant()));
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref maximum);
            if (previous >= candidate)
                return;
        }
        while (Interlocked.CompareExchange(ref maximum, candidate, previous) != previous);
    }
}
