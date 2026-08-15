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
        Assert.DoesNotContain(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute("Visibility"),
                "{Binding IsInspectorPanelVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
                StringComparison.Ordinal));

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
        XElement[] workflowTabs = document
            .Descendants(Presentation + "ToggleButton")
            .Where(static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding SelectWorkspaceCommand}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            ["Models", "Animations", "Playback", "Retarget / Edit", "Export"],
            workflowTabs
                .Select(static element =>
                    (string?)element.Attribute("Content") ?? string.Empty)
                .ToArray());
        Assert.Equal(
            ["Models", "Animations", "Playback", "Retarget/Edit", "Export"],
            workflowTabs
                .Select(static element =>
                    (string?)element.Attribute("CommandParameter") ?? string.Empty)
                .ToArray());
        Assert.All(
            workflowTabs,
            static element => Assert.Null(element.Attribute("IsEnabled")));

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
                (string?)element.Attribute(Xaml + "Name"),
                "AnimationLibraryTab",
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
    public void ModelsWorkspaceExposesIndependentCompleteAuthoringFlow()
    {
        XDocument shellDocument = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml"));
        Assert.Contains(
            shellDocument.Descendants(Presentation + "ToggleButton"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("Content"),
                    "Models",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("CommandParameter"),
                    "Models",
                    StringComparison.Ordinal));

        XDocument workspace = XDocument.Load(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "Views",
                "ModelsWorkspaceView.xaml"));
        XElement modelsSurface = Assert.Single(
            shellDocument.Descendants(),
            static element => string.Equals(
                element.Name.LocalName,
                "ModelsWorkspaceView",
                StringComparison.Ordinal));
        Assert.Null(modelsSurface.Attribute("DataContext"));
        Assert.Null(modelsSurface.Attribute("Visibility"));

        string shellCodeBehind = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml.cs"));
        Assert.Contains(
            "ModelsWorkspaceSurface.DataContext = _viewModel.Models;",
            shellCodeBehind,
            StringComparison.Ordinal);
        string[] requiredCommands =
        [
            "{Binding ImportFbxCommand}",
            "{Binding OpenPackageCommand}",
            "{Binding SavePackageCommand}",
            "{Binding SelectTextureCommand}",
            "{Binding OpenSelectedAnimationInAnimateCommand}",
            "{Binding ReturnToProjectModelsCommand}",
        ];
        foreach (string command in requiredCommands)
        {
            Assert.Contains(
                workspace.Descendants(Presentation + "Button"),
                element => string.Equals(
                    (string?)element.Attribute("Command"),
                    command,
                    StringComparison.Ordinal));
        }

        XElement rigTreatment = Assert.Single(
            workspace.Descendants(Presentation + "ComboBox"),
            static element => string.Equals(
                (string?)element.Attribute("SelectedItem"),
                "{Binding SelectedRigMode}",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding CanChangeRigMode}",
            (string?)rigTreatment.Attribute("IsEnabled"));

        string[] editableAnimationFields =
        [
            "{Binding Included, UpdateSourceTrigger=PropertyChanged}",
            "{Binding DisplayName, UpdateSourceTrigger=PropertyChanged}",
            "{Binding FrameRateNumerator, UpdateSourceTrigger=PropertyChanged}",
            "{Binding FrameRateDenominator, UpdateSourceTrigger=PropertyChanged}",
            "{Binding RootMotionMode, UpdateSourceTrigger=PropertyChanged}",
            "{Binding RootBoneName, UpdateSourceTrigger=PropertyChanged}",
        ];
        string[] attributeValues = workspace.Root!
            .DescendantsAndSelf()
            .Attributes()
            .Select(static attribute => attribute.Value)
            .ToArray();
        foreach (string binding in editableAnimationFields)
        {
            Assert.Contains(binding, attributeValues);
        }

        Assert.Contains(
            workspace.Descendants(Presentation + "TextBlock"),
            static element => ((string?)element.Attribute("Text"))?.Contains(
                "part of the project model library",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            workspace.Descendants(Presentation + "TextBlock"),
            static element => ((string?)element.Attribute("Text"))?.Contains(
                "cannot replace an animation target",
                StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            workspace.Descendants(Presentation + "Button"),
            static element => string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding OpenSelectedAnimationInAnimateCommand}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("Content"),
                    "Play selected in Playback",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            workspace.Descendants(Presentation + "TabItem"),
            static element => string.Equals(
                (string?)element.Attribute("Header"),
                "Build / diagnostics",
                StringComparison.Ordinal));

        foreach (string header in new[] { "Files", "Developer Tools" })
        {
            Assert.Contains(
                shellDocument.Descendants(Presentation + "TabItem"),
                element => string.Equals(
                    (string?)element.Attribute("Header"),
                    header,
                    StringComparison.Ordinal));
        }
        Assert.DoesNotContain(
            shellDocument.Descendants(Presentation + "TabItem"),
            static element => string.Equals(
                (string?)element.Attribute("Header"),
                "Receipts",
                StringComparison.Ordinal));
        Assert.Contains(
            shellDocument.Descendants(Presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding ExportCharacterFilesCommand}",
                StringComparison.Ordinal));

        string dialogCode = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "Infrastructure",
                "ProjectFileDialogs.cs"));
        Assert.Contains(
            "dialog.ShowDialog(owner)",
            dialogCode,
            StringComparison.Ordinal);
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
            "EditorRootGrid.Children.Remove(inactiveSurface);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorRootGrid.Children.Add(activeSurface);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(MainWindowViewModel.IsModelsWorkspace)",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(MainWindowViewModel.IsAnimationWorkspaceSurfaceVisible)",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameof(MainWindowViewModel.IsExportWorkspace)",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewportRegionGrid.Children.Remove(ViewportGrid);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewportRegionGrid.Children.Add(ViewportGrid);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnimationWorkspaceSurface.Children.Remove(\n" +
            "                ModelsWorkflowSurface);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnimationWorkspaceSurface.Children.Add(\n" +
            "                    ModelsWorkflowSurface);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.PropertyChanged -= OnViewModelPropertyChanged;",
            windowCode,
            StringComparison.Ordinal);

        XElement[] catalogButtons = document
            .Descendants(Presentation + "Button")
            .Where(static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding AssetBrowser.IndexGameCommand}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(catalogButtons);
        Assert.All(
            catalogButtons,
            static catalogButton => Assert.Equal(
                "{Binding AssetBrowser.CatalogActionLabel}",
                (string?)catalogButton.Attribute("Content")));
        Assert.Single(
            catalogButtons,
            static catalogButton => catalogButton
                .Ancestors(Presentation + "Border")
                .Any(static border => string.Equals(
                    (string?)border.Attribute("Visibility"),
                    "{Binding IsModelsWorkspace, Converter={StaticResource BooleanToVisibilityConverter}}",
                    StringComparison.Ordinal)));
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

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void WorkflowSurfacesHaveIndependentPurposeBuiltLayouts()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "MainWindow.xaml"));

        XElement animations = Assert.Single(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "AnimationsWorkflowSurface",
                StringComparison.Ordinal));
        XElement animationTable = Assert.Single(
            animations.Descendants(Presentation + "DataGrid"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "AnimationLibraryTable",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AnimationLibrary}",
            (string?)animationTable.Attribute("ItemsSource"));
        Assert.Contains(
            animationTable.Descendants(Presentation + "TextBlock"),
            static text => string.Equals(
                (string?)text.Attribute("Text"),
                "Immutable source",
                StringComparison.Ordinal));
        Assert.Contains(
            animationTable.Descendants(Presentation + "DataTrigger"),
            static trigger =>
                string.Equals(
                    (string?)trigger.Attribute("Binding"),
                    "{Binding ShowVariantGroupHeader}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)trigger.Attribute("Value"),
                    "True",
                    StringComparison.Ordinal));
        Assert.Contains(
            animationTable.Descendants().Where(static element =>
                element.Name.LocalName.EndsWith(
                    "Column",
                    StringComparison.Ordinal)),
            static column => string.Equals(
                (string?)column.Attribute("Header"),
                "Origin model / rig",
                StringComparison.Ordinal));
        Assert.Contains(
            animationTable.Descendants().Where(static element =>
                element.Name.LocalName.EndsWith(
                    "Column",
                    StringComparison.Ordinal)),
            static column => string.Equals(
                (string?)column.Attribute("Header"),
                "Primary SCR",
                StringComparison.Ordinal));

        XElement playback = Assert.Single(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "PlaybackWorkflowSurface",
                StringComparison.Ordinal));
        Assert.Single(
            playback.Descendants(),
            static element =>
                element.Name.LocalName == "ViewportPane");
        Assert.DoesNotContain(
            playback.Descendants(Presentation + "ListBox"),
            static _ => true);
        Assert.Contains(
            playback.Descendants(Presentation + "ToggleButton"),
            static toggle => string.Equals(
                (string?)toggle.Attribute("Content"),
                "FPP camera",
                StringComparison.Ordinal));

        XElement retargetExplorerColumn = Assert.Single(
            document.Descendants(Presentation + "ColumnDefinition"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "RetargetExplorerColumn",
                StringComparison.Ordinal));
        Assert.Equal("0", (string?)retargetExplorerColumn.Attribute("Width"));
        XElement retargetViewport = Assert.Single(
            document.Descendants(Presentation + "Grid"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "ViewportRegionGrid",
                StringComparison.Ordinal));
        Assert.Equal("0", (string?)retargetViewport.Attribute("Grid.Row"));
        Assert.Equal("5", (string?)retargetViewport.Attribute("Grid.ColumnSpan"));
        XElement retargetSourcePane = Assert.Single(
            retargetViewport.Descendants(),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "SourceViewportPane",
                StringComparison.Ordinal));
        Assert.Null(retargetSourcePane.Attribute("Visibility"));
        XElement retargetEditor = Assert.Single(
            document.Descendants(Presentation + "Border"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "RetargetEditorSurface",
                StringComparison.Ordinal));
        Assert.Equal("2", (string?)retargetEditor.Attribute("Grid.Row"));
        Assert.Equal("5", (string?)retargetEditor.Attribute("Grid.ColumnSpan"));
        XElement retargetDock = Assert.Single(
            document.Descendants(Presentation + "TabControl"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "RetargetBottomDock",
                StringComparison.Ordinal));
        Assert.Equal("4", (string?)retargetDock.Attribute("Grid.Row"));
        Assert.Equal("5", (string?)retargetDock.Attribute("Grid.ColumnSpan"));
        Assert.Contains(
            document.Descendants(Presentation + "Button"),
            static button => string.Equals(
                (string?)button.Attribute("Content"),
                "Reset layout",
                StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "TabItem"),
            static tab => string.Equals(
                (string?)tab.Attribute("Header"),
                "Timeline / curves",
                StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "TextBlock"),
            static text => string.Equals(
                (string?)text.Attribute("Text"),
                "Mapping evidence",
                StringComparison.Ordinal));
        string[] componentToggles = document
            .Descendants(Presentation + "CheckBox")
            .Where(static checkBox =>
                ((string?)checkBox.Attribute("IsChecked"))?.Contains(
                    "Enabled",
                    StringComparison.Ordinal) == true)
            .Select(static checkBox =>
                (string?)checkBox.Attribute("Content") ?? string.Empty)
            .Where(static content => content is "T" or "R" or "S")
            .ToArray();
        Assert.Equal(["T", "R", "S"], componentToggles);
        XElement transferPicker = Assert.Single(
            document.Descendants(Presentation + "ComboBox"),
            static item => string.Equals(
                (string?)item.Attribute("ItemsSource"),
                "{Binding TransferPolicyOptions}",
                StringComparison.Ordinal));
        Assert.Equal("Label", (string?)transferPicker.Attribute("DisplayMemberPath"));
        Assert.Equal(
            "{Binding SelectedTransferPolicyOption, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            (string?)transferPicker.Attribute("SelectedItem"));

        string[] exportTabs = document
            .Descendants(Presentation + "TabItem")
            .Select(static item =>
                (string?)item.Attribute("Header"))
            .Where(static header => header is "Files" or "Developer Tools")
            .Cast<string>()
            .ToArray();
        Assert.Equal(["Files", "Developer Tools"], exportTabs);
        Assert.DoesNotContain(
            document.Descendants(Presentation + "TabItem"),
            static item => string.Equals(
                (string?)item.Attribute("Header"),
                "Receipts",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            document.Descendants(Presentation + "TextBlock"),
            static item => string.Equals(
                (string?)item.Attribute("Text"),
                "Active variant readiness",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            document.Descendants(Presentation + "Button"),
            static item => string.Equals(
                (string?)item.Attribute("Content"),
                "Use as ANM2 source",
                StringComparison.Ordinal));
        Assert.Contains(
            document.Descendants(Presentation + "Expander"),
            static item => string.Equals(
                (string?)item.Attribute("Header"),
                "Base-game model browser",
                StringComparison.Ordinal));

        XDocument application = XDocument.Load(FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "App.xaml"));
        Assert.Contains(
            application.Descendants(Presentation + "Style"),
            static style => string.Equals(
                (string?)style.Attribute("TargetType"),
                "{x:Type ListBoxItem}",
                StringComparison.Ordinal));
        Assert.Contains(
            application.Descendants(Presentation + "Style"),
            static style => string.Equals(
                (string?)style.Attribute("TargetType"),
                "{x:Type DataGridRow}",
                StringComparison.Ordinal));

        string codeBehind = File.ReadAllText(FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "MainWindow.xaml.cs"));
        Assert.Contains(
            "SetWorkflowSurfaceAttached(\n            AnimationsWorkflowSurface",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnResetRetargetLayoutClick",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "_viewModel.IsRetargetWorkspace ||",
            codeBehind,
            StringComparison.Ordinal);
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
