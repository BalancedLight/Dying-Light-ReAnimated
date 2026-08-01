using System.Xml.Linq;

namespace ReAnimated.Tests;

public sealed class FppControlSurfaceTests
{
    [Fact]
    public void HeadCorrectionStagesHaveIndependentVisibleControls()
    {
        string mainWindowXamlPath = FindRepositoryFile(
            "src",
            "ReAnimated.App",
            "MainWindow.xaml");
        XDocument document = XDocument.Load(mainWindowXamlPath);
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        IEnumerable<XElement> checkBoxes =
            document.Descendants(presentation + "CheckBox");

        XElement basisCorrection = Assert.Single(
            checkBoxes,
            static element =>
                string.Equals(
                    (string?)element.Attribute("IsChecked"),
                    "{Binding FacialFpp.EnableHSpineBasisCorrection}",
                    StringComparison.Ordinal));
        Assert.Contains(
            "HSpine/HSpine1",
            (string?)basisCorrection.Attribute("Content") ??
            string.Empty,
            StringComparison.Ordinal);
        string basisToolTip =
            (string?)basisCorrection.Attribute("ToolTip") ??
            string.Empty;
        Assert.Contains(
            "decompile",
            basisToolTip,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "not game validated",
            basisToolTip,
            StringComparison.OrdinalIgnoreCase);

        XElement headPosition = Assert.Single(
            checkBoxes,
            static element =>
                string.Equals(
                    (string?)element.Attribute("IsChecked"),
                    "{Binding FacialFpp.EnableHeadPositionCorrection}",
                    StringComparison.Ordinal));
        Assert.Contains(
            "head-position",
            (string?)headPosition.Attribute("Content") ??
            string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "unavailable",
            (string?)headPosition.Attribute("ToolTip") ??
            string.Empty,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            checkBoxes,
            static element =>
                string.Equals(
                    (string?)element.Attribute("IsChecked"),
                    "{Binding FacialFpp.EnableHeadCorrection}",
                    StringComparison.Ordinal));
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
