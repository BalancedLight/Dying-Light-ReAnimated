using ReAnimated.App.Infrastructure;

namespace ReAnimated.Tests;

public sealed class AppPersistenceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"ReAnimated-AppTests-{Guid.NewGuid():N}");

    [Fact]
    public void FatalCrashPresentationGateAllowsExactlyOnePresenter()
    {
        var gate = new FatalCrashPresentationGate();
        int presenters = 0;

        Parallel.For(
            0,
            64,
            _ =>
            {
                if (gate.TryBegin())
                {
                    Interlocked.Increment(ref presenters);
                }
            });

        Assert.Equal(1, presenters);
        Assert.False(gate.TryBegin());
    }

    [Fact]
    public async Task ProjectSourceImporterCopiesIntoProjectAndRetainsHashIdentity()
    {
        string inputDirectory = Path.Combine(
            _temporaryDirectory,
            "incoming");
        string projectDirectory = Path.Combine(
            _temporaryDirectory,
            "project");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(projectDirectory);
        string source = Path.Combine(inputDirectory, "walk.fbx");
        await File.WriteAllBytesAsync(
            source,
            "valid-source-bytes"u8.ToArray());
        string projectPath = Path.Combine(
            projectDirectory,
            "sample.dlraproj");

        ImportedProjectSource first =
            await ProjectSourceImporter.ImportAsync(
                source,
                projectPath);
        ImportedProjectSource second =
            await ProjectSourceImporter.ImportAsync(
                source,
                projectPath);

        Assert.Equal("Sources/walk.fbx", first.ProjectRelativePath);
        Assert.Equal(first.AbsolutePath, second.AbsolutePath);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(
            "valid-source-bytes"u8.ToArray(),
            await File.ReadAllBytesAsync(first.AbsolutePath));
    }

    [Fact]
    public void StateStoreAtomicallyOverwritesAndDeletesSnapshot()
    {
        string statePath = Path.Combine(_temporaryDirectory, "recovery", "state.json");
        JsonWorkspaceStateStore store = new(statePath);
        WorkspaceSnapshot first = CreateSnapshot(12, "Retarget");
        WorkspaceSnapshot second = CreateSnapshot(49, "FPP");

        store.Save(first);
        store.Save(second);
        WorkspaceSnapshot loaded = Assert.IsType<WorkspaceSnapshot>(store.Load());

        Assert.Equal(49, loaded.CurrentFrame);
        Assert.Equal("FPP", loaded.ActiveWorkspaceMode);
        Assert.True(store.Exists);
        Assert.False(File.Exists(statePath + ".tmp"));

        store.Delete();
        Assert.False(store.Exists);
    }

    [Fact]
    public void CrashReporterRetainsExceptionAndRecoveryPath()
    {
        string crashDirectory = Path.Combine(_temporaryDirectory, "crashes");
        CrashReporter reporter = new(crashDirectory);
        InvalidOperationException exception = new("renderer test failure");

        string report = reporter.WriteReport(
            exception,
            "unit-test",
            @"C:\Recovery\workspace.autosave.json");
        string json = File.ReadAllText(report);

        Assert.Contains("renderer test failure", json, StringComparison.Ordinal);
        Assert.Contains("workspace.autosave.json", json, StringComparison.Ordinal);
        Assert.StartsWith(
            Path.GetFullPath(crashDirectory),
            Path.GetFullPath(report),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StructuredLoggerWritesParseableNdjsonAndFlushesErrors()
    {
        string logDirectory = Path.Combine(_temporaryDirectory, "logs");
        string logPath;
        using (var logger = new StructuredFileLogger(logDirectory))
        {
            logPath = logger.FilePath;
            logger.Write(
                AppLogLevel.Information,
                "asset_index_start",
                "Indexing retail assets.");
            logger.Write(
                AppLogLevel.Error,
                "asset_decode_error",
                "One resource failed locally.",
                new Dictionary<string, string>
                {
                    ["resource"] = "armored",
                },
                new InvalidDataException("test failure"));
        }

        string[] rows = File.ReadAllLines(logPath);
        Assert.Equal(2, rows.Length);
        Assert.All(rows, static row =>
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(row);
            Assert.Equal(
                System.Text.Json.JsonValueKind.Object,
                document.RootElement.ValueKind);
        });
        Assert.Contains("test failure", rows[1], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static WorkspaceSnapshot CreateSnapshot(
        int frame,
        string mode)
    {
        return new WorkspaceSnapshot(
            WorkspaceSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            null,
            string.Empty,
            null,
            null,
            frame,
            true,
            60.0f,
            0.02f,
            mode);
    }
}
