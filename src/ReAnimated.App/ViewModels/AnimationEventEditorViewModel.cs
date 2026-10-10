using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Core.Domain;

namespace ReAnimated.App.ViewModels;

public sealed class AnimationEventEditorViewModel : ObservableObject
{
    private readonly TimelineViewModel _timeline;
    private AnimationSequenceUse? _selectedSequence;
    private AnimationEventItemViewModel? _selectedEvent;
    private string _search = string.Empty;
    private string _frameText = "0";
    private string _idText = "0";
    private string _slotText = "Any";
    private string _previewSlotText = "All";
    private string _delivery = "Event";
    private string _status = string.Empty;
    private string _snap = "Native";
    private bool _showLanes = true;
    private bool _canEdit;
    private bool _canCreate;
    private bool _isCompiled;
    private AnimationEventChoice? _definition;
    private Dl1AnimationActionDefinition? _actionDefinition;
    private ImmutableArray<AnimationEvent> _clipboard = [];
    private Guid? _clipboardSequenceId;
    private HashSet<Guid> _selection = [];
    private AnimationSequenceUse? _dragOriginal;
    private double _dragAnchor;
    private string _rangeStart = "0", _rangeEnd = "120", _fps = "30", _retime = "Keep local frames";
    private AnimationSequenceUse? _rangeCandidate;

