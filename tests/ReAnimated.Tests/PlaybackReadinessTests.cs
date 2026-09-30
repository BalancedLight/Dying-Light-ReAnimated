using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class PlaybackReadinessTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(), $"ReAnimated-Playback-{Guid.NewGuid():N}");

    public PlaybackReadinessTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public async Task EmptyProjectDisablesPlaybackWhileFrameEditingAndModelTimelineStayAvailable()
    {
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(Path.Combine(_temporaryDirectory, "recovery.json")),
            new EmptyDialogs(), assets);

        viewModel.SelectWorkspaceCommand.Execute("Playback");
        Assert.True(viewModel.NeedsPlaybackAnimation);
        Assert.False(viewModel.HasPlaybackAnimation);
        Assert.Empty(viewModel.Timeline.Tracks);
        Assert.False(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));
        Assert.Equal("Play", viewModel.Timeline.PlaybackLabel);
        viewModel.Timeline.IsPlaying = true;
        Assert.False(viewModel.Timeline.IsPlaying);

        int frame = viewModel.Timeline.CurrentFrame;
        viewModel.Timeline.StepForwardCommand.Execute(null);
        Assert.Equal(frame + 1, viewModel.Timeline.CurrentFrame);
        Assert.True(viewModel.Models.Timeline.IsPlaybackEnabled);
    }

    [Fact]
    public async Task ActiveAnimationAndTargetStatusControlPlaybackRatherThanVisibleTrackCount()
    {
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(Path.Combine(_temporaryDirectory, "recovery.json")),
            new EmptyDialogs(), assets);
        var animation = new ProjectAnimation
        {
            Id = Guid.NewGuid(),
            Name = "Generic motion",
            SourceAssetId = Guid.NewGuid(),
            FrameCount = 2,
        };
        SetField(viewModel, "_project", DlraProject.Create("Playback") with { Animations = [animation] });
        SetField(viewModel, "_activeAnimationId", animation.Id);
        SetTargetStatus(viewModel, TargetBindingStatus.Direct);

        Assert.True(viewModel.HasPlaybackAnimation);
        Assert.False(viewModel.NeedsPlaybackAnimation);
        Assert.Empty(viewModel.Timeline.Tracks);
        Assert.True(viewModel.NeedsPlaybackSource);
        Assert.False(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));

        PlaybackTestData.SetEvaluableSource(viewModel);
        SetTargetStatus(viewModel, TargetBindingStatus.Direct);
        Assert.True(viewModel.HasPlaybackSource);
        Assert.False(viewModel.NeedsPlaybackSource);
        Assert.True(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));
        viewModel.Timeline.IsPlaying = true;

        SetTargetStatus(viewModel, TargetBindingStatus.Invalid);
        Assert.False(viewModel.Timeline.IsPlaying);
        Assert.False(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));
        Assert.Equal("Playback locked", viewModel.Timeline.PlaybackLabel);

        SetTargetStatus(viewModel, TargetBindingStatus.Direct);
        Assert.True(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));
        SetField(viewModel, "_sourceAnimation", null);
        SetTargetStatus(viewModel, TargetBindingStatus.Direct);
        Assert.False(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));
        Assert.True(viewModel.NeedsPlaybackSource);
        SetField(viewModel, "_activeAnimationId", null);
        SetTargetStatus(viewModel, TargetBindingStatus.Direct);
        Assert.False(viewModel.Timeline.TogglePlaybackCommand.CanExecute(null));
        Assert.True(viewModel.NeedsPlaybackAnimation);
        Assert.Equal("Play", viewModel.Timeline.PlaybackLabel);
    }

    [Fact]
    public async Task RuntimeRollbackRechecksSourceAvailabilityInsteadOfRestoringAnEnabledFlag()
    {
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(Path.Combine(_temporaryDirectory, "recovery.json")),
            new EmptyDialogs(), assets);
        viewModel.Timeline.IsPlaybackEnabled = true;
        viewModel.Timeline.IsPlaying = true;
        MethodInfo capture = typeof(MainWindowViewModel).GetMethod(
            "CaptureAnimationRuntimeSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object snapshot = capture.Invoke(viewModel, null)!;
        PlaybackTestData.SetEvaluableSource(viewModel);

        typeof(MainWindowViewModel).GetMethod(
            "RestoreAnimationRuntimeSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [snapshot]);

        Assert.False(viewModel.HasPlaybackSource);
        Assert.False(viewModel.Timeline.IsPlaybackEnabled);
        Assert.False(viewModel.Timeline.IsPlaying);
        Assert.True(viewModel.NeedsPlaybackAnimation);
    }

    private static void SetField(MainWindowViewModel owner, string name, object? value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(owner, value);

    private static void SetTargetStatus(MainWindowViewModel owner, TargetBindingStatus status) =>
        typeof(MainWindowViewModel).GetMethod("SetTargetBindingStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [status]);

    private sealed class EmptyDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory)) Directory.Delete(_temporaryDirectory, recursive: true);
    }
}
