using ReAnimated.App.ViewModels;

namespace ReAnimated.Tests;

public sealed class ViewModelTimelineTests
{
    [Fact]
    public void TimelineClampsFramesAndDeduplicatesKeys()
    {
        TimelineViewModel timeline = new()
        {
            CurrentFrame = 500,
        };

        Assert.Equal(timeline.EndFrame, timeline.CurrentFrame);

        timeline.CurrentFrame = 17;
        timeline.AddKeyframeCommand.Execute(null);
        timeline.AddKeyframeCommand.Execute(null);

        TimelineTrackViewModel track = Assert.Single(timeline.Tracks);
        TimelineKeyframeViewModel key = Assert.Single(track.Keyframes);
        Assert.Equal(17, key.Frame);
        Assert.Single(timeline.VisibleKeyframes);
    }

    [Fact]
    public void NonLoopingPlaybackStopsAtEnd()
    {
        TimelineViewModel timeline = new()
        {
            CurrentFrame = 119,
            IsLooping = false,
            IsPlaying = true,
        };
        DateTimeOffset started = DateTimeOffset.UtcNow;

        timeline.Tick(started);
        timeline.Tick(started.AddSeconds(2));

        Assert.Equal(timeline.EndFrame, timeline.CurrentFrame);
        Assert.False(timeline.IsPlaying);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void PlaybackLockPausesAndRejectsThePlayCommand()
    {
        TimelineViewModel timeline = new()
        {
            IsPlaying = true,
        };

        timeline.IsPlaybackEnabled = false;

        Assert.False(timeline.IsPlaying);
        Assert.False(timeline.TogglePlaybackCommand.CanExecute(null));
        Assert.Equal("Playback locked", timeline.PlaybackLabel);

        timeline.TogglePlaybackCommand.Execute(null);
        Assert.False(timeline.IsPlaying);

        timeline.IsPlaybackEnabled = true;
        Assert.True(timeline.TogglePlaybackCommand.CanExecute(null));
        Assert.Equal("Play", timeline.PlaybackLabel);
    }

    [Fact]
    public void HighRefreshPlaybackAccumulatesSubFrameElapsedTime()
    {
        TimelineViewModel timeline = new()
        {
            FramesPerSecond = 30.0,
            IsPlaying = true,
        };
        DateTimeOffset started = DateTimeOffset.UnixEpoch;
        timeline.Tick(started);

        for (int tick = 1; tick <= 100; tick++)
        {
            timeline.Tick(started.AddMilliseconds(tick * 10));
        }

        Assert.Equal(30, timeline.CurrentFrame);
        Assert.True(timeline.IsPlaying);
    }

    [Fact]
    public void PauseSeekAndRateChangesResetFractionalPlaybackTime()
    {
        TimelineViewModel timeline = new()
        {
            FramesPerSecond = 25.0,
            IsPlaying = true,
        };
        DateTimeOffset started = DateTimeOffset.UnixEpoch;
        timeline.Tick(started);
        timeline.Tick(started.AddMilliseconds(20));
        Assert.Equal(0, timeline.CurrentFrame);

        timeline.IsPlaying = false;
        timeline.Tick(started.AddSeconds(20));
        timeline.IsPlaying = true;
        timeline.Tick(started.AddSeconds(30));
        timeline.Tick(started.AddSeconds(30).AddMilliseconds(20));
        Assert.Equal(0, timeline.CurrentFrame);

        timeline.CurrentFrame = 10;
        timeline.Tick(started.AddSeconds(31));
        timeline.Tick(started.AddSeconds(31).AddMilliseconds(20));
        Assert.Equal(10, timeline.CurrentFrame);

        timeline.FramesPerSecond = 50.0;
        timeline.Tick(started.AddSeconds(32));
        timeline.Tick(started.AddSeconds(32).AddMilliseconds(10));
        Assert.Equal(10, timeline.CurrentFrame);
        timeline.Tick(started.AddSeconds(32).AddMilliseconds(20));
        Assert.Equal(11, timeline.CurrentFrame);
    }

    [Fact]
    public void CurveViewBuildsFiniteSharedScaleGeometry()
    {
        TimelineViewModel timeline = new();

        timeline.ReplaceCurves(
        [
            new TimelineCurveTrackViewModel(
                "hand.translation.x",
                "#E06B65",
                [
                    new TimelineCurveKeyViewModel(0.0, -2.0),
                    new TimelineCurveKeyViewModel(10.0, 0.0),
                    new TimelineCurveKeyViewModel(20.0, 2.0),
                ]),
            new TimelineCurveTrackViewModel(
                "hand.scale.x",
                "#66C58A",
                [
                    new TimelineCurveKeyViewModel(0.0, 1.0),
                    new TimelineCurveKeyViewModel(20.0, 1.0),
                ]),
        ]);

        Assert.Equal(2, timeline.Curves.Count);
        Assert.Equal(5, timeline.CurvePoints.Count);
        Assert.Equal(3, timeline.CurveSegments.Count);
        Assert.All(
            timeline.CurvePoints,
            point =>
            {
                Assert.True(double.IsFinite(point.PixelX));
                Assert.InRange(point.PixelY, 16.0, 142.0);
            });
        Assert.Equal(120.0, timeline.CurvePoints[2].PixelX);
        Assert.True(
            timeline.CurvePoints[0].PixelY >
            timeline.CurvePoints[2].PixelY);
    }

    [Fact]
    public void CurveViewRejectsDuplicateOrNonFiniteKeys()
    {
        Assert.Throws<ArgumentException>(() =>
            new TimelineCurveTrackViewModel(
                "invalid",
                "#FFFFFF",
                [
                    new TimelineCurveKeyViewModel(1.0, 0.0),
                    new TimelineCurveKeyViewModel(1.0, 1.0),
                ]));
        Assert.Throws<ArgumentException>(() =>
            new TimelineCurveTrackViewModel(
                "invalid",
                "#FFFFFF",
                [
                    new TimelineCurveKeyViewModel(
                        0.0,
                        double.NaN),
                ]));
    }
}
