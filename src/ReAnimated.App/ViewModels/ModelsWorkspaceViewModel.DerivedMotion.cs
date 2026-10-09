using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Renderer.D3D11;
namespace ReAnimated.App.ViewModels;
public sealed partial class ModelsWorkspaceViewModel
{
    private AsyncRelayCommand? _prepareSelectedAnimationCommand;
    private Guid? _preparedAnimationSourceId;
    private Guid? _preparedAnimationOwnerId;
    public IAsyncRelayCommand PrepareSelectedAnimationCommand => _prepareSelectedAnimationCommand ??=
        new AsyncRelayCommand(PrepareSelectedAnimationAsync, () => !IsBusy && !Conformance.IsBusy &&
            _model is not null && SelectedAnimation is { DecodedClip: not null } selected &&
            selected.Contract.DerivedMotion is null && selected.Contract.AuthoredAnimation is null);

    private async Task PrepareSelectedAnimationAsync()
    {
        if (_model is null || SelectedAnimation is not { DecodedClip: not null } selected) return;
        Guid sourceId = selected.Id;
        _preparedAnimationSourceId = null;
        _preparedAnimationOwnerId = null;
        SyncDocument();
        var model = _model!;
        long revision = PersistenceRevision;
        await Conformance.SetModelAsync(model);
        if (!ReferenceEquals(model, _model) || revision != PersistenceRevision || SelectedAnimation?.Id != sourceId) return;
        if (!Conformance.HasStudioSession && Conformance.StartRepairStudioCommand.CanExecute(null))
            Conformance.StartRepairStudioCommand.Execute(null);
        Conformance.StudioStage = RigStudioStage.Animate;
        Conformance.DerivedSource = Conformance.DerivedSources.FirstOrDefault(choice => choice.Id == sourceId);
        _preparedAnimationSourceId = sourceId;
        _preparedAnimationOwnerId = _model!.Package.Document.ModelId;
        model = _model;
        revision = PersistenceRevision;
        await Conformance.DeriveMotionCommand.ExecuteAsync(null);
        if (!ReferenceEquals(model, _model) || revision != PersistenceRevision || SelectedAnimation?.Id != sourceId)
        {
            _preparedAnimationSourceId = null;
            _preparedAnimationOwnerId = null;
            return;
        }
        if (Conformance.DerivedMotionPreview?.CanApply != true)
        {
            _preparedAnimationSourceId = null;
            _preparedAnimationOwnerId = null;
        }
        BuildStatus = Conformance.DerivedMotionPreview?.CanApply == true
            ? "Review the prepared animation, then save the clip."
            : Conformance.DerivedMotionStatus;
        _setStatus(BuildStatus);
    }
    private bool DerivedReviewActive=>IsConformTabSelected&&Conformance.DerivedPreviewEnabled&&Conformance.DerivedMotionPreview is {CanApply:true};
    private AnimationClip? DisplayedAnimationClip=>DerivedReviewActive?Conformance.DerivedMotionPreview!.PreviewModel!.AnimationClips[Conformance.DerivedMotionPreview.ClipId]:SelectedAnimation?.DecodedClip;
    private void OnDerivedMotionPreviewChanged(object? sender,EventArgs e)
    {
        if(_disposed||_suppressPreviewRefresh)return;
        Timeline.IsPlaying=false;_previewSession=null;RefreshTimeline();RefreshPreview();
    }
    private void OnDerivedMotionApplyRequested(object? sender,BodyModelEventArgs e)
    {
        Guid? id=Conformance.DerivedMotionPreview?.ClipId;
        if (id is { } preparedId && _preparedAnimationSourceId is { } sourceId &&
            e.Source.Package.Document.ModelId == _preparedAnimationOwnerId &&
            e.Result.Package.Document.AnimationClips.Any(clip => clip.Id == preparedId && clip.DerivedMotion?.SourceClipId == sourceId))
        {
            var result = e.Result with { Package = e.Result.Package with { Document = e.Result.Package.Document with
            {
                AnimationClips = e.Result.Package.Document.AnimationClips.Select(clip =>
                    clip.Id == preparedId ? clip with { Included = true } :
                    clip.Id == sourceId ? clip with { Included = false } : clip).ToImmutableArray(),
            } } };
            e = new(e.Source, result, "Saved the prepared animation.");
            _preparedAnimationSourceId = null;
            _preparedAnimationOwnerId = null;
        }
        OnBodyModelApplyRequested(sender,e);
        SelectedAnimation=Animations.FirstOrDefault(a=>a.Id==id);RefreshTimeline();RefreshPreview();
    }
    private ImmutableArray<MorphWeight> SampleAnimationMorphs(FbxModelAuthoringImportResult model,AnimationClip? clip,int frame)
    {
        if(clip is null||clip.ScalarTracks.IsEmpty)return MergeGuidedMorphOverrides(model, []);
        Guid? id=DerivedReviewActive?Conformance.DerivedMotionPreview?.ClipId:SelectedAnimation?.Id;
        var selection=model.Package.Document.AnimationClips.FirstOrDefault(c=>c.Id==id);
        double scale=selection?.FacialSourceValueUnit=="percent"?.01:1;
        var samples=clip.SampleScalars(frame/clip.FrameRate.FramesPerSecond);
        return MergeGuidedMorphOverrides(model, model.Package.Document.MorphChannels.Where(m=>samples.ContainsKey(m.Name))
            .Select(m=>new MorphWeight(m.Name,(float)(samples[m.Name]*scale))).ToImmutableArray());
    }
}
