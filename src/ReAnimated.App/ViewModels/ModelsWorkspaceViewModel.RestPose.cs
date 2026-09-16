using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _restPreviewActive;
    private bool _restOverlayVisible;
    private bool RestReviewActive=>IsConformTabSelected && Conformance.RestPosePreviewEnabled && Conformance.RestPosePreview is not null;
    private void OnRestPosePreviewChanged(object? sender,EventArgs e)
    {
        if(_disposed||_suppressPreviewRefresh)return;
        if(RestReviewActive)Timeline.IsPlaying=false;
        _previewSession=null;RefreshPreview();
    }
    private void PublishRestPoseOverlay()
    {
        if(!RestReviewActive||Conformance.RestPosePreview is not { } preview)
        {if(_restOverlayVisible)Viewport.SceneSource.SetGizmos([]);_restOverlayVisible=false;return;}
        var lines=new List<GizmoRenderData>();
        foreach(var (axis,color) in new[]{(Vector3D.UnitX,new Vector4(1,.2f,.2f,1)),(Vector3D.UnitY,new Vector4(.2f,1,.2f,1)),(Vector3D.UnitZ,new Vector4(.3f,.6f,1,1))})
        {
            Line(preview.BeforeFrame.Translation,preview.BeforeFrame.TransformPoint(axis*.06),new(.5f,.5f,.5f,1));
            Line(preview.AfterFrame.Translation,preview.AfterFrame.TransformPoint(axis*.06),color);
        }
        Line(preview.BeforeFrame.Translation,preview.AfterFrame.Translation,new(1,.8f,.2f,1));
        Viewport.SceneSource.SetGizmos(lines);_restOverlayVisible=true;
        return;
        void Line(Vector3D a,Vector3D b,Vector4 color)
        {
            var start=new Vector3((float)a.X,(float)a.Y,(float)a.Z);var end=new Vector3((float)b.X,(float)b.Y,(float)b.Z);
            if(float.IsFinite(start.X)&&float.IsFinite(start.Y)&&float.IsFinite(start.Z)&&float.IsFinite(end.X)&&float.IsFinite(end.Y)&&float.IsFinite(end.Z))
                lines.Add(new(GizmoKind.Line,start,end,color,2));
        }
    }
}
