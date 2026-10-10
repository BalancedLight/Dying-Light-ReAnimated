using System.IO;
using Microsoft.Win32;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Core.Domain;
using ReAnimated.Evaluation;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private readonly AnimationEventPreviewAudio _modelEventAudio = new();
    private readonly Dictionary<string, string> _modelEventSounds = new(StringComparer.OrdinalIgnoreCase);
    private Guid? _modelResumeSequence;
    private double? _modelResumePosition;
    private bool _modelEventEnded;

    private void InitializeModelEventAudio()
    {
        Timeline.Events.AuditionRequested += (_, _) => AuditionModelEvent();
        Timeline.Events.SelectionChanged += (_, _) => { _modelResumeSequence = null; _modelResumePosition = null; _modelEventAudio.Stop(); RefreshPreview(); };
        Timeline.PlaybackStopped += (_, _) => { _modelResumeSequence = null; _modelResumePosition = null; _modelEventAudio.Stop(); };
        Timeline.CurrentFrameChanged += (_, _) => { if (!Timeline.IsPlaying) { _modelResumeSequence = null; _modelResumePosition = null; _modelEventAudio.Stop(); } };
        Timeline.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(TimelineViewModel.IsPlaying)) return;
            if (Timeline.IsPlaying && Timeline.Events.SelectedSequence is { } sequence)
            {
                bool resuming = _modelResumeSequence == sequence.Id && _modelResumePosition == Timeline.PositionFrame;
                _modelResumeSequence = null; _modelResumePosition = null;
                if (!resuming) foreach (AnimationEvent item in AnimationEventPlaybackCursor.Start(sequence, Timeline.PositionFrame, Timeline.Events.PreviewSlot)) PlayModelEvent(item);
            }
            else { _modelResumeSequence = Timeline.Events.SelectedSequence?.Id; _modelResumePosition = Timeline.PositionFrame; _modelEventAudio.Stop(animationEnd: _modelEventEnded); _modelEventEnded = false; }
        };
        Timeline.PlaybackAdvanced += (_, advance) =>
        {
            if (Timeline.Events.SelectedSequence is not { } sequence) return;
            foreach (var segment in AnimationEventPlaybackCursor.CrossSegments(sequence, advance.From, advance.To, advance.Loops, Timeline.Events.PreviewSlot))
            {
                if (segment.StartsNewCycle) _modelEventAudio.Stop(animationEnd: true);
                foreach (AnimationEvent item in segment.Events) PlayModelEvent(item);
            }
            if (!Timeline.IsLooping && advance.To >= Timeline.EndFrame) { _modelEventEnded = true; _modelEventAudio.Stop(animationEnd: true); }
        };
    }
    private void PlayModelEvent(AnimationEvent item)
    {
        Timeline.Events.Log.Add($"{AnimationEventEditorViewModel.Format(item.LocalFrame)} · {AnimationEventEditorViewModel.DisplayName(item)}");
        while (Timeline.Events.Log.Count > 100) Timeline.Events.Log.RemoveAt(0);
        foreach (AnimationEventAction action in item.Actions)
        {
            Dl1AnimationActionDefinition? definition = Dl1AnimationEventCatalog.FindAction(action.Keyword);
            int index = definition?.ParameterNames.IndexOf("sound_name") ?? -1;
            if (definition is null || index < 0 || index >= action.Arguments.Length) continue;
            string sound = AnimationEventPreviewState.Unquote(action.Arguments[index].Text);
            if (_modelEventSounds.TryGetValue(sound, out string? path))
                _modelEventAudio.Play(path, MainWindowViewModel.ActionNumber(action, definition, "volume", 1), MainWindowViewModel.ActionNumber(action, definition, "stop_on_anim_end", 0) != 0);
        }
    }
    private void AuditionModelEvent()
    {
        if (Timeline.Events.SelectedEvent is not { } selected) return;
        string? sound = selected.Event.Actions.Select(action =>
        {
            int index = Dl1AnimationEventCatalog.FindAction(action.Keyword)?.ParameterNames.IndexOf("sound_name") ?? -1;
            return index >= 0 && index < action.Arguments.Length ? AnimationEventPreviewState.Unquote(action.Arguments[index].Text) : null;
        }).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        sound ??= Dl1AnimationEventCatalog.SoundMappings.FirstOrDefault(m => m.EventId == selected.Event.EventId)?.SoundName;
        if (string.IsNullOrWhiteSpace(sound)) { Timeline.Events.Status = "This event has no sound"; return; }
        try
        {
            if (!_modelEventSounds.TryGetValue(sound, out string? path) || !File.Exists(path))
            {
                var picker = new OpenFileDialog { Title = "Choose sound", Filter = "Audio|*.wav;*.mp3;*.wma;*.aac;*.m4a|All files|*.*", CheckFileExists = true };
                if (picker.ShowDialog() != true) return;
                path = picker.FileName;
                _modelEventSounds[sound] = path;
            }
            _modelEventAudio.Stop();
            _modelEventAudio.Play(path, 1, false);
        }
        catch (IOException exception) { Timeline.Events.Status = exception.Message; }
    }
}
