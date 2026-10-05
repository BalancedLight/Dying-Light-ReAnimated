using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App.Views;

public partial class ModelsWorkspaceView : UserControl
{
    public ModelsWorkspaceView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnWorkspaceChanged;
        ApplySetupMode();
    }

    private void OnOpenFaceExpressions(object sender, RoutedEventArgs args)
    {
        CharacterFaceExpressions.IsExpanded = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() =>
            {
                CharacterSystemsScroll.UpdateLayout();
                Point location = CharacterFaceExpressions.TranslatePoint(new Point(0, 0), CharacterSystemsScroll);
                CharacterSystemsScroll.ScrollToVerticalOffset(CharacterSystemsScroll.VerticalOffset + location.Y);
            }));
    }
    private void OnOpenCharacterCompanions(object sender, RoutedEventArgs args)
    {
        CharacterCompanionAuthoringPanel.IsExpanded = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            CharacterSystemsScroll.UpdateLayout();
            Point location = CharacterCompanionAuthoringPanel.TranslatePoint(new Point(0, 0), CharacterSystemsScroll);
            CharacterSystemsScroll.ScrollToVerticalOffset(CharacterSystemsScroll.VerticalOffset + location.Y);
        }));
    }
    private void OnWorkspaceChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (args.OldValue is ModelsWorkspaceViewModel previous) previous.Conformance.PropertyChanged -= OnSetupModeChanged;
        if (args.NewValue is ModelsWorkspaceViewModel current)
        {
            current.Conformance.PropertyChanged += OnSetupModeChanged;
            if (!current.Conformance.IsAdvancedSetupMode) current.IsConformTabSelected = true;
        }
        ApplySetupMode();
    }

    private void OnSetupModeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RigConformanceWizardViewModel.IsAdvancedSetupMode)) ApplySetupMode();
    }

    private void ApplySetupMode()
    {
        bool advanced = DataContext is ModelsWorkspaceViewModel model && model.Conformance.IsAdvancedSetupMode;
        AuthoringSettingsPane.Visibility = SettingsSplitter.Visibility = AuthoringInspectorTabs.Visibility =
            AuthoringTimelinePane.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        GuidedSetupPane.Visibility = GuidedTransport.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        SettingsColumn.MinWidth = advanced ? 215 : 0;
        SettingsColumn.Width = new GridLength(advanced ? 250 : 0);
        SettingsSplitterColumn.Width = new GridLength(advanced ? 5 : 0);
        InspectorColumn.MinWidth = advanced ? 400 : 300;
        InspectorColumn.Width = new GridLength(advanced ? 460 : 380);
        TimelineRow.MinHeight = advanced ? 180 : 60;
        TimelineRow.Height = new GridLength(advanced ? 285 : 60);
    }

    private void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not true)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (DataContext is ModelsWorkspaceViewModel viewModel &&
                    viewModel.FrameModelCommand.CanExecute(null))
                {
                    viewModel.FrameModelCommand.Execute(null);
                }
            }));
    }
}