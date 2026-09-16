using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record HierarchyNodeChoice(Guid EntityId,int Index,string Name,Guid? ParentEntityId,TransformMatrix GlobalFrame);
public sealed record HierarchyParentChoice(Guid? EntityId,string Name,TransformMatrix GlobalFrame);

public sealed partial class RigConformanceWizardViewModel
{
    private FbxHierarchyPreview? _hierarchyPreview;
    private bool _restoringHierarchy;
    private long _hierarchyGeneration;
    [ObservableProperty] private HierarchyNodeChoice? _hierarchyNode;
    [ObservableProperty] private HierarchyParentChoice? _hierarchyParent;
    [ObservableProperty] private bool _hierarchyPreviewEnabled;
    [ObservableProperty] private string _hierarchyStatus="Choose a joint and a new parent. The preview retains its rest position and the surface.";
    public ObservableCollection<HierarchyNodeChoice> HierarchyNodes{get;}=[];
    public ObservableCollection<HierarchyParentChoice> HierarchyParents{get;}=[];
    public IAsyncRelayCommand PreviewHierarchyCommand{get;private set;}=null!;
    public IRelayCommand ApplyHierarchyCommand{get;private set;}=null!;
    public IRelayCommand ClearHierarchyCommand{get;private set;}=null!;
    public IRelayCommand CancelHierarchyCommand{get;private set;}=null!;
    public event EventHandler? HierarchyPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? HierarchyApplyRequested;
    public FbxHierarchyPreview? HierarchyPreview=>_hierarchyPreview;
    public bool CanPreviewHierarchy=>!IsBusy&&HasStudioSession&&HierarchyNode is not null&&HierarchyParent is not null;
    public bool CanApplyHierarchy=>!IsBusy&&_hierarchyPreview is {HasChanges:true};
    public string HierarchyCurrentParent=>HierarchyNodes.FirstOrDefault(n=>n.EntityId==HierarchyNode?.ParentEntityId)?.Name??"World (no parent)";

