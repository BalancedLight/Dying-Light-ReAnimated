using System.Windows;

namespace ReAnimated.App.Infrastructure;

/// <summary>Optional UI capability for confirmations that have no headless action by default.</summary>
public interface IAnimationWorkspaceOfferDialog
{
    bool ConfirmEnableAnimationWorkspace(string modelName);
}

public static class ProjectFileDialogWorkspaceOfferExtensions
{
    /// <summary>Returns false for ordinary/headless dialog services that do not present this prompt.</summary>
    public static bool ConfirmEnableAnimationWorkspace(
        this IProjectFileDialogService dialogs,
        string modelName)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return dialogs is IAnimationWorkspaceOfferDialog offer &&
            offer.ConfirmEnableAnimationWorkspace(modelName);
    }
}

public sealed partial class WindowsProjectFileDialogService : IAnimationWorkspaceOfferDialog
{
    public bool ConfirmEnableAnimationWorkspace(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return MessageBox.Show(
            $"The imported model '{modelName}' contains animation with movement across multiple frames. Enable the animation workspace?",
            "Animation found",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
