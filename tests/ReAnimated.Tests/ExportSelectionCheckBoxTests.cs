using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using ReAnimated.App.ViewModels;

namespace ReAnimated.Tests;

/// <summary>
/// Guards the Export column's checkbox against silently losing the author's
/// selection.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToggleButton.IsChecked</c> is registered
/// <c>BindsTwoWayByDefault</c>, so <c>{Binding IsSelected}</c> looks like it
/// should write back. Inside a <see cref="DataGridTemplateColumn"/>'s
/// <c>CellTemplate</c> it does not: the value arrives with a
/// <c>BaseValueSource.ParentTemplate</c> and the implicit two-way default is
/// not applied, so the box toggles on screen while the row view model never
/// hears about it.
/// </para>
/// <para>
/// That failure is invisible - the grid shows ticked boxes and the export
/// then refuses to run because nothing is selected. Every other templated
/// column in MainWindow.xaml already spells the mode out; this test keeps the
/// Export column honest too.
/// </para>
/// </remarks>
public sealed class ExportSelectionCheckBoxTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void TogglingTheExportColumnCheckBoxSelectsTheRow()
    {
        RunOnStaThread(static () =>
        {
            DataGrid table = LoadExportVariantTable();
            var rows = new ObservableCollection<
                ExportVariantSelectionViewModel>
            {
                new(
                    Guid.NewGuid(),
                    "Thriller",
                    "Ready — reviewed retarget",
                    isEnabled: true,
                    isSelected: false,
                    targetModel: "player_1_tpp"),
                new(
                    Guid.NewGuid(),
                    "Thriller",
                    "Ready — reviewed retarget",
                    isEnabled: true,
                    isSelected: false,
                    targetModel: "zombie_man_a"),
            };
            table.ItemsSource = rows;

            var window = new Window
            {
                Width = 1200,
                Height = 400,
                Content = table,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Visibility = Visibility.Hidden,
            };
            window.Show();
            try
            {
                table.UpdateLayout();
                CheckBox box = Assert.IsType<CheckBox>(FindCheckBox(table));
                ExportVariantSelectionViewModel bound =
                    Assert.IsType<ExportVariantSelectionViewModel>(
                        box.DataContext);
                Assert.True(box.IsEnabled);
                Assert.False(box.IsChecked);

                Toggle(box);

                Assert.True(box.IsChecked);
                Assert.True(
                    bound.IsSelected,
                    "The Export checkbox toggled on screen without selecting the row.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void BlockedRowsCannotBeCheckedForExport()
    {
        RunOnStaThread(static () =>
        {
            DataGrid table = LoadExportVariantTable();
            table.ItemsSource = new ObservableCollection<
                ExportVariantSelectionViewModel>
            {
                new(
                    Guid.NewGuid(),
                    "Thriller",
                    "Target model missing",
                    isEnabled: false,
                    isSelected: false,
                    targetModel: "zombie_man_a"),
            };

            var window = new Window
            {
                Width = 1200,
                Height = 400,
                Content = table,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Visibility = Visibility.Hidden,
            };
            window.Show();
            try
            {
                table.UpdateLayout();
                CheckBox box = Assert.IsType<CheckBox>(FindCheckBox(table));

                Assert.False(
                    box.IsEnabled,
                    "A blocked row must not offer an export checkbox.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Toggle(CheckBox box)
    {
        var peer = new CheckBoxAutomationPeer(box);
        var toggle = (IToggleProvider)peer.GetPattern(
            PatternInterface.Toggle);
        toggle.Toggle();
    }

    /// <summary>
    /// Materializes the shipping Export grid from MainWindow.xaml so this
    /// exercises the real markup rather than a copy that could drift.
    /// </summary>
    private static DataGrid LoadExportVariantTable()
    {
        XDocument document = XDocument.Load(FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "MainWindow.xaml"));
        XElement source = Assert.Single(
            document.Descendants(Presentation + "DataGrid"),
            static element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"),
                "ExportVariantTable",
                StringComparison.Ordinal));

        XElement grid = new(source);
        grid.SetAttributeValue(XNamespace.Xmlns + "x", Xaml.NamespaceName);
        grid.Attribute(Xaml + "Name")?.Remove();
        foreach (XAttribute attribute in grid
                     .DescendantsAndSelf()
                     .Attributes()
                     .Where(static attribute =>
                         attribute.Value.StartsWith(
                             "{StaticResource ",
                             StringComparison.Ordinal))
                     .ToArray())
        {
            attribute.Value = "#FFFFFFFF";
        }

        // The row style is BasedOn a keyed default that only exists once
        // App.xaml is loaded; it carries no behaviour this test depends on.
        grid.Descendants(Presentation + "DataGrid.RowStyle").Remove();

        // Code-behind event handlers cannot resolve in a standalone parse -
        // there is no MainWindow to bind them to. They are chrome (selecting
        // the row under a right-click), not the binding behaviour under test.
        foreach (XAttribute handler in grid
                     .DescendantsAndSelf()
                     .Attributes()
                     .Where(static attribute =>
                         attribute.Value.StartsWith("On", StringComparison.Ordinal) &&
                         attribute.Value.All(char.IsLetterOrDigit))
                     .ToArray())
        {
            handler.Remove();
        }

        return Assert.IsType<DataGrid>(
            XamlReader.Parse(grid.ToString(SaveOptions.DisableFormatting)));
    }

    private static CheckBox? FindCheckBox(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is CheckBox box)
            {
                return box;
            }

            if (FindCheckBox(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', segments)}.");
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }
}
