using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private string? _selectedBodyCapSurface;
    private CharacterResourceRecord? _selectedDetachedAsset, _selectedBodyPhysicsAsset, _selectedBodyEffectAsset;
    private bool _bodyArtistGeometryReviewed, _bodyRelationshipsReviewed;
    private RelayCommand? _recordBodyAssemblyCommand, _previewBodyRegionCommand, _restoreBodyPreviewCommand;
    public ObservableCollection<string> BodyCapSurfaceChoices { get; } = [];
    public ObservableCollection<CharacterResourceRecord> BodyDetachedAssets { get; } = [];
    public ObservableCollection<CharacterResourceRecord> BodyPhysicsAssets { get; } = [];
    public ObservableCollection<CharacterResourceRecord> BodyEffectAssets { get; } = [];
    public ObservableCollection<CharacterBodyRegionAssemblyReview> BodyAssemblyReviews { get; } = [];
    public string? SelectedBodyCapSurface { get => _selectedBodyCapSurface; set { if(SetProperty(ref _selectedBodyCapSurface,value)) ResetBodyAssemblyReview(); } }
    public CharacterResourceRecord? SelectedDetachedAsset { get => _selectedDetachedAsset; set { if(SetProperty(ref _selectedDetachedAsset,value)) ResetBodyAssemblyReview(); } }
    public CharacterResourceRecord? SelectedBodyPhysicsAsset { get => _selectedBodyPhysicsAsset; set { if(SetProperty(ref _selectedBodyPhysicsAsset,value)) ResetBodyAssemblyReview(); } }
    public CharacterResourceRecord? SelectedBodyEffectAsset { get => _selectedBodyEffectAsset; set { if(SetProperty(ref _selectedBodyEffectAsset,value)) ResetBodyAssemblyReview(); } }
    public bool BodyArtistGeometryReviewed { get => _bodyArtistGeometryReviewed; set => SetProperty(ref _bodyArtistGeometryReviewed,value); }
    public bool BodyRelationshipsReviewed { get => _bodyRelationshipsReviewed; set => SetProperty(ref _bodyRelationshipsReviewed,value); }
    public RelayCommand RecordBodyAssemblyCommand => _recordBodyAssemblyCommand ??= new(RecordBodyAssembly);
    public RelayCommand PreviewBodyRegionCommand => _previewBodyRegionCommand ??= new(PreviewBodyRegion);
    public RelayCommand RestoreBodyPreviewCommand => _restoreBodyPreviewCommand ??= new(() => { _previewSession=null; RefreshPreview(); BuildStatus="Target model preview restored."; });

    public ObservableCollection<string> SelectedBodyCapSurfaceIds { get; } = [];
    public ObservableCollection<CharacterResourceRecord> SelectedBodyDetachedAssets { get; } = [];
    public ObservableCollection<CharacterResourceRecord> SelectedBodyPhysicsAssets { get; } = [];
    public ObservableCollection<CharacterResourceRecord> SelectedBodyEffectAssets { get; } = [];
    private RelayCommand? _addBodyCapCommand, _addBodyDetachedCommand, _addBodyPhysicsCommand, _addBodyEffectCommand, _clearBodySelectionsCommand;
    public RelayCommand AddBodyCapCommand => _addBodyCapCommand ??= new(() => { if(SelectedBodyCapSurface is { } id && !SelectedBodyCapSurfaceIds.Contains(id)){SelectedBodyCapSurfaceIds.Add(id);ResetBodyAssemblyReview();} });
    public RelayCommand AddBodyDetachedCommand => _addBodyDetachedCommand ??= new(() => AddBodyAsset(SelectedBodyDetachedAssets, SelectedDetachedAsset));
    public RelayCommand AddBodyPhysicsCommand => _addBodyPhysicsCommand ??= new(() => AddBodyAsset(SelectedBodyPhysicsAssets, SelectedBodyPhysicsAsset));
    public RelayCommand AddBodyEffectCommand => _addBodyEffectCommand ??= new(() => AddBodyAsset(SelectedBodyEffectAssets, SelectedBodyEffectAsset));
    public RelayCommand ClearBodySelectionsCommand => _clearBodySelectionsCommand ??= new(ClearBodyAssemblySelections);
    private void AddBodyAsset(ObservableCollection<CharacterResourceRecord> selected, CharacterResourceRecord? resource)
    { if(resource is not null && !selected.Any(item=>item.Id==resource.Id)){selected.Add(resource);ResetBodyAssemblyReview();} }
    private void ClearBodyAssemblySelections()
    { SelectedBodyCapSurfaceIds.Clear();SelectedBodyDetachedAssets.Clear();SelectedBodyPhysicsAssets.Clear();SelectedBodyEffectAssets.Clear();ResetBodyAssemblyReview(); }
    private void ResetBodyAssemblyReview() { BodyArtistGeometryReviewed=false; BodyRelationshipsReviewed=false; }
    private void RefreshBodyAssemblyChoices()
    {
        RefreshBodyHideEntityChoices();
        BodyDetachedAssets.Clear(); BodyPhysicsAssets.Clear(); BodyEffectAssets.Clear(); BodyAssemblyReviews.Clear();
        ClearBodyAssemblySelections();
        BodyCapSurfaceChoices.Clear();
        if (_model?.Package.Document.CharacterResources is not null)
        {
            var baseline = DecodedCharacterSnapshotCodec.DecodeSource(_model.Package).Surfaces.ToDictionary(surface=>surface.Id, StringComparer.Ordinal);
            foreach(var surface in _model.Surfaces)
                if(!baseline.TryGetValue(surface.Id,out var original) || original.MeshName!=surface.MeshName || !original.Indices.SequenceEqual(surface.Indices) || !original.Vertices.Select(vertex=>vertex.Position).SequenceEqual(surface.Vertices.Select(vertex=>vertex.Position))) BodyCapSurfaceChoices.Add(surface.Id);
        }
        foreach (var resource in _model?.Package.Document.CharacterResources?.Resources ?? [])
        {
            if (resource.IsOriginalArchive || resource.EntryPath is null) continue;
            string extension = System.IO.Path.GetExtension(resource.LogicalName).ToLowerInvariant();
            if ((extension is ".msh" or ".skn" || resource.NativeResource is {ResourceType:272}) && resource.Id != _model?.Package.Document.CharacterResources?.RootResourceId) BodyDetachedAssets.Add(resource);
            if (extension == ".phx") BodyPhysicsAssets.Add(resource);
            if (extension == ".fx") BodyEffectAssets.Add(resource);
        }
        foreach (var review in _model?.Package.Document.CharacterResources?.BodyRegionReviews ?? []) BodyAssemblyReviews.Add(review);
        SelectedBodyCapSurface = null; SelectedDetachedAsset=null; SelectedBodyPhysicsAsset=null; SelectedBodyEffectAsset=null;
        ResetBodyAssemblyReview();
    }
    private void RecordBodyAssembly()
    {
        if (_model is null || SelectedCompanion is null || SelectedCompanionGroup is not { CallIndex: >= 0 } group || _companionRead?.BodyElements is null)
        { BuildStatus="Choose a body-element source and one body region before recording its assembly."; return; }
        try
        {
            var proposal = new ReviewedBodyRegionAssembly(SelectedCompanion.Id, group.CallIndex, SelectedCompanion.ContentSha256!,
                SelectedBodyCapSurfaceIds.ToImmutableArray(), SelectedBodyDetachedAssets.Select(asset=>asset.Id).ToImmutableArray(),
                SelectedBodyPhysicsAssets.Select(asset=>asset.Id).ToImmutableArray(), SelectedBodyEffectAssets.Select(asset=>asset.Id).ToImmutableArray(),
                BodyArtistGeometryReviewed, BodyRelationshipsReviewed);
            var revised = CharacterBodyRegionAuthoring.Apply(_model, proposal);
            ApplyCharacterGeometry(revised,"Body-region assembly saved.");
        }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException)
        { BuildStatus="Body-region assembly review rejected: "+error.Message; }
    }
    private void PreviewBodyRegion()
    {
        if (_model is null || _companionRead?.BodyElements is not { } body || SelectedCompanionGroup is not { CallIndex: >=0 } group)
        { BuildStatus="Choose a body region before previewing its authored visibility."; return; }
        var hidden = body.MeshDisables.Where(disable=>disable.BodyElementCallIndex==group.CallIndex && !disable.FromRelic).Select(disable=>disable.EntityName).ToHashSet(StringComparer.Ordinal);
        if (SelectedBodyCapSurfaceIds.Any(cap=>_model.Surfaces.Any(surface=>surface.Id==cap && hidden.Contains(surface.MeshName))))
        { BuildStatus="The selected cap is also hidden by this region's original-body disable list. Correct the relationship before previewing."; return; }
        var selected = _model.Surfaces.Where(surface=>!hidden.Contains(surface.MeshName)).ToImmutableArray();
        if(selected.IsEmpty){BuildStatus="The declared body disable list hides every current surface. Supply and select the artist-made cap geometry.";return;}
        StopFacePicking(); _previewSession=null;
        var preview=CustomModelPreviewAdapter.CreateSession(_model with {Surfaces=selected});
        Viewport.SceneSource.SetScene(preview.Meshes.Select(mesh=>mesh with {IsSkinned=false,InverseBindMatrices=ReadOnlyMemory<Matrix4x4>.Empty,SkinBoneIndices=ReadOnlyMemory<int>.Empty}).ToArray(),null,[],generation:Interlocked.Increment(ref _previewGeneration));
        Viewport.SceneSource.SetMorphWeights([]); Viewport.SceneSource.SetMeshVisibility(true);
        _cameraCoordinator.SetTargetPreviewCameraOverride(null); FrameModel();
        BuildStatus="Body visibility preview active.";
    }
}
