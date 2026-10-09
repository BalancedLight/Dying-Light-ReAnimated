namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool HasAppControlUnsavedChanges => IsDirty ||
        Models.PersistenceRevision != _savedModelsRevision || !_modelsTargetRefreshTask.IsCompleted ||
        HasPendingSecondaryMotionEdits;

    public async Task OpenWorkspaceForAppControlAsync(string projectPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsBusy || Models.IsBusy) throw new InvalidOperationException("The app is busy.");
        if (HasAppControlUnsavedChanges)
            throw new InvalidOperationException("Save the current project before opening another.");
        string path = ReAnimated.Core.Automation.AppControlHandler.ValidateExistingProjectPath(projectPath);
        bool opened = await OpenWorkspaceCoreAsync(path, cancellationToken);
        if (!opened)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The project could not be opened.");
        }
    }
}
