using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceConformanceViewportTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task BackToGuidedAdjustReleasesFitCameraForGizmoAndRestoresSessionCameraOnExit()
    {
        using ModelsWorkspaceViewModel workspace = CreateWorkspaceWithPreviewCamera();
        ViewportSceneSource scene = workspace.Viewport.SceneSource;
        var probeTarget = new RecordingTranslationGizmoTarget();
        scene.SetTranslationGizmoTarget(probeTarget);
        var arbitraryBinding = new TranslationGizmoBinding(
            0,
            TranslationGizmoAxis.X,
            RenderGizmoSpace.Global);
        var arbitraryStart = new RenderTranslationGizmoDragStart(
            arbitraryBinding,
            Vector3.UnitX);

        Assert.False(scene.TryBeginTranslationGizmoDrag(arbitraryStart));
        Assert.Equal(0, probeTarget.BeginCount);

        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        workspace.GuidedShowJoints = true;

        Assert.True(workspace.IsGuidedAdjust);
        Assert.True(workspace.ShowGuidedJointControls);
        Assert.Equal(RigStudioStage.Fit, workspace.Conformance.StudioStage);
        Assert.Equal(RigConformanceStage.Refine, workspace.Conformance.Stage);
        Assert.True(workspace.Conformance.CanApplyGuidedFit, workspace.Conformance.GuidedFitBlockReason);
        scene.SetTranslationGizmoTarget(probeTarget);
        bool modelViewportAllowsDrag = scene.TryBeginTranslationGizmoDrag(arbitraryStart);
        if (modelViewportAllowsDrag)
        {
            scene.CompleteTranslationGizmoDrag(commit: false);
        }
        Assert.True(modelViewportAllowsDrag,
            "The model-owned Fit viewport must release its normal-preview camera override before routing joint drags.");
        scene.SetTranslationGizmoTarget(workspace.Conformance.GizmoTarget);
        AssertSelectedJointGizmoCanStart(workspace, scene);

        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);
        Assert.True(workspace.IsGuidedPreview, workspace.GuidedStatus);
        string? outputPreviewCamera = workspace.CaptureProjectSession().Model?.Package.Document.Camera.ActivePreviewNodeName;
        Assert.Equal("CC_Base_Head", outputPreviewCamera);

        var restoredCameraProbe = new RecordingTranslationGizmoTarget();
        scene.SetTranslationGizmoTarget(restoredCameraProbe);
        Assert.False(scene.TryBeginTranslationGizmoDrag(arbitraryStart),
            $"The session camera should be restored after leaving Fit. Camera={outputPreviewCamera ?? "<none>"}; " +
            $"viewport={workspace.Viewport.Title}.");
        Assert.Equal(0, restoredCameraProbe.BeginCount);

        await workspace.GuidedBackCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedAdjust, workspace.GuidedStatus);
        Assert.True(workspace.ShowGuidedJointControls);
        AssertSelectedJointGizmoCanStart(workspace, scene);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task BackFromPreviewPublishesJointPreviewWithoutSessionCameraOverride()
    {
        using ModelsWorkspaceViewModel workspace = CreateWorkspaceWithPreviewCamera(withPreviewCamera: false);
        workspace.SetGuidedPreviewHandler(static () => Task.FromResult(false));
        await workspace.BeginGuidedSetupAsync(CustomModelSkeletonChoice.MapToDl1);
        await workspace.GuidedPrimaryCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedPreview, workspace.GuidedStatus);
        await workspace.GuidedBackCommand.ExecuteAsync(null);

        Assert.True(workspace.IsGuidedAdjust, workspace.GuidedStatus);
        AssertSelectedJointGizmoCanStart(workspace, workspace.Viewport.SceneSource);
    }

    private static void AssertSelectedJointGizmoCanStart(
        ModelsWorkspaceViewModel workspace,
        ViewportSceneSource scene)
    {
        Assert.True(workspace.Conformance.SelectedBoneIndex >= 0);
        Assert.StartsWith("Joint preview - ", workspace.Viewport.Title, StringComparison.Ordinal);
        GizmoRenderData[] handles = scene.CaptureFrame().Gizmos
            .Where(static gizmo => gizmo.Kind == GizmoKind.TranslationHandle)
            .ToArray();
        Assert.Equal(3, handles.Length);

        GizmoRenderData handle = handles[0];
        Assert.NotNull(handle.TranslationBinding);
        Vector3 axis = Vector3.Normalize(handle.End - handle.Start);
        Assert.True(scene.TryBeginTranslationGizmoDrag(
            new RenderTranslationGizmoDragStart(handle.TranslationBinding!.Value, axis)),
            "The selected conformance gizmo should route to the wizard while the Fit viewport owns the scene.");
        scene.CompleteTranslationGizmoDrag(commit: false);
    }

    private static ModelsWorkspaceViewModel CreateWorkspaceWithPreviewCamera(
        bool withPreviewCamera = true)
    {
        FbxModelAuthoringImportResult source = RigConformanceWizardTests.CreateModel();
        if (withPreviewCamera)
        {
            source = source with
            {
                Package = source.Package with
                {
                    Document = source.Package.Document with
                    {
                        Camera = new CustomModelCameraMetadata
                        {
                            ActivePreviewNodeName = "CC_Base_Head",
                        },
                    },
                },
            };
        }

        var workspace = new ModelsWorkspaceViewModel(
            new NullProjectFileDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null,
            resolveRigTemplate: (profile, _) =>
                Task.FromResult(CreateTemplateResolutionWithMatchingHead(profile)),
            captureAuthoredLayer: static (model, _) => model);
        RigConformanceTestSchedulers.UseImmediate(workspace);
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            source,
            "synthetic-camera-preview.dlrmodel",
            new ProjectModelsWorkspaceState
            {
                PackageAssetId = Guid.NewGuid(),
            }));
        return workspace;
    }

    private static Dl1RigTemplateResolution CreateTemplateResolutionWithMatchingHead(string profile)
    {
        Dl1RigTemplateResolution resolution = RigConformanceWizardTests.CreateResolution(profile);
        Dl1RigTemplate template = resolution.Template!;
        ImmutableArray<Dl1RigTemplateEntity> entities = template.Entities
            .Select(entity => string.Equals(entity.SemanticRole, "body.head", StringComparison.Ordinal)
                ? entity with { Name = "CC_Base_Head" }
                : entity)
            .ToImmutableArray();
        var cameraTemplate = new Dl1RigTemplate(
            template.ProfileName,
            template.SourceResourceName,
            template.SourceFingerprint,
            entities);
        return resolution with { Template = cameraTemplate };
    }

    private sealed class RecordingTranslationGizmoTarget : IRenderTranslationGizmoTarget
    {
        public int BeginCount { get; private set; }

        public bool TryBeginTranslationGizmoDrag(RenderTranslationGizmoDragStart start)
        {
            BeginCount++;
            return true;
        }

        public bool UpdateTranslationGizmoDrag(RenderTranslationGizmoDragUpdate update) => true;

        public void CompleteTranslationGizmoDrag(bool commit) { }
    }

    private sealed class NullProjectFileDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
