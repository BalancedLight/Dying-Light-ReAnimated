using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ReAnimated.App.Infrastructure;

public partial class AnimationTargetSelectionDialog : Window,
    INotifyPropertyChanged
{
    public AnimationTargetSelectionDialog(
        string sourceName,
        IReadOnlyList<AnimationTargetModelOption> targetModels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(targetModels);
        InitializeComponent();
        TitleText = $"Add targets for {sourceName}";
        Rows = new ObservableCollection<AnimationTargetSelectionRow>(
            targetModels.Select(static model =>
                new AnimationTargetSelectionRow(model)));
        foreach (AnimationTargetSelectionRow row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TitleText { get; }

    public ObservableCollection<AnimationTargetSelectionRow> Rows { get; }

    public string SelectionSummary =>
        $"{Rows.Count(static row => row.IsSelected && row.CanSelect):N0} new target(s) checked; " +
        $"{Rows.Count(static row => row.IsAlreadyAssigned):N0} already assigned";

    public AnimationTargetSelection? Selection { get; private set; }

    private void Add_Click(object sender, RoutedEventArgs args)
    {
        ImmutableArray<Guid> selected = Rows
            .Where(static row => row.IsSelected && row.CanSelect)
            .Select(static row => row.ModelId)
            .ToImmutableArray();
        if (selected.IsEmpty)
        {
            MessageBox.Show(
                this,
                "Check at least one additional rigged project model.",
                "Add animation targets",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Selection = new AnimationTargetSelection(selected).Validate();
        DialogResult = true;
    }

    private void OnRowPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AnimationTargetSelectionRow.IsSelected))
        {
            OnPropertyChanged(nameof(SelectionSummary));
        }
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}

public sealed class AnimationTargetSelectionRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public AnimationTargetSelectionRow(AnimationTargetModelOption model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.ModelId == Guid.Empty)
        {
            throw new ArgumentException(
                "An animation target row requires a stable project-model ID.",
                nameof(model));
        }

        ModelId = model.ModelId;
        Name = model.Name;
        Source = model.Source;
        Contract = model.Contract;
        IsAlreadyAssigned = model.IsAlreadyAssigned;
        CanSelect = string.IsNullOrWhiteSpace(model.UnavailableReason) &&
            !model.IsAlreadyAssigned;
        _isSelected = model.IsAlreadyAssigned;
        Status = !string.IsNullOrWhiteSpace(model.UnavailableReason)
            ? model.UnavailableReason
            : model.IsAlreadyAssigned
                ? "Already assigned"
                : "Available";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid ModelId { get; }

    public string Name { get; }

    public string Source { get; }

    public string Contract { get; }

    public string Status { get; }

    public bool IsAlreadyAssigned { get; }

    public bool CanSelect { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool normalized = IsAlreadyAssigned || value && CanSelect;
            if (_isSelected == normalized)
            {
                return;
            }

            _isSelected = normalized;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
