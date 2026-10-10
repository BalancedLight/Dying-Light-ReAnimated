using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App.Views;

public partial class TimelinePanel : UserControl
{
    private bool _synchronizingVerticalScroll;
    private Canvas? _activeScrubCanvas;
    private bool _eventDragging;
    private bool _keyDragging;
    private double? _marqueeAnchor;

    public TimelinePanel()
    {
        InitializeComponent();
    }

    private void TimelinePanel_OnLoaded(
        object sender,
        RoutedEventArgs e) =>
        QueueViewportMeasurement();

    private void TimelinePanel_OnSizeChanged(
        object sender,
        SizeChangedEventArgs e) =>
        QueueViewportMeasurement();

    private void TimelineTabs_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TimelineTabs))
        {
            QueueViewportMeasurement();
        }
    }

    private void TimelinePanel_OnPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 ||
            DataContext is not TimelineViewModel timeline)
        {
            return;
        }

        if (e.Delta > 0)
        {
            timeline.ZoomInCommand.Execute(null);
        }
        else if (e.Delta < 0)
        {
            timeline.ZoomOutCommand.Execute(null);
        }

        e.Handled = e.Delta != 0;
    }

    private void QueueViewportMeasurement()
    {
        _ = Dispatcher.BeginInvoke(
            new Action(UpdateTimelineViewportSize),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateTimelineViewportSize()
    {
        ScrollViewer viewer = TimelineTabs.SelectedIndex == 1
            ? CurveScroll
            : DopeSheetScroll;
        UpdateTimelineViewportSize(viewer);
    }

    private void UpdateTimelineViewportSize(ScrollViewer viewer)
    {
        if (!IsVisible || DataContext is not TimelineViewModel timeline)
        {
            return;
        }

        double width = viewer.ViewportWidth > 0.0
            ? viewer.ViewportWidth
            : viewer.ActualWidth;
        double height = viewer.ViewportHeight > 0.0
            ? viewer.ViewportHeight
            : viewer.ActualHeight;
        timeline.SetViewportSize(width, height);
    }

    private void DopeChannelList_OnScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (_synchronizingVerticalScroll ||
            Math.Abs(e.VerticalChange) < double.Epsilon)
        {
            return;
        }

        SynchronizeVerticalScroll(
            DopeSheetScroll,
            e.VerticalOffset);
    }

    private void DopeSheetScroll_OnScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer viewer &&
            (Math.Abs(e.ViewportWidthChange) >= 0.5 ||
             Math.Abs(e.ViewportHeightChange) >= 0.5))
        {
            UpdateTimelineViewportSize(viewer);
        }

        if (DataContext is TimelineViewModel eventsTimeline) eventsTimeline.SetEventViewport(e.HorizontalOffset, e.ViewportWidth);
        if (_synchronizingVerticalScroll ||
            Math.Abs(e.VerticalChange) < double.Epsilon)
        {
            return;
        }

        ScrollViewer? channelScroll = FindVisualChild<ScrollViewer>(
            DopeChannelList);
        if (channelScroll is not null)
        {
            SynchronizeVerticalScroll(
                channelScroll,
                e.VerticalOffset);
        }
    }

    private void CurveScroll_OnScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer viewer &&
            (Math.Abs(e.ViewportWidthChange) >= 0.5 ||
             Math.Abs(e.ViewportHeightChange) >= 0.5))
        {
            UpdateTimelineViewportSize(viewer);
        }
    }

    private void SynchronizeVerticalScroll(
        ScrollViewer viewer,
        double offset)
    {
        _synchronizingVerticalScroll = true;
        try
        {
            viewer.ScrollToVerticalOffset(offset);
        }
        finally
        {
            _synchronizingVerticalScroll = false;
        }
    }

    private void TimelineCanvas_OnMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas ||
            DataContext is not TimelineViewModel timeline)
        {
            return;
        }

        _activeScrubCanvas = canvas;
        canvas.CaptureMouse();
        Point point = e.GetPosition(canvas);
        if (ReferenceEquals(canvas, DopeSheetCanvas))
        {
            timeline.SelectTrackFromCanvasY(point.Y);
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) _marqueeAnchor = timeline.PositionFromPixel(point.X);
        else timeline.ScrubToPixel(point.X);
        e.Handled = true;
    }

    private void TimelineCanvas_OnMouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (sender is not Canvas canvas ||
            !ReferenceEquals(canvas, _activeScrubCanvas) ||
            e.LeftButton != MouseButtonState.Pressed ||
            DataContext is not TimelineViewModel timeline)
        {
            return;
        }

        if (_keyDragging) { Point point = e.GetPosition(canvas); timeline.PreviewKeyDrag(point.X, point.Y); }
        else if (_eventDragging) timeline.Events.DragTo(timeline.PositionFromPixel(e.GetPosition(canvas).X));
        else if (_marqueeAnchor is { } anchor) timeline.Events.SelectRange(anchor, timeline.PositionFromPixel(e.GetPosition(canvas).X));
        else timeline.ScrubToPixel(e.GetPosition(canvas).X);
        e.Handled = true;
    }

    private void TimelineCanvas_OnMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not Canvas canvas ||
            !ReferenceEquals(canvas, _activeScrubCanvas))
        {
            return;
        }

        if (DataContext is TimelineViewModel timeline)
        {
            if (_keyDragging) { Point point = e.GetPosition(canvas); timeline.PreviewKeyDrag(point.X, point.Y); timeline.EndKeyDrag(); }
            else if (_eventDragging) { timeline.Events.DragTo(timeline.PositionFromPixel(e.GetPosition(canvas).X)); timeline.Events.EndDrag(); }
            else if (_marqueeAnchor is { } anchor) timeline.Events.SelectRange(anchor, timeline.PositionFromPixel(e.GetPosition(canvas).X));
            else timeline.ScrubToPixel(e.GetPosition(canvas).X);
        }
        _eventDragging = false;
        _keyDragging = false;
        _marqueeAnchor = null;
        canvas.ReleaseMouseCapture();
        _activeScrubCanvas = null;
        e.Handled = true;
    }

    private void KeyMarker_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimelineKeyframeViewModel key } ||
            key.TrackId is not { } trackId || DataContext is not TimelineViewModel timeline) return;
        BeginKeyPointerDrag(timeline, trackId, key.Frame, null, DopeSheetCanvas, e);
    }

    private void CurvePoint_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimelineCurvePointViewModel point } ||
            DataContext is not TimelineViewModel timeline || timeline.SelectedTrackId is not { } trackId) return;
        BeginKeyPointerDrag(timeline, trackId, point.Frame, point.Track, CurveCanvas, e);
    }

    private void BeginKeyPointerDrag(TimelineViewModel timeline, string trackId, double frame,
        string? component, Canvas canvas, MouseButtonEventArgs e)
    {
        Point position = e.GetPosition(canvas);
        timeline.SetKeyDragThreshold(SystemParameters.MinimumHorizontalDragDistance, SystemParameters.MinimumVerticalDragDistance);
        _keyDragging = timeline.BeginKeyDrag(trackId, frame, component, position.X, position.Y);
        if (_keyDragging)
        {
            _activeScrubCanvas = canvas;
            canvas.CaptureMouse();
        }
        Focus();
        e.Handled = true;
    }

    private void EventMarker_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimelineEventMarker marker } || DataContext is not TimelineViewModel timeline) return;
        timeline.ClearKeySelection();
        bool additive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!marker.Item.IsSelected || additive) timeline.Events.Select(marker.Id, additive);
        else timeline.Events.SelectedEvent = marker.Item;
        timeline.RefreshEventGeometry();
        if (marker.EventIds.Count > 1 && e.ClickCount > 1) timeline.ZoomInCommand.Execute(null);
        timeline.Events.BeginDrag(timeline.PositionFromPixel(e.GetPosition(DopeSheetCanvas).X));
        _eventDragging = true;
        _activeScrubCanvas = DopeSheetCanvas;
        DopeSheetCanvas.CaptureMouse();
        Focus();
        e.Handled = true;
    }

    private void TimelinePanel_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not TimelineViewModel timeline || e.OriginalSource is TextBox or ComboBox) return;
        if (e.Key == Key.Escape && _keyDragging)
        {
            timeline.EndKeyDrag(cancel: true);
            _keyDragging = false;
            _activeScrubCanvas?.ReleaseMouseCapture();
            _activeScrubCanvas = null;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && timeline.HasSelectedKey)
        {
            if (timeline.DeleteKeyCommand.CanExecute(null)) timeline.DeleteKeyCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (timeline.HasSelectedKey) return;
        if (e.Key == Key.Escape && _eventDragging)
        {
            timeline.Events.EndDrag(cancel: true); _eventDragging = false; _activeScrubCanvas?.ReleaseMouseCapture(); _activeScrubCanvas = null; e.Handled = true; return;
        }
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var command = e.Key switch
        {
            Key.Delete => timeline.Events.DeleteCommand,
            Key.D when control => timeline.Events.DuplicateCommand,
            Key.C when control => timeline.Events.CopyCommand,
            Key.V when control => timeline.Events.PasteCommand,
            _ => null,
        };
        if (command is not null && command.CanExecute(null)) { command.Execute(null); e.Handled = true; }
        else if (timeline.Events.SelectedEvent is not null && e.Key is Key.Left or Key.Right)
        {
            double step = timeline.Events.Snap == "Native" ? .2 : 1;
            timeline.Events.Nudge(e.Key == Key.Left ? -step : step); e.Handled = true;
        }
    }

    private void KeyCanvas_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_keyDragging || !ReferenceEquals(sender, _activeScrubCanvas)) return;
        _keyDragging = false;
        _activeScrubCanvas = null;
        if (DataContext is TimelineViewModel timeline) timeline.EndKeyDrag(cancel: true);
    }

    private void KeyEditor_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete || (Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is Key.V or Key.X)
            MarkKeyDraftEdited(e.OriginalSource);
        if (e.Key != Key.Enter || DataContext is not TimelineViewModel timeline) return;
        KeyFrameInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        KeyValueInput.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (Validation.GetHasError(KeyFrameInput) || Validation.GetHasError(KeyValueInput))
            timeline.SetKeyEditFeedback("Enter a valid frame and value.");
        else if (timeline.ApplyKeyEditCommand.CanExecute(null)) timeline.ApplyKeyEditCommand.Execute(null);
        e.Handled = true;
    }

    private void KeyEditor_OnPreviewTextInput(object sender, TextCompositionEventArgs e) => MarkKeyDraftEdited(e.OriginalSource);

    private void KeyEditor_OnPasting(object sender, DataObjectPastingEventArgs e) => MarkKeyDraftEdited(e.OriginalSource);

    private void MarkKeyDraftEdited(object source)
    {
        if (DataContext is not TimelineViewModel timeline) return;
        if (ReferenceEquals(source, KeyFrameInput)) timeline.MarkKeyFrameDraftEdited();
        else if (ReferenceEquals(source, KeyValueInput)) timeline.MarkKeyValueDraftEdited();
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < childCount; index++)
        {
            DependencyObject child =
                VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
