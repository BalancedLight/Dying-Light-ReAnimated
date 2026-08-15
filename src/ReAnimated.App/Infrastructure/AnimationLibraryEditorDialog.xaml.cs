using System.Windows;

namespace ReAnimated.App.Infrastructure;

public partial class AnimationLibraryEditorDialog : Window
{
    public AnimationLibraryEditorDialog(
        AnimationLibraryEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        ViewModel = new AnimationLibraryEditorViewModel(request);
        DataContext = ViewModel;
    }

    public AnimationLibraryEditorViewModel ViewModel { get; }

    public AnimationLibraryAssignmentResult? Result { get; private set; }

    private void NewLibrary_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.AddLibrary();

    private void RemoveLibrary_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.RemoveSelectedLibrary();

    private void AddProjectImport_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.AddSelectedProjectImport();

    private void AddRetailImport_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.AddSelectedRetailImport();

    private void MoveImportUp_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.MoveSelectedImport(-1);

    private void MoveImportDown_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.MoveSelectedImport(1);

    private void RemoveImport_Click(
        object sender,
        RoutedEventArgs args) =>
        ViewModel.RemoveSelectedImport();

    private void Assign_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (!ViewModel.TryCreateResult(out AnimationLibraryAssignmentResult? result))
        {
            return;
        }

        Result = result;
        DialogResult = true;
    }
}
