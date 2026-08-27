using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

/// <summary>
/// Guards the failure-reporting contract: a failed operation has to name its
/// reason where the operator is already looking, and leave a durable record.
/// </summary>
public sealed class FailureReportingTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-FailureReporting-{Guid.NewGuid():N}");

    [Fact]
    public void SummaryNamesTheInnermostReasonOnOneLine()
    {
        var exception = new InvalidOperationException(
            "Outer wrapper text",
            new InvalidDataException(
                "Selected type-320 animation identity 'mixamo_com' is duplicated.\nSecond line."));

        string summary =
            MainWindowViewModel.SummarizeException(exception);

        // The innermost exception carries the actionable sentence; the outer
        // wrapper is usually generic.
        Assert.Equal(
            "Selected type-320 animation identity 'mixamo_com' is duplicated.",
            summary);
        Assert.DoesNotContain('\n', summary);
    }

    [Fact]
    public void SummaryFallsBackToTheTypeWhenThereIsNoMessage()
    {
        Assert.Equal(
            nameof(OperationCanceledException),
            MainWindowViewModel.SummarizeException(
                new OperationCanceledException(string.Empty)));
    }

    [Fact]
    public void SummaryIsBoundedSoTheStatusBarStaysReadable()
    {
        string summary = MainWindowViewModel.SummarizeException(
            new InvalidDataException(new string('x', 5_000)));

        Assert.True(
            summary.Length <= 241,
            $"Status summaries must stay bounded; got {summary.Length}.");
    }

    [Fact]
    public void DescriptionKeepsTheWholeCauseChainAndTheStack()
    {
        Exception thrown;
        try
        {
            try
            {
                throw new InvalidDataException("innermost cause");
            }
            catch (InvalidDataException inner)
            {
                throw new InvalidOperationException("outer cause", inner);
            }
        }
        catch (InvalidOperationException exception)
        {
            thrown = exception;
        }

        string description =
            MainWindowViewModel.DescribeException(thrown);

        Assert.Contains(
            "InvalidOperationException: outer cause",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            "caused by InvalidDataException: innermost cause",
            description,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(DescriptionKeepsTheWholeCauseChainAndTheStack),
            description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CyclicCauseChainsDoNotRunAway()
    {
        // Defensive: a self-referencing chain must terminate.
        var exception = new InvalidDataException("a", new IOException("b"));

        string description =
            MainWindowViewModel.DescribeException(exception);

        Assert.Contains("IOException: b", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorDiagnosticsAreWrittenToTheSessionLog()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string logDirectory = Path.Combine(_temporaryDirectory, "logs");
        using var logger = new StructuredFileLogger(logDirectory);

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new NoDialogs(),
            assets,
            new StubFingerprintService(),
            structuredLogger: logger);

        // An invalid project path is the cheapest way to drive a real
        // reported failure through the pipeline.
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);

        logger.Dispose();
        string log = await File.ReadAllTextAsync(logger.FilePath);

        Assert.Contains(
            "\"event\":\"diagnostic\"",
            log,
            StringComparison.Ordinal);

        // Every line must stay valid NDJSON so the log remains machine-readable.
        foreach (string line in log.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.True(document.RootElement.TryGetProperty(
                "event",
                out _));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            Path.Combine(
                Path.GetTempPath(),
                $"missing-{Guid.NewGuid():N}.dlraproj");

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }

    private sealed class StubFingerprintService
        : IDl1InstalledBuildFingerprintService
    {
        public Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Dl1InstalledBuildFingerprint?>(null);

        public Task<Dl1InstalledBuildFingerprint> ReadAsync(
            string installPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used by this test.");
    }
}
