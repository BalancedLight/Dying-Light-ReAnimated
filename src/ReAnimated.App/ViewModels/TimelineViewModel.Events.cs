using System.Collections.ObjectModel;
using ReAnimated.Core.Domain;

namespace ReAnimated.App.ViewModels;

public sealed partial class TimelineViewModel
{
    private AnimationEventEditorViewModel? _events;
    private double _eventViewportLeft;
    private double _eventViewportWidth = 1080;
    public AnimationEventEditorViewModel Events => _events ??= new(this);
    public ObservableCollection<TimelineTrackViewModel> EventLanes { get; } = [];
    public ObservableCollection<TimelineEventMarker> EventMarkers { get; } = [];
    public ObservableCollection<TimelineEventSpan> EventSpans { get; } = [];
    public event EventHandler<TimelinePlaybackAdvance>? PlaybackAdvanced;

    public void SetEventViewport(double left, double width)
    {
        if (!double.IsFinite(left) || !double.IsFinite(width) || width <= 0) return;
        _eventViewportLeft = Math.Max(0, left);
        _eventViewportWidth = width;
        RefreshEventGeometry();
    }

    public void RebuildEventLanes()
    {
        EventLanes.Clear();
        foreach (string lane in new[] { "IK & Contacts", "Sound", "Visual", "Gameplay", "Other" })
        {
            int count = Events.Items.Count(e => e.Lane == lane);
            if (count > 0 && Events.ShowLanes) EventLanes.Add(new TimelineTrackViewModel("events:" + lane, lane, "Events", "Events", true, count));
        }
        RebuildFilteredTracks();
    }

    internal void RefreshEventGeometry()
    {
        EventMarkers.Clear();
        EventSpans.Clear();
        if (_events is null) return;
        int ikRow = VisibleTracks.ToList().FindIndex(t => t.Id == "events:IK & Contacts");
        if (ikRow >= 0 && _events.SelectedSequence is { } sequence)
        {
            foreach ((int begin, int end) in new[] { (1040, 1041), (1042, 1040), (1043, 1041), (1060, 1061), (1062, 1063), (1011, 1013), (1015, 1017) })
            {
                AnimationEvent? start = null;
                foreach (AnimationEvent item in sequence.Events.OrderBy(e => e.LocalFrame))
                {
                    if (item.EventId == begin) start = item;
                    else if (item.EventId == end && start is not null)
                    {
                        double x = ToPixel(start.LocalFrame);
                        double width = ToPixel(item.LocalFrame) - x;
                        if (width > 0 && x + width >= _eventViewportLeft && x <= _eventViewportLeft + _eventViewportWidth)
                            EventSpans.Add(new(x, TrackHeaderHeight + ikRow * TrackRowHeight + 3, width));
                        start = null;
                    }
                }
            }
        }
        foreach (IGrouping<string, AnimationEventItemViewModel> lane in _events.Items.GroupBy(e => e.Lane))
        {
            int row = VisibleTracks.ToList().FindIndex(t => t.Id == "events:" + lane.Key);
            if (row < 0) continue;
            var visible = lane.Where(e => double.IsFinite(e.Event.LocalFrame) && ToPixel(e.Event.LocalFrame) >= _eventViewportLeft - 16 && ToPixel(e.Event.LocalFrame) <= _eventViewportLeft + _eventViewportWidth + 16);
            foreach (var cluster in visible.GroupBy(e => Math.Floor(ToPixel(e.Event.LocalFrame) / 12)))
            {
                AnimationEventItemViewModel[] items = cluster.OrderBy(e => e.Event.LocalFrame).ToArray();
                if (PixelsPerFrame < 10 && items.Length > 1)
                {
                    EventMarkers.Add(new(items[0], items.Select(e => e.Id).ToArray(), ToPixel(items[0].Event.LocalFrame), TrackHeaderHeight + row * TrackRowHeight + 9, $"{items.Length}", items.Any(e => e.IsSelected)));
                    continue;
                }
                int index = 0;
                foreach (AnimationEventItemViewModel item in items)
                {
                    EventMarkers.Add(new(item, [item.Id], ToPixel(item.Event.LocalFrame), TrackHeaderHeight + row * TrackRowHeight + 5 + (index++ % 3) * 6, "", item.IsSelected));
                }
            }
        }
        OnPropertyChanged(nameof(EventMarkers));
    }
}

public sealed record TimelinePlaybackAdvance(double From, double To, long Loops);
public sealed record TimelineEventSpan(double PixelX, double TrackY, double Width);
public sealed record TimelineEventMarker(AnimationEventItemViewModel Item, IReadOnlyList<Guid> EventIds, double PixelX, double TrackY, string Count, bool IsSelected)
{
    public Guid Id => Item.Id;
    public string ToolTip => Item.ToolTip + (EventIds.Count > 1 ? $" · {EventIds.Count} events" : string.Empty);
    public string Color => Item.Lane switch { "IK & Contacts" => "#66C6A2", "Sound" => "#E7B75A", "Visual" => "#AC91E8", "Gameplay" => "#EC8D7A", _ => "#9AA9BA" };
}
