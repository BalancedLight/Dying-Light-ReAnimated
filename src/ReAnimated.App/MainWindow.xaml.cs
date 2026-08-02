using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly WorkspaceAutosaveService _autosave;
    private bool _isLoaded;

    public MainWindow(
        MainWindowViewModel viewModel,
        WorkspaceAutosaveService autosave)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _autosave = autosave ?? throw new ArgumentNullException(nameof(autosave));
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        _autosave.AutosaveCompleted += OnAutosaveCompleted;
    }

    private void OnWindowLoaded(
        object sender,
        RoutedEventArgs args)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _autosave.Start();
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void OnWindowClosing(
        object? sender,
        CancelEventArgs args)
    {
        _autosave.Stop();
        _ = _autosave.SaveNow("window-closing");
    }

    private void OnWindowClosed(
        object? sender,
        EventArgs args)
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
        _autosave.AutosaveCompleted -= OnAutosaveCompleted;
        _autosave.Dispose();
        Loaded -= OnWindowLoaded;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
    }

    private void OnCompositionRendering(
        object? sender,
        EventArgs args)
    {
        _viewModel.TickPlayback(DateTimeOffset.UtcNow);
    }

    private void OnAutosaveCompleted(
        object? sender,
        AutosaveCompletedEventArgs args)
    {
        _viewModel.NotifyAutosave(args);
    }

    private void OnCloseCommandExecuted(
        object sender,
        ExecutedRoutedEventArgs args)
    {
        Close();
    }

    private async void OnAssetExplorerDoubleClick(
        object sender,
        MouseButtonEventArgs args)
    {
        if (_viewModel.AssetBrowser.SelectedAsset?.Kind ==
                AssetKind.Mesh &&
            _viewModel.PreviewSelectedAssetCommand.CanExecute(null))
        {
            args.Handled = true;
            await _viewModel.PreviewSelectedAssetCommand
                .ExecuteAsync(null);
            return;
        }

        if (_viewModel.AssetBrowser.SelectedAsset?.Kind !=
                AssetKind.Animation ||
            !_viewModel.PlaySelectedExplorerAnimationCommand.CanExecute(
                null))
        {
            return;
        }

        args.Handled = true;
        await _viewModel.PlaySelectedExplorerAnimationCommand
            .ExecuteAsync(null);
    }

    private void OnAssetExplorerPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        if (sender is not ListBox listBox ||
            ItemsControl.ContainerFromElement(
                listBox,
                args.OriginalSource as DependencyObject) is not
            ListBoxItem item)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();
    }

    private async void OnAnimationLibraryDoubleClick(
        object sender,
        MouseButtonEventArgs args)
    {
        if (!_viewModel.ActivateSelectedAnimationCommand.CanExecute(
                null))
        {
            return;
        }

        args.Handled = true;
        await _viewModel.ActivateSelectedAnimationCommand
            .ExecuteAsync(null);
    }
}
