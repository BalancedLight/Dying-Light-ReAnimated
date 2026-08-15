using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspacePreviewVisibilityTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public void HidingBoneOverlayRetainsSkinningSkeletonAndMesh()
    {
        using var viewModel = new ModelsWorkspaceViewModel(
            new NullDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null);
        viewModel.CommitProjectRestore(
            new PreparedModelsWorkspaceRestore(
                CustomModelPreviewSessionTests.CreateModel(
                    flipTextureCoordinateV: true),
                "generic.dlrmodel",
                new ProjectModelsWorkspaceState
                {
                    PackageAssetId = Guid.NewGuid(),
                    ShowMeshes = true,
                    ShowBones = true,
                }));

        RenderFrameSnapshot visible =
            viewModel.Viewport.SceneSource.CaptureFrame();
        SkeletonRenderData visibleSkeleton =
            Assert.IsType<SkeletonRenderData>(visible.Skeleton);
        Assert.NotEmpty(visible.Meshes);
        Assert.True(visibleSkeleton.ShowDeformBones);

        viewModel.ShowBones = false;

        RenderFrameSnapshot hidden =
            viewModel.Viewport.SceneSource.CaptureFrame();
        SkeletonRenderData hiddenSkeleton =
            Assert.IsType<SkeletonRenderData>(hidden.Skeleton);
        Assert.NotEmpty(hidden.Meshes);
        Assert.Equal(
            visibleSkeleton.Bones.Count,
            hiddenSkeleton.Bones.Count);
        Assert.False(hiddenSkeleton.ShowDeformBones);
    }

    private sealed class NullDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }
}
