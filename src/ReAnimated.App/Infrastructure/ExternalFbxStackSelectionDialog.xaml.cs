using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ReAnimated.Codecs.Fbx;

namespace ReAnimated.App.Infrastructure;

public partial class ExternalFbxStackSelectionDialog : Window,
    INotifyPropertyChanged
{
    private FbxFacialSourceValueUnit _facialSourceValueUnit =
        FbxFacialSourceValueUnit.Percent;

    public ExternalFbxStackSelectionDialog(
        string sourceName,
        IReadOnlyList<FbxExternalAnimationStackDescriptor> stacks,
        IReadOnlyList<ExternalFbxTargetModelOption> targetModels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(stacks);
        ArgumentNullException.ThrowIfNull(targetModels);
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

        TargetRows = new ObservableCollection<
            ExternalFbxTargetSelectionRow>(
                targetModels.Select(model =>
                    new ExternalFbxTargetSelectionRow(model)));
        foreach (ExternalFbxTargetSelectionRow row in TargetRows)
        {
            row.PropertyChanged += OnTargetRowPropertyChanged;
        }

        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TitleText { get; }

    public ObservableCollection<ExternalFbxStackSelectionRow> Rows { get; }

    public ObservableCollection<ExternalFbxTargetSelectionRow>
        TargetRows { get; }

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
        $"{Rows.Count(static row => row.CanImport):N0} importable stack(s); " +
        $"{TargetRows.Count(static row => row.IsSelected):N0} of " +
        $"{TargetRows.Count(static row => row.CanTarget):N0} rigged target(s) checked";

    public ExternalFbxAnimationStackSelection? Selection { get; private set; }

    private void Import_Click(object sender, RoutedEventArgs args)
    {
        ImmutableArray<long> selected = Rows
            .Where(static row => row.IsSelected && row.CanImport)
            .Select(static row => row.StackObjectId)
            .ToImmutableArray();
        ImmutableArray<Guid> targetModelIds = TargetRows
            .Where(static row => row.IsSelected && row.CanTarget)
            .Select(static row => row.ModelId)
            .ToImmutableArray();
        if (selected.IsEmpty || targetModelIds.IsEmpty)
        {
            MessageBox.Show(
                this,
                "Check at least one importable animation stack and one rigged project model.",
                "Select FBX animation stacks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (selected.Length >
                ExternalFbxAnimationStackSelection.MaximumSelectedStacks ||
            targetModelIds.Length >
                ExternalFbxAnimationStackSelection
                    .MaximumSelectedTargetModels)
        {
            MessageBox.Show(
                this,
                $"Select at most {ExternalFbxAnimationStackSelection.MaximumSelectedStacks:N0} stacks and {ExternalFbxAnimationStackSelection.MaximumSelectedTargetModels:N0} target models per import.",
                "Select FBX animation stacks",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Selection = new ExternalFbxAnimationStackSelection(
                selected,
                FacialSourceValueUnit,
                targetModelIds)
            .Validate();
        DialogResult = true;
    }

    private void OnTargetRowPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName ==
            nameof(ExternalFbxTargetSelectionRow.IsSelected))
        {
            OnPropertyChanged(nameof(SelectionSummary));
        }
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

public sealed class ExternalFbxTargetSelectionRow :
    INotifyPropertyChanged
{
    private bool _isSelected;

    public ExternalFbxTargetSelectionRow(
        ExternalFbxTargetModelOption model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.ModelId == Guid.Empty)
        {
            throw new ArgumentException(
                "A target-model checklist row requires a stable project model ID.",
                nameof(model));
        }

        ModelId = model.ModelId;
        Name = model.Name;
        Source = model.Source;
        Contract = model.Contract;
        CanTarget = !model.IsStatic;
        _isSelected = model.IsSelected && CanTarget;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid ModelId { get; }

    public string Name { get; }

    public string Source { get; }

    public string Contract { get; }

    public bool CanTarget { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool normalized = value && CanTarget;
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
        SourceRig = string.IsNullOrWhiteSpace(stack.SourceRigId)
            ? "No skeletal rig"
            : stack.SourceRigId;
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
}
