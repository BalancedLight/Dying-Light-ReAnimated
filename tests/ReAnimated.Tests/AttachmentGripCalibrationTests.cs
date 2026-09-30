using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class AttachmentGripCalibrationTests
{
    private static RigDefinition Actor()=>new("actor","actor",
    [new(0,"root",-1,TransformTRS.Identity,BoneKind.Root),
     new(1,"primary",0,new(new(1,2,3),QuaternionD.FromAxisAngle(Vector3D.UnitZ,.7),new(2,2,2)),BoneKind.Prop),
     new(2,"secondary",0,new(new(2,2,3),QuaternionD.Identity,Vector3D.One),BoneKind.Prop)]);
    private static AttachmentGripCalibration Grip(Guid id)=>new()
    {
        PropAssetId=id,PropContentSha256=new string('a',64),
        PrimaryPropFrame=AttachmentGripFrame.FromMatrix(0,"holder",new TransformTRS(new(.2,.1,0),QuaternionD.FromAxisAngle(Vector3D.UnitY,.4),Vector3D.One).ToMatrix()),
        Secondary=new(){CharacterBoneIndex=2,CharacterBoneName="secondary",PropFrame=AttachmentGripFrame.FromMatrix(1,"support",TransformMatrix.CreateTranslation(new(.5,0,0)))},
    };

    [Fact]
    public void PrimaryFramesCoincideWhileOffsetsAndInheritedScaleRemainExplicit()
    {
        var rig=Actor();var id=Guid.NewGuid();var grip=Grip(id);
        var offset=new TransformTRS(new(.1,0,0),QuaternionD.FromAxisAngle(Vector3D.UnitX,.2),new(1.2,.8,1));
        var binding=new AttachmentBinding(Guid.NewGuid(),id,"prop",1,offset,AttachmentScope.PreviewOnly,"primary",grip);
        var frame=new AnimationEvaluator().Evaluate(new(rig,rig,new("still",new(30,1),1),0,PreviewProfile.RawAuthoring,attachments:[binding]));
        var attachment=Assert.Single(frame.DisplayAttachments);
        var expected=rig.CreateBindPose().GlobalMatrices[1]*offset.ToMatrix();
        Assert.True((attachment.WorldTransform*grip.PrimaryPropFrame.Matrix).NearlyEquals(expected,1e-9));
        Assert.True(attachment.PrimaryGripWorldFrame!.Value.NearlyEquals(expected,1e-9));
        var second=attachment.WorldTransform*grip.Secondary!.PropFrame.Matrix;
        Assert.Equal((second.Translation-rig.CreateBindPose().GlobalMatrices[2].Translation).Length,attachment.SecondaryPositionError!.Value,10);
        Assert.True(frame.DisplayPose.GlobalMatrices[2].NearlyEquals(rig.CreateBindPose().GlobalMatrices[2]));
    }

    [Fact]
    public void ReorderedSecondaryCharacterFrameIsNotSilentlyAccepted()
    {
        var rig=Actor();var id=Guid.NewGuid();var grip=Grip(id) with{Secondary=Grip(id).Secondary! with{CharacterBoneName="old_name"}};
        var binding=new AttachmentBinding(Guid.NewGuid(),id,"prop",1,TransformTRS.Identity,AttachmentScope.AuthoredExportable,"primary",grip);
        var frame=new AnimationEvaluator().Evaluate(new(rig,rig,new("still",new(30,1),1),0,PreviewProfile.RawAuthoring,attachments:[binding]));
        Assert.Empty(frame.DisplayAttachments);
        Assert.Contains(frame.Diagnostics,d=>d.Code=="attachment_secondary_parent_mismatch");
    }

    [Fact]
    public void PropOwnerHashAndFrameGuardsRejectChangedAssets()
    {
        Guid id=Guid.NewGuid();var skeleton=new SkeletonRenderData([
            new BoneRenderData("holder",-1,Matrix4x4.Identity,Matrix4x4.CreateTranslation(.2f,0,0),false),
            new BoneRenderData("support",0,Matrix4x4.Identity,Matrix4x4.CreateTranslation(.5f,0,0),false)],Matrix4x4.Identity);
        var asset=new AttachmentRenderAsset(id,"prop",[],skeleton){ContentSha256=new string('a',64)};
        var frames=AttachmentGripFrames.Read(asset);
        var grip=new AttachmentGripCalibration{PropAssetId=id,PropContentSha256=asset.ContentSha256,PrimaryPropFrame=frames[0]};
        Assert.True(AttachmentGripFrames.Matches(grip,asset,out _));
        Assert.False(AttachmentGripFrames.Matches(grip,asset with{ContentSha256=new string('b',64)},out _));
        var reordered=asset with{BindSkeleton=skeleton with{Bones=[skeleton.Bones[1],skeleton.Bones[0]]}};
        Assert.False(AttachmentGripFrames.Matches(grip,reordered,out _));
        Assert.Throws<ArgumentException>(()=>new AttachmentBinding(Guid.NewGuid(),Guid.NewGuid(),"foreign",1,TransformTRS.Identity,AttachmentScope.PreviewOnly,"primary",grip));
        Assert.ThrowsAny<ArgumentException>(()=>new AttachmentBinding(Guid.NewGuid(),id,"unguarded",1,TransformTRS.Identity,AttachmentScope.PreviewOnly,gripCalibration:grip));
    }

    [Fact]
    public void GripFramesRoundTripInTheOwningProjectWithoutEmbeddingGeometry()
    {
        string directory=RpackTestData.CreateTemporaryDirectory();
        try
        {
            Guid source=Guid.NewGuid(),prop=Guid.NewGuid();var grip=Grip(prop);
            var binding=new AttachmentBinding(Guid.NewGuid(),prop,"prop",1,TransformTRS.Identity,AttachmentScope.PreviewOnly,"primary",grip);
            var project=DlraProject.Create("grips") with
            {
                Assets=[new(){Id=source,Kind=ProjectAssetKind.SourceAnimation,RelativePath="source.fbx"},
                    new(){Id=prop,Kind=ProjectAssetKind.RetailGameResource,RelativePath="retail/272/7",ResourceId="rpack:272:generic_prop",ContentSha256=new string('a',64),
                        RetailIdentity=new(){InstallFingerprint="test",ProviderId="test",ProviderPack="generic.rpack",ResourceType=272,ResourceIndex=7,ResourceName="generic_prop",Precedence=0,ContentSha256=new string('a',64)}}],
                Animations=[new(){Name="clip",SourceAssetId=source,TargetRigId="actor",FrameCount=1,Attachments=[binding]}],
            };
            string path=Path.Combine(directory,"grips.dlraproj");ProjectSerializer.SaveAtomic(project,path);
            var restored=ProjectSerializer.Load(path).Animations[0].Attachments[0].GripCalibration!;
            Assert.Equal(prop,restored.PropAssetId);Assert.Equal(grip.PropContentSha256,restored.PropContentSha256);
            Assert.True(grip.PrimaryPropFrame.Matrix.NearlyEquals(restored.PrimaryPropFrame.Matrix));
            Assert.Equal("secondary",restored.Secondary!.CharacterBoneName);
            Assert.DoesNotContain("vertices",File.ReadAllText(path),StringComparison.OrdinalIgnoreCase);
        }
        finally{RpackTestData.DeleteTemporaryDirectory(directory);}
    }
}
