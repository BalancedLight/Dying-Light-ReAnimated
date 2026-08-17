using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;

namespace ReAnimated.App.Infrastructure;

public sealed record EditorDockPaneDefinition(
    EditorDockWorkflow Workflow,
    string Id,
    string Title,
    FrameworkElement Content,
    double MinimumWidth = 180.0,
    double MinimumHeight = 120.0);

public sealed record EditorDockPaneState(
    string Id,
    string Title,
    bool IsVisible);

/// <summary>
/// Owns one reusable docking surface and swaps a separately persisted layout
/// for each workflow. Pane content is singleton WPF content: switching or
/// floating a pane reparents it, which deliberately tears down and rebuilds
/// any HwndHost-backed renderer beneath it.
/// </summary>
public sealed class WorkflowDockController
{
    private readonly DockingManager _manager;
    private readonly EditorDockLayoutSettingsStore _settings;
    private readonly Dictionary<EditorDockWorkflow, Dictionary<string, EditorDockPaneDefinition>>
        _panes = [];
    private readonly Dictionary<string, LayoutAnchorable> _activeAnchorables =
        new(StringComparer.Ordinal);
    private EditorDockWorkflow? _activeWorkflow;

    public WorkflowDockController(
        DockingManager manager,
        EditorDockLayoutSettingsStore settings,
        IEnumerable<EditorDockPaneDefinition> panes)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(panes);

