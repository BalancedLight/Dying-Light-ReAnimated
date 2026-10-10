using CommunityToolkit.Mvvm.Input;
using System.Globalization;

namespace ReAnimated.App.ViewModels;

public sealed partial class TimelineViewModel
{
    private TimelineCurveTrackViewModel? _selectedCurve;
    private double? _selectedKeyFrame;
    private double _keyFrameEdit;
    private double _keyValueEdit;
    private string _keyFrameText = "0";
    private string _keyValueText = "0";
    private bool _keyFrameDraftDirty;
    private bool _keyValueDraftDirty;
    private double _keyDragThresholdX = 4;
    private double _keyDragThresholdY = 4;
    private string _keyEditFeedback = string.Empty;
    private bool _canAuthorKeys = true;
    private bool _canCreateLayer;
    private bool _snapKeyFrames = true;
    private KeyDragState? _keyDrag;
    private double _curveMinimum;
    private double _curveMaximum = 1;

    public event EventHandler? SelectedTrackChanged;
    public event EventHandler? EditLayerRequested;
    public event EventHandler<TimelineKeyEditRequestEventArgs>? KeyEditRequested;

    public string? SelectedTrackId => SelectedTrack?.Id;
    public string? SelectedComponent => SelectedCurve?.Name;
    public double? SelectedKeyFrame => _selectedKeyFrame;
    public bool HasSelectedKey => _selectedKeyFrame.HasValue;
    public bool CanEditSelectedTrack => SelectedTrack is { IsReadOnly: false } track &&
        (track.Id.StartsWith("edit:", StringComparison.Ordinal) || track.Id.StartsWith("bone:", StringComparison.Ordinal) || track.Id.StartsWith("morph:", StringComparison.Ordinal));
    public bool CanEditSelectedKey => HasSelectedKey && CanEditSelectedTrack;
    public bool CanEditKeyValue => CanEditSelectedKey && SelectedCurve is not null;
    public bool CanCreateEditLayer => _canCreateLayer && SelectedTrack is { IsReadOnly: true };
    public bool CanAddKey => _canAuthorKeys;
    public bool SnapKeyFrames { get => _snapKeyFrames; set => SetProperty(ref _snapKeyFrames, value); }
    public double KeyFrameEdit
    {
        get => _keyFrameEdit;
        set
        {
            if (!SetProperty(ref _keyFrameEdit, value)) return;
            _keyFrameDraftDirty = true;
            SetProperty(ref _keyFrameText, value.ToString("R", CultureInfo.InvariantCulture), nameof(KeyFrameText));
        }
    }
    public double KeyValueEdit
    {
        get => _keyValueEdit;
        set
        {
            if (!SetProperty(ref _keyValueEdit, value)) return;
            _keyValueDraftDirty = true;
            SetProperty(ref _keyValueText, value.ToString("R", CultureInfo.InvariantCulture), nameof(KeyValueText));
        }
    }
    public string KeyFrameText
    {
        get => _keyFrameText;
        set { if (SetProperty(ref _keyFrameText, value ?? string.Empty)) _keyFrameDraftDirty = true; }
    }
    public string KeyValueText
    {
        get => _keyValueText;
        set { if (SetProperty(ref _keyValueText, value ?? string.Empty)) _keyValueDraftDirty = true; }
    }
    public void MarkKeyFrameDraftEdited() => _keyFrameDraftDirty = true;
    public void MarkKeyValueDraftEdited() => _keyValueDraftDirty = true;
    public string KeyEditFeedback => _keyEditFeedback;
    public string KeySelectionLabel => HasSelectedKey ? $"Key {KeyFrameEdit:0.###}" : "Select a key";
    public string TrackAccessLabel => SelectedTrack is null ? "Select a track" :
        SelectedTrack.IsReadOnly ? "Source" : CanEditSelectedTrack ? "Editable" : "Preview";
    public IRelayCommand ApplyKeyEditCommand { get; private set; } = null!;
    public IRelayCommand DeleteKeyCommand { get; private set; } = null!;
    public IRelayCommand CreateEditLayerCommand { get; private set; } = null!;

    public TimelineCurveTrackViewModel? SelectedCurve
    {
        get => _selectedCurve;
        set
        {
            if (!SetProperty(ref _selectedCurve, value)) return;
            OnPropertyChanged(nameof(SelectedComponent));
            SynchronizeKeyInspector();
            RebuildCurveGeometry();
        }
    }

