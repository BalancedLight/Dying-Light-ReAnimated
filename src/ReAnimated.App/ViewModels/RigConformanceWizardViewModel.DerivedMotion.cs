using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record DerivedSourceChoice(Guid Id,string Name);
public sealed partial class RigConformanceWizardViewModel
{
    private FbxDerivedMotionPreview? _derivedMotionPreview;
    private bool _restoringDerivedMotion;
    private long _derivedGeneration;
    [ObservableProperty] private DerivedSourceChoice? _derivedSource;
    [ObservableProperty] private string _derivedName="compatible-motion";
    [ObservableProperty] private int _derivedSampleMultiplier=2;
    [ObservableProperty] private bool _derivedPreviewEnabled;
    [ObservableProperty] private string _derivedMotionStatus="Choose an original FBX clip to derive motion for the current rig. Source clips remain unchanged.";
    public ObservableCollection<DerivedSourceChoice> DerivedSources{get;}=[];
    public IAsyncRelayCommand DeriveMotionCommand{get;private set;}=null!;
    public IRelayCommand SaveDerivedMotionCommand{get;private set;}=null!;
    public IRelayCommand CancelDerivedMotionCommand{get;private set;}=null!;
    public IRelayCommand ClearDerivedMotionCommand{get;private set;}=null!;
    public event EventHandler? DerivedMotionPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? DerivedMotionApplyRequested;
    public FbxDerivedMotionPreview? DerivedMotionPreview=>_derivedMotionPreview;
    public bool CanDeriveMotion=>!IsBusy&&HasStudioSession&&DerivedSource is not null&&DerivedSampleMultiplier is >=1 and <=8&&!string.IsNullOrWhiteSpace(DerivedName);
    public bool CanSaveDerivedMotion=>!IsBusy&&_derivedMotionPreview is {CanApply:true};
    private void InitializeDerivedMotion()
    {
        DeriveMotionCommand=new AsyncRelayCommand(DeriveMotionAsync,()=>CanDeriveMotion);
        SaveDerivedMotionCommand=new RelayCommand(SaveDerivedMotion,()=>CanSaveDerivedMotion);
        CancelDerivedMotionCommand=new RelayCommand(()=>DeriveMotionCommand.Cancel(),()=>DeriveMotionCommand.IsRunning);
        ClearDerivedMotionCommand=new RelayCommand(InvalidateDerivedMotion,()=>_derivedMotionPreview is not null);
        DeriveMotionCommand.PropertyChanged+=(_,_)=>CancelDerivedMotionCommand.NotifyCanExecuteChanged();
    }
    private void RestoreDerivedMotion()
    {
        var id=DerivedSource?.Id;_restoringDerivedMotion=true;
        try
        {
            DeriveMotionCommand?.Cancel();_derivedGeneration++;_derivedMotionPreview=null;DerivedPreviewEnabled=false;DerivedSources.Clear();
            if(_model is { } model)
                foreach(var source in model.Package.Document.AnimationClips.Where(c=>c.DerivedMotion is null&&model.AnimationClips.ContainsKey(c.Id)))
                    DerivedSources.Add(new(source.Id,source.DisplayName));
            DerivedSource=DerivedSources.FirstOrDefault(c=>c.Id==id)??DerivedSources.FirstOrDefault();
            SuggestDerivedName();
            DerivedMotionStatus="This path derives absolute-local FBX motion through source and target bind bases. Native banks, additive references and runtime events/IK need separate validation.";
        }
        finally{_restoringDerivedMotion=false;NotifyDerivedMotion();}
    }
    private void SuggestDerivedName()
    {
        string basis=(DerivedSource?.Name??"motion")+" - compatible";string name=basis;int index=2;
        while(_model?.Package.Document.AnimationClips.Any(c=>c.DisplayName.Equals(name,StringComparison.OrdinalIgnoreCase))==true)name=basis+" "+index++;
        DerivedName=name;
    }
    partial void OnDerivedSourceChanged(DerivedSourceChoice? value)
    {if(!_restoringDerivedMotion){SuggestDerivedName();InvalidateDerivedMotion();}}
    partial void OnDerivedNameChanged(string value)=>InvalidateDerivedMotion();
    partial void OnDerivedSampleMultiplierChanged(int value)=>InvalidateDerivedMotion();
    partial void OnDerivedPreviewEnabledChanged(bool value)
    {
        if(value){HierarchyPreviewEnabled=false;RestPosePreviewEnabled=false;EyeMotionReviewEnabled=false;StressPreviewEnabled=false;}
        DerivedMotionPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    private void InvalidateDerivedMotion()
    {
        if(_restoringDerivedMotion)return;
        DeriveMotionCommand?.Cancel();_derivedGeneration++;_derivedMotionPreview=null;NotifyDerivedMotion();DerivedMotionPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    private async Task DeriveMotionAsync(CancellationToken token)
    {
        if(_model is not { } model||DerivedSource is not { } selected)return;
        RouteStudioAction(RigStudioStage.Animate);model=_model!;
        string name=DerivedName.Trim();int multiplier=DerivedSampleMultiplier;long generation=++_derivedGeneration;
        IsBusy=true;NotifyStateChanged();
        try
        {
            var preview=await Task.Run(()=>FbxDerivedMotionAuthoring.Preview(model,selected.Id,name,multiplier,token),token);
            if(generation!=_derivedGeneration||!ReferenceEquals(model,_model))return;
            _derivedMotionPreview=preview;var report=preview.Report;
            DerivedMotionStatus=$"{report.MappedTargetCount} mapped joints; {report.UnmappedTargetCount} nodes follow their target parents. {report.OutputFrameCount} frames at {report.OutputFrameRate.FramesPerSecond:0.###} FPS. " +
                $"Maximum measured error: {report.MaximumPositionError*1000:0.####} mm, {report.MaximumAngularErrorRadians*180/Math.PI:0.####} degrees, {report.MaximumLinearError:G3} linear-matrix error. "+
                (preview.CanApply?"Preview uses recorded motion. Save as a separate clip after review; export remains off by default.":"The candidate cannot be saved within the current representation/tolerance limits. ")+string.Join(" ",report.Diagnostics);
            DerivedPreviewEnabled=preview.CanApply;
        }
        catch(OperationCanceledException){if(generation==_derivedGeneration)DerivedMotionStatus="Motion derivation cancelled.";}
        catch(Exception error) when(RestPoseError(error)){if(generation==_derivedGeneration)DerivedMotionStatus="Motion was not derived: "+error.Message;}
        finally{IsBusy=false;NotifyStateChanged();NotifyDerivedMotion();DerivedMotionPreviewChanged?.Invoke(this,EventArgs.Empty);}
    }
    private void SaveDerivedMotion()
    {
        if(_model is not { } model||_derivedMotionPreview is not {CanApply:true} preview)return;
        if(!FbxDerivedMotionAuthoring.TryApply(model,preview,out var result)){DerivedMotionStatus="The source changed. Derive the current clip again.";return;}
        DerivedMotionApplyRequested?.Invoke(this,new(model,result,"Saved a separately identified derived clip. Original source curves were retained; native acceptance remains unverified."));
        DerivedMotionStatus="Derived clip saved. Enable its Use checkbox only after reviewing motion and export component ownership.";
    }
    private void NotifyDerivedMotion()
    {
        OnPropertyChanged(nameof(DerivedMotionPreview));OnPropertyChanged(nameof(CanDeriveMotion));OnPropertyChanged(nameof(CanSaveDerivedMotion));
        DeriveMotionCommand?.NotifyCanExecuteChanged();SaveDerivedMotionCommand?.NotifyCanExecuteChanged();ClearDerivedMotionCommand?.NotifyCanExecuteChanged();
    }
    private void RefreshDerivedMotionMetadata(FbxModelAuthoringImportResult model)
    {
        if(_derivedMotionPreview is { } preview)_derivedMotionPreview=FbxDerivedMotionAuthoring.RefreshMetadata(preview,model);
        NotifyDerivedMotion();
    }
}