        foreach (EditorDockPaneDefinition pane in panes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pane.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(pane.Title);
            if (!_panes.TryGetValue(pane.Workflow, out Dictionary<string, EditorDockPaneDefinition>? workflowPanes))
            {
                workflowPanes = new Dictionary<string, EditorDockPaneDefinition>(
                    StringComparer.Ordinal);
                _panes.Add(pane.Workflow, workflowPanes);
            }

            if (!workflowPanes.TryAdd(pane.Id, pane))
            {
                throw new InvalidOperationException(
                    $"Duplicate editor dock pane id '{pane.Id}'.");
            }
        }
    }

    public EditorDockWorkflow? ActiveWorkflow => _activeWorkflow;

    public IReadOnlyList<EditorDockPaneState> CurrentPaneStates =>
        _activeWorkflow is not { } workflow ||
        !_panes.TryGetValue(workflow, out Dictionary<string, EditorDockPaneDefinition>? panes)
            ? []
            : panes.Values
                .OrderBy(static pane => pane.Title, StringComparer.OrdinalIgnoreCase)
                .Select(pane => new EditorDockPaneState(
                    pane.Id,
                    pane.Title,
                    _activeAnchorables.TryGetValue(pane.Id, out LayoutAnchorable? anchorable) &&
                    anchorable.IsVisible))
                .ToArray();

    public void SwitchWorkflow(EditorDockWorkflow workflow)
    {
        if (_activeWorkflow == workflow)
        {
            return;
        }

        SaveCurrentLayout();
        CloseFloatingWindowsAndDetachCurrentLayout();
        _activeWorkflow = workflow;

        if (!TryRestoreLayout(workflow))
        {
            ApplyDefaultLayout(workflow);
        }
    }

    public void ResetCurrentLayout()
    {
        if (_activeWorkflow is not { } workflow)
        {
            return;
        }

        CloseFloatingWindowsAndDetachCurrentLayout();
        ApplyDefaultLayout(workflow);
        SaveCurrentLayout();
    }

    public void SaveCurrentLayout()
    {
        if (_activeWorkflow is not { } workflow ||
            _manager.Layout?.RootPanel is null)
        {
            return;
        }

        try
        {
            using var writer = new StringWriter(
                System.Globalization.CultureInfo.InvariantCulture);
            var serializer = new XmlLayoutSerializer(_manager);
            serializer.Serialize(writer);
            _settings.Save(workflow, writer.ToString());
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            NotSupportedException)
        {
            // Layout persistence is convenience state. A failure must never
            // block project saves, renderer shutdown, or workspace switching.
        }
    }

    public void SetPaneVisible(string paneId, bool visible)
    {
        if (!_activeAnchorables.TryGetValue(
                paneId,
                out LayoutAnchorable? anchorable))
        {
            return;
        }

        if (visible)
        {
            anchorable.Show();
            anchorable.IsActive = true;
        }
        else if (anchorable.IsVisible)
        {
            anchorable.Hide();
        }
    }

    public bool IsPaneVisible(string paneId) =>
        _activeAnchorables.TryGetValue(
            paneId,
            out LayoutAnchorable? anchorable) &&
        anchorable.IsVisible;

    public bool FloatPane(
        string paneId,
        double? floatingLeft = null,
        double? floatingTop = null)
    {
        if (!_activeAnchorables.TryGetValue(
                paneId,
                out LayoutAnchorable? anchorable))
        {
            return false;
        }

        if (!anchorable.IsVisible)
        {
            anchorable.Show();
        }

        anchorable.IsSelected = true;
        anchorable.IsActive = true;
        if (floatingLeft is { } left && double.IsFinite(left))
        {
            anchorable.FloatingLeft = left;
        }

        if (floatingTop is { } top && double.IsFinite(top))
        {
            anchorable.FloatingTop = top;
        }

        anchorable.Float();
        anchorable.IsSelected = true;
        anchorable.IsActive = true;
        return anchorable.IsFloating;
    }

    public bool DockPane(string paneId)
    {
        if (!_activeAnchorables.TryGetValue(
                paneId,
                out LayoutAnchorable? anchorable))
        {
            return false;
        }

        anchorable.Dock();
        anchorable.IsSelected = true;
        anchorable.IsActive = true;
        return !anchorable.IsFloating;
    }

    private bool TryRestoreLayout(EditorDockWorkflow workflow)
    {
        if (!_settings.TryLoad(workflow, out string? serializedLayout) ||
            string.IsNullOrWhiteSpace(serializedLayout) ||
            !_panes.TryGetValue(workflow, out Dictionary<string, EditorDockPaneDefinition>? panes))
        {
            return false;
        }

        _activeAnchorables.Clear();
        bool rejected = false;
        try
        {
            var serializer = new XmlLayoutSerializer(_manager);
            serializer.LayoutSerializationCallback += (_, args) =>
            {
                string? contentId = args.Model.ContentId;
                if (string.IsNullOrWhiteSpace(contentId) ||
                    !panes.TryGetValue(contentId, out EditorDockPaneDefinition? pane))
                {
                    args.Cancel = true;
                    rejected = true;
                    return;
                }

                args.Content = pane.Content;
                if (args.Model is LayoutAnchorable anchorable)
                {
                    ConfigureAnchorable(anchorable, pane);
                    _activeAnchorables[pane.Id] = anchorable;
                }
            };
            using var reader = new StringReader(serializedLayout);
            serializer.Deserialize(reader);

            if (rejected ||
                _activeAnchorables.Count != panes.Count)
            {
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            FormatException or
            System.Xml.XmlException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
    }

    private void ApplyDefaultLayout(EditorDockWorkflow workflow)
    {
        if (!_panes.TryGetValue(
                workflow,
                out Dictionary<string, EditorDockPaneDefinition>? panes))
        {
            throw new InvalidOperationException(
                $"No dock panes are registered for {workflow}.");
        }

        _activeAnchorables.Clear();
        var root = new LayoutRoot();
        LayoutPanel panel = workflow switch
        {
            EditorDockWorkflow.Models => BuildModelsLayout(panes),
            EditorDockWorkflow.Animations => BuildAnimationsLayout(panes),
            EditorDockWorkflow.Playback => BuildPlaybackLayout(panes),
            EditorDockWorkflow.RetargetEdit => BuildRetargetLayout(panes),
            EditorDockWorkflow.Export => BuildExportLayout(panes),
            _ => throw new ArgumentOutOfRangeException(nameof(workflow)),
        };
        root.RootPanel = panel;
        _manager.Layout = root;
        if (workflow == EditorDockWorkflow.Models)
        {
            foreach (string id in AuthoringModelPaneIds)
            {
                SetPaneVisible(id, visible: false);
            }
        }
    }

    private LayoutPanel BuildModelsLayout(
        Dictionary<string, EditorDockPaneDefinition> panes)
    {
        LayoutAnchorablePane project = Pane(panes, "models.project");
        project.DockWidth = new GridLength(285.0);
        LayoutAnchorablePane browser = Pane(panes, "models.browser");
        browser.DockWidth = new GridLength(365.0);
        LayoutAnchorablePane preview = Pane(panes, "models.preview");
        LayoutPanel browserLayout = Split(
            Orientation.Horizontal,
            project,
            browser,
            preview);

        LayoutAnchorablePane authoringSettings = Pane(
            panes,
            "models.authoring.settings");
        authoringSettings.DockWidth = new GridLength(320.0);
        LayoutAnchorablePane authoringPreview = Pane(
            panes,
            "models.authoring.preview");
        LayoutAnchorablePane authoringTimeline = Pane(
            panes,
            "models.authoring.timeline");
        authoringTimeline.DockHeight = new GridLength(235.0);
        LayoutAnchorablePane authoringInspector = Pane(
            panes,
            "models.authoring.rig",
            "models.authoring.materials",
            "models.authoring.animations");
        authoringInspector.DockWidth = new GridLength(380.0);
        LayoutPanel authoringLayout = Split(
            Orientation.Horizontal,
            authoringSettings,
            Split(
                Orientation.Vertical,
                authoringPreview,
                authoringTimeline),
            authoringInspector);

        return Split(
            Orientation.Vertical,
            browserLayout,
            authoringLayout);
    }

    private LayoutPanel BuildAnimationsLayout(
        Dictionary<string, EditorDockPaneDefinition> panes)
    {
        LayoutAnchorablePane actions = Pane(panes, "animations.actions");
        actions.DockHeight = new GridLength(105.0);
        LayoutAnchorablePane preview = Pane(panes, "animations.preview");
        LayoutAnchorablePane details = Pane(panes, "animations.details");
        details.DockWidth = new GridLength(330.0);
        LayoutAnchorablePane browser = Pane(panes, "animations.browser");
        browser.DockWidth = new GridLength(350.0);
        LayoutAnchorablePane library = Pane(panes, "animations.library");

        LayoutPanel top = Split(
            Orientation.Horizontal,
            preview,
            details);
        LayoutPanel bottom = Split(
            Orientation.Horizontal,
            browser,
            library);
        return Split(
            Orientation.Vertical,
            actions,
            top,
            bottom);
    }

    private LayoutPanel BuildPlaybackLayout(
        Dictionary<string, EditorDockPaneDefinition> panes)
    {
        LayoutAnchorablePane header = Pane(panes, "playback.context");
        header.DockHeight = new GridLength(110.0);
        LayoutAnchorablePane fpp = Pane(panes, "playback.fpp-camera");
        LayoutAnchorablePane target = Pane(panes, "playback.target-camera");
        LayoutAnchorablePane timeline = Pane(panes, "playback.timeline");
        timeline.DockHeight = new GridLength(270.0);
        return Split(
            Orientation.Vertical,
            header,
            Split(Orientation.Horizontal, fpp, target),
            timeline);
    }

    private LayoutPanel BuildRetargetLayout(
        Dictionary<string, EditorDockPaneDefinition> panes)
    {
        LayoutAnchorablePane explorer = Pane(
            panes,
            "retarget.assets",
            "retarget.animation-library",
            "retarget.skeleton");
        explorer.DockWidth = new GridLength(285.0);

        LayoutAnchorablePane context = Pane(panes, "retarget.context");
        context.DockHeight = new GridLength(105.0);
        LayoutPanel viewports = Split(
            Orientation.Horizontal,
            Pane(panes, "retarget.source-camera"),
            Pane(panes, "retarget.target-camera"));
        LayoutAnchorablePane bottom = Pane(
            panes,
            "retarget.animations",
            "retarget.timeline",
            "retarget.jobs",
            "retarget.diagnostics",
            "retarget.fidelity");
        bottom.DockHeight = new GridLength(245.0);
        LayoutPanel center = Split(
            Orientation.Vertical,
            context,
            viewports,
            bottom);

        LayoutAnchorablePane inspector = Pane(
            panes,
            "retarget.mapping",
            "retarget.edit",
            "retarget.attachments",
            "retarget.ik",
            "retarget.facial",
            "retarget.fpp-camera",
            "retarget.movie-camera");
        inspector.DockWidth = new GridLength(430.0);
        return Split(
            Orientation.Horizontal,
            explorer,
            center,
            inspector);
    }

    private LayoutPanel BuildExportLayout(
        Dictionary<string, EditorDockPaneDefinition> panes) =>
        Split(
            Orientation.Horizontal,
            Pane(panes, "export.files", "export.developer-tools"));

    private LayoutAnchorablePane Pane(
        Dictionary<string, EditorDockPaneDefinition> panes,
        params string[] ids)
    {
        var pane = new LayoutAnchorablePane();
        foreach (string id in ids)
        {
            if (!panes.TryGetValue(id, out EditorDockPaneDefinition? definition))
            {
                throw new InvalidOperationException(
                    $"The default dock layout references missing pane '{id}'.");
            }

            var anchorable = new LayoutAnchorable();
            ConfigureAnchorable(anchorable, definition);
            bool selectInPane = pane.ChildrenCount == 0;
            pane.Children.Add(anchorable);
            if (selectInPane)
            {
                anchorable.IsSelected = true;
            }

            _activeAnchorables.Add(id, anchorable);
        }

        return pane;
    }

    private static LayoutPanel Split(
        Orientation orientation,
        params ILayoutPanelElement[] children)
    {
        var panel = new LayoutPanel
        {
            Orientation = orientation,
        };
        foreach (ILayoutPanelElement child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static void ConfigureAnchorable(
        LayoutAnchorable anchorable,
        EditorDockPaneDefinition pane)
    {
        anchorable.ContentId = pane.Id;
        anchorable.Title = pane.Title;
        anchorable.Content = pane.Content;
        anchorable.CanClose = false;
        anchorable.CanHide = true;
        anchorable.CanAutoHide = true;
        anchorable.CanDockAsTabbedDocument = true;
        anchorable.FloatingWidth = Math.Max(420.0, pane.MinimumWidth * 1.8);
        anchorable.FloatingHeight = Math.Max(320.0, pane.MinimumHeight * 1.8);
        anchorable.AutoHideMinWidth = pane.MinimumWidth;
        anchorable.AutoHideMinHeight = pane.MinimumHeight;
    }

    private void CloseFloatingWindowsAndDetachCurrentLayout()
    {
        _manager.Layout = new LayoutRoot
        {
            RootPanel = new LayoutPanel(),
        };
        _activeAnchorables.Clear();
    }

    private static readonly string[] AuthoringModelPaneIds =
    [
        "models.authoring.settings",
        "models.authoring.preview",
        "models.authoring.timeline",
        "models.authoring.rig",
        "models.authoring.materials",
        "models.authoring.animations",
    ];
}
