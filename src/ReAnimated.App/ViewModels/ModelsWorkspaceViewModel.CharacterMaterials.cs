using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed class CharacterMaterialChoice(Guid materialId, string label)
{
    public Guid MaterialId { get; } = materialId;
    public string Label { get; } = label;
}

public sealed partial class ModelsWorkspaceViewModel
{
    private FbxModelAuthoringImportResult? _characterMaterialChoicesModel;
    private CharacterMaterialChoice? _selectedRetainedCharacterMaterial;
    private CharacterMaterialChoice? _selectedAccessoryCharacterMaterial;
    private bool _characterMaterialReviewed;
    private RelayCommand? _applyCharacterMaterialCommand;

    public ObservableCollection<CharacterMaterialChoice> RetainedCharacterMaterialChoices { get; } = [];
    public ObservableCollection<CharacterMaterialChoice> AccessoryCharacterMaterialChoices { get; } = [];

    public CharacterMaterialChoice? SelectedRetainedCharacterMaterial
    {
        get => _selectedRetainedCharacterMaterial;
        set
        {
            if (!SetProperty(ref _selectedRetainedCharacterMaterial, value)) return;
            CharacterMaterialReviewed = false;
            _applyCharacterMaterialCommand?.NotifyCanExecuteChanged();
        }
    }

    public CharacterMaterialChoice? SelectedAccessoryCharacterMaterial
    {
        get => _selectedAccessoryCharacterMaterial;
        set
        {
            if (!SetProperty(ref _selectedAccessoryCharacterMaterial, value)) return;
            CharacterMaterialReviewed = false;
            _applyCharacterMaterialCommand?.NotifyCanExecuteChanged();
        }
    }

    public bool CharacterMaterialReviewed
    {
        get => _characterMaterialReviewed;
        set
        {
            if (SetProperty(ref _characterMaterialReviewed, value))
                _applyCharacterMaterialCommand?.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand ApplyCharacterMaterialCommand =>
        _applyCharacterMaterialCommand ??= new(ApplyCharacterMaterial, CanApplyCharacterMaterial);

    internal void RefreshCharacterMaterialChoices()
    {
        Guid? retainedId = SelectedRetainedCharacterMaterial?.MaterialId;
        Guid? accessoryId = SelectedAccessoryCharacterMaterial?.MaterialId;
        CharacterMaterialReviewed = false;
        _characterMaterialChoicesModel = _model;
        RetainedCharacterMaterialChoices.Clear();
        AccessoryCharacterMaterialChoices.Clear();
        if (_model?.Package.Document is { Source.Kind: CustomModelSourceKind.StockCharacter, CharacterResources: { } inventory } document &&
            inventory.OriginalMaterialSlotCount > 0 && inventory.OriginalMaterialSlotCount <= document.Materials.Length)
        {
            for (int index = 0; index < document.Materials.Length; index++)
            {
                CustomModelMaterial material = document.Materials[index];
                if (index < inventory.OriginalMaterialSlotCount)
                {
                    if (material.ExistingDl1MaterialReference is { Length: > 0 } name &&
                        inventory.Resources.Count(resource => !resource.IsOriginalArchive &&
                            resource.EntryPath is not null && resource.LogicalName == name &&
                            resource.Status is CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded &&
                            resource.Material is { } receipt && receipt.MaterialName == name &&
                            inventory.Resources.Any(provider => provider.Id == receipt.ProviderResourceId &&
                                !provider.IsOriginalArchive && provider.EntryPath is not null &&
                                provider.Status is CharacterDependencyStatus.Preserved or CharacterDependencyStatus.Decoded)) == 1)
                        RetainedCharacterMaterialChoices.Add(new(material.Id, name));
                }
                else if (_model.Surfaces.Any(surface => surface.MaterialId == material.Id &&
                    surface.Id.StartsWith("attachment:", StringComparison.Ordinal)))
                    AccessoryCharacterMaterialChoices.Add(new(material.Id, material.Name));
            }
        }
        SelectedRetainedCharacterMaterial = RetainedCharacterMaterialChoices.FirstOrDefault(choice => choice.MaterialId == retainedId)
            ?? RetainedCharacterMaterialChoices.FirstOrDefault();
        SelectedAccessoryCharacterMaterial = AccessoryCharacterMaterialChoices.FirstOrDefault(choice => choice.MaterialId == accessoryId)
            ?? AccessoryCharacterMaterialChoices.FirstOrDefault();
        _applyCharacterMaterialCommand?.NotifyCanExecuteChanged();
    }

    private bool CanApplyCharacterMaterial() => !IsBusy && CharacterMaterialReviewed && _model is not null &&
        CharacterMaterialScopeIsCurrent() &&
        SelectedRetainedCharacterMaterial is { } retained && SelectedAccessoryCharacterMaterial is { } accessory &&
        RetainedCharacterMaterialChoices.Any(choice => ReferenceEquals(choice, retained)) &&
        AccessoryCharacterMaterialChoices.Any(choice => ReferenceEquals(choice, accessory));

    private bool CharacterMaterialScopeIsCurrent() => _model is { } current &&
        _characterMaterialChoicesModel is { } captured &&
        current.Package.Document.ModelId == captured.Package.Document.ModelId &&
        current.Package.Document.Materials == captured.Package.Document.Materials &&
        current.Surfaces == captured.Surfaces &&
        ReferenceEquals(current.Package.Document.CharacterResources, captured.Package.Document.CharacterResources) &&
        current.Package.Document.Source == captured.Package.Document.Source &&
        current.Package.DecodedCharacterPayload == captured.Package.DecodedCharacterPayload &&
        current.Package.SourceFbx == captured.Package.SourceFbx &&
        ReferenceEquals(current.Package.CompanionPayloads, captured.Package.CompanionPayloads);

    private void ApplyCharacterMaterial()
    {
        if (!CanApplyCharacterMaterial())
        {
            CharacterMaterialReviewed = false;
            BuildStatus = "Select and review both materials.";
            return;
        }
        try
        {
            FbxModelAuthoringImportResult revised = CharacterAccessoryMaterialAuthoring.ReuseRetainedSlot(_model!,
                SelectedAccessoryCharacterMaterial!.MaterialId, SelectedRetainedCharacterMaterial!.MaterialId, reviewed: true);
            ApplyCharacterGeometry(revised, "Material assigned.");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or FormatException)
        {
            CharacterMaterialReviewed = false;
            BuildStatus = "Material assignment refused: " + exception.Message;
        }
    }
}