    private void InitializeHierarchy()
    {
        PreviewHierarchyCommand=new AsyncRelayCommand(PreviewHierarchyAsync,()=>CanPreviewHierarchy);
        ApplyHierarchyCommand=new RelayCommand(ApplyHierarchy,()=>CanApplyHierarchy);
        ClearHierarchyCommand=new RelayCommand(InvalidateHierarchy,()=>_hierarchyPreview is not null);
        CancelHierarchyCommand=new RelayCommand(()=>PreviewHierarchyCommand.Cancel(),()=>PreviewHierarchyCommand.IsRunning);
        PreviewHierarchyCommand.PropertyChanged+=(_,_)=>CancelHierarchyCommand.NotifyCanExecuteChanged();
    }
    private void RestoreHierarchy()
    {
        var id=HierarchyNode?.EntityId;_restoringHierarchy=true;
        try
        {
            PreviewHierarchyCommand?.Cancel();_hierarchyGeneration++;_hierarchyPreview=null;HierarchyPreviewEnabled=false;HierarchyNodes.Clear();
            if(_model?.Package.Document is {RiggingSession:not null} document)
            {
                var observed=RiggingSessions.ObserveSourceHierarchy(document);var globals=new TransformMatrix[document.Bones.Length];
                foreach(var bone in document.Bones)
                {
                    globals[bone.Index]=bone.ParentIndex<0?bone.ExactLocalBindMatrix:globals[bone.ParentIndex]*bone.ExactLocalBindMatrix;
                    HierarchyNodes.Add(new(observed[bone.Index].EntityId,bone.Index,bone.Name,observed[bone.Index].ParentEntityId,globals[bone.Index]));
                }
            }
            HierarchyNode=HierarchyNodes.FirstOrDefault(n=>n.EntityId==id)??HierarchyNodes.FirstOrDefault();RestoreHierarchyParents();
            HierarchyStatus="Select a parent and preview the relationship. Original animation keyframes remain unchanged and require review after reparenting.";
        }
        catch(Exception error) when(RestPoseError(error)){HierarchyStatus="Hierarchy editing needs a current source: "+error.Message;}
        finally{_restoringHierarchy=false;NotifyHierarchy();}
    }
    private void RestoreHierarchyParents()
    {
        HierarchyParents.Clear();HierarchyParents.Add(new(null,"World (no parent)",TransformMatrix.Identity));
        if(HierarchyNode is { } selected)
        {
            var parents=HierarchyNodes.ToDictionary(n=>n.EntityId,n=>n.ParentEntityId);
            foreach(var node in HierarchyNodes)
            {
                Guid? cursor=node.EntityId;bool forbidden=false;
                while(cursor is { } id)
                {if(id==selected.EntityId){forbidden=true;break;}cursor=parents[id];}
                if(!forbidden)HierarchyParents.Add(new(node.EntityId,node.Name,node.GlobalFrame));
            }
            HierarchyParent=HierarchyParents.FirstOrDefault(p=>p.EntityId==selected.ParentEntityId)??HierarchyParents[0];
        }
        else HierarchyParent=HierarchyParents[0];
    }
    partial void OnHierarchyNodeChanged(HierarchyNodeChoice? value)
    {
        if(_restoringHierarchy)return;
        _restoringHierarchy=true;try{RestoreHierarchyParents();}finally{_restoringHierarchy=false;}
        InvalidateHierarchy();
    }
    partial void OnHierarchyParentChanged(HierarchyParentChoice? value)=>InvalidateHierarchy();
    partial void OnHierarchyPreviewEnabledChanged(bool value)
    {if(value){RestPosePreviewEnabled=false;EyeMotionReviewEnabled=false;}HierarchyPreviewChanged?.Invoke(this,EventArgs.Empty);}
    private void InvalidateHierarchy()
    {
        if(_restoringHierarchy)return;
        PreviewHierarchyCommand?.Cancel();_hierarchyGeneration++;_hierarchyPreview=null;NotifyHierarchy();HierarchyPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    private async Task PreviewHierarchyAsync(CancellationToken token)
    {
        if(_model is not { } model||HierarchyNode is not { } node||HierarchyParent is not { } parent)return;
        RouteStudioAction(RigStudioStage.HelpersAndHooks);model=_model!;
        long generation=++_hierarchyGeneration;IsBusy=true;NotifyStateChanged();
        try
        {
            var preview=await Task.Run(()=>FbxHierarchyAuthoring.Preview(model,node.EntityId,parent.EntityId,token),token);
            if(generation!=_hierarchyGeneration||!ReferenceEquals(_model,model))return;
            _hierarchyPreview=preview;HierarchyPreviewEnabled=true;
            HierarchyStatus=preview.HasChanges?$"Preview only: {node.Name} becomes a child of {parent.Name}. {preview.ReorderedBones} bone rows move; {preview.RemappedTracks} animation tracks retain their identities through remapping. Rest frames and surface data stay in place.":"This parent relationship is already current. Nothing will change.";
        }
        catch(OperationCanceledException){if(generation==_hierarchyGeneration)HierarchyStatus="Hierarchy preview cancelled.";}
        catch(Exception error) when(RestPoseError(error)){if(generation==_hierarchyGeneration)HierarchyStatus="The hierarchy was not changed: "+error.Message;}
        finally{IsBusy=false;NotifyStateChanged();NotifyHierarchy();HierarchyPreviewChanged?.Invoke(this,EventArgs.Empty);}
    }
    private void ApplyHierarchy()
    {
        if(_model is not { } model||_hierarchyPreview is not { } preview)return;
        try
        {
            if(!FbxHierarchyAuthoring.TryApply(model,preview,out var result)){HierarchyStatus="The source changed. Preview the current hierarchy again.";return;}
            if(!ReferenceEquals(model,result))HierarchyApplyRequested?.Invoke(this,new(model,result,"Applied the reviewed parent relationship. Rest placement and bone-bound data were preserved; motion requires review."));
            HierarchyStatus="Parent relationship saved. Review animation and the target profile's parent rules before native acceptance.";
        }
        catch(Exception error) when(RestPoseError(error)){HierarchyStatus="Hierarchy edit was not saved: "+error.Message;}
    }
    private void RefreshHierarchyMetadata(FbxModelAuthoringImportResult model)
    {if(_hierarchyPreview is { } preview)_hierarchyPreview=FbxHierarchyAuthoring.RefreshMetadata(preview,model);NotifyHierarchy();}
    private void NotifyHierarchy()
    {
        foreach(string name in new[]{nameof(HierarchyPreview),nameof(HierarchyCurrentParent),nameof(CanPreviewHierarchy),nameof(CanApplyHierarchy)})OnPropertyChanged(name);
        PreviewHierarchyCommand?.NotifyCanExecuteChanged();ApplyHierarchyCommand?.NotifyCanExecuteChanged();ClearHierarchyCommand?.NotifyCanExecuteChanged();
    }
}
