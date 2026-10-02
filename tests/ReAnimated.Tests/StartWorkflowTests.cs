using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class StartWorkflowTests
{
    [Theory]
    [InlineData(StartWorkflowMode.ModelsOnly, false, false, true)]
    [InlineData(StartWorkflowMode.ModelsOnly, true, false, false)]
    [InlineData(StartWorkflowMode.ModelsOnly, false, true, false)]
    [InlineData(StartWorkflowMode.AnimationsOnly, false, false, false)]
    [InlineData(StartWorkflowMode.ModelsAndAnimations, false, false, false)]
    public void GuidedImportEntryRequiresModelsOnlyAndAnEmptyProject(
        StartWorkflowMode mode,
        bool projectHasModels,
        bool workspaceHasModel,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindowViewModel.ShouldOpenGuidedModelImport(
                mode,
                projectHasModels,
                workspaceHasModel));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ReturningFromPlaybackKeepsTheOpenModelSetupAndExplicitBrowserChoice()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ReAnimated-Navigation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "assets.sqlite3"), Path.Combine(directory, "cache"));
            await using var viewModel = new MainWindowViewModel(new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json")),
                new HeadlessDialogs(), assets, new NullFingerprintService());
            viewModel.OpenCustomModelAuthoringCommand.Execute(null);
            Assert.True(viewModel.IsCustomModelAuthoringSurfaceVisible);

            viewModel.SelectWorkspaceCommand.Execute("Playback");
            Assert.False(viewModel.IsCustomModelAuthoringSurfaceVisible);
            viewModel.SelectWorkspaceCommand.Execute("Models");
            Assert.True(viewModel.IsCustomModelAuthoringSurfaceVisible);

            viewModel.Models.ReturnToProjectModelsCommand.Execute(null);
            Assert.True(viewModel.IsRetailModelBrowserSurfaceVisible);
            viewModel.SelectWorkspaceCommand.Execute("Playback");
            viewModel.SelectWorkspaceCommand.Execute("Models");
            Assert.True(viewModel.IsRetailModelBrowserSurfaceVisible);
            Assert.False(viewModel.IsCustomModelAuthoringSurfaceVisible);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ModelsOnlyDoesNotReturnToModelsAfterLaterBusyCompletion()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-StartWorkflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using var assets = new Dl1AssetWorkspace(
                Path.Combine(directory, "assets.sqlite3"),
                Path.Combine(directory, "cache"));
            await using var viewModel = new MainWindowViewModel(
                new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json")),
                new HeadlessDialogs(),
                assets,
                new NullFingerprintService());

            var preferenceStore = new StartWorkflowSettingsStore(
                Path.Combine(directory, "workflow.json"));
            preferenceStore.Save(StartWorkflowMode.ModelsOnly);
            viewModel.InitializeStartWorkflow(preferenceStore);

            Guid assetId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            Guid packageModelId = Guid.NewGuid();
            var modelAsset = new ProjectAssetReference
            {
                Id = assetId,
                Kind = ProjectAssetKind.CustomModelSource,
                RelativePath = "Sources/existing-model.dlrmodel",
                ResourceId = $"custom-model:{packageModelId:N}:existing-model",
            };
            DlraProject project = DlraProject.Create("Existing project") with
            {
                Assets = [modelAsset],
                Models =
                [
                    new ProjectModelEntry
                    {
                        Id = modelId,
                        AssetId = assetId,
                        Name = "Existing model",
                        IsStatic = true,
                    },
                ],
            };
            string projectPath = Path.Combine(directory, "existing.dlraproj");
            viewModel.RestoreSnapshot(new WorkspaceSnapshot(
                WorkspaceSnapshot.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                projectPath,
                string.Empty,
                null,
                null,
                0,
                false,
                70,
                0.1f,
                "Models",
                project));

            viewModel.ApplyStartWorkflowAfterProjectOpen();
            Assert.Equal(EditorWorkspaceMode.Models, viewModel.ActiveWorkspace);

            // Saving toggles the view model's IsBusy state. It must not reapply
            // the startup preference after the user has moved to Playback.
            viewModel.ActiveWorkspace = EditorWorkspaceMode.Playback;
            await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

            Assert.Equal(EditorWorkspaceMode.Playback, viewModel.ActiveWorkspace);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(StartWorkflowMode.AnimationsOnly)]
    [InlineData(StartWorkflowMode.ModelsOnly)]
    [InlineData(StartWorkflowMode.ModelsAndAnimations)]
    public void WorkflowPreferenceRoundTrips(StartWorkflowMode mode)
    {
        string path = Path.Combine(Path.GetTempPath(), "reanimated-workflow-tests", Guid.NewGuid().ToString("N"), "workflow.json");
        try
        {
            var store = new StartWorkflowSettingsStore(path);
            Assert.Null(store.Load());
            store.Save(mode);
            Assert.Equal(mode, store.Load());
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HeadlessAnimationWorkspaceOfferDefaultsToNo()
    {
        Assert.False(new HeadlessDialogs().ConfirmEnableAnimationWorkspace("Imported model"));
    }

    [Fact]
    public void MovementDetectionRequiresActualChangesAcrossFrames()
    {
        AnimationClip still = new(
            "still",
            new FrameRate(30, 1),
            2,
            [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity), new TransformKeyframe(1, TransformTRS.Identity)])]);
        AnimationClip changing = new(
            "moving",
            new FrameRate(30, 1),
            2,
            [new TransformTrack(0, [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(1, TransformTRS.Identity with { Translation = new Vector3D(0.02, 0, 0) }),
            ])]);
        AnimationClip oneFrame = new(
            "pose",
            new FrameRate(30, 1),
            1,
            [new TransformTrack(0, [new TransformKeyframe(0, TransformTRS.Identity)])]);

        Assert.False(MainWindowViewModel.AnimationContainsTemporalMovement([still, oneFrame]));
        Assert.True(MainWindowViewModel.AnimationContainsTemporalMovement([still, oneFrame, changing]));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void ExplorerSourceMustCoverExactTypedTrackDescriptorsBeforeFramePartition()
    {
        var matchingSource = new RigDefinition("generic-source", "Generic source", [
            new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root, descriptorHash: 0x101),
            new BoneDefinition(1, "spine", 0, TransformTRS.Identity, BoneKind.Deform, descriptorHash: 0x102),
        ]);
        ExplorerSourceDescriptorCoverage sufficient = MainWindowViewModel.AnalyzeExplorerSourceDescriptorCoverage(
            [0x101, 0x102, 0x999], matchingSource);
        Assert.True(sufficient.IsSufficient);
        Assert.Equal(2, sufficient.BodyDescriptors.Length);
        Assert.Single(sufficient.UnresolvedDescriptors);

        var unrelatedSource = new RigDefinition("unrelated-source", "Unrelated source", [
            new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root, descriptorHash: 0x201),
            new BoneDefinition(1, "spine", 0, TransformTRS.Identity, BoneKind.Deform, descriptorHash: 0x202),
        ]);
        ExplorerSourceDescriptorCoverage incompatible = MainWindowViewModel.AnalyzeExplorerSourceDescriptorCoverage(
            [0x101, 0x102, 0x999], unrelatedSource);
        Assert.False(incompatible.IsSufficient);
        Assert.Contains("0/3 exact body descriptors", incompatible.Describe("Synthetic clip"), StringComparison.Ordinal);
        Assert.Contains("source", incompatible.Describe("Synthetic clip"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", incompatible.Describe("Synthetic clip"), StringComparison.OrdinalIgnoreCase);

        ExplorerSourceDescriptorCoverage duplicate = MainWindowViewModel.AnalyzeExplorerSourceDescriptorCoverage(
            [0x101, 0x101, 0x102], matchingSource);
        Assert.False(duplicate.IsSufficient);
        Assert.Contains(0x101u, duplicate.AmbiguousDescriptors);
    }

    private sealed class HeadlessDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    private sealed class NullFingerprintService : IDl1InstalledBuildFingerprintService
    {
        public Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Dl1InstalledBuildFingerprint?>(null);

        public Task<Dl1InstalledBuildFingerprint> ReadAsync(
            string installPath,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Dl1InstalledBuildFingerprint>(new FileNotFoundException());
    }
}
