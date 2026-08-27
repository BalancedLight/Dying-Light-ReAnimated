using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

/// <summary>
/// Guards two behaviours the editor used to get wrong: batch work stealing the
/// author's workspace, and long operations running with no visible progress.
/// </summary>
public sealed class WorkspaceAndProgressTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-WorkspaceProgress-{Guid.NewGuid():N}");

    [Fact]
    public void FinishingAJobClearsTheRunningIndicator()
    {
        var job = new JobViewModel("Export", "Evaluate", "Working");

        Assert.False(job.IsFinished);

        job.Complete("Complete");

        Assert.True(job.IsFinished);
    }

    [Fact]
    public void DisposingAJobAlsoEndsIt()
    {
        // A job evicted by the 24-entry trim is disposed, not completed. The
        // indicator must not keep pointing at it.
        var job = new JobViewModel("Export", "Evaluate", "Working");

        job.Dispose();

        Assert.True(job.IsFinished);
    }

    [Fact]
    public async Task OpeningAProjectReportsLoadingAndRunsAJob()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "progress.dlraproj");
        ProjectSerializer.SaveAtomic(
            DlraProject.Create("Progress"),
            projectPath);

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new OpenOnly(projectPath),
            assets);

        Assert.False(viewModel.HasActiveJob);

        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);

        // The job is created, driven and finished, so nothing is left running.
        Assert.Contains(
            viewModel.Jobs,
            job => job.Name.Contains(
                "progress.dlraproj",
                StringComparison.Ordinal));
        Assert.All(viewModel.Jobs, static job => Assert.True(job.IsFinished));
        Assert.False(viewModel.HasActiveJob);
    }

    [Fact]
    public async Task OpeningAProjectKeepsTheWorkspaceItWasSavedWith()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "workspace.dlraproj");
        ProjectSerializer.SaveAtomic(
            DlraProject.Create("Workspace"),
            projectPath);

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "ws-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "ws-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "ws-state.json")),
            new OpenOnly(projectPath),
            assets);

        viewModel.SelectWorkspaceCommand.Execute("Export");
        Assert.True(viewModel.IsExportWorkspace);

        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);

        // Opening restores the workspace stored in the project rather than
        // being dragged to Playback by the activation that follows.
        Assert.Equal(
            ProjectWorkflowTab.Models.ToString(),
            viewModel.CurrentProject.Workflow.ActiveTab.ToString());
    }

    [Fact]
    public void RenamingAnAnimationCarriesADerivedOutputNameWithIt()
    {
        // Mixamo FBX takes are all called "mixamo.com", so every variant made
        // from one starts with the same derived output name. Renaming the row
        // has to move the output too or the pack refuses both rows.
        // Lowercase: the type-322 name table is lowercase-only, so an
        // exported identity has to be too or its script cannot name it.
        Assert.Equal(
            "thriller_player.anm2",
            MainWindowViewModel.DeriveFollowingOutputName(
                "mixamo_com.anm2",
                previousAnimationName: "Thriller",
                sourceName: "mixamo.com",
                newAnimationName: "Thriller - Player"));
    }

    [Fact]
    public void RenamingLeavesAnAuthoredOutputNameAlone()
    {
        // The author typed this one; a rename must not overwrite it.
        Assert.Null(
            MainWindowViewModel.DeriveFollowingOutputName(
                "my_chosen_name.anm2",
                previousAnimationName: "Thriller",
                sourceName: "mixamo.com",
                newAnimationName: "Thriller - Player"));
    }

    [Fact]
    public void AnOutputNameDerivedFromThePreviousNameAlsoFollows()
    {
        Assert.Equal(
            "sprint.anm2",
            MainWindowViewModel.DeriveFollowingOutputName(
                "Walk.anm2",
                previousAnimationName: "Walk",
                sourceName: "take_001",
                newAnimationName: "Sprint"));
    }

    [Fact]
    public void ARenameThatChangesNothingProducesNoOutputChange()
    {
        Assert.Null(
            MainWindowViewModel.DeriveFollowingOutputName(
                "walk.anm2",
                previousAnimationName: "Walk",
                sourceName: "take_001",
                newAnimationName: "Walk"));
    }

    [Fact]
    public void ExportedIdentitiesAreLowercased()
    {
        // Renaming to mixed case still yields a referenceable identity.
        Assert.Equal(
            "thriller_player.anm2",
            MainWindowViewModel.DeriveFollowingOutputName(
                "mixamo_com.anm2",
                previousAnimationName: "Take",
                sourceName: "mixamo.com",
                newAnimationName: "Thriller Player"));
    }

    [Fact]
    public void ASnapshotFromThePreviousSchemaIsStillRecoverable()
    {
        // KeepFramed was added as optional, so schema 2 stays readable.
        // Rejecting it discarded a recoverable session and let the next
        // autosave overwrite it.
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(_temporaryDirectory, "snapshot.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 2,
              "savedAt": "2026-08-01T00:00:00+00:00",
              "projectPath": null,
              "assetSearch": "",
              "selectedAssetId": null,
              "selectedBonePath": null,
              "currentFrame": 0,
              "viewportsLinked": true,
              "fppFieldOfView": 60,
              "fppNearPlane": 0.05,
              "activeWorkspaceMode": "Models"
            }
            """);

        WorkspaceSnapshot? snapshot =
            new JsonWorkspaceStateStore(path).Load();

        // What matters is that it loads at all; the loader normalises the
        // version and leaves the field this schema never carried unset.
        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.KeepFramed);
        Assert.Equal("Models", snapshot.ActiveWorkspaceMode);
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

    private sealed class OpenOnly(string projectPath)
        : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            projectPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => projectPath;
    }
}