    public AnimationEventEditorViewModel(TimelineViewModel timeline)
    {
        _timeline = timeline;
        AddEventCommand = new RelayCommand(AddEvent, () => CanEdit && SelectedSequence is not null);
        NewSequenceCommand = new RelayCommand(() => NewSequenceRequested?.Invoke(this, EventArgs.Empty), () => CanCreate && !IsCompiled);
        ApplyCommand = new RelayCommand(Apply, () => CanEdit && SelectedEvent is not null);
        DeleteCommand = new RelayCommand(Delete, () => CanEdit && _selection.Count > 0);
        DuplicateCommand = new RelayCommand(Duplicate, () => CanEdit && _selection.Count > 0);
        CopyCommand = new RelayCommand(Copy, () => _selection.Count > 0);
        PasteCommand = new RelayCommand(Paste, () => CanEdit && !_clipboard.IsEmpty && SelectedSequence is not null);
        AddActionCommand = new RelayCommand(AddAction, () => CanEdit && !IsCompiled && SelectedEvent is not null && SelectedActionDefinition is not null);
        AuditionCommand = new RelayCommand(() => AuditionRequested?.Invoke(this, EventArgs.Empty), () => SelectedEvent is not null);
        ReviewRangeCommand = new RelayCommand(ReviewRange, () => CanEdit && SelectedSequence is not null);
        ApplyRangeCommand = new RelayCommand(ApplyRange, () => CanEdit && _rangeCandidate is not null);
        ImportCommand = new RelayCommand(() => ImportRequested?.Invoke(this, EventArgs.Empty), () => CanCreate);
        ExportSourceCommand = new RelayCommand(() => ExportSourceRequested?.Invoke(this, EventArgs.Empty), () => Sequences.Count > 0);
        RefreshCatalog();
    }

    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportSourceCommand { get; }
    public event EventHandler? ImportRequested;
    public event EventHandler? ExportSourceRequested;
    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;
    public event EventHandler? NewSequenceRequested;
    public event EventHandler? AuditionRequested;
    public ObservableCollection<AnimationSequenceUse> Sequences { get; } = [];
    public ObservableCollection<AnimationEventItemViewModel> Items { get; } = [];
    public ObservableCollection<AnimationEventChoice> Definitions { get; } = [];
    public ImmutableArray<Dl1AnimationActionDefinition> ActionDefinitions { get; } = Dl1AnimationEventCatalog.Actions;
    public ObservableCollection<AnimationActionEditorViewModel> Actions { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public IReadOnlyList<string> SnapOptions { get; } = ["Native", "Frame", "Free"];
    public IReadOnlyList<string> DeliveryOptions { get; } = ["Event", "Must send"];
    public IReadOnlyList<string> RetimeOptions { get; } = ["Keep local frames", "Preserve seconds", "Keep source positions", "Scale to range"];
    public RelayCommand AddEventCommand { get; }
    public RelayCommand NewSequenceCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand AddActionCommand { get; }
    public RelayCommand AuditionCommand { get; }
    public RelayCommand ReviewRangeCommand { get; }
    public RelayCommand ApplyRangeCommand { get; }
    public bool IsCompiled { get => _isCompiled; set { if (SetProperty(ref _isCompiled, value)) { NewSequenceCommand.NotifyCanExecuteChanged(); AddActionCommand.NotifyCanExecuteChanged(); } } }
    public bool ShowLanes { get => _showLanes; set { if (SetProperty(ref _showLanes, value)) _timeline.RebuildEventLanes(); } }
    public bool CanEdit { get => _canEdit; set { if (SetProperty(ref _canEdit, value)) NotifyCommands(); } }
    public bool CanCreate { get => _canCreate; set { if (SetProperty(ref _canCreate, value)) NewSequenceCommand.NotifyCanExecuteChanged(); ImportCommand.NotifyCanExecuteChanged(); } }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Search { get => _search; set { if (SetProperty(ref _search, value ?? string.Empty)) { RefreshCatalog(); RebuildItems(); } } }
    public string Snap { get => _snap; set => SetProperty(ref _snap, SnapOptions.Contains(value) ? value : "Native"); }
    public string FrameText { get => _frameText; set { if (SetProperty(ref _frameText, value)) OnPropertyChanged(nameof(TimingLabel)); } }
    public string IdText { get => _idText; set => SetProperty(ref _idText, value); }
    public string SlotText { get => _slotText; set => SetProperty(ref _slotText, value); }
    public short? PreviewSlot => short.TryParse(_previewSlotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out short slot) ? slot : null;
    public string PreviewSlotText { get => _previewSlotText; set { if (SetProperty(ref _previewSlotText, value)) SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    public string Delivery { get => _delivery; set => SetProperty(ref _delivery, value); }
    public string RangeStart { get => _rangeStart; set { _rangeCandidate = null; SetProperty(ref _rangeStart, value); ApplyRangeCommand.NotifyCanExecuteChanged(); } }
    public string RangeEnd { get => _rangeEnd; set { _rangeCandidate = null; SetProperty(ref _rangeEnd, value); ApplyRangeCommand.NotifyCanExecuteChanged(); } }
    public string FPS { get => _fps; set { _rangeCandidate = null; SetProperty(ref _fps, value); ApplyRangeCommand.NotifyCanExecuteChanged(); } }
    public string Retime { get => _retime; set { _rangeCandidate = null; SetProperty(ref _retime, value); ApplyRangeCommand.NotifyCanExecuteChanged(); } }

    public AnimationEventChoice? SelectedDefinition
    {
        get => _definition;
        set { if (SetProperty(ref _definition, value) && value is not null) IdText = value.Symbol; }
    }
    public Dl1AnimationActionDefinition? SelectedActionDefinition
    {
        get => _actionDefinition;
        set { if (SetProperty(ref _actionDefinition, value)) AddActionCommand.NotifyCanExecuteChanged(); }
    }
    public AnimationSequenceUse? SelectedSequence
    {
        get => _selectedSequence;
        set
        {
            if (!SetProperty(ref _selectedSequence, value)) return;
            _selection.Clear();
            _rangeCandidate = null;
            RangeStart = Format(value?.SourceStartFrame ?? 0);
            RangeEnd = Format(value?.SourceEndFrame ?? 120);
            FPS = Format(value?.FPS ?? 30);
            if (value is not null && double.IsFinite(value.FPS) && value.FPS > 0)
            {
                _timeline.IsPlaying = false;
                _timeline.FramesPerSecond = value.FPS;
                _timeline.EndFrame = checked((int)Math.Clamp(Math.Ceiling(value.SourceEndFrame - value.SourceStartFrame), 1, int.MaxValue - 1));
                _timeline.PositionFrame = 0;
            }
            _timeline.SourceFrameOffset = value is { SourceStartFrame: >= 0 and <= int.MaxValue } ? value.SourceStartFrame : 0;
            SelectedEvent = null;
            RebuildItems();
            NotifyCommands();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public AnimationEventItemViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetProperty(ref _selectedEvent, value)) return;
            Actions.Clear();
            if (value is not null)
            {
                FrameText = value.Event.FrameExpression ?? NativeExpression(value.Event.LocalFrame);
                IdText = value.Event.IdExpression;
                AnimationEventChoice? definition = Definitions.FirstOrDefault(choice => choice.Symbol.Equals(value.Event.IdExpression, StringComparison.OrdinalIgnoreCase))
                    ?? (value.Event.EventId is { } numericId ? Definitions.FirstOrDefault(choice => choice.Id == numericId) : null);
                SetProperty(ref _definition, definition, nameof(SelectedDefinition));
                SlotText = value.Event.RequiredSlot == -1 ? "Any" : value.Event.SlotExpression;
                Delivery = value.Event.Delivery == AnimationEventDelivery.MustSendEvent ? "Must send" : "Event";
                foreach (AnimationEventAction action in value.Event.Actions) Actions.Add(new AnimationActionEditorViewModel(action, RemoveAction, MoveAction));
            }
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Details));
            OnPropertyChanged(nameof(TimingLabel));
            NotifyCommands();
        }
    }
    public bool HasSelection => SelectedEvent is not null;
    public string Details => SelectedEvent is { } item
        ? $"{item.Event.IdExpression}  ·  ID {item.Event.EventId?.ToString(CultureInfo.InvariantCulture) ?? "?"}\nSlot {item.Event.SlotExpression}  ·  Flags {item.Event.RawFlags}\nTicks {item.Event.RawTicks?.ToString(CultureInfo.InvariantCulture) ?? "—"}\n{SoundMappingDetails(item.Event)}"
        : string.Empty;
    public string TimingLabel
    {
        get
        {
            if (!TryNumber(FrameText, out double frame)) return string.Empty;
            if (frame < 0 || frame > 13107) return "Outside native timing range";
            double stored = Math.Truncate((float)((float)frame * 5f)) / 5;
            double seconds = SelectedSequence is { FPS: > 0 } seq ? frame / seq.FPS : 0;
            string coordinates = $"Source {Format((SelectedSequence?.SourceStartFrame ?? 0) + frame)}  ·  {seconds:0.###} s";
            return Math.Abs(stored - frame) > 1e-7 ? $"{coordinates}  ·  Exports at {Format(stored)}" : coordinates;
        }
    }

