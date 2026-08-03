using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReAnimated.App.ViewModels;

public sealed class TimelineViewModel : ObservableObject
{
    private const double DefaultPixelsPerFrame = 6.0;
    private const double MinimumPixelsPerFrame = 0.05;
    private const double MaximumPixelsPerFrame = 24.0;
    private const double FitCanvasWidth = 1080.0;
    private const double TrackHeaderHeight = 24.0;
    private const double TrackRowHeight = 28.0;
    private const double CurveTop = 32.0;
    private const double CurveBottom = 166.0;
    private readonly int _startFrame;
    private readonly List<TimelineCurveTrackViewModel> _allCurves = [];
    private int _currentFrame;
    private int _endFrame;
    private double _pixelsPerFrame = DefaultPixelsPerFrame;
    private double _framesPerSecond = 30.0;
    private double _playbackFrameRemainder;
    private bool _isPlaying;
    private bool _isPlaybackEnabled = true;
    private bool _isLooping = true;
    private bool _settingFrameFromPlayback;
    private DateTimeOffset? _lastTick;
    private string _trackSearchText = string.Empty;
    private string _selectedTrackScope = "All";
    private TimelineTrackViewModel? _selectedTrack;

    public TimelineViewModel(int startFrame = 0, int endFrame = 120)
    {
        _startFrame = startFrame;
        _currentFrame = startFrame;
        _endFrame = Math.Max(startFrame + 1, endFrame);
        TogglePlaybackCommand = new RelayCommand(
            () => IsPlaying = !IsPlaying,
            () => IsPlaybackEnabled);
        StopCommand = new RelayCommand(Stop);
        StepBackwardCommand = new RelayCommand(
            () => CurrentFrame = Math.Max(StartFrame, CurrentFrame - 1));
        StepForwardCommand = new RelayCommand(
            () => CurrentFrame = Math.Min(EndFrame, CurrentFrame + 1));
        AddKeyframeCommand = new RelayCommand(AddKeyframe);
        FitTimelineCommand = new RelayCommand(FitTimeline);
        ZoomInCommand = new RelayCommand(
            () => SetPixelsPerFrame(PixelsPerFrame * 1.5));
        ZoomOutCommand = new RelayCommand(
            () => SetPixelsPerFrame(PixelsPerFrame / 1.5));
        FitTimeline();
    }

    public int StartFrame => _startFrame;

    public double FramesPerSecond
    {
        get => _framesPerSecond;
        set
        {
            if (!double.IsFinite(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    "Timeline playback rate must be finite and positive.");
            }

            if (SetProperty(ref _framesPerSecond, value))
            {
                ResetPlaybackClock();
            }
        }
    }

    public ObservableCollection<TimelineTrackViewModel> Tracks { get; } = [];

    public ObservableCollection<TimelineTrackViewModel> VisibleTracks { get; } = [];

    public ObservableCollection<TimelineKeyframeViewModel> VisibleKeyframes { get; } = [];

    public ObservableCollection<TimelineFrameMarkerViewModel> FrameMarkers { get; } = [];

    /// <summary>
    /// Curves belonging to the currently selected channel. Source clips can
    /// contain hundreds of channels; drawing every one simultaneously makes
    /// the graph unreadable and needlessly expensive.
    /// </summary>
    public ObservableCollection<TimelineCurveTrackViewModel> Curves { get; } = [];

    public ObservableCollection<TimelineCurveSegmentViewModel> CurveSegments { get; } = [];

    public ObservableCollection<TimelineCurvePointViewModel> CurvePoints { get; } = [];

    public IReadOnlyList<string> TrackScopeOptions { get; } =
    [
        "All",
        "Source animation",
        "Authored edits",
        "Facial",
        "IK / attachments",
    ];

    public event EventHandler? CurrentFrameChanged;

    public event EventHandler? KeyframeRequested;

    public IRelayCommand TogglePlaybackCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IRelayCommand StepBackwardCommand { get; }

    public IRelayCommand StepForwardCommand { get; }

    public IRelayCommand AddKeyframeCommand { get; }

    public IRelayCommand FitTimelineCommand { get; }

    public IRelayCommand ZoomInCommand { get; }

