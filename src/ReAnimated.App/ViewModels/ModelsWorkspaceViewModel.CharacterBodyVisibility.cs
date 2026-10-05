using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private string? _selectedBodyHideEntity;
    private RelayCommand? _addBodyHideEntityCommand;
    public ObservableCollection<string> BodyHideEntityChoices { get; } = [];
    public string? SelectedBodyHideEntity
    {
        get => _selectedBodyHideEntity;
        set { if(SetProperty(ref _selectedBodyHideEntity,value)) CompanionEditReviewed=false; }
    }
    public RelayCommand AddBodyHideEntityCommand => _addBodyHideEntityCommand ??= new(AddBodyHideEntity);

    private void RefreshBodyHideEntityChoices()
    {
        BodyHideEntityChoices.Clear();
        if(_model is not null && IsBodyElementsCompanionSelected)
            foreach(string name in CharacterModelEntityInventory.UniqueNames(_model)) BodyHideEntityChoices.Add(name);
        if(!BodyHideEntityChoices.Contains(SelectedBodyHideEntity ?? string.Empty)) SelectedBodyHideEntity=null;
    }

    private void AddBodyHideEntity()
    {
        if(_model is null || SelectedCompanion is null || _companionRead?.BodyElements is not { } body ||
            SelectedCompanionGroup is not {CallIndex:>=0} group || SelectedBodyHideEntity is not { } name)
        {BuildStatus="Select a body region and model element.";return;}
        try
        {
            RequireCompanionReview();
            _=CharacterModelEntityInventory.RequireUnique(_model,name);
            var element=body.Elements.SingleOrDefault(element=>element.CallIndex==group.CallIndex)
                ?? throw new InvalidOperationException("Select one body region.");
            ApplyCompanionResult(CharacterCompanionAuthoring.ApplyBodyMeshDisableAddition(_model.Package,
                new(SelectedCompanion.Id,SelectedCompanion.ContentSha256!,element.CallIndex,element.ElementToken,
                    element.HelperName,name,CharacterModelEntityInventory.UniqueNames(_model),true)));
        }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException)
        {BuildStatus="Visibility update rejected: "+error.Message;}
    }
}