    public void Load(IEnumerable<AnimationSequenceUse> sequences, Guid? preferred = null)
    {
        Guid? selected = preferred ?? SelectedSequence?.Id;
        Guid? eventId = SelectedEvent?.Id;
        HashSet<Guid> selectedIds = new(_selection);
        Sequences.Clear();
        foreach (AnimationSequenceUse sequence in sequences) Sequences.Add(sequence);
        SelectedSequence = Sequences.FirstOrDefault(s => s.Id == selected) ?? Sequences.FirstOrDefault();
        _timeline.SourceFrameOffset = SelectedSequence is { SourceStartFrame: >= 0 and <= int.MaxValue } selectedUse ? selectedUse.SourceStartFrame : 0;
        _selection = selectedIds;
        ExportSourceCommand.NotifyCanExecuteChanged();
        RebuildItems();
        Select(eventId, false, preserveSelection: true);
    }

    public ImmutableArray<AnimationSequenceUse> Snapshot() => Sequences.ToImmutableArray();

    public bool HasEvents => Items.Count > 0;

    public void Select(Guid? id, bool additive, bool preserveSelection = false)
    {
        if (!preserveSelection && !additive) _selection.Clear();
        if (id is { } selected && !preserveSelection)
        {
            if (additive && !_selection.Add(selected)) _selection.Remove(selected);
            else _selection.Add(selected);
        }
        SelectedEvent = Items.FirstOrDefault(x => x.Id == id);
        foreach (AnimationEventItemViewModel item in Items) item.IsSelected = _selection.Contains(item.Id);
        _timeline.RefreshEventGeometry();
        NotifyCommands();
    }

