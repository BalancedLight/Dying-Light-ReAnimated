using System.Windows;

namespace ReAnimated.App.Infrastructure;

public sealed partial class OperationFailureReportDialog : Window
{
    public OperationFailureReportDialog(
        string title,
        string summary,
        string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        InitializeComponent();
        Title = title;
        SummaryTextBlock.Text = summary;
        DetailsTextBox.Text = details;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(
                SummaryTextBlock.Text + Environment.NewLine +
                Environment.NewLine + DetailsTextBox.Text);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or
            InvalidOperationException)
        {
            // The clipboard may temporarily be owned by another process.
        }
    }

}
