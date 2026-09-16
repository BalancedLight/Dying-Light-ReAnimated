using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RestPoseWorkflowTests
{
    [Theory]
    [InlineData(RigRestSurfaceMode.PreserveSurface)]
    [InlineData(RigRestSurfaceMode.BakePose)]
    public async Task PreviewIsTransientNavigationKeepsItAndApplyIsOneUndo(RigRestSurfaceMode mode)
    {
        var source=FbxRestPoseAuthoringTests.Source();
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),_=>{},_=>Task.CompletedTask,()=>null);
        workspace.CommitProjectRestore(new(source,"rest-test.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.IsConformTabSelected=true;
        var vm=workspace.Conformance;
        vm.RestPoseNode=vm.RestPoseNodes.Single(n=>n.Name=="body_head");
        vm.RestSurfaceMode=vm.RestSurfaceChoices.Single(c=>c.Mode==mode);
        vm.RestPosePosition.X+=.04;vm.RestPoseRotation.Z=20;
        var payload=workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload;
        await vm.PreviewRestPoseCommand.ExecuteAsync(null);
        Assert.NotNull(vm.RestPosePreview);Assert.True(vm.CanApplyRestPose,vm.RestPoseStatus);
        Assert.Equal(payload,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);
        var shown=workspace.Viewport.SceneSource.CaptureFrame();
        var expected=CustomModelPreviewAdapter.CreateSession(vm.RestPosePreview!.PreviewModel,CustomModelPreviewMode.SourceFbx);
        Assert.Equal(expected.Meshes.Length,shown.Meshes.Count);
        for(int i=0;i<expected.Meshes.Length;i++)
        {
            var a=CpuMeshDeformationEvaluator.Evaluate(expected.Meshes[i],expected.CreateSkeleton(null,0,null),[]);
            var b=CpuMeshDeformationEvaluator.Evaluate(shown.Meshes[i],shown.Skeleton,[]);
            Assert.Equal(a,b);
        }
        vm.StudioStage=RigStudioStage.HelpersAndHooks;
        Assert.NotNull(vm.RestPosePreview);Assert.True(vm.CanApplyRestPose);
        vm.ApplyRestPoseCommand.Execute(null);
        var applied=workspace.CaptureProjectSession().Model!;
        Assert.NotEqual(payload,applied.Package.AuthoredLayerPayload);
        Assert.Null(vm.RestPosePreview);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(payload,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(applied.Package.AuthoredLayerPayload,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
    }
    private sealed class NoDialogs:IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
    }
}
