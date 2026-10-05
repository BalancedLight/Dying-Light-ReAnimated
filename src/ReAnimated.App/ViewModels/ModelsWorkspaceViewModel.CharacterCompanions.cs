using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record CharacterCompanionArgumentRow(int Index,string Label);
public sealed record CharacterCompanionGroupRow(string Name,int CallIndex,string Detail,ImmutableArray<int> RelatedCalls);
public sealed record CharacterCompanionCallRow(int Index, string Name, string Arguments);
public sealed partial class ModelsWorkspaceViewModel
{
    private CharacterCompanionFamily _companionFamily;
    private CharacterResourceRecord? _selectedCompanion;
    private CharacterCompanionCallRow? _selectedCompanionCall;
    private CharacterCompanionGroupRow? _selectedCompanionGroup;
    private CharacterCompanionReadResult? _companionRead;
    private string? _companionReadResourceId;
    private CharacterCompanionFamily _companionReadFamily;
    private string _companionDetails = string.Empty;
    private string _companionNewName = string.Empty;
    private string _companionOldName = string.Empty;
    private NativeCharacterReferenceKind _companionNameKind;
    private int _companionArgumentIndex;
    private CharacterCompanionArgumentRow? _selectedCompanionArgument;
    private double _companionNumericValue;
    private bool _companionEditReviewed;
    private RelayCommand? _applyCompanionNumericCommand, _applyCompanionNameCommand, _renameCompanionResourceCommand;
    public IReadOnlyList<CharacterCompanionFamily> CompanionFamilies { get; } = Enum.GetValues<CharacterCompanionFamily>();
    private RelayCommand? _showBodyElementsCommand, _showRagdollCommand, _showDamagePatchesCommand;
    public RelayCommand ShowBodyElementsCommand => _showBodyElementsCommand ??= new(() => CompanionFamily=CharacterCompanionFamily.BodyElements);
    public RelayCommand ShowRagdollCommand => _showRagdollCommand ??= new(() => CompanionFamily=CharacterCompanionFamily.Ragdoll);
    public RelayCommand ShowDamagePatchesCommand => _showDamagePatchesCommand ??= new(() => CompanionFamily=CharacterCompanionFamily.DamagePatches);
    public IReadOnlyList<NativeCharacterReferenceKind> CompanionNameKinds { get; } = Enum.GetValues<NativeCharacterReferenceKind>();
    public ObservableCollection<CharacterResourceRecord> CompanionCandidates { get; } = [];
    public ObservableCollection<CharacterCompanionGroupRow> CompanionGroups {get;}=[];
    public ObservableCollection<string> CompanionReplacementNames {get;}=[];
    public CharacterCompanionGroupRow? SelectedCompanionGroup {get=>_selectedCompanionGroup;set {if(SetProperty(ref _selectedCompanionGroup,value)) SelectCompanionGroup();}}
    public string CompanionGroupDetail=>SelectedCompanionGroup?.Detail??string.Empty;
    public ObservableCollection<CharacterCompanionArgumentRow> CompanionArguments {get;}=[];
    public CharacterCompanionArgumentRow? SelectedCompanionArgument {get=>_selectedCompanionArgument;set {if(SetProperty(ref _selectedCompanionArgument,value)) {CompanionEditReviewed=false;if(value is not null){CompanionArgumentIndex=value.Index;LoadCompanionReference();}}}}
    public ObservableCollection<CharacterCompanionCallRow> CompanionCalls { get; } = [];
    public bool IsRagdollCompanionSelected => CompanionFamily == CharacterCompanionFamily.Ragdoll;
    public bool IsBodyElementsCompanionSelected => CompanionFamily == CharacterCompanionFamily.BodyElements;
    public CharacterCompanionFamily CompanionFamily { get => _companionFamily; set { if (SetProperty(ref _companionFamily,value)) {OnPropertyChanged(nameof(IsRagdollCompanionSelected));OnPropertyChanged(nameof(IsBodyElementsCompanionSelected));RefreshCompanionCandidates();} } }
    public CharacterResourceRecord? SelectedCompanion { get => _selectedCompanion; set { if (SetProperty(ref _selectedCompanion,value)) ReadCompanion(); } }
    public CharacterCompanionCallRow? SelectedCompanionCall { get => _selectedCompanionCall; set {if(SetProperty(ref _selectedCompanionCall,value)) {CompanionEditReviewed=false;LoadCompanionArguments();}} }
    public string CompanionDetails { get => _companionDetails; private set => SetProperty(ref _companionDetails,value); }
    public string CompanionNewName { get => _companionNewName; set { if(SetProperty(ref _companionNewName,value)) CompanionEditReviewed=false; } }
    public string CompanionOldName { get => _companionOldName; set { if(SetProperty(ref _companionOldName,value)) CompanionEditReviewed=false; } }
    public NativeCharacterReferenceKind CompanionNameKind { get => _companionNameKind; set {if(SetProperty(ref _companionNameKind,value)) {CompanionEditReviewed=false;RefreshReplacementNames();}} }
    public int CompanionArgumentIndex { get => _companionArgumentIndex; set { if(SetProperty(ref _companionArgumentIndex,value)) CompanionEditReviewed=false; } }
    public double CompanionNumericValue { get => _companionNumericValue; set { if(SetProperty(ref _companionNumericValue,value)) CompanionEditReviewed=false; } }
    public bool CompanionEditReviewed { get => _companionEditReviewed; set => SetProperty(ref _companionEditReviewed,value); }
    public RelayCommand ApplyCompanionNumericCommand => _applyCompanionNumericCommand ??= new(ApplyCompanionNumeric);
    public RelayCommand ApplyCompanionNameCommand => _applyCompanionNameCommand ??= new(ApplyCompanionName);
    public RelayCommand RenameCompanionResourceCommand => _renameCompanionResourceCommand ??= new(RenameCompanionResource);

