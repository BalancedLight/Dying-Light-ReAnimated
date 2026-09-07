using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

/// <summary>Transfers only explicit secondary corrections between camera and external-orbit presentations.</summary>
public static class SecondaryMotionRenderAdapter
{
    /// <summary>Converts a source-bone-local correction to the emitted bind axes without changing its world-space effect.</summary>
    public static TransformMatrix RebaseBoneLocalDelta(TransformMatrix sourceDelta,
        TransformMatrix sourceBindGlobal, TransformMatrix presentationBindGlobal)
    {
        TransformMatrix basis = sourceBindGlobal.InvertedAffine() * presentationBindGlobal;
        return basis.InvertedAffine() * sourceDelta * basis;
    }

    public static SkeletonRenderData ApplyBoneLocalDeltas(SkeletonRenderData skeleton,
        IReadOnlyDictionary<string, TransformMatrix> localDeltas)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(localDeltas);
        if (localDeltas.Count == 0) return skeleton;
        BoneRenderData[] bones = skeleton.Bones.ToArray();
        for (int i = 0; i < bones.Length; i++)
            if (localDeltas.TryGetValue(bones[i].Name, out TransformMatrix delta))
            {
                if (!delta.IsFinite) throw new ArgumentException("Secondary deltas must be finite.", nameof(localDeltas));
                bones[i] = bones[i] with { WorldTransform = CorePreviewAdapter.ToSystemMatrix(CorePreviewAdapter.ToCoreMatrix(bones[i].WorldTransform) * delta) };
            }
        for (int i = 0; i < bones.Length; i++)
            if (localDeltas.ContainsKey(bones[i].Name))
            {
                Matrix4x4 parent = bones[i].ParentIndex >= 0 ? bones[bones[i].ParentIndex].WorldTransform : Matrix4x4.Identity;
                if (!Matrix4x4.Invert(parent, out Matrix4x4 inverse)) throw new InvalidOperationException("Secondary display parent is singular.");
                bones[i] = bones[i] with { LocalTransform = bones[i].WorldTransform * inverse };
            }
        return skeleton with { Bones = bones };
    }
}
