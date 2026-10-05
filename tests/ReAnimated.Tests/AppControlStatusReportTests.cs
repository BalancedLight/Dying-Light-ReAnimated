using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Core.Automation;

namespace ReAnimated.Tests;

public sealed class AppControlStatusReportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task StatusPreservesWorkflowSelectionsAndDirtyBusyStateWithoutAuthoringActions()
    {
        Guid projectId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid selectedAnimationId = Guid.NewGuid();
        Guid activeAnimationId = Guid.NewGuid();
        var snapshot = new AppControlStatus(Guid.NewGuid().ToString("N"), 123, "Generic project", null, true, true)
        {
            ActiveWorkflow = "Models",
            ActivePage = "ModelAuthoring",
            GuidedStep = "Preview",
            ProjectId = projectId,
            SelectedModelId = modelId,
            SelectedAnimationId = selectedAnimationId,
            ActiveAnimationId = activeAnimationId,
        };
        var host = new ReadOnlyHost(snapshot);
        AppControlResponse response = await AppControlHandler.ExecuteAsync(host, "status", null, CancellationToken.None);
        AppControlResponseReport report = AppControlResponseReport.Create("status", response);
        string json = JsonSerializer.Serialize(report, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("dl-reanimated-app-response-v1", root.GetProperty("format").GetString());
        Assert.Equal("status", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("closeScheduled").GetBoolean());
        JsonElement state = root.GetProperty("status");
        Assert.Equal("Models", state.GetProperty("activeWorkflow").GetString());
        Assert.Equal("ModelAuthoring", state.GetProperty("activePage").GetString());
        Assert.Equal("Preview", state.GetProperty("guidedStep").GetString());
        Assert.Equal(projectId, state.GetProperty("projectId").GetGuid());
        Assert.Equal(modelId, state.GetProperty("selectedModelId").GetGuid());
        Assert.Equal(selectedAnimationId, state.GetProperty("selectedAnimationId").GetGuid());
        Assert.Equal(activeAnimationId, state.GetProperty("activeAnimationId").GetGuid());
        Assert.True(state.GetProperty("isDirty").GetBoolean());
        Assert.True(state.GetProperty("isBusy").GetBoolean());
        Assert.Equal(1, host.StatusReads);
        Assert.DoesNotContain("accessToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pipeName", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void OlderSnapshotRetainsUnavailableContextAsNull()
    {
        const string legacy = """
            {"InstanceId":"00000000000000000000000000000001","ProcessId":123,
             "ProjectName":"Generic project","ProjectPath":null,"IsDirty":false,"IsBusy":false}
            """;
        AppControlStatus snapshot = Assert.IsType<AppControlStatus>(JsonSerializer.Deserialize<AppControlStatus>(legacy));
        string json = JsonSerializer.Serialize(AppControlResponseReport.Create("status", new(true, null, snapshot)), JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement state = document.RootElement.GetProperty("status");
        foreach (string field in new[] { "activeWorkflow", "activePage", "guidedStep", "projectId",
                     "selectedModelId", "selectedAnimationId", "activeAnimationId" })
            Assert.Equal(JsonValueKind.Null, state.GetProperty(field).ValueKind);
        Assert.Equal("Generic project", state.GetProperty("projectName").GetString());
        Assert.False(state.GetProperty("isDirty").GetBoolean());
        Assert.False(state.GetProperty("isBusy").GetBoolean());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public async Task EnrichedSnapshotRoundTripsAuthenticatedTransportResponse()
    {
        var status = new AppControlStatus(Guid.NewGuid().ToString("N"), 123, "Generic project", null, false, false)
        {
            ActiveWorkflow = "Playback",
            ActivePage = "Playback",
            ProjectId = Guid.NewGuid(),
            ActiveAnimationId = Guid.NewGuid(),
        };
        var response = new AppControlResponse(true, null, status);
        using var stream = new MemoryStream();
        await AppControlTransport.WriteAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(response, await AppControlTransport.ReadAsync<AppControlResponse>(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("close", false)]
    [InlineData("status", true)]
    [Trait("ValidationTier", "Hermetic")]
    public void ReportNeverSchedulesCloseForFailureOrStatus(string command, bool success)
    {
        var status = new AppControlStatus(Guid.NewGuid().ToString("N"), 123, "Generic project", null, true, true)
        {
            ActiveWorkflow = "Animations",
            ActivePage = "Animations",
            ProjectId = Guid.NewGuid(),
        };
        var response = new AppControlResponse(success, success ? null : "The app is busy.", status);
        AppControlResponseReport report = AppControlResponseReport.Create(command, response);
        Assert.False(report.CloseScheduled);
        Assert.Same(status, report.Status);
        Assert.Equal(response.Error, report.Error);
        Assert.Equal(response.Success, report.Success);
    }

    private sealed class ReadOnlyHost(AppControlStatus status) : IAppControlHost
    {
        public int StatusReads { get; private set; }
        public Task<AppControlStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusReads++;
            return Task.FromResult(status);
        }
        public Task SaveAsync(string outputPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Status cannot save.");
        public Task OpenAsync(string projectPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Status cannot open.");
        public void ScheduleClose() => throw new InvalidOperationException("Status cannot close.");
    }
}
