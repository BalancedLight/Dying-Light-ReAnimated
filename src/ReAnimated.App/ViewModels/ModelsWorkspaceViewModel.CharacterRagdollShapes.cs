using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private string? _ragdollProposedShape;
    private RelayCommand? _applyRagdollShapeCommand;
    public ObservableCollection<string> RagdollSourceShapeTokens { get; } = [];
    public string? RagdollProposedShape { get=>_ragdollProposedShape; set {if(SetProperty(ref _ragdollProposedShape,value)) CompanionEditReviewed=false;} }
    public RelayCommand ApplyRagdollShapeCommand=>_applyRagdollShapeCommand??=new(ApplyRagdollShape);
    private void RefreshRagdollShapeChoices()
    {
        RagdollSourceShapeTokens.Clear();
        if(_companionRead?.Ragdoll is not { } ragdoll){RagdollProposedShape=null;return;}
        foreach(string token in Dl1RagdollShapeInputCalculator.SupportedShapeTokens) RagdollSourceShapeTokens.Add(token);
        RagdollProposedShape=ragdoll.Bones.FirstOrDefault(bone=>bone.CallIndex==SelectedCompanionGroup?.CallIndex)?.ShapeToken;
    }
    private void ApplyRagdollShape()
    {
        if(_model is null || SelectedCompanion is null || _companionRead?.Ragdoll is not { } ragdoll || SelectedCompanionGroup is not {CallIndex:>=0} group)
        {BuildStatus="Select a physical bone from the original ragdoll source before changing its shape token.";return;}
        try
        {
            RequireCompanionReview();
            var bone=ragdoll.Bones.SingleOrDefault(bone=>bone.CallIndex==group.CallIndex)??throw new InvalidOperationException("Select one physical bone declaration.");
            if(RagdollProposedShape is null)throw new InvalidOperationException("Choose a shape.");
            ApplyCompanionResult(CharacterCompanionAuthoring.ApplyRagdollShapeEdit(_model.Package,new(SelectedCompanion.Id,SelectedCompanion.ContentSha256!,bone.CallIndex,bone.ShapeToken,RagdollProposedShape,RagdollSourceShapeTokens.ToImmutableArray())));
        }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException)
        {BuildStatus="Ragdoll shape edit rejected: "+error.Message;}
    }
}
