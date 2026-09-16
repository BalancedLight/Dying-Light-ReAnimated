using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record RestNodeChoice(Guid EntityId, int Index, string Name, TransformMatrix GlobalFrame);
public sealed record RestSurfaceChoice(RigRestSurfaceMode Mode,string Label);
public sealed record RestDescendantChoice(RigRestDescendantMode Mode,string Label);

public sealed partial class RigConformanceWizardViewModel
{
    private FbxRestPosePreview? _restPosePreview;
    private long _restPoseGeneration;
    private bool _restoringRestPose;
    [ObservableProperty] private RestNodeChoice? _restPoseNode;
    [ObservableProperty] private RestSurfaceChoice? _restSurfaceMode;
    [ObservableProperty] private RestDescendantChoice? _restDescendantMode;
    [ObservableProperty] private bool _restPosePreviewEnabled;
    [ObservableProperty] private string _restPoseStatus = "Select a joint, choose how its children and surface should behave, then preview the change.";
    public ContactVector RestPosePosition { get; } = new();
    public ContactVector RestPoseRotation { get; } = new();
    public ContactVector RestPoseScale { get; } = new();
    public ObservableCollection<RestNodeChoice> RestPoseNodes { get; } = [];
    public IReadOnlyList<RestSurfaceChoice> RestSurfaceChoices { get; } = [new(RigRestSurfaceMode.PreserveSurface,"Refit joint — keep the surface still"),new(RigRestSurfaceMode.BakePose,"Bake the posed shape into the rest mesh")];
    public IReadOnlyList<RestDescendantChoice> RestDescendantChoices { get; } = [new(RigRestDescendantMode.KeepGlobal,"Keep child joints and helpers in place"),new(RigRestDescendantMode.FollowLocal,"Move descendants with this joint")];
    public IAsyncRelayCommand PreviewRestPoseCommand { get; private set; } = null!;
    public IRelayCommand ApplyRestPoseCommand { get; private set; } = null!;
    public IRelayCommand ClearRestPoseCommand { get; private set; } = null!;
    public IRelayCommand CancelRestPoseCommand { get; private set; } = null!;
    public event EventHandler? RestPosePreviewChanged;
    public event EventHandler<BodyModelEventArgs>? RestPoseApplyRequested;
    public FbxRestPosePreview? RestPosePreview => _restPosePreview;
    public bool CanPreviewRestPose => !IsBusy && HasStudioSession && RestPoseNode is not null &&
        RestPosePosition.Value.IsFinite && RestPoseRotation.Value.IsFinite && RestPoseScale.Value.IsFinite &&
        RestPoseScale.X>0 && RestPoseScale.Y>0 && RestPoseScale.Z>0;
    public bool CanApplyRestPose => !IsBusy && _restPosePreview is {HasChanges:true};

