using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void RefreshAttachmentGripEditor()
    {
        AttachmentRenderAsset? asset=null;
        if(AttachmentEditor.SelectedAttachment is { } selected)_attachmentRenderAssets.TryGetValue(selected.Binding.AssetId,out asset);
        AttachmentEditor.ReplaceGripAsset(asset);
    }

    private GizmoRenderData[] BuildAttachmentGripGizmos(EvaluationFrame frame,bool authored)
    {
        var selected=AttachmentEditor.SelectedAttachment;
        var evaluated=(authored?frame.AuthoredAttachments:frame.DisplayAttachments).FirstOrDefault(a=>a.BindingId==selected?.Id);
        if(evaluated?.GripCalibration is not { } grip || !_attachmentRenderAssets.TryGetValue(evaluated.AssetId,out var asset)||
            !AttachmentGripFrames.Matches(grip,asset,out _))return [];
        var shapes=new List<GizmoRenderData>();
        if(evaluated.PrimaryGripWorldFrame is { } primary)Axes(frame.ActorWorldTransform*primary,.06,null);
        if(evaluated.SecondaryPropWorldFrame is { } prop)Axes(frame.ActorWorldTransform*prop,.045,new Vector4(0,1,1,1));
        if(evaluated.SecondaryCharacterWorldFrame is { } hand)Axes(frame.ActorWorldTransform*hand,.055,new Vector4(1,0,1,1));
        if(evaluated.SecondaryPropWorldFrame is { } a&&evaluated.SecondaryCharacterWorldFrame is { } b)
            Line(frame.ActorWorldTransform.TransformPoint(a.Translation),frame.ActorWorldTransform.TransformPoint(b.Translation),new(1,1,0,1));
        return shapes.ToArray();
        void Axes(TransformMatrix matrix,double length,Vector4? color)
        {
            var origin=matrix.Translation;
            Line(origin,matrix.TransformPoint(new(length,0,0)),color??new(1,0,0,1));
            Line(origin,matrix.TransformPoint(new(0,length,0)),color??new(0,1,0,1));
            Line(origin,matrix.TransformPoint(new(0,0,length)),color??new(0,0,1,1));
        }
        void Line(Vector3D a,Vector3D b,Vector4 color)
        {
            var start=new Vector3((float)a.X,(float)a.Y,(float)a.Z);var end=new Vector3((float)b.X,(float)b.Y,(float)b.Z);
            if(!float.IsFinite(start.X)||!float.IsFinite(start.Y)||!float.IsFinite(start.Z)||!float.IsFinite(end.X)||!float.IsFinite(end.Y)||!float.IsFinite(end.Z))return;
            shapes.Add(new(GizmoKind.Line,start,end,color,2));
        }
    }

    private void PublishAttachmentGripStatus(EvaluationFrame frame,AttachmentSceneComposition scene)
    {
        Guid? id=AttachmentEditor.SelectedAttachment?.Id;
        var error=scene.Diagnostics.FirstOrDefault(d=>d.BindingId==id);
        var evaluated=frame.DisplayAttachments.FirstOrDefault(a=>a.BindingId==id);
        if(error is not null)
        {
            AttachmentEditor.GripContactStatus=$"Secondary contact unavailable: {error.Message}";
            return;
        }
        var ikReport=frame.AttachmentIkReports.FirstOrDefault(r=>r.BindingId==id&&r.DisplayEvaluation);
        if(ikReport is not null)
        {
            string state=!ikReport.Applied&&ikReport.Message.Contains("weight is zero",StringComparison.OrdinalIgnoreCase)
                ? "inactive"
                : ikReport.Applied
                    ? ikReport.Clamped?"applied with reach clamp":"applied"
                    : "rejected";
            string gapLabel=evaluated?.SecondaryPositionError is { } distance
                ? $" Contact gap: {distance*1000:0.###} mm."
                : string.Empty;
            AttachmentEditor.GripContactStatus=$"Secondary IK {state}: {ikReport.Message}{gapLabel}";
            return;
        }
        AttachmentEditor.GripContactStatus=evaluated?.SecondaryPositionError is { } gap?
            $"Secondary contact separation: {gap*1000:0.###} mm in evaluated model space. Cyan: prop contact; magenta: character frame; yellow: gap. This does not drive hand IK.":
            "RGB axes show the calibrated primary frame. Optional secondary IK status appears here; parent/local scale and offsets are retained. Native equipment behavior remains unverified.";
    }
}
