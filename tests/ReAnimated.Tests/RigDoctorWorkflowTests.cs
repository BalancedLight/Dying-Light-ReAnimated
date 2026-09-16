using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RigDoctorWorkflowTests
{
    [Fact]
    public async Task DiagnosisShowsTransientContactsAndRepairsAsOneUndo()
    {
        var (source,_)=FbxContactAuthoringTests.Model();
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),_=>{},_=>Task.CompletedTask,()=>null);
        workspace.CommitProjectRestore(new(source,"doctor-control.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.IsConformTabSelected=true;var vm=workspace.Conformance;
        _=workspace.CaptureProjectSession();
        vm.SetDoctorRules([FbxRigDoctorTests.Rule(source),FbxRigDoctorTests.Rule(source,"second_contact","contact.second")],"generic test rules");
        await vm.RunDoctorCommand.ExecuteAsync(null);
        Assert.True(vm.DoctorPreview?.CanApply,vm.DoctorStatus);Assert.False(vm.CanApplyDoctor);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);
        vm.DoctorReviewed=true;Assert.True(vm.CanApplyDoctor);
        vm.StudioStage=RigStudioStage.Animate;Assert.NotNull(vm.DoctorPreview);
        vm.ApplyDoctorCommand.Execute(null);
        Assert.Null(vm.DoctorPreview);
        var changed=workspace.CaptureProjectSession().Model!;
        Assert.Equal(2,changed.Package.Document.AuthoredHelpers.Length);
        Assert.Equal(source.Surfaces,changed.Surfaces);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(2,workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers.Length);
        await vm.RunDoctorCommand.ExecuteAsync(null);
        Assert.True(vm.DoctorPreview?.IsNoOp,vm.DoctorStatus);Assert.False(vm.CanApplyDoctor);
    }
    private sealed class NoDialogs:IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
    }

    [Fact]
    public async Task AmbiguousGeometryCanBeResolvedInTheDoctorPanel()
    {
        var (source,_)=FbxContactAuthoringTests.Model();var surface=source.Surfaces[0];
        source=source with{Surfaces=source.Surfaces.Add(surface with{SourceGeometry=surface.SourceGeometry! with{Id="alternative"}})};
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),_=>{},_=>Task.CompletedTask,()=>null);
        workspace.CommitProjectRestore(new(source,"ambiguous-control.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.IsConformTabSelected=true;_=workspace.CaptureProjectSession();
        var vm=workspace.Conformance;vm.SetDoctorRules([FbxRigDoctorTests.Rule(source) with{ComponentId=null}],"generic rules");
        await vm.RunDoctorCommand.ExecuteAsync(null);Assert.False(vm.DoctorPreview!.CanApply);
        vm.DoctorComponent=vm.DoctorComponents.Single(c=>c.Id=="source-shoe");
        Assert.Null(vm.DoctorPreview);Assert.False(vm.CanApplyDoctor);
        await vm.RunDoctorCommand.ExecuteAsync(null);
        Assert.True(vm.DoctorPreview!.CanApply,vm.DoctorStatus);
        Assert.Equal("source-shoe",Assert.Single(vm.DoctorRows).ComponentId);
    }
}
