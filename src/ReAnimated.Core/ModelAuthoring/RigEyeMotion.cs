using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Transient local gaze review. Exact affine composition preserves the reviewed eye pivot under scaled or sheared parents.</summary>
public static class RigEyeMotion
{
    public static SkeletonPose Evaluate(CustomModelDocument document, RigDefinition rig, RigEyeSide side, double yawDegrees, double pitchDegrees)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentNullException.ThrowIfNull(rig);
        if (!double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees) || Math.Abs(yawDegrees) > 90 || Math.Abs(pitchDegrees) > 90)
            throw new ArgumentOutOfRangeException(nameof(yawDegrees), "Eye review angles must be finite and within +/-90 degrees.");
        var bones = document.CreateEffectiveBones();
        if (rig.BoneCount != bones.Length || rig.Bones.Where((bone, i) => bone.Name != bones[i].Name || bone.ParentIndex != bones[i].ParentIndex).Any())
            throw new ArgumentException("Eye review requires the current effective hierarchy.", nameof(rig));
        int index = GeneratedEyeRig.GetBoneIndex(document, side);
        var exact = bones.Select(b => b.ExactLocalBindMatrix).ToArray();
        var projected = bones.Select(b => b.LocalBindTransform).ToArray();
        var rotation = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, yawDegrees * Math.PI / 180)) *
            TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitX, pitchDegrees * Math.PI / 180));
        exact[index] *= rotation;
        projected[index] = Dl1AuthoredRigContract.ProjectAffineToTrs(exact[index]);
        return new(rig, projected, exact);
    }
}
