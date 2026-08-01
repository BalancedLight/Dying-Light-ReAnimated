using System.Text.Json;
using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class WpfStartupSmokeTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ResizeScheduleIsDeterministicBoundedAndAlternating()
    {
        IReadOnlyList<WpfStartupSmoke.WpfResizeTarget>
            first =
                WpfStartupSmoke.CreateResizeSchedule(
                    720.0,
                    520.0,
                    1160.0,
                    700.0);
        IReadOnlyList<WpfStartupSmoke.WpfResizeTarget>
            second =
                WpfStartupSmoke.CreateResizeSchedule(
                    720.0,
                    520.0,
                    1160.0,
                    700.0);

        Assert.Equal(
            WpfStartupSmoke.RequiredResizeStepCount,
            first.Count);
        Assert.Equal(first, second);
        Assert.All(
            first,
            target =>
            {
                Assert.InRange(target.Width, 720.0, 1160.0);
                Assert.InRange(target.Height, 520.0, 700.0);
            });
        for (int index = 0;
             index < first.Count;
             index += 2)
        {
            WpfStartupSmoke.WpfResizeTarget compact =
                first[index];
            WpfStartupSmoke.WpfResizeTarget expanded =
                first[index + 1];
            Assert.InRange(
                compact.Width,
                720.0,
                1160.0);
            Assert.InRange(
                compact.Height,
                520.0,
                700.0);
            Assert.True(
                expanded.Width > compact.Width);
            Assert.True(
                expanded.Height > compact.Height);
        }
    }

    [Theory]
    [InlineData(
        319.1,
        179.2,
        1.25,
        1.5,
        399,
        269)]
    [InlineData(
        0.0,
        0.0,
        1.0,
        1.0,
        1,
        1)]
    public void ExpectedPixelSizeMatchesHostedCeilingPolicy(
        double actualWidth,
        double actualHeight,
        double dpiScaleX,
        double dpiScaleY,
        int expectedWidth,
        int expectedHeight)
    {
        WpfStartupSmoke.WpfViewportPixelSize result =
            WpfStartupSmoke.CalculateExpectedPixelSize(
                actualWidth,
                actualHeight,
                dpiScaleX,
                dpiScaleY);

        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Fact]
    public void SwitchIsDistinctFromNormalAndPackageInvocation()
    {
        Assert.True(
            WpfStartupSmoke.IsRequested(
                [WpfStartupSmoke.Switch, "output"]));
        Assert.False(
            WpfStartupSmoke.IsRequested([]));
        Assert.False(
            WpfStartupSmoke.IsRequested(
                ["--package-self-test", "output"]));
    }

    [Fact]
    public void FailureWritesOneAtomicIncompleteReceipt()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        string output = Path.Combine(
            directory,
            "result");
        try
        {
            WpfStartupSmoke smoke =
                WpfStartupSmoke.Create(
                    [
                        WpfStartupSmoke.Switch,
                        output,
                        "5",
                    ]);
            smoke.TryWriteStartupFailure(
                new InvalidOperationException(
                    "synthetic startup failure"),
                "unit-test");

            string[] files =
                Directory.GetFiles(output);
            string resultPath = Assert.Single(files);
            Assert.Equal(
                WpfStartupSmoke.ResultFileName,
                Path.GetFileName(resultPath));
            Assert.False(
                File.Exists(resultPath + ".tmp"));

            using JsonDocument document =
                JsonDocument.Parse(
                    File.ReadAllBytes(resultPath));
            JsonElement root =
                document.RootElement;
            Assert.Equal(
                WpfStartupSmoke.Format,
                root.GetProperty("format")
                    .GetString());
            Assert.Equal(
                WpfStartupSmoke.SchemaVersion,
                root.GetProperty("schemaVersion")
                    .GetInt32());
            Assert.False(
                root.GetProperty("complete")
                    .GetBoolean());
            Assert.Equal(
                "unit-test",
                root.GetProperty("errorStage")
                    .GetString());
            Assert.Equal(
                "synthetic startup failure",
                root.GetProperty("errorMessage")
                    .GetString());
            Assert.Equal(
                WpfStartupSmoke.RequiredResizeStepCount,
                root.GetProperty(
                        "requiredResizeStepCount")
                    .GetInt32());
            Assert.False(
                root.GetProperty(
                        "animationLibraryRowMaterialized")
                    .GetBoolean());
            Assert.Empty(
                root.GetProperty("resizeSteps")
                    .EnumerateArray());
            Assert.False(smoke.IsComplete);
        }
        finally
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    [Fact]
    public void InvalidOrOccupiedOutputFailsClosed()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                WpfStartupSmoke.Create(
                    [
                        WpfStartupSmoke.Switch,
                        Path.Combine(directory, "short"),
                        "4",
                    ]));

            string occupied =
                Path.Combine(
                    directory,
                    "occupied");
            Directory.CreateDirectory(occupied);
            File.WriteAllText(
                Path.Combine(occupied, "existing.txt"),
                "keep");
            Assert.Throws<IOException>(() =>
                WpfStartupSmoke.Create(
                    [
                        WpfStartupSmoke.Switch,
                        occupied,
                    ]));
            Assert.Equal(
                "keep",
                File.ReadAllText(
                    Path.Combine(
                        occupied,
                        "existing.txt")));
        }
        finally
        {
            Directory.Delete(
                directory,
                recursive: true);
        }
    }
}
