using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record CharacterCompanionRelationshipRow(int CallIndex, int ArgumentIndex,
    NativeCharacterReferenceKind Kind, string Role, string Name, string Resolution);

public sealed partial class ModelsWorkspaceViewModel
{
    private CharacterCompanionRelationshipRow? _selectedCompanionRelationship;
    private RelayCommand? _applySelectedCompanionReferenceCommand;
    public ObservableCollection<CharacterCompanionRelationshipRow> CompanionRelationships { get; } = [];
    public CharacterCompanionRelationshipRow? SelectedCompanionRelationship
    {
        get => _selectedCompanionRelationship;
        set
        {
            if (!SetProperty(ref _selectedCompanionRelationship, value)) return;
            CompanionEditReviewed = false;
            if (value is null) return;
            SelectedCompanionCall = CompanionCalls.FirstOrDefault(call => call.Index == value.CallIndex);
            SelectedCompanionArgument = CompanionArguments.FirstOrDefault(argument => argument.Index == value.ArgumentIndex);
            CompanionNameKind = value.Kind;
            CompanionOldName = value.Name;
            CompanionNewName = value.Name;
            RefreshReplacementNames();
        }
    }
    public RelayCommand ApplySelectedCompanionReferenceCommand => _applySelectedCompanionReferenceCommand ??= new(ApplySelectedCompanionReference);

    private void RefreshCompanionRelationships()
    {
        CompanionRelationships.Clear();
        SelectedCompanionRelationship = null;
        if (_companionRead is not { } read || _model is null) return;
        var visibleCalls = CompanionCalls.Select(call => call.Index).ToHashSet();
        var references = read.BodyElements?.References ?? read.DamagePatches?.References ?? read.Ragdoll?.References ?? [];
        foreach (var reference in references.Where(reference => visibleCalls.Contains(reference.CallIndex)))
        {
            var call = read.Syntax.Calls[reference.CallIndex];
            string role = call.Name switch
            {
                "BodyElement" => "Body-region cut helper",
                "AddMesh2Disable" => "Hide on original body",
                "AddMesh2DisableFromRelic" => "Hide on detached part",
                "AddRelics" or "AddRelicsWithDestroyedChild" when reference.Kind == NativeCharacterReferenceKind.PhysicsResource => "Detached-part physics",
                "AddRelics" or "AddRelicsWithDestroyedChild" when reference.Kind == NativeCharacterReferenceKind.EffectResource => "Detached-part effect",
                "Damage" when reference.Kind == NativeCharacterReferenceKind.Helper => "Damage-patch helper",
                "DestroyedHeadParts" or "AddMeatPart" => "Detached geometry resource",
                _ => reference.Kind.ToString(),
            };
            string resolution = ResolveCompanionRelationship(reference);
            CompanionRelationships.Add(new(reference.CallIndex, reference.ArgumentIndex, reference.Kind, role, reference.Name, resolution));
        }
        if (read.BodyElements is { } body)
            foreach (var relic in body.Relics.Where(relic => visibleCalls.Contains(relic.CallIndex)))
                CompanionRelationships.Add(new(relic.CallIndex, 0, NativeCharacterReferenceKind.MeshResource,
                    "Detached mesh", relic.Name, "Select and verify the matching model resource."));
        SelectedCompanionRelationship = CompanionRelationships.FirstOrDefault();
    }
    private string ResolveCompanionRelationship(NativeCharacterReference reference)
    {
        if (_model is null) return "No model loaded.";
        if (reference.Kind is NativeCharacterReferenceKind.Helper or NativeCharacterReferenceKind.Bone)
        {
            int count = _model.Package.Document.CreateEffectiveBones().Count(bone => bone.Name == reference.Name);
            return count == 1 ? "Bone found." : count == 0 ? "Missing hierarchy name." : "Ambiguous hierarchy name.";
        }
        if (reference.Kind == NativeCharacterReferenceKind.MeshEntity)
        {
            var entityMatches = CharacterModelEntityInventory.Build(_model).Where(entity =>
                string.Equals(entity.Name, reference.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return entityMatches.Length == 1 ? entityMatches[0].IsBone ? "Bone found." : "Mesh found."
                : entityMatches.Length == 0 ? "Missing mesh entity." : "Ambiguous model element.";
        }
        if (reference.Kind == NativeCharacterReferenceKind.Patch) return "Source declaration; use the global rename control to rename a patch.";
        var resources = _model.Package.Document.CharacterResources?.Resources ?? [];
        var matches = resources.Where(resource => !resource.IsOriginalArchive &&
            (resource.LogicalName == reference.Name || System.IO.Path.GetFileName(resource.LogicalName) == reference.Name)).ToArray();
        if (matches.Length == 0) return "Missing retained resource.";
        if (matches.Length > 1) return "Ambiguous retained resource identity.";
        return matches[0].EntryPath is null ? "Retained dependency has no payload." : "Resource found.";
    }
    private void ApplySelectedCompanionReference()
    {
        if (_model is null || SelectedCompanion is null || SelectedCompanionRelationship is not { } selected) return;
        try
        {
            RequireCompanionReview();
            if (_companionRead?.BodyElements?.Relics.Any(relic => relic.CallIndex == selected.CallIndex && selected.ArgumentIndex == 0) == true)
                throw new InvalidOperationException("Verify the matching resource and naming rule before changing this detached mesh name.");
            ImmutableArray<string> targets = selected.Kind switch
            {
                NativeCharacterReferenceKind.Helper => _model.Package.Document.CreateEffectiveBones().Select(bone => bone.Name).ToImmutableArray(),
                NativeCharacterReferenceKind.MeshEntity => CharacterModelEntityInventory.UniqueNames(_model),
                _ => _model.Package.Document.CharacterResources!.Resources.Where(resource => !resource.IsOriginalArchive && resource.EntryPath is not null &&
                    ReferenceResourceKindMatches(selected.Kind, resource.LogicalName)).Select(resource => resource.LogicalName).ToImmutableArray(),
            };
            ApplyCompanionResult(CharacterCompanionAuthoring.ApplyReferenceEdit(_model.Package,
                new(SelectedCompanion.Id, CompanionFamily, SelectedCompanion.ContentSha256!, selected.CallIndex,
                    selected.ArgumentIndex, selected.Kind, selected.Name, CompanionNewName, targets)));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException)
        { BuildStatus = "Selected relationship edit rejected: " + error.Message; }
    }
    private static bool ReferenceResourceKindMatches(NativeCharacterReferenceKind kind, string name)
    {
        string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return kind switch
        {
            NativeCharacterReferenceKind.PhysicsResource => extension == ".phx",
            NativeCharacterReferenceKind.EffectResource => extension == ".fx",
            NativeCharacterReferenceKind.MeshResource => extension is ".msh" or ".skn",
            NativeCharacterReferenceKind.BodyElementsResource => extension == ".bel",
            NativeCharacterReferenceKind.IncludeResource => extension is ".def" or ".scr" or ".phx" or ".bel",
            _ => false,
        };
    }}
