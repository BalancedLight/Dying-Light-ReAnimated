using ReAnimated.App.Infrastructure;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    public async Task<WorkspaceSnapshot> PrepareSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task pending = _modelsTargetRefreshTask;
            await pending.WaitAsync(cancellationToken);
            long revision = Models.PersistenceRevision;
            await SynchronizeModelsWorkspaceProjectAsync(cancellationToken);
            if (revision != Models.PersistenceRevision || !ReferenceEquals(pending, _modelsTargetRefreshTask))
                continue;
            return CreateSnapshot();
        }
    }
}
