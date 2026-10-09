using System.IO;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    internal Guid CaptureEditedAnimation(FbxModelAuthoringImportResult expectedModel,
        Guid originalClipId, string name, ReAnimated.Core.Domain.AnimationClip sampledClip)
    {
        if (!ReferenceEquals(_model, expectedModel) || IsBusy)
            throw new InvalidOperationException("The model changed before the animation could be saved.");
        FbxModelAuthoringImportResult candidate = FbxAuthoredAnimationAuthoring.Capture(expectedModel, name, sampledClip);
        Guid clipId = candidate.Package.Document.AnimationClips.Except(expectedModel.Package.Document.AnimationClips).Single().Id;
        candidate = candidate with { Package = candidate.Package with { Document = candidate.Package.Document with
        {
            AnimationClips = candidate.Package.Document.AnimationClips.Select(clip => clip.Id == originalClipId
                ? clip with { Included = false } : clip).ToImmutableArray(),
        } } };
        AuthoringSnapshot before = CaptureAuthoringSnapshot();
        CommitModel(candidate, _sourcePath, _packagePath, markAuthoringChanged: false, preserveAuthoringHistory: true);
        RecordAuthoringUndo(before);
        SelectedAnimation = Animations.Single(clip => clip.Id == clipId);
        MarkAuthoringChanged();
        BuildStatus = $"Saved {name} to the model.";
        _setStatus(BuildStatus);
        return clipId;
    }

    private AsyncRelayCommand? _createAuthoredAnimationCommand;

    public IAsyncRelayCommand CreateAuthoredAnimationCommand =>
        _createAuthoredAnimationCommand ??= new AsyncRelayCommand(
            CreateAuthoredAnimationAsync,
            () => HasModel && _model?.Rig is not null && !IsBusy);

    private Task CreateAuthoredAnimationAsync()
    {
        if (_model?.Rig is null || IsBusy)
            return Task.CompletedTask;

        AuthoredAnimationDialogResult? input = _fileDialogs.ShowNewAnimationDialog(
            SuggestAuthoredAnimationName());
        if (input is null)
            return Task.CompletedTask;

        try
        {
            AuthoredAnimationDialogResult request = input.Validate();
            SyncDocument();
            FbxModelAuthoringImportResult original = _model ??
                throw new InvalidOperationException("The model changed before clip creation.");
            AuthoringSnapshot before = CaptureAuthoringSnapshot();
            FbxModelAuthoringImportResult candidate = FbxAuthoredAnimationAuthoring.Create(
                original,
                request.Name,
                request.DurationSeconds,
                request.FrameRate);
            Guid clipId = candidate.Package.Document.AnimationClips
                .Except(original.Package.Document.AnimationClips)
                .Single().Id;

            long revisionBeforeCommit = PersistenceRevision;
            CommitModel(
                candidate,
                _sourcePath,
                _packagePath,
                markAuthoringChanged: false,
                preserveAuthoringHistory: true);
            RecordAuthoringUndo(before);
            SelectedAnimation = Animations.Single(animation => animation.Id == clipId);
            if (PersistenceRevision == revisionBeforeCommit)
                MarkAuthoringChanged();
            BuildStatus = $"Created {SelectedAnimation.DisplayName}. Add keys in Animate.";
            _setStatus(BuildStatus);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
                OverflowException)
        {
            BuildStatus = $"Animation could not be created: {exception.Message}";
            _setStatus(BuildStatus);
        }

        return Task.CompletedTask;
    }

    private string SuggestAuthoredAnimationName()
    {
        string baseName = "New animation";
        if (!Animations.Any(animation => string.Equals(
                animation.DisplayName,
                baseName,
                StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (int suffix = 2; suffix <= Animations.Count + 1; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!Animations.Any(animation => string.Equals(
                    animation.DisplayName,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return baseName;
    }
}
