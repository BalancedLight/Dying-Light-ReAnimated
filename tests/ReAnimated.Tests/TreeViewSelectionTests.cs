using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ReAnimated.App.Behaviors;

namespace ReAnimated.Tests;

public sealed class TreeViewSelectionTests
{
    [Fact]
    public void SelectedTreeItemUpdatesViewModelWithoutDetachingBinding()
    {
        RunOnStaThread(() =>
        {
            var treeView = new TreeView();
            var viewModel = new SelectionProbe();
            var selectedBone = new object();
            var binding = new Binding(nameof(SelectionProbe.SelectedBone))
            {
                Mode = BindingMode.TwoWay,
                Source = viewModel,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            };

            BindingOperations.SetBinding(
                treeView,
                TreeViewSelection.SelectedItemProperty,
                binding);
            TreeViewSelection.SetIsEnabled(treeView, true);

            treeView.RaiseEvent(
                new RoutedPropertyChangedEventArgs<object>(
                    new object(),
                    selectedBone,
                    TreeView.SelectedItemChangedEvent));

            Assert.Same(selectedBone, viewModel.SelectedBone);
            Assert.True(
                BindingOperations.IsDataBound(
                    treeView,
                    TreeViewSelection.SelectedItemProperty));
        });
    }

    private static void RunOnStaThread(Action action) => WpfTestDispatcher.Run(action);

    private sealed class SelectionProbe
    {
        public object? SelectedBone { get; set; }
    }
}
