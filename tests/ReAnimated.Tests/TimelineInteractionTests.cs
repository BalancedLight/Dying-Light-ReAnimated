using System.Windows;
using System.Windows.Controls;
using ReAnimated.App.ViewModels;
using ReAnimated.App.Views;

namespace ReAnimated.Tests;

public sealed class TimelineInteractionTests
{
    [Fact]
    public void StationaryClickOnAFractionalKeyDoesNotCommitOrSnap()
    {
        TimelineViewModel timeline = CreateTimeline();
        int commits = 0;
        timeline.KeyEditRequested += (_, args) => { commits++; args.Success = true; };
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 10, 10));
        timeline.PreviewKeyDrag(10, 10);
        timeline.EndKeyDrag();
        Assert.Equal(0, commits);
        Assert.Equal(2.5, timeline.SelectedKeyFrame);
    }

    [Fact]
    public void PointerJitterAndVerticalDragPreserveFractionalTime()
    {
        TimelineViewModel timeline = CreateTimeline();
        int commits = 0;
        TimelineKeyEditRequestEventArgs? request = null;
        timeline.KeyEditRequested += (_, args) => { commits++; request = args; args.Success = true; };
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 10, 10));
        timeline.PreviewKeyDrag(11, 11);
        timeline.EndKeyDrag();
        Assert.Equal(0, commits);
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 10, 10));
        timeline.PreviewKeyDrag(10, 25);
        timeline.EndKeyDrag();
        Assert.NotNull(request);
        Assert.Equal(2.5, request.Frame);
        Assert.NotNull(request.Value);
    }

    [Fact]
    public void CurveRefreshClearsAKeyRemovedByHistory()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
        timeline.ReplaceCurves([new("Translation X", "#E06B65", [new(0, 1)], "edit:layer:0")]);
        Assert.False(timeline.HasSelectedKey);
        Assert.False(timeline.ApplyKeyEditCommand.CanExecute(null));
        Assert.False(timeline.DeleteKeyCommand.CanExecute(null));
    }

    [Fact]
    public void TrackRefreshDuringADragCannotCommitOrRestoreStaleKeys()
    {
        TimelineViewModel timeline = CreateTimeline();
        int commits = 0;
        timeline.KeyEditRequested += (_, args) => { commits++; args.Success = true; };
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 10, 10));
        timeline.PreviewKeyDrag(10 + timeline.PixelsPerFrame, 10);
        var replacement = new TimelineTrackViewModel("edit:layer:0", "Root", "Transform", "Edit layer", false);
        replacement.Keyframes.Add(new TimelineKeyframeViewModel("Replacement", 4, 0, 0));
        timeline.ReplaceTracks([replacement]);
        timeline.ReplaceCurves([new("Translation X", "#E06B65", [new(4, 7)], "edit:layer:0")]);
        timeline.EndKeyDrag();

        Assert.Equal(0, commits);
        Assert.Same(replacement, timeline.SelectedTrack);
        Assert.Equal(4, Assert.Single(replacement.Keyframes).Frame);
        Assert.False(timeline.HasSelectedKey);
    }

    [Fact]
    public void ChangingTheSequenceOffsetCancelsADrag()
    {
        TimelineViewModel timeline = CreateTimeline();
        int commits = 0;
        timeline.KeyEditRequested += (_, args) => { commits++; args.Success = true; };
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 10, 10));
        timeline.PreviewKeyDrag(10 + timeline.PixelsPerFrame, 10);
        timeline.SourceFrameOffset = 2;
        timeline.EndKeyDrag();

        Assert.Equal(0, commits);
        Assert.False(timeline.HasSelectedKey);
        Assert.Contains(timeline.SelectedTrack!.Keyframes, key => key.Frame == 2.5);
    }

    [Fact]
    public void FormattedValueBindingCannotAlterATimeOnlyApply()
    {
        WpfTestDispatcher.Run(() =>
        {
            TimelineViewModel timeline = CreateTimeline();
            timeline.ReplaceCurves([new("Translation X", "#E06B65", [new(2.5, 0.123456789)], "edit:layer:0")]);
            timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
            var panel = new TimelinePanel { DataContext = timeline };
            panel.Measure(new Size(500, 320));
            panel.Arrange(new Rect(0, 0, 500, 320));
            panel.UpdateLayout();
            var frameInput = Assert.IsType<TextBox>(panel.FindName("KeyFrameInput"));
            var valueInput = Assert.IsType<TextBox>(panel.FindName("KeyValueInput"));
            TimelineKeyEditRequestEventArgs? request = null;
            timeline.KeyEditRequested += (_, args) => { request = args; args.Success = true; };
            frameInput.Text = "3.25";
            frameInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            valueInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal("0.123456789", valueInput.Text);
            Assert.Equal(0.123456789, timeline.KeyValueEdit);
            timeline.ApplyKeyEditCommand.Execute(null);
            Assert.NotNull(request);
            Assert.Equal(3.25, request.Frame);
            Assert.Null(request.Value);
        });
    }

    [Fact]
    public void CurveValuesFitInsideACompactViewport()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.SetViewportSize(320, 90);
        Assert.Equal(90, timeline.CurveCanvasHeight);
        Assert.All(timeline.CurvePoints, point => Assert.InRange(point.PixelY, 32, 76));
    }

    [Fact]
    public void ImportedSubframeKeysRemainDistinctInTheDopeSheet()
    {
        var timeline = new TimelineViewModel(0, 10);
        var track = new TimelineTrackViewModel("source-transform:0", "Root", "Transform", "Source animation", true,
            exactKeyPositions: [2.1, 2.2, 2.3]);
        timeline.ReplaceTracks([track]);
        timeline.SelectTrack(track.Id);
        Assert.Equal(3, timeline.VisibleKeyframes.Count);
        Assert.Equal([2.1, 2.2, 2.3], timeline.VisibleKeyframes.Select(static key => key.Frame));
    }

    [Fact]
    public void TimeOnlyApplyDoesNotRewriteTheComponentValue()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
        TimelineKeyEditRequestEventArgs? request = null;
        timeline.KeyEditRequested += (_, args) => { request = args; args.Success = true; };
        timeline.KeyFrameEdit = 3.5;
        timeline.ApplyKeyEditCommand.Execute(null);
        Assert.NotNull(request);
        Assert.Equal(3.5, request.Frame);
        Assert.Null(request.Value);
    }

    [Fact]
    public void SourceKeyInspectionDoesNotAllowEditing()
    {
        TimelineViewModel timeline = CreateTimeline(true);
        timeline.SetAuthoringAvailability(false, true);
        int requests = 0;
        timeline.KeyEditRequested += (_, _) => requests++;
        timeline.SelectKey("source-transform:0", 2.5, "Translation X");
        Assert.True(timeline.HasSelectedKey);
        Assert.False(timeline.ApplyKeyEditCommand.CanExecute(null));
        Assert.True(timeline.CreateEditLayerCommand.CanExecute(null));
        Assert.False(timeline.BeginKeyDrag("source-transform:0", 2.5, "Translation X", 0, 0));
        Assert.Equal(0, requests);
        Assert.Contains("Source", timeline.CurveStatusLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void FractionalTimeAndNumericEditsRespectSourceOffset()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.SourceFrameOffset = 2;
        timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
        Assert.Equal(.5, timeline.KeyFrameEdit);
        TimelineKeyframeViewModel marker = Assert.Single(timeline.VisibleKeyframes, key => key.IsSelected);
        Assert.Equal(.5 * timeline.PixelsPerFrame, marker.PixelX, 6);
        Assert.Equal(2.5, marker.Frame);
        TimelineKeyEditRequestEventArgs? captured = null;
        timeline.KeyEditRequested += (_, args) => { captured = args; args.Success = true; };
        timeline.KeyFrameEdit = 1.25;
        timeline.KeyValueEdit = 4;
        timeline.ApplyKeyEditCommand.Execute(null);
        Assert.NotNull(captured);
        Assert.Equal(2.5, captured.OriginalFrame);
        Assert.Equal(3.25, captured.Frame);
        Assert.Equal(4, captured.Value);
        Assert.Equal("Translation X", captured.Component);
    }

    [Theory]
    [InlineData(double.NaN, 2)]
    [InlineData(3, double.PositiveInfinity)]
    [InlineData(99, 2)]
    public void InvalidEditsDoNotReachPersistence(double frame, double value)
    {
        TimelineViewModel timeline = CreateTimeline();
        int requests = 0;
        timeline.KeyEditRequested += (_, _) => requests++;
        timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
        timeline.KeyFrameEdit = frame;
        timeline.KeyValueEdit = value;
        timeline.ApplyKeyEditCommand.Execute(null);
        Assert.Equal(0, requests);
        Assert.NotEmpty(timeline.KeyEditFeedback);
    }

    [Fact]
    public void DragCommitsOnceAndCancelRestoresGeometry()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.SnapKeyFrames = false;
        int commits = 0;
        timeline.KeyEditRequested += (_, args) => { commits++; args.Success = true; };
        double initialPixel = timeline.CurvePoints[0].PixelX;
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 0, 0));
        timeline.PreviewKeyDrag(timeline.PixelsPerFrame, -5);
        timeline.PreviewKeyDrag(2 * timeline.PixelsPerFrame, -10);
        Assert.Equal(0, commits);
        Assert.Equal(4.5, timeline.SelectedKeyFrame);
        timeline.EndKeyDrag(cancel: true);
        Assert.Equal(0, commits);
        Assert.Equal(2.5, timeline.SelectedKeyFrame);
        Assert.Equal(initialPixel, timeline.CurvePoints[0].PixelX, 6);
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, "Translation X", 0, 0));
        timeline.PreviewKeyDrag(timeline.PixelsPerFrame, 0);
        timeline.EndKeyDrag();
        Assert.Equal(1, commits);
    }

    [Fact]
    public void DragDoesNotReplaceAnotherKeyAtTheDestination()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.SnapKeyFrames = false;
        int commits = 0;
        timeline.KeyEditRequested += (_, _) => commits++;
        Assert.True(timeline.BeginKeyDrag("edit:layer:0", 2.5, null, 0, 0));
        timeline.PreviewKeyDrag(5.5 * timeline.PixelsPerFrame, 0);
        Assert.Equal(2.5, timeline.SelectedKeyFrame);
        Assert.Contains("already", timeline.KeyEditFeedback, StringComparison.Ordinal);
        timeline.EndKeyDrag();
        Assert.Equal(0, commits);
    }

    [Fact]
    public void ComponentSelectionUsesSeparateScaleAndTrackChangeClearsKey()
    {
        TimelineViewModel timeline = CreateTimeline();
        timeline.ReplaceCurves([
            new("Translation X", "#E06B65", [new(2.5, 2), new(8, 4)], "edit:layer:0"),
            new("Scale X", "#66C58A", [new(2.5, 1000), new(8, 2000)], "edit:layer:0"),
        ]);
        timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
        Assert.Equal(2, timeline.CurvePoints.Count);
        Assert.All(timeline.CurvePoints, point => Assert.Equal("Translation X", point.Track));
        Assert.True(timeline.CurvePoints[0].PixelY - timeline.CurvePoints[1].PixelY > 50);
        timeline.SelectedTrack = null;
        Assert.False(timeline.HasSelectedKey);
        Assert.False(timeline.DeleteKeyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(480, 300, false)]
    [InlineData(900, 360, false)]
    [InlineData(480, 300, true)]
    public void CompactPanelKeepsGraphVisible(double width, double height, bool selectedKey)
    {
        WpfTestDispatcher.Run(() =>
        {
            TimelineViewModel timeline = CreateTimeline();
            if (selectedKey) timeline.SelectKey("edit:layer:0", 2.5, "Translation X");
            var panel = new TimelinePanel { DataContext = timeline };
            panel.Measure(new Size(width, height));
            panel.Arrange(new Rect(0, 0, width, height));
            panel.UpdateLayout();
            var graph = Assert.IsType<ScrollViewer>(panel.FindName("DopeSheetScroll"));
            Assert.True(graph.ActualHeight >= 100, $"Graph height: {graph.ActualHeight}");
            Assert.InRange(graph.TranslatePoint(new Point(0, graph.ActualHeight), panel).Y, 100, height);
            var tabs = Assert.IsType<TabControl>(panel.FindName("TimelineTabs"));
            tabs.SelectedIndex = 1;
            panel.UpdateLayout();
            var curve = Assert.IsType<ScrollViewer>(panel.FindName("CurveScroll"));
            Assert.True(curve.ActualHeight >= 100, $"Curve height: {curve.ActualHeight}");
        });
    }

    private static TimelineViewModel CreateTimeline(bool readOnly = false)
    {
        var timeline = new TimelineViewModel(0, 10);
        string id = readOnly ? "source-transform:0" : "edit:layer:0";
        var track = new TimelineTrackViewModel(id, "Root", "Transform", readOnly ? "Source animation" : "Authored edits", readOnly);
        track.Keyframes.Add(new("Root", 2.5, 0, 0));
        track.Keyframes.Add(new("Root", 8, 0, 0));
        timeline.ReplaceTracks([track]);
        timeline.ReplaceCurves([new("Translation X", "#E06B65", [new(2.5, 2), new(8, 4)], id)]);
        timeline.SelectTrack(id);
        return timeline;
    }
}
