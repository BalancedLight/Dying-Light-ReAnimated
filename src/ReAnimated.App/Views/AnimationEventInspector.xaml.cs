using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App.Views;

public partial class AnimationEventInspector : UserControl
{
    public static readonly DependencyProperty IsStandaloneProperty = DependencyProperty.Register(
        nameof(IsStandalone), typeof(bool), typeof(AnimationEventInspector), new PropertyMetadata(false));
    private Window? _editorWindow;
    public bool IsStandalone { get => (bool)GetValue(IsStandaloneProperty); set => SetValue(IsStandaloneProperty, value); }

    public AnimationEventInspector() => InitializeComponent();

    private void OpenEditor_OnClick(object sender, RoutedEventArgs e)
        => OpenEditor(Window.GetWindow(this));

    public void OpenEditor(Window? owner)
    {
        if (DataContext is not AnimationEventEditorViewModel editor) return;
        if (_editorWindow is { IsVisible: true }) { _editorWindow.Activate(); return; }
        var panel = new DockPanel { Margin = new Thickness(8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        ICommand? save = null;
        if (owner?.DataContext is MainWindowViewModel main)
        {
            bool projectEditor = ReferenceEquals(editor, main.Timeline.Events);
            save = projectEditor ? main.SaveWorkspaceCommand : main.Models.SavePackageCommand;
            ICommand export = projectEditor
                ? new AsyncRelayCommand(() => main.ExportEventAnimationRpackAsync(editor), () => !main.IsBusy)
                : main.Models.ExportAnimationRpackCommand;
            actions.Children.Add(new Button { Content = "Save", Command = save, Margin = new Thickness(4) });
            actions.Children.Add(new Button { Content = "Export RPack", Command = export, Margin = new Thickness(4) });
            var status = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap };
            status.SetBinding(TextBlock.TextProperty, new Binding(projectEditor ? nameof(MainWindowViewModel.StatusText) : nameof(ModelsWorkspaceViewModel.BuildStatus)) { Source = projectEditor ? (object)main : main.Models });
            DockPanel.SetDock(status, Dock.Bottom);
            panel.Children.Add(status);
            if (projectEditor)
            {
                var context = new TextBlock { Margin = new Thickness(4) };
                context.SetBinding(TextBlock.TextProperty, new Binding(nameof(MainWindowViewModel.AnimationScriptStateSummary)) { Source = main });
                DockPanel.SetDock(context, Dock.Top);
                panel.Children.Add(context);
            }
        }
        DockPanel.SetDock(actions, Dock.Bottom);
        panel.Children.Add(actions);
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new AnimationEventInspector { DataContext = editor, IsStandalone = true },
        });
        var window = new Window
        {
            Title = "Animation events", Width = 900, Height = 600, MinWidth = 650, MinHeight = 400,
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner?.Background, Foreground = owner?.Foreground, Content = panel,
        };
        if (save is not null) window.InputBindings.Add(new KeyBinding(save, new KeyGesture(Key.S, ModifierKeys.Control)));
        window.Closed += (_, _) => _editorWindow = null;
        _editorWindow = window;
        window.Show();
    }

    private void EventList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AnimationEventEditorViewModel editor && e.AddedItems.Count > 0 && e.AddedItems[0] is AnimationEventItemViewModel item)
            editor.Select(item.Id, false);
    }
}
