using System.Windows;

namespace ReAnimated.App.Infrastructure;

public partial class RecoverySaveFailureDialog : Window
{
    public RecoverySaveFailureDialog(string details)
    {
        InitializeComponent();
        ErrorText.Text = details;
    }

    public RecoveryCloseDecision Decision { get; private set; } = RecoveryCloseDecision.Cancel;

    private void Retry_Click(object sender, RoutedEventArgs args)
    {
        Decision = RecoveryCloseDecision.Retry;
        DialogResult = true;
    }

    private void SaveElsewhere_Click(object sender, RoutedEventArgs args)
    {
        Decision = RecoveryCloseDecision.SaveElsewhere;
        DialogResult = true;
    }
}
