using System.IO;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private FbxModelAuthoringImportResult? _morphReference;
    private MorphExpressionProposal? _expressionProposal;
    private string? _selectedCharacterSurface;
    private string? _selectedReferenceSurface;
    private string _expressionName = string.Empty;
    private string _expressionCorrespondences = string.Empty;
    private string _expressionLockedVertices = string.Empty;
    private string _expressionLandmarks=string.Empty,_expressionTargetRegion=string.Empty,_expressionRegionName=string.Empty;
    private bool _expressionCorrespondenceReviewed;
    private bool _expressionReviewed;
    private MorphTransferConflict _expressionConflict = MorphTransferConflict.Reject;
    private AsyncRelayCommand? _loadMorphReferenceCommand, _importManualShapeCommand, _addCharacterAttachmentCommand;
    private RelayCommand? _adoptCharacterReferenceCommand;
    private RelayCommand? _proposeExpressionCommand, _acceptExpressionCommand, _previewExpressionCommand, _previewReferenceExpressionCommand;
    private double _expressionPreviewWeight=1;
    private long _expressionSceneGeneration;
    private string? _expressionSceneName;
    private string _expressionPreviewSubject = "Neutral";
    private RelayCommand? _previewTargetNeutralCommand;
    public string ExpressionPreviewSubject { get => _expressionPreviewSubject; private set => SetProperty(ref _expressionPreviewSubject, value); }
    public RelayCommand PreviewTargetNeutralCommand => _previewTargetNeutralCommand ??= new(() =>
    {
        if (_model is not null) ShowExpressionScene(_model, null, "Neutral");
    });
    public double ExpressionPreviewWeight
    {
        get => _expressionPreviewWeight;
        set
        {
            if (!double.IsFinite(value) || !SetProperty(ref _expressionPreviewWeight, value)) return;
            if (_expressionSceneName is { } name && Viewport.SceneSource.CaptureFrame().Generation == _expressionSceneGeneration)
                Viewport.SceneSource.SetMorphWeights([new(name, (float)value)]);
        }
    }
    public ObservableCollection<string> CharacterSurfaceIds { get; } = [];
    public ObservableCollection<string> MorphReferenceSurfaceIds { get; } = [];
    public ObservableCollection<string> MorphReferenceNames { get; } = [];
    public string? SelectedCharacterSurface { get => _selectedCharacterSurface; set {if(SetProperty(ref _selectedCharacterSurface, value)){InvalidateExpressionProposal();InvalidateFaceSelections();}} }
    public string? SelectedReferenceSurface { get => _selectedReferenceSurface; set {if(SetProperty(ref _selectedReferenceSurface, value)){InvalidateExpressionProposal();InvalidateFaceSelections();}} }
    public string ExpressionName { get => _expressionName; set {if(SetProperty(ref _expressionName,value)){_expressionSceneName=null;_expressionProposal=null;ExpressionReviewed=false;InvalidateFaceSelections();}} }
    public string ExpressionCorrespondences { get => _expressionCorrespondences; set {if(SetProperty(ref _expressionCorrespondences, value))InvalidateExpressionProposal();} }
    public string ExpressionLandmarks {get=>_expressionLandmarks;set {if(SetProperty(ref _expressionLandmarks,value))InvalidateExpressionProposal();}}
    public string ExpressionTargetRegion {get=>_expressionTargetRegion;set {if(SetProperty(ref _expressionTargetRegion,value))InvalidateExpressionProposal();}}
    public string ExpressionRegionName {get=>_expressionRegionName;set {if(SetProperty(ref _expressionRegionName,value))InvalidateExpressionProposal();}}
    public bool ExpressionCorrespondenceReviewed {get=>_expressionCorrespondenceReviewed;set=>SetProperty(ref _expressionCorrespondenceReviewed,value);}
    private void InvalidateExpressionProposal(){_expressionSceneName=null;_expressionProposal=null;ExpressionReviewed=false;ExpressionCorrespondenceReviewed=false;StopFacePicking();}
    public string ExpressionLockedVertices { get => _expressionLockedVertices; set {if(SetProperty(ref _expressionLockedVertices, value))InvalidateExpressionProposal();} }
    public bool ExpressionReviewed { get => _expressionReviewed; set => SetProperty(ref _expressionReviewed, value); }
    public MorphTransferConflict ExpressionConflict { get => _expressionConflict; set => SetProperty(ref _expressionConflict, value); }
    public IReadOnlyList<MorphTransferConflict> ExpressionConflictChoices { get; } = Enum.GetValues<MorphTransferConflict>();
    public AsyncRelayCommand LoadMorphReferenceCommand => _loadMorphReferenceCommand ??= new(LoadMorphReferenceAsync);
    public AsyncRelayCommand ImportManualShapeCommand => _importManualShapeCommand ??= new(ImportManualShapeAsync);
    private AsyncRelayCommand? _exportManualNeutralCommand;
    public AsyncRelayCommand ExportManualNeutralCommand => _exportManualNeutralCommand ??= new(ExportManualNeutralAsync);
    public AsyncRelayCommand AddCharacterAttachmentCommand => _addCharacterAttachmentCommand ??= new(AddCharacterAttachmentAsync);
    public RelayCommand AdoptCharacterReferenceCommand=>_adoptCharacterReferenceCommand??=new(()=>
    {
        if(_model is null || _morphReference is null){BuildStatus="Load the target model and a character reference first.";return;}
        try
        {
            var revised=CharacterReferenceAuthoring.AdoptReference(_model,_morphReference);
            ApplyCharacterGeometry(revised,"Reference systems attached. Review expressions, helpers, visibility, physics and damage mappings.");
        }
        catch(Exception e)when(e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException){BuildStatus="Character reference adoption rejected: "+e.Message;}
    });
    public RelayCommand ProposeExpressionCommand => _proposeExpressionCommand ??= new(ProposeExpression);
    public RelayCommand AcceptExpressionCommand => _acceptExpressionCommand ??= new(AcceptExpression);
    public RelayCommand PreviewReferenceExpressionCommand=>_previewReferenceExpressionCommand??=new(()=>
    {
        if(_morphReference is null) { BuildStatus = "Load a character reference first."; return; }
        ShowExpressionScene(_morphReference, ExpressionName, "Original expression");
    });
    public RelayCommand PreviewExpressionCommand => _previewExpressionCommand ??= new(() =>
    {
        if (_model?.Package.Document.MorphChannels.Any(c => c.Name == ExpressionName) == true)
            ShowExpressionScene(_model, ExpressionName, "Target expression");
    });

    private async Task LoadMorphReferenceAsync()
    {
        string? path = _fileDialogs.ShowOpenCustomModelPackageDialog(_packagePath);
        if (path is null) return;
        try
        {
            _morphReference = await Task.Run(() => FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path)));
            MorphReferenceNames.Clear(); MorphReferenceSurfaceIds.Clear();
            foreach (var c in _morphReference.Package.Document.MorphChannels) MorphReferenceNames.Add(c.Name);
            foreach (var s in _morphReference.Surfaces.Where(s => !s.MorphTargets.IsEmpty)) MorphReferenceSurfaceIds.Add(s.Id);
            SelectedReferenceSurface = MorphReferenceSurfaceIds.FirstOrDefault(); ExpressionName = MorphReferenceNames.FirstOrDefault() ?? string.Empty;
            BuildStatus = "Select the reference and target face surfaces, then choose an expression.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Morph reference load failed: " + e.Message; }
    }
    private void ProposeExpression()
    {
        if (_model is null || _morphReference is null || SelectedCharacterSurface is null || SelectedReferenceSurface is null)
        {
            BuildStatus = "Load the target and reference models, then select their face surfaces and an expression.";
            return;
        }
        try
        {
            if(!ExpressionCorrespondenceReviewed)throw new InvalidOperationException("Review the selected face region and landmark/triangle correspondence before proposing an expression.");
            var source = CharacterGeometryAuthoring.CreateMorphProfile(_morphReference, SelectedReferenceSurface);
            var target = CharacterGeometryAuthoring.CreateMorphProfile(_model, SelectedCharacterSurface);
            var original = _morphReference.Surfaces.Single(s => s.Id == SelectedReferenceSurface).MorphTargets.Single(m => m.Name == ExpressionName);
            var triangles = ExpressionCorrespondences.Split(['\r','\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 2) throw new ArgumentException("Enter source triangle index and target triangle index per line.");
                return new MorphTriangleCorrespondence(SelectedReferenceSurface, int.Parse(fields[0], CultureInfo.InvariantCulture), SelectedCharacterSurface, int.Parse(fields[1], CultureInfo.InvariantCulture));
            }).ToImmutableArray();
            var landmarks=ExpressionLandmarks.Split(['\r','\n'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(line=>
            {
                var fields=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
                if(fields.Length is <2 or >3)throw new ArgumentException("Enter source vertex, target vertex and optional weight per landmark.");
                return new MorphVertexCorrespondence(SelectedReferenceSurface,int.Parse(fields[0],CultureInfo.InvariantCulture),SelectedCharacterSurface,int.Parse(fields[1],CultureInfo.InvariantCulture),fields.Length==3?double.Parse(fields[2],CultureInfo.InvariantCulture):1);
            }).ToImmutableArray();
            var region=ExpressionTargetRegion.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(s=>int.Parse(s,CultureInfo.InvariantCulture)).ToImmutableHashSet();
            var locks = ExpressionLockedVertices.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToImmutableHashSet();
            _expressionProposal = MorphDeformationTransfer.Propose(source, target, ExpressionName,
                new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>(StringComparer.Ordinal) { [ExpressionName] = [original.PositionDeltas] },
                new() { Method = MorphTransferMethod.CorrespondenceDeformationGradient, TriangleCorrespondences = triangles, Correspondences=landmarks, TargetVertexMask=region, LockedTargetVertices = locks });
            ExpressionReviewed = false;
            PreviewExpressionProposal();
            BuildStatus = "Expression preview ready. Review the face region and seams before accepting.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException or FormatException or OverflowException) { BuildStatus = "Expression transfer rejected: " + e.Message; }
    }
    private void PreviewExpressionProposal()
    {
        if (_model is null || _expressionProposal is null || SelectedCharacterSurface is null) return;
        var candidate = CharacterGeometryAuthoring.SetExpression(_model, SelectedCharacterSurface, _expressionProposal.TargetExpressionName,
            _expressionProposal.SourceDescriptorHash, _expressionProposal.PositionDeltas[0], MorphTransferConflict.ReplaceExisting, true);
        ShowExpressionScene(candidate, _expressionProposal.TargetExpressionName, "Expression preview");
    }
    private void ShowExpressionScene(FbxModelAuthoringImportResult model, string? expression, string subject)
    {
        string? surfaceId = ReferenceEquals(model, _morphReference) ? SelectedReferenceSurface : SelectedCharacterSurface;
        var surface = model.Surfaces.SingleOrDefault(surface => surface.Id == surfaceId);
        if (surface is null) { BuildStatus = "Select a face to preview."; return; }
        StopFacePicking();
        Timeline.StopCommand.Execute(null);
        var preview = ReAnimated.App.Infrastructure.CustomModelPreviewAdapter.CreateSession(model with { Surfaces = [surface] });
        Viewport.SceneSource.SetExternalPreviewScene(null);
        Viewport.SceneSource.SetScene(preview.Meshes.Select(mesh => mesh with
        {
            IsSkinned = false,
            InverseBindMatrices = ReadOnlyMemory<System.Numerics.Matrix4x4>.Empty,
            SkinBoneIndices = ReadOnlyMemory<int>.Empty,
        }).ToArray(), null, [], generation: Interlocked.Increment(ref _previewGeneration));
        Viewport.SceneSource.SetMorphWeights(expression is null ? [] : [new(expression, (float)ExpressionPreviewWeight)]);
        Viewport.SceneSource.SetMeshVisibility(true);
        _cameraCoordinator.SetTargetPreviewCameraOverride(null);
        ExpressionPreviewSubject = subject;
        FrameModel();
        _expressionSceneName = expression;
        _expressionSceneGeneration = Viewport.SceneSource.CaptureFrame().Generation;
    }
    private void AcceptExpression()
    {
        if (_model is null || _morphReference is null || _expressionProposal is null || SelectedCharacterSurface is null || SelectedReferenceSurface is null)
        {
            BuildStatus = "Propose an expression before accepting it.";
            return;
        }
        try
        {
            if (!ExpressionReviewed) throw new InvalidOperationException("Mark this expression reviewed before accepting.");
            _expressionProposal.ValidateAgainst(CharacterGeometryAuthoring.CreateMorphProfile(_morphReference, SelectedReferenceSurface), CharacterGeometryAuthoring.CreateMorphProfile(_model, SelectedCharacterSurface));
            var revised = CharacterGeometryAuthoring.SetExpression(_model, SelectedCharacterSurface, _expressionProposal.TargetExpressionName,
                _expressionProposal.SourceDescriptorHash, _expressionProposal.PositionDeltas[0], ExpressionConflict, true);
            if (ReferenceEquals(revised, _model))
            {
                _expressionProposal = null; ExpressionReviewed = false;
                ShowExpressionScene(_model, ExpressionName, "Existing expression");
                BuildStatus = "Existing expression kept.";
                return;
            }
            revised=RecordExpressionOrigin(revised,MorphAuthoringMethod.AssistedTransfer);
            ApplyCharacterGeometry(revised, "Expression accepted.");
            _expressionProposal = null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Expression acceptance rejected: " + e.Message; }
    }
    private async Task ExportManualNeutralAsync()
    {
        if (_model is null || SelectedCharacterSurface is null) return;
        string? path = _fileDialogs.ShowSaveManualMorphNeutralDialog("neutral-face", _packagePath);
        if (path is null) return;
        string? temporary = null;
        try
        {
            var target = _model.Surfaces.Single(s => s.Id == SelectedCharacterSurface);
            byte[] bytes = ManualMorphObjCodec.EncodeNeutral(target);
            string destination = Path.GetFullPath(path);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            BuildStatus = "Face exported in meters; keep vertex order and triangles while sculpting, then import under the selected expression name.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        { BuildStatus = "Neutral face export rejected: " + e.Message; }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    private async Task ImportManualShapeAsync()
    {
        if (_model is null || SelectedCharacterSurface is null || _morphReference is null)
        {
            BuildStatus = "Load the target and reference models, then select their face surfaces and an expression before importing.";
            return;
        }
        var targetModel = _model;
        var referenceModel = _morphReference;
        string targetSurfaceId = SelectedCharacterSurface;
        string? referenceSurfaceId = SelectedReferenceSurface;
        string expressionName = ExpressionName;
        MorphTransferConflict conflict = ExpressionConflict;
        bool reviewed = ExpressionReviewed;
        string? path = _fileDialogs.ShowOpenManualMorphSculptDialog(_sourcePath);
        if (path is null) return;
        try
        {
            if (!reviewed) throw new InvalidOperationException("Review the sculpt and choose keep or replace before importing.");
            var deltas = await CharacterManualExpressionAuthoring.ReadDeltasAsync(targetModel, targetSurfaceId, path);
            if (!ReferenceEquals(_model, targetModel) || !ReferenceEquals(_morphReference, referenceModel) ||
                SelectedCharacterSurface != targetSurfaceId || SelectedReferenceSurface != referenceSurfaceId ||
                ExpressionName != expressionName || ExpressionConflict != conflict || ExpressionReviewed != reviewed)
                throw new InvalidOperationException("Target, original expression or review changed while reading the sculpt. Review and import it again.");
            if (referenceSurfaceId is null) throw new InvalidOperationException("Select a reference face.");
            var revised=CharacterManualExpressionAuthoring.Apply(targetModel,referenceModel,targetSurfaceId,
                referenceSurfaceId,expressionName,deltas,conflict,reviewed);
            if (ReferenceEquals(revised, _model))
            {
                ExpressionReviewed = false;
                ShowExpressionScene(_model, ExpressionName, "Existing expression");
                BuildStatus = "Existing expression kept.";
                return;
            }
            ApplyCharacterGeometry(revised,"Manual expression imported under its original channel name.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Manual sculpt import rejected: " + e.Message; }
    }
    private async Task AddCharacterAttachmentAsync()
    {
        if (_model is null) return;
        var target = _model;
        string? rigidBone = SelectedBone?.Name;
        string? path = _fileDialogs.ShowOpenCustomModelFbxDialog(_sourcePath);
        if (path is null) return;
        try
        {
            var attachment = await FbxModelAuthoringImporter.ImportFileAsync(path, new() { DecodeAnimationClips = false });
            if (!ReferenceEquals(target, _model) || !string.Equals(rigidBone, SelectedBone?.Name, StringComparison.Ordinal))
            {
                BuildStatus = "The character or bone selection changed. Select the attachment again.";
                return;
            }
            ApplyCharacterGeometry(CharacterGeometryAuthoring.AddAttachment(target, attachment, rigidBone), "Attachment added.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) { BuildStatus = "Attachment rejected: " + e.Message; }
    }
    private FbxModelAuthoringImportResult RecordExpressionOrigin(FbxModelAuthoringImportResult revised,MorphAuthoringMethod method)
    {
        if(_model is null || ReferenceEquals(revised,_model) || _morphReference is null || SelectedCharacterSurface is null || SelectedReferenceSurface is null) return revised;
        var source=CharacterGeometryAuthoring.CreateMorphProfile(_morphReference,SelectedReferenceSurface);
        var target=CharacterGeometryAuthoring.CreateMorphProfile(_model,SelectedCharacterSurface);
        var channel=_morphReference.Package.Document.MorphChannels.Single(c=>c.Name==ExpressionName);
        var targetChannel=revised.Package.Document.MorphChannels.Single(c=>c.Name==ExpressionName);
        var old=revised.Package.Document.MorphAuthoringRecords.Where(r=>r.Name!=ExpressionName || r.TargetSurfaceId!=SelectedCharacterSurface).ToImmutableArray();
        var receipt=new MorphAuthoringRecord {Name=channel.Name,DescriptorHash=channel.DescriptorHash,ReferenceSourceSha256=source.SourceSha256,
            OriginalSourceChannelIndex=_morphReference.Package.Document.CharacterResources?.MorphBindings.FirstOrDefault(b=>b.Name==channel.Name)?.SourceChannelIndex??channel.Index,
            TargetChannelSlot=targetChannel.Index,ReferenceSurfaceId=SelectedReferenceSurface,TargetSurfaceId=SelectedCharacterSurface,
            ReferenceTopologyFingerprint=source.TopologyFingerprint,TargetNeutralFingerprint=target.TopologyFingerprint,Method=method,ConflictChoice=ExpressionConflict,
            AuthoredExpressionSha256=MorphAuthoringEvidence.ExpressionFingerprint(revised.Surfaces.Single(s=>s.Id==SelectedCharacterSurface).MorphTargets.Single(m=>m.Name==ExpressionName)),
            ReviewedRegionName=method==MorphAuthoringMethod.AssistedTransfer?ExpressionRegionName:null,
            ReviewedTargetRegion=method==MorphAuthoringMethod.AssistedTransfer?ExpressionTargetRegion.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(s=>int.Parse(s,CultureInfo.InvariantCulture)).ToImmutableArray():[],
            ReviewedLandmarks=method==MorphAuthoringMethod.AssistedTransfer?_expressionProposal?.Correspondences??[]:[],
            ReviewedTriangles=method==MorphAuthoringMethod.AssistedTransfer?_expressionProposal?.TriangleCorrespondences??[]:[],LockedTargetVertices=(method==MorphAuthoringMethod.AssistedTransfer?ExpressionLockedVertices:string.Empty).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
                .Select(s=>int.Parse(s,CultureInfo.InvariantCulture)).ToImmutableArray(),Accepted=true};
        return ModelGeometryRevisionCodec.Capture(revised with {Package=revised.Package with {Document=revised.Package.Document with {MorphAuthoringRecords=old.Add(receipt)}}});
    }

    private void ApplyCharacterGeometry(FbxModelAuthoringImportResult revised, string status)
    {
        var before = CaptureAuthoringSnapshot();
        CommitModel(revised, _sourcePath, _packagePath, preserveAuthoringHistory: true);
        RecordAuthoringUndo(before); BuildStatus = status; _setStatus(status);
    }
}
