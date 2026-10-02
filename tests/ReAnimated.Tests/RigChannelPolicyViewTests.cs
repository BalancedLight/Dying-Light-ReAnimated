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
