using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using ReAnimated.App.Controls;
using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class EditorDockLayoutTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void DeferredDockUpdatesCoalesceAndDoNotRunAfterOwnerStops()
    {
        RunOnStaThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int applies = 0;
            var scheduler = new DockLayoutUpdateScheduler(dispatcher, () => applies++);
            scheduler.Request();
            scheduler.Request();
            scheduler.Request();
            Assert.Equal(0, applies);
            DrainIdle(dispatcher);
            Assert.Equal(1, applies);

            scheduler.Request();
            scheduler.Stop();
            DrainIdle(dispatcher);
            Assert.Equal(1, applies);
        });
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void PlaybackToModelsDockTransitionRunsAheadOfContinuousRenderWork()
    {
        RunOnStaThread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            string root = CreateTemporaryDirectory();
            bool renderPumpRunning = true;
            int renderCallbacks = 0;
            try
            {
                var manager = new DockingManager();
                var controller = new WorkflowDockController(
                    manager,
                    new EditorDockLayoutSettingsStore(root),
                    [
                        .. CreatePlaybackPanes(
                            new System.Windows.Controls.Button
                            {
                                Content = "Choose animation",
                                Height = 32,
                            }),
                        .. CreateModelPanes(),
                    ]);
                controller.SwitchWorkflow(EditorDockWorkflow.Playback);

                Action? renderPump = null;
                renderPump = () =>
                {
                    renderCallbacks++;
                    if (renderPumpRunning)
                    {
                        _ = dispatcher.BeginInvoke(
                            DispatcherPriority.Render,
                            renderPump);
                    }
                };
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    renderPump);

                int applied = 0;
                var scheduler = new DockLayoutUpdateScheduler(
                    dispatcher,
                    () =>
                    {
                        applied++;
                        controller.SwitchWorkflow(EditorDockWorkflow.Models);
                        controller.SetPaneVisible(
                            "models.authoring.workspace",
                            visible: false);
                    });
                scheduler.Request();

                var frame = new DispatcherFrame();
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.Normal,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                Assert.Equal(1, applied);
                Assert.Equal(EditorDockWorkflow.Models, controller.ActiveWorkflow);
                Assert.Equal(
                    [
                        "models.authoring.workspace",
                        "models.browser",
                        "models.preview",
                        "models.project",
                    ],
                    controller.CurrentPaneStates
                        .Select(static pane => pane.Id)
                        .OrderBy(static id => id, StringComparer.Ordinal)
                        .ToArray());
                Assert.DoesNotContain(
                    controller.CurrentPaneStates,
                    static pane => pane.Id.StartsWith(
                        "playback.",
                        StringComparison.Ordinal));
                Assert.True(controller.IsPaneVisible("models.project"));
                Assert.True(controller.IsPaneVisible("models.browser"));
                Assert.True(controller.IsPaneVisible("models.preview"));
                Assert.Equal(0, renderCallbacks);

                scheduler.Stop();
            }
            finally
            {
                renderPumpRunning = false;
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void LayoutStoreKeepsEachWorkflowMachineLocalAndVersioned()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var store = new EditorDockLayoutSettingsStore(root);
            store.Save(EditorDockWorkflow.Models, "<models />");
            store.Save(EditorDockWorkflow.RetargetEdit, "<retarget />");

            Assert.True(store.TryLoad(
                EditorDockWorkflow.Models,
                out string? models));
            Assert.True(store.TryLoad(
                EditorDockWorkflow.RetargetEdit,
                out string? retarget));
            Assert.Equal("<models />", models);
            Assert.Equal("<retarget />", retarget);
            Assert.EndsWith(
                "models.v1.xml",
                store.GetLayoutPath(EditorDockWorkflow.Models),
                StringComparison.Ordinal);
            Assert.EndsWith(
                "retarget-edit.v1.xml",
                store.GetLayoutPath(EditorDockWorkflow.RetargetEdit),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void LayoutStoreRejectsXmlDocumentTypes()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            var store = new EditorDockLayoutSettingsStore(root);
            Directory.CreateDirectory(root);
            File.WriteAllText(
                store.GetLayoutPath(EditorDockWorkflow.Models),
                "<!DOCTYPE layout><layout />");

            Assert.False(store.TryLoad(
                EditorDockWorkflow.Models,
                out string? layout));
            Assert.Null(layout);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void DockControllerRoundTripsHiddenPaneState()
    {
        RunOnStaThread(() =>
        {
            string root = CreateTemporaryDirectory();
            try
            {
                var store = new EditorDockLayoutSettingsStore(root);
                var firstManager = new DockingManager();
                var first = new WorkflowDockController(
                    firstManager,
                    store,
                    CreateModelPanes());
                first.SwitchWorkflow(EditorDockWorkflow.Models);
                first.SetPaneVisible("models.browser", visible: false);
                first.SaveCurrentLayout();

                var secondManager = new DockingManager();
                var second = new WorkflowDockController(
                    secondManager,
                    store,
                    CreateModelPanes());
                second.SwitchWorkflow(EditorDockWorkflow.Models);

                Assert.False(second.IsPaneVisible("models.browser"));
                Assert.True(second.IsPaneVisible("models.project"));
                Assert.Equal(
                    BrowserModelPaneIds.Length + AuthoringModelPaneIds.Length,
                    second.CurrentPaneStates.Count);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void DockControllerSwapsModelBrowserAndAuthoringPaneSets()
    {
        RunOnStaThread(() =>
        {
            string root = CreateTemporaryDirectory();
            try
            {
                var manager = new DockingManager();
                var controller = new WorkflowDockController(
                    manager,
                    new EditorDockLayoutSettingsStore(root),
                    CreateModelPanes());
                controller.SwitchWorkflow(EditorDockWorkflow.Models);

                SetModelPaneMode(
                    controller,
                    authoring: true);
                AssertPaneSetRooted(
                    manager,
                    AuthoringModelPaneIds,
                    expectedVisible: true);
                AssertPaneSetRooted(
                    manager,
                    BrowserModelPaneIds,
                    expectedVisible: false);

                SetModelPaneMode(
                    controller,
                    authoring: false);
                AssertPaneSetRooted(
                    manager,
                    BrowserModelPaneIds,
                    expectedVisible: true);
                AssertPaneSetRooted(
                    manager,
                    AuthoringModelPaneIds,
                    expectedVisible: false);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ModelsLayoutSavedWithOnlyAuthoringVisibleRestoresAUsableBrowser()
    {
        RunOnStaThread(() =>
        {
            string root = CreateTemporaryDirectory();
            try
            {
                var store = new EditorDockLayoutSettingsStore(root);
                var first = new WorkflowDockController(
                    new DockingManager(),
                    store,
                    CreateModelPanes());
                first.SwitchWorkflow(EditorDockWorkflow.Models);
                SetModelPaneMode(first, authoring: true);
                first.SetModelsAuthoringFocus(true);
                first.SaveCurrentLayout();

                var manager = new DockingManager();
                var reopened = new WorkflowDockController(
                    manager,
                    store,
                    CreateModelPanes());
                reopened.SwitchWorkflow(EditorDockWorkflow.Models);

                AssertPaneSetRooted(
                    manager,
                    BrowserModelPaneIds,
                    expectedVisible: true);
                AssertPaneSetRooted(
                    manager,
                    AuthoringModelPaneIds,
                    expectedVisible: false);
                Assert.True(store.TryLoad(EditorDockWorkflow.Models, out string? repaired));
                Assert.NotNull(repaired);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void AuthoringFocusExpandsTransientlyWithoutSavingTheFocusedSplit()
    {
        RunOnStaThread(() =>
        {
            string root = CreateTemporaryDirectory();
            try
            {
                var store = new EditorDockLayoutSettingsStore(root);
                var manager = new DockingManager();
                var controller = new WorkflowDockController(
                    manager,
                    store,
                    CreateModelPanes());
                controller.SwitchWorkflow(EditorDockWorkflow.Models);
                LayoutPanel split = manager.Layout.RootPanel;
                var browser = Assert.IsType<LayoutPanel>(split.Children[0]);
                var authoring = Assert.IsType<LayoutAnchorablePane>(split.Children[1]);
                SetModelPaneMode(controller, authoring: true);
                split = manager.Layout.RootPanel;
                browser = Assert.IsType<LayoutPanel>(split.Children[0]);
                authoring = Assert.IsType<LayoutAnchorablePane>(split.Children[1]);
                GridLength originalBrowser = browser.DockHeight;
                GridLength originalAuthoring = authoring.DockHeight;
                controller.SetModelsAuthoringFocus(true);
                Assert.True(browser.DockHeight.IsStar);
                Assert.InRange(browser.DockHeight.Value, 0.0, 0.02);
                Assert.True(authoring.DockHeight.IsStar);

                controller.SaveCurrentLayout();
                Assert.InRange(browser.DockHeight.Value, 0.0, 0.02);
                controller.SetModelsAuthoringFocus(false);
                Assert.Equal(originalBrowser, browser.DockHeight);
                Assert.Equal(originalAuthoring, authoring.DockHeight);

                var restoredManager = new DockingManager();
                var restored = new WorkflowDockController(
                    restoredManager,
                    store,
                    CreateModelPanes());
                restored.SwitchWorkflow(EditorDockWorkflow.Models);
                LayoutPanel restoredSplit = restoredManager.Layout.RootPanel;
                Assert.Equal(
                    originalBrowser,
                    Assert.IsType<LayoutPanel>(restoredSplit.Children[0]).DockHeight);
                Assert.Equal(
                    originalAuthoring,
                    Assert.IsType<LayoutAnchorablePane>(restoredSplit.Children[1]).DockHeight);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void ResponsiveFormGridStacksWhenDockedNarrow()
    {
        RunOnStaThread(() =>
        {
            var grid = new ResponsiveUniformGrid
            {
                PreferredColumns = 3,
                MinimumCellWidth = 100,
            };
            grid.Children.Add(new System.Windows.Controls.TextBox());
            grid.Children.Add(new System.Windows.Controls.TextBox());
            grid.Children.Add(new System.Windows.Controls.TextBox());

            grid.Measure(new Size(350, 300));
            Assert.Equal(3, grid.Columns);
            grid.Measure(new Size(180, 300));
            Assert.Equal(1, grid.Columns);
        });
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void PlaybackContextMinimumSurvivesUndersizedSavedLayoutWithoutResettingOtherSplits()
    {
        RunOnStaThread(() =>
        {
            string root = CreateTemporaryDirectory();
            Window? firstWindow = null;
            Window? restoredWindow = null;
            try
            {
                var store = new EditorDockLayoutSettingsStore(root);
                var firstManager = new DockingManager();
                var firstButton = new System.Windows.Controls.Button { Content = "Choose animation", Height = 32 };
                EditorDockPaneDefinition[] firstPanes = CreatePlaybackPanes(firstButton);
                var first = new WorkflowDockController(firstManager, store, firstPanes);
                first.SwitchWorkflow(EditorDockWorkflow.Playback);
                firstWindow = new Window { Width = 960, Height = 760, Content = firstManager, ShowInTaskbar = false };
                firstWindow.Show();
                DrainIdle(Dispatcher.CurrentDispatcher);
                firstManager.UpdateLayout();
                AssertPlaybackActionFits(firstManager, firstButton);

                LayoutAnchorablePane header = FindPlaybackPane(firstManager, "playback.context");
                LayoutAnchorablePane timeline = FindPlaybackPane(firstManager, "playback.timeline");
                timeline.DockHeight = new GridLength(195);
                header.DockMinHeight = 0;
                header.DockHeight = new GridLength(32);
                first.SaveCurrentLayout();
                firstWindow.Close();
                firstWindow = null;

                var restoredManager = new DockingManager();
                var restoredButton = new System.Windows.Controls.Button { Content = "Choose animation", Height = 32 };
                var restored = new WorkflowDockController(restoredManager, store, CreatePlaybackPanes(restoredButton));
                restored.SwitchWorkflow(EditorDockWorkflow.Playback);
                Assert.Equal(new GridLength(195), FindPlaybackPane(restoredManager, "playback.timeline").DockHeight);
                Assert.True(FindPlaybackPane(restoredManager, "playback.context").DockHeight.Value >= 180);
                restoredWindow = new Window { Width = 960, Height = 760, Content = restoredManager, ShowInTaskbar = false };
                restoredWindow.Show();
                DrainIdle(Dispatcher.CurrentDispatcher);
                restoredManager.UpdateLayout();

                AssertPlaybackActionFits(restoredManager, restoredButton);
            }
            finally
            {
                firstWindow?.Close();
                restoredWindow?.Close();
                Directory.Delete(root, recursive: true);
            }
        });
    }

    private static EditorDockPaneDefinition[] CreatePlaybackPanes(System.Windows.Controls.Button action)
    {
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "No animation selected", FontSize = 16, TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(action);
        return
        [
            new(EditorDockWorkflow.Playback, "playback.context", "Playback context",
                new System.Windows.Controls.Border { Child = content }, 300, 120),
            new(EditorDockWorkflow.Playback, "playback.fpp-camera", "FPP camera", new System.Windows.Controls.Border()),
            new(EditorDockWorkflow.Playback, "playback.target-camera", "Target camera", new System.Windows.Controls.Border()),
            new(EditorDockWorkflow.Playback, "playback.timeline", "Timeline", new System.Windows.Controls.Border()),
        ];
    }

    private static LayoutAnchorablePane FindPlaybackPane(DockingManager manager, string id) =>
        manager.Layout.Descendents().OfType<LayoutAnchorable>()
            .Single(item => item.ContentId == id).Parent as LayoutAnchorablePane
            ?? throw new InvalidOperationException("Playback pane not attached.");

    private static void AssertPlaybackActionFits(DockingManager manager, FrameworkElement action)
    {
        FrameworkElement? parent = System.Windows.Media.VisualTreeHelper.GetParent(action) as FrameworkElement;
        while (parent is not null && parent.GetType().Name != "LayoutAnchorablePaneControl")
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
        Assert.NotNull(parent);
        Rect actionBounds = action.TransformToAncestor(parent).TransformBounds(new Rect(action.RenderSize));
        Assert.True(action.ActualHeight > 0);
        Assert.InRange(actionBounds.Top, 0, parent.ActualHeight);
        Assert.InRange(actionBounds.Bottom, 0, parent.ActualHeight);
        Assert.True(parent.ActualHeight >= 120);
    }

    private static EditorDockPaneDefinition[] CreateModelPanes() =>
    [
        Pane("models.project"),
        Pane("models.browser"),
        Pane("models.preview"),
        Pane("models.authoring.workspace"),
    ];

    private static EditorDockPaneDefinition Pane(string id) =>
        new(
            EditorDockWorkflow.Models,
            id,
            id,
            new System.Windows.Controls.Border());

    private static void SetModelPaneMode(
        WorkflowDockController controller,
        bool authoring)
    {
        foreach (string id in BrowserModelPaneIds)
        {
            controller.SetPaneVisible(id, !authoring);
        }

        foreach (string id in AuthoringModelPaneIds)
        {
            controller.SetPaneVisible(id, authoring);
        }
    }

    private static void AssertPaneSetRooted(
        DockingManager manager,
        IEnumerable<string> paneIds,
        bool expectedVisible)
    {
        Dictionary<string, LayoutAnchorable> anchorables =
            manager.Layout.Descendents()
                .OfType<LayoutAnchorable>()
                .Where(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.ContentId))
                .ToDictionary(
                    candidate => candidate.ContentId!,
                    StringComparer.Ordinal);
        foreach (string paneId in paneIds)
        {
            Assert.True(
                anchorables.TryGetValue(
                    paneId,
                    out LayoutAnchorable? anchorable),
                $"Pane '{paneId}' left the active layout tree.");
            Assert.Equal(expectedVisible, anchorable.IsVisible);
            Assert.Same(manager.Layout, anchorable.Root);
        }
    }

    private static readonly string[] BrowserModelPaneIds =
    [
        "models.project",
        "models.browser",
        "models.preview",
    ];

    private static readonly string[] AuthoringModelPaneIds =
    [
        "models.authoring.workspace",
    ];

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "dlra-dock-layout-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RunOnStaThread(Action action) => WpfTestDispatcher.Run(action);

    private static void DrainIdle(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        _ = dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
