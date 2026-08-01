using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

/// <summary>
/// Applies immutable bone edit layers to a pose using the same semantics for
/// export, evaluated preview, and the editor's bind-pose fallback preview.
/// </summary>
public static class BoneEditLayerEvaluator
{
    public static SkeletonPose ApplyLayers(
        SkeletonPose pose,
        double frame,
        ImmutableArray<BoneEditLayer> layers,
        BoneEditLayerScope scope)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!double.IsFinite(frame))
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                "The edit-layer sample frame must be finite.");
        }

        if (layers.IsDefault)
        {
            throw new ArgumentException(
                "The edit-layer collection must be initialized.",
                nameof(layers));
        }

        ImmutableArray<TransformTRS> locals = pose.LocalTransforms;
        foreach (BoneEditLayer layer in layers.Where(
                     layer => layer.Enabled && layer.Scope == scope))
        {
            if (layer.Weight <= 0.0)
            {
                continue;
            }

            foreach (BoneEditTrack track in layer.Tracks)
            {
                if ((uint)track.BoneIndex >= (uint)locals.Length)
                {
                    throw new InvalidOperationException(
                        $"Edit layer '{layer.Name}' refers to missing bone index {track.BoneIndex}.");
                }

                TransformTRS underlying = locals[track.BoneIndex];
                TransformTRS edit = track.Sample(frame);
                double trackWeight = layer.Weight *
                    (layer.BoneMask.TryGetValue(
                        track.BoneIndex,
                        out double maskWeight)
                        ? maskWeight
                        : 1.0);
                if (trackWeight <= 0.0)
                {
                    continue;
                }

                TransformTRS result = layer.BlendMode switch
                {
                    BoneEditBlendMode.Override =>
                        TransformTRS.Interpolate(
                            underlying,
                            edit,
                            trackWeight),
                    BoneEditBlendMode.Additive =>
                        ApplyAdditive(
                            underlying,
                            edit,
                            trackWeight),
                    _ => throw new InvalidOperationException(
                        $"Unsupported edit blend mode '{layer.BlendMode}'."),
                };
                locals = locals.SetItem(track.BoneIndex, result);
            }
        }

        return new SkeletonPose(pose.Rig, locals);
    }

    private static TransformTRS ApplyAdditive(
        TransformTRS underlying,
        TransformTRS delta,
        double weight)
    {
        Vector3D translation =
            underlying.Translation + (delta.Translation * weight);
        QuaternionD weightedDelta = QuaternionD.Slerp(
            QuaternionD.Identity,
            delta.Rotation,
            weight);
        QuaternionD rotation =
            (underlying.Rotation * weightedDelta).Normalized();
        Vector3D scaleMultiplier = Vector3D.Lerp(
            Vector3D.One,
            delta.Scale,
            weight);
        Vector3D scale = Vector3D.ComponentMultiply(
            underlying.Scale,
            scaleMultiplier);
        return new(translation, rotation, scale);
    }
}
