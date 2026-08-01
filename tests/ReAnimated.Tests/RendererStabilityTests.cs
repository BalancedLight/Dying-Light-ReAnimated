using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererStabilityTests
{
    [Fact]
    public void ResizeMailboxNormalizesAndPublishesOneAtomicPair()
    {
        RendererViewportSizeMailbox mailbox = new(
            width: 0,
            height: -20);

        Assert.Equal(
            new RendererViewportSize(1, 1),
            mailbox.Read());

        mailbox.Publish(3_840, 2_160);

        Assert.Equal(
            new RendererViewportSize(3_840, 2_160),
            mailbox.Read());
    }

    [Fact]
    public async Task ResizeMailboxNeverReturnsTornConcurrentDimensions()
    {
        RendererViewportSize first =
            new(1_111, 2_222);
        RendererViewportSize second =
            new(3_333, 4_444);
        RendererViewportSizeMailbox mailbox =
            new(first.Width, first.Height);
        using ManualResetEventSlim start = new(false);

        Task firstWriter = Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < 50_000; index++)
            {
                mailbox.Publish(first.Width, first.Height);
            }
        });
        Task secondWriter = Task.Run(() =>
        {
            start.Wait();
            for (int index = 0; index < 50_000; index++)
            {
                mailbox.Publish(second.Width, second.Height);
            }
        });

        start.Set();
        for (int index = 0; index < 100_000; index++)
        {
            RendererViewportSize observed = mailbox.Read();
            Assert.True(
                observed == first || observed == second,
                $"Observed torn viewport dimensions {observed.Width}x{observed.Height}.");
        }

        await Task.WhenAll(firstWriter, secondWriter);
    }

    [Fact]
    public void AdapterRefreshSignalCannotLoseMultipleRequests()
    {
        RendererAdapterRefreshSignal signal = new();
        long originalGeneration = signal.CaptureGeneration();

        signal.RequestRefresh();
        signal.RequestRefresh();

        Assert.True(signal.HasChanged(originalGeneration));
        long latestGeneration = signal.CaptureGeneration();
        Assert.Equal(originalGeneration + 2, latestGeneration);
        Assert.False(signal.HasChanged(latestGeneration));
    }
}
