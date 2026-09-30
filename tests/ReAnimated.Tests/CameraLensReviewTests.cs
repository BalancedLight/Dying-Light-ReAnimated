using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class CameraLensReviewTests
{
    [Fact]
    public void FrustumMatchesRendererProjectionAndNearFarClipping()
    {
        var world = new TransformTRS(new(2,3,4), QuaternionD.FromAxisAngle(Vector3D.UnitY,.4), Vector3D.One).ToMatrix();
        var frame = CameraReviewGeometry.Create(world, new CameraLens(70, 16.0/9, .1, 10));
        RenderCamera camera = CameraReviewOverlayBuilder.CreateRenderCamera(frame)!.Value;
        Matrix4x4 view = Matrix4x4.CreateLookAt(camera.Eye, camera.Target, camera.Up);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(camera.VerticalFieldOfViewDegrees*MathF.PI/180,
            camera.ProjectionAspectRatio!.Value,camera.NearPlane,camera.FarPlane);
        Vector3 Project(Vector3D point)
        {
            Vector4 p = Vector4.Transform(new Vector4((float)point.X,(float)point.Y,(float)point.Z,1), view*projection);
            return new(p.X/p.W,p.Y/p.W,p.Z/p.W);
        }
        foreach (Vector3D corner in frame.NearCorners)
        {
            Vector3 p=Project(corner);
            Assert.InRange(MathF.Abs(p.X),.9999f,1.0001f);
            Assert.InRange(MathF.Abs(p.Y),.9999f,1.0001f);
            Assert.InRange(MathF.Abs(p.Z),0,.0001f);
        }
        Assert.True(Project(frame.Position+frame.Forward*.05).Z<0);
        Assert.True(Project(frame.Position+frame.Forward*20).Z>1);
        Assert.Equal(15,CameraReviewOverlayBuilder.Build(frame,.5).Count);
        Assert.Equal(10,camera.FarPlane);
    }

    [Fact]
    public async Task LensEditsDoNotChangeTheAuthoredCameraDraft()
    {
        var model=CameraHelperCalibrationTests.Source();
        var editor=new RigConformanceWizardViewModel((_,_)=>Task.FromResult(Dl1RigTemplateResolution.Failed("test","synthetic")),static _=>{});
        editor.SetModel(model);editor.SelectedCameraNode=editor.CameraNodes.Single(n=>n.Name=="EyeCamera");editor.CameraOffsetX=.01;
        await editor.PreviewCameraCalibrationCommand.ExecuteAsync(null);
        var preview=editor.CameraCalibrationPreview;Assert.NotNull(preview);
        editor.CameraReviewed=true;
        editor.CameraVerticalFov=75;editor.CameraNearClip=.05;editor.CameraFarClip=80;editor.CameraLookThrough=true;
        editor.CameraInvertUp=false;editor.CameraAnimateReview=true;
        Assert.Same(preview,editor.CameraCalibrationPreview);
        Assert.True(editor.CanApplyCameraCalibration);
        Assert.True(editor.TryCreateCameraLens(out var lens,out _));Assert.Equal(75,lens!.Value.VerticalFieldOfViewDegrees);
        editor.CameraNearClip=100;
        Assert.False(editor.TryCreateCameraLens(out _,out _));
        Assert.Contains("invalid",editor.CameraLensStatus,StringComparison.OrdinalIgnoreCase);
        Assert.Null(editor.CreateCameraViewFrame(preview.WorldFrame));
        Assert.Same(preview,editor.CameraCalibrationPreview);
    }

    [Fact]
    public async Task WorkspaceLensFollowsTheEvaluatedAnimationAndRestoresOrbit()
    {
        var source=CameraHelperCalibrationTests.Source();
        Guid id=source.AnimationClips.Keys.First();
        AnimationClip previous=source.AnimationClips[id];
        Assert.True(previous.FrameCount>1);
        TransformKeyframe[] keys=[new(0,source.Rig!.Bones[0].LocalBindPose),new(previous.FrameCount-1,
            new(new(.7,.2,.1),QuaternionD.FromAxisAngle(Vector3D.UnitY,.35),Vector3D.One))];
        var clip=new AnimationClip(previous.Name,previous.FrameRate,previous.FrameCount,[new TransformTrack(0,keys)]);
        source=source with{AnimationClips=source.AnimationClips.SetItem(id,clip)};
        using var workspace=new ModelsWorkspaceViewModel(new NoDialogs(),static _=>{},static _=>Task.CompletedTask,static ()=>null);
        workspace.CommitProjectRestore(new(source,"camera-review.dlrmodel",new ProjectModelsWorkspaceState{PackageAssetId=Guid.NewGuid(),SelectedAnimationClipId=id}));
        workspace.IsConformTabSelected=true;
        var editor=workspace.Conformance;editor.StudioStage=RigStudioStage.HelpersAndHooks;
        RenderCamera orbit=workspace.Viewport.SceneSource.CaptureFrame().Camera;
        editor.SelectedCameraNode=editor.CameraNodes.Single(n=>n.Name=="EyeCamera");editor.CameraOffsetZ=.025;
        await editor.PreviewCameraCalibrationCommand.ExecuteAsync(null);
        Assert.True(editor.HasCameraCalibrationPreview,editor.CameraStatus);
        editor.CameraLookThrough=true;editor.CameraAnimateReview=true;
        workspace.Timeline.CurrentFrame=0;
        var first=workspace.Viewport.SceneSource.CaptureFrame();
        workspace.Timeline.CurrentFrame=checked((int)clip.FrameCount-1);
        var last=workspace.Viewport.SceneSource.CaptureFrame();
        var eye=last.Skeleton!.Bones.Single(b=>b.Name=="EyeCamera");
        var expected=CameraReviewGeometry.Create(CorePreviewAdapter.ToCoreMatrix(eye.WorldTransform*last.Skeleton.RootTransform),
            new(editor.CameraVerticalFov,editor.CameraAspect,editor.CameraNearClip,editor.CameraFarClip),editor.CameraInvertUp);
        Assert.InRange(Vector3.Distance(last.Camera.Eye,new((float)expected.Position.X,(float)expected.Position.Y,(float)expected.Position.Z)),0,1e-5f);
        Assert.True(Vector3.Distance(first.Camera.Eye,last.Camera.Eye)>.1f);
        var helpers=editor.CameraCalibrationPreview!.Candidate.Package.Document.AuthoredHelpers;
        TransformMatrix analytic=clip.SamplePose(source.Rig!,clip.FrameRate.SecondsForFrame(clip.FrameCount-1)).GlobalMatrices[0] *
            helpers.Single(h=>h.Name=="RefCamera").ExactLocalMatrix * helpers.Single(h=>h.Name=="EyeCamera").ExactLocalMatrix;
        Assert.True(CorePreviewAdapter.ToCoreMatrix(eye.WorldTransform).NearlyEquals(analytic,1e-5));

        Assert.Equal<double>(keys.Select(k=>k.Frame),clip.TransformTracks[0].Keyframes.Select(k=>k.Frame));
        Assert.Equal<TransformKeyframe>(keys,clip.TransformTracks[0].Keyframes);
        editor.CameraLookThrough=false;
        Assert.Equal(orbit,workspace.Viewport.SceneSource.CaptureFrame().Camera);
        Assert.Contains("animation",editor.CameraPoseStatus,StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NoDialogs:IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath)=>null;
        public string? ShowSaveProjectDialog(string suggestedName,string? currentPath)=>null;
    }
}
