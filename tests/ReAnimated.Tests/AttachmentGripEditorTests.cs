using System.Numerics;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class AttachmentGripEditorTests
{
    private static RigDefinition Rig()=>new("character","character",
        [new(0,"primary",-1,TransformTRS.Identity,BoneKind.Prop),new(1,"secondary",0,TransformTRS.Identity,BoneKind.Prop)]);
    private static AttachmentRenderAsset Asset(Guid id)=>new(id,"generic_prop",[],new SkeletonRenderData(
        [new("holder",-1,Matrix4x4.Identity,Matrix4x4.Identity,false),new("support",0,Matrix4x4.Identity,Matrix4x4.CreateTranslation(.2f,0,0),false)],Matrix4x4.Identity))
        {ContentSha256=new string('a',64)};
    private static AttachmentEditorViewModel Editor(AttachmentBinding binding,RigDefinition? rig=null)
    {
        rig ??= Rig();
        var editor=new AttachmentEditorViewModel();editor.ReplaceParentBones(rig);
        editor.ReplaceBindings([binding],new Dictionary<Guid,ProjectAssetReference>{{binding.AssetId,new(){Id=binding.AssetId,Kind=ProjectAssetKind.SourceAnimation,RelativePath="generic.fbx"}}},rig);
        editor.SelectedAttachment=editor.Attachments[0];return editor;
    }

    private static RigDefinition IkRig()=>new("character","character",
        [new(0,"root",-1,TransformTRS.Identity,BoneKind.Root),new(1,"primary",0,TransformTRS.Identity,BoneKind.Prop),
         new(2,"upper",0,TransformTRS.Identity,BoneKind.Deform),new(3,"joint",2,TransformTRS.Identity,BoneKind.Deform),
         new(4,"hand",3,TransformTRS.Identity,BoneKind.Deform),new(5,"contact",4,TransformTRS.Identity,BoneKind.Helper)]);

    [Fact]
    public void EditorKeepsCharacterAndPropSelectionsSeparate()
    {
        Guid prop=Guid.NewGuid();var binding=new AttachmentBinding(Guid.NewGuid(),prop,"prop",0,TransformTRS.Identity,AttachmentScope.PreviewOnly,"primary");
        var editor=Editor(binding);editor.ReplaceGripAsset(Asset(prop));
        editor.UseGripCalibration=true;editor.SelectedPropGrip=editor.PropGripFrames[0];
        editor.UseSecondaryGrip=true;editor.SelectedSecondaryPropGrip=editor.PropGripFrames[1];editor.SecondaryCharacterBone=editor.ParentBones[1];
        editor.SecondaryOffsetY=.025;
        var calibration=editor.CreateGripCalibration(binding.Id,prop)!;
        Assert.Equal(prop,calibration.PropAssetId);Assert.Equal("holder",calibration.PrimaryPropFrame.NodeName);
        Assert.Equal("support",calibration.Secondary!.PropFrame.NodeName);Assert.Equal("secondary",calibration.Secondary.CharacterBoneName);
        Assert.Equal(.025,calibration.Secondary.CharacterLocalOffset.Translation.Y);
        Assert.Null(editor.CreateGripCalibration(Guid.NewGuid(),prop));
    }

    [Fact]
    public void MissingDecodeCannotSilentlyDiscardSavedCalibration()
    {
        Guid prop=Guid.NewGuid();var asset=Asset(prop);
        var calibration=new AttachmentGripCalibration{PropAssetId=prop,PropContentSha256=asset.ContentSha256!,PrimaryPropFrame=AttachmentGripFrames.Read(asset)[0]};
        var binding=new AttachmentBinding(Guid.NewGuid(),prop,"prop",0,TransformTRS.Identity,AttachmentScope.PreviewOnly,"primary",calibration);
        var editor=Editor(binding);editor.ReplaceGripAsset(null);
        Assert.True(editor.UseGripCalibration);Assert.Null(editor.SelectedPropGrip);
        Assert.Throws<InvalidOperationException>(()=>editor.CreateGripCalibration(binding.Id,prop));
        editor.ReplaceGripAsset(asset);Assert.NotNull(editor.CreateGripCalibration(binding.Id,prop));
        editor.UseGripCalibration=false;Assert.Null(editor.CreateGripCalibration(binding.Id,prop));
    }

    [Fact]
    public void OptionalSecondaryIkSavesGuardedChainAndPoleSettings()
    {
        Guid prop=Guid.NewGuid();var binding=new AttachmentBinding(Guid.NewGuid(),prop,"prop",1,TransformTRS.Identity,AttachmentScope.AuthoredExportable,"primary");
        var editor=Editor(binding,IkRig());editor.ReplaceGripAsset(Asset(prop));
        editor.UseGripCalibration=true;editor.SelectedPropGrip=editor.PropGripFrames[0];editor.UseSecondaryGrip=true;
        editor.SelectedSecondaryPropGrip=editor.PropGripFrames[1];editor.SecondaryCharacterBone=editor.ParentBones.Single(b=>b.Name=="contact");
        editor.UseSecondaryIk=true;editor.SecondaryIkRoot=editor.SecondaryIkBones.Single(b=>b.Name=="upper");
        editor.SecondaryIkJoint=editor.SecondaryIkBones.Single(b=>b.Name=="joint");editor.SecondaryIkEnd=editor.SecondaryIkBones.Single(b=>b.Name=="hand");
        editor.SecondaryIkPoleX=.25;editor.SecondaryIkPoleY=-.5;editor.SecondaryIkPoleZ=.75;editor.SecondaryIkPoleInPrimaryGripSpace=false;
        editor.SecondaryIkMatchOrientation=false;editor.SecondaryIkWeight=.6;
        var calibration=editor.CreateGripCalibration(binding.Id,prop)!;var ik=calibration.Secondary!.Ik!;
        Assert.Equal((2,3,4),(ik.RootBoneIndex,ik.JointBoneIndex,ik.EndBoneIndex));
        Assert.Equal("upper",ik.RootBoneName);Assert.Equal("joint",ik.JointBoneName);Assert.Equal("hand",ik.EndBoneName);
        Assert.Equal<double>([.25,-.5,.75],ik.Pole);Assert.False(ik.PoleInPrimaryGripSpace);Assert.False(ik.MatchOrientation);Assert.Equal(.6,ik.Weight);
    }

    [Fact]
    public void SavedUnresolvedSecondaryIkChainIsRetainedForLaterRebind()
    {
        Guid prop=Guid.NewGuid();var saved=new AttachmentGripCalibration{PropAssetId=prop,PropContentSha256=new string('a',64),
            PrimaryPropFrame=AttachmentGripFrames.Read(Asset(prop))[0],Secondary=new(){CharacterBoneIndex=5,CharacterBoneName="contact",
                PropFrame=AttachmentGripFrames.Read(Asset(prop))[1],Ik=new(){RootBoneIndex=2,RootBoneName="upper",JointBoneIndex=3,JointBoneName="joint",
                    EndBoneIndex=99,EndBoneName="removed_hand",Pole=[0,0,1],Weight=.5}}};
        var binding=new AttachmentBinding(Guid.NewGuid(),prop,"prop",1,TransformTRS.Identity,AttachmentScope.AuthoredExportable,"primary",saved);
        var editor=Editor(binding,IkRig());editor.ReplaceGripAsset(Asset(prop));
        Assert.True(editor.UseSecondaryIk);Assert.Equal("removed_hand",editor.SecondaryIkEnd!.Name);
        Assert.Equal("saved unresolved IK node",editor.SecondaryIkEnd.SemanticRole);
        var roundTrip=editor.CreateGripCalibration(binding.Id,prop)!;
        Assert.Equal(99,roundTrip.Secondary!.Ik!.EndBoneIndex);Assert.Equal("removed_hand",roundTrip.Secondary.Ik.EndBoneName);
    }
}