    public void SelectRange(double first, double last)
    {
        _selection = Items.Where(x => x.Event.LocalFrame >= Math.Min(first, last) && x.Event.LocalFrame <= Math.Max(first, last)).Select(x => x.Id).ToHashSet();
        Select(_selection.FirstOrDefault(), false, preserveSelection: true);
    }

    public double SnapFrame(double frame) => Snap switch
    {
        "Native" => Math.Round(frame * 5, MidpointRounding.AwayFromZero) / 5,
        "Frame" => Math.Round(frame, MidpointRounding.AwayFromZero),
        _ => frame,
    };

    public void BeginDrag(double frame)
    {
        if (!CanEdit || _selection.Count == 0) return;
        _timeline.IsPlaying = false;
        _dragOriginal = SelectedSequence;
        _dragAnchor = frame;
    }
    public void DragTo(double frame)
    {
        if (_dragOriginal is not { } original) return;
        double delta = SnapFrame(frame - _dragAnchor);
        double minimum = original.Events.Where(e => _selection.Contains(e.Id)).Min(e => e.LocalFrame);
        delta = Math.Max(-minimum, delta);
        SetSequence(original with { Events = original.Events.Select(e => _selection.Contains(e.Id) ? e with { LocalFrame = e.LocalFrame + delta, FrameExpression = NativeExpression(e.LocalFrame + delta), RawTicks = null } : e).ToImmutableArray() }, false);
    }
    public void EndDrag(bool cancel = false)
    {
        if (_dragOriginal is not { } original) return;
        _dragOriginal = null;
        if (cancel) SetSequence(original, false);
        else if (SelectedSequence != original) Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Nudge(double delta)
    {
        BeginDrag(0);
        DragTo(delta);
        EndDrag();
    }

    private void AddEvent()
    {
        if (SelectedSequence is not { } sequence) return;
        double frame = SnapFrame(_timeline.PositionFrame);
        AnimationEvent item = new() { LocalFrame = frame, FrameExpression = NativeExpression(frame), EventId = SelectedDefinition is null ? 0 : SelectedDefinition.Id, IdExpression = SelectedDefinition?.Symbol ?? "0", RequiredSlot = -1, SlotExpression = "-1" };
        _selection = [item.Id];
        SetSequence(sequence with { Events = sequence.Events.Add(item) });
        Select(item.Id, false);
    }
    private void Apply()
    {
        if (SelectedEvent is not { } selected || SelectedSequence is not { } sequence) return;
        if (!TryNumber(FrameText, out double frame) || frame < 0 || frame > 13107) { Status = "Enter a frame from 0 to 13107"; return; }
        string slot = SlotText.Equals("Any", StringComparison.OrdinalIgnoreCase) ? "-1" : SlotText.Trim();
        if (!short.TryParse(slot, NumberStyles.Integer, CultureInfo.InvariantCulture, out short parsedSlot)) { Status = "Enter Any or a slot from -32768 to 32767"; return; }
        string idText = IdText.Trim();
        int? eventId = Dl1AnimationEventCatalog.FindEvent(idText)?.Id;
        if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
        {
            if (numeric is < 0 or > 65535) { Status = "Enter an event ID from 0 to 65535"; return; }
            eventId = numeric;
        }
        if (string.IsNullOrWhiteSpace(idText) || idText.IndexOfAny(['\r', '\n', '(', ')', '{', '}', ',']) >= 0) { Status = "Enter an event symbol or numeric ID"; return; }
        ImmutableArray<AnimationEventAction> actions = Actions.Select(a => a.Build()).ToImmutableArray();
        AnimationEvent updated = selected.Event with { LocalFrame = frame, FrameExpression = NativeExpression(frame), RawTicks = frame == selected.Event.LocalFrame ? selected.Event.RawTicks : null, IdExpression = idText, EventId = eventId, RequiredSlot = parsedSlot, SlotExpression = slot, Delivery = Delivery == "Must send" ? AnimationEventDelivery.MustSendEvent : AnimationEventDelivery.Event, Actions = actions };
        SetSequence(sequence with { Events = sequence.Events.Replace(selected.Event, updated) });
        Status = eventId is null ? "Saved; resolve the ID before compiled export" : "Saved";
    }
    private void Delete()
    {
        if (SelectedSequence is not { } sequence) return;
        SetSequence(sequence with { Events = sequence.Events.Where(e => !_selection.Contains(e.Id)).ToImmutableArray() });
        _selection.Clear();
        SelectedEvent = null;
    }
    private void Copy()
    {
        _clipboardSequenceId = SelectedSequence?.Id;
        _clipboard = SelectedSequence?.Events.Where(e => _selection.Contains(e.Id)).ToImmutableArray() ?? [];
        PasteCommand.NotifyCanExecuteChanged();
    }
    private void Duplicate() { Copy(); PasteAt(_clipboard.IsEmpty ? 0 : _clipboard.Min(e => e.LocalFrame), 1); }
    private void Paste() => PasteAt(_timeline.PositionFrame, 0);
    private void PasteAt(double frame, double offset)
    {
        if (_clipboard.IsEmpty || SelectedSequence is not { } sequence) return;
        if (_clipboardSequenceId != sequence.Id && _clipboard.Any(e => e.RawActionReference is not null and not uint.MaxValue)) { Status = "Resolve compiled actions before copying to another sequence"; return; }
        double first = _clipboard.Min(e => e.LocalFrame);
        ImmutableArray<AnimationEvent> pasted = _clipboard.Select(e => e with { Id = Guid.NewGuid(), LocalFrame = SnapFrame(frame + offset + e.LocalFrame - first), FrameExpression = NativeExpression(SnapFrame(frame + offset + e.LocalFrame - first)), RawTicks = null, SourceSpan = null, OriginalSource = null, Actions = e.Actions.Select(a => a with { Id = Guid.NewGuid(), SourceSpan = null, OriginalSource = null, Arguments = a.Arguments.Select(arg => arg with { SourceSpan = null }).ToImmutableArray() }).ToImmutableArray() }).ToImmutableArray();
        _selection = pasted.Select(e => e.Id).ToHashSet();
        SetSequence(sequence with { Events = sequence.Events.AddRange(pasted) });
        Select(pasted[0].Id, false, preserveSelection: true);
    }
    private void AddAction()
    {
        if (SelectedActionDefinition is not { } definition) return;
        var action = new AnimationEventAction { Keyword = definition.Keyword, Arguments = definition.Signature.Select((type, index) => new AnimationEventArgument { Text = DefaultArgument(type, definition.ParameterNames.ElementAtOrDefault(index) ?? string.Empty) }).ToImmutableArray() };
        Actions.Add(new AnimationActionEditorViewModel(action, RemoveAction, MoveAction));
    }
    private void RemoveAction(AnimationActionEditorViewModel action) => Actions.Remove(action);
    private void MoveAction(AnimationActionEditorViewModel action, int delta)
    {
        int index = Actions.IndexOf(action);
        int target = index + delta;
        if (index >= 0 && target >= 0 && target < Actions.Count) Actions.Move(index, target);
    }
    private void ReviewRange()
    {
        if (SelectedSequence is not { } sequence || !TryNumber(RangeStart, out double start) || !TryNumber(RangeEnd, out double end) || !TryNumber(FPS, out double fps) || start < 0 || end < start || fps <= 0 || fps > 10000) { Status = "Enter a valid range and FPS"; return; }
        if (Retime != "Keep local frames" && sequence.Events.Any(e => e.FrameExpression is { } expression && !TryNumber(expression, out _))) { Status = "Resolve event frames before retiming"; return; }
        if (Retime == "Preserve seconds" && (!double.IsFinite(sequence.FPS) || sequence.FPS <= 0)) { Status = "Resolve the current FPS before preserving seconds"; return; }
        double oldLength = sequence.SourceEndFrame - sequence.SourceStartFrame;
        if (Retime == "Scale to range" && oldLength == 0) { Status = "Cannot scale a zero-length range"; return; }
        ImmutableArray<AnimationEvent> events = sequence.Events.Select(e =>
        {
            double frame = Retime switch { "Preserve seconds" => e.LocalFrame * fps / sequence.FPS, "Keep source positions" => e.LocalFrame + sequence.SourceStartFrame - start, "Scale to range" => e.LocalFrame * (end - start) / oldLength, _ => e.LocalFrame };
            return frame == e.LocalFrame ? e : e with { LocalFrame = frame, FrameExpression = NativeExpression(frame), RawTicks = null };
        }).ToImmutableArray();
        _rangeCandidate = sequence with { SourceStartFrame = start, SourceEndFrame = end, FPS = fps, Events = events, SourceStartFrameExpression = null, SourceEndFrameExpression = null, FpsExpression = null };
        int outside = events.Count(e => e.LocalFrame < 0 || e.LocalFrame > end - start);
        Status = $"{events.Count(e => sequence.Events.First(o => o.Id == e.Id).LocalFrame != e.LocalFrame)} events move · {outside} outside range";
        Log.Clear();
        foreach (AnimationEvent item in events.Where(e => sequence.Events.First(o => o.Id == e.Id).LocalFrame != e.LocalFrame || e.LocalFrame < 0 || e.LocalFrame > end - start)) Log.Add($"{DisplayName(item)}: {Format(sequence.Events.First(e => e.Id == item.Id).LocalFrame)} → {Format(item.LocalFrame)}");
        ApplyRangeCommand.NotifyCanExecuteChanged();
    }
    private void ApplyRange() { if (_rangeCandidate is { } candidate) { _rangeCandidate = null; SetSequence(candidate); Status = "Range updated"; } }

    private void SetSequence(AnimationSequenceUse sequence, bool commit = true)
    {
        int index = Sequences.ToList().FindIndex(s => s.Id == sequence.Id);
        if (index < 0) return;
        if (commit) _timeline.IsPlaying = false;
        if (sequence.FPS > 0 && double.IsFinite(sequence.FPS)) _timeline.FramesPerSecond = sequence.FPS;
        _timeline.SourceFrameOffset = sequence.SourceStartFrame is >= 0 and <= int.MaxValue ? sequence.SourceStartFrame : 0;
        if (sequence.SourceEndFrame >= sequence.SourceStartFrame) _timeline.EndFrame = (int)Math.Clamp(Math.Ceiling(sequence.SourceEndFrame - sequence.SourceStartFrame), 1, int.MaxValue - 1);
        Sequences[index] = sequence;
        _selectedSequence = sequence;
        OnPropertyChanged(nameof(SelectedSequence));
        Guid? selected = SelectedEvent?.Id;
        RebuildItems();
        Select(selected, false, preserveSelection: true);
        if (commit) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void RebuildItems()
    {
        Items.Clear();
        foreach (AnimationEvent item in SelectedSequence?.Events ?? [])
        {
            string name = DisplayName(item);
            if (Search.Length > 0 && !name.Contains(Search, StringComparison.OrdinalIgnoreCase) && !item.IdExpression.Contains(Search, StringComparison.OrdinalIgnoreCase) && !item.Actions.Any(a => a.Keyword.Contains(Search, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(new AnimationEventItemViewModel(item, name, Lane(item)) { IsSelected = _selection.Contains(item.Id) });
        }
        OnPropertyChanged(nameof(HasEvents));
        _timeline.RebuildEventLanes();
    }
    private void RefreshCatalog()
    {
        Definitions.Clear();
        foreach (Dl1AnimationEventDefinition entry in Dl1AnimationEventCatalog.Events.Where(e => Search.Length == 0 || e.Symbol.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Category.Contains(Search, StringComparison.OrdinalIgnoreCase) || e.Id.ToString(CultureInfo.InvariantCulture).Contains(Search, StringComparison.OrdinalIgnoreCase))) Definitions.Add(new(entry.Symbol, entry.Id, entry.DisplayName));
        foreach (Dl1UnresolvedAnimationEvent entry in Dl1AnimationEventCatalog.Unresolved.Where(e => Search.Length == 0 || e.Expression.Contains(Search, StringComparison.OrdinalIgnoreCase))) Definitions.Add(new(entry.Expression, null, Humanize(entry.Expression)));
    }
    private void NotifyCommands()
    {
        AddEventCommand.NotifyCanExecuteChanged(); ApplyCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); DuplicateCommand.NotifyCanExecuteChanged(); CopyCommand.NotifyCanExecuteChanged(); PasteCommand.NotifyCanExecuteChanged(); AddActionCommand.NotifyCanExecuteChanged(); AuditionCommand.NotifyCanExecuteChanged(); ReviewRangeCommand.NotifyCanExecuteChanged(); ApplyRangeCommand.NotifyCanExecuteChanged();
    }
    public static string DisplayName(AnimationEvent item)
    {
        if (item.IdExpression == "0" && item.Actions.Length > 0) return Dl1AnimationEventCatalog.FindAction(item.Actions[0].Keyword)?.DisplayName ?? Humanize(item.Actions[0].Keyword);
        return Dl1AnimationEventCatalog.FindEvent(item.IdExpression)?.DisplayName ?? (item.EventId is { } id ? Dl1AnimationEventCatalog.FindEvents(id).FirstOrDefault()?.DisplayName : null) ?? Humanize(item.IdExpression);
    }
    public static string Humanize(string symbol)
    {
        string name = symbol;
        foreach (string prefix in new[] { "VIS_EVENT_", "SOUND_EVENT_", "EVENT_" }) if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant());
    }
    public static string Lane(AnimationEvent item)
    {
        string name = (Dl1AnimationEventCatalog.FindEvent(item.IdExpression)?.Symbol
            ?? (item.EventId is { } numericId ? Dl1AnimationEventCatalog.FindEvents(numericId).FirstOrDefault()?.Symbol : null)
            ?? item.IdExpression).ToUpperInvariant();
        if (name.Contains("IK", StringComparison.Ordinal) || name.Contains("FOOT", StringComparison.Ordinal) || name.Contains("STEP", StringComparison.Ordinal)) return "IK & Contacts";
        if (name.Contains("SOUND", StringComparison.Ordinal) || item.Actions.Any(a => a.Keyword.StartsWith("PlaySound", StringComparison.OrdinalIgnoreCase) || a.Keyword.StartsWith("PlayPlayerSound", StringComparison.OrdinalIgnoreCase) || a.Keyword.Equals("PlayAISound", StringComparison.OrdinalIgnoreCase))) return "Sound";
        if (name.StartsWith("VIS_", StringComparison.Ordinal) || item.Actions.Any(a => a.Keyword.Contains("Element", StringComparison.OrdinalIgnoreCase) || a.Keyword.Equals("PlayFx", StringComparison.OrdinalIgnoreCase))) return "Visual";
        return item.EventId is null || item.EventId == 0 ? "Other" : "Gameplay";
    }
    private static string SoundMappingDetails(AnimationEvent item) => string.Join("\n", Dl1AnimationEventCatalog.SoundMappings.Where(m => m.EventId == item.EventId).Select(m => $"{m.TargetSymbol}: {m.SoundName}"));
    private static string DefaultArgument(string type, string name) => type switch { "s" => "\"\"", "v" or "v3" => "[0,0,0]", _ => name is "probability" or "volume" or "power_multiplier" or "duration_multiplier" or "strength_multiplier" ? "1" : "0" };
    private static string NativeExpression(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    internal static string Format(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    private static bool TryNumber(string? text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

public sealed record AnimationEventChoice(string Symbol, int? Id, string DisplayName);

public sealed class AnimationEventItemViewModel(AnimationEvent item, string label, string lane) : ObservableObject
{
    private bool _selected;
    public AnimationEvent Event { get; } = item;
    public Guid Id => Event.Id;
    public string Label { get; } = label;
    public string Lane { get; } = lane;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
    public string ToolTip => $"{Label} · {AnimationEventEditorViewModel.Format(Event.LocalFrame)}";
}

public sealed class AnimationActionEditorViewModel
{
    private readonly AnimationEventAction _original;
    public AnimationActionEditorViewModel(AnimationEventAction action, Action<AnimationActionEditorViewModel> remove, Action<AnimationActionEditorViewModel, int> move)
    {
        _original = action;
        Dl1AnimationActionDefinition? definition = Dl1AnimationEventCatalog.FindAction(action.Keyword);
        Arguments = action.Arguments.Select((arg, index) => new AnimationActionArgumentViewModel(definition?.ParameterNames.ElementAtOrDefault(index) ?? $"Argument {index + 1}", definition?.Signature.ElementAtOrDefault(index) ?? string.Empty, arg)).ToArray();
        RemoveCommand = new RelayCommand(() => remove(this));
        UpCommand = new RelayCommand(() => move(this, -1));
        DownCommand = new RelayCommand(() => move(this, 1));
    }
    public string Keyword => _original.Keyword;
    public string DisplayName => Dl1AnimationEventCatalog.FindAction(_original.Keyword)?.DisplayName ?? _original.Keyword;
    public IReadOnlyList<AnimationActionArgumentViewModel> Arguments { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }
    public AnimationEventAction Build() => _original with { Arguments = Arguments.Select(a => a.Build()).ToImmutableArray() };
}

public sealed class AnimationActionArgumentViewModel(string name, string type, AnimationEventArgument argument) : ObservableObject
{
    private string _text = argument.Text;
    public string Name { get; } = AnimationEventEditorViewModel.Humanize(name);
    public string Type { get; } = type;
    public bool IsBoolean => name is "attach" or "stop_on_anim_end" or "stop_on_animation_end" or "cleanup" && (_text is "0" or "1");
    public bool IsSelector => name == "sound_flag_selector" && _text is "0" or "1" or "2";
    public IReadOnlyList<string> SelectorChoices { get; } = ["Always", "If stopped", "Item"];
    public bool BooleanValue { get => _text == "1"; set => Text = value ? "1" : "0"; }
    public string SelectorValue { get => _text switch { "1" => "If stopped", "2" => "Item", _ => "Always" }; set => Text = value switch { "If stopped" => "1", "Item" => "2", _ => "0" }; }
    public string EditorText
    {
        get => Type == "s" && _text.StartsWith('"') ? ReAnimated.Evaluation.AnimationEventPreviewState.Unquote(_text) : _text;
        set => Text = Type == "s" ? JsonSerializer.Serialize(value) : value;
    }
    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value)) return;
            OnPropertyChanged(nameof(EditorText)); OnPropertyChanged(nameof(BooleanValue)); OnPropertyChanged(nameof(SelectorValue));
        }
    }
    public AnimationEventArgument Build() => argument with { Text = Text, Value = null };
}
