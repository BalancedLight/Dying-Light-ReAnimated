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
                Assert.InRange(point.PixelY, 32.0, 166.0);
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

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void LongClipFitsWithoutCreatingAnUnboundedTimelineCanvas()
    {
        TimelineViewModel timeline = new(
            startFrame: 0,
            endFrame: 3_342);

        Assert.InRange(timeline.CanvasWidth, 720.0, 1_121.0);
        Assert.InRange(timeline.FrameMarkers.Count, 2, 20);
        Assert.True(timeline.PixelsPerFrame < 1.0);

        timeline.ZoomInCommand.Execute(null);

        Assert.True(timeline.CanvasWidth > 1_120.0);
        Assert.True(timeline.FrameMarkers.Count < 40);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ChannelSelectionAndSearchShowOnlyOwnedCurves()
    {
        TimelineViewModel timeline = new();
        var hips = new TimelineTrackViewModel(
            "source-transform:0",
            "mixamorig:Hips",
            "Transform | 120 source keys",
            "Source animation",
            isReadOnly: true);
        var jaw = new TimelineTrackViewModel(
            "source-scalar:jaw_open",
            "jaw_open",
            "Scalar | 12 source keys",
            "Facial",
            isReadOnly: true);
        timeline.ReplaceTracks([hips, jaw]);
        timeline.ReplaceCurves(
        [
            new TimelineCurveTrackViewModel(
                "Translation X",
                "#F26C6C",
                [new TimelineCurveKeyViewModel(0, 0)],
                hips.Id),
            new TimelineCurveTrackViewModel(
                "Value",
                "#E599F7",
                [new TimelineCurveKeyViewModel(0, 1)],
                jaw.Id),
        ]);

        Assert.Same(hips, timeline.SelectedTrack);
        Assert.Equal("Translation X", Assert.Single(timeline.Curves).Name);

        timeline.TrackSearchText = "jaw";

        Assert.Same(jaw, timeline.SelectedTrack);
        Assert.Equal("Value", Assert.Single(timeline.Curves).Name);
        Assert.Equal("1 of 2 tracks", timeline.VisibleTrackCountLabel);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void LongSourceTracksReportRealCountsAndDeemphasizeUnselectedKeys()
    {
        var hips = new TimelineTrackViewModel(
            "source-transform:0",
            "mixamorig:Hips",
            "Transform | 3,343 source keys",
            "Source animation",
            isReadOnly: true,
            totalKeyCount: 3_343);
        var spine = new TimelineTrackViewModel(
            "source-transform:1",
            "mixamorig:Spine",
            "Transform | 3,343 source keys",
            "Source animation",
            isReadOnly: true,
            totalKeyCount: 3_343);
        for (var index = 0; index < 48; index++)
        {
            hips.Keyframes.Add(new TimelineKeyframeViewModel(
                hips.Name,
                index * 70,
                0,
                0));
            spine.Keyframes.Add(new TimelineKeyframeViewModel(
                spine.Name,
                index * 70,
                0,
                0));
        }

        TimelineViewModel timeline = new(0, 3_342);
        timeline.ReplaceTracks([hips, spine]);

        Assert.Equal("3,343", hips.KeyCountLabel);
        Assert.Contains(
            "48 representative markers",
            hips.KeyPresentationLabel,
            StringComparison.Ordinal);
        Assert.Equal(60, timeline.VisibleKeyframes.Count);
        Assert.Equal(
            48,
            timeline.VisibleKeyframes.Count(static key =>
                key.IsSelectedTrack));
        Assert.Equal(
            12,
            timeline.VisibleKeyframes.Count(static key =>
                !key.IsSelectedTrack));
    }
}
