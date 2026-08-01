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
