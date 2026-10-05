using System.Buffers.Binary;
using ReAnimated.Core.Automation;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AppControlTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CloseRefusesUnsavedOrBusyState(bool dirty, bool busy)
    {
        var host = new FakeHost { Status = Status(dirty, busy) };
        AppControlResponse response = await AppControlHandler.ExecuteAsync(host, "close", null,
            CancellationToken.None);
        Assert.False(response.Success);
        Assert.False(host.CloseRequested);
        Assert.Null(host.SavedPath);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CloseSavesToNewPathThenChecksStateAgain()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string output = Path.Combine(directory, "project.dlraproj");
            var host = new FakeHost { Status = Status(true, false) };
            AppControlResponse response = await AppControlHandler.ExecuteAsync(host, "close", output,
                CancellationToken.None);
            Assert.True(response.Success);
            Assert.Equal(output, host.SavedPath);
            Assert.False(response.Status!.IsDirty);
            Assert.True(host.CloseRequested);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CloseRefusesIfEditsRemainAfterSave()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var host = new FakeHost { Status = Status(true, false), RemainDirtyAfterSave = true };
            AppControlResponse response = await AppControlHandler.ExecuteAsync(host, "close",
                Path.Combine(directory, "project.dlraproj"), CancellationToken.None);
            Assert.False(response.Success);
            Assert.False(host.CloseRequested);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task SaveFailureNeverSchedulesClose()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var host = new FakeHost { Status = Status(true, false), FailSave = true };
            await Assert.ThrowsAsync<IOException>(() => AppControlHandler.ExecuteAsync(host, "close",
                Path.Combine(directory, "project.dlraproj"), CancellationToken.None));
            Assert.False(host.CloseRequested);
            Assert.True(host.Status.IsDirty);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CancellationPreventsSaveAndClose()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new FakeHost { Status = Status(false, false) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppControlHandler.ExecuteAsync(host, "close", null, cancellation.Token));
        Assert.False(host.CloseRequested);
        Assert.Null(host.SavedPath);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task CancellationAfterSavePreventsClose()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var host = new FakeHost { Status = Status(true, false), AfterSave = cancellation.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AppControlHandler.ExecuteAsync(host, "close", Path.Combine(directory, "project.dlraproj"),
                    cancellation.Token));
            Assert.NotNull(host.SavedPath);
            Assert.False(host.CloseRequested);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("execute")]
    [InlineData("kill")]
    [Trait("ValidationTier", "Hermetic")]
    public async Task ReadOnlyOrUnknownCommandsNeverMutate(string command)
    {
        var host = new FakeHost { Status = Status(true, false) };
        AppControlResponse response = await AppControlHandler.ExecuteAsync(host, command, null,
            CancellationToken.None);
        Assert.Equal(command == "status", response.Success);
        Assert.Null(host.SavedPath);
        Assert.False(host.CloseRequested);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DiscoveryRejectsReusedProcessIdsAndChangedExecutable()
    {
        AppControlInstance instance = Instance();
        Assert.True(AppControlTransport.IsValidInstance(instance));
        Assert.True(AppControlTransport.MatchesProcessGeneration(instance, instance.ProcessId,
            instance.ProcessStartUtcTicks, instance.ExecutablePath));
        Assert.False(AppControlTransport.MatchesProcessGeneration(instance, instance.ProcessId,
            instance.ProcessStartUtcTicks + 1, instance.ExecutablePath));
        Assert.False(AppControlTransport.MatchesProcessGeneration(instance, instance.ProcessId,
            instance.ProcessStartUtcTicks, instance.ExecutablePath + ".other"));
        Assert.False(AppControlTransport.IsValidInstance(instance with { PipeName = "other" }));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void AuthenticationRequiresTokenInstanceAndProcessGeneration()
    {
        AppControlInstance instance = Instance();
        var request = new AppControlRequest(AppControlTransport.ProtocolVersion, instance.InstanceId,
            instance.ProcessStartUtcTicks, instance.AccessToken, "status");
        Assert.True(AppControlTransport.Authenticate(instance, request));
        Assert.False(AppControlTransport.Authenticate(instance, request with { AccessToken = new string('B', 64) }));
        Assert.False(AppControlTransport.Authenticate(instance, request with { InstanceId = Guid.NewGuid().ToString("N") }));
        Assert.False(AppControlTransport.Authenticate(instance, request with { ProcessStartUtcTicks = 2 }));
        Assert.False(AppControlTransport.Authenticate(instance, request with { ProtocolVersion = 0 }));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task TransportRejectsOversizeMessagesBeforeReadingBody()
    {
        using var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, AppControlTransport.MaximumMessageBytes + 1);
        stream.Write(header);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AppControlTransport.ReadAsync<AppControlRequest>(stream, CancellationToken.None));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task TransportRoundTripsStatusWithoutCredentials()
    {
        using var stream = new MemoryStream();
        var response = new AppControlResponse(true, null, Status(false, false));
        await AppControlTransport.WriteAsync(stream, response, CancellationToken.None);
        Assert.DoesNotContain("AccessToken", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        Assert.Equal(response, await AppControlTransport.ReadAsync<AppControlResponse>(stream,
            CancellationToken.None));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void NewProjectOutputRefusesExistingFile()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "project.dlraproj");
            DlraProject project = DlraProject.Create("Generic project");
            ProjectSerializer.SaveAtomic(project, path, overwrite: false);
            byte[] original = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => AppControlHandler.ValidateNewOutputPath(path));
            Assert.Throws<IOException>(() => ProjectSerializer.SaveAtomic(project, path, overwrite: false));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static AppControlStatus Status(bool dirty, bool busy) =>
        new(Guid.NewGuid().ToString("N"), 123, "Generic project", null, dirty, busy);

    private static AppControlInstance Instance()
    {
        string id = Guid.NewGuid().ToString("N");
        return new(AppControlTransport.ProtocolVersion, id, 123, 1,
            Path.Combine(Path.GetTempPath(), "generic-app.exe"), "DLReAnimated.App." + id,
            new string('A', 64));
    }

    private sealed class FakeHost : IAppControlHost
    {
        public AppControlStatus Status { get; set; } = AppControlTests.Status(false, false);
        public string? SavedPath { get; private set; }
        public bool CloseRequested { get; private set; }
        public bool RemainDirtyAfterSave { get; init; }
        public bool FailSave { get; init; }
        public Action? AfterSave { get; init; }

        public Task<AppControlStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Status);
        }

        public Task SaveAsync(string outputPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave) throw new IOException("Save failed.");
            SavedPath = outputPath;
            Status = Status with { IsDirty = RemainDirtyAfterSave, ProjectPath = outputPath };
            AfterSave?.Invoke();
            return Task.CompletedTask;
        }

        public void ScheduleClose() => CloseRequested = true;

        public Task OpenAsync(string projectPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Opening is not used by this test host.");
    }
}
