using System.Globalization;
using System.IO;
using System.Numerics;
using Microsoft.Win32;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Core.Domain;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Guid? _eventResumeSequence;
    private double? _eventResumePosition;
    private bool _eventAnimationEnded;
    private readonly AnimationEventPreviewAudio _eventAudio = new();
    private readonly Dictionary<string, string> _eventSoundResources = new(StringComparer.OrdinalIgnoreCase);

    private void StopAnimationEventAudio() { _eventResumeSequence = null; _eventResumePosition = null; _eventAudio.Stop(); }
    private void StartAnimationEventPlayback()
    {
        if (Timeline.Events.SelectedSequence is not { } sequence) return;
        _eventAudio.Stop();
        bool resuming = _eventResumeSequence == sequence.Id && _eventResumePosition == Timeline.PositionFrame;
        _eventResumeSequence = null; _eventResumePosition = null;
        if (resuming) return;
        foreach (AnimationEvent item in AnimationEventPlaybackCursor.Start(sequence, Timeline.PositionFrame, Timeline.Events.PreviewSlot)) PreviewAnimationEvent(item);
    }
    private void OnAnimationEventPlaybackAdvance(object? sender, TimelinePlaybackAdvance advance)
    {
        if (Timeline.Events.SelectedSequence is not { } sequence) return;
        foreach (var segment in AnimationEventPlaybackCursor.CrossSegments(sequence, advance.From, advance.To, advance.Loops, Timeline.Events.PreviewSlot))
        {
            if (segment.StartsNewCycle) _eventAudio.Stop(animationEnd: true);
            foreach (AnimationEvent item in segment.Events) PreviewAnimationEvent(item);
        }
        if (advance.Loops > 65) AddAnimationEventLog($"{advance.Loops - 65} additional loops elapsed");
        if (!Timeline.IsLooping && advance.To >= Timeline.EndFrame) { _eventAnimationEnded = true; _eventAudio.Stop(animationEnd: true); }
    }
    private void PreviewAnimationEvent(AnimationEvent item)
    {
        AddAnimationEventLog($"{AnimationEventEditorViewModel.Format(item.LocalFrame)} · {AnimationEventEditorViewModel.DisplayName(item)}");
        foreach (AnimationEventAction action in item.Actions)
        {
            Dl1AnimationActionDefinition? definition = Dl1AnimationEventCatalog.FindAction(action.Keyword);
            if (definition is null) continue;
            int soundIndex = definition.ParameterNames.IndexOf("sound_name");
            if (soundIndex < 0 || soundIndex >= action.Arguments.Length) continue;
            string sound = AnimationEventPreviewState.Unquote(action.Arguments[soundIndex].Text);
            if (!_eventSoundResources.TryGetValue(sound, out string? path)) continue;
            double volume = ActionNumber(action, definition, "volume", 1);
            bool stopOnEnd = ActionNumber(action, definition, "stop_on_anim_end", 0) != 0;
            double probability = ActionNumber(action, definition, "probability", 1);
            if (Random.Shared.NextDouble() > probability) continue;
            try { _eventAudio.Play(path, volume, stopOnEnd); }
            catch (IOException exception) { AddAnimationEventLog(exception.Message); }
        }
        foreach (Dl1AnimationSoundMapping mapping in Dl1AnimationEventCatalog.SoundMappings.Where(m => m.EventId == item.EventId))
            if (_eventSoundResources.TryGetValue(mapping.SoundName, out string? path)) _eventAudio.Play(path, 1, false);
    }

    private void AuditionAnimationEvent()
    {
        if (Timeline.Events.SelectedEvent is not { } selected) return;
        string? sound = selected.Event.Actions.Select(action =>
        {
            Dl1AnimationActionDefinition? definition = Dl1AnimationEventCatalog.FindAction(action.Keyword);
            int index = definition?.ParameterNames.IndexOf("sound_name") ?? -1;
            return index >= 0 && index < action.Arguments.Length ? AnimationEventPreviewState.Unquote(action.Arguments[index].Text) : null;
        }).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        sound ??= Dl1AnimationEventCatalog.SoundMappings.FirstOrDefault(m => m.EventId == selected.Event.EventId)?.SoundName;
        if (string.IsNullOrWhiteSpace(sound)) { Timeline.Events.Status = "This event has no sound"; return; }
        if (!_eventSoundResources.TryGetValue(sound, out string? path) || !File.Exists(path))
        {
            OpenFileDialog picker = new() { Title = "Choose sound", Filter = "Audio|*.wav;*.mp3;*.wma;*.aac;*.m4a|All files|*.*", CheckFileExists = true };
            if (picker.ShowDialog() != true) return;
            path = picker.FileName;
            _eventSoundResources[sound] = path;
        }
        _eventAudio.Stop();
        _eventAudio.Play(path, 1, false);
    }

    private void AddAnimationEventLog(string message)
    {
        Timeline.Events.Log.Add(message);
        while (Timeline.Events.Log.Count > 100) Timeline.Events.Log.RemoveAt(0);
    }
    internal static double ActionNumber(AnimationEventAction action, Dl1AnimationActionDefinition definition, string name, double fallback)
    {
        int index = definition.ParameterNames.IndexOf(name);
        return index >= 0 && index < action.Arguments.Length && double.TryParse(action.Arguments[index].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
    }

    private IEnumerable<GizmoRenderData> BuildAnimationEventFxLocators()
    {
        if (Timeline.Events.SelectedEvent is not { } selected) yield break;
        foreach (AnimationEventAction action in selected.Event.Actions.Where(a => a.Keyword.Equals("PlayFx", StringComparison.OrdinalIgnoreCase)))
        {
            Dl1AnimationActionDefinition? definition = Dl1AnimationEventCatalog.FindAction(action.Keyword);
            int index = definition?.ParameterNames.IndexOf("local_position") ?? -1;
            if (index < 0 || index >= action.Arguments.Length) continue;
            string[] values = action.Arguments[index].Text.Trim().Trim('[', ']').Split(',');
            if (values.Length != 3 || !float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            var position = new Vector3(x, y, z);
            int elementIndex = definition?.ParameterNames.IndexOf("element_name") ?? -1;
            if (elementIndex >= 0 && elementIndex < action.Arguments.Length)
            {
                string element = AnimationEventPreviewState.Unquote(action.Arguments[elementIndex].Text);
                var skeleton = TargetViewport.SceneSource.CaptureFrame().Skeleton;
                var bone = skeleton?.Bones.FirstOrDefault(b => b.Name.Equals(element, StringComparison.OrdinalIgnoreCase));
                if (bone is { } owner) position = Vector3.Transform(position, owner.WorldTransform * skeleton!.RootTransform);
            }
            var color = new Vector4(.7f, .5f, 1, 1);
            yield return new(GizmoKind.Line, position - Vector3.UnitX * .08f, position + Vector3.UnitX * .08f, color, 2);
            yield return new(GizmoKind.Line, position - Vector3.UnitY * .08f, position + Vector3.UnitY * .08f, color, 2);
            yield return new(GizmoKind.Line, position - Vector3.UnitZ * .08f, position + Vector3.UnitZ * .08f, color, 2);
        }
    }
}
