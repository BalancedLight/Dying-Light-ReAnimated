using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class SecondaryMotionViewModel
{
    private readonly Stack<ImmutableArray<NativeClothSource>> _nativeCollisionUndo = new();
    private RelayCommand? _applyNativeCollisionCommand;
    private RelayCommand? _undoNativeCollisionCommand;
    private NativeCollisionChoice? _selectedNativeCollision;
    private string _nativeCollisionRadiusText = "0.05";
    public ObservableCollection<NativeCollisionChoice> NativeCollisionChoices { get; } = [];
    public NativeCollisionChoice? SelectedNativeCollision
    {
        get => _selectedNativeCollision;
        set
        {
            if (!SetProperty(ref _selectedNativeCollision, value)) return;
            if (value is not null) NativeCollisionRadiusText = value.Radius.ToString("R", CultureInfo.InvariantCulture);
            ApplyNativeCollisionCommand.NotifyCanExecuteChanged();
        }
    }
    public string NativeCollisionRadiusText
    {
        get => _nativeCollisionRadiusText;
        set => SetProperty(ref _nativeCollisionRadiusText, value);
    }
    public RelayCommand ApplyNativeCollisionCommand => _applyNativeCollisionCommand ??=
        new(ApplyNativeCollision, () => SelectedNativeCollision is not null);
    public RelayCommand UndoNativeCollisionCommand => _undoNativeCollisionCommand ??=
        new(UndoNativeCollision, () => _nativeCollisionUndo.Count > 0);

    private void RefreshNativeCollisionChoices()
    {
        NativeCollisionChoice? previous = SelectedNativeCollision;
        NativeCollisionChoices.Clear();
        foreach (NativeCollisionChoice choice in Dl1NativeCollisionAuthoring.Inspect(definition)) NativeCollisionChoices.Add(choice);
        SelectedNativeCollision = previous is null ? null : NativeCollisionChoices.FirstOrDefault(choice =>
            choice.ResourceName == previous.ResourceName && choice.CommandIndex == previous.CommandIndex);
        UndoNativeCollisionCommand.NotifyCanExecuteChanged();
    }

    private void ApplyNativeCollision()
    {
        if (SelectedNativeCollision is not { } choice) return;
        try
        {
            if (!double.TryParse(NativeCollisionRadiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out double radius))
                throw new ArgumentException("Enter a numeric radius.");
            SecondaryMotionDefinition next = Dl1NativeCollisionAuthoring.SetRadius(definition, choice, radius);
            next.Validate(BoneNames.Count > 0 ? BoneNames : null);
            _nativeCollisionUndo.Push(definition.NativeSources);
            definition = next;
            RefreshNativeCollisionChoices();
            OnPropertyChanged(nameof(Definition));
            Status = "Native collision updated.";
            PersistenceStatus = "Save a model copy to retain this collision.";
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or FormatException)
        {
            Status = "Collision could not be applied: " + error.Message;
        }
    }

    private void UndoNativeCollision()
    {
        if (_nativeCollisionUndo.Count == 0) return;
        definition = definition with { NativeSources = _nativeCollisionUndo.Pop() };
        RefreshNativeCollisionChoices();
        OnPropertyChanged(nameof(Definition));
        Status = "Native collision edit undone.";
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
