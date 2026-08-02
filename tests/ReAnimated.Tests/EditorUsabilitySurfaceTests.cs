using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using ReAnimated.App.ViewModels;

namespace ReAnimated.Tests;

public sealed class EditorUsabilitySurfaceTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MenuPopupUsesExplicitDarkReadableTemplate()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "App.xaml"));
        XElement style = Assert.Single(
            document.Descendants(
                Presentation + "Style"),
            static element =>
                string.Equals(
                    (string?)element.Attribute(
                        "TargetType"),
                    "{x:Type MenuItem}",
                    StringComparison.Ordinal));
        XElement popupBorder = Assert.Single(
            style.Descendants(
                    Presentation + "Popup")
                .Elements(
                    Presentation + "Border"));

        Assert.Equal(
            "{StaticResource PanelBackgroundBrush}",
            (string?)popupBorder.Attribute(
                "Background"));
        Assert.Contains(
            style.Descendants(
                Presentation + "ControlTemplate.Triggers"),
            static _ => true);
        Assert.Contains(
            document.Descendants(
                Presentation + "SolidColorBrush"),
            static element =>
                string.Equals(
                    (string?)element.Attribute(
                        Xaml + "Key"),
                    "{x:Static SystemColors.MenuTextBrushKey}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute(
                        "Color"),
                    "#E7ECF3",
                    StringComparison.Ordinal));

        XElement contextMenuStyle = Assert.Single(
            document.Descendants(Presentation + "Style"),
            static element => string.Equals(
                (string?)element.Attribute("TargetType"),
                "{x:Type ContextMenu}",
                StringComparison.Ordinal));
        Assert.Contains(
            contextMenuStyle.Descendants(Presentation + "Border"),
            static border => string.Equals(
                (string?)border.Attribute("Background"),
                "{TemplateBinding Background}",
                StringComparison.Ordinal));
    }

    [Fact]
    public void InspectorAndToolbarExposeReadableNamedControls()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml"));
        Assert.DoesNotContain(
            document.Descendants(Presentation + "ColumnDefinition"),
            static element => string.Equals(
                (string?)element.Attribute("Width"),
                "560",
                StringComparison.Ordinal));
        XElement inspector = Assert.Single(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute("Visibility"),
                "{Binding IsInspectorPanelVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
                StringComparison.Ordinal));
        Assert.Equal("4", (string?)inspector.Attribute("Grid.Column"));

        XElement frameAttachment = Assert.Single(
            document.Descendants(
                Presentation + "Button"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding FrameAttachmentCommand}",
                    StringComparison.Ordinal));
        Assert.Equal(
            "Frame attachment",
            (string?)frameAttachment.Attribute(
                "Content"));

        XElement fidelityItems = Assert.Single(
            document.Descendants(
                Presentation + "ItemsControl"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("ItemsSource"),
                    "{Binding FidelityBadges}",
                    StringComparison.Ordinal));
        XElement fidelityTab = Assert.IsType<XElement>(
            fidelityItems.Ancestors(
                    Presentation + "TabItem")
                .FirstOrDefault());
        Assert.Equal(
            "Fidelity",
            (string?)fidelityTab.Attribute("Header"));

        Assert.Contains(
            document.Descendants(
                Presentation + "TextBlock"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "Fidelity:",
                    StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding ShowFidelityDetailsCommand}",
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void GuidedShellWrapsAtLaptopWidthAndUsesExplicitAssetActions()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml"));
        XElement window = document.Root!;
        Assert.True(
            double.Parse(
                (string?)window.Attribute("MinHeight") ?? "0",
                System.Globalization.CultureInfo.InvariantCulture) <= 720);
        Assert.Contains(
            document.Descendants(Presentation + "WrapPanel"),
            static element => element.Ancestors(
                    Presentation + "Border")
                .Any(border => string.Equals(
                    (string?)border.Attribute("Grid.Row"),
                    "0",
                    StringComparison.Ordinal)));

        string[] labels =
        [
            "Preview now",
            "Use as Source",
            "Use as Target",
            "Edit bones",
            "Accept proposal & play",
        ];
        foreach (string label in labels)
        {
            Assert.Contains(
                document.Descendants(Presentation + "Button"),
                element => string.Equals(
                    (string?)element.Attribute("Content"),
                    label,
                    StringComparison.Ordinal));
        }

        Assert.Contains(
            document.Descendants(Presentation + "ToggleButton"),
            static element => string.Equals(
                (string?)element.Attribute("Content"),
                "Retarget / Edit",
                StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("IsChecked"),
                    "{Binding IsRetargetWorkspace, Mode=OneWay}",
                    StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "ToggleButton"),
            static element => string.Equals(
                    (string?)element.Attribute("Content"),
                    "Animate",
                    StringComparison.Ordinal) &&
                element.Attribute("IsEnabled") is null &&
                string.Equals(
                    (string?)element.Attribute("ToolTip"),
                    "{Binding AnimateWorkspaceHint}",
                    StringComparison.Ordinal));

        string[] visibleRigControls =
        [
            "Helpers",
            "Camera helpers",
            "Prop helpers",
        ];
        foreach (string label in visibleRigControls)
        {
            Assert.Contains(
                document.Descendants(Presentation + "ToggleButton"),
                element => string.Equals(
                    (string?)element.Attribute("Content"),
                    label,
                    StringComparison.Ordinal));
        }

        Assert.Contains(
            document.Descendants(Presentation + "ComboBox"),
            static element => string.Equals(
                (string?)element.Attribute("ItemsSource"),
                "{Binding RootBoneCandidates}",
                StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("SelectedItem"),
                    "{Binding SelectedRootBoneName}",
                    StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void TimelineExposesChannelFilteringSelectionAndBoundedZoom()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "Views",
                "TimelinePanel.xaml"));

        Assert.Contains(
            document.Descendants(Presentation + "TextBox"),
            static element => string.Equals(
                (string?)element.Attribute("Text"),
                "{Binding TrackSearchText, UpdateSourceTrigger=PropertyChanged}",
                StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "ListBox"),
            static element => string.Equals(
                (string?)element.Attribute("ItemsSource"),
                "{Binding VisibleTracks}",
                StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("SelectedItem"),
                    "{Binding SelectedTrack}",
                    StringComparison.Ordinal));
        string[] commands =
        [
            "{Binding FitTimelineCommand}",
            "{Binding ZoomOutCommand}",
            "{Binding ZoomInCommand}",
        ];
        foreach (string command in commands)
        {
            Assert.Contains(
                document.Descendants(Presentation + "Button"),
                element => string.Equals(
                    (string?)element.Attribute("Command"),
                    command,
                    StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ExplorerAndLibraryExposeExplicitAnimationPlayback()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml"));
        XElement explorer = Assert.Single(
            document.Descendants(Presentation + "ListBox"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "AssetExplorerList",
                StringComparison.Ordinal));
        Assert.Equal(
            "OnAssetExplorerDoubleClick",
            (string?)explorer.Attribute("MouseDoubleClick"));
        Assert.Equal(
            "OnAssetExplorerPreviewMouseRightButtonDown",
            (string?)explorer.Attribute(
                "PreviewMouseRightButtonDown"));

        XElement exportToFbx = Assert.Single(
            explorer.Descendants(Presentation + "MenuItem"),
            static element => string.Equals(
                (string?)element.Attribute("Header"),
                "Export to FBX…",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding ExportSelectedBrowserMeshToFbxCommand}",
            (string?)exportToFbx.Attribute("Command"));
        string[] contextCommands =
        [
            "{Binding PreviewSelectedAssetCommand}",
            "{Binding UseSelectedAssetAsSourceCommand}",
            "{Binding UseSelectedAssetAsTargetCommand}",
            "{Binding PlaySelectedExplorerAnimationCommand}",
        ];
        foreach (string command in contextCommands)
        {
            Assert.Contains(
                explorer.Descendants(Presentation + "MenuItem"),
                element => string.Equals(
                    (string?)element.Attribute("Command"),
                    command,
                    StringComparison.Ordinal));
        }

        XElement play = Assert.Single(
            document.Descendants(Presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding PlaySelectedExplorerAnimationCommand}",
                StringComparison.Ordinal));
        Assert.Equal("Play Animation", (string?)play.Attribute("Content"));

        XElement animationTab = Assert.Single(
            document.Descendants(Presentation + "TabItem"),
            static element => string.Equals(
                (string?)element.Attribute("Header"),
                "Animations",
                StringComparison.Ordinal));
        XElement library = Assert.Single(
            animationTab.Descendants(Presentation + "ListBox"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "AnimationLibraryList",
                StringComparison.Ordinal));
        Assert.Equal(
            "OnAnimationLibraryDoubleClick",
            (string?)library.Attribute("MouseDoubleClick"));
        Assert.Equal(
            "{Binding AnimationLibrary}",
            (string?)library.Attribute("ItemsSource"));

        string[] explicitAssetCommands =
        [
            "{Binding PreviewSelectedAssetCommand}",
            "{Binding UseSelectedAssetAsSourceCommand}",
            "{Binding UseSelectedAssetAsTargetCommand}",
        ];
        foreach (string command in explicitAssetCommands)
        {
            Assert.Contains(
                document.Descendants(Presentation + "Button"),
                element => string.Equals(
                    (string?)element.Attribute("Command"),
                    command,
                    StringComparison.Ordinal));
        }

        Assert.DoesNotContain(
            document.Descendants(Presentation + "ToggleButton"),
            static element => string.Equals(
                (string?)element.Attribute("Content"),
                "Compare",
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ViewportContextAndCatalogStatusUseNonOverlappingClearLayout()
    {
        XDocument document = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml"));
        XElement contextStrip = Assert.Single(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "AnimationContextStrip",
                StringComparison.Ordinal));
        Assert.Equal("0", (string?)contextStrip.Attribute("Grid.Row"));
        Assert.Null(contextStrip.Attribute("Panel.ZIndex"));
        XElement layoutGrid = Assert.IsType<XElement>(
            contextStrip.Parent);
        XElement[] viewportPanes = layoutGrid
            .Elements()
            .Where(static element => string.Equals(
                element.Name.LocalName,
                "ViewportPane",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, viewportPanes.Length);
        Assert.All(
            viewportPanes,
            static pane => Assert.Equal(
                "1",
                (string?)pane.Attribute("Grid.Row")));
        Assert.Contains(
            viewportPanes,
            static pane => string.Equals(
                (string?)pane.Attribute(Xaml + "Name"),
                "SourceViewportPane",
                StringComparison.Ordinal));

        XElement sourceColumn = Assert.Single(
            layoutGrid
                .Element(Presentation + "Grid.ColumnDefinitions")!
                .Elements(Presentation + "ColumnDefinition"),
            static column => string.Equals(
                (string?)column.Attribute(Xaml + "Name"),
                "SourceViewportColumn",
                StringComparison.Ordinal));
        Assert.Equal("*", (string?)sourceColumn.Attribute("Width"));
        Assert.Empty(sourceColumn.Descendants(Presentation + "DataTrigger"));
        XElement splitterColumn = Assert.Single(
            layoutGrid
                .Element(Presentation + "Grid.ColumnDefinitions")!
                .Elements(Presentation + "ColumnDefinition"),
            static column => string.Equals(
                (string?)column.Attribute(Xaml + "Name"),
                "ViewportSplitterColumn",
                StringComparison.Ordinal));
        Assert.Equal("6", (string?)splitterColumn.Attribute("Width"));

        string windowCode = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml.cs"));
        Assert.Contains(
            "_viewModel.PropertyChanged += OnViewModelPropertyChanged;",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(MainWindowViewModel.IsSourceViewportVisible)",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "SourceViewportColumn.MaxWidth = 0.0;",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "SourceViewportColumn.Width = new GridLength(0.0);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewportSplitterColumn.Width = new GridLength(0.0);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewportGrid.Children.Remove(SourceViewportPane)",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewportGrid.Children.Add(SourceViewportPane);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.PropertyChanged -= OnViewModelPropertyChanged;",
            windowCode,
            StringComparison.Ordinal);

        XElement catalogButton = Assert.Single(
            document.Descendants(Presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding AssetBrowser.IndexGameCommand}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AssetBrowser.CatalogActionLabel}",
            (string?)catalogButton.Attribute("Content"));
        Assert.DoesNotContain(
            document.Root!.DescendantsAndSelf()
                .Attributes()
                .Select(static attribute => attribute.Value),
            static value => value.Contains(
                "Index the Dying Light",
                StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute("Visibility"),
                "{Binding IsRetargetSetupVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
                StringComparison.Ordinal));

        string applicationStartup = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "App.xaml.cs"));
        Assert.Contains(
            "_ = InitializeAssetCatalogAsync(viewModel);",
            applicationStartup,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_startupSmoke is null)",
            applicationStartup,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void TimelineUsesAnApplicationThemeResourceForItsPanelBackground()
    {
        string timelineXaml = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "Views",
                "TimelinePanel.xaml"));
        string applicationXaml = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "App.xaml"));

        Assert.DoesNotContain(
            "{StaticResource PanelBrush}",
            timelineXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "{StaticResource PanelBackgroundBrush}",
            timelineXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"PanelBackgroundBrush\"",
            applicationXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void AnimationLibraryTemplateMaterializesReadOnlyMetadata()
    {
        RunOnStaThread(() =>
        {
            XDocument document = XDocument.Load(
                FindRepositoryFile(
                    "src",
                    "ReAnimated.App",
                    "MainWindow.xaml"));
            XElement library = Assert.Single(
                document.Descendants(Presentation + "ListBox"),
                static element => string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    "AnimationLibraryList",
                    StringComparison.Ordinal));
            XElement sourceTemplate = Assert.Single(
                library.Descendants(Presentation + "DataTemplate"));
            XElement templateXaml = new(sourceTemplate);
            templateXaml.SetAttributeValue(
                XNamespace.Xmlns + "x",
                Xaml.NamespaceName);
            foreach (XAttribute attribute in templateXaml
                         .DescendantsAndSelf()
                         .Attributes()
                         .Where(static attribute =>
                             attribute.Value.StartsWith(
                                 "{StaticResource ",
                                 StringComparison.Ordinal)))
            {
                attribute.Value = "#FFFFFFFF";
            }

            XAttribute[] boundRunText = templateXaml
                .Descendants(Presentation + "Run")
                .Select(static run => run.Attribute("Text"))
                .Where(static attribute =>
                    attribute?.Value.StartsWith(
                        "{Binding",
                        StringComparison.Ordinal) == true)
                .Cast<XAttribute>()
                .ToArray();
            Assert.NotEmpty(boundRunText);
            Assert.All(
                boundRunText,
                static attribute => Assert.Contains(
                    "Mode=OneWay",
                    attribute.Value,
                    StringComparison.Ordinal));

            var template = Assert.IsType<DataTemplate>(
                XamlReader.Parse(
                    templateXaml.ToString(
                        SaveOptions.DisableFormatting)));
            var row = Assert.IsAssignableFrom<FrameworkElement>(
                template.LoadContent());
            row.DataContext = new AnimationLibraryItemViewModel(
                Guid.NewGuid(),
                "Runtime binding control",
                "retail.anm2",
                "zombie_prime",
                "zombie_prime",
                "Body + facial",
                "30/1 FPS",
                "1.0 seconds",
                "Same rig / direct",
                string.Empty,
                true);
            row.Measure(new Size(640, 480));
            row.Arrange(new Rect(0, 0, 640, 480));
            row.UpdateLayout();
        });
    }

    private static void RunOnStaThread(Action action)
    {
        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                capturedException =
                    ExceptionDispatchInfo.Capture(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        capturedException?.Throw();
    }

    private static string FindRepositoryFile(
        params string[] relativeSegments)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{Path.Combine(relativeSegments)}' " +
            $"above '{AppContext.BaseDirectory}'.");
    }
}
