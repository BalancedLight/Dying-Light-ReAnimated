using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Renderer.D3D11;

public static class AttachmentGripFrames
{
    public static ImmutableArray<AttachmentGripFrame> Read(AttachmentRenderAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if(asset.BindSkeleton is not { } skeleton)return [];
        var frames=ImmutableArray.CreateBuilder<AttachmentGripFrame>();
        for(int index=0;index<skeleton.Bones.Count;index++)
        {
            var bone=skeleton.Bones[index];
            try { frames.Add(AttachmentGripFrame.FromMatrix(index,bone.Name,ToCore(bone.WorldTransform))); }
            catch(ArgumentException) { /* Invalid frames are not offered for calibration. */ }
        }
        return frames.ToImmutable();
    }

    public static bool Matches(AttachmentGripCalibration grip,AttachmentRenderAsset asset,out string reason)
    {
        ArgumentNullException.ThrowIfNull(grip);ArgumentNullException.ThrowIfNull(asset);
        if(grip.PropAssetId!=asset.ProjectAssetId || !string.Equals(grip.PropContentSha256,asset.ContentSha256,StringComparison.OrdinalIgnoreCase))
        {reason="The calibrated prop asset/content identity does not match the decoded owner.";return false;}
        foreach(var frame in grip.Secondary is { } secondary?new[]{grip.PrimaryPropFrame,secondary.PropFrame}:new[]{grip.PrimaryPropFrame})
        {
            if(asset.BindSkeleton is not { } skeleton || (uint)frame.NodeIndex>=(uint)skeleton.Bones.Count ||
                !string.Equals(skeleton.Bones[frame.NodeIndex].Name,frame.NodeName,StringComparison.OrdinalIgnoreCase) ||
                !ToCore(skeleton.Bones[frame.NodeIndex].WorldTransform).NearlyEquals(frame.Matrix,1e-6))
            {reason=$"Prop frame '{frame.NodeName}' is missing, reordered or changed. Recalibrate against the current owner asset.";return false;}
        }
        reason=string.Empty;return true;
    }

    private static TransformMatrix ToCore(Matrix4x4 m)=>new(m.M11,m.M21,m.M31,m.M41,m.M12,m.M22,m.M32,m.M42,
        m.M13,m.M23,m.M33,m.M43,m.M14,m.M24,m.M34,m.M44);
}
