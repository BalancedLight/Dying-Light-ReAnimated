using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private Func<CancellationToken, Task<Dl1RigTemplateResolution>>? _pickRetailReference;

    public IAsyncRelayCommand UseSelectedRetailMeshCommand { get; private set; } = null!;

    internal void SetRetailReferencePicker(Func<CancellationToken, Task<Dl1RigTemplateResolution>> picker)
    {
        _pickRetailReference = picker ?? throw new ArgumentNullException(nameof(picker));
        UseSelectedRetailMeshCommand?.NotifyCanExecuteChanged();
    }

    private void InitializeRetailReferenceSelection() =>
        UseSelectedRetailMeshCommand = new AsyncRelayCommand(UseSelectedRetailMeshAsync,
            () => !IsBusy && _pickRetailReference is not null);

    private async Task UseSelectedRetailMeshAsync(CancellationToken cancellationToken)
    {
        if (_pickRetailReference is not { } pick) return;
        var source = _model;
        IsBusy = true;
        try
        {
            Dl1RigTemplateResolution result = await pick(cancellationToken).ConfigureAwait(true);
            if (!ReferenceEquals(source, _model) || cancellationToken.IsCancellationRequested) return;
            if (result.Succeeded) TemplateProfileName = result.ProfileName;
            UseTemplateResolution(result);
        }
        finally
        {
            IsBusy = false;
            NotifyStateChanged();
        }
    }
}
