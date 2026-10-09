using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceConformancePreviewOwnershipTests
{
    [Fact]
    public void FitJointSelectionPublishesDraggableAxesAndResetRestoresPlacement()
    {
        var model = RigConformanceWizardTests.CreateModel();
        using var workspace = new ModelsWorkspaceViewModel(
            new NoDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null,
            resolveRigTemplate: (profile, _) =>
                Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)));
        RigConformanceTestSchedulers.UseImmediate(workspace);
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            model,
            "generic-model.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        workspace.Conformance.ResolveTemplateCommand.Execute(null);
        workspace.Conformance.StudioStage = RigStudioStage.Fit;
        workspace.Conformance.Stage = RigConformanceStage.Refine;
        workspace.Conformance.MirrorEdits = false;
        workspace.Conformance.SelectedLandmark = workspace.Conformance.Landmarks.Single(
            row => row.BoneName == "pelvis");

        RenderFrameSnapshot frame = workspace.Viewport.SceneSource.CaptureFrame();
        int selected = frame.Skeleton!.Bones.ToList().FindIndex(static bone => bone.IsSelected);
        Assert.True(selected >= 0);
        GizmoRenderData[] handles = frame.Gizmos.Where(static gizmo =>
            gizmo.Kind == GizmoKind.TranslationHandle).ToArray();
        Assert.Equal(3, handles.Length);
        Assert.All(handles, handle =>
            Assert.Equal(selected, handle.TranslationBinding!.Value.BoneIndex));

        float fullDistance = Vector3.Distance(frame.Camera.Eye, frame.Camera.Target);
        Vector3 jointPosition =
            (frame.Skeleton.Bones[selected].WorldTransform *
             frame.Skeleton.RootTransform).Translation;
        workspace.FrameSelectedConformanceJointCommand.Execute(null);
        RenderCamera jointCamera = workspace.Viewport.SceneSource.CaptureFrame().Camera;
        Assert.True(Vector3.Distance(jointCamera.Eye, jointCamera.Target) < fullDistance);
        Assert.True(Vector3.Distance(jointCamera.Target, jointPosition) < 0.001f);

        Vector3D before = workspace.Conformance.TryGetBonePosition("pelvis")!.Value;
        TranslationGizmoBinding binding = handles.Single(handle =>
            handle.TranslationBinding!.Value.Axis == TranslationGizmoAxis.Y)
            .TranslationBinding!.Value;
        ViewportSceneSource target = workspace.Viewport.SceneSource;
        Assert.True(target.TryBeginTranslationGizmoDrag(
            new RenderTranslationGizmoDragStart(binding, Vector3.UnitY)));
        Assert.True(target.UpdateTranslationGizmoDrag(
            new RenderTranslationGizmoDragUpdate(binding, new Vector3(0, .04f, 0), .04f)));
        target.CompleteTranslationGizmoDrag(commit: true);
        Assert.True(workspace.Conformance.HasOverride("pelvis"));
        Assert.NotEqual(before, workspace.Conformance.TryGetBonePosition("pelvis")!.Value);

        workspace.Conformance.ResetBoneCommand.Execute(null);
        Assert.False(workspace.Conformance.HasOverride("pelvis"));
        Assert.Equal(before, workspace.Conformance.TryGetBonePosition("pelvis")!.Value);
    }

    [Fact]
    public void FittedMeshesKeepTheirSkeletonAcrossOrdinaryRefreshAndTabChanges()
    {
        var model = RigConformanceWizardTests.CreateModel();
        using var workspace = new ModelsWorkspaceViewModel(
            new NoDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null,
            resolveRigTemplate: (profile, _) =>
                Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)));
        RigConformanceTestSchedulers.UseImmediate(workspace);
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            model,
            "generic-model.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));

        RenderFrameSnapshot source = workspace.Viewport.SceneSource.CaptureFrame();
        AssertValidPair(source);
        int sourceRows = source.Skeleton!.Bones.Count;

        workspace.IsConformTabSelected = true;
        workspace.Conformance.ResolveTemplateCommand.Execute(null);
        workspace.Conformance.StudioStage = RigStudioStage.Fit;
        RenderFrameSnapshot fitted = workspace.Viewport.SceneSource.CaptureFrame();
        AssertValidPair(fitted);
        int fittedRows = fitted.Skeleton!.Bones.Count;
        Assert.NotEqual(sourceRows, fittedRows);

        // Bone selection requests an ordinary preview refresh. It must not
        // attach the imported skeleton to the fitted model's remapped mesh.
        workspace.SelectedBone = workspace.Bones.First(bone =>
            !ReferenceEquals(bone, workspace.SelectedBone));
        RenderFrameSnapshot refreshedFit = workspace.Viewport.SceneSource.CaptureFrame();
        AssertValidPair(refreshedFit);
        Assert.Equal(fittedRows, refreshedFit.Skeleton!.Bones.Count);

        workspace.IsConformTabSelected = false;
        RenderFrameSnapshot restoredSource = workspace.Viewport.SceneSource.CaptureFrame();
        AssertValidPair(restoredSource);
        Assert.Equal(sourceRows, restoredSource.Skeleton!.Bones.Count);

        workspace.IsConformTabSelected = true;
        RenderFrameSnapshot restoredFit = workspace.Viewport.SceneSource.CaptureFrame();
        AssertValidPair(restoredFit);
        Assert.Equal(fittedRows, restoredFit.Skeleton!.Bones.Count);
    }

    [Fact]
    public void ConformedModelStartsAtRestAndLabelsOriginalFbxMotionAsUnadapted()
    {
        FbxModelAuthoringImportResult source = RigConformanceWizardTests.CreateModel();
        Guid clipId = Guid.NewGuid();
        var selection = new CustomModelAnimationClip
        {
            Id = clipId,
            FbxObjectId = 1,
            SourceName = "source_walk",
            DisplayName = "Source walk",
            FrameRate = new FrameRate(30, 1),
            FrameCount = 2,
            SourceFingerprint = new string('a', 64),
            HasSkeletalTracks = true,
        };
        var sourceClip = new AnimationClip(
            selection.DisplayName,
            selection.FrameRate,
            selection.FrameCount,
            [new TransformTrack(0,
            [
                new TransformKeyframe(0, TransformTRS.Identity),
                new TransformKeyframe(1, TransformTRS.Identity),
            ])]);
        source = source with
        {
            Package = source.Package with
            {
                Document = source.Package.Document with { AnimationClips = [selection] },
            },
            AnimationClips = ImmutableDictionary<Guid, AnimationClip>.Empty.Add(clipId, sourceClip),
        };

        using var workspace = new ModelsWorkspaceViewModel(
            new NoDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null,
            resolveRigTemplate: (profile, _) =>
                Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)));
        RigConformanceTestSchedulers.UseImmediate(workspace);
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            source,
            "generic-source.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        workspace.Conformance.ResolveTemplateCommand.Execute(null);
        Assert.NotNull(workspace.Conformance.Fit);

        FbxModelAuthoringImportResult conformed = Dl1RigConformanceApplier.Apply(
            source,
            workspace.Conformance.Fit!,
            workspace.Conformance.CreateSettings());
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            conformed,
            "generic-conformed.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));

        Assert.Null(workspace.SelectedAnimation);
        Assert.Single(workspace.Animations);
        Assert.NotNull(workspace.Viewport.SceneSource.CaptureFrame().Skeleton);

        workspace.SelectedAnimation = workspace.Animations.Single();
        workspace.Timeline.CurrentFrame = 1;
        Assert.Contains("Unadapted FBX source clip", workspace.Viewport.FidelityLabel);
        Assert.Contains("reindexed but not retargeted", workspace.Viewport.DiagnosticOverlay);
    }

    [Fact]
    public async Task LatePreparedFitPreviewCannotReplaceTheCurrentFit()
    {
        FbxModelAuthoringImportResult model = RigConformanceWizardTests.CreateModel();
        var scheduler = new DeferredConformancePreviewScheduler();
        using var workspace = new ModelsWorkspaceViewModel(
            new NoDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null,
            resolveRigTemplate: (profile, _) =>
                Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)));
        workspace.ConformancePreviewPreparationScheduler = scheduler;
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            model,
            "generated-model.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        workspace.Conformance.StudioStage = RigStudioStage.Fit;

        await workspace.Conformance.ResolveTemplateCommand.ExecuteAsync(null);
        Task oldPreparation = workspace.ConformancePreviewPreparationTask;
        Assert.Single(scheduler.Calls);
        DeferredConformancePreviewScheduler.Call oldCall = scheduler.Calls[0];
        RenderFrameSnapshot sourceFrame = workspace.Viewport.SceneSource.CaptureFrame();

        var currentFitPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFitChanged(object? sender, EventArgs args) => currentFitPublished.TrySetResult();
        workspace.Conformance.FitChanged += OnFitChanged;
        try
        {
            workspace.Conformance.ConformanceStrength = 0.75;
            await currentFitPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            workspace.Conformance.FitChanged -= OnFitChanged;
        }

        Task currentPreparation = workspace.ConformancePreviewPreparationTask;
        Assert.Equal(2, scheduler.Calls.Count);
        DeferredConformancePreviewScheduler.Call currentCall = scheduler.Calls[1];
        DeferredConformancePreviewScheduler.Complete(currentCall);
        await currentPreparation.WaitAsync(TimeSpan.FromSeconds(10));
        RenderFrameSnapshot currentFrame = workspace.Viewport.SceneSource.CaptureFrame();
        Assert.NotNull(currentFrame.Skeleton);
        Assert.False(sourceFrame.Skeleton!.Bones.SequenceEqual(currentFrame.Skeleton!.Bones));

        DeferredConformancePreviewScheduler.Complete(oldCall);
        await oldPreparation.WaitAsync(TimeSpan.FromSeconds(10));
        RenderFrameSnapshot afterLateCompletion = workspace.Viewport.SceneSource.CaptureFrame();
        Assert.Equal(currentFrame.Skeleton.Bones, afterLateCompletion.Skeleton!.Bones);
    }

    private static void AssertValidPair(RenderFrameSnapshot frame)
    {
        Assert.NotNull(frame.Skeleton);
        Assert.NotEmpty(frame.Meshes);
        foreach (MeshRenderData mesh in frame.Meshes)
        {
            Assert.True(
                RenderMeshValidation.TryValidate(mesh, frame.Skeleton, out string? error),
                error);
        }
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    private sealed class DeferredConformancePreviewScheduler : IConformancePreviewPreparationScheduler
    {
        public sealed record Call(Func<CancellationToken, PreparedConformancePreview> Prepare,
            TaskCompletionSource<PreparedConformancePreview> Completion);

        public List<Call> Calls { get; } = [];

        public Task<PreparedConformancePreview> PrepareAsync(
            Func<CancellationToken, PreparedConformancePreview> prepare,
            CancellationToken cancellationToken)
        {
            var call = new Call(prepare, new TaskCompletionSource<PreparedConformancePreview>());
            Calls.Add(call);
            return call.Completion.Task;
        }

        public static void Complete(Call call) =>
            call.Completion.TrySetResult(call.Prepare(CancellationToken.None));
    }
}
