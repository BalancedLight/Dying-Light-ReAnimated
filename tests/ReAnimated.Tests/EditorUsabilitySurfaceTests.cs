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
                string.Equals(
                    (string?)element.Attribute("IsEnabled"),
                    "{Binding CanOpenAnimateWorkspace}",
                    StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
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

        XElement sourceColumn = Assert.Single(
            layoutGrid
                .Element(Presentation + "Grid.ColumnDefinitions")!
                .Elements(Presentation + "ColumnDefinition"),
            static column => string.Equals(
                (string?)column.Attribute(Xaml + "Name"),
                "SourceViewportColumn",
                StringComparison.Ordinal));
        XElement hiddenSourceTrigger = Assert.Single(
            sourceColumn.Descendants(Presentation + "DataTrigger"),
            static trigger => string.Equals(
                    (string?)trigger.Attribute("Binding"),
                    "{Binding IsSourceViewportVisible}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)trigger.Attribute("Value"),
                    "False",
                    StringComparison.Ordinal));
        Assert.Contains(
            hiddenSourceTrigger.Elements(Presentation + "Setter"),
            static setter => string.Equals(
                    (string?)setter.Attribute("Property"),
                    "Width",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)setter.Attribute("Value"),
                    "0",
                    StringComparison.Ordinal));
        Assert.Contains(
            hiddenSourceTrigger.Elements(Presentation + "Setter"),
            static setter => string.Equals(
                    (string?)setter.Attribute("Property"),
                    "MaxWidth",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)setter.Attribute("Value"),
                    "0",
                    StringComparison.Ordinal));

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
