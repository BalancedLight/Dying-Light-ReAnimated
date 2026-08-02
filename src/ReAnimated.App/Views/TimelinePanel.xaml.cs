using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ReAnimated.App.Views;

public partial class TimelinePanel : UserControl
{
    private bool _synchronizingVerticalScroll;

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
