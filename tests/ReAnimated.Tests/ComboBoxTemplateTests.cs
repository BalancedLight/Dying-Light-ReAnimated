using System.Xml.Linq;

namespace ReAnimated.Tests;

public sealed class ComboBoxTemplateTests
{
    [Fact]
    public void SelectedValuePresenterForwardsDisplayMemberTemplateSelector()
    {
        string appXamlPath = FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "App.xaml");
        XDocument document = XDocument.Load(appXamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        XElement comboBoxStyle = Assert.Single(
            document.Descendants(presentation + "Style"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("TargetType"),
                    "{x:Type ComboBox}",
                    StringComparison.Ordinal));
        XElement selectionPresenter = Assert.Single(
            comboBoxStyle.Descendants(presentation + "ContentPresenter"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("Content"),
                    "{TemplateBinding SelectionBoxItem}",
                    StringComparison.Ordinal));

        Assert.Equal(
            "{TemplateBinding ItemTemplateSelector}",
            (string?)selectionPresenter.Attribute(
                "ContentTemplateSelector"));
    }

    [Fact]
    public void DropDownArrowUsesDedicatedTrailingColumn()
    {
        string appXamlPath = FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "App.xaml");
        XDocument document = XDocument.Load(appXamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement comboBoxStyle = Assert.Single(
            document.Descendants(presentation + "Style"),
            static element =>
                string.Equals(
                    (string?)element.Attribute("TargetType"),
                    "{x:Type ComboBox}",
                    StringComparison.Ordinal));
        XElement chromeGrid = Assert.Single(
            comboBoxStyle.Descendants(presentation + "Grid"),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "ComboBoxChromeGrid",
                StringComparison.Ordinal));
        XElement[] columns = chromeGrid
            .Element(presentation + "Grid.ColumnDefinitions")!
            .Elements(presentation + "ColumnDefinition")
            .ToArray();

        Assert.Equal(2, columns.Length);
        Assert.Equal("*", (string?)columns[0].Attribute("Width"));
        Assert.Equal("28", (string?)columns[1].Attribute("Width"));

        XElement toggle = Assert.Single(
            chromeGrid.Elements(presentation + "ToggleButton"));
        Assert.Equal("2", (string?)toggle.Attribute("Grid.ColumnSpan"));
        Assert.Equal(
            "Stretch",
            (string?)toggle.Attribute("HorizontalContentAlignment"));

        XElement selectionPresenter = Assert.Single(
            chromeGrid.Elements(presentation + "ContentPresenter"));
        Assert.Equal(
            "0",
            (string?)selectionPresenter.Attribute("Grid.Column"));

        XElement arrow = Assert.Single(
            chromeGrid.Elements(presentation + "Path"),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "DropDownArrow",
                StringComparison.Ordinal));
        Assert.Equal("1", (string?)arrow.Attribute("Grid.Column"));
        Assert.Equal(
            "Center",
            (string?)arrow.Attribute("HorizontalAlignment"));
    }

    private static string FindRepositoryFile(
        params string[] relativeSegments)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                [directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{Path.Combine(relativeSegments)}' " +
            $"above '{AppContext.BaseDirectory}'.");
    }
}
