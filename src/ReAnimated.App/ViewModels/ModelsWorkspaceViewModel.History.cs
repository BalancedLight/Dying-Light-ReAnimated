using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private sealed record AuthoringSnapshot(
        FbxModelAuthoringImportResult Model, Guid? SelectedHelperId, long? SelectedSourceId,
        string? SelectedName, Guid? SelectedClipId, int Frame, bool ExplicitCharacterId, Guid? SelectedBodyGuideId);

    private AuthoringSnapshot CaptureAuthoringSnapshot()
    {
        FbxModelAuthoringImportResult model = _model ?? throw new InvalidOperationException("There is no model to snapshot.");
        long? sourceId = SelectedBone is { } selected && selected.Index < model.Package.Document.Bones.Length
            ? model.Package.Document.Bones[selected.Index].FbxObjectId : null;
        return new(model, SelectedBone?.AuthoredHelperId, sourceId is 0 ? null : sourceId,
            SelectedBone?.Name, SelectedAnimation?.Id, Timeline.CurrentFrame, _characterIdWasExplicitlyEdited, Conformance.SelectedBodyGuideId);
    }

    private void RecordAuthoringUndo(AuthoringSnapshot snapshot)
    {
        _helperUndo.Push(snapshot);
        _helperRedo.Clear();
        UndoHelperEditCommand.NotifyCanExecuteChanged();
        RedoHelperEditCommand.NotifyCanExecuteChanged();
    }

    private void RestoreAuthoringHistory(Stack<AuthoringSnapshot> from, Stack<AuthoringSnapshot> to, string status)
    {
        if (_model is null || from.Count == 0) return;
        AuthoringSnapshot current = CaptureAuthoringSnapshot();
        AuthoringSnapshot target = from.Peek();
        FbxModelAuthoringImportResult restored = target.Model;
        if (restored.Package.Document.RiggingSession is { } snapshot)
        {
            RiggingSession renewed = _model.Package.Document.RiggingSession is { } active && active.Id == snapshot.Id
                ? RiggingSessions.RestoreForUndo(active, snapshot)
                : snapshot with { Generation = Guid.NewGuid(), Revision = checked(snapshot.Revision + 1) };
            restored = restored with { Package = restored.Package with { Document = restored.Package.Document with { RiggingSession = renewed } } };
        }
        restored.Package.Document.Validate();
        string? selectedName = target.SelectedHelperId is { } helperId
            ? restored.Package.Document.AuthoredHelpers.FirstOrDefault(h => h.Id == helperId)?.Name
            : target.SelectedSourceId is { } sourceId
                ? restored.Package.Document.Bones.FirstOrDefault(b => b.FbxObjectId == sourceId)?.Name : target.SelectedName;
        CommitModel(restored, _sourcePath, _packagePath, markAuthoringChanged: false, preserveAuthoringHistory: true);
        _characterIdWasExplicitlyEdited = target.ExplicitCharacterId;
        Conformance.SelectBodyGuide(target.SelectedBodyGuideId);
        UpdateConformanceViewportBinding();
        PopulateHierarchyRows(selectedName);
        SelectedAnimation = target.SelectedClipId is { } clipId ? Animations.FirstOrDefault(a => a.Id == clipId) : null;
        Timeline.CurrentFrame = Math.Clamp(target.Frame, Timeline.StartFrame, Timeline.EndFrame);
        from.Pop();
        to.Push(current);
        BuildStatus = status;
        _setStatus(status);
        NotifyCommands();
        MarkAuthoringChanged();
    }
}
