using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
namespace ReAnimated.App.ViewModels;
public sealed partial class ModelsWorkspaceViewModel
{
    private bool DoctorReviewActive=>IsConformTabSelected&&Conformance.DoctorPreviewEnabled&&Conformance.DoctorPreview is {CanApply:true};
    private void OnDoctorPreviewChanged(object? sender,EventArgs e)
    {if(_disposed||_suppressPreviewRefresh)return;Timeline.IsPlaying=false;_previewSession=null;Viewport.SceneSource.SetGizmos([]);RefreshPreview();}
    private void PublishDoctorOverlay()
    {
        if(!DoctorReviewActive)return;
        var shapes=Conformance.DoctorPreview!.Rows.Where(r=>r.Status==RigDoctorRowStatus.Proposed&&r.Fit is not null)
            .SelectMany(r=>ContactOverlayBuilder.Build(new ContactPreview(r.Fit!,r.Fit!.GlobalFrame,r.BoundsCenter!.Value,r.BoundsHalfExtents!.Value))).ToArray();
        Viewport.SceneSource.SetGizmos(shapes);
        Viewport.SetPresentation("Rig Doctor repair preview","Proposed contacts only. Original skinning and source clips retained; native acceptance unverified.");
    }
}
