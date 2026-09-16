using ReAnimated.Core.Domain;
using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Renderer.D3D11;
namespace ReAnimated.App.ViewModels;
public sealed partial class ModelsWorkspaceViewModel
{
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
        OnBodyModelApplyRequested(sender,e);
        SelectedAnimation=Animations.FirstOrDefault(a=>a.Id==id);RefreshTimeline();RefreshPreview();
    }
    private ImmutableArray<MorphWeight> SampleAnimationMorphs(FbxModelAuthoringImportResult model,AnimationClip? clip,int frame)
    {
        if(clip is null||clip.ScalarTracks.IsEmpty)return [];
        Guid? id=DerivedReviewActive?Conformance.DerivedMotionPreview?.ClipId:SelectedAnimation?.Id;
        var selection=model.Package.Document.AnimationClips.FirstOrDefault(c=>c.Id==id);
        double scale=selection?.FacialSourceValueUnit=="percent"?.01:1;
        var samples=clip.SampleScalars(frame/clip.FrameRate.FramesPerSecond);
        return model.Package.Document.MorphChannels.Where(m=>samples.ContainsKey(m.Name))
            .Select(m=>new MorphWeight(m.Name,(float)(samples[m.Name]*scale))).ToImmutableArray();
    }
}
