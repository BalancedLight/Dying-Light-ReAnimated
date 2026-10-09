using System.Windows;

namespace ReAnimated.App.Infrastructure;

public sealed partial class WindowsProjectFileDialogService
{
    public AuthoredAnimationDialogResult? ShowNewAnimationDialog(string suggestedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        var dialog = new NewAnimationDialog(suggestedName);
        Window? owner = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(static window => window.IsActive);
        if (owner is not null)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
