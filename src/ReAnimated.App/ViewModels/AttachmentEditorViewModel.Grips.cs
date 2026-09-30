using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class AttachmentEditorViewModel
{
    private AttachmentRenderAsset? _gripAsset;
    private TransformTRS _secondaryOffset=TransformTRS.Identity;
    [ObservableProperty] private bool _useGripCalibration;
    [ObservableProperty] private bool _useSecondaryGrip;
    [ObservableProperty] private AttachmentGripFrame? _selectedPropGrip;
    [ObservableProperty] private AttachmentGripFrame? _selectedSecondaryPropGrip;
    [ObservableProperty] private AttachmentBoneOptionViewModel? _secondaryCharacterBone;
    [ObservableProperty] private bool _useSecondaryIk;
    [ObservableProperty] private AttachmentBoneOptionViewModel? _secondaryIkRoot;
    [ObservableProperty] private AttachmentBoneOptionViewModel? _secondaryIkJoint;
    [ObservableProperty] private AttachmentBoneOptionViewModel? _secondaryIkEnd;
    [ObservableProperty] private double _secondaryIkPoleX;
    [ObservableProperty] private double _secondaryIkPoleY;
    [ObservableProperty] private double _secondaryIkPoleZ = 1;
    [ObservableProperty] private bool _secondaryIkPoleInPrimaryGripSpace = true;
    [ObservableProperty] private bool _secondaryIkMatchOrientation = true;
    [ObservableProperty] private double _secondaryIkWeight = 1;
    [ObservableProperty] private double _secondaryOffsetX;
    [ObservableProperty] private double _secondaryOffsetY;
    [ObservableProperty] private double _secondaryOffsetZ;
    [ObservableProperty] private string _gripAssetStatus="Select an attached prop to inspect its own frames.";
    [ObservableProperty] private string _gripContactStatus="Secondary contact is measured only; optional secondary IK is disabled until enabled; no hand IK or installed equipment is implied.";
    public ObservableCollection<AttachmentGripFrame> PropGripFrames { get; }=[];
    public ObservableCollection<AttachmentBoneOptionViewModel> SecondaryIkBones { get; }=[];

    public void ReplaceGripAsset(AttachmentRenderAsset? asset)
    {
        _gripAsset=asset;PropGripFrames.Clear();
        if(asset is not null)foreach(var frame in AttachmentGripFrames.Read(asset))PropGripFrames.Add(frame);
        GripAssetStatus=asset is null?"The selected prop has not been decoded.":
            $"Prop owner: {asset.DisplayName}; {PropGripFrames.Count} valid named frames. No native equipment configuration is emitted.";
        LoadGrip(SelectedAttachment?.Binding.GripCalibration);
    }

    private void LoadGrip(AttachmentGripCalibration? grip)
    {
        UseGripCalibration=grip is not null;UseSecondaryGrip=grip?.Secondary is not null;
        SelectedPropGrip=Find(grip?.PrimaryPropFrame);SelectedSecondaryPropGrip=Find(grip?.Secondary?.PropFrame);
        SecondaryCharacterBone=grip?.Secondary is { } secondary?ParentBones.FirstOrDefault(b=>b.Index==secondary.CharacterBoneIndex&&
            b.Name.Equals(secondary.CharacterBoneName,StringComparison.OrdinalIgnoreCase)):null;
        _secondaryOffset=grip?.Secondary?.CharacterLocalOffset??TransformTRS.Identity;
        SecondaryOffsetX=_secondaryOffset.Translation.X;SecondaryOffsetY=_secondaryOffset.Translation.Y;SecondaryOffsetZ=_secondaryOffset.Translation.Z;
        AttachmentSecondaryIk? ik=grip?.Secondary?.Ik;
        UseSecondaryIk=ik is not null;
        RefreshSecondaryIkBones(ik);
        SecondaryIkRoot=ResolveSecondaryIkBone(ik?.RootBoneIndex,ik?.RootBoneName);
        SecondaryIkJoint=ResolveSecondaryIkBone(ik?.JointBoneIndex,ik?.JointBoneName);
        SecondaryIkEnd=ResolveSecondaryIkBone(ik?.EndBoneIndex,ik?.EndBoneName);
        SecondaryIkPoleX=ik?.Pole is { Length:3 } poleX?poleX[0]:0;
        SecondaryIkPoleY=ik?.Pole is { Length:3 } poleY?poleY[1]:0;
        SecondaryIkPoleZ=ik?.Pole is { Length:3 } poleZ?poleZ[2]:1;
        SecondaryIkPoleInPrimaryGripSpace=ik?.PoleInPrimaryGripSpace??true;
        SecondaryIkMatchOrientation=ik?.MatchOrientation??true;
        SecondaryIkWeight=ik?.Weight??1;
        if(grip is not null && (_gripAsset is null||!AttachmentGripFrames.Matches(grip,_gripAsset,out _)))
            GripAssetStatus="Saved grip owner or frames do not match the decoded prop. Review and recalibrate; the saved binding is retained.";
        AttachmentGripFrame? Find(AttachmentGripFrame? expected)=>expected is null?null:PropGripFrames.FirstOrDefault(f=>
            f.NodeIndex==expected.NodeIndex&&f.NodeName.Equals(expected.NodeName,StringComparison.OrdinalIgnoreCase)&&f.Matrix.NearlyEquals(expected.Matrix,1e-6));
    }

    public AttachmentGripCalibration? CreateGripCalibration(Guid bindingId,Guid assetId)
    {
        if(bindingId!=SelectedAttachment?.Id||!UseGripCalibration)return null;
        if(_gripAsset is not {ContentSha256: { } hash} asset||asset.ProjectAssetId!=assetId||SelectedPropGrip is null)
            throw new InvalidOperationException("Choose a frame from the decoded owning prop before applying grip calibration.");
        if(UseSecondaryIk&&!UseSecondaryGrip)
            throw new InvalidOperationException("Enable the secondary hand contact before enabling its IK driver.");
        AttachmentSecondaryGrip? secondary=null;
        if(UseSecondaryGrip)
        {
            if(SecondaryCharacterBone is not { } hand||SelectedSecondaryPropGrip is not { } support)
                throw new InvalidOperationException("Choose both the second character frame and its prop-side contact frame.");
            if(hand.Index==SelectedParentBone?.Index)throw new InvalidOperationException("Choose a distinct secondary hand/frame.");
            AttachmentSecondaryIk? ik=null;
            if(UseSecondaryIk)
            {
                RequireSecondaryIkBone(SecondaryIkRoot,"root");
                RequireSecondaryIkBone(SecondaryIkJoint,"joint");
                RequireSecondaryIkBone(SecondaryIkEnd,"end");
                ik=new AttachmentSecondaryIk
                {
                    RootBoneIndex=SecondaryIkRoot!.Index,RootBoneName=SecondaryIkRoot.Name,
                    JointBoneIndex=SecondaryIkJoint!.Index,JointBoneName=SecondaryIkJoint.Name,
                    EndBoneIndex=SecondaryIkEnd!.Index,EndBoneName=SecondaryIkEnd.Name,
                    Pole=[SecondaryIkPoleX,SecondaryIkPoleY,SecondaryIkPoleZ],
                    PoleInPrimaryGripSpace=SecondaryIkPoleInPrimaryGripSpace,
                    MatchOrientation=SecondaryIkMatchOrientation,Weight=SecondaryIkWeight,
                };
                ik.Validate();
            }
            secondary=new(){CharacterBoneIndex=hand.Index,CharacterBoneName=hand.Name,PropFrame=support,
                CharacterLocalOffset=_secondaryOffset with{Translation=new Vector3D(SecondaryOffsetX,SecondaryOffsetY,SecondaryOffsetZ)},Ik=ik};
        }
        var calibration=new AttachmentGripCalibration{PropAssetId=assetId,PropContentSha256=hash,PrimaryPropFrame=SelectedPropGrip,Secondary=secondary};
        calibration.Validate(assetId);
        if(!AttachmentGripFrames.Matches(calibration,asset,out string reason))throw new InvalidOperationException(reason);
        return calibration;
    }

    private void RefreshSecondaryIkBones(AttachmentSecondaryIk? saved)
    {
        SecondaryIkBones.Clear();
        foreach(AttachmentBoneOptionViewModel bone in ParentBones)
            SecondaryIkBones.Add(bone);
        if(saved is null)return;
        AddSaved(saved.RootBoneIndex,saved.RootBoneName);
        AddSaved(saved.JointBoneIndex,saved.JointBoneName);
        AddSaved(saved.EndBoneIndex,saved.EndBoneName);

        void AddSaved(int index,string name)
        {
            if(index<0||string.IsNullOrWhiteSpace(name)||SecondaryIkBones.Any(b=>b.Index==index&&
                b.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))return;
            SecondaryIkBones.Add(new AttachmentBoneOptionViewModel(index,name,BoneKind.Helper,"saved unresolved IK node"));
        }
    }

    private AttachmentBoneOptionViewModel? ResolveSecondaryIkBone(int? index,string? name)=>
        index is { } i&&name is { Length: >0 }
            ? SecondaryIkBones.FirstOrDefault(b=>b.Index==i&&b.Name.Equals(name,StringComparison.OrdinalIgnoreCase))
            : null;

    private void RequireSecondaryIkBone(AttachmentBoneOptionViewModel? selected,string role)
    {
        if(selected is null)throw new InvalidOperationException($"Choose a secondary IK {role} bone.");
        AttachmentBoneOptionViewModel? current=ParentBones.FirstOrDefault(b=>b.Index==selected.Index&&
            b.Name.Equals(selected.Name,StringComparison.OrdinalIgnoreCase));
        if(current is null&&!string.Equals(selected.SemanticRole,"saved unresolved IK node",StringComparison.Ordinal))
            throw new InvalidOperationException($"The selected secondary IK {role} bone is no longer present in the character rig.");
    }
}
