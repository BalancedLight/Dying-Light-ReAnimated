using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.Retargeting.Mapping;

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

    [Fact]
    public async Task RetargetDiagnosticBurstUsesOneCompleteFailureReport()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var dialogs = new NoDialogs();
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "retarget-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "retarget-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "retarget-workspace.json")),
            dialogs,
            assets,
            new StubFingerprintService());
        CompatibilityDiagnostic[] diagnostics = Enumerable.Range(1, 50)
            .Select(index => new CompatibilityDiagnostic(
                $"missing_target_{index}",
                CompatibilityDiagnosticSeverity.Error,
                $"Required target bone {index} is not mapped."))
            .ToArray();

        viewModel.PublishCompatibilityDiagnostics(
            "Retargeting",
            diagnostics);

        (string Title, string Summary, string Details) report =
            Assert.Single(dialogs.ReportedFailures);
        Assert.Equal("Retargeting", report.Title);
        Assert.Contains("50 retargeting issues", report.Summary);
        Assert.Contains("1. [Error] Required target bone 1", report.Details);
        Assert.Contains("50. [Error] Required target bone 50", report.Details);
        Assert.Equal(
            50,
            viewModel.Diagnostics.Count(entry =>
                entry.Area == "Retargeting" && entry.Severity == "Error"));
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
        public List<(string Title, string Summary, string Details)>
            ReportedFailures
        { get; } = [];

        public string? ShowOpenProjectDialog(string? initialPath) =>
            Path.Combine(
                Path.GetTempPath(),
                $"missing-{Guid.NewGuid():N}.dlraproj");

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;

        public void ShowOperationFailure(
            string title,
            string summary,
            string details) =>
            ReportedFailures.Add((title, summary, details));
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

    [Fact]
    public async Task AFailedOperationInterruptsInsteadOfOnlyUpdatingTheCorner()
    {
        // A status-bar line is overwritten by the next status write, so a
        // failure that only lands there is effectively silent.
        Directory.CreateDirectory(_temporaryDirectory);
        var dialogs = new NoDialogs();
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "popup-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "popup-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "popup-workspace.json")),
            dialogs,
            assets,
            new StubFingerprintService());

        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);

        (string Title, string Summary, string Details) failure =
            Assert.Single(dialogs.ReportedFailures);
        Assert.False(string.IsNullOrWhiteSpace(failure.Title));
        Assert.False(string.IsNullOrWhiteSpace(failure.Summary));
        Assert.False(string.IsNullOrWhiteSpace(failure.Details));

        // The same failure still reaches the status bar and the drawer; the
        // dialog is an addition, not a replacement.
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusText));
        Assert.Contains(
            viewModel.Diagnostics,
            entry => entry.Severity == "Error");
    }
}
