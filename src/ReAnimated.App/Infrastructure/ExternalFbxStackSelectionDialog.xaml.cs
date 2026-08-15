using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;

namespace ReAnimated.App.Infrastructure;

public partial class ExternalFbxStackSelectionDialog : Window,
    INotifyPropertyChanged
{
    private FbxFacialSourceValueUnit _facialSourceValueUnit =
        FbxFacialSourceValueUnit.Percent;

    public ExternalFbxStackSelectionDialog(
        string sourceName,
        IReadOnlyList<FbxExternalAnimationStackDescriptor> stacks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(stacks);
        InitializeComponent();
        TitleText = $"Animation stacks in {sourceName}";
        int importableCount = stacks.Count(static candidate =>
            candidate.CanImport);
        Rows = new ObservableCollection<ExternalFbxStackSelectionRow>(
            stacks.Select(stack =>
                new ExternalFbxStackSelectionRow(
                    stack,
                    isSelected: stack.CanImport &&
                        importableCount == 1)));
        foreach (ExternalFbxStackSelectionRow row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TitleText { get; }

    public ObservableCollection<ExternalFbxStackSelectionRow> Rows { get; }

    public IReadOnlyList<FbxFacialSourceValueUnit>
        FacialSourceValueUnits { get; } =
        [
            FbxFacialSourceValueUnit.Percent,
            FbxFacialSourceValueUnit.Normalized,
        ];

    public FbxFacialSourceValueUnit FacialSourceValueUnit
    {
        get => _facialSourceValueUnit;
        set
        {
            if (_facialSourceValueUnit == value)
            {
                return;
            }

            _facialSourceValueUnit = value;
            OnPropertyChanged();
        }
    }

    public string SelectionSummary =>
        $"{Rows.Count(static row => row.IsSelected):N0} of " +
        $"{Rows.Count(static row => row.CanImport):N0} importable stack(s) checked";

    public ExternalFbxAnimationStackSelection? Selection { get; private set; }

    private void Import_Click(object sender, RoutedEventArgs args)
    {
        ImmutableArray<long> selected = Rows
            .Where(static row => row.IsSelected && row.CanImport)
            .Select(static row => row.StackObjectId)
            .ToImmutableArray();
        if (selected.IsEmpty)
        {
            MessageBox.Show(
                this,
                "Check at least one importable animation stack.",
                "Select FBX animation stacks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (selected.Length >
                ExternalFbxAnimationStackSelection.MaximumSelectedStacks)
        {
            MessageBox.Show(
                this,
                $"Select at most {ExternalFbxAnimationStackSelection.MaximumSelectedStacks:N0} stacks per import.",
                "Select FBX animation stacks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Selection = new ExternalFbxAnimationStackSelection(
                selected,
                FacialSourceValueUnit)
            .Validate();
        DialogResult = true;
    }

    private void OnRowPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName ==
            nameof(ExternalFbxStackSelectionRow.IsSelected))
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

public sealed class ExternalFbxStackSelectionRow :
    INotifyPropertyChanged
{
    private bool _isSelected;

    public ExternalFbxStackSelectionRow(
        FbxExternalAnimationStackDescriptor stack,
        bool isSelected)
    {
        ArgumentNullException.ThrowIfNull(stack);
        StackObjectId = stack.StackObjectId;
        Name = stack.Name;
        CanImport = stack.CanImport;
        _isSelected = isSelected && CanImport;
        Timing =
            $"{stack.FrameRate.Numerator}/{stack.FrameRate.Denominator} fps | {stack.FrameCount:N0} frames";
        Roles = stack.Roles.ToString();
        SourceRig = DescribeSourceRig(stack);
        Layers = stack.LayerNames.IsEmpty
            ? "None"
            : string.Join(", ", stack.LayerNames);
        Diagnostics = stack.Diagnostics.IsEmpty
            ? "Ready"
            : string.Join(
                " | ",
                stack.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Severity}: {diagnostic.Message}"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long StackObjectId { get; }

    public string Name { get; }

    public bool CanImport { get; }

    public string Timing { get; }

    public string Roles { get; }

    public string SourceRig { get; }

    public string Layers { get; }

    public string Diagnostics { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool normalized = value && CanImport;
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

    private static string DescribeSourceRig(
        FbxExternalAnimationStackDescriptor stack)
    {
        if (!string.IsNullOrWhiteSpace(stack.SourceRigId))
        {
            return stack.SourceRigId;
        }

        if (stack.Diagnostics.Any(static diagnostic =>
                diagnostic.Code is "body_import_failed" or
                    "invalid_skeletal_stack"))
        {
            return "Rig preparation failed";
        }

        return (stack.Roles & AnimationSourceRoles.Facial) != 0
            ? "Facial-only (no skeletal rig)"
            : "No skeletal animation";
    }
}
