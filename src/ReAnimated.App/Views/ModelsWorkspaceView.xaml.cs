using System.Windows;
using System.Windows.Controls;

namespace ReAnimated.App.Views;

public partial class ModelsWorkspaceView : UserControl
{
    public ModelsWorkspaceView()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ModelsWorkspaceDockContent>
        DetachDockContents()
    {
        FrameworkElement settings = DetachFromPanel(
            AuthoringSettingsPane);
        FrameworkElement viewport = DetachFromPanel(
            AuthoringViewportPane);
        FrameworkElement timeline = DetachFromPanel(
            AuthoringTimelinePane);
        FrameworkElement rig = DetachTabContent(
            AuthoringRigTab);
        FrameworkElement materials = DetachTabContent(
            AuthoringMaterialsTab);
        FrameworkElement animations = DetachTabContent(
            AuthoringAnimationsTab);

        return
        [
            new("models.authoring.settings", "Custom model", settings),
            new("models.authoring.preview", "Model preview", viewport),
            new("models.authoring.timeline", "Model timeline", timeline),
            new("models.authoring.rig", "Rig", rig),
            new("models.authoring.materials", "Materials", materials),
            new("models.authoring.animations", "Animation stacks", animations),
        ];
    }

    private static FrameworkElement DetachFromPanel(
        FrameworkElement element)
    {
        if (element.Parent is Panel panel)
        {
            panel.Children.Remove(element);
        }

        return element;
    }

    private static FrameworkElement DetachTabContent(TabItem tab)
    {
        if (tab.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException(
                $"The '{tab.Header}' custom-model tab has no dockable content.");
        }

        tab.Content = null;
        return content;
    }
}

public sealed record ModelsWorkspaceDockContent(
    string Id,
    string Title,
    FrameworkElement Content);
