using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private static readonly JsonSerializerOptions DoctorJson=new()
    {
        PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new JsonStringEnumConverter()},
    };
    private Func<string?>? _doctorRulePicker;
    private ImmutableArray<RigDoctorContactRule> _doctorRules=[];
    private RigDoctorPreview? _doctorPreview;
    private long _doctorGeneration;
    private bool _restoringDoctorSelection;
    [ObservableProperty] private RigDoctorRow? _doctorSelectedRow;
    [ObservableProperty] private ContactComponentChoice? _doctorComponent;
    [ObservableProperty] private bool _doctorReviewed;
    [ObservableProperty] private bool _doctorPreviewEnabled;
    [ObservableProperty] private string _doctorRuleLabel="No repair rules selected.";
    [ObservableProperty] private string _doctorStatus="Load contact requirements, diagnose the current rig and review the proposed changes. Native acceptance remains separate.";
    public ObservableCollection<RigDoctorRow> DoctorRows { get; }=[];
    public ObservableCollection<ContactComponentChoice> DoctorComponents { get; }=[];
    public bool CanChooseDoctorGeometry=>!IsBusy&&DoctorSelectedRow is not null;
    public string DoctorRuleDetails=>string.Join(Environment.NewLine,_doctorRules.Select(r=>$"{r.HelperName}: {r.Components?.ToString()??"unresolved components"}; {r.AnimationLod?.ToString()??"unresolved LOD"}; {r.Origin}; {r.Axes}; directions in {(r.DirectionsInParentSpace?"parent":"model")} space. {r.Evidence.Description}"))+
        string.Concat(DoctorRows.Where(r=>r.BoundsHalfExtents is not null).Select(r=>$"{Environment.NewLine}{r.HelperName} box size: {r.BoundsHalfExtents!.Value.X*2000:0.###} x {r.BoundsHalfExtents!.Value.Y*2000:0.###} x {r.BoundsHalfExtents!.Value.Z*2000:0.###} mm."));
    public RigDoctorPreview? DoctorPreview=>_doctorPreview;
    public bool CanRunDoctor=>!IsBusy&&HasStudioSession&&!_doctorRules.IsEmpty;
    public bool CanApplyDoctor=>!IsBusy&&DoctorReviewed&&_doctorPreview is {CanApply:true};
    public IAsyncRelayCommand LoadDoctorRulesCommand { get; private set; }=null!;
    public IAsyncRelayCommand RunDoctorCommand { get; private set; }=null!;
    public IRelayCommand ApplyDoctorCommand { get; private set; }=null!;
    public IRelayCommand ClearDoctorCommand { get; private set; }=null!;
    public IRelayCommand CancelDoctorCommand { get; private set; }=null!;
    public event EventHandler? DoctorPreviewChanged;
    public event EventHandler<BodyModelEventArgs>? DoctorApplyRequested;
    internal void SetDoctorRulePicker(Func<string?> picker)=>_doctorRulePicker=picker;
    internal void SetDoctorRules(ImmutableArray<RigDoctorContactRule> rules,string label)
    { RestoreDoctor();_doctorRules=rules;DoctorRuleLabel=label;NotifyDoctor(); }
    private void InitializeDoctor()
    {
        LoadDoctorRulesCommand=new AsyncRelayCommand(LoadDoctorRulesAsync,()=>!IsBusy);
        RunDoctorCommand=new AsyncRelayCommand(RunDoctorAsync,()=>CanRunDoctor);
        ApplyDoctorCommand=new RelayCommand(ApplyDoctor,()=>CanApplyDoctor);
        ClearDoctorCommand=new RelayCommand(RestoreDoctor,()=>_doctorPreview is not null);
        CancelDoctorCommand=new RelayCommand(()=>{LoadDoctorRulesCommand.Cancel();RunDoctorCommand.Cancel();});
    }
    private async Task LoadDoctorRulesAsync(CancellationToken token)
    {
        string? path=_doctorRulePicker?.Invoke();if(path is null)return;
        IsBusy=true;NotifyStateChanged();
        try
        {
            if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Repair rule files must be at most 1 MiB.");
            var rules=JsonSerializer.Deserialize<ImmutableArray<RigDoctorContactRule>>(await File.ReadAllTextAsync(path,token),DoctorJson);
            if(rules.IsDefaultOrEmpty||rules.Length>64)throw new InvalidDataException("Choose a file with one to 64 explicit contact requirements.");
            token.ThrowIfCancellationRequested();SetDoctorRules(rules,Path.GetFileName(path));
            DoctorStatus=$"{rules.Length} contact requirements loaded. These are authoring rules, not a native validation certificate.";
        }
        catch(Exception error) when(DoctorError(error)) {DoctorStatus="Rules were not loaded: "+error.Message;}
        finally {IsBusy=false;NotifyStateChanged();}
    }
    private async Task RunDoctorAsync(CancellationToken token)
    {
        if(_model is not { } model)return;
        RouteStudioAction(RigStudioStage.HelpersAndHooks);model=_model!;
        long generation=++_doctorGeneration;var rules=_doctorRules;
        IsBusy=true;DoctorReviewed=false;NotifyStateChanged();
        try
        {
            var preview=await Task.Run(()=>FbxRigDoctor.Preview(model,rules,token),token);
            if(generation!=_doctorGeneration||!ReferenceEquals(model,_model))return;
            _doctorPreview=preview;DoctorRows.Clear();foreach(var row in preview.Rows)DoctorRows.Add(row);
            DoctorSelectedRow=DoctorRows.FirstOrDefault(r=>r.Status==RigDoctorRowStatus.NeedsReview)??DoctorRows.FirstOrDefault();
            DoctorPreviewEnabled=preview.CanApply;
            DoctorStatus=preview.IsNoOp?"No repair is needed for these selected contact rules. Existing nodes and data were retained; native frame and behavior checks remain separate.":
                preview.CanApply?$"{preview.RepairCount} missing contact helpers proposed. Review the footprint, frame, bounds and component rules, then apply the whole repair once.":
                "Repair is blocked by the rows marked NeedsReview. No partial changes will be applied.";
        }
        catch(Exception error) when(DoctorError(error)) {if(generation==_doctorGeneration)DoctorStatus="Diagnosis did not complete: "+error.Message;}
        finally {IsBusy=false;NotifyStateChanged();DoctorPreviewChanged?.Invoke(this,EventArgs.Empty);}
    }
    private void ApplyDoctor()
    {
        if(_model is not { } model||_doctorPreview is not { } preview)return;
        if(!FbxRigDoctor.TryApply(model,preview,DoctorReviewed,out var result)) {DoctorStatus="The model changed. Diagnose it again before applying.";return;}
        if(ReferenceEquals(model,result))return;
        DoctorApplyRequested?.Invoke(this,new(model,result,$"Applied {preview.RepairCount} reviewed contact repairs. Skinning and source animation payloads retained; compiled/live acceptance remains unverified."));
    }
    private void RestoreDoctor()
    {
        RunDoctorCommand?.Cancel();_doctorGeneration++;_doctorPreview=null;DoctorRows.Clear();DoctorReviewed=false;DoctorPreviewEnabled=false;
        DoctorSelectedRow=null;DoctorComponents.Clear();DoctorComponents.Add(new("","Automatic: require one fitting component"));
        if(_model is { } current)
            foreach(var group in current.Surfaces.Where(s=>s.SourceGeometry is not null).GroupBy(s=>s.SourceGeometry!.Id))
                DoctorComponents.Add(new(group.Key,group.First().MeshName));
        NotifyDoctor();DoctorPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    private void RefreshDoctorMetadata(FbxModelAuthoringImportResult model)
    {if(_doctorPreview is { } preview)_doctorPreview=FbxRigDoctor.RefreshMetadata(preview,model);NotifyDoctor();}
    partial void OnDoctorSelectedRowChanged(RigDoctorRow? value)
    {
        _restoringDoctorSelection=true;
        try
        {
            string? component=_doctorRules.FirstOrDefault(r=>r.RoleId==value?.RoleId)?.ComponentId;
            DoctorComponent=DoctorComponents.FirstOrDefault(c=>c.Id==(component??""));
        }
        finally{_restoringDoctorSelection=false;NotifyDoctor();}
    }
    partial void OnDoctorComponentChanged(ContactComponentChoice? value)
    {
        if(_restoringDoctorSelection||DoctorSelectedRow is not { } row||value is null)return;
        int index=-1;for(int i=0;i<_doctorRules.Length;i++)if(_doctorRules[i].RoleId==row.RoleId)index=i;
        if(index<0)return;string? component=string.IsNullOrEmpty(value.Id)?null:value.Id;
        if(_doctorRules[index].ComponentId==component)return;
        _doctorRules=_doctorRules.SetItem(index,_doctorRules[index] with{ComponentId=component});
        _doctorGeneration++;_doctorPreview=null;DoctorReviewed=false;DoctorPreviewEnabled=false;
        DoctorStatus="Geometry choice changed. Diagnose again to refresh every proposed repair.";NotifyDoctor();DoctorPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    partial void OnDoctorReviewedChanged(bool value)=>NotifyDoctor();
    partial void OnDoctorPreviewEnabledChanged(bool value)
    {
        if(value){DerivedPreviewEnabled=false;HierarchyPreviewEnabled=false;RestPosePreviewEnabled=false;StressPreviewEnabled=false;EyeMotionReviewEnabled=false;}
        DoctorPreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    private static bool DoctorError(Exception error)=>error is IOException or ArgumentException or InvalidOperationException or OperationCanceledException or JsonException or UnauthorizedAccessException;
    private void NotifyDoctor()
    {
        OnPropertyChanged(nameof(CanChooseDoctorGeometry));OnPropertyChanged(nameof(DoctorRuleDetails));
        OnPropertyChanged(nameof(DoctorPreview));OnPropertyChanged(nameof(CanRunDoctor));OnPropertyChanged(nameof(CanApplyDoctor));
        LoadDoctorRulesCommand?.NotifyCanExecuteChanged();RunDoctorCommand?.NotifyCanExecuteChanged();ApplyDoctorCommand?.NotifyCanExecuteChanged();ClearDoctorCommand?.NotifyCanExecuteChanged();
    }
}
