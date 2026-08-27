using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AvalonDock.Themes;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.App.Views;

namespace ReAnimated.App;

public partial class MainWindow : Window
{
    private static readonly string[] NormalModelPaneIds =
    [
        "models.project",
        "models.browser",
        "models.preview",
    ];
    private static readonly string[] AuthoringModelPaneIds =
    [
        "models.authoring.settings",
        "models.authoring.preview",
        "models.authoring.timeline",
        "models.authoring.rig",
        "models.authoring.materials",
        "models.authoring.animations",
    ];

    private readonly MainWindowViewModel _viewModel;
    private readonly WorkspaceAutosaveService _autosave;
    private readonly WorkflowDockController _dockController;
    private bool _isLoaded;

    public MainWindow(
        MainWindowViewModel viewModel,
        WorkspaceAutosaveService autosave,
        EditorDockLayoutSettingsStore? dockLayoutSettings = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _autosave = autosave ?? throw new ArgumentNullException(nameof(autosave));
        InitializeComponent();
        DataContext = _viewModel;

        // The Models workspace contributes detachable content to the docking
        // surface. Keep its view model explicit so commands remain valid while
        // panes are hidden, tabbed, auto-hidden, or hosted by a floating window.
        ModelsWorkspaceSurface.DataContext = _viewModel.Models;

        // Dock content is reparented beneath AvalonDock layout items, whose
        // DataContext is not MainWindow's DataContext. Give every pane whose
        // root expects a nested view model a stable direct reference. Without
        // this, the chrome can keep rendering while titles, metadata, timeline
        // state, and SceneSource all silently bind to null.
        AnimationsSourcePreviewPane.DataContext = _viewModel.SourceViewport;
        PlaybackFppViewportPane.DataContext = _viewModel.SourceViewport;
        PlaybackTargetViewportPane.DataContext = _viewModel.TargetViewport;
        PlaybackTimelinePane.DataContext = _viewModel.Timeline;
        SourceViewportPane.DataContext = _viewModel.SourceViewport;
        RetargetTimelinePane.DataContext = _viewModel.Timeline;
        _dockController = InitializeWorkflowDocking(
            dockLayoutSettings ??
            EditorDockLayoutSettingsStore.CreateDefault());
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ApplyWorkspaceSurfaceLayout();
        ApplyWorkflowDockLayout();
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
        _dockController.SaveCurrentLayout();
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
                StringComparison.Ordinal) ||
            // Playback and Fpp share IsPlaybackWorkspace, so toggling the FPP
            // camera never raises it. The split pane keys on IsFppWorkspace.
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsFppWorkspace),
                StringComparison.Ordinal);
        bool viewportLayoutChanged = string.Equals(
            args.PropertyName,
            nameof(MainWindowViewModel.IsSourceViewportVisible),
            StringComparison.Ordinal);
        bool dockLayoutChanged = workspaceSurfaceChanged ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.ActiveWorkspace),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsRetailAnimationBrowserVisible),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsAnimationDiagnosticsDrawerVisible),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsCustomModelAuthoringSurfaceVisible),
                StringComparison.Ordinal) ||
            string.Equals(
                args.PropertyName,
                nameof(MainWindowViewModel.IsFppWorkspace),
                StringComparison.Ordinal);
        if (!workspaceSurfaceChanged &&
            !viewportLayoutChanged &&
            !dockLayoutChanged)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => ApplyShellLayout(
                    workspaceSurfaceChanged,
                    viewportLayoutChanged,
                    dockLayoutChanged));
            return;
        }

        ApplyShellLayout(
            workspaceSurfaceChanged,
            viewportLayoutChanged,
            dockLayoutChanged);
    }

    private void ApplyShellLayout(
        bool workspaceSurfaceChanged,
        bool viewportLayoutChanged,
        bool dockLayoutChanged)
    {
        if (workspaceSurfaceChanged)
        {
            ApplyWorkspaceSurfaceLayout();
        }

        if (workspaceSurfaceChanged ||
            viewportLayoutChanged ||
            dockLayoutChanged)
        {
            ApplyWorkflowDockLayout();
        }
    }

    private void ApplyWorkspaceSurfaceLayout()
    {
        // The docking manager is now the one stable workspace surface. Every
        // HwndHost viewport is content of an active dock pane; hiding,
        // switching, or floating that pane removes and rebuilds its child HWND.
        EditorRootGrid.Children.Remove(ModelsWorkspaceSurface);
        if (!EditorRootGrid.Children.Contains(AnimationWorkspaceSurface))
        {
            Grid.SetRow(AnimationWorkspaceSurface, 2);
            EditorRootGrid.Children.Add(AnimationWorkspaceSurface);
        }

        AnimationWorkspaceSurface.Visibility = Visibility.Visible;

        EditorRootGrid.InvalidateMeasure();
        EditorRootGrid.InvalidateArrange();
    }

    private WorkflowDockController InitializeWorkflowDocking(
        EditorDockLayoutSettingsStore settings)
    {
        WorkflowDockManager.Theme = new ExpressionDarkTheme();
        WorkflowDockManager.DataContext = _viewModel;

        var panes = new List<EditorDockPaneDefinition>
        {
            Pane(EditorDockWorkflow.Models, "models.project", "Project models", Detach(ModelsProjectLibraryPane), 240, 220),
            Pane(EditorDockWorkflow.Models, "models.browser", "Base-game model browser", Detach(ModelsRetailBrowserPane), 240, 220),
            Pane(EditorDockWorkflow.Models, "models.preview", "Model preview", Detach(ModelsPreviewPane), 320, 240),

            Pane(EditorDockWorkflow.Animations, "animations.actions", "Animation actions", Detach(AnimationsActionsPane), 300, 80),
            Pane(EditorDockWorkflow.Animations, "animations.preview", "Source preview", Detach(AnimationsSourcePreviewPane), 320, 220, _viewModel.SourceViewport),
            Pane(EditorDockWorkflow.Animations, "animations.timeline", "Source timeline", Detach(AnimationsSourceTimelinePane), 340, 170, _viewModel.Timeline),
            Pane(EditorDockWorkflow.Animations, "animations.details", "Animation details", Detach(AnimationsDetailsPane), 240, 180),
            Pane(EditorDockWorkflow.Animations, "animations.browser", "Base-game animations", Detach(RetailAnimationBrowserPane), 260, 220),
            Pane(EditorDockWorkflow.Animations, "animations.library", "Animation library", Detach(AnimationsLibraryPane), 360, 220),

            Pane(EditorDockWorkflow.Playback, "playback.context", "Playback context", Detach(PlaybackContextPane), 300, 80),
            Pane(EditorDockWorkflow.Playback, "playback.fpp-camera", "FPP camera", Detach(PlaybackFppViewportPane), 280, 220, _viewModel.SourceViewport),
            Pane(EditorDockWorkflow.Playback, "playback.target-camera", "DL1 target camera", Detach(PlaybackTargetViewportPane), 320, 240, _viewModel.TargetViewport),
            Pane(EditorDockWorkflow.Playback, "playback.timeline", "Timeline / curves", Detach(PlaybackTimelinePane), 340, 180, _viewModel.Timeline),

            Pane(EditorDockWorkflow.RetargetEdit, "retarget.context", "Animation context", Detach(AnimationContextStrip), 320, 80),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.source-camera", "Source camera", Detach(SourceViewportPane), 280, 220, _viewModel.SourceViewport),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.target-camera", "DL1 target camera", Detach(RetargetTargetViewportPane), 320, 240),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.assets", "Assets", DetachTabContent(ExplorerAssetsTab), 240, 220),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.animation-library", "Animation library", DetachTabContent(AnimationLibraryTab), 260, 220),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.skeleton", "Skeleton", DetachTabContent(ExplorerSkeletonTab), 240, 220),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.mapping", "Mapping", DetachTabContent(RetargetMappingTab), 320, 260),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.edit", "Edit", DetachTabContent(RetargetEditTab), 320, 260),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.attachments", "Attachments", DetachTabContent(RetargetAttachmentsTab), 320, 260),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.ik", "IK", DetachTabContent(RetargetIkTab), 320, 240),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.facial", "Facial", DetachTabContent(RetargetFacialTab), 340, 280),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.fpp-camera", "FPP / camera", DetachTabContent(RetargetFppCameraTab), 340, 280),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.movie-camera", "Movie camera", DetachTabContent(RetargetMovieCameraTab), 340, 280),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.animations", "Animations", DetachTabContent(RetargetAnimationsTab), 340, 180),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.timeline", "Timeline / curves", DetachTabContent(RetargetTimelineTab), 340, 180, _viewModel.Timeline),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.jobs", "Jobs", DetachTabContent(JobsTab), 320, 180),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.diagnostics", "Diagnostics", DetachTabContent(DiagnosticsTab), 320, 180),
            Pane(EditorDockWorkflow.RetargetEdit, "retarget.fidelity", "Fidelity", DetachTabContent(FidelityTab), 320, 180),

            Pane(EditorDockWorkflow.Export, "export.files", "Files", DetachTabContent(ExportFilesTab), 420, 300),
            Pane(EditorDockWorkflow.Export, "export.developer-tools", "Developer Tools", DetachTabContent(ExportDeveloperToolsTab), 420, 300),
        };

        foreach (ModelsWorkspaceDockContent content in
                 ModelsWorkspaceSurface.DetachDockContents())
        {
            panes.Add(Pane(
                EditorDockWorkflow.Models,
                content.Id,
                content.Title,
                content.Content,
                260,
                220,
                _viewModel.Models));
        }

        Detach(ModelsWorkspaceSurface);
        Detach(DiagnosticsDrawerPane);
        return new WorkflowDockController(
            WorkflowDockManager,
            settings,
            panes);
    }

    private void ApplyWorkflowDockLayout()
    {
        EditorDockWorkflow workflow = ResolveDockWorkflow(
            _viewModel.ActiveWorkspace);
        _dockController.SwitchWorkflow(workflow);

        if (workflow == EditorDockWorkflow.Models)
        {
            bool authoring =
                _viewModel.IsCustomModelAuthoringSurfaceVisible;
            foreach (string id in NormalModelPaneIds)
            {
                _dockController.SetPaneVisible(id, !authoring);
            }

            foreach (string id in AuthoringModelPaneIds)
            {
                _dockController.SetPaneVisible(id, authoring);
            }
        }

        if (workflow == EditorDockWorkflow.Animations)
        {
            _dockController.SetPaneVisible(
                "animations.browser",
                _viewModel.IsRetailAnimationBrowserVisible);
        }

        if (workflow == EditorDockWorkflow.Playback)
        {
            _dockController.SetPaneVisible(
                "playback.fpp-camera",
                _viewModel.IsFppWorkspace);
        }

        if (workflow == EditorDockWorkflow.RetargetEdit)
        {
            bool diagnosticsVisible =
                _viewModel.IsAnimationDiagnosticsDrawerVisible;
            _dockController.SetPaneVisible(
                "retarget.jobs",
                diagnosticsVisible);
            _dockController.SetPaneVisible(
                "retarget.diagnostics",
                diagnosticsVisible);
            _dockController.SetPaneVisible(
                "retarget.fidelity",
                diagnosticsVisible);
        }

        WorkflowDockManager.InvalidateMeasure();
        WorkflowDockManager.InvalidateArrange();
    }

    private static EditorDockWorkflow ResolveDockWorkflow(
        EditorWorkspaceMode workspace) =>
        workspace switch
        {
            EditorWorkspaceMode.Models or
            EditorWorkspaceMode.Browse => EditorDockWorkflow.Models,
            EditorWorkspaceMode.Animations => EditorDockWorkflow.Animations,
            EditorWorkspaceMode.Animate or
            EditorWorkspaceMode.Fpp => EditorDockWorkflow.Playback,
            EditorWorkspaceMode.RetargetEdit or
            EditorWorkspaceMode.Face => EditorDockWorkflow.RetargetEdit,
            EditorWorkspaceMode.Export => EditorDockWorkflow.Export,
            _ => EditorDockWorkflow.Models,
        };

    private EditorDockPaneDefinition Pane(
        EditorDockWorkflow workflow,
        string id,
        string title,
        FrameworkElement content,
        double minimumWidth,
        double minimumHeight,
        object? dataContext = null)
    {
        content.DataContext = dataContext ?? _viewModel;

        // AvalonDock's Expression theme uses a warm gray content surface.
        // Pane content is intentionally transparent in several workflows, so
        // host it on the editor's own neutral background instead of allowing
        // the third-party theme color to leak through.
        var host = new Border
        {
            Child = content,
        };
        host.SetResourceReference(
            Border.BackgroundProperty,
            "PanelBackgroundBrush");

        return new EditorDockPaneDefinition(
            workflow,
            id,
            title,
            host,
            minimumWidth,
            minimumHeight);
    }

    private static FrameworkElement Detach(
        FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case ContentControl contentControl
                when ReferenceEquals(contentControl.Content, element):
                contentControl.Content = null;
                break;
            case Decorator decorator
                when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
        }

        element.ClearValue(Grid.RowProperty);
        element.ClearValue(Grid.ColumnProperty);
        element.ClearValue(Grid.RowSpanProperty);
        element.ClearValue(Grid.ColumnSpanProperty);
        element.ClearValue(Panel.ZIndexProperty);
        element.Visibility = Visibility.Visible;
        return element;
    }

    private static FrameworkElement DetachTabContent(TabItem tab)
    {
        if (tab.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException(
                $"The '{tab.Header}' editor tab has no dockable content.");
        }

        tab.Content = null;
        content.Visibility = Visibility.Visible;
        return content;
    }

    private void OnDockPanesMenuOpened(
        object sender,
        RoutedEventArgs args)
    {
        DockPanesMenu.Items.Clear();
        foreach (EditorDockPaneState pane in
                 _dockController.CurrentPaneStates)
        {
            var item = new MenuItem
            {
                Header = pane.Title,
                IsCheckable = true,
                IsChecked = pane.IsVisible,
                Tag = pane.Id,
            };
            item.Click += OnDockPaneMenuItemClick;
            DockPanesMenu.Items.Add(item);
        }
    }

    private void OnDockPaneMenuItemClick(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not MenuItem item ||
            item.Tag is not string paneId)
        {
            return;
        }

        _dockController.SetPaneVisible(
            paneId,
            item.IsChecked);
    }

    private void OnResetDockLayoutClick(
        object sender,
        RoutedEventArgs args)
    {
        _dockController.ResetCurrentLayout();
        ApplyWorkflowDockLayout();
    }

    internal bool FloatTargetViewportForSmoke() =>
        _dockController.FloatPane(
            "retarget.target-camera",
            Left + 80.0,
            Top + 80.0);

    internal bool DockTargetViewportForSmoke() =>
        _dockController.DockPane("retarget.target-camera");

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

    /// <summary>
    /// Selects the row under the pointer before its context menu opens.
    /// </summary>
    /// <remarks>
    /// Without this, right-clicking an unselected row runs every command
    /// against whatever was selected before - which for Remove is destructive.
    /// </remarks>
    private void OnListBoxPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs args) =>
        OnAssetExplorerPreviewMouseRightButtonDown(sender, args);

    /// <summary>
    /// The DataGrid equivalent; the ListBox handler cannot serve it because
    /// the container type differs.
    /// </summary>
    private void OnDataGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        if (sender is not DataGrid grid ||
            ItemsControl.ContainerFromElement(
                grid,
                args.OriginalSource as DependencyObject) is not
            DataGridRow row)
        {
            return;
        }

        grid.SelectedItem = row.Item;
        row.Focus();
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
        _dockController.ResetCurrentLayout();
        ApplyWorkflowDockLayout();
    }
}
