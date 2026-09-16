using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using System.Collections.Immutable;

namespace ReAnimated.Tests;

public sealed class DerivedMotionWorkflowTests
{
    [Fact]
    public async Task DerivedPreviewIsTransientNavigableAndSavesAsOneUndo()
    {
        var source=FbxHierarchyAuthoringTests.Source();
        var reparent=FbxHierarchyAuthoring.Preview(source,FbxHierarchyAuthoringTests.Entity(source,"Child"),FbxHierarchyAuthoringTests.Entity(source,"aux_eye"));
        Assert.True(FbxHierarchyAuthoring.TryApply(source,reparent,out source));
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),_=>{},_=>Task.CompletedTask,()=>null);
        workspace.CommitProjectRestore(new(source,"motion-review.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.IsConformTabSelected=true;var vm=workspace.Conformance;
        var original=workspace.CaptureProjectSession().Model!;
        vm.DerivedSampleMultiplier=2;
        await vm.DeriveMotionCommand.ExecuteAsync(null);
        Assert.True(vm.CanSaveDerivedMotion,vm.DerivedMotionStatus);
        var preview=vm.DerivedMotionPreview!;var id=preview.ClipId;
        AnimationClip derived=preview.PreviewModel!.AnimationClips[id];
        Assert.DoesNotContain(workspace.CaptureProjectSession().Model!.Package.Document.AnimationClips,c=>c.Id==id);
        Assert.Equal(derived.FrameRate.FramesPerSecond,workspace.Timeline.FramesPerSecond);
        Assert.Equal(derived.FrameCount-1,workspace.Timeline.EndFrame);
        workspace.Timeline.CurrentFrame=workspace.Timeline.EndFrame;
        Assert.NotNull(workspace.Viewport.SceneSource.CaptureFrame().Skeleton);
        vm.StudioStage=RigStudioStage.Fit;
        Assert.True(vm.CanSaveDerivedMotion);
        vm.SaveDerivedMotionCommand.Execute(null);
        Assert.Null(vm.DerivedMotionPreview);
        Assert.Equal(id,workspace.SelectedAnimation!.Id);
        Assert.Equal("Derived",workspace.SelectedAnimation.OriginLabel);
        Assert.False(workspace.SelectedAnimation.Included);
        Assert.Equal(RigStudioStage.Fit,workspace.CaptureProjectSession().Model!.Package.Document.RiggingSession!.Stage);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(original.Package.Document.AnimationClips,workspace.CaptureProjectSession().Model!.Package.Document.AnimationClips);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Contains(workspace.CaptureProjectSession().Model!.Package.Document.AnimationClips,c=>c.Id==id);
    }

    [Fact]
    public void SourcePreviewSamplesSignedFacialWeightsUsingTheirDeclaredUnit()
    {
        var source=FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateNormalMorphFbx(),"animated-morph.fbx");
        var metadata=new CustomModelAnimationClip{Id=Guid.NewGuid(),FbxObjectId=1,SourceName="facial_cycle",DisplayName="facial_cycle",
            FrameRate=new(30,1),FrameCount=31,SourceFingerprint=new string('a',64),HasMorphTracks=true,FacialSourceValueUnit="percent"};
        string morph=source.Package.Document.MorphChannels[0].Name;
        var clip=new AnimationClip(metadata.DisplayName,metadata.FrameRate,metadata.FrameCount,scalarTracks:
            [new ScalarTrack(morph,[new(0,-25),new(metadata.FrameCount-1,75)])]);
        source=source with{AnimationClips=ImmutableDictionary<Guid,AnimationClip>.Empty.Add(metadata.Id,clip),Package=source.Package with{Document=source.Package.Document with
            {AnimationClips=[metadata]}}};
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),_=>{},_=>Task.CompletedTask,()=>null);
        workspace.CommitProjectRestore(new(source,"facial-preview.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.SelectedAnimation=workspace.Animations.Single();
        Assert.Equal(-.25f,Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights).Weight);
        workspace.Timeline.CurrentFrame=workspace.Timeline.EndFrame;
        Assert.Equal(.75f,Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights).Weight);
    }

    private sealed class NoDialogs:IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
    }
}
