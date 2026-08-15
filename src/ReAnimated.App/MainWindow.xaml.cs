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
    private GridLength _visibleSourceViewportWidth =
        new(1.0, GridUnitType.Star);
    private bool _isLoaded;

    public MainWindow(
        MainWindowViewModel viewModel,
        WorkspaceAutosaveService autosave)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _autosave = autosave ?? throw new ArgumentNullException(nameof(autosave));
        InitializeComponent();
        DataContext = _viewModel;

        // ModelsWorkspaceSurface is deliberately removed from the visual tree
        // whenever another workspace owns the D3D viewport. Do not rely on an
        // inherited DataContext binding for that detachable surface: after an
        // airspace teardown it can return with a null binding source, leaving
        // every Models button visibly enabled but with no command to execute.
        // A direct reference is stable for the lifetime of this window and is
        // retained across every detach/reattach cycle.
        ModelsWorkspaceSurface.DataContext = _viewModel.Models;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyWorkspaceSurfaceLayout();
        ApplyWorkflowAirspaceLayout();
        ApplyViewportColumnLayout();
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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _autosave.Dispose();
        Loaded -= OnWindowLoaded;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        bool workspaceSurfaceChanged =
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsCustomModelAuthoringSurfaceVisible),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsModelsWorkspace),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsAnimationWorkspaceSurfaceVisible),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsExportWorkspace),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsAnimationsWorkspace),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsPlaybackWorkspace),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsRetargetWorkspace),
                StringComparison.Ordinal);
        bool viewportLayoutChanged = string.Equals(
            args.PropertyName,
            nameof(MainWindowViewModel.IsSourceViewportVisible),
            StringComparison.Ordinal);
        if (!workspaceSurfaceChanged && !viewportLayoutChanged)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => ApplyShellLayout(
                    workspaceSurfaceChanged,
                    viewportLayoutChanged));
            return;
        }

        ApplyShellLayout(
            workspaceSurfaceChanged,
            viewportLayoutChanged);
    }

    private void ApplyShellLayout(
        bool workspaceSurfaceChanged,
        bool viewportLayoutChanged)
    {
        if (workspaceSurfaceChanged)
        {
            ApplyWorkspaceSurfaceLayout();
            ApplyWorkflowAirspaceLayout();
        }

        if (workspaceSurfaceChanged || viewportLayoutChanged)
        {
            ApplyViewportColumnLayout();
        }
    }

    private void ApplyWorkspaceSurfaceLayout()
    {
        FrameworkElement activeSurface =
            _viewModel.IsCustomModelAuthoringSurfaceVisible
                ? ModelsWorkspaceSurface
                : AnimationWorkspaceSurface;
        FrameworkElement inactiveSurface =
            _viewModel.IsCustomModelAuthoringSurfaceVisible
                ? AnimationWorkspaceSurface
                : ModelsWorkspaceSurface;

        // Visibility alone is insufficient for HwndHost. A collapsed workspace
        // can leave its native D3D child in the Win32 airspace, where it covers
        // the active workspace and keeps the previous pane's size and camera.
        // Keep exactly one workspace surface in the visual tree so switching
        // tears down the inactive HWND before the replacement is arranged.
        EditorRootGrid.Children.Remove(inactiveSurface);
        if (!EditorRootGrid.Children.Contains(activeSurface))
        {
            Grid.SetRow(activeSurface, 2);
            EditorRootGrid.Children.Add(activeSurface);
        }

        // Keep this invariant local to the detach/reattach boundary. It also
        // repairs the surface if a WPF theme or future layout pass clears an
        // inherited context while the native viewport is being reconstructed.
        ModelsWorkspaceSurface.DataContext = _viewModel.Models;

        EditorRootGrid.InvalidateMeasure();
        EditorRootGrid.InvalidateArrange();
    }

    private void ApplyWorkflowAirspaceLayout()
    {
        bool animationSurfaceIsAttached =
            EditorRootGrid.Children.Contains(AnimationWorkspaceSurface);
        bool modelsOwnViewport =
            animationSurfaceIsAttached && _viewModel.IsModelsWorkspace;
        bool retargetOwnsViewport =
            animationSurfaceIsAttached &&
            _viewModel.IsRetargetWorkspace;
        bool animationsOwnViewport =
            animationSurfaceIsAttached &&
            _viewModel.IsAnimationsWorkspace;
        bool playbackOwnsViewport =
            animationSurfaceIsAttached &&
            _viewModel.IsPlaybackWorkspace;

        // HwndHost always wins over WPF z-order. Each workflow owns a distinct
        // viewport arrangement, so the Retarget/Edit comparison leaves the
        // visual tree whenever another workflow becomes active. Otherwise its
        // native target window punches through the active workspace.
        if (retargetOwnsViewport)
        {
            if (!ViewportRegionGrid.Children.Contains(ViewportGrid))
            {
                Grid.SetRow(ViewportGrid, 0);
                ViewportRegionGrid.Children.Add(ViewportGrid);
            }
        }
        else
        {
            ViewportRegionGrid.Children.Remove(ViewportGrid);
        }

        // The Models workflow owns a separate selection-only preview HwndHost.
        // Remove that whole surface on every other tab so its native child is
        // torn down before Export or the authoring viewport is arranged.
        if (modelsOwnViewport)
        {
            if (!AnimationWorkspaceSurface.Children.Contains(
                    ModelsWorkflowSurface))
            {
                Grid.SetColumnSpan(ModelsWorkflowSurface, 5);
                AnimationWorkspaceSurface.Children.Add(
                    ModelsWorkflowSurface);
            }
        }
        else
        {
            AnimationWorkspaceSurface.Children.Remove(
                ModelsWorkflowSurface);
        }

        SetWorkflowSurfaceAttached(
            AnimationsWorkflowSurface,
            animationsOwnViewport);
        SetWorkflowSurfaceAttached(
            PlaybackWorkflowSurface,
            playbackOwnsViewport);

        ViewportRegionGrid.InvalidateMeasure();
        ViewportRegionGrid.InvalidateArrange();
        AnimationWorkspaceSurface.InvalidateMeasure();
        AnimationWorkspaceSurface.InvalidateArrange();
    }

    private void SetWorkflowSurfaceAttached(
        FrameworkElement surface,
        bool shouldAttach)
    {
        if (shouldAttach)
        {
            if (!AnimationWorkspaceSurface.Children.Contains(surface))
            {
                Grid.SetColumnSpan(surface, 5);
                AnimationWorkspaceSurface.Children.Add(surface);
            }

            return;
        }

        // ViewportPane contains HwndHost. Removing an inactive workspace from
        // the visual tree is required; Collapsed alone can leave the native
        // child above the next workspace and retain the previous pane size.
        AnimationWorkspaceSurface.Children.Remove(surface);
    }

    private void ApplyViewportColumnLayout()
    {
        // Retarget/Edit always compares the original source hierarchy with the
        // target. A meshless FBX still owns a skeleton-only presentation, so
        // direct variants must not collapse the source pane just because no
        // mapping proposal is required.
        if (_viewModel.IsRetargetWorkspace ||
            _viewModel.IsSourceViewportVisible)
        {
            SourceViewportColumn.MinWidth = 0.0;
            SourceViewportColumn.MaxWidth = double.PositiveInfinity;
            SourceViewportColumn.Width =
                _visibleSourceViewportWidth.Value > 0.0
                    ? _visibleSourceViewportWidth
                    : new GridLength(1.0, GridUnitType.Star);
            ViewportSplitterColumn.Width = new GridLength(6.0);
            if (!ViewportGrid.Children.Contains(SourceViewportPane))
            {
                Grid.SetRow(SourceViewportPane, 1);
                Grid.SetColumn(SourceViewportPane, 0);
                ViewportGrid.Children.Add(SourceViewportPane);
            }

            ViewportGrid.InvalidateMeasure();
            ViewportGrid.InvalidateArrange();
            return;
        }

        if (SourceViewportColumn.Width.Value > 0.0)
        {
            _visibleSourceViewportWidth = SourceViewportColumn.Width;
        }

        // A collapsed HwndHost can retain its native child window and stale
        // Direct3D airspace even when WPF reports a zero-width column. Remove
        // the source pane from the visual tree in every single-pane layout so
        // the HWND is destroyed instead of leaving a blue strip behind.
        ViewportGrid.Children.Remove(SourceViewportPane);
        SourceViewportColumn.MinWidth = 0.0;
        SourceViewportColumn.MaxWidth = 0.0;
        SourceViewportColumn.Width = new GridLength(0.0);
        ViewportSplitterColumn.Width = new GridLength(0.0);
        ViewportGrid.InvalidateMeasure();
        ViewportGrid.InvalidateArrange();
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
        if (!_viewModel.OpenSelectedAnimationCommand.CanExecute(
                null))
        {
            return;
        }

        args.Handled = true;
        await _viewModel.OpenSelectedAnimationCommand
            .ExecuteAsync(null);
    }

    private void OnResetRetargetLayoutClick(
        object sender,
        RoutedEventArgs args)
    {
        // The pre-workspace shell persisted a horizontal explorer/editor
        // split. Retarget/Edit is vertical now, so reset that legacy width
        // state to one full-width content column before restoring its own
        // bounded rows.
        RetargetViewportColumn.Width =
            new GridLength(1.0, GridUnitType.Star);
        RetargetEditorSplitterColumn.Width = new GridLength(0.0);
        RetargetEditorColumn.Width = new GridLength(0.0);
        RetargetViewportRow.Height =
            new GridLength(3.0, GridUnitType.Star);
        RetargetEditorRow.Height =
            new GridLength(2.3, GridUnitType.Star);
        RetargetBottomDockRow.Height =
            new GridLength(220.0);
        SourceViewportColumn.Width =
            new GridLength(1.0, GridUnitType.Star);
        ViewportGrid.ColumnDefinitions[2].Width =
            new GridLength(1.0, GridUnitType.Star);

        AnimationWorkspaceSurface.InvalidateMeasure();
        AnimationWorkspaceSurface.InvalidateArrange();
    }
}
