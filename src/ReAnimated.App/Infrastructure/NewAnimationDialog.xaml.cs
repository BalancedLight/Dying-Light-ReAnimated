using System.Globalization;
using System.Windows;

namespace ReAnimated.App.Infrastructure;

public partial class NewAnimationDialog : Window
{
    public NewAnimationDialog(string suggestedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        InitializeComponent();
        NameInput.Text = suggestedName.Trim();
        NameInput.SelectAll();
        NameInput.Focus();
    }

    public AuthoredAnimationDialogResult? Result { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs args)
    {
        string name = NameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter a name.", NameInput);
            return;
        }

        if (!double.TryParse(DurationInput.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double durationSeconds) ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            ShowError("Enter a positive duration in seconds.", DurationInput);
            return;
        }

        if (!int.TryParse(FrameRateNumeratorInput.Text, NumberStyles.None,
                CultureInfo.InvariantCulture, out int numerator) || numerator <= 0 ||
            !int.TryParse(FrameRateDenominatorInput.Text, NumberStyles.None,
                CultureInfo.InvariantCulture, out int denominator) || denominator <= 0)
        {
            ShowError("Enter a positive FPS numerator and denominator.", FrameRateNumeratorInput);
            return;
        }

        try
        {
            var rate = new ReAnimated.Core.Domain.FrameRate(numerator, denominator);
            double intervals = durationSeconds * rate.FramesPerSecond;
            if (!double.IsFinite(intervals) || intervals < 1 || intervals >= ushort.MaxValue)
            {
                ShowError($"Choose a duration that fits within {ushort.MaxValue:N0} frames.", DurationInput);
                return;
            }

            Result = new AuthoredAnimationDialogResult(name, durationSeconds, rate).Validate();
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ShowError(exception.Message, DurationInput);
        }
    }

    private void ShowError(string message, System.Windows.Controls.Control input)
    {
        ValidationMessage.Text = message;
        input.Focus();
        if (input is System.Windows.Controls.TextBox textBox)
            textBox.SelectAll();
    }
}
