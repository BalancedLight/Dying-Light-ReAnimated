using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _refreshingModelEvents;

    private void InitializeModelAnimationEvents()
    {
        InitializeModelEventAudio();
        Timeline.Events.ImportRequested += ImportModelAnimationEventScript;
        Timeline.Events.ExportSourceRequested += (_, _) =>
        {
            try
            {
                string text = ReAnimated.Codecs.Models.AnimationSequenceExport.Source(Timeline.Events.Snapshot());
                var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export animation script", Filter = "Animation script|*.scr", FileName = "animations.scr", OverwritePrompt = true };
                if (picker.ShowDialog() == true) { MainWindowViewModel.WriteEventSourceAtomic(picker.FileName, text); Timeline.Events.Status = "Script exported"; }
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { Timeline.Events.Status = exception.Message; }
        };
        Timeline.Events.Changed += (_, _) =>
        {
            if (_refreshingModelEvents || _model is null || IsBusy) return;
            AuthoringSnapshot before = CaptureAuthoringSnapshot();
            Guid? clipId = SelectedAnimation?.Id;
            ImmutableArray<AnimationSequenceUse> edited = Timeline.Events.Snapshot();
            ImmutableArray<AnimationSequenceUse> all = _model.Package.Document.SequenceUses.Where(s => s.AnimationReferenceId != clipId).Concat(edited).ToImmutableArray();
            _model = _model with { Package = _model.Package with { Document = _model.Package.Document with { SequenceUses = all, LastBuildReceipt = null } } };
            RecordAuthoringUndo(before);
            MarkAuthoringChanged();
            RefreshPreview();
        };
        Timeline.Events.NewSequenceRequested += (_, _) =>
        {
            if (_model is null || SelectedAnimation is not { } selected || IsBusy) { Timeline.Events.Status = "Select an animation first"; return; }
            string baseName = Dl1SourceModelWriter.SanitizeName(selected.DisplayName, 63);
            string name = baseName;
            for (int suffix = 2; _model.Package.Document.SequenceUses.Any(s => s.Name == name); suffix++) name = baseName + "_" + suffix;
            AnimationClip clip = selected.DecodedClip
                ?? throw new InvalidOperationException("The selected animation has no decoded clip.");
            AnimationSequenceUse sequence = MainWindowViewModel.CreateAnimationSequenceDefaults(
                name, baseName + ".anm2", selected.Id, clip.FrameRate, clip.FrameCount);
            AuthoringSnapshot before = CaptureAuthoringSnapshot();
            _model = _model with { Package = _model.Package with { Document = _model.Package.Document with { SequenceUses = _model.Package.Document.SequenceUses.Add(sequence), LastBuildReceipt = null } } };
            RecordAuthoringUndo(before);
            MarkAuthoringChanged();
            RefreshModelAnimationEvents(sequence.Id);
        };
    }

    private void RefreshModelAnimationEvents(Guid? preferred = null)
    {
        if (_refreshingModelEvents) return;
        _refreshingModelEvents = true;
        try
        {
            RefreshModelAnimationEventPermissions();
            Timeline.Events.Load(_model?.Package.Document.SequenceUses.Where(s => s.AnimationReferenceId == SelectedAnimation?.Id) ?? [], preferred);
        }
        finally { _refreshingModelEvents = false; }
    }

    private void RefreshModelAnimationEventPermissions()
    {
        Timeline.Events.CanEdit = _model is not null && !IsBusy;
        Timeline.Events.CanCreate = _model is not null && !IsBusy && SelectedAnimation is not null;
    }

    private async void ImportModelAnimationEventScript(object? sender, EventArgs args)
    {
        if (_model is null || SelectedAnimation is not { } selected || IsBusy) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Import animation script", Filter = "Animation script|*.scr", CheckFileExists = true };
        if (picker.ShowDialog() != true) return;
        var expected = _model;
        long revision = PersistenceRevision;
        try
        {
            if (new System.IO.FileInfo(picker.FileName).Length > ReAnimated.Core.Project.ProjectAnimationLibrary.MaximumAuthoredScriptLength) throw new System.IO.InvalidDataException("The script is too large.");
            string text = await System.IO.File.ReadAllTextAsync(picker.FileName);
            if (!ReferenceEquals(_model, expected) || PersistenceRevision != revision || SelectedAnimation?.Id != selected.Id) { Timeline.Events.Status = "The animation changed; import again"; return; }
            var sequences = ReAnimated.Codecs.AnimationScripts.AnimationScriptTextCodec.Read(text).Sequences;
            string animation = Dl1SourceModelWriter.SanitizeName(selected.DisplayName, 63) + ".anm2";
            sequences = sequences.Select(s => s with { AnimationReferenceId = selected.Id, Anm2Name = animation }).ToImmutableArray();
            if (sequences.IsEmpty) { Timeline.Events.Status = "No sequences found"; return; }
            AuthoringSnapshot before = CaptureAuthoringSnapshot();
            var all = expected.Package.Document.SequenceUses.AddRange(sequences);
            _model = expected with { Package = expected.Package with { Document = expected.Package.Document with { SequenceUses = all, LastBuildReceipt = null } } };
            RecordAuthoringUndo(before);
            MarkAuthoringChanged();
            RefreshModelAnimationEvents(sequences[0].Id);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { Timeline.Events.Status = exception.Message; }
    }
}
