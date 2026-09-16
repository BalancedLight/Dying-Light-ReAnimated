using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private void OnStudioMetadataRequested(object? sender, StudioMetadataEventArgs e)
    {
        if (!ReferenceEquals(_model, e.Model)) return;
        var previous = _model!;
        var currentSession = previous.Package.Document.RiggingSession;
        if (ReferenceEquals(currentSession, e.Session)) return;
        if (currentSession is not null && !e.Session.Matches(currentSession.CreateJobToken())) return;
        var document = previous.Package.Document with { RiggingSession = e.Session };
        document.Validate();
        var before = e.Undoable ? CaptureAuthoringSnapshot() : null;
        _model = previous with { Package = previous.Package with { Document = document } };
        Conformance.RefreshStudioMetadataSnapshot(previous, _model);
        if (before is not null) RecordAuthoringUndo(before);
        if (currentSession is null) MarkAuthoringChanged();
        else PersistenceStateChanged?.Invoke(this, EventArgs.Empty);
        UpdateConformanceViewportBinding();
        NotifyCommands();
    }

    private bool BuildSnapshotStillCurrent(ReAnimated.Codecs.Fbx.FbxModelAuthoringImportResult built, long revision, string resourceName)
    {
        if (_model is not { } current || revision != Volatile.Read(ref _authoringRevision)) return false;
        if (ReferenceEquals(current, built)) return true;
        var settings = built.Package.Document.BuildSettings;
        return ReAnimated.Codecs.Models.Dl1OfficialModelCompiler.CalculateInputFingerprint(current, resourceName, settings.SurfaceName, settings.AnimationScriptAlias) ==
            ReAnimated.Codecs.Models.Dl1OfficialModelCompiler.CalculateInputFingerprint(built, resourceName, settings.SurfaceName, settings.AnimationScriptAlias);
    }
}
