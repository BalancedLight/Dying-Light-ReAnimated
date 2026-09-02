using System.Runtime.ExceptionServices;
using System.Windows;
using AvalonDock;
using AvalonDock.Layout;
using ReAnimated.App.Controls;
using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class EditorDockLayoutTests
{
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

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                captured = ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        captured?.Throw();
    }
}
