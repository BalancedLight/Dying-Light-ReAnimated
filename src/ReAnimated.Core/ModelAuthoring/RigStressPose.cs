using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>A transient rotation in the selected model's local joint frame. It is not a saved rest edit or animation track.</summary>
public readonly record struct RigStressJointRotation(int BoneIndex, Vector3D Degrees);

public static class RigStressPose
{
    public static SkeletonPose Evaluate(CustomModelDocument document, RigDefinition rig,
        IReadOnlyList<RigStressJointRotation> rotations, double amount = 1)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate();
        ArgumentNullException.ThrowIfNull(rig); ArgumentNullException.ThrowIfNull(rotations);
        if (!double.IsFinite(amount) || amount is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(amount));
        var bones = document.CreateEffectiveBones();
        if (bones.Length != rig.BoneCount || bones.Where((bone, index) => bone.Name != rig.Bones[index].Name || bone.ParentIndex != rig.Bones[index].ParentIndex).Any())
            throw new ArgumentException("Stress poses require the model's own hierarchy.", nameof(rig));
        if (rotations.Count > 64) throw new ArgumentException("Stress review supports at most 64 explicitly selected joints.", nameof(rotations));
        var ids = new HashSet<int>();
        var projections = bones.Select(static b => b.LocalBindTransform.Normalized()).ToArray();
        var exact = bones.Select(static b => b.ExactLocalBindMatrix).ToArray();
        foreach (var rotation in rotations)
        {
            if ((uint)rotation.BoneIndex >= (uint)bones.Length || !ids.Add(rotation.BoneIndex) || !rotation.Degrees.IsFinite ||
                Math.Abs(rotation.Degrees.X) > 360 || Math.Abs(rotation.Degrees.Y) > 360 || Math.Abs(rotation.Degrees.Z) > 360)
                throw new ArgumentException("Stress joint edits need unique current indices and finite angles within +/-360 degrees.", nameof(rotations));
            if (amount == 0 || rotation.Degrees == Vector3D.Zero) continue;
            var angles = rotation.Degrees * (amount * Math.PI / 180);
            var delta = QuaternionD.FromAxisAngle(Vector3D.UnitZ, angles.Z) * QuaternionD.FromAxisAngle(Vector3D.UnitY, angles.Y) * QuaternionD.FromAxisAngle(Vector3D.UnitX, angles.X);
            int index = rotation.BoneIndex;
            var original = projections[index];
            var changed = original with { Rotation = (original.Rotation * delta).Normalized() };
            // Rotate in the declared local frame while retaining the exact affine residual (scale/shear/reflection).
            exact[index] = changed.ToMatrix() * original.ToMatrix().InvertedAffine() * exact[index];
            projections[index] = changed;
        }
        return new(rig, projections, exact);
    }

    public static double CycleAmount(double elapsedSeconds, double durationSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || !double.IsFinite(durationSeconds) || durationSeconds is < .5 or > 30)
            throw new ArgumentException("Stress cycles require a finite elapsed time and a duration from 0.5 to 30 seconds.");
        double phase = elapsedSeconds % durationSeconds / durationSeconds;
        return .5 - .5 * Math.Cos(Math.Tau * phase);
    }
}
