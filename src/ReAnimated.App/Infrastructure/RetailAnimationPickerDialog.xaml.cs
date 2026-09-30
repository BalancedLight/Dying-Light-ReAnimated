using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReAnimated.App.ViewModels;

namespace ReAnimated.App.Infrastructure;

public partial class RetailAnimationPickerDialog : Window
{
    private readonly RetailAnimationBrowserViewModel _browser;
    private readonly string _originalSearch;
    private readonly string _originalProvider;

    public RetailAnimationPickerDialog(
        RetailAnimationBrowserViewModel browser)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _originalSearch = browser.SearchText;
        _originalProvider = browser.SelectedProviderFilter;
        InitializeComponent();
        DataContext = browser;
        if (browser.SelectedAsset is { Kind: AssetKind.Animation } selected)
        {
            Candidates.SelectedItem = selected;
        }

        Loaded += (_, _) => SearchBox.Focus();
    }

    public AssetItemViewModel? SelectedAnimation { get; private set; }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
        {
            _browser.SelectedProviderFilter = _originalProvider;
            _browser.SearchText = _originalSearch;
        }

        base.OnClosed(e);
    }

    private void OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ChooseButton.IsEnabled = Candidates.SelectedItem is AssetItemViewModel
        {
            Kind: AssetKind.Animation,
            RetailAsset: not null,
        };

    private void OnCandidateDoubleClick(
        object sender,
        MouseButtonEventArgs e) => ChooseSelectedAnimation();

    private void OnChooseClicked(
        object sender,
        RoutedEventArgs e) => ChooseSelectedAnimation();

    private void ChooseSelectedAnimation()
    {
        if (Candidates.SelectedItem is not AssetItemViewModel
            {
                Kind: AssetKind.Animation,
                RetailAsset: not null,
            } selected)
        {
            return;
        }

        SelectedAnimation = selected;
        _browser.SelectedAsset = selected;
        DialogResult = true;
    }
}
