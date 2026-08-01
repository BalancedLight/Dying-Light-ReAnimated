using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ReAnimated.App.ViewModels;

public sealed class TimelineViewModel : ObservableObject
{
    private const double PixelsPerFrame = 6.0;
    private const double CurveTop = 16.0;
    private const double CurveBottom = 142.0;
    private readonly int _startFrame;
    private int _currentFrame;
    private int _endFrame;
    private double _framesPerSecond = 30.0;
    private double _playbackFrameRemainder;
    private bool _isPlaying;
    private bool _isLooping = true;
    private bool _settingFrameFromPlayback;
    private DateTimeOffset? _lastTick;

    public TimelineViewModel(int startFrame = 0, int endFrame = 120)
    {
        _startFrame = startFrame;
        _currentFrame = startFrame;
        _endFrame = Math.Max(startFrame + 1, endFrame);
        TogglePlaybackCommand = new RelayCommand(
            () => IsPlaying = !IsPlaying);
        StopCommand = new RelayCommand(Stop);
        StepBackwardCommand = new RelayCommand(
            () => CurrentFrame = Math.Max(StartFrame, CurrentFrame - 1));
        StepForwardCommand = new RelayCommand(
            () => CurrentFrame = Math.Min(EndFrame, CurrentFrame + 1));
        AddKeyframeCommand = new RelayCommand(AddKeyframe);
        RebuildFrameMarkers();
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

    public ObservableCollection<TimelineKeyframeViewModel> VisibleKeyframes { get; } = [];

    public ObservableCollection<TimelineFrameMarkerViewModel> FrameMarkers { get; } = [];

    public ObservableCollection<TimelineCurveTrackViewModel> Curves { get; } = [];

    public ObservableCollection<TimelineCurveSegmentViewModel> CurveSegments { get; } = [];

    public ObservableCollection<TimelineCurvePointViewModel> CurvePoints { get; } = [];

    public event EventHandler? CurrentFrameChanged;

    public event EventHandler? KeyframeRequested;

    public IRelayCommand TogglePlaybackCommand { get; }

    public IRelayCommand StopCommand { get; }

    public IRelayCommand StepBackwardCommand { get; }

    public IRelayCommand StepForwardCommand { get; }

    public IRelayCommand AddKeyframeCommand { get; }

    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            int normalized = Math.Clamp(value, StartFrame, EndFrame);
            bool changed = SetProperty(
                ref _currentFrame,
                normalized);
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

    public int EndFrame
    {
        get => _endFrame;
        set
        {
            int normalized = Math.Max(StartFrame + 1, value);
            if (SetProperty(ref _endFrame, normalized))
            {
                CurrentFrame = Math.Min(CurrentFrame, normalized);
                RebuildFrameMarkers();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                ResetPlaybackClock();
                OnPropertyChanged(nameof(PlaybackLabel));
            }
        }
    }

    public bool IsLooping
    {
        get => _isLooping;
        set => SetProperty(ref _isLooping, value);
    }

    public string PlaybackLabel => IsPlaying ? "Pause" : "Play";

    public double CanvasWidth => Math.Max(720.0, (EndFrame + 10) * PixelsPerFrame);

    public double CurrentFramePixelX => CurrentFrame * PixelsPerFrame;

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
            wholeFrames = (long)Math.Floor(
                accumulatedFrames + 1.0e-9);
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

        long framesRemaining =
            (long)EndFrame - CurrentFrame;
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
        Tracks.Clear();
        foreach (TimelineTrackViewModel track in tracks)
        {
            Tracks.Add(track);
        }

        RebuildVisibleKeyframes();
    }

    public void ReplaceCurves(
        IEnumerable<TimelineCurveTrackViewModel> curves)
    {
        ArgumentNullException.ThrowIfNull(curves);
        Curves.Clear();
        foreach (TimelineCurveTrackViewModel curve in curves)
        {
            ArgumentNullException.ThrowIfNull(curve);
            Curves.Add(curve);
        }

        RebuildCurveGeometry();
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
            track = new TimelineTrackViewModel("Selected bone", "Transform");
            Tracks.Add(track);
        }
        else
        {
            track = Tracks[0];
        }

        if (track.Keyframes.All(item => item.Frame != CurrentFrame))
        {
            track.Keyframes.Add(
                new TimelineKeyframeViewModel(
                    track.Name,
                    CurrentFrame,
                    CurrentFrame * PixelsPerFrame,
                    12.0));
        }

        RebuildVisibleKeyframes();
    }

    private void RebuildFrameMarkers()
    {
        FrameMarkers.Clear();
        for (int frame = StartFrame; frame <= EndFrame; frame += 10)
        {
            FrameMarkers.Add(
                new TimelineFrameMarkerViewModel(
                    frame,
                    frame * PixelsPerFrame));
        }

        OnPropertyChanged(nameof(CanvasWidth));
    }

    private void RebuildVisibleKeyframes()
    {
        VisibleKeyframes.Clear();
        for (int trackIndex = 0; trackIndex < Tracks.Count; trackIndex++)
        {
            foreach (TimelineKeyframeViewModel keyframe in Tracks[trackIndex].Keyframes)
            {
                VisibleKeyframes.Add(keyframe with
                {
                    TrackY = 12.0 + (trackIndex * 24.0),
                });
            }
        }
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
        if (!double.IsFinite(minimum) ||
            !double.IsFinite(maximum))
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
                    key.Frame * PixelsPerFrame,
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
}

public sealed class TimelineTrackViewModel
{
    public TimelineTrackViewModel(string name, string channel)
    {
        Name = name;
        Channel = channel;
    }

    public string Name { get; }

    public string Channel { get; }

    public ObservableCollection<TimelineKeyframeViewModel> Keyframes { get; } = [];
}

public sealed record TimelineKeyframeViewModel(
    string Track,
    int Frame,
    double PixelX,
    double TrackY);

public sealed record TimelineFrameMarkerViewModel(int Frame, double PixelX);

public sealed class TimelineCurveTrackViewModel
{
    public TimelineCurveTrackViewModel(
        string name,
        string color,
        IEnumerable<TimelineCurveKeyViewModel> keys)
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
    }

    public string Name { get; }

    public string Color { get; }

    public IReadOnlyList<TimelineCurveKeyViewModel> Keys { get; }
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