    private void InitializeRestPose()
    {
        RestSurfaceMode=RestSurfaceChoices[0];RestDescendantMode=RestDescendantChoices[0];RestPoseScale.Set(Vector3D.One);
        foreach(var v in new[]{RestPosePosition,RestPoseRotation,RestPoseScale})v.PropertyChanged+=(_,_)=>InvalidateRestPose();
        PreviewRestPoseCommand=new AsyncRelayCommand(PreviewRestPoseAsync,()=>CanPreviewRestPose);
        ApplyRestPoseCommand=new RelayCommand(ApplyRestPose,()=>CanApplyRestPose);
        ClearRestPoseCommand=new RelayCommand(InvalidateRestPose,()=>_restPosePreview is not null);
        CancelRestPoseCommand=new RelayCommand(()=>PreviewRestPoseCommand.Cancel(),()=>PreviewRestPoseCommand.IsRunning);
        PreviewRestPoseCommand.PropertyChanged+=(_,_)=>CancelRestPoseCommand.NotifyCanExecuteChanged();
    }
    private void RestoreRestPose()
    {
        var id=RestPoseNode?.EntityId;_restoringRestPose=true;
        try
        {
            PreviewRestPoseCommand?.Cancel();_restPoseGeneration++;_restPosePreview=null;RestPosePreviewEnabled=false;RestPoseNodes.Clear();
            if(_model?.Package.Document is {RiggingSession:not null} document)
            {
                var observed=RiggingSessions.ObserveSourceHierarchy(document);var globals=new TransformMatrix[document.Bones.Length];
                foreach(var bone in document.Bones)
                {
                    globals[bone.Index]=bone.ParentIndex<0?bone.ExactLocalBindMatrix:globals[bone.ParentIndex]*bone.ExactLocalBindMatrix;
                    if(bone.Kind is BoneKind.Root or BoneKind.Deform) RestPoseNodes.Add(new(observed[bone.Index].EntityId,bone.Index,bone.Name,globals[bone.Index]));
                }
            }
            RestPoseNode=RestPoseNodes.FirstOrDefault(n=>n.EntityId==id)??RestPoseNodes.FirstOrDefault();
            RestoreRestPoseFields();RestPoseStatus="Select a joint and preview its rest edit. Source clips are retained and require a fresh motion review after applying.";
        }
        catch(Exception error) when(RestPoseError(error)){RestPoseStatus="Rest editing needs a current source hierarchy: "+error.Message;}
        finally{_restoringRestPose=false;NotifyRestPose();}
    }
    private void RestoreRestPoseFields()
    {RestPosePosition.Set(RestPoseNode?.GlobalFrame.Translation??Vector3D.Zero);RestPoseRotation.Set(Vector3D.Zero);RestPoseScale.Set(Vector3D.One);}
    partial void OnRestPoseNodeChanged(RestNodeChoice? value)
    {
        if(_restoringRestPose)return;
        _restoringRestPose=true;try{RestoreRestPoseFields();}finally{_restoringRestPose=false;}
        InvalidateRestPose();
    }
    partial void OnRestSurfaceModeChanged(RestSurfaceChoice? value)=>InvalidateRestPose();
    partial void OnRestDescendantModeChanged(RestDescendantChoice? value)=>InvalidateRestPose();
    partial void OnRestPosePreviewEnabledChanged(bool value)=>RestPosePreviewChanged?.Invoke(this,EventArgs.Empty);
    private void InvalidateRestPose()
    {
        if(_restoringRestPose)return;
        PreviewRestPoseCommand?.Cancel();_restPoseGeneration++;_restPosePreview=null;
        NotifyRestPose();RestPosePreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    private TransformMatrix RestDesiredFrame()
    {
        var frame=RestPoseNode!.GlobalFrame;var degrees=RestPoseRotation.Value;var scale=RestPoseScale.Value;
        var rotation=TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitX,degrees.X*Math.PI/180))*
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY,degrees.Y*Math.PI/180))*
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitZ,degrees.Z*Math.PI/180));
        var result=frame*rotation*new TransformMatrix(scale.X,0,0,0,0,scale.Y,0,0,0,0,scale.Z,0,0,0,0,1);
        return result with{M14=RestPosePosition.X,M24=RestPosePosition.Y,M34=RestPosePosition.Z};
    }
    private async Task PreviewRestPoseAsync(CancellationToken token)
    {
        if(_model is not { } model || RestPoseNode is not { } node || !CanPreviewRestPose)return;
        RouteStudioAction(RigStudioStage.Fit);model=_model!;
        var frame=RestDesiredFrame();var descendants=RestDescendantMode!.Mode;var surfaces=RestSurfaceMode!.Mode;
        long generation=++_restPoseGeneration;IsBusy=true;NotifyStateChanged();
        try
        {
            var preview=await Task.Run(()=>FbxRestPoseAuthoring.Preview(model,node.EntityId,frame,descendants,surfaces,token),token);
            if(generation!=_restPoseGeneration||!ReferenceEquals(model,_model))return;
            _restPosePreview=preview;RestPosePreviewEnabled=true;
            var report=preview.Report;
            RestPoseStatus=preview.HasChanges?$"Preview only: {report.ChangedNodes} changed joint/helper frames; {report.UpdatedInverseBinds} inverse binds updated. Maximum visible surface movement {report.MaximumVisibleDisplacement*1000:0.###} mm. Review the surface, joints and helpers before applying.":"The requested rest frame is already current. Nothing will be changed.";
        }
        catch(OperationCanceledException){if(generation==_restPoseGeneration)RestPoseStatus="Rest-pose preview cancelled.";}
        catch(Exception error) when(RestPoseError(error)){if(generation==_restPoseGeneration)RestPoseStatus="Rest edit was not applied: "+error.Message;}
        finally{IsBusy=false;NotifyStateChanged();NotifyRestPose();RestPosePreviewChanged?.Invoke(this,EventArgs.Empty);}
    }
    private void ApplyRestPose()
    {
        if(_model is not { } model||_restPosePreview is not { } preview)return;
        try
        {
            if(!FbxRestPoseAuthoring.TryApply(model,preview,out var result)){RestPoseStatus="The source changed. Preview the current joint again.";return;}
            if(!ReferenceEquals(model,result))RestPoseApplyRequested?.Invoke(this,new(model,result,"Applied the reviewed rest-pose transaction. Original source and animation data were retained."));
            RestPoseStatus="Rest edit saved. Review animation, helper and native binding behavior before acceptance.";
        }
        catch(Exception error) when(RestPoseError(error)){RestPoseStatus="Rest edit was not saved: "+error.Message;}
    }
    private void RefreshRestPoseMetadata(FbxModelAuthoringImportResult model)
    {if(_restPosePreview is { } preview)_restPosePreview=FbxRestPoseAuthoring.RefreshMetadata(preview,model);NotifyRestPose();}
    private static bool RestPoseError(Exception error)=>error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException;
    private void NotifyRestPose()
    {
        OnPropertyChanged(nameof(RestPosePreview));OnPropertyChanged(nameof(CanPreviewRestPose));OnPropertyChanged(nameof(CanApplyRestPose));
        PreviewRestPoseCommand?.NotifyCanExecuteChanged();ApplyRestPoseCommand?.NotifyCanExecuteChanged();ClearRestPoseCommand?.NotifyCanExecuteChanged();
    }
}
