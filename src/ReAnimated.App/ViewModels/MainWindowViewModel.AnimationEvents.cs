using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using ReAnimated.Codecs.AnimationScripts;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _refreshingAnimationEvents;
    private bool _animationEventLoadFailed;
    private Guid? _eventLibraryId;

    private void InitializeAnimationEvents()
    {
        Timeline.Events.ImportRequested += ImportAnimationEventFile;
        Timeline.Events.ExportSourceRequested += ExportAnimationEventSource;
        Timeline.Events.Changed += CommitVisualAnimationEvents;
        Timeline.Events.NewSequenceRequested += CreateAnimationEventSequence;
        Timeline.Events.SelectionChanged += OnAnimationEventSequenceSelected;
        Timeline.Events.AuditionRequested += (_, _) => AuditionAnimationEvent();
        Timeline.PlaybackAdvanced += OnAnimationEventPlaybackAdvance;
        Timeline.PlaybackStopped += (_, _) => StopAnimationEventAudio();
        Timeline.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.IsPlaying))
            {
                if (Timeline.IsPlaying) StartAnimationEventPlayback();
                else { _eventResumeSequence = Timeline.Events.SelectedSequence?.Id; _eventResumePosition = Timeline.PositionFrame; _eventAudio.Stop(animationEnd: _eventAnimationEnded); _eventAnimationEnded = false; }
            }
        };
    }

    private void RefreshAnimationEvents()
    {
        if (_refreshingAnimationEvents) return;
        _refreshingAnimationEvents = true;
        try
        {
            _animationEventLoadFailed = false;
            ProjectAnimationLibrary? library = SelectedScriptLibrary is { } selected ? ResolveAnimationLibrary(selected.Id) : null;
            Guid? previousLibrary = _eventLibraryId;
            _eventLibraryId = library?.Id;
            RefreshAnimationEventPermissions();
            if (library is null) { Timeline.Events.Load([]); return; }
            string text = library.AuthoredScriptText ?? BuildGeneratedAnimationScriptText(library);
            ImmutableArray<AnimationSequenceUse> sequences = library.SequenceUses;
            if (sequences.IsEmpty)
            {
                sequences = AnimationScriptTextCodec.Read(text).Sequences;
                if (previousLibrary == library.Id) sequences = ReconcileEventIdentities(sequences, Timeline.Events.Snapshot());
            }
            sequences = sequences.Select(sequence => sequence with
            {
                AnimationReferenceId = sequence.AnimationReferenceId ?? _project.AnimationVariants.FirstOrDefault(v => v.OwningAnimationLibraryId == library.Id && string.Equals(v.OutputAnm2Name, sequence.Anm2Name, StringComparison.OrdinalIgnoreCase))?.Id,
            }).ToImmutableArray();
            Timeline.Events.Load(sequences);
            if (HasUncommittedAnimationScript()) Timeline.Events.Status = "Save or discard script edits before editing events";
            else Timeline.Events.Status = string.Empty;
        }
        catch (InvalidDataException exception)
        {
            _animationEventLoadFailed = true;
            Timeline.Events.CanEdit = false;
            Timeline.Events.Status = exception.Message;
        }
        finally { _refreshingAnimationEvents = false; }
    }

    private void RefreshAnimationEventPermissions()
    {
        ProjectAnimationLibrary? library = SelectedScriptLibrary is { } selected
            ? ResolveAnimationLibrary(selected.Id)
            : null;
        Timeline.Events.CanCreate = !IsBusy && !_animationEventLoadFailed && !HasUncommittedAnimationScript();
        Timeline.Events.IsCompiled = library?.ImportedBinaryScript is not null;
        Timeline.Events.CanEdit = !IsBusy && !_animationEventLoadFailed && library is not null && !HasUncommittedAnimationScript();
    }

    private bool HasUncommittedAnimationScript()
    {
        if (SelectedScriptLibrary is not { } selected || ResolveAnimationLibrary(selected.Id) is not { } library) return false;
        string saved = library.AuthoredScriptText ?? BuildGeneratedAnimationScriptText(library);
        return !string.Equals(saved, _animationScriptText, StringComparison.Ordinal);
    }

    private void CommitVisualAnimationEvents(object? sender, EventArgs args)
    {
        if (_refreshingAnimationEvents || _eventLibraryId is not { } libraryId || !Timeline.Events.CanEdit) return;
        int index = FindAnimationLibraryIndex(libraryId);
        if (index < 0 || IsBusy || HasUncommittedAnimationScript()) { RefreshAnimationEvents(); return; }
        ProjectAnimationLibrary library = _project.AnimationLibraries[index];
        ImmutableArray<AnimationSequenceUse> desired = Timeline.Events.Snapshot();
        Guid? selectedSequence = Timeline.Events.SelectedSequence?.Id;
        Guid? selectedEvent = Timeline.Events.SelectedEvent?.Id;
        try
        {
            if (library.ImportedBinaryScript is { } binary)
            {
                var sections = new ReAnimated.Codecs.Anm2.AnimationScrSections(binary.RecordsAndNames, binary.IndexAndNames);
                var patched = ReAnimated.Codecs.Anm2.AnimationScrCodec.PatchEvents(sections, desired.ToDictionary(s => s.Name, s => s.Events, StringComparer.OrdinalIgnoreCase));
                patched = ReAnimated.Codecs.Anm2.AnimationScrCodec.PatchRanges(patched, desired.ToDictionary(s => s.Name, s => ((float)s.SourceStartFrame, (float)s.SourceEndFrame, (float)s.FPS), StringComparer.OrdinalIgnoreCase));
                var saved = desired.Select(s => s with { Events = s.Events.Select(e => e with { RawTicks = e.RawTicks ?? AnimationEventTiming.ToTicks(e.LocalFrame) }).ToImmutableArray() }).ToImmutableArray();
                double compiledPosition = Timeline.PositionFrame;
                CommitProject(_project with { AnimationLibraries = _project.AnimationLibraries.SetItem(index, library with { SequenceUses = saved, ImportedBinaryScript = new AnimationScriptBinaryBacking { RecordsAndNames = patched.RecordsAndNames, IndexAndNames = patched.IndexAndNames } }) });
                Timeline.Events.Load(saved, selectedSequence);
                if (selectedEvent is { } compiledEventId) Timeline.Events.Select(compiledEventId, false);
                Timeline.PositionFrame = compiledPosition;
                StatusText = "Animation events saved";
                return;
            }
            string original = library.AuthoredScriptText ?? BuildGeneratedAnimationScriptText(library);
            AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(original);
            // Reader syntax nodes and persisted identities must describe the same source.
            ImmutableArray<AnimationSequenceUse> aligned = AlignForTextWriter(document.Sequences, desired);
            string text = ReAnimated.Codecs.Models.AnimationSequenceExport.IncludeDefinitions(AnimationScriptTextCodec.Write(document with { Sequences = aligned }), desired);
            ImmutableArray<AnimationSequenceUse> rebased = ReconcileEventIdentities(AnimationScriptTextCodec.Read(text).Sequences, desired);
            double position = Timeline.PositionFrame;
            CommitProject(_project with { AnimationLibraries = _project.AnimationLibraries.SetItem(index, library with { AuthoredScriptText = text, SequenceUses = rebased }) });
            Timeline.Events.Load(rebased, selectedSequence);
            if (selectedEvent is { } sourceEventId) Timeline.Events.Select(sourceEventId, false);
            Timeline.PositionFrame = position;
            StatusText = "Animation events saved";
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        {
            RefreshAnimationEvents();
            Timeline.Events.Status = exception.Message;
        }
    }

    internal static ImmutableArray<AnimationSequenceUse> AlignForTextWriter(ImmutableArray<AnimationSequenceUse> baseline, ImmutableArray<AnimationSequenceUse> desired)
    {
        return desired.Select(sequence =>
        {
            AnimationSequenceUse? source = baseline.FirstOrDefault(s => s.SourceSpan == sequence.SourceSpan && s.Name == sequence.Name) ?? baseline.FirstOrDefault(s => s.Name == sequence.Name);
            if (source is null) return sequence with { SourceSpan = null, OriginalSource = null };
            return sequence with
            {
                Id = source.Id,
                Events = sequence.Events.Select(item =>
                {
                    AnimationEvent? old = source.Events.FirstOrDefault(e => e.SourceSpan == item.SourceSpan && item.SourceSpan is not null);
                    if (old is null) return item;
                    return item with { Id = old.Id, Actions = item.Actions.Select(action =>
                    {
                        AnimationEventAction? oldAction = old.Actions.FirstOrDefault(a => a.SourceSpan == action.SourceSpan && action.SourceSpan is not null);
                        return oldAction is null ? action : action with { Id = oldAction.Id };
                    }).ToImmutableArray() };
                }).ToImmutableArray(),
            };
        }).ToImmutableArray();
    }

    internal static ImmutableArray<AnimationSequenceUse> ReconcileEventIdentities(ImmutableArray<AnimationSequenceUse> parsed, ImmutableArray<AnimationSequenceUse> previous)
    {
        var usedSequences = new HashSet<Guid>();
        return parsed.Select(sequence =>
        {
            AnimationSequenceUse? old = previous.FirstOrDefault(s => !usedSequences.Contains(s.Id) && s.Name == sequence.Name && s.Anm2Name == sequence.Anm2Name);
            if (old is null) return sequence;
            usedSequences.Add(old.Id);
            var usedEvents = new HashSet<Guid>();
            return sequence with
            {
                Id = old.Id,
                AnimationReferenceId = old.AnimationReferenceId,
                Events = sequence.Events.Select((item, index) =>
                {
                    AnimationEvent? prior = old.Events.FirstOrDefault(e => !usedEvents.Contains(e.Id) && e.OriginalSource is not null && e.OriginalSource == item.OriginalSource);
                    if (prior is null && old.Events.ElementAtOrDefault(index) is { } indexed && !usedEvents.Contains(indexed.Id)) prior = indexed;
                    prior ??= old.Events.FirstOrDefault(e => !usedEvents.Contains(e.Id));
                    if (prior is null) return item;
                    usedEvents.Add(prior.Id);
                    return item with { Id = prior.Id, RawTicks = prior.LocalFrame == item.LocalFrame ? prior.RawTicks : item.RawTicks, RawFlags = prior.RawFlags, RawActionReference = prior.RawActionReference, Actions = item.Actions.Select((action, actionIndex) => action with { Id = prior.Actions.ElementAtOrDefault(actionIndex)?.Id ?? action.Id }).ToImmutableArray() };
                }).ToImmutableArray(),
            };
        }).ToImmutableArray();
    }

    private void CreateAnimationEventSequence(object? sender, EventArgs args)
    {
        if (IsBusy) return;
        if (HasUncommittedAnimationScript()) { Timeline.Events.Status = "Save or discard script edits first"; return; }
        ProjectAnimationVariant? variant = _project.AnimationVariants.FirstOrDefault(v => v.Id == _activeAnimationId) ?? _project.AnimationVariants.FirstOrDefault(v => v.Id == SelectedAnimationLibraryItem?.Id);
        ProjectAnimationLibrary? library = _eventLibraryId is { } id ? ResolveAnimationLibrary(id) : null;
        if (library is null && variant?.OwningAnimationLibraryId is { } owning) library = ResolveAnimationLibrary(owning);
        bool createLibrary = library is null;
        if (library is null)
        {
            string resourceName = "animation_events";
            for (int suffix = 2; _project.AnimationLibraries.Any(l => l.ResourceName.Equals(resourceName, StringComparison.OrdinalIgnoreCase)); suffix++) resourceName = "animation_events_" + suffix.ToString(CultureInfo.InvariantCulture);
            library = new ProjectAnimationLibrary { ResourceName = resourceName, DisplayName = "Animation events" };
        }
        string? animationName = variant?.OutputAnm2Name;
        if (string.IsNullOrWhiteSpace(animationName)) animationName = _sourceAnimation?.Clip.Name;
        if (string.IsNullOrWhiteSpace(animationName)) { Timeline.Events.Status = "Select an animation first"; return; }
        if (!animationName.EndsWith(".anm2", StringComparison.OrdinalIgnoreCase)) animationName += ".anm2";
        string baseName = ReAnimated.Codecs.Models.Dl1SourceModelWriter.SanitizeName(Path.GetFileNameWithoutExtension(animationName), 63);
        string name = baseName;
        for (int suffix = 2; library.SequenceUses.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || Timeline.Events.Sequences.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); suffix++) name = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        ProjectAnimationSource? sourceMetadata = variant is null
            ? null
            : _project.AnimationSources.FirstOrDefault(source => source.Id == variant.SourceId);
        FrameRate frameRate = sourceMetadata?.FrameRate ?? _sourceAnimation?.Clip.FrameRate ?? new FrameRate(30, 1);
        long frameCount = sourceMetadata?.FrameCount ?? _sourceAnimation?.Clip.FrameCount ?? 1;
        AnimationSequenceUse sequence = CreateAnimationSequenceDefaults(
            name, animationName, variant?.Id, frameRate, frameCount);
        string original = library.AuthoredScriptText ?? (createLibrary ? string.Empty : BuildGeneratedAnimationScriptText(library));
        AnimationScriptTextDocument document = AnimationScriptTextCodec.Read(original);
        string text = AnimationScriptTextCodec.Write(document with { Sequences = document.Sequences.Add(sequence) });
        ImmutableArray<AnimationSequenceUse> sequences = ReconcileEventIdentities(AnimationScriptTextCodec.Read(text).Sequences, document.Sequences.Add(sequence));
        library = library with { AuthoredScriptText = text, SequenceUses = sequences };
        ImmutableArray<ProjectAnimationLibrary> libraries = createLibrary ? _project.AnimationLibraries.Add(library) : _project.AnimationLibraries.SetItem(FindAnimationLibraryIndex(library.Id), library);
        CommitProject(_project with { AnimationLibraries = libraries });
        SelectedScriptLibrary = AnimationScriptLibraries.FirstOrDefault(s => s.Id == library.Id);
        Timeline.Events.Load(sequences, sequence.Id);
    }

    internal static AnimationSequenceUse CreateAnimationSequenceDefaults(
        string name,
        string animationName,
        Guid? animationReferenceId,
        FrameRate frameRate,
        long frameCount) =>
        new()
        {
            Name = name,
            Anm2Name = animationName,
            AnimationReferenceId = animationReferenceId,
            FPS = frameRate.FramesPerSecond,
            SourceStartFrame = 0,
            SourceEndFrame = Math.Max(0, frameCount - 1),
            WeightMode = 1,
            WeightTime = .5,
        };

    private double ResolveEventSourceFrame(double localFrame) => Timeline.Events.SelectedSequence is { } sequence ? sequence.SourceStartFrame + localFrame : localFrame;

    public async Task ExportEventAnimationRpackAsync(AnimationEventEditorViewModel editor)
    {
        await Task.Yield();
        if (IsBusy) return;
        Guid? animationId = editor.SelectedSequence?.AnimationReferenceId;
        ExportVariantSelectionViewModel? target = ExportVariants.FirstOrDefault(row => row.AnimationId == animationId);
        if (target is null || !target.IsEnabled)
        {
            StatusText = target is null ? "Select an animation sequence to export" : $"Animation not ready: {target.Readiness}";
            return;
        }
        var previous = ExportVariants.ToDictionary(row => row.AnimationId, row => row.IsSelected);
        try
        {
            foreach (var row in ExportVariants) row.IsSelected = row.AnimationId == animationId;
            await ExportAnimationRPackCommand.ExecuteAsync(null);
        }
        finally
        {
            foreach (var row in ExportVariants)
                if (previous.TryGetValue(row.AnimationId, out bool selected)) row.IsSelected = selected;
        }
    }

    private async void OnAnimationEventSequenceSelected(object? sender, EventArgs args)
    {
        StopAnimationEventAudio();
        if (_refreshingAnimationEvents) return;
        AnimationSequenceUse? selected = Timeline.Events.SelectedSequence;
        if (selected?.AnimationReferenceId is { } id && id != _activeAnimationId && !IsBusy)
        {
            await ActivateAnimationAsync(id, beginPlayback: false, persistActivation: false, switchWorkspace: false);
            if (Timeline.Events.SelectedSequence?.Id != selected.Id) return;
            Timeline.FramesPerSecond = selected.FPS > 0 ? selected.FPS : Timeline.FramesPerSecond;
            Timeline.EndFrame = (int)Math.Clamp(Math.Ceiling(selected.SourceEndFrame - selected.SourceStartFrame), 1, int.MaxValue - 1);
            Timeline.PositionFrame = 0;
        }
        RefreshEditableSkeletonPreview();
    }
}
