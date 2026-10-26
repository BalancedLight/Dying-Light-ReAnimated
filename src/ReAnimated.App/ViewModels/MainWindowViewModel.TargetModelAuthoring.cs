using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private AsyncRelayCommand? _adjustTargetModelCommand;

    public IAsyncRelayCommand AdjustTargetModelCommand => _adjustTargetModelCommand ??=
        new AsyncRelayCommand(AdjustTargetModelAsync, () =>
            !IsBusy && !IsTargetSwitching && _targetProjectAsset?.Kind == ProjectAssetKind.CustomModelSource);

    private void NotifyTargetModelAuthoringCommands() => _adjustTargetModelCommand?.NotifyCanExecuteChanged();

    private async Task AdjustTargetModelAsync()
    {
        if (IsBusy || IsTargetSwitching || _targetProjectAsset is not { Kind: ProjectAssetKind.CustomModelSource } asset) return;
        try
        {
            string path = await ResolveProjectAssetPathAsync(asset, "custom-model package", CancellationToken.None);
            OpenCustomModelAuthoring();
            await Models.OpenPackagePathAsync(path);
            Models.OpenGripAdjustmentsCommand.Execute(null);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = "Target model could not be opened: " + error.Message;
        }
    }
}
