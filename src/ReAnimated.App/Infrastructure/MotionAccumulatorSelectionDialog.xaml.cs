using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ReAnimated.App.Infrastructure;

/// <summary>
/// Asks which bone should receive accumulated travel and heading.
/// </summary>
/// <remarks>
/// A rig only advertises an accumulator when it owns a node whose name hashes
/// to the DL1 descriptor 0xCCC3CDDF - in practice one called
/// <c>offsethelper</c>. Rigs that are retargeted onto usually have none, and
/// applying the accumulator policy anyway used to strip the movement off the
/// root and drop it with no warning. This dialog is how the author nominates a
/// track instead.
/// </remarks>
public partial class MotionAccumulatorSelectionDialog : Window,
    INotifyPropertyChanged
{
    private string? _selectedBoneName;

    public MotionAccumulatorSelectionDialog(
        string targetRigId,
        ImmutableArray<string> candidateBoneNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRigId);
        if (candidateBoneNames.IsDefault)
        {
            throw new ArgumentException(
                "A motion-accumulator prompt requires a candidate list.",
                nameof(candidateBoneNames));
        }

        InitializeComponent();
        TitleText = $"Accumulator bone for {targetRigId}";
        CandidateBoneNames = candidateBoneNames;
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TitleText { get; }

    public ImmutableArray<string> CandidateBoneNames { get; }

    public string? SelectedBoneName
    {
        get => _selectedBoneName;
        set
        {
            if (string.Equals(_selectedBoneName, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedBoneName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionSummary));
        }
    }

    public string SelectionSummary =>
        string.IsNullOrWhiteSpace(SelectedBoneName)
            ? $"{CandidateBoneNames.Length:N0} bone(s) available"
            : $"Accumulated motion will be written to '{SelectedBoneName}'";

    /// <summary>
    /// The chosen bone, or null when the author declined.
    /// </summary>
    public string? ChosenBoneName { get; private set; }

    private void Choose_Click(object sender, RoutedEventArgs args) =>
        Choose();

    private void Choose_DoubleClick(object sender, RoutedEventArgs args) =>
        Choose();

    private void Choose()
    {
        if (string.IsNullOrWhiteSpace(SelectedBoneName))
        {
            MessageBox.Show(
                this,
                "Select the bone that should receive the accumulated travel and heading.",
                "Choose a motion-accumulator bone",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ChosenBoneName = SelectedBoneName;
        DialogResult = true;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}