    public IRelayCommand ZoomOutCommand { get; }

    public string TrackSearchText
    {
        get => _trackSearchText;
        set
        {
            if (SetProperty(
                    ref _trackSearchText,
                    value ?? string.Empty))
            {
                RebuildFilteredTracks();
            }
        }
    }

    public string SelectedTrackScope
    {
        get => _selectedTrackScope;
        set
        {
            string normalized = TrackScopeOptions.Contains(value)
                ? value
                : "All";
            if (SetProperty(ref _selectedTrackScope, normalized))
            {
                RebuildFilteredTracks();
            }
        }
    }

    public TimelineTrackViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                OnPropertyChanged(nameof(SelectedTrackLabel));
                FilterCurves();
                RebuildVisibleKeyframes();
            }
        }
    }

    public string SelectedTrackLabel => SelectedTrack is null
        ? "Select a channel to inspect its curves"
        : $"{SelectedTrack.Name}  |  {SelectedTrack.Channel}";

    public string VisibleTrackCountLabel =>
        $"{VisibleTracks.Count:N0} of {Tracks.Count:N0} tracks";

    public string CurveStatusLabel => Curves.Count == 0
        ? "No numeric curves are available for the selected channel."
        : $"{Curves.Count:N0} components | shared value scale | source values are read-only";

    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            int normalized = Math.Clamp(value, StartFrame, EndFrame);
            bool changed = SetProperty(ref _currentFrame, normalized);
            if (!_settingFrameFromPlayback)
            {
                ResetPlaybackClock();
            }

            if (changed)
            {
                OnPropertyChanged(nameof(CurrentFramePixelX));
                CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int FrameFromPixel(double pixelX)
    {
        if (!double.IsFinite(pixelX))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelX));
        }

        double frame = StartFrame +
                       (Math.Max(0.0, pixelX) / PixelsPerFrame);
        double clamped = Math.Clamp(frame, StartFrame, EndFrame);
        return checked((int)Math.Round(clamped));
    }

    public void ScrubToPixel(double pixelX)
    {
        IsPlaying = false;
        CurrentFrame = FrameFromPixel(pixelX);
    }

    public TimelineTrackViewModel? SelectTrackFromCanvasY(double pixelY)
    {
        if (!double.IsFinite(pixelY))
        {
            throw new ArgumentOutOfRangeException(nameof(pixelY));
        }

        int index = checked((int)Math.Floor(
            (pixelY - TrackHeaderHeight) / TrackRowHeight));
        if ((uint)index >= (uint)VisibleTracks.Count)
        {
            return null;
        }

        SelectedTrack = VisibleTracks[index];
        return SelectedTrack;
    }

    public int EndFrame
    {
        get => _endFrame;
        set
        {
            int normalized = Math.Max(StartFrame + 1, value);
            if (SetProperty(ref _endFrame, normalized))
            {
                CurrentFrame = Math.Min(CurrentFrame, normalized);
                FitTimeline();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            bool normalized = value && IsPlaybackEnabled;
            if (SetProperty(ref _isPlaying, normalized))
            {
                ResetPlaybackClock();
                OnPropertyChanged(nameof(PlaybackLabel));
            }
        }
    }

    public bool IsPlaybackEnabled
    {
        get => _isPlaybackEnabled;
        set
        {
            if (!SetProperty(ref _isPlaybackEnabled, value))
            {
                return;
            }

            if (!value)
            {
                IsPlaying = false;
            }

            TogglePlaybackCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(PlaybackLabel));
        }
    }

    public bool IsLooping
    {
        get => _isLooping;
        set => SetProperty(ref _isLooping, value);
    }

    public string PlaybackLabel => !IsPlaybackEnabled
        ? "Playback locked"
        : IsPlaying
            ? "Pause"
            : "Play";

    public double PixelsPerFrame => _pixelsPerFrame;

    public string ZoomLabel => $"{PixelsPerFrame:0.##} px/frame";

    public double CanvasWidth => Math.Max(
        720.0,
        ((EndFrame - StartFrame) * PixelsPerFrame) + 40.0);

    public double CurrentFramePixelX => ToPixel(CurrentFrame);

    public double DopeSheetCanvasHeight => Math.Max(
        160.0,
        TrackHeaderHeight + (VisibleTracks.Count * TrackRowHeight));

    public double TimelineGridHeight => Math.Max(
        136.0,
        DopeSheetCanvasHeight - TrackHeaderHeight);

    public void Tick(DateTimeOffset now)
    {
        if (!IsPlaying)
        {
            ResetPlaybackClock();
            return;
        }

        if (_lastTick is not DateTimeOffset previous)
        {
            _lastTick = now;
            return;
        }

        _lastTick = now;
        TimeSpan elapsed = now - previous;
        if (elapsed <= TimeSpan.Zero)
        {
            _playbackFrameRemainder = 0.0;
            return;
        }

        double accumulatedFrames =
            _playbackFrameRemainder +
            (elapsed.TotalSeconds * FramesPerSecond);
        long wholeFrames;
        if (!double.IsFinite(accumulatedFrames) ||
            accumulatedFrames >= long.MaxValue)
        {
            wholeFrames = long.MaxValue;
            _playbackFrameRemainder = 0.0;
        }
        else
        {
            wholeFrames = (long)Math.Floor(accumulatedFrames + 1.0e-9);
            _playbackFrameRemainder = Math.Max(
                0.0,
                accumulatedFrames - wholeFrames);
        }

        if (wholeFrames <= 0)
        {
            return;
        }

        if (IsLooping)
        {
            long span = (long)EndFrame - StartFrame + 1L;
            long offset =
                ((long)CurrentFrame - StartFrame +
                 (wholeFrames % span)) %
                span;
            SetCurrentFrameFromPlayback(
                checked(StartFrame + (int)offset));
            return;
        }

        long framesRemaining = (long)EndFrame - CurrentFrame;
        if (wholeFrames >= framesRemaining)
        {
            SetCurrentFrameFromPlayback(EndFrame);
            IsPlaying = false;
            return;
        }

        SetCurrentFrameFromPlayback(
            checked(CurrentFrame + (int)wholeFrames));
    }

    public void ReplaceTracks(IEnumerable<TimelineTrackViewModel> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        string? selectedId = SelectedTrack?.Id;
        Tracks.Clear();
        foreach (TimelineTrackViewModel track in tracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            Tracks.Add(track);
        }

        RebuildFilteredTracks(selectedId);
    }

    public void SelectTrack(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return;
        }

        TimelineTrackViewModel? track = VisibleTracks.FirstOrDefault(
            item => string.Equals(
                item.Id,
                trackId,
                StringComparison.Ordinal));
        if (track is not null)
        {
            SelectedTrack = track;
        }
    }

    public void ReplaceCurves(
        IEnumerable<TimelineCurveTrackViewModel> curves)
    {
        ArgumentNullException.ThrowIfNull(curves);
        _allCurves.Clear();
        foreach (TimelineCurveTrackViewModel curve in curves)
        {
            ArgumentNullException.ThrowIfNull(curve);
            _allCurves.Add(curve);
        }

        FilterCurves();
    }

    private void Stop()
    {
        IsPlaying = false;
        CurrentFrame = StartFrame;
    }

    private void SetCurrentFrameFromPlayback(int frame)
    {
        _settingFrameFromPlayback = true;
        try
        {
            CurrentFrame = frame;
        }
        finally
        {
            _settingFrameFromPlayback = false;
        }
    }

    private void ResetPlaybackClock()
    {
        _lastTick = null;
        _playbackFrameRemainder = 0.0;
    }

    private void AddKeyframe()
    {
        EventHandler? handler = KeyframeRequested;
        if (handler is not null)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        TimelineTrackViewModel track;
        if (Tracks.Count == 0)
        {
            track = new TimelineTrackViewModel(
                "editor:selected-bone",
                "Selected bone",
                "Transform",
                "Authored edits",
                isReadOnly: false);
            Tracks.Add(track);
            RebuildFilteredTracks(track.Id);
        }
        else
        {
            track = SelectedTrack ?? Tracks[0];
        }

        if (track.Keyframes.All(item => item.Frame != CurrentFrame))
        {
            track.Keyframes.Add(
                new TimelineKeyframeViewModel(
                    track.Name,
                    CurrentFrame,
                    ToPixel(CurrentFrame),
                    12.0));
        }

        RebuildVisibleKeyframes();
    }

    private void FitTimeline()
    {
        double span = Math.Max(1.0, EndFrame - StartFrame);
        SetPixelsPerFrame(Math.Min(
            DefaultPixelsPerFrame,
            FitCanvasWidth / span));
    }

    private void SetPixelsPerFrame(double value)
    {
        double normalized = Math.Clamp(
            value,
            MinimumPixelsPerFrame,
            MaximumPixelsPerFrame);
        if (Math.Abs(normalized - _pixelsPerFrame) < 1.0e-9)
        {
            RebuildPresentationGeometry();
            return;
        }

        _pixelsPerFrame = normalized;
        OnPropertyChanged(nameof(PixelsPerFrame));
        OnPropertyChanged(nameof(ZoomLabel));
        RebuildPresentationGeometry();
    }

    private void RebuildPresentationGeometry()
    {
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CurrentFramePixelX));
        RebuildFrameMarkers();
        RebuildVisibleKeyframes();
        RebuildCurveGeometry();
    }

    private void RebuildFrameMarkers()
    {
        FrameMarkers.Clear();
        int interval = CalculateMarkerInterval();
        for (int frame = StartFrame; frame <= EndFrame; frame += interval)
        {
            FrameMarkers.Add(
                new TimelineFrameMarkerViewModel(
                    frame,
                    ToPixel(frame)));
        }
    }

    private int CalculateMarkerInterval()
    {
        double desiredFrames = 80.0 / PixelsPerFrame;
        double power = Math.Pow(
            10.0,
            Math.Floor(Math.Log10(Math.Max(1.0, desiredFrames))));
        foreach (double multiplier in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            double candidate = multiplier * power;
            if (candidate >= desiredFrames)
            {
                return Math.Max(1, checked((int)Math.Ceiling(candidate)));
            }
        }

        return Math.Max(1, checked((int)Math.Ceiling(desiredFrames)));
    }

    private void RebuildFilteredTracks(string? preferredTrackId = null)
    {
        string? selection = preferredTrackId ?? SelectedTrack?.Id;
        VisibleTracks.Clear();
        foreach (TimelineTrackViewModel track in Tracks.Where(MatchesTrackFilter))
        {
            VisibleTracks.Add(track);
        }

        TimelineTrackViewModel? selected = VisibleTracks.FirstOrDefault(
            item => string.Equals(
                item.Id,
                selection,
                StringComparison.Ordinal)) ??
            VisibleTracks.FirstOrDefault();
        SelectedTrack = selected;
        OnPropertyChanged(nameof(VisibleTrackCountLabel));
        OnPropertyChanged(nameof(DopeSheetCanvasHeight));
        OnPropertyChanged(nameof(TimelineGridHeight));
        RebuildVisibleKeyframes();
    }

    private bool MatchesTrackFilter(TimelineTrackViewModel track)
    {
        bool scopeMatches = SelectedTrackScope switch
        {
            "Source animation" => track.Group == "Source animation",
            "Authored edits" => track.Group == "Authored edits",
            "Facial" => track.Group == "Facial",
            "IK / attachments" => track.Group is "IK" or "Attachments",
            _ => true,
        };
        if (!scopeMatches || string.IsNullOrWhiteSpace(TrackSearchText))
        {
            return scopeMatches;
        }

        string query = TrackSearchText.Trim();
        return track.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               track.Channel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               track.Group.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildVisibleKeyframes()
    {
        VisibleKeyframes.Clear();
        for (int trackIndex = 0; trackIndex < VisibleTracks.Count; trackIndex++)
        {
            TimelineTrackViewModel track = VisibleTracks[trackIndex];
            bool isSelected = ReferenceEquals(track, SelectedTrack);
            IEnumerable<TimelineKeyframeViewModel> presentationKeys;
            if (track.ExactKeyFrames.Count > 0)
            {
                IEnumerable<int> exactFrames = isSelected
                    ? track.ExactKeyFrames
                    : SelectEvenlySpacedPresentationFrames(
                        track.ExactKeyFrames,
                        maximumCount: 12);
                presentationKeys = exactFrames.Select(frame =>
                    new TimelineKeyframeViewModel(
                        track.Name,
                        frame,
                        ToPixel(frame),
                        0.0));
            }
            else
            {
                presentationKeys = isSelected || !track.IsReadOnly
                    ? track.Keyframes
                    : SelectEvenlySpacedPresentationKeys(
                        track.Keyframes,
                        maximumCount: 12);
            }
            foreach (TimelineKeyframeViewModel keyframe in
                     presentationKeys)
            {
                VisibleKeyframes.Add(keyframe with
                {
                    PixelX = ToPixel(keyframe.Frame),
                    TrackY = TrackHeaderHeight + 9.0 +
                             (trackIndex * TrackRowHeight),
                    IsSelectedTrack = isSelected,
                });
            }
        }
    }

    private static IEnumerable<TimelineKeyframeViewModel>
        SelectEvenlySpacedPresentationKeys(
            ObservableCollection<TimelineKeyframeViewModel> keys,
            int maximumCount)
    {
        if (keys.Count <= maximumCount)
        {
            return keys;
        }

        var selected = new TimelineKeyframeViewModel[maximumCount];
        for (var index = 0; index < maximumCount; index++)
        {
            int sourceIndex = checked((int)Math.Round(
                index * (keys.Count - 1.0) /
                (maximumCount - 1.0)));
            selected[index] = keys[sourceIndex];
        }

        return selected;
    }

    private static IEnumerable<int> SelectEvenlySpacedPresentationFrames(
        IReadOnlyList<int> frames,
        int maximumCount)
    {
        if (frames.Count <= maximumCount)
        {
            return frames;
        }

        var selected = new int[maximumCount];
        for (var index = 0; index < maximumCount; index++)
        {
            int sourceIndex = checked((int)Math.Round(
                index * (frames.Count - 1.0) /
                (maximumCount - 1.0)));
            selected[index] = frames[sourceIndex];
        }

        return selected;
    }

    private void FilterCurves()
    {
        Curves.Clear();
        bool usesTrackOwnership = _allCurves.Any(
            static curve => curve.OwnerTrackId is not null);
        IEnumerable<TimelineCurveTrackViewModel> visible =
            usesTrackOwnership
                ? SelectedTrack is null
                    ? []
                    : _allCurves.Where(curve => string.Equals(
                        curve.OwnerTrackId,
                        SelectedTrack.Id,
                        StringComparison.Ordinal))
                : _allCurves;
        foreach (TimelineCurveTrackViewModel curve in visible)
        {
            Curves.Add(curve);
        }

        OnPropertyChanged(nameof(CurveStatusLabel));
        RebuildCurveGeometry();
    }

    private void RebuildCurveGeometry()
    {
        CurveSegments.Clear();
        CurvePoints.Clear();
        TimelineCurveKeyViewModel[] keys = Curves
            .SelectMany(static curve => curve.Keys)
            .ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        double minimum = keys.Min(static key => key.Value);
        double maximum = keys.Max(static key => key.Value);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            throw new InvalidDataException(
                "Timeline curve values must be finite.");
        }

        if (Math.Abs(maximum - minimum) < 1.0e-12)
        {
            minimum -= 1.0;
            maximum += 1.0;
        }

        foreach (TimelineCurveTrackViewModel curve in Curves)
        {
            TimelineCurvePointViewModel[] points = curve.Keys
                .Select(key => new TimelineCurvePointViewModel(
                    curve.Name,
                    curve.Color,
                    key.Frame,
                    key.Value,
                    ToPixel(key.Frame),
                    CurveBottom -
                    ((key.Value - minimum) /
                     (maximum - minimum) *
                     (CurveBottom - CurveTop))))
                .ToArray();
            foreach (TimelineCurvePointViewModel point in points)
            {
                CurvePoints.Add(point);
            }

            for (int index = 1; index < points.Length; index++)
            {
                CurveSegments.Add(new TimelineCurveSegmentViewModel(
                    curve.Name,
                    curve.Color,
                    points[index - 1].PixelX,
                    points[index - 1].PixelY,
                    points[index].PixelX,
                    points[index].PixelY,
                    points[index - 1].Value,
                    points[index].Value));
            }
        }
    }

    private double ToPixel(double frame) =>
        (frame - StartFrame) * PixelsPerFrame;
}

