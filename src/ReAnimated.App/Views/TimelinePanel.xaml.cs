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

    public TimelinePanel()
    {
        InitializeComponent();
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
        timeline.ScrubToPixel(point.X);
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

        timeline.ScrubToPixel(e.GetPosition(canvas).X);
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
            timeline.ScrubToPixel(e.GetPosition(canvas).X);
        }
        canvas.ReleaseMouseCapture();
        _activeScrubCanvas = null;
        e.Handled = true;
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
