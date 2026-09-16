using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class HierarchyWorkflowTests
{
    [Fact]
    public async Task ParentPreviewIsTransientAndAppliesAsOneUndoKeepingSelection()
    {
        var source=FbxHierarchyAuthoringTests.Source();
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),_=>{},_=>Task.CompletedTask,()=>null);
        workspace.CommitProjectRestore(new(source,"hierarchy-test.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.IsConformTabSelected=true;var vm=workspace.Conformance;
        vm.HierarchyNode=vm.HierarchyNodes.Single(n=>n.Name=="Child");
        vm.HierarchyParent=vm.HierarchyParents.Single(n=>n.Name=="aux_eye");
        var before=workspace.CaptureProjectSession().Model!;
        await vm.PreviewHierarchyCommand.ExecuteAsync(null);
        Assert.True(vm.CanApplyHierarchy,vm.HierarchyStatus);Assert.NotNull(vm.HierarchyPreview);
        Assert.Equal(before.Package.AuthoredLayerPayload,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        var frame=workspace.Viewport.SceneSource.CaptureFrame();Assert.NotEmpty(frame.Gizmos);
        var preview=CustomModelPreviewAdapter.CreateSession(vm.HierarchyPreview!.PreviewModel,CustomModelPreviewMode.SourceFbx);
        for(int i=0;i<frame.Meshes.Count;i++)
        {
            var actual=CpuMeshDeformationEvaluator.Evaluate(frame.Meshes[i],frame.Skeleton,[]);
            var expected=CpuMeshDeformationEvaluator.Evaluate(preview.Meshes[i],preview.CreateSkeleton(null,0,null),[]);
            Assert.Equal(actual,expected);
        }
        vm.StudioStage=RigStudioStage.Fit;Assert.NotNull(vm.HierarchyPreview);
        vm.ApplyHierarchyCommand.Execute(null);
        var applied=workspace.CaptureProjectSession().Model!;
        var child=applied.Package.Document.Bones.Single(b=>b.Name=="Child");
        Assert.Equal("aux_eye",applied.Package.Document.Bones[child.ParentIndex].Name);
        Assert.Equal("Child",workspace.SelectedBone?.Name);
        Assert.Null(vm.HierarchyPreview);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(before.Package.AuthoredLayerPayload,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(applied.Package.AuthoredLayerPayload,workspace.CaptureProjectSession().Model!.Package.AuthoredLayerPayload);
        vm.HierarchyNode=vm.HierarchyNodes.Single(n=>n.Name=="aux_eye");
        Assert.DoesNotContain(vm.HierarchyParents,p=>p.Name=="Child"||p.Name=="aux_eye");
    }
    private sealed class NoDialogs:IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
    }
}
