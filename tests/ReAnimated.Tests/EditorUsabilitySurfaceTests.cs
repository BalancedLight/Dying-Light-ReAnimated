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

        // Fidelity details moved off the toolbar into the Help menu; what
        // matters is that the command is still reachable from the chrome.
        Assert.Contains(
            document.Descendants(Presentation + "MenuItem"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding ShowFidelityDetailsCommand}",
                StringComparison.Ordinal));

        XElement playbackControls = Assert.Single(
            document.Descendants(Presentation + "StackPanel"),
            static element => element.Elements(Presentation + "Button")
                .Any(button => string.Equals(
                    (string?)button.Attribute("Command"),
                    "{Binding Timeline.TogglePlaybackCommand}",
                    StringComparison.Ordinal)) &&
                element.Ancestors(Presentation + "Grid")
                    .Any(grid => string.Equals(
                        (string?)grid.Attribute(Xaml + "Name"),
                        "PrimaryToolbarLayout",
                        StringComparison.Ordinal)));
        Assert.Equal(
            "1",
            (string?)playbackControls.Attribute("Grid.Row"));
        Assert.Equal(
            "Center",
            (string?)playbackControls.Attribute("HorizontalAlignment"));

        string[] attachmentNumericBindings =
        [
            "PositionX", "PositionY", "PositionZ",
            "RotationX", "RotationY", "RotationZ",
            "ScaleX", "ScaleY", "ScaleZ",
        ];
        foreach (string propertyName in attachmentNumericBindings)
        {
            Assert.Contains(
                document.Descendants(Presentation + "TextBox"),
                element => string.Equals(
                    (string?)element.Attribute("Text"),
                    $"{{Binding AttachmentEditor.{propertyName}, UpdateSourceTrigger=LostFocus}}",
                    StringComparison.Ordinal));
        }
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
            "Accept pending proposal & play",
        ];
        // Edit bones is now a Viewport menu item rather than a toolbar button.
        Assert.Contains(
            document.Descendants(Presentation + "MenuItem"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding OpenBoneEditorCommand}",
                StringComparison.Ordinal));

        foreach (string label in labels)
        {
            Assert.Contains(
                document.Descendants(Presentation + "Button"),
                element => string.Equals(
                    (string?)element.Attribute("Content"),
                    label,
                    StringComparison.Ordinal));
        }

        Assert.All(
            document.Descendants(Presentation + "Button")
                .Where(static element => string.Equals(
                    (string?)element.Attribute("Content"),
                    "Accept pending proposal & play",
                    StringComparison.Ordinal)),
            static element => Assert.Equal(
                "{Binding HasPendingMappingProposal, Converter={StaticResource BooleanToVisibilityConverter}}",
                (string?)element.Attribute("Visibility")));

        // The workspace switcher is now Window > Workspace. It is still the
        // only global switcher, so it must carry every mode, in order, with
        // each item reflecting the workspace it selects.
        XElement[] workflowTabs = document
            .Descendants(Presentation + "MenuItem")
            .Where(static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding SelectWorkspaceCommand}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            ["Models", "Animations", "Playback", "Retarget/Edit", "Export"],
            workflowTabs
                .Select(static element =>
                    (string?)element.Attribute("CommandParameter") ?? string.Empty)
                .ToArray());
        Assert.Contains(
            workflowTabs,
            static element => string.Equals(
                (string?)element.Attribute("IsChecked"),
                "{Binding IsRetargetWorkspace, Mode=OneWay}",
                StringComparison.Ordinal));
        Assert.All(
            workflowTabs,
            static element => Assert.Null(element.Attribute("IsEnabled")));

        // These moved to the Viewport menu, which already carried duplicates
        // of them before the toolbar was trimmed.
        string[] visibleRigControls = [];
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

        XElement root = document.Root!;
        Assert.Equal(
            "TimelinePanel_OnSizeChanged",
            (string?)root.Attribute("SizeChanged"));
        Assert.Equal(
            "TimelinePanel_OnPreviewMouseWheel",
            (string?)root.Attribute("PreviewMouseWheel"));
        XElement curveCanvas = Assert.Single(
            document.Descendants(Presentation + "Canvas"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "CurveCanvas",
                StringComparison.Ordinal));
        Assert.Equal(
            "{Binding CurveCanvasHeight}",
            (string?)curveCanvas.Attribute("Height"));
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
        // The workspace switcher lives in Window > Workspace now; the menu is
        // the only global switcher, so it has to keep carrying every mode.
        Assert.Contains(
            shellDocument.Descendants(Presentation + "MenuItem"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("CommandParameter"),
                    "Models",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding SelectWorkspaceCommand}",
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
        Assert.Contains(
            shellDocument.Descendants(Presentation + "TextBox"),
            static element => string.Equals(
                (string?)element.Attribute("Text"),
                "{Binding AnimationRpackFileName, UpdateSourceTrigger=PropertyChanged}",
                StringComparison.Ordinal));
        Assert.Contains(
            shellDocument.Descendants(Presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding SelectAnimationRpackAppendSourceCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            shellDocument.Descendants(Presentation + "CheckBox"),
            static element => string.Equals(
                (string?)element.Attribute("IsChecked"),
                "{Binding ReplaceAnimationRpackConflicts}",
                StringComparison.Ordinal));
        Assert.Contains(
            shellDocument.Descendants(Presentation + "Button"),
            static element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding AddSelectedRetailAnimationScriptCommand}",
                StringComparison.Ordinal));

        string viewModelCode = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "ViewModels",
                "MainWindowViewModel.cs"));
        Assert.Contains(
            "Rp6lAnimationLibraryCodec.AppendAtomicAsync(",
            viewModelCode,
            StringComparison.Ordinal);

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
        XElement dockManager = Assert.Single(
            document.Descendants(),
            static element =>
                element.Name.LocalName == "DockingManager" &&
                string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    "WorkflowDockManager",
                    StringComparison.Ordinal));
        Assert.Equal(
            "True",
            (string?)dockManager.Attribute("AllowMixedOrientation"));
        Assert.Equal(
            "True",
            (string?)dockManager.Attribute("IsVirtualizingAnchorable"));
        Assert.Equal(
            "500",
            (string?)dockManager.Attribute("Panel.ZIndex"));

        Assert.Contains(
            document.Descendants(Presentation + "MenuItem"),
            static item => string.Equals(
                (string?)item.Attribute(Xaml + "Name"),
                "DockPanesMenu",
                StringComparison.Ordinal));

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
            "_dockController.SwitchWorkflow(workflow);",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditorDockLayoutSettingsStore.CreateDefault()",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "retarget.target-camera",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "playback.target-camera",
            windowCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "models.authoring.workspace",
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

        // One catalog load feeds every browser, so each button may label
        // itself from whichever browser it sits next to, but it must still
        // report a catalog action rather than static text.
        Assert.All(
            catalogButtons,
            static catalogButton => Assert.True(
                (string?)catalogButton.Attribute("Content") is
                    "{Binding AssetBrowser.CatalogActionLabel}" or
                    "{Binding AnimationBrowser.CatalogActionLabel}",
                "Every catalog button must report a live catalog action label."));
        Assert.Contains(
            catalogButtons,
            static catalogButton => catalogButton
                .Ancestors()
                .Any(static element => string.Equals(
                    (string?)element.Attribute(Xaml + "Name"),
                    "ModelsRetailBrowserPane",
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
                "{Binding VariantGroupLabel, StringFormat=Source: {0}}",
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
        // Playback is single-pane until the FPP camera is enabled, at which
        // point the evaluated-camera pane joins the free external orbit.
        Assert.Equal(
            2,
            playback.Descendants().Count(static element =>
                element.Name.LocalName == "ViewportPane"));
        XElement playbackFppPane = Assert.Single(
            playback.Descendants(),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "PlaybackFppViewportPane",
                StringComparison.Ordinal));

        // Every workflow pane is detached into AvalonDock. Viewport and
        // timeline roots therefore need direct view-model references instead
        // of inherited bindings from MainWindow.
        Assert.Null(playbackFppPane.Attribute("DataContext"));
        string shellCodeBehind = File.ReadAllText(
            FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml.cs"));
        Assert.Contains(
            "PlaybackFppViewportPane.DataContext = _viewModel.SourceViewport;",
            shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "AnimationsSourcePreviewPane.DataContext = _viewModel.SourceViewport;",
            shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "PlaybackTargetViewportPane.DataContext = _viewModel.TargetViewport;",
            shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "PlaybackTimelinePane.DataContext = _viewModel.Timeline;",
            shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "SourceViewportPane.DataContext = _viewModel.SourceViewport;",
            shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RetargetTimelinePane.DataContext = _viewModel.Timeline;",
            shellCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            playback.Descendants(Presentation + "ColumnDefinition"),
            static column => string.Equals(
                (string?)column.Attribute(Xaml + "Name"),
                "PlaybackFppCameraColumn",
                StringComparison.Ordinal));
        Assert.Contains(
            playback.Descendants(Presentation + "ColumnDefinition"),
            static column => string.Equals(
                (string?)column.Attribute(Xaml + "Name"),
                "PlaybackFppSplitterColumn",
                StringComparison.Ordinal));
        Assert.Contains(
            playback.Descendants(Presentation + "GridSplitter"),
            static splitter => string.Equals(
                (string?)splitter.Attribute("ResizeDirection"),
                "Columns",
                StringComparison.Ordinal));

        // The camera-bone picker must stay a ComboBox: a ListBox here would
        // compete with the timeline for the surface's vertical space.
        Assert.DoesNotContain(
            playback.Descendants(Presentation + "ListBox"),
            static _ => true);
        Assert.Contains(
            playback.Descendants(Presentation + "ComboBox"),
            static picker => string.Equals(
                (string?)picker.Attribute("ItemsSource"),
                "{Binding TargetPreviewCameraBoneOptions}",
                StringComparison.Ordinal));
        Assert.Contains(
            playback.Descendants(Presentation + "TextBlock"),
            static text => string.Equals(
                (string?)text.Attribute("Text"),
                "{Binding FppPlaybackCameraStatus, Mode=OneWay}",
                StringComparison.Ordinal));
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
        XElement[] interactiveSplitters = document
            .Descendants(Presentation + "GridSplitter")
            .Where(static splitter =>
                string.Equals(
                    (string?)splitter.Attribute("ResizeBehavior"),
                    "PreviousAndNext",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.True(interactiveSplitters.Length >= 5);
        XElement retargetViewportEditorSplitter = Assert.Single(
            interactiveSplitters,
            static splitter => string.Equals(
                (string?)splitter.Attribute(Xaml + "Name"),
                "RetargetViewportEditorSplitter",
                StringComparison.Ordinal));
        Assert.Equal(
            "True",
            (string?)retargetViewportEditorSplitter.Attribute("ShowsPreview"));
        Assert.All(
            interactiveSplitters.Where(static splitter => !string.Equals(
                (string?)splitter.Attribute(Xaml + "Name"),
                "RetargetViewportEditorSplitter",
                StringComparison.Ordinal)),
            static splitter => Assert.Equal(
                "False",
                (string?)splitter.Attribute("ShowsPreview")));
        Assert.All(
            interactiveSplitters,
            static splitter => Assert.True(
                (string?)splitter.Attribute("ResizeDirection") is
                    "Rows" or "Columns"));
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
            "InitializeWorkflowDocking",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnResetRetargetLayoutClick",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "_dockController.ResetCurrentLayout();",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "content.DataContext = dataContext ?? _viewModel;",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"PanelBackgroundBrush\"",
            codeBehind,
            StringComparison.Ordinal);

        string paletteSurfaces = string.Concat(
            File.ReadAllText(FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "MainWindow.xaml")),
            File.ReadAllText(FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "Views",
                "ViewportPane.xaml")),
            File.ReadAllText(FindRepositoryFile(
                "src",
                "ReAnimated.App",
                "Views",
                "ModelsWorkspaceView.xaml")));
        string[] removedWarmSurfaceColors =
        [
            "#3B301D",
            "#735D2A",
            "#F3C978",
            "#3A2D18",
            "#E63B301D",
            "#3C2F1C",
        ];
        foreach (string color in removedWarmSurfaceColors)
        {
            Assert.DoesNotContain(
                color,
                paletteSurfaces,
                StringComparison.OrdinalIgnoreCase);
        }

        string dockController = File.ReadAllText(FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "Infrastructure",
            "WorkflowDockController.cs"));
        Assert.Contains(
            "Orientation.Horizontal",
            dockController,
            StringComparison.Ordinal);
        Assert.Contains(
            "retarget.target-camera",
            dockController,
            StringComparison.Ordinal);
        Assert.Contains(
            "retarget.mapping",
            dockController,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanDockAsTabbedDocument = true",
            dockController,
            StringComparison.Ordinal);
        Assert.Contains(
            "new XmlLayoutSerializer(_manager)",
            dockController,
            StringComparison.Ordinal);

        Assert.True(
            document.Descendants().Count(static element =>
                element.Name.LocalName == "ResponsiveUniformGrid") >= 10);
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