public sealed class TimelineTrackViewModel : ObservableObject
{
    public TimelineTrackViewModel(string name, string channel)
        : this(
            $"legacy:{name}:{channel}",
            name,
            channel,
            "Other",
            isReadOnly: false)
    {
    }

    public TimelineTrackViewModel(
        string id,
        string name,
        string channel,
        string group,
        bool isReadOnly,
        int? totalKeyCount = null,
        IEnumerable<int>? exactKeyFrames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        Id = id;
        Name = name;
        Channel = channel;
        Group = group;
        IsReadOnly = isReadOnly;
        ExactKeyFrames = exactKeyFrames?
            .Distinct()
            .Order()
            .ToArray() ?? [];
        TotalKeyCount = totalKeyCount ??
            (ExactKeyFrames.Count > 0
                ? ExactKeyFrames.Count
                : null);
        Keyframes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(KeyCountLabel));
            OnPropertyChanged(nameof(KeyPresentationLabel));
        };
    }

    public string Id { get; }

    public string Name { get; }

    public string Channel { get; }

    public string Group { get; }

    public bool IsReadOnly { get; }

    public int? TotalKeyCount { get; }

    public IReadOnlyList<int> ExactKeyFrames { get; }

    public int EffectiveKeyCount => TotalKeyCount ?? Keyframes.Count;

    public string KeyCountLabel => $"{EffectiveKeyCount:N0}";

    public string KeyPresentationLabel =>
        ExactKeyFrames.Count > 0
            ? $"{ExactKeyFrames.Count:N0} exact source keys. The selected row draws every key; unselected dense rows use representative markers."
            : EffectiveKeyCount > Keyframes.Count
            ? $"{EffectiveKeyCount:N0} source keys; {Keyframes.Count:N0} representative markers are drawn for responsive navigation."
            : $"{EffectiveKeyCount:N0} keys";

    public ObservableCollection<TimelineKeyframeViewModel> Keyframes { get; } = [];
}

