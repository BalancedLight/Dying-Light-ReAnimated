using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;

namespace ReAnimated.App.Infrastructure;

public partial class DevelopmentNoticeDialog : Window
{
    private readonly DevelopmentNoticeSettingsStore _settings;

    public DevelopmentNoticeDialog(DevelopmentNoticeSettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
    }

    private void Continue_Click(object sender, RoutedEventArgs args)
    {
        if (DontShowAgain.IsChecked == true)
        {
            try
            {
                _settings.Dismiss();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ErrorMessage.Text = "Your preference couldn't be saved. Try again, or uncheck the box to continue.";
                ErrorMessage.Visibility = Visibility.Visible;
                return;
            }
        }

        DialogResult = true;
    }

    private void Wiki_RequestNavigate(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("https://dldt.miraheze.org/wiki/") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ErrorMessage.Text = "Couldn't open your browser. Visit https://dldt.miraheze.org/wiki/ for the wiki.";
            ErrorMessage.Visibility = Visibility.Visible;
        }
    }
}
