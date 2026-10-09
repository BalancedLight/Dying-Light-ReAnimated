using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceAuthoredAnimationTests
{
    [Fact]
    public async Task AuthoredFrameRateEditResamplesPreviewAndRoundTripsItsPayload()
    {
        using var workspace = CreateWorkspace(FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "timed-model.fbx"),
            new TestDialogs(new("open", 1, new FrameRate(30, 1))), static _ => Task.CompletedTask);
        await workspace.CreateAuthoredAnimationCommand.ExecuteAsync(null);
        var selected = workspace.SelectedAnimation!;
        selected.FrameRateNumerator = 24;
        Assert.Equal(new FrameRate(24, 1), selected.DecodedClip!.FrameRate);
        var payload = workspace.CreatePersistencePayload()!;
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "timed-model.dlrmodel");
            File.WriteAllBytes(path, payload.PackageBytes.ToArray());
            var decoded = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal(new FrameRate(24, 1), decoded.AnimationClips[selected.Id].FrameRate);
            Assert.Equal(1, decoded.AnimationClips[selected.Id].DurationSeconds, 6);
            Assert.Equal(25, decoded.AnimationClips[selected.Id].FrameCount);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    [Fact]
    public async Task SwitchingModelsDuringPreparationCannotChangeTheNewModelsStage()
    {
        using var workspace = CreateWorkspace(FbxHierarchyAuthoringTests.Source(), new TestDialogs(null), static _ => Task.CompletedTask);
        RigConformanceTestSchedulers.UseImmediate(workspace);
        await workspace.Conformance.ResolveTemplateCommand.ExecuteAsync(null);
        var scheduler = new HeldFitScheduler();
        workspace.Conformance.SetSolveScheduler(scheduler);
        Task preparation = workspace.PrepareSelectedAnimationCommand.ExecuteAsync(null);
        Assert.False(preparation.IsCompleted);
        var replacement = FbxHierarchyAuthoringTests.Source();
        workspace.CommitProjectRestore(new(replacement, "replacement.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        RigStudioStage stage = workspace.Conformance.StudioStage;
        scheduler.Complete();
        await preparation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(stage, workspace.Conformance.StudioStage);
        Assert.Null(workspace.Conformance.DerivedMotionPreview);
    }
    [Fact]
    public async Task PrepareAnimationReviewsASeparateClipAndSwitchesExportSelectionOnlyOnSave()
    {
        var source = FbxHierarchyAuthoringTests.Source();
        var document = source.Package.Document;
        int child = document.Bones.Single(static bone => bone.Name == "Child").Index;
        var preview = FbxRestPoseAuthoring.Preview(source,
            RiggingSessions.ObserveSourceHierarchy(document)[child].EntityId,
            FbxRestPoseAuthoringTests.ExactGlobals(document)[child] *
                ReAnimated.Core.Mathematics.TransformMatrix.CreateTranslation(new(0.2, 0.1, 0)),
            RigRestDescendantMode.KeepGlobal, RigRestSurfaceMode.PreserveSurface);
        Assert.True(FbxRestPoseAuthoring.TryApply(source, preview, out var changed));
        using var workspace = CreateWorkspace(changed, new TestDialogs(null), static _ => Task.CompletedTask);
        var original = workspace.Animations.Single();
        await workspace.PrepareSelectedAnimationCommand.ExecuteAsync(null);
        Assert.True(workspace.Conformance.DerivedMotionPreview!.CanApply);
        Assert.True(original.Included);
        Assert.Single(workspace.Animations);
        workspace.Conformance.SaveDerivedMotionCommand.Execute(null);
        var adapted = workspace.SelectedAnimation!;
        Assert.NotNull(adapted.Contract.DerivedMotion);
        Assert.True(adapted.Included);
        Assert.False(workspace.Animations.Single(clip => clip.Id == original.Id).Included);
        Assert.Equal(source.Package.SourceFbx, workspace.CaptureProjectSession().Model!.Package.SourceFbx);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Single(workspace.Animations);
        Assert.True(workspace.Animations.Single().Included);
    }
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task NewAnimationCreatesSelectsAndHandsOffAnEditableBindPoseClip()
    {
        FbxModelAuthoringImportResult source = RigConformanceWizardTests.CreateModel();
        var dialogs = new TestDialogs(new("gate-open", 2.0, new FrameRate(30_000, 1_001)));
        CustomModelAnimationHandoff? handoff = null;
        using var workspace = CreateWorkspace(source, dialogs, value =>
        {
            handoff = value;
            return Task.CompletedTask;
        });

        long revisionBefore = workspace.PersistenceRevision;
        await workspace.CreateAuthoredAnimationCommand.ExecuteAsync(null);

        CustomModelAnimationClipItemViewModel created = Assert.Single(
            workspace.Animations,
            static animation => animation.Contract.AuthoredAnimation is not null);
        Assert.Same(created, workspace.SelectedAnimation);
        Assert.Equal("Authored", created.OriginLabel);
        Assert.NotNull(created.DecodedClip);
        Assert.Equal(new FrameRate(30_000, 1_001), created.Contract.FrameRate);
        Assert.Equal(61, created.FrameCount);
        Assert.Equal(60, workspace.Timeline.EndFrame);
        Assert.True(workspace.PersistenceRevision > revisionBefore);

        int rootIndex = source.Rig!.Bones.First(static bone => bone.ParentIndex < 0).Index;
        TransformTrack rootTrack = Assert.Single(
            created.DecodedClip!.TransformTracks,
            track => track.BoneIndex == rootIndex);
        CustomModelBone root = source.Package.Document.CreateEffectiveBones()[rootIndex];
        Assert.Equal(root.LocalBindTransform, rootTrack.Keyframes[0].Value);
        Assert.Equal(root.LocalBindTransform, rootTrack.Keyframes[^1].Value);

        await workspace.OpenSelectedAnimationInAnimateCommand.ExecuteAsync(null);

        Assert.NotNull(handoff);
        Assert.Equal(created.Id, handoff.Selection.Id);
        Assert.Equal(created.DecodedClip, handoff.Clip);
        Assert.NotEmpty(handoff.PreviewMeshes);

        workspace.UndoHelperEditCommand.Execute(null);
        Assert.DoesNotContain(workspace.Animations,
            static animation => animation.Contract.AuthoredAnimation is not null);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AuthoredBindPoseClipIsAvailableToTheProjectEvaluatorBeforeItsFirstMotionKey()
    {
        FbxModelAuthoringImportResult source = RigConformanceWizardTests.CreateModel();
        using var workspace = CreateWorkspace(source,
            new TestDialogs(new("gate-open", 1.0, new FrameRate(30, 1))),
            static _ => Task.CompletedTask);
        await workspace.CreateAuthoredAnimationCommand.ExecuteAsync(null);

        ModelsWorkspacePersistencePayload payload =
            Assert.IsType<ModelsWorkspacePersistencePayload>(workspace.CreatePersistencePayload());
        var asset = new ProjectAssetReference
        {
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "Sources/generated-model.dlrmodel",
            ContentSha256 = new string('a', 64),
        };
        var entry = new ProjectModelEntry
        {
            AssetId = asset.Id,
            Name = "Generated model",
            RigSignature = payload.RuntimeRigSignature,
            AnimationSkeletonSignature = payload.AnimationSkeletonSignature,
        };
        DlraProject project = DlraProject.Create("Generated model project") with
        {
            Assets = [asset],
            Models = [entry],
        };

        DlraProject reconciled = MainWindowViewModel.ReconcileEmbeddedCustomModelStacks(
            project, asset, entry, payload);

        ProjectAnimationSource sourceAnimation = Assert.Single(reconciled.AnimationSources);
        Assert.Equal(asset.Id, sourceAnimation.SourceAssetId);
        Assert.Equal(workspace.SelectedAnimation!.Id,
            sourceAnimation.EmbeddedCustomModelStack!.ClipId);
        ProjectAnimationVariant variant = Assert.Single(reconciled.AnimationVariants);
        Assert.Equal(sourceAnimation.Id, variant.SourceId);
        Assert.Equal(entry.Id, variant.TargetModelId);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task CancelingNewAnimationLeavesTheModelUnchanged()
    {
        FbxModelAuthoringImportResult source = RigConformanceWizardTests.CreateModel();
        using var workspace = CreateWorkspace(source, new TestDialogs(null), static _ => Task.CompletedTask);
        int clipCount = workspace.Animations.Count;
        long revisionBefore = workspace.PersistenceRevision;

        await workspace.CreateAuthoredAnimationCommand.ExecuteAsync(null);

        Assert.Equal(clipCount, workspace.Animations.Count);
        Assert.Equal(revisionBefore, workspace.PersistenceRevision);
    }

    private static ModelsWorkspaceViewModel CreateWorkspace(
        FbxModelAuthoringImportResult source,
        TestDialogs dialogs,
        Func<CustomModelAnimationHandoff, Task> openInAnimate)
    {
        var workspace = new ModelsWorkspaceViewModel(
            dialogs,
            static _ => { },
            openInAnimate,
            static () => null,
            resolveRigTemplate: (profile, _) => Task.FromResult(
                RigConformanceWizardTests.CreateResolution(profile)),
            captureAuthoredLayer: static (model, _) => model);
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            source,
            "generated-model.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        return workspace;
    }

    private sealed class HeldFitScheduler : IRigConformanceSolveScheduler
    {
        private readonly List<(RigConformanceSolveRequest Request, TaskCompletionSource<RigConformanceSolveResult> Completion)> _calls = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RigConformanceSolveResult> SolveAsync(RigConformanceSolveRequest request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<RigConformanceSolveResult>();
            _calls.Add((request, completion));
            return completion.Task;
        }
        public void Complete()
        {
            foreach (var (request, completion) in _calls.ToArray()) completion.TrySetResult(request.Compute(CancellationToken.None));
        }
    }

    private sealed class TestDialogs(AuthoredAnimationDialogResult? result)
        : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;

        public AuthoredAnimationDialogResult? ShowNewAnimationDialog(string suggestedName) => result;
    }
}
