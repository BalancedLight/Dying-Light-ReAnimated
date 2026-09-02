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