    private void RefreshCompanionCandidates()
    {
        string? selected = SelectedCompanion?.Id;
        CompanionCandidates.Clear();
        if (_model?.Package.Document.CharacterResources is not null)
            foreach (var r in CharacterCompanionAuthoring.SelectCandidates(_model.Package, CompanionFamily)) CompanionCandidates.Add(r);
        CharacterResourceRecord? next = CompanionCandidates.FirstOrDefault(r => r.Id == selected) ?? CompanionCandidates.FirstOrDefault();
        bool changed = !EqualityComparer<CharacterResourceRecord?>.Default.Equals(SelectedCompanion, next);
        SelectedCompanion = next;
        if (!changed) ReadCompanion();
    }
    private CharacterCompanionReadResult ReadSelectedCompanion()
    {
        var model = _model ?? throw new InvalidOperationException("Load a character first.");
        var resource = SelectedCompanion ?? throw new InvalidOperationException("Choose a companion resource.");
        var names = model.Package.Document.CreateEffectiveBones().Select(b => b.Name).ToArray();
        var resources = model.Package.Document.CharacterResources!.Resources.Where(r => !r.IsOriginalArchive).SelectMany(r => new[] { r.LogicalName, System.IO.Path.GetFileName(r.LogicalName) });
        return CharacterCompanionAuthoring.Read(model.Package, resource.Id, CompanionFamily, names, resources);
    }
    private void ReadCompanion()
    {
        var previousGroup = SelectedCompanion?.Id == _companionReadResourceId && CompanionFamily == _companionReadFamily ? SelectedCompanionGroup : null;
        ClearCompanionSelectionState();
        if (_model is null || SelectedCompanion is null)
        { CompanionDetails = "Attach a source resource for this system."; return; }
        try
        {
            var read = ReadSelectedCompanion();
            _companionRead=read;
            _companionReadResourceId=read.Resource.Id; _companionReadFamily=CompanionFamily;
            BuildCompanionGroups(read);
            for (int i=0;i<read.Syntax.Calls.Length;i++)
                CompanionCalls.Add(new(i,read.Syntax.Calls[i].Name,string.Join(", ",read.Syntax.Calls[i].Arguments)));
            if (read.Ragdoll is { } ragdoll) CompanionDetails = $"{ragdoll.Bones.Length} physical bones, {ragdoll.Joints.Length} joints, {ragdoll.CollisionSettings.Length} collision settings.";
            else if (read.BodyElements is { } body) CompanionDetails = $"{body.Elements.Length} body regions, {body.Relics.Length} detached parts, {body.MeshDisables.Length} visibility relationships.";
            else if (read.DamagePatches is { } damage) CompanionDetails = $"{damage.Patches.Length} damage patches. Create helpers before assigning them.";
            CompanionDetails += string.Concat(read.Diagnostics.Select(d => "\n" + d.Message));
            SelectedCompanionGroup=CompanionGroups.FirstOrDefault(group=>previousGroup is not null && group.CallIndex==previousGroup.CallIndex && group.Name==previousGroup.Name) ?? CompanionGroups.FirstOrDefault();
            CompanionEditReviewed = false;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { CompanionDetails = e.Message; }
    }
    private void ClearCompanionSelectionState()
    {
        _companionRead = null;
        _companionReadResourceId = null;
        CompanionCalls.Clear(); CompanionGroups.Clear(); CompanionRelationships.Clear();
        SelectedCompanionRelationship = null;
        SelectedCompanionGroup = null;
        SelectedCompanionCall = null;
        SelectedCompanionArgument = null;
        CompanionArguments.Clear(); CompanionReplacementNames.Clear(); RagdollSourceShapeTokens.Clear();
        RagdollProposedShape = null;
        CompanionOldName = string.Empty; CompanionNewName = string.Empty;
        CompanionArgumentIndex = 0; CompanionNumericValue = 0;
        CompanionEditReviewed = false;
        RefreshBodyAssemblyChoices();
    }
    private void LoadCompanionArguments()
    {
        CompanionArguments.Clear();
        if(SelectedCompanionCall is null || _model is null || SelectedCompanion is null) return;
        try
        {
            var call=ReadSelectedCompanion().Syntax.Calls[SelectedCompanionCall.Index];
            for(int i=0;i<call.Arguments.Length;i++)
            {
                string label=(call.Name,i) switch
                {
                    ("UseBone" or "UseBoneScale",0)=>"Physical bone",("UseBone" or "UseBoneScale",1)=>"Shape type",
                    ("UseBone" or "UseBoneScale",2)=>"Relative mass",("UseBoneScale",3)=>"Declared scale multiplier",
                    ("BodyElement",0)=>"Body region",("BodyElement",5)=>"Cut helper frame",
                    ("BodyElement",_)=>"Numeric field",
                    ("Damage",0)=>"Damage patch",("Damage",1)=>"Helper frame",
                    ("AddRelics" or "AddRelicsWithDestroyedChild",0)=>"Detached part",("AddRelics" or "AddRelicsWithDestroyedChild",1)=>"Physics mode",
                    ("AddRelics" or "AddRelicsWithDestroyedChild",2)=>"Physics resource",("AddRelics" or "AddRelicsWithDestroyedChild",3)=>"Effect resource",
                    ("AddMesh2Disable" or "AddMesh2DisableFromRelic",0)=>"Mesh element",("Xform",_)=>"Original transform component "+(i+1),
                    _=>"Original setting "+(i+1),
                };
                CompanionArguments.Add(new(i,label+": "+call.Arguments[i]));
            }
            SelectedCompanionArgument=CompanionArguments.FirstOrDefault();
        }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) {CompanionDetails=e.Message;}
    }

