using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.App.Views;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigChannelPolicyViewTests
{
    private static readonly string[] OwnerBindingPaths = ["PositionChannelOwner", "RotationChannelOwner", "ScaleChannelOwner", "ChannelLod"];

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void GuidedAdjustShowsPlayerReferenceActionAndCurrentReference()
    {
        WpfTestDispatcher.Run(() =>
        {
            var wizard = new RigConformanceWizardViewModel(
                (profile, _) => Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)),
                static _ => { });
            wizard.ResolveTemplateCommand.Execute(null);
            var view = new GuidedModelSetupView { DataContext = new { IsGuidedAdjust = true, Conformance = wizard } };
            view.Measure(new Size(800, 1000));
            view.Arrange(new Rect(0, 0, 800, 1000));
            view.UpdateLayout();

            Button action = Descendants<Button>(view).Single(button =>
                Equals(button.Content, "Use Player 1 third-person reference"));
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Same(wizard.UsePlayerTppReferenceCommand, action.Command);
            Assert.Contains(Descendants<TextBlock>(view), text =>
                text.Visibility == Visibility.Visible && text.Text.Contains(wizard.Template!.SourceResourceName, StringComparison.Ordinal));
        });
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void InactiveSelectedGridRowAndComboChoiceRemainReadable()
    {
        WpfTestDispatcher.Run(() =>
        {
            var gridView = new GridView();
            gridView.Columns.Add(new GridViewColumn { Header = "Node", DisplayMemberBinding = new Binding("Name") });
            gridView.Columns.Add(new GridViewColumn { Header = "Status", DisplayMemberBinding = new Binding("ExportSummary") });
            var list = new ListView { View = gridView, Width = 400, Height = 100 };
            list.Items.Add(new RigChannelPolicyRow(Guid.NewGuid(), "example", null));
            list.SelectedIndex = 0;
            list.Measure(new Size(400, 100));
            list.Arrange(new Rect(0, 0, 400, 100));
            list.UpdateLayout();

            var row = Assert.IsType<ListViewItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(row.IsSelected);
            Assert.False(row.IsKeyboardFocusWithin);
            row.ApplyTemplate();
            var rowChrome = Assert.IsType<Border>(row.Template.FindName("ItemChrome", row));
            AssertReadable(row.Foreground, rowChrome.Background);
            var presenter = Assert.Single(Descendants<GridViewRowPresenter>(row));
            Assert.Equal(2, presenter.Columns.Count);
            Assert.Same(list.Items[0], presenter.Content);

            var choice = new ComboBoxItem { Content = "Animation clip", IsSelected = true };
            choice.ApplyTemplate();
            Assert.False(choice.IsKeyboardFocusWithin);
            var choiceChrome = Assert.IsType<Border>(choice.Template.FindName("ItemChrome", choice));
            AssertReadable(choice.Foreground, choiceChrome.Background);
            Assert.True(choice.Focusable);
            Assert.True(row.Focusable);
        });
    }

    private static void AssertReadable(Brush foreground, Brush background)
    {
        Color text = Assert.IsType<SolidColorBrush>(foreground).Color;
        Color fill = Assert.IsType<SolidColorBrush>(background).Color;
        double a = Luminance(text), b = Luminance(fill);
        Assert.True((Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) >= 4.5);

        static double Luminance(Color color)
        {
            static double Channel(byte component)
            {
                double value = component / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SelectedNodeEditorAppearsBeforeAdvancedStockReview()
    {
        WpfTestDispatcher.Run(() =>
        {
            var view = new RigChannelPolicyView();
            view.Measure(new Size(800, 2000));
            view.Arrange(new Rect(0, 0, 800, 2000));
            view.UpdateLayout();

            Button selected = Descendants<Button>(view).Single(b => Equals(b.Content, "Apply to selected node"));
            Expander stock = Descendants<Expander>(view).Single(e => Equals(e.Header, "Stock humanoid clip settings"));
            Assert.True(selected.TransformToAncestor(view).Transform(new Point()).Y <
                        stock.TransformToAncestor(view).Transform(new Point()).Y);
        });
    }

    [Theory]
    [InlineData(340, 500)]
    [InlineData(460, 680)]
    [Trait("ValidationTier", "Hermetic")]
    public void HelperStageScrollsEachPolicyControlIntoTheViewport(double width, double height)
    {
        WpfTestDispatcher.Run(() =>
        {
            var model = FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
            model = model with { Package = model.Package with { Document = model.Package.Document with {
                RiggingSession = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig)
            } } };
            var wizard = new RigConformanceWizardViewModel(
                (profile, _) => Task.FromResult(
                    Dl1RigTemplateResolution.Failed(profile, "No installation indexed.")),
                static _ => { });
            wizard.SetModel(model);
            wizard.IsAdvancedSetupMode = true;
            wizard.StudioStage = RigStudioStage.HelpersAndHooks;
            var view = new RigConformanceView { DataContext = wizard };
            Layout();
            var section = Descendants<Expander>(view).Single(e => Equals(e.Header, "Channel ownership and LOD"));
            section.IsExpanded = true;
            // Exercise the taller layout that also displays terminal-helper review.
            Descendants<Expander>(view).Single(e => Equals(e.Header, "Unmatched terminal helpers")).IsExpanded = true;
            Layout();
            var policies = Descendants<RigChannelPolicyView>(section).Single();
            ScrollViewer scroll = Ancestor<ScrollViewer>(section);
            Assert.True(double.IsFinite(scroll.ViewportHeight) && scroll.ViewportHeight > 0);
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.True(wizard.CanEditChannelPolicies);

            foreach (string path in OwnerBindingPaths)
            {
                ComboBox control = Descendants<ComboBox>(policies).Last(combo =>
                    BindingOperations.GetBindingExpression(combo, ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path == path);
                AssertReachable(control);
            }
            AssertReachable(Descendants<Button>(policies).Single(button => Equals(button.Content, "Apply to selected node")));

            void Layout()
            {
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                view.UpdateLayout();
            }

            void AssertReachable(FrameworkElement control)
            {
                Assert.True(control.Visibility == Visibility.Visible && control.IsEnabled && control.ActualHeight > 0);
                Rect before = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + before.Top);
                Layout();
                Rect after = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
                Assert.True(scroll.VerticalOffset > 0);
                Assert.InRange(after.Top, -1, scroll.ViewportHeight - control.ActualHeight + 1);
                Assert.True(after.Bottom <= scroll.ViewportHeight + 1);
            }
        });
    }

    private static T Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T typed) return typed;
        throw new InvalidOperationException($"No {typeof(T).Name} ancestor found.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
