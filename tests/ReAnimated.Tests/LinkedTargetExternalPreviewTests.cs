using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class LinkedTargetExternalPreviewTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-LinkedTargetPreview-{Guid.NewGuid():N}");

    [Fact]
    public void EvaluatedCameraLocksOnlyTargetGizmo()
    {
        LinkedViewportCoordinator coordinator = new();
        ViewportSceneSource source = new(
            coordinator,
            ViewportSide.Source,
            Vector4.Zero);
        ViewportSceneSource target = new(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        var sourceGizmo = new AcceptingTransformGizmoTarget();
        var targetGizmo = new AcceptingTransformGizmoTarget();
        source.SetTransformGizmoTarget(sourceGizmo);
        target.SetTransformGizmoTarget(targetGizmo);
        coordinator.SetTargetPreviewCameraOverride(
            RenderCamera.Default);
        var start = new RenderTransformGizmoDragStart(
            new RenderTransformGizmoBinding(
                0,
                RenderTransformGizmoMode.Translate,
                RenderTransformGizmoAxis.X,
                RenderGizmoSpace.Global),
            Vector3.UnitX);

        Assert.True(source.TryBeginTransformGizmoDrag(start));
        Assert.False(target.TryBeginTransformGizmoDrag(start));
        Assert.Equal(1, sourceGizmo.BeginCount);
        Assert.Equal(0, targetGizmo.BeginCount);
    }

    [Theory]
    [InlineData("FPP")]
    [InlineData("Cutscene")]
    public async Task LinkedPreviewMirrorsEvaluatedTargetAndRestoresAuthoredSource(
        string workspaceMode)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                $"{workspaceMode}-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                $"{workspaceMode}-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(
                    _temporaryDirectory,
                    $"{workspaceMode}-workspace.json")),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    $"{workspaceMode}.dlraproj")),
            assets);

        MeshRenderData authoredMesh = CreateMesh(
            "authored-source",
            MeshProjectionRole.Scene,
            isSelected: false);
        SkeletonRenderData authoredSkeleton = CreateSkeleton(
            "AuthoredRoot",
            Matrix4x4.CreateTranslation(2.0f, 3.0f, 4.0f),
            isSelected: false);
        GizmoRenderData authoredGizmo = CreateGizmo(
            new Vector3(2.0f, 3.0f, 4.0f));
        MorphWeight[] authoredMorphs =
        [
            new MorphWeight("authored-smile", 0.25f),
        ];
        var authoredProjection = new RenderFppProjectionState(
            RouteHandsMeshes: false,
            SceneAspectRatio: 1.5f,
            HandsProjection: null);
        viewModel.SetSourcePreviewScene(
            [authoredMesh],
            authoredSkeleton,
            [authoredGizmo]);
        viewModel.SourceViewport.SceneSource.SetMorphWeights(
            authoredMorphs);
        viewModel.SourceViewport.SceneSource.SetFppProjectionState(
            authoredProjection);
        viewModel.ShowBoneLocalAxes = true;
        RenderFrameSnapshot authoredBefore =
            viewModel.SourceViewport.SceneSource.CaptureFrame();

        viewModel.ActiveWorkspaceMode = workspaceMode;
        viewModel.FacialFpp.UseFppCamera = true;
        MeshRenderData targetBody = CreateMesh(
            "evaluated-target-body",
            MeshProjectionRole.Scene,
            isSelected: true);
        MeshRenderData targetAttachment = CreateMesh(
            "evaluated-target-attachment",
            MeshProjectionRole.FppHands,
            isSelected: false);
        SkeletonRenderData evaluatedSkeleton = CreateSkeleton(
            "EvaluatedRoot",
            Matrix4x4.CreateTranslation(7.0f, 8.0f, 9.0f),
            isSelected: true);
        GizmoRenderData evaluatedGizmo = CreateGizmo(
            new Vector3(7.0f, 8.0f, 9.0f));
        MorphWeight[] evaluatedMorphs =
        [
            new MorphWeight("evaluated-snarl", 0.75f),
        ];
        viewModel.TargetViewport.SceneSource.SetScene(
            [targetBody, targetAttachment],
            evaluatedSkeleton,
            [evaluatedGizmo],
            evaluatedMorphs);
        EvaluationFrame evaluatedFrame = CreateFrame(
            workspaceMode,
            evaluatedSkeleton);

        viewModel.ApplyEvaluatedPreviewCamera(evaluatedFrame);

        RenderFrameSnapshot external =
            viewModel.SourceViewport.SceneSource.CaptureFrame();
        RenderFrameSnapshot target =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.True(
            viewModel.SourceViewport.SceneSource
                .HasExternalPreviewScene);
        Assert.Equal(
            target.Meshes.Select(static mesh => mesh.Id),
            external.Meshes.Select(static mesh => mesh.Id));
        Assert.Equal(
            target.Meshes.Select(static mesh => mesh.IsSelected),
            external.Meshes.Select(static mesh => mesh.IsSelected));
        Assert.Equal(
            target.Skeleton!.Bones,
            external.Skeleton!.Bones);
        Assert.Equal(target.Gizmos, external.Gizmos);
        Assert.Equal(target.MorphWeights, external.MorphWeights);
        Assert.Null(external.FppProjectionState);
        Assert.NotEqual(target.Camera, external.Camera);
        Assert.Equal(
            RenderCameraNavigationResult.Applied,
            viewModel.SourceViewport.SceneSource.NavigateCamera(
                RenderCameraNavigationInput.Pan(
                    20.0f,
                    -10.0f,
                    800,
                    600)));
        Assert.Equal(
            RenderCameraNavigationResult.PreviewCameraLocked,
            viewModel.TargetViewport.SceneSource.NavigateCamera(
                RenderCameraNavigationInput.Orbit(
                    20.0f,
                    -10.0f,
                    800,
                    600)));
        Assert.Equal(
            "DL1 Target / External",
            viewModel.SourceViewport.Title);
        Assert.Contains(
            "Same evaluated target",
            viewModel.SourceViewport.FidelityLabel,
            StringComparison.Ordinal);
        if (workspaceMode == "FPP")
        {
            Assert.NotNull(target.FppProjectionState);
            Assert.NotNull(
                target.FppProjectionState!.HandsProjection);
            Assert.Contains(
                "hands projection disabled",
                viewModel.SourceViewport.FidelityLabel,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "DL1 Target / EyeCamera",
                viewModel.TargetViewport.Title);
        }
        else
        {
            Assert.Null(target.FppProjectionState);
            Assert.Equal(
                "DL1 Target / Movie Camera",
                viewModel.TargetViewport.Title);
        }

        var keyedMorph = new MorphChannelViewModel(
            "manual-preview-morph")
        {
            Weight = 0.6f,
        };
        viewModel.FacialFpp.ReplaceMorphs([keyedMorph]);

        Assert.Equal(
            viewModel.TargetViewport.SceneSource
                .CaptureFrame()
                .MorphWeights,
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .MorphWeights);
        MorphWeight mirroredMorph = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .MorphWeights);
        Assert.Equal("manual-preview-morph", mirroredMorph.Name);
        Assert.Equal(0.6f, mirroredMorph.Weight);

        RenderCamera evaluatedCameraBeforeLayoutChange =
            viewModel.TargetViewport.SceneSource.CaptureFrame().Camera;

        viewModel.ActiveWorkspaceMode = "Retarget";

        RenderFrameSnapshot restored =
            viewModel.SourceViewport.SceneSource.CaptureFrame();
        Assert.False(
            viewModel.SourceViewport.SceneSource
                .HasExternalPreviewScene);
        Assert.Equal(
            authoredBefore.Meshes.Select(static mesh => mesh.Id),
            restored.Meshes.Select(static mesh => mesh.Id));
        Assert.Equal(
            authoredBefore.Skeleton!.Bones,
            restored.Skeleton!.Bones);
        Assert.Equal(authoredBefore.Gizmos, restored.Gizmos);
        Assert.Equal(
            authoredBefore.MorphWeights,
            restored.MorphWeights);
        Assert.Equal(
            authoredProjection,
            restored.FppProjectionState);
        Assert.Equal(
            authoredBefore.AuthoringOverlays.Options,
            restored.AuthoringOverlays.Options);
        Assert.Equal("Source / Authored", viewModel.SourceViewport.Title);
        Assert.Equal("DL1 Target", viewModel.TargetViewport.Title);
        RenderFrameSnapshot targetOrbit =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.NotEqual(
            evaluatedCameraBeforeLayoutChange,
            targetOrbit.Camera);
        Assert.Null(targetOrbit.FppProjectionState);
        Assert.Equal(
            RenderCameraNavigationResult.Applied,
            viewModel.TargetViewport.SceneSource.NavigateCamera(
                RenderCameraNavigationInput.Pan(
                    6.0f,
                    -3.0f,
                    800,
                    600)));

        viewModel.ActiveWorkspaceMode = workspaceMode;

        RenderFrameSnapshot restoredTargetCamera =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.True(
            viewModel.SourceViewport.SceneSource
                .HasExternalPreviewScene);
        Assert.Equal(
            evaluatedCameraBeforeLayoutChange,
            restoredTargetCamera.Camera);
        Assert.Equal(
            RenderCameraNavigationResult.PreviewCameraLocked,
            viewModel.TargetViewport.SceneSource.NavigateCamera(
                RenderCameraNavigationInput.Orbit(
                    6.0f,
                    -3.0f,
                    800,
                    600)));
        if (workspaceMode == "FPP")
        {
            Assert.NotNull(restoredTargetCamera.FppProjectionState);
        }
        else
        {
            Assert.Null(restoredTargetCamera.FppProjectionState);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(
                _temporaryDirectory,
                recursive: true);
        }
    }

    private static EvaluationFrame CreateFrame(
        string workspaceMode,
        SkeletonRenderData skeleton)
    {
        var rig = new RigDefinition(
            "linked-target-rig",
            "Linked target rig",
            [
                new BoneDefinition(
                    0,
                    skeleton.Bones[0].Name,
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
            ]);
        SkeletonPose pose = rig.CreateBindPose();
        bool isFpp = workspaceMode == "FPP";
        var lens = new CameraLens(
            68.0,
            16.0 / 9.0,
            0.02,
            800.0);
        Dl1ProjectionParameters? handsProjection = isFpp
            ? new Dl1ProjectionParameters(
                72.0,
                Dl1ProjectionFovAxis.Horizontal,
                16.0 / 9.0,
                0.01,
                Dl1ProjectionFarPlane.Infinite)
            : null;
        var camera = new EvaluatedCamera(
            TransformMatrix.CreateTranslation(
                new Vector3D(4.0, 5.0, 6.0)),
            lens,
            isFpp,
            isFpp
                ? EvaluatedCameraSource.Dl1FppEyeCamera
                : EvaluatedCameraSource.Dl1MovieReferenceCamera,
            handsProjection);
        return new EvaluationFrame(
            0.0,
            pose,
            pose,
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty,
            isFpp
                ? PreviewProfile.FirstPersonAuthoring
                : PreviewProfile.MovieAuthoring,
            camera,
            [],
            [],
            null,
            [],
            isFpp
                ?
                [
                    new Dl1PreviewStageReport(
                        Dl1PreviewStageIds.FppViewTransform,
                        true,
                        Dl1PreviewStageStatus.Fallback,
                        "Using evaluated EyeCamera."),
                    new Dl1PreviewStageReport(
                        Dl1PreviewStageIds.FppSceneProjection,
                        true,
                        Dl1PreviewStageStatus.Applied,
                        "Using captured scene projection."),
                    new Dl1PreviewStageReport(
                        Dl1PreviewStageIds.FppHandsProjection,
                        true,
                        Dl1PreviewStageStatus.Applied,
                        "Using captured hands projection."),
                ]
                :
                [
                    new Dl1PreviewStageReport(
                        Dl1PreviewStageIds.MovieReferenceCamera,
                        true,
                        Dl1PreviewStageStatus.Applied,
                        "Using explicit external movie camera."),
                ]);
    }

    private static MeshRenderData CreateMesh(
        string id,
        MeshProjectionRole projectionRole,
        bool isSelected) =>
        new(
            id,
            ReadOnlyMemory<MeshVertex>.Empty,
            ReadOnlyMemory<uint>.Empty,
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            false)
        {
            ProjectionRole = projectionRole,
            IsSelected = isSelected,
        };

    private static SkeletonRenderData CreateSkeleton(
        string boneName,
        Matrix4x4 worldTransform,
        bool isSelected) =>
        new(
            [
                new BoneRenderData(
                    boneName,
                    -1,
                    worldTransform,
                    worldTransform,
                    isSelected),
            ],
            Matrix4x4.Identity);

    private static GizmoRenderData CreateGizmo(Vector3 start) =>
        new(
            GizmoKind.Axis,
            start,
            start + Vector3.UnitX,
            Vector4.One,
            2.0f);

    private sealed class TestProjectFileDialogs(
        string projectPath) : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            projectPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;

        public string? ShowSelectAdditionalRpackRootDialog(
            string? initialPath) =>
            null;
    }

    private sealed class AcceptingTransformGizmoTarget :
        IRenderTransformGizmoTarget
    {
        public int BeginCount { get; private set; }

        public bool TryBeginTransformGizmoDrag(
            RenderTransformGizmoDragStart start)
        {
            BeginCount++;
            return true;
        }

        public bool UpdateTransformGizmoDrag(
            RenderTransformGizmoDragUpdate update) =>
            true;

        public void CompleteTransformGizmoDrag(bool commit)
        {
        }
    }
}
