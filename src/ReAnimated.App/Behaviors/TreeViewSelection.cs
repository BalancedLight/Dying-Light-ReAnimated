using System.Windows;
using System.Windows.Controls;

namespace ReAnimated.App.Behaviors;

public static class TreeViewSelection
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(TreeViewSelection),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.RegisterAttached(
            "SelectedItem",
            typeof(object),
            typeof(TreeViewSelection),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedItemChanged));

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(
        DependencyObject element,
        bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    public static object? GetSelectedItem(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SelectedItemProperty);
    }

    public static void SetSelectedItem(
        DependencyObject element,
        object? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SelectedItemProperty, value);
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TreeView treeView)
        {
            return;
        }

        treeView.SelectedItemChanged -= OnTreeSelectedItemChanged;
        if (args.NewValue is true)
        {
            treeView.SelectedItemChanged += OnTreeSelectedItemChanged;
        }
    }

    private static void OnSelectedItemChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TreeView treeView ||
            !GetIsEnabled(treeView))
        {
            return;
        }

        // Keep the subscription idempotent when a binding replaces the
        // selected item or the DataContext changes.
        treeView.SelectedItemChanged -= OnTreeSelectedItemChanged;
        treeView.SelectedItemChanged += OnTreeSelectedItemChanged;
    }

    private static void OnTreeSelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> args)
    {
        TreeView treeView = (TreeView)sender;
        // SetCurrentValue preserves the two-way binding expression. SetValue
        // would replace it, leaving TreeView.SelectedItem highlighted while
        // the view model continued to report no selected bone.
        treeView.SetCurrentValue(SelectedItemProperty, args.NewValue);
    }
}
