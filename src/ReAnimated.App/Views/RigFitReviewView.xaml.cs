using System.Windows.Controls;
using System.Windows.Threading;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App.Views;

public partial class RigFitReviewView : UserControl
{
    public RigFitReviewView() => InitializeComponent();

    private void OnMappingSourceSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox
            {
                DataContext: RigConformanceMappingItemViewModel row,
            } ||
            DataContext is not RigConformanceWizardViewModel owner ||
            args.AddedItems.Count != 1 ||
            args.AddedItems[0] is not string selected ||
            string.Equals(row.SelectedSourceName, selected, StringComparison.Ordinal))
        {
            return;
        }

        // Rebuilding the mapping rows inside ComboBox's mouse-up selection
        // event leaves WPF's single-select collection mid-update. Apply the
        // user's choice after the input event has completed.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (ReferenceEquals(DataContext, owner) && owner.Mappings.Contains(row))
                {
                    row.SelectedSourceName = selected;
                }
            }));
    }
}
