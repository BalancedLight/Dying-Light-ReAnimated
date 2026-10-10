namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public bool HasAppControlUnsavedChanges => IsDirty ||
        Models.PersistenceRevision != _savedModelsRevision || !_modelsTargetRefreshTask.IsCompleted ||
        HasPendingSecondaryMotionEdits;

    // Workspace navigation can dirty an untitled project before any content is added.
    internal bool RequiresRecoverySaveOnClose => HasAppControlUnsavedChanges &&
        (ProjectPath is not null || Models.HasModel || HasPendingSecondaryMotionEdits ||
         !_project.Assets.IsEmpty || !_project.Models.IsEmpty ||
         !_project.AnimationSources.IsEmpty || !_project.AnimationVariants.IsEmpty ||
         !_project.AnimationLibraries.IsEmpty || !_project.Animations.IsEmpty ||
         _project.ModelsWorkspace is not null);

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