    private void InitializeKeyEditing()
    {
        ApplyKeyEditCommand = new RelayCommand(ApplyKeyEdit, () => CanEditSelectedKey);
        DeleteKeyCommand = new RelayCommand(DeleteSelectedKey, () => CanEditSelectedKey);
        CreateEditLayerCommand = new RelayCommand(() => EditLayerRequested?.Invoke(this, EventArgs.Empty), () => CanCreateEditLayer);
    }

    public void SetAuthoringAvailability(bool canAdd, bool canCreateLayer)
    {
        _canAuthorKeys = canAdd;
        _canCreateLayer = canCreateLayer;
        NotifyKeyEditingState();
    }

    public void SetKeyEditFeedback(string message)
    {
        _keyEditFeedback = message;
        OnPropertyChanged(nameof(KeyEditFeedback));
    }

    private void OnSelectedTrackEditingChanged(string? previousId)
    {
        if (!string.Equals(previousId, SelectedTrackId, StringComparison.Ordinal))
        {
            EndKeyDrag(cancel: true);
            _selectedKeyFrame = null;
            _selectedCurve = Curves.FirstOrDefault();
            SetKeyEditFeedback(string.Empty);
        }
        SynchronizeKeyInspector();
        RebuildVisibleKeyframes();
        RebuildCurveGeometry();
        SelectedTrackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectKey(string trackId, double frame, string? component = null)
    {
        if (!double.IsFinite(frame)) return;
        SelectTrack(trackId);
        if (SelectedTrackId != trackId) return;
        if (component is not null) SelectedCurve = Curves.FirstOrDefault(curve => curve.Name == component);
        _selectedKeyFrame = frame;
        IsPlaying = false;
        PositionFrame = frame - SourceFrameOffset;
        SetKeyEditFeedback(string.Empty);
        SynchronizeKeyInspector();
        RebuildVisibleKeyframes();
        RebuildCurveGeometry();
    }

    public void ClearKeySelection()
    {
        _selectedKeyFrame = null;
        SynchronizeKeyInspector();
        RebuildVisibleKeyframes();
        RebuildCurveGeometry();
    }

    private void SynchronizeKeyInspector()
    {
        if (_selectedKeyFrame is { } frame)
        {
            SetProperty(ref _keyFrameEdit, frame - SourceFrameOffset, nameof(KeyFrameEdit));
            TimelineCurveKeyViewModel? key = SelectedCurve?.Keys
                .Where(key => Math.Abs(key.Frame - frame) < 1e-7)
                .Select(static key => (TimelineCurveKeyViewModel?)key).FirstOrDefault();
            SetProperty(ref _keyValueEdit, key?.Value ?? 0, nameof(KeyValueEdit));
        }
        else
        {
            SetProperty(ref _keyFrameEdit, 0, nameof(KeyFrameEdit));
            SetProperty(ref _keyValueEdit, 0, nameof(KeyValueEdit));
        }
        SetProperty(ref _keyFrameText, KeyFrameEdit.ToString("R", CultureInfo.InvariantCulture), nameof(KeyFrameText));
        SetProperty(ref _keyValueText, KeyValueEdit.ToString("R", CultureInfo.InvariantCulture), nameof(KeyValueText));
        _keyFrameDraftDirty = false;
        _keyValueDraftDirty = false;
        NotifyKeyEditingState();
    }

    private void OnKeyContentReplaced(bool curvesChanged)
    {
        if (_selectedKeyFrame is { } frame && !SelectedKeyExists(frame, curvesChanged)) _selectedKeyFrame = null;
        SynchronizeKeyInspector();
        RebuildVisibleKeyframes();
        RebuildCurveGeometry();
    }

    private bool SelectedKeyExists(double frame, bool curvesChanged)
    {
        if (SelectedTrack is not { } track) return false;
        if (track.IsReadOnly)
            return track.ExactKeyPositions.Any(position => Math.Abs(position - frame) < 1e-7) ||
                   track.Keyframes.Any(key => Math.Abs(key.Frame - frame) < 1e-7) ||
                   Curves.Any(curve => curve.Keys.Any(key => Math.Abs(key.Frame - frame) < 1e-7));
        if (curvesChanged)
            return Curves.Any(curve => curve.Keys.Any(key => Math.Abs(key.Frame - frame) < 1e-7));
        return track.Keyframes.Any(key => Math.Abs(key.Frame - frame) < 1e-7);
    }

    private void NotifyKeyEditingState()
    {
        OnPropertyChanged(nameof(SelectedTrackId));
        OnPropertyChanged(nameof(SelectedCurve));
        OnPropertyChanged(nameof(SelectedKeyFrame));
        OnPropertyChanged(nameof(HasSelectedKey));
        OnPropertyChanged(nameof(CanEditSelectedTrack));
        OnPropertyChanged(nameof(CanEditSelectedKey));
        OnPropertyChanged(nameof(CanEditKeyValue));
        OnPropertyChanged(nameof(CanCreateEditLayer));
        OnPropertyChanged(nameof(CanAddKey));
        OnPropertyChanged(nameof(KeySelectionLabel));
        OnPropertyChanged(nameof(TrackAccessLabel));
        AddKeyframeCommand.NotifyCanExecuteChanged();
        ApplyKeyEditCommand?.NotifyCanExecuteChanged();
        DeleteKeyCommand?.NotifyCanExecuteChanged();
        CreateEditLayerCommand?.NotifyCanExecuteChanged();
    }

    private void ApplyKeyEdit()
    {
        if (!CanEditSelectedKey || SelectedKeyFrame is not { } originalFrame) return;
        double localFrame = originalFrame - SourceFrameOffset;
        if (_keyFrameDraftDirty && !TryParseKeyDraft(KeyFrameText, out localFrame))
        {
            SetKeyEditFeedback("Enter a finite frame.");
            return;
        }
        double? value = null;
        if (_keyValueDraftDirty && SelectedCurve is not null)
        {
            if (!TryParseKeyDraft(KeyValueText, out double parsed))
            {
                SetKeyEditFeedback("Enter a finite value.");
                return;
            }
            value = parsed;
        }
        SubmitKeyEdit(localFrame + SourceFrameOffset, value, delete: false);
    }

    private static bool TryParseKeyDraft(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public void DeleteSelectedKey() => SubmitKeyEdit(_selectedKeyFrame ?? 0, null, delete: true);

    private bool SubmitKeyEdit(double frame, double? value, bool delete, string? component = null)
    {
        if (!CanEditSelectedKey || SelectedTrackId is not { } trackId || _selectedKeyFrame is not { } original) return false;
        if (!double.IsFinite(frame) || (value.HasValue && !double.IsFinite(value.Value)) ||
            frame < StartFrame + SourceFrameOffset || frame > EndFrame + SourceFrameOffset)
        {
            SetKeyEditFeedback("Enter a finite value and a frame inside the clip.");
            return false;
        }
        var request = new TimelineKeyEditRequestEventArgs(trackId, component ?? SelectedComponent, original, frame, value, delete);
        KeyEditRequested?.Invoke(this, request);
        if (!request.Success)
        {
            SetKeyEditFeedback(request.Error ?? "This key could not be changed.");
            return false;
        }
        if (delete) _selectedKeyFrame = null;
        else _selectedKeyFrame = frame;
        SetKeyEditFeedback(delete ? "Key removed" : "Key updated");
        SynchronizeKeyInspector();
        RebuildVisibleKeyframes();
        RebuildCurveGeometry();
        return true;
    }

    public bool BeginKeyDrag(string trackId, double frame, string? component, double pixelX, double pixelY)
    {
        EndKeyDrag(cancel: true);
        SelectKey(trackId, frame, component);
        if (!CanEditSelectedKey) return false;
        _keyDrag = new KeyDragState(SelectedTrack!, frame, KeyValueEdit, component, pixelX, pixelY,
            _curveMinimum, _curveMaximum, _allCurves.ToArray(), SelectedTrack!.Keyframes.ToArray());
        return true;
    }

    public void SetKeyDragThreshold(double horizontal, double vertical)
    {
        if (!double.IsFinite(horizontal) || !double.IsFinite(vertical) || horizontal < 0 || vertical < 0)
            throw new ArgumentOutOfRangeException(nameof(horizontal));
        _keyDragThresholdX = horizontal;
        _keyDragThresholdY = vertical;
    }

    public void CancelKeyDrag() => EndKeyDrag(cancel: true);

    public void PreviewKeyDrag(double pixelX, double pixelY)
    {
        if (_keyDrag is not { } drag || SelectedTrack is null) return;
        if (!ReferenceEquals(SelectedTrack, drag.Track)) { EndKeyDrag(cancel: true); return; }
        double deltaX = pixelX - drag.PixelX;
        double deltaY = pixelY - drag.PixelY;
        bool moveTime = Math.Abs(deltaX) >= _keyDragThresholdX && Math.Abs(deltaX) > 1e-9;
        bool moveValue = drag.Component is not null && Math.Abs(deltaY) >= _keyDragThresholdY && Math.Abs(deltaY) > 1e-9;
        if (!drag.Started && !moveTime && !moveValue) return;
        if (!drag.Started) _keyDrag = drag = drag with { Started = true };
        double frame = moveTime ? drag.Frame + deltaX / PixelsPerFrame : drag.Frame;
        if (SnapKeyFrames && moveTime) frame = Math.Round(frame - SourceFrameOffset) + SourceFrameOffset;
        frame = Math.Clamp(frame, StartFrame + SourceFrameOffset, EndFrame + SourceFrameOffset);
        double value = drag.Value;
        if (moveValue)
        {
            double height = Math.Max(1, CurveCanvasHeight - 14 - CurveTop);
            value -= deltaY / height * (drag.Maximum - drag.Minimum);
        }
        if (!double.IsFinite(frame) || !double.IsFinite(value)) return;
        // Keep the domain unchanged during a drag. Mouse-up performs one history commit.
        if (drag.Curves.Any(curve => curve.OwnerTrackId == SelectedTrackId &&
            curve.Keys.Any(key => Math.Abs(key.Frame - drag.Frame) > 1e-7 && Math.Abs(key.Frame - frame) < 1e-7)))
        {
            SetKeyEditFeedback("Another key already uses this frame.");
            return;
        }
        _allCurves.Clear();
        foreach (TimelineCurveTrackViewModel curve in drag.Curves)
        {
            _allCurves.Add(curve.OwnerTrackId == SelectedTrackId
                ? new TimelineCurveTrackViewModel(curve.Name, curve.Color, curve.Keys.Select(key =>
                    Math.Abs(key.Frame - drag.Frame) < 1e-7
                        ? new TimelineCurveKeyViewModel(frame, curve.Name == drag.Component ? value : key.Value)
                        : key), curve.OwnerTrackId, curve.OwnerLabel)
                : curve);
        }
        SelectedTrack.Keyframes.Clear();
        foreach (TimelineKeyframeViewModel key in drag.Keys)
            SelectedTrack.Keyframes.Add(Math.Abs(key.Frame - drag.Frame) < 1e-7 ? key with { Frame = frame } : key);
        _selectedKeyFrame = frame;
        KeyFrameEdit = frame - SourceFrameOffset;
        KeyValueEdit = value;
        FilterCurves();
        RebuildVisibleKeyframes();
        PositionFrame = frame - SourceFrameOffset;
    }

    public void EndKeyDrag(bool cancel = false)
    {
        if (_keyDrag is not { } drag) return;
        double frame = _selectedKeyFrame ?? drag.Frame;
        double value = KeyValueEdit;
        _keyDrag = null;
        _allCurves.Clear();
        _allCurves.AddRange(drag.Curves);
        drag.Track.Keyframes.Clear();
        foreach (TimelineKeyframeViewModel key in drag.Keys) drag.Track.Keyframes.Add(key);
        bool sameTrack = ReferenceEquals(SelectedTrack, drag.Track);
        _selectedKeyFrame = sameTrack ? drag.Frame : null;
        FilterCurves();
        RebuildVisibleKeyframes();
        if (!cancel && sameTrack && drag.Started && (Math.Abs(frame - drag.Frame) > 1e-7 || (drag.Component is not null && Math.Abs(value - drag.Value) > 1e-10)))
            SubmitKeyEdit(frame, drag.Component is null || Math.Abs(value - drag.Value) < 1e-12 ? null : value, false, drag.Component);
        else SynchronizeKeyInspector();
    }

    private sealed record KeyDragState(TimelineTrackViewModel Track, double Frame, double Value, string? Component, double PixelX, double PixelY,
        double Minimum, double Maximum, TimelineCurveTrackViewModel[] Curves, TimelineKeyframeViewModel[] Keys, bool Started = false);
}

public sealed class TimelineKeyEditRequestEventArgs(string trackId, string? component, double originalFrame,
    double frame, double? value, bool delete = false) : EventArgs
{
    public string TrackId { get; } = trackId;
    public string? Component { get; } = component;
    public double OriginalFrame { get; } = originalFrame;
    public double Frame { get; } = frame;
    public double? Value { get; } = value;
    public bool Delete { get; } = delete;
    public bool Handled { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
