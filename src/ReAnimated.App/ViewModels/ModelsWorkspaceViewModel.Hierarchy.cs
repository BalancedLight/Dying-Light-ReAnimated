using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _hierarchyPreviewActive;
    private bool _hierarchyOverlayVisible;
    private bool HierarchyReviewActive=>IsConformTabSelected&&Conformance.HierarchyPreviewEnabled&&Conformance.HierarchyPreview is not null;
    private void OnHierarchyPreviewChanged(object? sender,EventArgs e)
    {
        if(_disposed||_suppressPreviewRefresh)return;
        if(HierarchyReviewActive)Timeline.IsPlaying=false;
        _previewSession=null;RefreshPreview();
    }
    private void OnHierarchyApplyRequested(object? sender,BodyModelEventArgs e)
    {
        string? name=Conformance.HierarchyNode?.Name;
        OnBodyModelApplyRequested(sender,e);
        PopulateHierarchyRows(name);RefreshPreview();
    }
    private void PublishHierarchyOverlay()
    {
        if(!HierarchyReviewActive||Conformance.HierarchyNode is not { } node||Conformance.HierarchyParent is not { } parent)
        {if(_hierarchyOverlayVisible)Viewport.SceneSource.SetGizmos([]);_hierarchyOverlayVisible=false;return;}
        var lines=new List<GizmoRenderData>();
        if(Conformance.HierarchyNodes.FirstOrDefault(n=>n.EntityId==node.ParentEntityId) is { } previous)
            Line(previous.GlobalFrame.Translation,node.GlobalFrame.Translation,new(.5f,.5f,.5f,1));
        if(parent.EntityId is not null)Line(parent.GlobalFrame.Translation,node.GlobalFrame.Translation,new(.1f,1,1,1));
        foreach(var axis in new[]{Vector3D.UnitX,Vector3D.UnitY,Vector3D.UnitZ})Line(node.GlobalFrame.Translation-axis*.015,node.GlobalFrame.Translation+axis*.015,new(1,.8f,.1f,1));
        Viewport.SceneSource.SetGizmos(lines);_hierarchyOverlayVisible=true;
        return;
        void Line(Vector3D a,Vector3D b,Vector4 color)
        {
            var start=new Vector3((float)a.X,(float)a.Y,(float)a.Z);var end=new Vector3((float)b.X,(float)b.Y,(float)b.Z);
            if(float.IsFinite(start.X)&&float.IsFinite(start.Y)&&float.IsFinite(start.Z)&&float.IsFinite(end.X)&&float.IsFinite(end.Y)&&float.IsFinite(end.Z))
                lines.Add(new(GizmoKind.Line,start,end,color,3));
        }
    }
}