public sealed record TimelineKeyframeViewModel(
    string Track,
    int Frame,
    double PixelX,
    double TrackY,
    bool IsSelectedTrack = false);

public sealed record TimelineFrameMarkerViewModel(int Frame, double PixelX);

public sealed class TimelineCurveTrackViewModel
{
    public TimelineCurveTrackViewModel(
        string name,
        string color,
        IEnumerable<TimelineCurveKeyViewModel> keys,
        string? ownerTrackId = null,
        string? ownerLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        ArgumentNullException.ThrowIfNull(keys);
        TimelineCurveKeyViewModel[] ordered = keys
            .OrderBy(static key => key.Frame)
            .ToArray();
        if (ordered.Any(static key =>
                !double.IsFinite(key.Frame) ||
                !double.IsFinite(key.Value)) ||
            ordered
                .Zip(ordered.Skip(1))
                .Any(static pair =>
                    pair.First.Frame >= pair.Second.Frame))
        {
            throw new ArgumentException(
                "Curve keys must be finite and strictly increasing.",
                nameof(keys));
        }

        Name = name;
        Color = color;
        Keys = ordered;
        OwnerTrackId = ownerTrackId;
        OwnerLabel = ownerLabel;
    }

    public string Name { get; }

    public string Color { get; }

    public IReadOnlyList<TimelineCurveKeyViewModel> Keys { get; }

    public string? OwnerTrackId { get; }

    public string? OwnerLabel { get; }
}

public readonly record struct TimelineCurveKeyViewModel(
    double Frame,
    double Value);

public readonly record struct TimelineCurvePointViewModel(
    string Track,
    string Color,
    double Frame,
    double Value,
    double PixelX,
    double PixelY);

public readonly record struct TimelineCurveSegmentViewModel(
    string Track,
    string Color,
    double X1,
    double Y1,
    double X2,
    double Y2,
    double StartValue,
    double EndValue);
