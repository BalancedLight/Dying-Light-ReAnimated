using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class CameraTemplateCreationTests
{
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    internal static FbxModelAuthoringImportResult Target()
    {
        var model=FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(),"camera-target.fbx");
        var doc=model.Package.Document;
        return model with{Package=model.Package with{Document=doc with{RiggingSession=RiggingSessions.Create(doc,RigStudioEntryPath.RepairExistingRig)}}};
    }
    internal static Dl1RigTemplate Template()
    {
        var child=TransformMatrix.CreateTranslation(new(0,1,0));
        var reference=new TransformTRS(new(.1,.2,.3),QuaternionD.FromAxisAngle(Vector3D.UnitY,.2),Vector3D.One).ToMatrix();
        var eye=new TransformTRS(new(.02,.03,.04),QuaternionD.FromAxisAngle(Vector3D.UnitZ,.3),Vector3D.One).ToMatrix();
        return new("generic-camera-profile","generic_reference",new string('c',64),
        [
            new(){Index=0,Name="Root",ParentIndex=-1,Kind=BoneKind.Root,IsDeform=false,LocalRestMatrix=TransformMatrix.Identity,GlobalRestMatrix=TransformMatrix.Identity},
            new(){Index=1,Name="Child",ParentIndex=0,Kind=BoneKind.Deform,IsDeform=true,LocalRestMatrix=child,GlobalRestMatrix=child},
            new(){Index=2,Name="refcamera",ParentIndex=0,Kind=BoneKind.Camera,IsDeform=false,LocalRestMatrix=reference,GlobalRestMatrix=reference},
            new(){Index=3,Name="eyecamera",ParentIndex=1,Kind=BoneKind.Camera,IsDeform=false,LocalRestMatrix=eye,GlobalRestMatrix=child*eye},
        ]);
    }

    [Fact]
    public void CreatesIndependentFramesAndRetainsHistoricalOriginThroughReopen()
    {
        var original=Target();var template=Template();var current=original;
        var observations=FbxCameraHelperAuthoring.ObserveTemplate(template);
        foreach(var observation in observations)
        {
            var doc=current.Package.Document;int parent=doc.Bones.Single(b=>b.Name==observation.ParentName).Index;
            Guid parentId=RiggingSessions.ObserveSourceHierarchy(doc)[parent].EntityId;
            var offset=new TransformTRS(new(.01,0,0),QuaternionD.Identity,Vector3D.One);
            var preview=FbxCameraHelperAuthoring.PreviewCreation(current,template,observation.Name,parentId,offset);
            Assert.True(preview.IsCreation);Assert.True(preview.HasChanges);
            Assert.False(FbxCameraHelperAuthoring.TryApply(current,preview,out _));
            Assert.False(FbxCameraHelperAuthoring.TryApply(current,preview,template.CreateScaled(2),out _));
            Assert.True(FbxCameraHelperAuthoring.TryApply(current,preview,template,out var applied));current=applied;
            var helper=current.Package.Document.RiggingSession!.Recipe.Helpers.Single(h=>h.EntityId==preview.Node.EntityId);
            Assert.True(helper.LocalFrame.NearlyEquals(observation.LocalFrame*offset.ToMatrix()));
            Assert.True(helper.FollowPreparedParent);
            Assert.Equal(template.SourceFingerprint,helper.TemplateOrigin!.ResourceSha256);
            Assert.Equal(observation.ParentName,helper.TemplateOrigin.SourceParentName);
            Assert.DoesNotContain(helper.Evidence,e=>e.Kind==RigEvidenceKind.ProfileRule);
        }
        Assert.Equal<CustomModelBone>(original.Package.Document.Bones,current.Package.Document.Bones);
        Assert.Equal<FbxModelSurface>(original.Surfaces,current.Surfaces);
        Assert.Same(original.AnimationClips,current.AnimationClips);
        Assert.Equal(original.Package.SourceFbx,current.Package.SourceFbx);
        Assert.Empty(current.Package.Document.RiggingSession!.Recipe.ComponentPolicies);
        Assert.Empty(current.Package.Document.RiggingSession.ValidationHistory);
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            string path=Path.Combine(directory,"created-cameras.dlrmodel");CustomModelPackageSerializer.SaveAtomic(current.Package,path);
            var reopened=FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
            Assert.Equal(2,reopened.Package.Document.AuthoredHelpers.Length);
            Assert.Equal("Root",FbxCameraHelperAuthoring.Inspect(reopened).Single(n=>n.Name=="RefCamera").ParentName);
            Assert.Equal("Child",FbxCameraHelperAuthoring.Inspect(reopened).Single(n=>n.Name=="EyeCamera").ParentName);
            Assert.All(reopened.Package.Document.RiggingSession!.Recipe.Helpers,h=>Assert.Equal(template.SourceFingerprint,h.TemplateOrigin!.ResourceSha256));
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }

    [Fact]
    public void RejectsCollisionsForeignParentsStaleSourceAndCancelledWork()
    {
        var model=Target();var template=Template();Guid parent=RiggingSessions.ObserveSourceHierarchy(model.Package.Document)[0].EntityId;
        var preview=FbxCameraHelperAuthoring.PreviewCreation(model,template,"RefCamera",parent,TransformTRS.Identity);
        Assert.False(FbxCameraHelperAuthoring.TryApply(model with{Package=model.Package},preview,template,out _));
        Assert.Throws<ArgumentException>(()=>FbxCameraHelperAuthoring.PreviewCreation(preview.Candidate,template,"RefCamera",parent,TransformTRS.Identity));
        Assert.Throws<InvalidOperationException>(()=>FbxCameraHelperAuthoring.PreviewCreation(model,template,"RefCamera",Guid.NewGuid(),TransformTRS.Identity));
        Assert.Throws<OperationCanceledException>(()=>FbxCameraHelperAuthoring.PreviewCreation(model,template,"RefCamera",parent,TransformTRS.Identity,new(true)));
        Assert.Empty(model.Package.Document.AuthoredHelpers);
    }

    [Fact]
    public void NamedSelectionSurvivesReferencePhysicalReorderingAndMetadataNavigation()
    {
        var template=Template();
        var reordered=new Dl1RigTemplate(template.ProfileName,template.SourceResourceName,new string('d',64),
            [template.Entities[0],template.Entities[1],template.Entities[3] with{Index=2},template.Entities[2] with{Index=3}]);
        var model=Target();var doc=model.Package.Document;Guid parent=RiggingSessions.ObserveSourceHierarchy(doc)[1].EntityId;
        var preview=FbxCameraHelperAuthoring.PreviewCreation(model,reordered,"EyeCamera",parent,TransformTRS.Identity);
        Assert.Equal("EyeCamera",preview.Node.Name);
        Assert.True(preview.Candidate.Package.Document.AuthoredHelpers[0].ExactLocalMatrix.NearlyEquals(template.Entities[3].LocalRestMatrix));
        var navigated=model with{Package=model.Package with{Document=doc with{RiggingSession=RiggingSessions.Navigate(doc.RiggingSession!,RigStudioStage.Animate)}}};
        var refreshed=FbxCameraHelperAuthoring.RefreshMetadata(preview,navigated);
        Assert.NotNull(refreshed);
        Assert.True(FbxCameraHelperAuthoring.TryApply(navigated,refreshed,reordered,out var applied));
        Assert.Equal(RigStudioStage.Animate,applied.Package.Document.RiggingSession!.Stage);
        Assert.Null(FbxCameraHelperAuthoring.RefreshMetadata(preview,navigated with{Package=navigated.Package with{Document=navigated.Package.Document with{Name="changed"}}}));
    }

    [Fact]
    public async Task WorkspaceAppliesCreationAsOneUndoAndKeepsStageOnlyReview()
    {
        var source=Target();var template=Template();
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),static _=>{},static _=>Task.CompletedTask,static ()=>null,
            resolveRigTemplate:(_,_)=>Task.FromResult(new Dl1RigTemplateResolution(template,template.ProfileName,template.SourceResourceName,template.SourceFingerprint,"Synthetic reference")));
        workspace.CommitProjectRestore(new(source,"camera-target.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid()}));
        workspace.IsConformTabSelected=true;var editor=workspace.Conformance;editor.TemplateProfileName=template.ProfileName;
        await editor.ResolveTemplateCommand.ExecuteAsync(null);editor.StudioStage=RigStudioStage.HelpersAndHooks;
        editor.CameraTemplateChoice=editor.CameraTemplateChoices.Single(c=>c.Name=="EyeCamera");
        await editor.PreviewCameraCreationCommand.ExecuteAsync(null);editor.CameraReviewed=true;
        editor.StudioStage=RigStudioStage.Animate;editor.StudioStage=RigStudioStage.HelpersAndHooks;
        Assert.True(editor.CanApplyCameraCalibration);
        editor.ApplyCameraCalibrationCommand.Execute(null);
        Assert.Single(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        workspace.RedoHelperEditCommand.Execute(null);
        var restored=workspace.CaptureProjectSession().Model!;
        Assert.Equal("EyeCamera",Assert.Single(restored.Package.Document.AuthoredHelpers).Name);
        Assert.NotNull(Assert.Single(restored.Package.Document.RiggingSession!.Recipe.Helpers).TemplateOrigin);
        Assert.Equal<FbxModelSurface>(source.Surfaces,restored.Surfaces);
    }

    [Fact]
    public async Task WizardShowsReferenceParentAndRequiresReviewBeforeCreation()
    {
        var template=Template();var source=Target();
        var editor=new RigConformanceWizardViewModel((_,_)=>Task.FromResult(new Dl1RigTemplateResolution(template,template.ProfileName,
            template.SourceResourceName,template.SourceFingerprint,"Synthetic reference")),static _=>{});
        editor.SetModel(source);editor.TemplateProfileName=template.ProfileName;
        await editor.ResolveTemplateCommand.ExecuteAsync(null);
        editor.CameraTemplateChoice=editor.CameraTemplateChoices.Single(c=>c.Name=="EyeCamera");
        Assert.Equal("Child",editor.CameraCreationParent!.Name);
        Assert.True(editor.CanPreviewCameraCreation);
        await editor.PreviewCameraCreationCommand.ExecuteAsync(null);
        Assert.True(editor.HasCameraCalibrationPreview,editor.CameraStatus);Assert.True(editor.CameraCalibrationPreview!.IsCreation);
        Assert.False(editor.CanApplyCameraCalibration);editor.CameraReviewed=true;Assert.True(editor.CanApplyCameraCalibration);
        FbxModelAuthoringImportResult? result=null;editor.CameraModelApplyRequested+=(_,e)=>result=e.Result;
        editor.ApplyCameraCalibrationCommand.Execute(null);
        Assert.Equal("EyeCamera",Assert.Single(result!.Package.Document.AuthoredHelpers).Name);
        await editor.PreviewCameraCreationCommand.ExecuteAsync(null);
        editor.TemplateProfileName="changed-reference";
        Assert.False(editor.CanApplyCameraCalibration);Assert.False(editor.CanPreviewCameraCreation);
    }
}