    private void BuildCompanionGroups(CharacterCompanionReadResult read)
    {
        CompanionGroups.Add(new("All settings",-1,"Select a bone, region or patch to review its references.",[]));
        if(read.BodyElements is { } body)
            foreach(var element in body.Elements)
            {
                var relics=body.Relics.Where(r=>r.BodyElementCallIndex==element.CallIndex).ToArray();
                var disables=body.MeshDisables.Where(r=>r.BodyElementCallIndex==element.CallIndex).ToArray();
                string detail=$"Helper: {element.HelperName}\nDetached parts: "+string.Join(", ",relics.Select(r=>r.Name))+
                    "\nDisable on body: "+string.Join(", ",disables.Where(d=>!d.FromRelic).Select(d=>d.EntityName))+
                    "\nDisable on detached part: "+string.Join(", ",disables.Where(d=>d.FromRelic).Select(d=>d.EntityName))+
                    "\nPhysics/effects: "+string.Join(", ",relics.Select(r=>r.PhysicsResource+" / "+r.EffectResource))+
                    "";
                CompanionGroups.Add(new(element.ElementToken,element.CallIndex,detail,[element.CallIndex,..relics.Select(r=>r.CallIndex),..disables.Select(d=>d.CallIndex)]));
            }
        if(read.DamagePatches is { } damage)
            foreach(var patch in damage.Patches)
                CompanionGroups.Add(new(patch.PatchName,patch.CallIndex,$"Helper: {patch.HelperName}",[patch.CallIndex]));
        if(read.Ragdoll is { } ragdoll)
            foreach(var bone in Dl1RagdollBoneReviewProjection.Build(ragdoll))
                CompanionGroups.Add(new(bone.BoneName,bone.CallIndex,
                    $"Shape token: {bone.ShapeToken}; relative mass: {bone.RelativeMass:R}; declared scale multiplier: {bone.ScaleMultiplier?.ToString("R",CultureInfo.InvariantCulture)??"original default"}. Related joints: {bone.JointCount}; collision settings: {bone.CollisionCount}; synchronization settings: {bone.SynchronizationCount}.",bone.RelatedCallIndexes));
    }
    private void SelectCompanionGroup()
    {
        OnPropertyChanged(nameof(CompanionGroupDetail));
        if(_companionRead is not { } read || SelectedCompanionGroup is not { } group) return;
        CompanionCalls.Clear();
        for(int i=0;i<read.Syntax.Calls.Length;i++)
        {
            int parent=i;bool included=group.CallIndex<0;
            while(!included && parent>=0){included=group.RelatedCalls.Contains(parent);parent=read.Syntax.Calls[parent].ParentCallIndex;}
            if(included) CompanionCalls.Add(new(i,read.Syntax.Calls[i].Name,string.Join(", ",read.Syntax.Calls[i].Arguments)));
        }
        SelectedCompanionCall=CompanionCalls.FirstOrDefault();CompanionEditReviewed=false;
        RefreshCompanionRelationships();
        RefreshBodyAssemblyChoices();
        RefreshRagdollShapeChoices();
        if (read.Ragdoll is not null && group.CallIndex >= 0)
        {
            var physical = read.Ragdoll.Bones.SingleOrDefault(bone=>bone.CallIndex==group.CallIndex);
            var matches = physical is null ? [] : Bones.Where(bone=>bone.Name==physical.BoneName).ToArray();
            if (matches.Length == 1) {SelectedBone=matches[0];ShowCharacterBounds=true;LoadBoneBounds();}
            else {SelectedBone=null;ShowCharacterBounds=false;BuildStatus="The physical bone has no exact unique hierarchy match. Preserve the source and review its mapping.";}
        }
    }
    private void RefreshReplacementNames()
    {
        CompanionReplacementNames.Clear();
        if(_model is null)return;
        if (SelectedCompanionRelationship is { } selected && selected.Kind == CompanionNameKind &&
            selected.Kind is not (NativeCharacterReferenceKind.Helper or NativeCharacterReferenceKind.MeshEntity or NativeCharacterReferenceKind.Bone or NativeCharacterReferenceKind.Patch))
        {
            foreach (string logicalName in _model.Package.Document.CharacterResources!.Resources.Where(resource =>
                !resource.IsOriginalArchive && resource.EntryPath is not null && ReferenceResourceKindMatches(selected.Kind, resource.LogicalName))
                .Select(resource => resource.LogicalName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                CompanionReplacementNames.Add(logicalName);
            return;
        }
        IEnumerable<string> names=CompanionNameKind switch
        {
            NativeCharacterReferenceKind.Bone or NativeCharacterReferenceKind.Helper=>_model.Package.Document.CreateEffectiveBones().Select(b=>b.Name),
            NativeCharacterReferenceKind.MeshEntity=>CharacterModelEntityInventory.UniqueNames(_model),
            NativeCharacterReferenceKind.Patch=>_companionRead?.DamagePatches?.Patches.Select(p=>p.PatchName)??[],
            _=>_model.Package.Document.CharacterResources?.Resources.Where(r=>!r.IsOriginalArchive).SelectMany(r=>new[]{r.LogicalName,System.IO.Path.GetFileName(r.LogicalName)})??[],
        };
        foreach(var name in names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))CompanionReplacementNames.Add(name);
    }
    private void LoadCompanionReference()
    {
        if(_companionRead is not { } read || SelectedCompanionCall is null || SelectedCompanionArgument is null)return;
        var reference=read.BodyElements?.References.Concat(read.DamagePatches?.References??[]).Concat(read.Ragdoll?.References??[])
            ?? (read.DamagePatches?.References.Concat(read.Ragdoll?.References??[])??read.Ragdoll?.References??[]);
        var selected=reference.FirstOrDefault(r=>r.CallIndex==SelectedCompanionCall.Index && r.ArgumentIndex==CompanionArgumentIndex);
        if(selected is not null){CompanionNameKind=selected.Kind;CompanionOldName=selected.Name;RefreshReplacementNames();}
        string literal=read.Syntax.Calls[SelectedCompanionCall.Index].Arguments[CompanionArgumentIndex];
        if(double.TryParse(literal,NumberStyles.Float,CultureInfo.InvariantCulture,out double number))CompanionNumericValue=number;
    }

    private void ApplyCompanionNumeric()
    {
        if (_model is null || SelectedCompanion is null || SelectedCompanionCall is null) return;
        try
        {
            RequireCompanionReview();
            var read = ReadSelectedCompanion();
            var call = read.Syntax.Calls[SelectedCompanionCall.Index];
            if ((uint)CompanionArgumentIndex >= (uint)call.Arguments.Length) throw new ArgumentException("Select an argument present in the chosen setting.");
            ApplyCompanionResult(CharacterCompanionAuthoring.ApplyNumericEdit(_model.Package,new(SelectedCompanion.Id,CompanionFamily,
                SelectedCompanion.ContentSha256!,SelectedCompanionCall.Index,CompanionArgumentIndex,call.Arguments[CompanionArgumentIndex],CompanionNumericValue)));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Companion edit rejected: " + e.Message; }
    }
    private void ApplyCompanionName()
    {
        if (_model is null || SelectedCompanion is null) return;
        try
        {
            RequireCompanionReview();
            var read = ReadSelectedCompanion();
            ImmutableArray<string> targets = CompanionNameKind switch
            {
                NativeCharacterReferenceKind.Bone or NativeCharacterReferenceKind.Helper => _model.Package.Document.CreateEffectiveBones().Select(b=>b.Name).ToImmutableArray(),
                NativeCharacterReferenceKind.MeshEntity => _model.Surfaces.Select(s=>s.MeshName).Distinct(StringComparer.Ordinal).ToImmutableArray(),
                NativeCharacterReferenceKind.Patch => read.DamagePatches?.Patches.Select(p=>p.PatchName).ToImmutableArray() ?? [],
                _ => _model.Package.Document.CharacterResources!.Resources.Where(r=>!r.IsOriginalArchive).SelectMany(r=>new[] {r.LogicalName,System.IO.Path.GetFileName(r.LogicalName)}).ToImmutableArray(),
            };
            ApplyCompanionResult(CharacterCompanionAuthoring.ApplyNameEdit(_model.Package,new(SelectedCompanion.Id,CompanionFamily,
                SelectedCompanion.ContentSha256!,CompanionNameKind,CompanionOldName,CompanionNewName,targets)));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Companion reference edit rejected: " + e.Message; }
    }
    private void RenameCompanionResource()
    {
        if (_model is null || SelectedCompanion is null) return;
        try
        {
            RequireCompanionReview();
            ApplyCompanionResult(CharacterCompanionAuthoring.ApplyResourceRename(_model.Package,new(SelectedCompanion.Id,SelectedCompanion.ContentSha256!,CompanionNewName)));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Resource rename rejected: " + e.Message; }
    }
    private void RequireCompanionReview()
    {
        if (!CompanionEditReviewed) throw new InvalidOperationException("Review the resource, related geometry and proposed change before applying.");
    }
    private void ApplyCompanionResult(CharacterCompanionAuthoringResult result)
    {
        if (_model is null) return;
        if (!result.Applied) { BuildStatus = string.Join("; ",result.Diagnostics.Select(d=>d.Message)); return; }
        var before = CaptureAuthoringSnapshot();
        CommitModel(_model with { Package = result.Package },_sourcePath,_packagePath,preserveAuthoringHistory:true);
        RecordAuthoringUndo(before); BuildStatus = "Character system updated.";
        RefreshCompanionCandidates();
    }
}
